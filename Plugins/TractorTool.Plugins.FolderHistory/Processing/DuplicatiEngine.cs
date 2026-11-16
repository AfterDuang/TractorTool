using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
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

    public async Task<HistoryVersion> BackupAsync(string root, string historyDirectory, CancellationToken token)
    {
        root = HistoryPaths.Absolute(root);
        HistoryPaths.RequireDirectory(root);
        if (!HistoryPaths.Comparer.Equals(HistoryPaths.HistoryDirectory(root), HistoryPaths.Absolute(historyDirectory)))
            throw new InvalidDataException("历史库路径和工作目录不匹配。");

        historyDirectory = HistoryPaths.Absolute(historyDirectory);
        var volumes = ReadVolumes(historyDirectory);
        foreach (var volume in volumes) _ = ReadVersion(volume);
        var before = volumes.Select(volume => volume.Id).ToHashSet(StringComparer.Ordinal);
        var repository = Repository(historyDirectory);
        Directory.CreateDirectory(repository);
        if (before.Count != 0 && HistoryPaths.Attributes(Index(historyDirectory)) is null)
            await RunAsync("repair", historyDirectory, [], token).ConfigureAwait(false);

        // Duplicati expands environment variables before compiling bracket-delimited filters.
        var excluded = Regex.Escape(historyDirectory + Path.DirectorySeparatorChar).Replace("%", @"\x25", StringComparison.Ordinal);
        // A separator-only marker list disables the default without passing an invalid blank path.
        var ignoreMarkers = $"--ignore-filenames={Path.PathSeparator}";
        await RunAsync("backup", historyDirectory,
            [
                root, $"--exclude=[^{excluded}$]", "--snapshot-policy=off", "--usn-policy=off",
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

    public async Task RestoreAsync(HistoryVersion version, string historyDirectory, string stagingDirectory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(version);
        var volumes = ReadVolumes(historyDirectory);
        var index = Array.FindIndex(volumes, volume => string.Equals(volume.Id, version.Id, StringComparison.Ordinal));
        if (index < 0 || ReadVersion(volumes[index]) is not { } stored || stored.CreatedAt != version.CreatedAt ||
            !HistoryPaths.Comparer.Equals(stored.SourceRoot, version.SourceRoot) || !HistoryComparison.Equal(stored.Entries, version.Entries))
            throw new InvalidDataException("所选历史版本已变化或不可读取，请刷新后重试。");

        var temporary = TempDirectory(historyDirectory);
        var staging = HistoryPaths.Absolute(stagingDirectory);
        if (HistoryPaths.Comparer.Equals(staging, temporary) ||
            !staging.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("历史必须提取到专用暂存目录。");
        HistoryPaths.CheckPath(staging);
        Directory.CreateDirectory(staging);
        if (Directory.EnumerateFileSystemEntries(staging).Any())
            throw new IOException("恢复暂存目录已含内容。");

        var emptyRestoreWarning = stored.Entries.All(entry => entry.IsDirectory)
            ? stored.Entries.Count == 0 ? "NoFilesOrFoldersRestored" : "NoFilesRestored"
            : null;
        // Read the live repository's numbering, including incomplete versions, without trusting a stale local index.
        await RunAsync("restore", historyDirectory,
            [
                $"--version={index.ToString(CultureInfo.InvariantCulture)}", $"--restore-path={staging}",
                "--no-local-db=true", "--overwrite=false", "--dont-compress-restore-paths=false",
                "--allow-restore-outside-target-directory=false", "--skip-metadata=true", "--disable-ads-restore=true"
            ], token, emptyRestoreWarning).ConfigureAwait(false);

        var restored = await WorkingTree.ScanAsync(staging, excludeHistory: false, token).ConfigureAwait(false);
        if (!HistoryComparison.Equal(stored.Entries, restored))
            throw new InvalidDataException("提取内容与所选版本的目录清单或 SHA256 不符。");
    }

    private async Task RunAsync(string command, string historyDirectory, IReadOnlyList<string> arguments,
        CancellationToken token, string? emptyRestoreWarning = null)
    {
        HistoryPaths.RequireDirectory(historyDirectory);
        var repository = Repository(historyDirectory);
        HistoryPaths.RequireDirectory(repository);
        var temporary = TempDirectory(historyDirectory);
        HistoryPaths.CheckPath(temporary);
        HistoryPaths.CheckPath(Index(historyDirectory));
        Directory.CreateDirectory(temporary);
        var logPath = Path.Combine(temporary, $"duplicati-{command}-{Guid.NewGuid():N}.log");
        ProcessResult? result = null;
        Exception? processError = null;
        try
        {
            result = await processes.RunAsync(new ProcessRequest(executable,
                [
                    command, LocalUri(repository), .. arguments, $"--dbpath={Index(historyDirectory)}", $"--tempdir={temporary}",
                    "--portable-mode=true", "--no-encryption=true", "--compression-module=zip", "--file-hash-algorithm=SHA256",
                    "--disable-autocreate-folder=true", "--allowed-backend-modules=file", "--use-move-for-put=true",
                    "--suppress-warnings=NoFilesInBackup,SQLiteFilePermissionError" + (emptyRestoreWarning is null ? "" : "," + emptyRestoreWarning),
                    $"--log-file={logPath}", "--log-file-log-level=Warning", $"--log-file-log-filter=+{PermissionLogTag}",
                    "--disable-module=runscript,sendmail,sendhttp,sendxmpp,sendtelegram"
                ]), token).ConfigureAwait(false);
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

    private static HistoryVersion? ReadVersion(NativeVolume volume)
    {
        try
        {
            return ReadVolumeContents(volume);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("历史版本的清单缺少字段或格式无效。", exception);
        }
    }

    private static HistoryVersion? ReadVolumeContents(NativeVolume volume)
    {
        HistoryPaths.CheckPath(volume.File);
        using var archive = ZipFile.OpenRead(volume.File);
        using var fileset = ReadMetadata(archive, "fileset", 4096);
        if (!fileset.RootElement.GetProperty("IsFullBackup").GetBoolean()) return null;
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

        if (root is null || Path.GetDirectoryName(root) is null)
            throw new InvalidDataException("历史清单缺少可识别的源目录。");
        var prefix = root + Path.DirectorySeparatorChar;
        var relative = new List<HistoryEntry>(entries.Count - 1);
        foreach (var item in entries.Values)
        {
            if (HistoryPaths.Comparer.Equals(item.Path, root)) continue;
            if (!item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !entries.TryGetValue(Path.GetDirectoryName(item.Path)!, out var parent) || !parent.IsDirectory)
                throw new InvalidDataException("历史清单包含越界或缺少父目录的路径。");
            relative.Add(item with { Path = HistoryPaths.NormalizeRelative(item.Path[prefix.Length..]) });
        }
        return new(volume.Id, volume.CreatedAt, root, relative.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
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
