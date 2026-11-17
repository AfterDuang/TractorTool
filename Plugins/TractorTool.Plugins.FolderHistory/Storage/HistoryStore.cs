using System.Text.Json;
using System.Text.Json.Serialization;
using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

internal sealed class FolderRegistry : IVersionedSettings
{
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    [JsonRequired]
    public List<FolderRegistration> Folders { get; set; } = [];
}

internal sealed record FolderRegistration(string Root, Guid Id);

internal sealed class RepositoryState
{
    [JsonRequired]
    public int SchemaVersion { get; set; } = 2;
    [JsonRequired]
    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired]
    public string Engine { get; set; } = "Duplicati";
    [JsonRequired]
    public List<VersionReceipt> Versions { get; set; } = [];
    public bool ProtectionEnabled { get; set; }
    public FolderSettings Settings { get; set; } = new();
    public List<string> PendingDeleteIds { get; set; } = [];
    public bool PendingDeleteAll { get; set; }
}

internal sealed record VersionReceipt([property: JsonRequired] string Id, [property: JsonRequired] string SourceRoot,
    [property: JsonRequired] string Note, [property: JsonRequired] bool Pinned, bool RestoreProtection = false);

internal static class HistoryStore
{
    private const string StateName = "history.json";
    private const int StateLimit = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static RepositoryState Read(string root)
    {
        var directory = HistoryPaths.HistoryDirectory(root);
        HistoryPaths.RequireDirectory(directory);
        var file = Path.Combine(directory, StateName);
        HistoryPaths.CheckPath(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > StateLimit) throw new InvalidDataException("历史库元数据过大，已停止读取。");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var state = document.Deserialize<RepositoryState>(Json) ?? throw new InvalidDataException("历史库元数据为空。");
        if (state.SchemaVersion is not (1 or 2) || state.Id == Guid.Empty || state.Engine != "Duplicati" || state.Versions is null ||
            state.Versions.Any(v => v is null || string.IsNullOrWhiteSpace(v.Id) || Path.GetFileName(v.Id) != v.Id ||
                string.IsNullOrWhiteSpace(v.SourceRoot) || !Path.IsPathFullyQualified(v.SourceRoot) || v.Note is null || v.Note.Length > 500) ||
            state.Versions.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Versions.Count)
            throw new InvalidDataException("历史库格式不兼容或元数据损坏；未覆盖原内容。");
        if (state.SchemaVersion == 1)
        {
            if (document.RootElement.TryGetProperty("settings", out _) || document.RootElement.TryGetProperty("protectionEnabled", out _) ||
                document.RootElement.TryGetProperty("pendingDeleteIds", out _) || document.RootElement.TryGetProperty("pendingDeleteAll", out _))
                throw new InvalidDataException("旧格式库包含新版本保护记录，已停止操作；不能降级历史库。");
            // A legacy library never opted into automatic writes or deletion.
            state.ProtectionEnabled = false;
            state.Settings = new();
            state.PendingDeleteIds = [];
        }
        else
        {
            var metadata = document.RootElement;
            RequireProperties(metadata, "protectionEnabled", "settings", "pendingDeleteIds", "pendingDeleteAll");
            RequireProperties(metadata.GetProperty("settings"), "stableSeconds", "minimumIntervalSeconds", "keepLatestVersions", "keepDays", "excludedPaths");
            foreach (var receipt in metadata.GetProperty("versions").EnumerateArray()) RequireProperties(receipt, "restoreProtection");
        }
        state.Settings = ValidateSettings(state.Settings);
        if (state.PendingDeleteIds is null || state.PendingDeleteIds.Any(id => !state.Versions.Any(version => version.Id == id)) ||
            state.PendingDeleteIds.Distinct(StringComparer.Ordinal).Count() != state.PendingDeleteIds.Count ||
            state.PendingDeleteAll && (state.PendingDeleteIds.Count == 0 || state.PendingDeleteIds.Count != state.Versions.Count))
            throw new InvalidDataException("历史清理记录损坏，已停止写入和清理。");
        return state;
    }

    internal static RepositoryState Create(string root, FolderSettings? settings = null)
    {
        var destination = HistoryPaths.HistoryDirectory(root);
        HistoryPaths.CheckPath(destination);
        if (HistoryPaths.Attributes(destination) is not null)
            throw new IOException("历史目录名称已被占用，请先检查原内容。");
        var temporary = Path.Combine(root, ".tractortool-history-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var state = new RepositoryState { Settings = ValidateSettings(settings ?? new()) };
            WriteFile(Path.Combine(temporary, StateName), state);
            if (OperatingSystem.IsWindows()) File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
            Directory.Move(temporary, destination);
            return state;
        }
        finally
        {
            // Only our known staging file may be removed; unexpected contents are preserved.
            if (Directory.Exists(temporary))
            {
                HistoryPaths.RequireDirectory(temporary);
                File.Delete(Path.Combine(temporary, StateName));
                Directory.Delete(temporary);
            }
        }
    }

    internal static void Save(string root, RepositoryState state)
    {
        state.SchemaVersion = 2;
        state.Settings = ValidateSettings(state.Settings);
        var directory = HistoryPaths.HistoryDirectory(root);
        HistoryPaths.RequireDirectory(directory);
        var destination = Path.Combine(directory, StateName);
        HistoryPaths.CheckPath(destination);
        var temporary = Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteFile(temporary, state);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static FolderSettings ValidateSettings(FolderSettings? settings)
    {
        if (settings is null || settings.StableSeconds is < 1 or > 3600 || settings.MinimumIntervalSeconds is < 1 or > 86400 ||
            settings.KeepLatestVersions is < 0 or > 100000 || settings.KeepDays is < 0 or > 36500 || settings.ExcludedPaths is null ||
            settings.ExcludedPaths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("文件夹规则无效：稳定窗口须为 1–3600 秒，最小间隔 1–86400 秒；保留数量/天数不能为负数。");
        return settings with { ExcludedPaths = HistoryExclusions.Normalize(settings.ExcludedPaths).ToArray() };
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || names.Any(name => !element.TryGetProperty(name, out _)))
            throw new InvalidDataException("历史库保护或清理规则缺失，已停止写入和清理。");
    }

    internal static FileStream Lock(string root)
    {
        var path = Path.Combine(HistoryPaths.HistoryDirectory(root), "operation.lock");
        HistoryPaths.CheckPath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void WriteFile(string path, RepositoryState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        if (bytes.Length > StateLimit) throw new InvalidDataException("历史库元数据已达到本版容量上限，未覆盖原记录。");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }
}
