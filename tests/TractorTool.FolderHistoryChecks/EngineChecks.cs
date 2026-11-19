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
        var nestedRoot = Path.Combine(oldRoot, "child");
        WriteVolume(revision, true, "SHA256", folder, new { type = "Folder", path = nestedRoot },
            new { type = "Folder", path = HistoryPaths.HistoryDirectory(nestedRoot) });
        ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, Path.GetFileName(revision)), "拒绝旧或恶意清单中的任意层级历史库");
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
        await CheckSelectionAsync(fixture.FullName, executable);
        await CheckLongExclusionsAsync(fixture.FullName, executable);
        await CheckPublicationSourcesAsync(fixture.FullName, executable);
        await CheckCleanupAsync(fixture.FullName, executable);
    }

    private static readonly ProcessResult Success = new(0, "", "", false, false);
    private const string PermissionTag = "Information-Duplicati.Library.SQLiteHelper.SQLiteLoader-SQLiteFilePermissionError";
    private const string PermissionLog = "2026-11-16 10:00:00 +08 - [" + PermissionTag + "]: permission warning detail\n" +
        "System.IO.IOException: E:\\项目 - [旧稿]\\样本\nexception detail without indentation\n";
    private const string OtherLog = "2026-11-16 10:00:00 +08 - [Warning-Duplicati.Library.Main.Controller-UnknownWarning]: other warning detail\n";
    private const string ErrorLog = "2026-11-16 10:00:00 +08 - [Error-Duplicati.Library.Main.Controller-UnknownError]: error detail\n";

    private static async Task CheckSelectionAsync(string fixture, string executable)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "selection %WINDIR% [1]")).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        var source = Path.Combine(fixture, "original root");
        var folder = Path.Combine(source, "folder[1]");
        var path = Path.Combine(folder, "%TEMP%.txt");
        var bytes = new byte[] { 1, 3, 5 };
        var hash = Convert.ToBase64String(SHA256.HashData(bytes));
        var id = "duplicati-20261007T120000Z.dlist.zip";
        WriteVolume(Path.Combine(repository, id), true, "SHA256",
            new { type = "Folder", path = source + Path.DirectorySeparatorChar },
            new { type = "Folder", path = folder + Path.DirectorySeparatorChar },
            new { type = "File", path, size = bytes.Length, hash },
            new { type = "File", path = Path.Combine(source, "second.txt"), size = bytes.Length, hash },
            new { type = "File", path = Path.Combine(source, "huge.bin"), size = 1_000_000_000_000L, hash });
        var selected = "folder[1]/%TEMP%.txt";
        var version = DuplicatiEngine.ReadVersion(history, id)!;
        var staging = Path.Combine(history, "temp", "selection");
        var runner = new FakeRunner(request =>
        {
            var include = request.Arguments.Single(argument => argument.StartsWith("--include=[", StringComparison.Ordinal));
            var expression = Environment.ExpandEnvironmentVariables(include[11..^1]);
            Check(Regex.IsMatch(source + Path.DirectorySeparatorChar, expression) &&
                Regex.IsMatch(folder + Path.DirectorySeparatorChar, expression) && Regex.IsMatch(path, expression) &&
                !Regex.IsMatch(Path.Combine(source, "huge.bin"), expression) && request.Arguments.Contains("--exclude=*"),
                "单项提取必须只包含原根、祖先和字面文件，不能提取其他大型内容");
            Directory.CreateDirectory(Path.Combine(staging, "folder[1]"));
            File.WriteAllBytes(Path.Combine(staging, "folder[1]", "%TEMP%.txt"), bytes);
            return Success;
        });
        await new DuplicatiEngine(runner, executable, new FakeLog()).RestoreAsync(version, history, staging, CancellationToken.None, selected);
        Check(DuplicatiEngine.SelectEntries(version, selected).Count == 2, "所选提取清单包含无关条目");
        var unionStaging = Path.Combine(history, "temp", "union");
        string? parameterFile = null;
        var union = new FakeRunner(request =>
        {
            var argument = request.Arguments.Single(value => value.StartsWith("--parameters-file=", StringComparison.Ordinal));
            parameterFile = Path.Combine(request.WorkingDirectory!, argument["--parameters-file=".Length..]);
            var filters = File.ReadAllLines(parameterFile);
            var expression = Environment.ExpandEnvironmentVariables(filters[0][11..^1]);
            Check(filters.Length == 2 && filters[1] == "--exclude=*" && !request.Arguments.Contains("--exclude=*") &&
                Regex.IsMatch(folder + Path.DirectorySeparatorChar, expression) && Regex.IsMatch(path, expression) &&
                Regex.IsMatch(Path.Combine(source, "second.txt"), expression) && !Regex.IsMatch(Path.Combine(source, "huge.bin"), expression),
                "多选参数文件必须包含精确筛选和排除，不能扩大到整库");
            Directory.CreateDirectory(Path.Combine(unionStaging, "folder[1]"));
            File.WriteAllBytes(Path.Combine(unionStaging, "folder[1]", "%TEMP%.txt"), bytes);
            File.WriteAllBytes(Path.Combine(unionStaging, "second.txt"), bytes);
            return Success;
        });
        await new DuplicatiEngine(union, executable, new FakeLog()).RestoreSelectionAsync(version, history, unionStaging,
            CancellationToken.None, ["folder[1]", selected, "second.txt"]);
        Check(union.Requests.Count == 1 && parameterFile is not null && !File.Exists(parameterFile),
            "多选应在一次提取中合并父子范围并清理参数文件");
        File.WriteAllBytes(Path.Combine(history, "index.sqlite"), []);
        var backupRunner = new FakeRunner(_ => new ProcessResult(2, "", "", false, false));
        var backup = new DuplicatiEngine(backupRunner, executable, new FakeLog());
        await ExpectAsync<IOException>(() => backup.BackupAsync(root, history, CancellationToken.None, ["cache", "literal %TEMP%"]),
            "替身退出失败应拒绝");
        var filters = backupRunner.Requests.Single().Arguments.Where(argument => argument.StartsWith("--exclude=[", StringComparison.Ordinal)).ToArray();
        Check(filters.Length == 3, "备份缺少强制历史排除或用户规则");
        Check(Regex.IsMatch(Path.Combine(root, "child", HistoryPaths.DirectoryName, "repository", "file"),
            Environment.ExpandEnvironmentVariables(filters[0][11..^1])), "引擎强制规则必须阻止嵌套历史内容进入备份");
        foreach (var name in new[] { ".tractortool-history", "cache", "literal %TEMP%" })
        {
            var full = Path.Combine(root, name);
            Check(filters.Any(filter => Regex.IsMatch(full, Environment.ExpandEnvironmentVariables(filter[11..^1])) &&
                Regex.IsMatch(full + Path.DirectorySeparatorChar + "child.txt", Environment.ExpandEnvironmentVariables(filter[11..^1])) &&
                !Regex.IsMatch(full + "sibling", Environment.ExpandEnvironmentVariables(filter[11..^1]))),
                "排除规则必须与扫描器的精确路径及子树一致");
        }
    }

    private static async Task CheckLongExclusionsAsync(string fixture, string executable)
    {
        var exclusions = Enumerable.Range(0, 512).Select(index => $"排除 {index:D4} [%TEMP%]").ToArray();
        Check(string.Join('\n', exclusions).Length < 16000, "长排除规则样本应仍可从设置界面输入");
        foreach (var outcome in new[] { "success", "failure", "cancel" })
        {
            var root = Directory.CreateDirectory(Path.Combine(fixture, "long-exclusions %WINDIR% [1]", outcome)).FullName;
            var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
            var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
            var temporary = Path.Combine(history, "temp");
            using var cancellation = new CancellationTokenSource();
            string? parameterFile = null;
            var log = new FakeLog();
            var runner = new FakeRunner(request =>
            {
                var option = request.Arguments.Single(argument => argument.StartsWith("--parameters-file=", StringComparison.Ordinal));
                var name = option["--parameters-file=".Length..];
                Check(request.WorkingDirectory == temporary && Path.GetFileName(name) == name && !Path.IsPathRooted(name) &&
                    request.Arguments[0] == "backup" && request.Arguments[2] == root &&
                    request.Arguments.Contains("--snapshot-policy=off") && request.Arguments.Contains("--no-auto-compact=true") &&
                    !request.Arguments.Any(argument => argument.StartsWith("--exclude=", StringComparison.Ordinal)),
                    "长排除备份必须保留源和选项，并用临时目录及参数文件基名传入规则");
                parameterFile = Path.Combine(request.WorkingDirectory!, name);
                var filters = File.ReadAllLines(parameterFile);
                Check(filters.Length == exclusions.Length + 1 && filters.Sum(filter => (long)filter.Length) > 32767 &&
                    request.Arguments.Sum(argument => (long)argument.Length) < 32767,
                    "参数文件必须完整保留超过 Windows 命令行长度的全部规则");
                Check(Regex.IsMatch(history + Path.DirectorySeparatorChar, Environment.ExpandEnvironmentVariables(filters[0][11..^1])),
                    "参数文件遗漏了强制历史目录排除");
                for (var index = 0; index < exclusions.Length; index++)
                {
                    var path = Path.Combine(root, exclusions[index]);
                    var expression = Environment.ExpandEnvironmentVariables(filters[index + 1][11..^1]);
                    Check(Regex.IsMatch(path, expression) && Regex.IsMatch(path + Path.DirectorySeparatorChar + "child", expression) &&
                        !Regex.IsMatch(path + "sibling", expression), "长规则丢失、顺序改变或字面转义被改变");
                }
                if (outcome == "cancel")
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                if (outcome == "failure") return new ProcessResult(2, "", "", false, false);
                WriteVolume(Path.Combine(repository, "duplicati-20261007T120000Z.dlist.zip"), true, "SHA256",
                    new { type = "Folder", path = root + Path.DirectorySeparatorChar });
                return Success;
            });
            var engine = new DuplicatiEngine(runner, executable, log);
            if (outcome == "cancel")
                await ExpectAsync<OperationCanceledException>(() => engine.BackupAsync(root, history, cancellation.Token, exclusions), "长规则备份取消语义被改变");
            else if (outcome == "failure")
                await ExpectAsync<IOException>(() => engine.BackupAsync(root, history, cancellation.Token, exclusions), "长规则备份失败被当作成功");
            else await engine.BackupAsync(root, history, cancellation.Token, exclusions);
            Check(parameterFile is not null && !File.Exists(parameterFile) && !Directory.EnumerateFiles(temporary, "selection-*.txt").Any(),
                "备份成功、失败或取消后必须关闭租约并清理本次参数文件");
            Check(File.Exists(LogPath(runner.Requests.Single())) == (outcome != "success") &&
                (outcome != "cancel" || log.Entries.Single().Level == AppLogLevel.Info), "参数文件复用改变了诊断保留或取消日志级别");
        }
    }

    private static async Task CheckPublicationSourcesAsync(string fixture, string executable)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "publish-source-boundary")).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        var source = Path.Combine(history, "temp", "publish-source-" + Guid.NewGuid().ToString("N"));
        var bytes = new byte[] { 1, 4, 9 };
        var hash = Convert.ToBase64String(SHA256.HashData(bytes));
        const string normalId = "duplicati-20261007T120000Z.dlist.zip";
        const string publishedId = "duplicati-20261007T130000Z.dlist.zip";
        const string candidateId = "duplicati-20261007T140000Z.dlist.zip";
        var candidate = Path.Combine(repository, candidateId);
        WriteVolume(Path.Combine(repository, normalId), true, "SHA256", new { type = "Folder", path = root + Path.DirectorySeparatorChar });
        var sourceFolder = new { type = "Folder", path = source + Path.DirectorySeparatorChar };
        var sourceFile = new { type = "File", path = Path.Combine(source, "content.bin"), size = bytes.Length, hash };
        WriteVolume(candidate, true, "SHA256", sourceFolder, sourceFile);
        var parsed = DuplicatiEngine.ReadVersion(history, candidateId)!;
        Check(parsed.SourceRoot == source && parsed.Entries.Single().Path == "content.bin", "精确的库内发布源根应能按相对路径读取");
        foreach (var invalidRoot in new[]
        {
            history, Path.Combine(history, "temp"), source + "-extra", Path.Combine(source, "deeper"),
            Path.Combine(history, "other", Path.GetFileName(source)),
            Path.Combine(history, "temp", "publish-source-not-a-guid"),
            Path.Combine(source, HistoryPaths.DirectoryName, "temp", Path.GetFileName(source)),
            source + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "escape"
        })
        {
            foreach (var full in new[] { true, false })
            {
                WriteVolume(candidate, full, "SHA256", new { type = "Folder", path = invalidRoot + Path.DirectorySeparatorChar });
                if (full) ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, candidateId), "拒绝发布源的伪前缀、错层、嵌套库或穿越");
                else ExpectInvalid(() => DuplicatiEngine.PreflightDelete(history, [normalId]), "strict partial 必须同样拒绝非法发布源根");
            }
        }
        foreach (var full in new[] { true, false })
        {
            WriteVolume(candidate, full, "SHA256", sourceFolder,
                new { type = "Folder", path = Path.Combine(source, "nested") + Path.DirectorySeparatorChar },
                new { type = "Folder", path = Path.Combine(source, "nested", HistoryPaths.DirectoryName) + Path.DirectorySeparatorChar });
            if (full) ExpectInvalid(() => DuplicatiEngine.ReadVersion(history, candidateId), "合法发布源中的相对条目仍不能包含历史库保留名");
            else ExpectInvalid(() => DuplicatiEngine.PreflightDelete(history, [normalId]), "strict partial 中相对保留名仍被拒绝");
        }
        File.Delete(candidate);
        var untouched = new FakeRunner(_ => Success);
        var engine = new DuplicatiEngine(untouched, executable, new FakeLog());
        foreach (var invalidSource in new[] { Path.Combine(root, "elsewhere"), source + "-extra", Path.Combine(source, "child") })
            await ExpectAsync<InvalidDataException>(() => engine.BackupStagedAsync(invalidSource, history, CancellationToken.None),
                "发布备份必须拒绝不精确的当前库暂存源");
        await ExpectAsync<InvalidDataException>(() => engine.BackupStagedAsync(source, HistoryPaths.HistoryDirectory(Path.Combine(fixture, "other-owner")), CancellationToken.None),
            "不能把一个库中的发布材料备份到另一库");
        Check(untouched.Requests.Count == 0, "非法发布暂存源触发了外部工具");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "content.bin"), bytes);
        File.WriteAllBytes(Path.Combine(history, "index.sqlite"), []);
        var backup = new FakeRunner(request =>
        {
            Check(request.Arguments[0] == "backup" && request.Arguments[2] == source, "发布备份未使用当前库内的精确暂存源");
            WriteVolume(Path.Combine(repository, publishedId), true, "SHA256", sourceFolder, sourceFile);
            return Success;
        });
        var published = await new DuplicatiEngine(backup, executable, new FakeLog()).BackupStagedAsync(source, history, CancellationToken.None);
        WriteVolume(candidate, false, "SHA256", sourceFolder, sourceFile);
        DuplicatiEngine.PreflightDelete(history, [normalId]);
        File.Delete(candidate);
        var moved = root + "-moved";
        Directory.Move(root, moved);
        var movedHistory = HistoryPaths.HistoryDirectory(moved);
        var reopened = DuplicatiEngine.ReadVersion(movedHistory, publishedId)!;
        Check(reopened.SourceRoot == published.SourceRoot && reopened.Entries.SequenceEqual(published.Entries) && !Directory.Exists(root),
            "移动后的发布版本应仅解析原源根字符串，不能读取或重建旧位置");
        var staging = Path.Combine(movedHistory, "temp", "after-move");
        var restore = new FakeRunner(request =>
        {
            var expression = request.Arguments.Single(argument => argument.StartsWith("--include=[", StringComparison.Ordinal))[11..^1];
            Check(request.Arguments[0] == "restore" && Regex.IsMatch(Path.Combine(source, "content.bin"), expression),
                "发布版本移动后应按原生 SourceRoot 筛选并映射到新暂存区");
            File.WriteAllBytes(Path.Combine(staging, "content.bin"), bytes);
            return Success;
        });
        await new DuplicatiEngine(restore, executable, new FakeLog()).RestoreSelectionAsync(reopened, movedHistory, staging, CancellationToken.None, ["content.bin"]);
        Check(File.ReadAllBytes(Path.Combine(staging, "content.bin")).SequenceEqual(bytes) && !Directory.Exists(root),
            "发布版本恢复未正确映射字节或触及旧根");
    }

    private static async Task CheckCleanupAsync(string fixture, string executable)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "cleanup")).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(history, "repository")).FullName;
        var ids = new[] { "duplicati-20261007T120000Z.dlist.zip", "duplicati-20261007T130000Z.dlist.zip", "duplicati-20261007T140000Z.dlist.zip" };
        foreach (var id in ids) WriteVolume(Path.Combine(repository, id), true, "SHA256", new { type = "Folder", path = root + Path.DirectorySeparatorChar });
        var partial = Path.Combine(repository, "duplicati-20261007T133000Z.dlist.zip");
        WriteVolume(partial, false, "SHA256");
        DuplicatiEngine.PreflightDelete(history, [ids[1]]);
        ExpectInvalid(() => DuplicatiEngine.PreflightDelete(history, [Path.GetFileName(partial)]), "partial不能成为用户删除候选");
        ExpectInvalid(() => DuplicatiEngine.PreflightDelete(history, ids, allowEmpty: true), "未确认partial存在时不能清空整个历史库");
        Check(!File.Exists(Path.Combine(history, "cleanup.json")), "只读预检失败不能建立删除意图");
        var deletionStarted = false;
        var runner = new FakeRunner(request =>
        {
            var database = request.Arguments.Single(argument => argument.StartsWith("--dbpath=", StringComparison.Ordinal))[9..];
            if (request.Arguments[0] == "repair")
            {
                Check(!deletionStarted, "只读准备不能标记为已开始删除");
                File.WriteAllText(database, "native index fixture");
            }
            else if (request.Arguments[0] == "delete")
            {
                Check(request.Arguments.Contains("--version=2") && request.Arguments.Contains("--keep-versions=0") &&
                    request.Arguments.Contains("--keep-time=") && request.Arguments.Contains("--retention-policy=") &&
                    request.Arguments.Contains("--allow-full-removal=false") && File.Exists(Path.Combine(history, "cleanup.json")) && deletionStarted,
                    "删除编号必须包含partial，且先持久化原生索引计划再标记删除开始");
                File.Delete(Path.Combine(repository, ids[1]));
            }
            return Success;
        });
        await new DuplicatiEngine(runner, executable, new FakeLog()).DeleteVersionsAsync(history, [ids[1]], CancellationToken.None,
            deletionStarting: () => deletionStarted = true);
        Check(runner.Requests.Select(request => request.Arguments[0]).SequenceEqual(new[] { "repair", "delete" }) &&
            File.Exists(Path.Combine(repository, ids[0])) && File.Exists(Path.Combine(repository, ids[2])) && File.Exists(partial) &&
            File.Exists(Path.Combine(history, "index.sqlite")) && !File.Exists(Path.Combine(history, "cleanup.json")),
            "清理不应删除未选版本或遗失已重建索引");
        var invalidPartial = Path.Combine(repository, "duplicati-20261007T134000Z.dlist.zip");
        WriteVolume(invalidPartial, false, "SHA1");
        ExpectInvalid(() => DuplicatiEngine.PreflightDelete(history, [ids[0]]), "partial仍必须完整校验算法与清单格式");
        Check(!File.Exists(Path.Combine(history, "cleanup.json")), "损坏partial预检不应建立删除记录");
        File.Delete(invalidPartial);
        var beforeDeleteFailure = new FakeRunner(_ => new ProcessResult(2, "", "", false, false));
        deletionStarted = false;
        await ExpectAsync<IOException>(() => new DuplicatiEngine(beforeDeleteFailure, executable, new FakeLog())
            .DeleteVersionsAsync(history, [ids[0]], CancellationToken.None, deletionStarting: () => deletionStarted = true),
            "repair失败须在删除前返回");
        Check(!deletionStarted && !File.Exists(Path.Combine(history, "cleanup.json")) && File.Exists(Path.Combine(repository, ids[0])),
            "native删除未开始的失败必须可安全撤销服务pending");
        var unchanged = new FakeRunner(request =>
        {
            if (request.Arguments[0] == "repair")
                File.WriteAllText(request.Arguments.Single(argument => argument.StartsWith("--dbpath=", StringComparison.Ordinal))[9..], "fixture");
            return Success;
        });
        await ExpectAsync<InvalidDataException>(() => new DuplicatiEngine(unchanged, executable, new FakeLog())
            .DeleteVersionsAsync(history, [ids[0]], CancellationToken.None), "退出0但未删除选定卷不能成功");
        Check(File.Exists(Path.Combine(history, "cleanup.json")) && File.Exists(Path.Combine(repository, ids[0])), "清理结果异常必须留计划和原卷");
        var firstIndex = unchanged.Requests[0].Arguments.Single(argument => argument.StartsWith("--dbpath=", StringComparison.Ordinal))[9..];
        var retry = new FakeRunner(request =>
        {
            if (request.Arguments[0] == "repair")
                File.WriteAllText(request.Arguments.Single(argument => argument.StartsWith("--dbpath=", StringComparison.Ordinal))[9..], "replacement index");
            else File.Delete(Path.Combine(repository, ids[0]));
            return Success;
        });
        await new DuplicatiEngine(retry, executable, new FakeLog()).DeleteVersionsAsync(history, [ids[0]], CancellationToken.None, resume: true);
        Check(!File.Exists(firstIndex) && File.Exists(Path.Combine(history, "index.sqlite")), "续办清理后应保留已验证的新索引并释放旧索引");

        var fullRoot = Directory.CreateDirectory(Path.Combine(fixture, "cleanup-all-resume")).FullName;
        var fullHistory = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(fullRoot)).FullName;
        var fullRepository = Directory.CreateDirectory(Path.Combine(fullHistory, "repository")).FullName;
        WriteVolume(Path.Combine(fullRepository, ids[0]), true, "SHA256", new { type = "Folder", path = fullRoot + Path.DirectorySeparatorChar });
        var block = Path.Combine(fullRepository, "duplicati-b" + new string('a', 32) + ".dblock.zip");
        File.WriteAllText(block, "owned native block fixture");
        string? originalDatabase = null;
        var interrupted = new FakeRunner(request =>
        {
            var db = request.Arguments.Single(argument => argument.StartsWith("--dbpath=", StringComparison.Ordinal))[9..];
            if (request.Arguments[0] == "repair") { originalDatabase = db; File.WriteAllText(db, "native delete state"); }
            else
            {
                Check(request.Arguments.Contains("--allow-full-removal=true"), "全删必须明确开启官方最后版本删除");
                File.Delete(Path.Combine(fullRepository, ids[0]));
                throw new IOException("interrupted after fileset deletion");
            }
            return Success;
        });
        await ExpectAsync<IOException>(() => new DuplicatiEngine(interrupted, executable, new FakeLog())
            .DeleteVersionsAsync(fullHistory, [ids[0]], CancellationToken.None, allowEmpty: true), "中断清理不得报成功");
        var resumed = new FakeRunner(request =>
        {
            Check(request.Arguments[0] == "compact" && request.Arguments.Contains("--dbpath=" + originalDatabase),
                "全删中断须复用持久化原生索引完成官方compact");
            File.Delete(block);
            return Success;
        });
        await new DuplicatiEngine(resumed, executable, new FakeLog()).DeleteVersionsAsync(fullHistory, [ids[0]], CancellationToken.None,
            resume: true, allowEmpty: true);
        Check(!Directory.EnumerateFileSystemEntries(fullRepository).Any() && !File.Exists(Path.Combine(fullHistory, "cleanup.json")) &&
            originalDatabase is not null && !File.Exists(originalDatabase),
            "全删中断后未完成内容和记录核对");
    }

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
        var canceledLog = new FakeLog();
        var canceledRunner = new FakeRunner(_ =>
        {
            canceled.Cancel();
            throw new OperationCanceledException(canceled.Token);
        });
        var canceling = new DuplicatiEngine(canceledRunner, executable, canceledLog);
        await ExpectAsync<OperationCanceledException>(() => canceling.BackupAsync(root, history, new(true)), "启动前取消应跳过清单读取与引擎调用");
        Check(canceledRunner.Requests.Count == 0 && canceledLog.Entries.Count == 0, "启动前取消仍调用了引擎或生成了进程日志");
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
