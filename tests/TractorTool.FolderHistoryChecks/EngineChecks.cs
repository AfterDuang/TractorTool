using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

internal static class EngineChecks
{
    internal static async Task RunAsync(DirectoryInfo fixture)
    {
        var managed = Directory.CreateDirectory(Path.Combine(fixture.FullName, "引擎 [11] +%40 # %WINDIR%")).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(managed)).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        var oldRoot = Path.Combine(fixture.FullName, "移动前");
        var sourceFile = Path.Combine(oldRoot, "内容.bin");
        var digest = SHA256.HashData(new byte[] { 1, 2, 3 });
        var entry = new { type = "File", path = sourceFile, size = 3, hash = Convert.ToBase64String(digest) };
        var folder = new { type = "Folder", path = oldRoot + Path.DirectorySeparatorChar };
        var empty = new { type = "Folder", path = Path.Combine(oldRoot, "空") + Path.DirectorySeparatorChar };
        var good = Path.Combine(repository, "duplicati-20261007T120000Z.dlist.zip");
        var revision = Path.Combine(repository, "duplicati-20261007T130000Z.dlist.zip");

        WriteVolume(good, true, "SHA256", folder, empty, entry);
        WriteVolume(revision, false, "SHA256");
        var version = DuplicatiEngine.ReadVersion(history, Path.GetFileName(good))!;
        Check(version.SourceRoot == oldRoot && version.Entries.Any(file => file.Path == "内容.bin" && file.Hash == Convert.ToHexString(digest)) &&
            version.Entries.Any(item => item.Path == "空" && item.IsDirectory), "读原生文件表、移动前根、空目录与 SHA256");

