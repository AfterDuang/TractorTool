using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var version = DuplicatiEngine.ReadVersions(history).Single();
        Check(version.SourceRoot == oldRoot && version.Entries.Any(file => file.Path == "内容.bin" && file.Hash == Convert.ToHexString(digest)) &&
            version.Entries.Any(item => item.Path == "空" && item.IsDirectory), "读原生文件表、移动前根、空目录与 SHA256");

        using (var stream = File.Create(revision))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var fileset = new StreamWriter(zip.CreateEntry("fileset").Open());
            fileset.Write("{}");
        }
        ExpectInvalid(() => DuplicatiEngine.ReadVersions(history), "拒绝缺少清单字段的历史文件");
        WriteVolume(revision, true, "SHA1", folder, entry);
        ExpectInvalid(() => DuplicatiEngine.ReadVersions(history), "拒绝未知哈希算法");
        WriteVolume(revision, true, "SHA256", folder, new { type = "Folder", path = Path.Combine(fixture.FullName, "越界") });
        ExpectInvalid(() => DuplicatiEngine.ReadVersions(history), "拒绝越界目录");
        WriteVolume(revision, true, "SHA256", folder, new { type = "Folder", path = HistoryPaths.HistoryDirectory(oldRoot) });
        ExpectInvalid(() => DuplicatiEngine.ReadVersions(history), "拒绝恢复库子树");
        WriteVolume(revision, false, "SHA256");

        var executable = Path.Combine(fixture.FullName, "unused-cli.exe");
        var badStage = Path.Combine(history, "temp", "bad");
        var correctStage = Path.Combine(history, "temp", "correct");
        foreach (var (stage, payload) in new[] { (badStage, new byte[] { 3, 2, 1 }), (correctStage, new byte[] { 1, 2, 3 }) })
        {
            var engine = new DuplicatiEngine(new FakeRunner(request =>
            {
                Check(request.Arguments[0] == "restore" && request.Arguments.Contains("--version=1") &&
                    request.Arguments.Contains("--no-local-db=true") && request.Arguments.Contains($"--restore-path={stage}"),
                    "恢复序号包含未完成版本，始终提取到暂存目录");
                Directory.CreateDirectory(Path.Combine(stage, "空"));
                File.WriteAllBytes(Path.Combine(stage, "内容.bin"), payload);
                return Success;
            }), executable);
            if (stage == correctStage)
                await engine.RestoreAsync(version, history, stage, CancellationToken.None);
            else
                await ExpectAsync<InvalidDataException>(() => engine.RestoreAsync(version, history, stage, CancellationToken.None),
                    "退出码 0 但字节不符必须拒绝");
        }

        File.WriteAllBytes(Path.Combine(history, "index.sqlite"), []);
        var backup = new DuplicatiEngine(new FakeRunner(request =>
        {
            Check(request.Arguments[0] == "backup" && request.Arguments[1].Contains("%2B%2540%20%23", StringComparison.Ordinal) &&
                request.Arguments.Contains("--no-encryption=true") && request.Arguments.Contains("--upload-unchanged-backups=true") &&
                request.Arguments.Contains($"--ignore-filenames={Path.PathSeparator}") &&
                request.Arguments.Contains("--suppress-warnings=NoFilesInBackup") && request.Arguments.Contains("--use-move-for-put=true"),
                "备份本地路径编码、无加密与安全重试参数");
            var exclude = request.Arguments.Single(value => value.StartsWith("--exclude=[", StringComparison.Ordinal));
            Check(Regex.IsMatch(history + Path.DirectorySeparatorChar, Environment.ExpandEnvironmentVariables(exclude[11..^1])),
                "历史目录排除规则按字面路径匹配");
            return new ProcessResult(2, "", "", false, false);
        }), executable);
        await ExpectAsync<IOException>(() => backup.BackupAsync(managed, history, CancellationToken.None), "备份警告不能报成功");

        var emptyBackup = new DuplicatiEngine(new FakeRunner(_ =>
        {
            var rootFolder = new { type = "Folder", path = managed + Path.DirectorySeparatorChar };
            WriteVolume(Path.Combine(repository, "duplicati-20261007T140000Z.dlist.zip"), true, "SHA256", rootFolder);
            return Success;
        }), executable);
        await emptyBackup.BackupAsync(managed, history, CancellationToken.None);
        Check(DuplicatiEngine.ReadVersions(history)[0].Entries.Count == 0, "只有源目录的完整版本可登记");
    }

    private static readonly ProcessResult Success = new(0, "", "", false, false);

    private static void WriteVolume(string path, bool full, string fileHash, params object[] entries)
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

    private static async Task ExpectAsync<TException>(Func<Task> action, string requirement) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(requirement);
    }

    private static void Check(bool value, string requirement)
    {
        if (!value) throw new InvalidOperationException(requirement);
    }

    private sealed class FakeRunner(Func<ProcessRequest, ProcessResult> run) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(run(request));
    }
}
