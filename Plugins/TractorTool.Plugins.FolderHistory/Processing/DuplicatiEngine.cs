using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Processing;

public sealed class DuplicatiEngine(IProcessRunner processes, string executable, IAppLog log)
{
    public const string PackageId = "Duplicati.CLI";
    public const string PackageVersion = "1.0.1";
    public const string ToolName = "duplicati";

    private const string FilePrefix = "duplicati-";
    private const string FileSuffix = ".dlist.zip";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int MetadataLimit = 64 * 1024 * 1024;
    private const int DiagnosticLimit = 16 * 1024;
    private const string PermissionLogTag = "Information-Duplicati.Library.SQLiteHelper.SQLiteLoader-SQLiteFilePermissionError";
    private const string LogSource = "TractorTool.FolderHistory";
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowDuplicateProperties = false };

    public static HistoryVersion? ReadVersion(string historyDirectory, string versionId)
    {
        if (Path.GetFileName(versionId) != versionId) throw new InvalidDataException("历史版本标识不是文件名。");
        return ReadVersion(ParseVolume(Path.Combine(Repository(historyDirectory), versionId)));
    }

    public Task<HistoryVersion> BackupAsync(string root, string historyDirectory, CancellationToken token,
        IReadOnlyList<string>? excludedRelativePaths = null)
    {
        root = HistoryPaths.Absolute(root);
        HistoryPaths.RequireDirectory(root);
        if (!HistoryPaths.Comparer.Equals(HistoryPaths.HistoryDirectory(root), HistoryPaths.Absolute(historyDirectory)))
            throw new InvalidDataException("历史库路径和工作目录不匹配。");
        return BackupCoreAsync(root, historyDirectory, token, excludedRelativePaths);
    }

    internal Task<HistoryVersion> BackupStagedAsync(string source, string historyDirectory, CancellationToken token)
    {
        source = HistoryPaths.Absolute(source);
        HistoryPaths.RequireLocalVolume(source);
        if (!HistoryPaths.Comparer.Equals(PublishSourceHistoryDirectory(source), HistoryPaths.Absolute(historyDirectory)))
            throw new InvalidDataException("发布只能备份当前历史库内专用的暂存源目录。");
        return BackupCoreAsync(source, historyDirectory, token, []);
    }

    private static string? PublishSourceHistoryDirectory(string source)
    {
        var name = Path.GetFileName(source);
        var temporary = Path.GetDirectoryName(source);
        var history = temporary is null ? null : Path.GetDirectoryName(temporary);
        var owner = history is null ? null : Path.GetDirectoryName(history);
        if (!Regex.IsMatch(name, "^publish-source-[0-9a-f]{32}$", RegexOptions.CultureInvariant) ||
            !HistoryPaths.Comparer.Equals(Path.GetFileName(temporary), "temp") ||
            !HistoryPaths.Comparer.Equals(Path.GetFileName(history), HistoryPaths.DirectoryName) ||
            owner is null || Path.GetDirectoryName(owner) is null)
            return null;
        // Stored source roots may no longer exist after a folder move; validate their shape without touching that old location.
        HistoryPaths.RejectHistoryContainer(owner);
        return history;
    }

    private async Task<HistoryVersion> BackupCoreAsync(string root, string historyDirectory, CancellationToken token,
        IReadOnlyList<string>? excludedRelativePaths)
    {
        HistoryPaths.RequireDirectory(root);
        historyDirectory = HistoryPaths.Absolute(historyDirectory);
        var volumes = ReadVolumes(historyDirectory);
        foreach (var volume in volumes) _ = ReadVersion(volume);
        var before = volumes.Select(volume => volume.Id).ToHashSet(StringComparer.Ordinal);
        var repository = Repository(historyDirectory);
        Directory.CreateDirectory(repository);
        if (before.Count != 0 && HistoryPaths.Attributes(Index(historyDirectory)) is null)
            await RunAsync("repair", historyDirectory, [], token).ConfigureAwait(false);

        var exclusions = HistoryExclusions.Normalize(excludedRelativePaths ?? []);
        var separator = Regex.Escape(Path.DirectorySeparatorChar.ToString());
        var filters = new List<string>
        {
            $"--exclude=[^{EscapeFilter(root)}{separator}(?:.*{separator})?{Regex.Escape(HistoryPaths.DirectoryName)}(?:{separator}.*)?$]"
        };
        filters.AddRange(exclusions.Select(path => ExcludePath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))));
        // A separator-only marker list disables the default without passing an invalid blank path.
        var ignoreMarkers = $"--ignore-filenames={Path.PathSeparator}";
        await RunAsync("backup", historyDirectory,
            [
                root, .. filters, "--snapshot-policy=off", "--usn-policy=off",
                "--disable-filetime-check=true", "--exclude-empty-folders=false", ignoreMarkers,
                "--abort-if-source-missing=true", "--symlink-policy=store", "--enable-ads-backup=false",
                "--upload-unchanged-backups=true", "--keep-versions=0", "--keep-time=", "--retention-policy=",
                "--no-auto-compact=true", "--auto-vacuum=false"
            ], token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        var added = ReadVolumes(historyDirectory).Where(volume => !before.Contains(volume.Id))
            .Select(ReadVersion).OfType<HistoryVersion>().ToArray();
        if (added.Length != 1 || !HistoryPaths.Comparer.Equals(added[0].SourceRoot, root))
            throw new InvalidDataException("保存后未找到与当前目录一致的完整版本。");
        return added[0];
    }

    public Task RestoreAsync(HistoryVersion version, string historyDirectory, string stagingDirectory, CancellationToken token,
        string? selection = null) => RestoreSelectionAsync(version, historyDirectory, stagingDirectory, token,
            [selection ?? HistorySelection.Root]);

    public async Task RestoreSelectionAsync(HistoryVersion version, string historyDirectory, string stagingDirectory, CancellationToken token,
        IReadOnlyList<string> selections)
    {
        ArgumentNullException.ThrowIfNull(version);
        var volumes = ReadVolumes(historyDirectory);
        var index = Array.FindIndex(volumes, volume => string.Equals(volume.Id, version.Id, StringComparison.Ordinal));
        if (index < 0 || ReadVersion(volumes[index]) is not { } stored || stored.CreatedAt != version.CreatedAt ||
            !HistoryPaths.Comparer.Equals(stored.SourceRoot, version.SourceRoot) || !HistoryComparison.Equal(stored.Entries, version.Entries))
            throw new InvalidDataException("所选历史版本已变化或不可读取，请刷新后重试。");

        selections = HistorySelection.Resolve(stored, selections);
        var selectedEntries = HistorySelection.Scope(stored.Entries, selections);
        var filters = selections.Contains(HistorySelection.Root) ? Array.Empty<string>() : RestoreFilters(stored, selections, selectedEntries);

        var temporary = TempDirectory(historyDirectory);
        var staging = HistoryPaths.Absolute(stagingDirectory);
        if (HistoryPaths.Comparer.Equals(staging, temporary) ||
            !staging.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("历史必须提取到专用暂存目录。");
        HistoryPaths.CheckPath(staging);
        Directory.CreateDirectory(staging);
        if (Directory.EnumerateFileSystemEntries(staging).Any())
            throw new IOException("恢复暂存目录已含内容。");

        var emptyRestoreWarning = selectedEntries.All(entry => entry.IsDirectory)
            ? selectedEntries.Count == 0 ? "NoFilesOrFoldersRestored" : "NoFilesRestored"
            : null;
        // Keep large literal selections out of the Windows process command line.
        string? parameterFile = null;
        FileStream? lease = null;
        try
        {
            if (selections.Count > 1 || filters.Any(filter => filter.Length > 4096))
            {
                var file = Path.Combine(temporary, "selection-" + Guid.NewGuid().ToString("N") + ".txt");
                var content = Encoding.UTF8.GetBytes(string.Join('\n', filters));
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    parameterFile = file;
                    await output.WriteAsync(content, token).ConfigureAwait(false);
                }
                // Duplicati opens the file independently. Hold a read-only lease so options cannot change after verification.
                HistoryPaths.CheckPath(file);
                lease = File.OpenRead(file);
                var actual = await SHA256.HashDataAsync(lease, token).ConfigureAwait(false);
                if (lease.Length != content.Length || !actual.SequenceEqual(SHA256.HashData(content)))
                    throw new IOException("恢复选择文件已变化，已停止提取。");
                filters = ["--parameters-file=" + Path.GetFileName(file)];
            }
            // Read the live repository's numbering, including incomplete versions, without trusting a stale local index.
            await RunAsync("restore", historyDirectory,
                [
                    $"--version={index.ToString(CultureInfo.InvariantCulture)}", $"--restore-path={staging}", .. filters,
                    "--no-local-db=true", "--overwrite=false", "--dont-compress-restore-paths=false",
                    "--allow-restore-outside-target-directory=false", "--skip-metadata=true", "--disable-ads-restore=true"
                ], token, emptyRestoreWarning, workingDirectory: parameterFile is null ? null : temporary).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
            if (parameterFile is not null)
            {
                try { HistoryPaths.CheckPath(parameterFile); File.Delete(parameterFile); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { log.Write(AppLogLevel.Warning, LogSource, "本次恢复的临时选择文件未能清理：" + parameterFile, error); }
            }
        }

        var restored = await WorkingTree.ScanAsync(staging, excludeHistory: false, token).ConfigureAwait(false);
        if (!HistoryComparison.Equal(selectedEntries, restored))
            throw new InvalidDataException("提取内容与所选版本的目录清单或 SHA256 不符。");
    }

    public static IReadOnlyList<HistoryEntry> SelectEntries(HistoryVersion version, string? selection)
        => SelectEntriesForSelection(version, [selection ?? HistorySelection.Root]);

    public static IReadOnlyList<HistoryEntry> SelectEntriesForSelection(HistoryVersion version, IReadOnlyList<string> selections)
        => HistorySelection.Scope(version.Entries, HistorySelection.Resolve(version, selections));

    public static void PreflightDelete(string historyDirectory, IReadOnlyList<string> versionIds, bool allowEmpty = false)
    {
        historyDirectory = HistoryPaths.Absolute(historyDirectory);
        ValidateDeletionIds(historyDirectory, versionIds);
        if (ReadCleanup(Path.Combine(historyDirectory, "cleanup.json")) is not null)
            throw new InvalidDataException("存在未完成的清理记录，请先继续原操作。");
        var before = ReadCleanupRepository(historyDirectory, out var fullVersions);
        ValidateDeletionSelection(versionIds, before, fullVersions, resume: false, allowEmpty);
    }

    private static void ValidateDeletionIds(string historyDirectory, IReadOnlyList<string> versionIds)
    {
        if (versionIds.Count == 0 || versionIds.Any(string.IsNullOrWhiteSpace) ||
            versionIds.Distinct(StringComparer.Ordinal).Count() != versionIds.Count)
            throw new InvalidDataException("清理版本列表为空或含重复项。");
        foreach (var id in versionIds)
        {
            if (Path.GetFileName(id) != id) throw new InvalidDataException("清理版本标识无效。");
            _ = ParseVolume(Path.Combine(Repository(historyDirectory), id));
        }
    }

    public async Task DeleteVersionsAsync(string historyDirectory, IReadOnlyList<string> versionIds, CancellationToken token,
        bool resume = false, bool allowEmpty = false, Action? deletionStarting = null)
    {
        token.ThrowIfCancellationRequested();
        historyDirectory = HistoryPaths.Absolute(historyDirectory);
        ValidateDeletionIds(historyDirectory, versionIds);
        var before = ReadCleanupRepository(historyDirectory, out var fullVersions);
        var selected = versionIds.ToHashSet(StringComparer.Ordinal);
        ValidateDeletionSelection(versionIds, before, fullVersions, resume, allowEmpty);
        var retained = before.Where(pair => !selected.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        var journalPath = Path.Combine(historyDirectory, "cleanup.json");
        var pending = ReadCleanup(journalPath);
        if (pending is not null)
        {
            if (!resume || pending.AllowEmpty != allowEmpty || !selected.SetEquals(pending.VersionIds))
                throw new InvalidDataException("存在另一份未完成的清理记录，请先处理原操作。");
            RequireSameRepository(pending.Retained, retained);
        }
        if (retained.Count == 0 && !allowEmpty)
            throw new InvalidDataException("版本清理至少保留一个完整版本；删除整个历史请使用独立操作。");
        if (before.Count == 0)
        {
            if (!resume || !allowEmpty)
                throw new InvalidDataException("清理缺少可核对的完整版本，已停止。");
            if (Directory.EnumerateFileSystemEntries(Repository(historyDirectory)).Any())
            {
                if (pending is null) throw new InvalidDataException("剩余内容块缺少原生清理索引，已保留材料。");
                var previousDatabase = Path.Combine(TempDirectory(historyDirectory), pending.DatabaseName);
                HistoryPaths.CheckPath(previousDatabase);
                if (!File.Exists(previousDatabase)) throw new InvalidDataException("未完成清理的原生索引已丢失，已保留材料。");
                deletionStarting?.Invoke();
                await RunAsync("compact", historyDirectory, ["--auto-vacuum=false"], token, databasePath: previousDatabase).ConfigureAwait(false);
            }
            if (Directory.EnumerateFileSystemEntries(Repository(historyDirectory)).Any())
                throw new InvalidDataException("引擎尚未回收全部内容块，已保留清理记录。");
            RequireClosedDatabase(Index(historyDirectory));
            File.Delete(Index(historyDirectory));
            DeleteCleanupJournal(journalPath);
            return;
        }

        // A fresh native index prevents stale local numbering from deleting a different version.
        var temporary = TempDirectory(historyDirectory);
        HistoryPaths.CheckPath(temporary);
        Directory.CreateDirectory(temporary);
        var database = Path.Combine(temporary, "cleanup-" + Guid.NewGuid().ToString("N") + ".sqlite");
        await RunAsync("repair", historyDirectory, [], token, databasePath: database).ConfigureAwait(false);
        RequireSameRepository(before, ReadCleanupRepository(historyDirectory, out _));
        WriteCleanup(journalPath, new(versionIds.ToArray(), allowEmpty, retained, Path.GetFileName(database)));
        deletionStarting?.Invoke();
        var ordered = ReadVolumes(historyDirectory);
        var indices = ordered.Select((volume, index) => (volume, index)).Where(item => selected.Contains(item.volume.Id))
            .Select(item => item.index.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (indices.Length != 0)
            await RunAsync("delete", historyDirectory,
                [$"--version={string.Join(',', indices)}", "--keep-versions=0", "--keep-time=", "--retention-policy=",
                    $"--allow-full-removal={allowEmpty.ToString().ToLowerInvariant()}", "--no-auto-compact=false", "--auto-vacuum=false"],
                token, databasePath: database).ConfigureAwait(false);
        else
            await RunAsync("compact", historyDirectory, ["--auto-vacuum=false"], token, databasePath: database).ConfigureAwait(false);
        RequireSameRepository(retained, ReadCleanupRepository(historyDirectory, out _));
        if (retained.Count == 0 && Directory.EnumerateFileSystemEntries(Repository(historyDirectory)).Any())
            throw new InvalidDataException("引擎未回收全部历史内容，已保留清理记录。");
        token.ThrowIfCancellationRequested();
        foreach (var path in new[] { database, Index(historyDirectory) }) RequireClosedDatabase(path);
        File.Move(database, Index(historyDirectory), overwrite: true);
        DeleteCleanupJournal(journalPath);
    }

    private sealed record CleanupJournal(
        [property: JsonRequired] string[] VersionIds,
        [property: JsonRequired] bool AllowEmpty,
        [property: JsonRequired] Dictionary<string, string> Retained,
        [property: JsonRequired] string DatabaseName,
        [property: JsonRequired] int SchemaVersion = 1);

    private static CleanupJournal? ReadCleanup(string path)
    {
        HistoryPaths.CheckPath(path);
        if (HistoryPaths.Attributes(path) is null) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MetadataLimit) throw new InvalidDataException("清理记录超过读取限制。");
        var value = JsonSerializer.Deserialize<CleanupJournal>(input, new JsonSerializerOptions
            { AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (value?.SchemaVersion != 1 || value.VersionIds is null || value.Retained is null || value.DatabaseName is null ||
            !Regex.IsMatch(value.DatabaseName, @"^cleanup-[0-9a-f]{32}\.sqlite$", RegexOptions.CultureInvariant) ||
            value.VersionIds.Length == 0 || value.VersionIds.Distinct(StringComparer.Ordinal).Count() != value.VersionIds.Length ||
            value.Retained.Any(item => !Regex.IsMatch(item.Key, @"^duplicati-\d{8}T\d{6}Z\.dlist\.zip$", RegexOptions.CultureInvariant) ||
                item.Value is null || !Regex.IsMatch(item.Value, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("清理记录损坏或格式不兼容，已保留材料。");
        return value;
    }

    private static void WriteCleanup(string path, CleanupJournal value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MetadataLimit) throw new InvalidDataException("清理记录超过容量限制。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        HistoryPaths.CheckPath(path);
        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteCleanupJournal(string path)
    {
        HistoryPaths.CheckPath(path);
        File.Delete(path);
    }

    private static void RequireClosedDatabase(string path)
    {
        HistoryPaths.CheckPath(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (HistoryPaths.Attributes(path + suffix) is not null)
                throw new IOException("清理索引尚有事务辅助文件，已保留材料并停止发布索引。");
    }

    private static void ValidateDeletionSelection(IReadOnlyList<string> selected, IReadOnlyDictionary<string, string> volumes,
        IReadOnlySet<string> fullVersions, bool resume, bool allowEmpty)
    {
        if (selected.Any(id => volumes.ContainsKey(id) ? !fullVersions.Contains(id) : !resume))
            throw new InvalidDataException("只能清理已确认的完整版本；预览版本已变化或包含不完整卷。");
        if (allowEmpty && (fullVersions.Count != volumes.Count || fullVersions.Any(id => !selected.Contains(id))))
            throw new InvalidDataException("库中存在未纳入确认范围的版本或不完整卷，不能扩大全部版本的删除范围。");
        if (!allowEmpty && !fullVersions.Any(id => !selected.Contains(id)))
            throw new InvalidDataException("版本清理至少保留一个完整版本；删除整个历史请使用独立操作。");
    }

    private static Dictionary<string, string> ReadCleanupRepository(string historyDirectory, out HashSet<string> fullVersions)
    {
        var volumes = ReadVolumes(historyDirectory);
        foreach (var path in Directory.EnumerateFileSystemEntries(Repository(historyDirectory)))
        {
            HistoryPaths.CheckPath(path);
            if (HistoryPaths.Attributes(path)?.HasFlag(FileAttributes.Directory) != false ||
                !Regex.IsMatch(Path.GetFileName(path), @"^duplicati-(?:\d{8}T\d{6}Z\.dlist|b[0-9a-f]{32}\.dblock|i[0-9a-f]{32}\.dindex)\.zip$",
                    RegexOptions.CultureInvariant))
                throw new InvalidDataException("历史内容目录存在未知文件或目录，已停止清理。");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        fullVersions = new(StringComparer.Ordinal);
        foreach (var volume in volumes)
        {
            // Native version numbers include partial filesets; validate and retain them without exposing them as restorable versions.
            if (ReadVersion(volume, validatePartial: true) is not null) fullVersions.Add(volume.Id);
            using var input = new FileStream(volume.File, FileMode.Open, FileAccess.Read, FileShare.Read);
            result.Add(volume.Id, Convert.ToHexString(SHA256.HashData(input)));
        }
        return result;
    }

    private static void RequireSameRepository(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new InvalidDataException("清理前后版本列表或保留版本内容不符，已停止并保留待处理记录。");
    }

    private static string ExcludePath(string path) => $"--exclude=[^{EscapeFilter(path)}(?:{Regex.Escape(Path.DirectorySeparatorChar.ToString())}.*)?$]";

    private static string EscapeFilter(string value) => Regex.Escape(value).Replace("%", @"\x25", StringComparison.Ordinal);

    private static string[] RestoreFilters(HistoryVersion version, IReadOnlyList<string> selections, IReadOnlyList<HistoryEntry> entries)
    {
        var selected = selections.ToHashSet(HistoryPaths.Comparer);
        var root = version.SourceRoot + Path.DirectorySeparatorChar;
        // Include the stored root and ancestors so Duplicati keeps the same relative path mapping.
        var exact = entries.Where(item => !HistorySelection.Contains(selected, item.Path))
            .Select(item => EscapeFilter(root + item.Path.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
        var selectedPaths = entries.Where(entry => selected.Contains(entry.Path)).Select(entry => EscapeFilter(root + entry.Path.Replace('/', Path.DirectorySeparatorChar)) +
            (entry.IsDirectory ? Regex.Escape(Path.DirectorySeparatorChar.ToString()) + ".*" : ""));
        return [$"--include=[^(?:{string.Join('|', new[] { EscapeFilter(root) }.Concat(exact).Concat(selectedPaths))})$]", "--exclude=*"];
    }

    private async Task RunAsync(string command, string historyDirectory, IReadOnlyList<string> arguments,
        CancellationToken token, string? emptyRestoreWarning = null, string? databasePath = null, string? workingDirectory = null)
    {
        HistoryPaths.RequireDirectory(historyDirectory);
        var repository = Repository(historyDirectory);
        HistoryPaths.RequireDirectory(repository);
        var temporary = TempDirectory(historyDirectory);
        HistoryPaths.CheckPath(temporary);
        var database = databasePath ?? Index(historyDirectory);
        HistoryPaths.CheckPath(database);
        Directory.CreateDirectory(temporary);
        var logPath = Path.Combine(temporary, $"duplicati-{command}-{Guid.NewGuid():N}.log");
        ProcessResult? result = null;
        Exception? processError = null;
        try
        {
            result = await processes.RunAsync(new ProcessRequest(executable,
                [
                    command, LocalUri(repository), .. arguments, $"--dbpath={database}", $"--tempdir={temporary}",
                    "--portable-mode=true", "--no-encryption=true", "--compression-module=zip", "--file-hash-algorithm=SHA256",
                    "--disable-autocreate-folder=true", "--allowed-backend-modules=file", "--use-move-for-put=true",
                    "--suppress-warnings=NoFilesInBackup,SQLiteFilePermissionError" + (emptyRestoreWarning is null ? "" : "," + emptyRestoreWarning),
                    $"--log-file={logPath}", "--log-file-log-level=Warning", $"--log-file-log-filter=+{PermissionLogTag}",
                    "--disable-module=runscript,sendmail,sendhttp,sendxmpp,sendtelegram"
                ]) { WorkingDirectory = workingDirectory }, token).ConfigureAwait(false);
        }
        catch (Exception error) { processError = error; }

        var (operationLog, logError) = ReadOperationLog(logPath);
        var (permissionWarning, entryError) = CheckOperationLog(operationLog);
        var details = $"Command: {command}\nExitCode: {result?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}" +
            $"\nLog: {logPath}\nLogStatus: {logError ?? "complete"}\nLogEntries: {entryError ?? "compatible"}" +
            $"\nOperation log:\n{operationLog}\nProcess error:\n{Limit(processError?.ToString() ?? "none")}" +
            $"\nstderr (truncated: {result?.StandardErrorTruncated}):\n{Limit(result?.StandardError ?? "unavailable")}" +
            $"\nstdout (truncated: {result?.StandardOutputTruncated}):\n{Limit(result?.StandardOutput ?? "unavailable")}";
        if (permissionWarning)
            log.Write(AppLogLevel.Warning, LogSource,
                "Duplicati 未能收紧索引文件权限；历史仍受所在目录权限控制。", new IOException(details));

        if (processError is OperationCanceledException && token.IsCancellationRequested)
        {
            log.Write(AppLogLevel.Info, LogSource, "Duplicati 操作已取消，已记录现有诊断。", new IOException(details));
            throw new OperationCanceledException("操作已取消。诊断日志位置：" + logPath, token);
        }
        var failure = processError is not null || result is null ? $"Duplicati {command} 执行失败。" :
            logError is not null ? "Duplicati 诊断日志不完整，无法确认操作结果。" :
            entryError is not null ? "Duplicati 操作日志包含未兼容的警告、错误或未知内容。" :
            result.StandardOutputTruncated || result.StandardErrorTruncated ? "Duplicati 输出已截断，无法确认操作结果。" :
            !string.IsNullOrWhiteSpace(result.StandardError) ? "Duplicati 报告错误，无法确认操作结果。" :
            result.ExitCode != 0 ? $"Duplicati {command} 未成功完成（退出码 {result.ExitCode}）。" : null;
        if (failure is not null) throw new EngineException(failure + "诊断日志位置：" + logPath, details);

        try
        {
            HistoryPaths.CheckPath(logPath);
            File.Delete(logPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Write(AppLogLevel.Warning, LogSource, "Duplicati 已完成，本次诊断日志未能清理：" + logPath, error);
        }
    }

    private static (bool PermissionWarning, string? Error) CheckOperationLog(string text)
    {
        var permissionWarning = false;
        var permissionEntry = false;
        string? error = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            // The CLI's calendar and time separator follow its culture; only the native header shape is needed.
            if (Regex.IsMatch(line, @"^\d{4,}-\d{2}-\d{2} ", RegexOptions.CultureInvariant))
            {
                var separator = line.IndexOf(" - [", StringComparison.Ordinal);
                var end = separator >= 0 ? line.IndexOf("]: ", separator + 4, StringComparison.Ordinal) : -1;
                if (separator <= 0 || end < 0 ||
                    !Regex.IsMatch(line[..separator], @"^\S+ .+ [+-]\d{2}$", RegexOptions.CultureInvariant))
                {
                    permissionEntry = false;
                    error ??= "操作日志包含无法识别的条目头。";
                    continue;
                }
                permissionEntry = line.AsSpan(separator + 4, end - separator - 4).SequenceEqual(PermissionLogTag);
                permissionWarning |= permissionEntry;
                if (!permissionEntry) error ??= "操作日志包含未兼容的警告、错误或其他条目。";
            }
            // Exception details are opaque continuation lines, only after a recognized permission entry.
            else if (!permissionEntry) error ??= "操作日志包含无法识别的内容。";
        }
        return (permissionWarning, error);
    }

    private static (string Text, string? Error) ReadOperationLog(string path)
    {
        var text = new StringBuilder();
        try
        {
            HistoryPaths.CheckPath(path);
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), new UTF8Encoding(false, true));
            var buffer = new char[4096];
            while (text.Length <= DiagnosticLimit)
            {
                var count = reader.Read(buffer, 0, Math.Min(buffer.Length, DiagnosticLimit + 1 - text.Length));
                if (count == 0) return (text.ToString(), null);
                text.Append(buffer, 0, count);
            }
            return (Limit(text.ToString()), "诊断日志超过读取限制，原文件已保留。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return (text.ToString(), "诊断日志无法读取：" + error.Message);
        }
    }

    private static string Limit(string value) => value.Length <= DiagnosticLimit ? value : value[..DiagnosticLimit] + "\n[已截断]";

    private static string LocalUri(string directory)
    {
        var drive = Path.GetPathRoot(directory)!;
        var segments = directory[drive.Length..].Split(Path.DirectorySeparatorChar);
        var path = string.Join('/', segments.Select(Uri.EscapeDataString));
        return $"file:///{drive.TrimEnd(Path.DirectorySeparatorChar)}/{path}";
    }

    private static NativeVolume[] ReadVolumes(string historyDirectory)
    {
        var repository = Repository(historyDirectory);
        HistoryPaths.CheckPath(repository);
        if (HistoryPaths.Attributes(repository) is null) return [];
        HistoryPaths.RequireDirectory(repository);
        return Directory.EnumerateFiles(repository, "*.dlist.*").Select(ParseVolume).OrderByDescending(volume => volume.CreatedAt).ToArray();
    }

    private static NativeVolume ParseVolume(string path)
    {
        HistoryPaths.CheckPath(path);
        var name = Path.GetFileName(path);
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParseExact(name[FilePrefix.Length..^FileSuffix.Length], TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
            throw new InvalidDataException("历史库包含未知格式的版本文件。");
        return new(path, name, createdAt);
    }

    private static HistoryVersion? ReadVersion(NativeVolume volume) => ReadVersion(volume, validatePartial: false);

    private static HistoryVersion? ReadVersion(NativeVolume volume, bool validatePartial)
    {
        try
        {
            return ReadVolumeContents(volume, validatePartial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("历史版本的清单缺少字段或格式无效。", exception);
        }
    }

    private static HistoryVersion? ReadVolumeContents(NativeVolume volume, bool validatePartial)
    {
        HistoryPaths.CheckPath(volume.File);
        using var archive = ZipFile.OpenRead(volume.File);
        using var fileset = ReadMetadata(archive, "fileset", 4096);
        var full = fileset.RootElement.GetProperty("IsFullBackup").GetBoolean();
        if (!full && !validatePartial) return null;
        using var manifest = ReadMetadata(archive, "manifest", 4096);
        if (manifest.RootElement.GetProperty("Version").GetInt32() != 2 ||
            manifest.RootElement.GetProperty("FileHash").GetString() != "SHA256")
            throw new InvalidDataException("无法读取历史版本的清单格式或哈希算法。");

        using var filelist = ReadMetadata(archive, "filelist.json", MetadataLimit);
        var entries = new Dictionary<string, HistoryEntry>(HistoryPaths.Comparer);
        string? root = null;
        foreach (var item in filelist.RootElement.EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            var path = item.GetProperty("path").GetString() ?? throw new InvalidDataException("历史版本缺少项目路径。");
            var normalized = HistoryPaths.Absolute(path);
            if (!HistoryPaths.Comparer.Equals(normalized, path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                throw new InvalidDataException("历史清单包含非规范路径。");
            var entry = type switch
            {
                "Folder" => new HistoryEntry(normalized, true, 0, ""),
                "File" => new HistoryEntry(normalized, false, item.GetProperty("size").GetInt64(), ReadHash(item)),
                _ => throw new InvalidDataException("历史版本含有不支持的链接或其他项目类型。")
            };
            if (entry.Length < 0 || !entries.TryAdd(normalized, entry))
                throw new InvalidDataException("历史清单含有无效大小或重复路径。");
            if (entry.IsDirectory && (root is null || normalized.Length < root.Length)) root = normalized;
        }

        if (!full && root is null)
        {
            foreach (var item in entries.Values) HistoryPaths.RejectHistoryContainer(item.Path);
            return null;
        }
        if (root is null || Path.GetDirectoryName(root) is null)
            throw new InvalidDataException("历史清单缺少可识别的源目录。");
        if (PublishSourceHistoryDirectory(root) is null) HistoryPaths.RejectHistoryContainer(root);
        var prefix = root + Path.DirectorySeparatorChar;
        var relative = new List<HistoryEntry>(entries.Count - 1);
        foreach (var item in entries.Values)
        {
            if (HistoryPaths.Comparer.Equals(item.Path, root)) continue;
            if (!item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                full && (!entries.TryGetValue(Path.GetDirectoryName(item.Path)!, out var parent) || !parent.IsDirectory))
                throw new InvalidDataException("历史清单包含越界或缺少父目录的路径。");
            relative.Add(item with { Path = HistoryPaths.NormalizeRelative(item.Path[prefix.Length..]) });
        }
        return full ? new(volume.Id, volume.CreatedAt, root, relative.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray()) : null;
    }

    private static string ReadHash(JsonElement item)
    {
        var encoded = item.GetProperty("hash").GetString();
        Span<byte> bytes = stackalloc byte[32];
        if (encoded is null || !Convert.TryFromBase64String(encoded, bytes, out var length) || length != bytes.Length)
            throw new InvalidDataException("历史清单包含无效的 SHA256。");
        return Convert.ToHexString(bytes);
    }

    private static JsonDocument ReadMetadata(ZipArchive archive, string name, long maxLength)
    {
        var entries = archive.Entries.Where(entry => string.Equals(entry.FullName, name, StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 || entries[0].Length > maxLength)
            throw new InvalidDataException("历史版本缺少必要的清单，或清单超过读取限制。");
        using var input = entries[0].Open();
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = reader.Read(buffer)) != 0)
        {
            if (count > maxLength - text.Length) throw new InvalidDataException("历史清单内容超过读取限制。");
            text.Append(buffer, 0, count);
        }
        return JsonDocument.Parse(text.ToString(), JsonOptions);
    }

    private static string Repository(string directory) => Path.Combine(HistoryPaths.Absolute(directory), "repository");
    private static string Index(string directory) => Path.Combine(HistoryPaths.Absolute(directory), "index.sqlite");
    private static string TempDirectory(string directory) => Path.Combine(HistoryPaths.Absolute(directory), "temp");
    private sealed record NativeVolume(string File, string Id, DateTimeOffset CreatedAt);
    // Task summaries use GetBaseException().Message; only the log should receive command output.
    private sealed class EngineException(string message, string details) : IOException(message)
    {
        public override string ToString() => base.ToString() + Environment.NewLine + details;
    }
}