        using (var stream = File.Create(revision))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var fileset = new StreamWriter(zip.CreateEntry("fileset").Open());
            fileset.Write("{}");
        }
        ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, Path.GetFileName(revision)), "拒绝缺少清单字段的历史文件");
        WriteVolume(revision, true, "SHA1", folder, entry);
        ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, Path.GetFileName(revision)), "拒绝未知哈希算法");
        WriteVolume(revision, true, "SHA256", folder, new { type = "Folder", path = Path.Combine(fixture.FullName, "越界") });
        ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, Path.GetFileName(revision)), "拒绝越界目录");
        WriteVolume(revision, true, "SHA256", folder, new { type = "Folder", path = HistoryPaths.HistoryDirectory(oldRoot) });
        ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, Path.GetFileName(revision)), "拒绝恢复库子树");
        WriteVolume(revision, true, "SHA256", folder, new { type = "Symlink", path = sourceFile });
        ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, Path.GetFileName(revision)), "拒绝不支持的链接清单");

        var executable = Path.Combine(fixture.FullName, "unused-cli.exe");
        var log = new FakeLog();
        var untouched = new FakeRunner(_ => Success);
        var damaged = new DuplicatiEngine(untouched, executable, log);
        await ExpectAsync<InvalidDataException>(() => damaged.BackupAsync(managed, history, CancellationToken.None),
            "含未登记链接清单的库必须停止写入");
        Check(untouched.Requests.Count == 0, "异常库预检失败仍调用了引擎");
        File.WriteAllText(revision, "damaged uncommitted volume");
        Check(DuplicatiEngine.ReadVersion(history, version.Id)!.Entries.SequenceEqual(version.Entries), "未登记坏卷阻塞了指定旧版本读取");
        var badStage = Path.Combine(history, "temp", "bad");
        var correctStage = Path.Combine(history, "temp", "correct");
        foreach (var (stage, payload) in new[] { (badStage, new byte[] { 3, 2, 1 }), (correctStage, new byte[] { 1, 2, 3 }) })
        {
            var engine = new DuplicatiEngine(new FakeRunner(request =>
            {
                Check(request.Arguments[0] == "restore" && request.Arguments.Contains("--version=1") &&
                    request.Arguments.Contains("--no-local-db=true") && request.Arguments.Contains($"--restore-path={stage}") &&
                    request.Arguments.Contains("--suppress-warnings=NoFilesInBackup,SQLiteFilePermissionError"),
                    "恢复序号包含未登记坏卷，始终提取到暂存目录");
                Directory.CreateDirectory(Path.Combine(stage, "空"));
                File.WriteAllBytes(Path.Combine(stage, "内容.bin"), payload);
                return Success;
            }), executable, log);
            if (stage == correctStage)
                await engine.RestoreAsync(version, history, stage, CancellationToken.None);
            else
                await ExpectAsync<InvalidDataException>(() => engine.RestoreAsync(version, history, stage, CancellationToken.None),
                    "退出码 0 但字节不符必须拒绝");
        }

        WriteVolume(revision, false, "SHA256");
        File.WriteAllBytes(Path.Combine(history, "index.sqlite"), []);
        var backupRunner = new FakeRunner(_ => new ProcessResult(2, "", "", false, false));
        var backup = new DuplicatiEngine(backupRunner, executable, log);
        await ExpectAsync<IOException>(() => backup.BackupAsync(managed, history, CancellationToken.None), "备份警告不能报成功");
        var request = backupRunner.Requests.Single();
        Check(request.Arguments[0] == "backup" && request.Arguments[1].Contains("%2B%2540%20%23", StringComparison.Ordinal) &&
            request.Arguments.Contains("--no-encryption=true") && request.Arguments.Contains("--upload-unchanged-backups=true") &&
            request.Arguments.Contains($"--ignore-filenames={Path.PathSeparator}") && request.Arguments.Contains("--use-move-for-put=true"),
            "备份本地路径编码、无加密与安全重试参数");
        var exclude = request.Arguments.Single(value => value.StartsWith("--exclude=[", StringComparison.Ordinal));
        Check(Regex.IsMatch(history + Path.DirectorySeparatorChar, Environment.ExpandEnvironmentVariables(exclude[11..^1])),
            "历史目录排除规则按字面路径匹配");

        var emptyBackup = new DuplicatiEngine(new FakeRunner(_ =>
        {
            var rootFolder = new { type = "Folder", path = managed + Path.DirectorySeparatorChar };
            WriteVolume(Path.Combine(repository, "duplicati-20261007T140000Z.dlist.zip"), true, "SHA256", rootFolder);
            return Success;
        }), executable, log);
        var emptyVersion = await emptyBackup.BackupAsync(managed, history, CancellationToken.None);
        Check(emptyVersion.Entries.Count == 0, "只有源目录的完整版本可登记");
        await CheckDiagnosticsAsync(fixture.FullName, executable);
    }

    private static readonly ProcessResult Success = new(0, "", "", false, false);
    private const string PermissionTag = "Information-Duplicati.Library.SQLiteHelper.SQLiteLoader-SQLiteFilePermissionError";
    private const string PermissionLog = "2026-11-16 10:00:00 +08 - [" + PermissionTag + "]: permission warning detail\n" +
        "System.IO.IOException: E:\\项目 - [旧稿]\\样本\nexception detail without indentation\n";
    private const string OtherLog = "2026-11-16 10:00:00 +08 - [Warning-Duplicati.Library.Main.Controller-UnknownWarning]: other warning detail\n";
    private const string ErrorLog = "2026-11-16 10:00:00 +08 - [Error-Duplicati.Library.Main.Controller-UnknownError]: error detail\n";

    private static async Task CheckDiagnosticsAsync(string fixture, string executable)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "diagnostics")).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        var folder = new { type = "Folder", path = root + Path.DirectorySeparatorChar };
        WriteVolume(Path.Combine(repository, "duplicati-20261007T120000Z.dlist.zip"), true, "SHA256", folder);
        var log = new FakeLog();
        var runner = new FakeRunner(request =>
        {
            if (request.Arguments[0] == "backup")
                WriteVolume(Path.Combine(repository, "duplicati-20261007T130000Z.dlist.zip"), true, "SHA256", folder);
            return Success;
        }, string.Concat(Enumerable.Repeat(PermissionLog, 4)));
        var engine = new DuplicatiEngine(runner, executable, log);
        var version = await engine.BackupAsync(root, history, CancellationToken.None);
        await engine.RestoreAsync(version, history, Path.Combine(history, "temp", "restore"), CancellationToken.None);
        Check(runner.Requests.Select(item => item.Arguments[0]).SequenceEqual(new[] { "repair", "backup", "restore" }), "覆盖修复、保存和恢复参数");
        foreach (var request in runner.Requests)
        {
            var suppressed = "--suppress-warnings=NoFilesInBackup,SQLiteFilePermissionError" +
                (request.Arguments[0] == "restore" ? ",NoFilesOrFoldersRestored" : "");
            Check(request.Arguments.Count(value => value.StartsWith("--suppress-warnings=", StringComparison.Ordinal)) == 1 &&
                request.Arguments.Contains(suppressed) &&
                request.Arguments.Contains("--log-file-log-level=Warning") &&
                request.Arguments.Contains("--log-file-log-filter=+" + PermissionTag) && request.OutputLimit == 1024 * 1024,
                "三类命令只按精确 ID 抑制已知警告并保留原进程捕获上限");
            Check(!File.Exists(LogPath(request)), "成功转存诊断后未清理本次日志");
        }
        Check(runner.Requests.Select(LogPath).Distinct().Count() == 3 &&
            log.Entries.Count == 3 && log.Entries.All(item => item.Level == AppLogLevel.Warning &&
                item.Error!.ToString().Contains(PermissionLog, StringComparison.Ordinal)), "重复权限日志每个命令只产生一条宿主 Warning 并保留详情");

        var foldersVolume = Path.Combine(repository, "duplicati-20261007T110000Z.dlist.zip");
        WriteVolume(foldersVolume, true, "SHA256", folder, new { type = "Folder", path = Path.Combine(root, "empty") });
        var folderVersion = DuplicatiEngine.ReadVersion(history, Path.GetFileName(foldersVolume))!;
        var folderStage = Path.Combine(history, "temp", "folders");
        var foldersRunner = new FakeRunner(_ =>
        {
            Directory.CreateDirectory(Path.Combine(folderStage, "empty"));
            return Success;
        }, PermissionLog.Replace("10:00:00", "10.00.00", StringComparison.Ordinal));
        await new DuplicatiEngine(foldersRunner, executable, new FakeLog()).RestoreAsync(folderVersion, history, folderStage, CancellationToken.None);
        Check(foldersRunner.Requests.Single().Arguments.Contains("--suppress-warnings=NoFilesInBackup,SQLiteFilePermissionError,NoFilesRestored"),
            "只有含子目录且无普通文件的恢复才兼容 NoFilesRestored，日志头不依赖区域时间分隔符");

        File.WriteAllBytes(Path.Combine(history, "index.sqlite"), []);
        var noVersionLog = new FakeLog();
        var noVersion = new DuplicatiEngine(new FakeRunner(_ => Success, PermissionLog), executable, noVersionLog);
        await ExpectAsync<InvalidDataException>(() => noVersion.BackupAsync(root, history, CancellationToken.None),
            "只有权限警告和退出码 0 也必须有完整新增版本");
        Check(noVersionLog.Entries.Single().Level == AppLogLevel.Warning, "校验失败前的权限 Warning 丢失");

        var normalLog = new FakeLog();
        var normal = new DuplicatiEngine(new FakeRunner(_ =>
        {
            WriteVolume(Path.Combine(repository, "duplicati-20261007T140000Z.dlist.zip"), true, "SHA256", folder);
            return Success with { StandardOutput = PermissionLog };
        }), executable, normalLog);
        await normal.BackupAsync(root, history, CancellationToken.None);
        Check(normalLog.Entries.Count == 0, "不能从无 ID 的控制台输出推断权限警告或重复记录正常输出");

        (string Name, ProcessResult Result, string? Log)[] failures =
        [
            ("unknown", new(2, "stdout detail", "", false, false), OtherLog),
            ("mixed", new(2, "stdout detail", "", false, false), PermissionLog + OtherLog),
            ("unknown exit 0", new(0, "stdout detail", "", false, false), OtherLog),
            ("mixed exit 0", new(0, "stdout detail", "", false, false), PermissionLog + OtherLog),
            ("error exit 0", new(0, "stdout detail", "", false, false), ErrorLog),
            ("unknown format", new(0, "stdout detail", "", false, false), "unframed diagnostic content\n"),
            ("malformed header", new(0, "stdout detail", "", false, false), PermissionLog + "2026-11-16 10:00:00 +08 - [Warning-broken\n"),
            ("stderr", new(0, "stdout detail", "stderr detail", false, false), PermissionLog),
            ("stdout truncation", new(0, "stdout detail", "", true, false), PermissionLog),
            ("stderr truncation", new(0, "stdout detail", "stderr detail", false, true), PermissionLog),
            ("missing log", new(0, "stdout detail", "", false, false), null),
            ("oversized log", new(0, "stdout detail", "", false, false), PermissionLog + new string('x', 128 * 1024)),
            ("unreadable log", new(0, "stdout detail", "", false, false), ""),
            ("bounded detail", new(2, "stdout detail" + new string('x', 128 * 1024), "stderr detail", false, false), OtherLog)
        ];
        foreach (var failure in failures)
        {
            var failedLog = new FakeLog();
            var failedRunner = new FakeRunner(request =>
            {
                if (failure.Name == "unreadable log")
                {
                    File.Delete(LogPath(request));
                    Directory.CreateDirectory(LogPath(request));
                }
                return failure.Result;
            }, failure.Log);
            var failed = new DuplicatiEngine(failedRunner, executable, failedLog);
            var error = await ExpectAsync<IOException>(() => failed.BackupAsync(root, history, CancellationToken.None), failure.Name);
            var logPath = LogPath(failedRunner.Requests.Single());
            Check(error.GetBaseException().Message.Contains(logPath, StringComparison.Ordinal) &&
                !error.GetBaseException().Message.Contains("stdout detail", StringComparison.Ordinal) &&
                error.ToString().Contains("Command: backup", StringComparison.Ordinal) &&
                error.ToString().Contains("ExitCode: " + failure.Result.ExitCode, StringComparison.Ordinal) &&
                error.ToString().Contains("stdout detail", StringComparison.Ordinal) && error.ToString().Length < 64 * 1024,
                failure.Name + "：短摘要与有界完整诊断未分开");
            if (failure.Result.StandardError.Length != 0)
                Check(error.ToString().Contains("stderr detail", StringComparison.Ordinal), "丢失 stderr 诊断");
            if (failure.Name != "missing log")
                Check(File.Exists(logPath) || Directory.Exists(logPath), failure.Name + "：失败日志未保留");
            if (failure.Log?.Contains(OtherLog, StringComparison.Ordinal) == true)
                Check(error.ToString().Contains(OtherLog, StringComparison.Ordinal) && !error.Message.Contains("诊断日志不完整", StringComparison.Ordinal),
                    "未知警告详情被吞掉，或完整日志被错误报告为读取不完整");
            if (failure.Name == "oversized log")
                Check(error.ToString().Contains("超过读取限制", StringComparison.Ordinal), "超限日志未明确报告诊断不完整");
            Check(failedLog.Entries.Count(item => item.Level == AppLogLevel.Warning) ==
                (failure.Log?.Contains(PermissionLog, StringComparison.Ordinal) == true ? 1 : 0), "只有精确权限日志才产生一次宿主 Warning");
        }

        File.Delete(Path.Combine(history, "index.sqlite"));
        foreach (var command in new[] { "repair", "restore" })
        foreach (var diagnostic in new[] { OtherLog, PermissionLog + OtherLog, ErrorLog })
        {
            var failedRunner = new FakeRunner(_ => Success, diagnostic);
            var failed = new DuplicatiEngine(failedRunner, executable, new FakeLog());
            var error = await ExpectAsync<IOException>(() => command == "repair"
                ? failed.BackupAsync(root, history, CancellationToken.None)
                : failed.RestoreAsync(version, history, Path.Combine(history, "temp", "failed-" + Guid.NewGuid().ToString("N")), CancellationToken.None),
                command + "：退出码 0 仍须拒绝警告或错误");
            Check(failedRunner.Requests.Single().Arguments[0] == command && File.Exists(LogPath(failedRunner.Requests.Single())) &&
                error.ToString().Contains(diagnostic, StringComparison.Ordinal), "异常修复不应继续保存，异常恢复不应通过或丢失日志");
        }
        File.WriteAllBytes(Path.Combine(history, "index.sqlite"), []);
        var throwingRunner = new FakeRunner(_ => throw new IOException("runner failure detail"));
        var throwing = new DuplicatiEngine(throwingRunner, executable, new FakeLog());
        var runnerError = await ExpectAsync<IOException>(() => throwing.BackupAsync(root, history, CancellationToken.None), "进程异常必须保留诊断");
        Check(!runnerError.GetBaseException().Message.Contains("runner failure detail", StringComparison.Ordinal) &&
            runnerError.ToString().Contains("runner failure detail", StringComparison.Ordinal) &&
            runnerError.ToString().Contains("ExitCode: unavailable", StringComparison.Ordinal), "进程异常没有区分短摘要和未知退出码");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledLog = new FakeLog();
        var canceledRunner = new FakeRunner(_ => throw new OperationCanceledException(canceled.Token));
        var canceling = new DuplicatiEngine(canceledRunner, executable, canceledLog);
        await ExpectAsync<OperationCanceledException>(() => canceling.BackupAsync(root, history, canceled.Token), "正常取消保持取消语义");
        Check(canceledLog.Entries.Single().Level == AppLogLevel.Info && File.Exists(LogPath(canceledRunner.Requests.Single())),
            "正常取消应保留诊断且不记 Error");
    }

    internal static void WriteVolume(string path, bool full, string fileHash, params object[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Write("manifest", new { Version = 2, FileHash = fileHash });
        Write("fileset", new { IsFullBackup = full });
        Write("filelist.json", entries);
        void Write(string name, object value)
        {
            using var entry = zip.CreateEntry(name).Open();
            JsonSerializer.Serialize(entry, value);
        }
    }

    private static void ExpectInvalid(Action action, string requirement)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(requirement);
    }

    private static async Task<TException> ExpectAsync<TException>(Func<Task> action, string requirement) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { return error; }
        throw new InvalidOperationException(requirement);
    }

    private static void Check(bool value, string requirement)
    {
        if (!value) throw new InvalidOperationException(requirement);
    }

    internal static void WriteOperationLog(ProcessRequest request, string text = "") => File.WriteAllText(LogPath(request), text);
    private static string LogPath(ProcessRequest request) => request.Arguments.Single(value => value.StartsWith("--log-file=", StringComparison.Ordinal))[11..];

    private sealed class FakeRunner(Func<ProcessRequest, ProcessResult> run, string? operationLog = "") : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (operationLog is not null) WriteOperationLog(request, operationLog);
            return Task.FromResult(run(request));
        }
    }

    private sealed class FakeLog : IAppLog
    {
        public List<(AppLogLevel Level, Exception? Error)> Entries { get; } = [];
        public void Write(string source, string message, Exception? error = null) => Write(AppLogLevel.Info, source, message, error);
        public void Write(AppLogLevel level, string source, string message, Exception? error = null) => Entries.Add((level, error));
    }
}
