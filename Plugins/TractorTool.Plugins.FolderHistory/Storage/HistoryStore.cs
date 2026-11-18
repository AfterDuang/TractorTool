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
    public int SchemaVersion { get; set; } = 3;
    [JsonRequired]
    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired]
    public string Engine { get; set; } = "Duplicati";
    [JsonRequired]
    public List<VersionReceipt> Versions { get; set; } = [];
    public long LastSnapshotNumber { get; set; }
    public bool ProtectionEnabled { get; set; }
    public FolderSettings Settings { get; set; } = new();
    public List<string> PendingDeleteIds { get; set; } = [];
    public bool PendingDeleteAll { get; set; }

    public VersionReceipt AddVersion(string id, string sourceRoot, string note, bool pinned, bool restoreProtection = false)
    {
        var receipt = new VersionReceipt(id, sourceRoot, note, pinned, restoreProtection, checked(LastSnapshotNumber + 1));
        Versions.Add(receipt);
        LastSnapshotNumber = receipt.Number;
        return receipt;
    }
}

internal sealed record VersionReceipt([property: JsonRequired] string Id, [property: JsonRequired] string SourceRoot,
    [property: JsonRequired] string Note, [property: JsonRequired] bool Pinned, bool RestoreProtection = false, long Number = 0);

internal static class HistoryStore
{
    internal const string StateName = "history.json";
    private const int LegacyStateLimit = 8 * 1024 * 1024;
    private const int StateLimit = 2 * LegacyStateLimit;
    // A pending cleanup persists up to one extra copy of every receipt ID before it can remove any rows.
    private const int CleanupStateLimit = 2 * StateLimit;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static FileStream OpenMetadata(string root)
    {
        var directory = HistoryPaths.HistoryDirectory(root);
        HistoryPaths.RequireDirectory(directory);
        var file = Path.Combine(directory, StateName);
        HistoryPaths.CheckPath(file);
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    internal static void RequireIdentity(string root, Guid expected)
    {
        using var input = OpenMetadata(root);
        if (input.Length > CleanupStateLimit) throw new InvalidDataException("历史库元数据过大，已停止读取。");
        // The ID is near the start in our format. Reordered JSON falls back to the full checked reader.
        Span<byte> prefix = stackalloc byte[256];
        var length = input.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        var bytes = prefix[..length];
        var reader = new Utf8JsonReader(bytes.StartsWith(System.Text.Encoding.UTF8.Preamble) ? bytes[3..] : bytes,
            isFinalBlock: false, default);
        while (reader.Read())
        {
            if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("id"u8)) continue;
            if (reader.Read() && reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var id))
            {
                if (id == expected) return;
                throw new InvalidDataException("此位置的历史库与已关联的文件夹不同。");
            }
            break;
        }
        if (Read(root).Id != expected) throw new InvalidDataException("此位置的历史库与已关联的文件夹不同。");
    }

    internal static RepositoryState Read(string root)
    {
        using var input = OpenMetadata(root);
        if (input.Length > CleanupStateLimit) throw new InvalidDataException("历史库元数据过大，已停止读取。");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var state = document.Deserialize<RepositoryState>(Json) ?? throw new InvalidDataException("历史库元数据为空。");
        if (input.Length > Limit(state)) throw new InvalidDataException("历史库元数据过大，已停止读取。");
        if (state.SchemaVersion is not (1 or 2 or 3) || state.Id == Guid.Empty || state.Engine != "Duplicati" || state.Versions is null ||
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
        if (state.PendingDeleteIds is null || (state.PendingDeleteIds.Count > 0 &&
            state.PendingDeleteIds.Except(state.Versions.Select(version => version.Id), StringComparer.Ordinal).Any()) ||
            state.PendingDeleteIds.Distinct(StringComparer.Ordinal).Count() != state.PendingDeleteIds.Count ||
            state.PendingDeleteAll && (state.PendingDeleteIds.Count == 0 || state.PendingDeleteIds.Count != state.Versions.Count))
            throw new InvalidDataException("历史清理记录损坏，已停止写入和清理。");
        if (state.SchemaVersion < 3)
        {
            var metadata = document.RootElement;
            if (metadata.TryGetProperty("lastSnapshotNumber", out _) || state.LastSnapshotNumber != 0 || state.Versions.Any(v => v.Number != 0))
                throw new InvalidDataException("旧格式库包含快照编号，不能降级历史库。");
            // Receipts retain insertion order after cleanup. Persist their numbers on the next normal write.
            for (var i = 0; i < state.Versions.Count; i++)
                state.Versions[i] = state.Versions[i] with { Number = i + 1L };
            state.LastSnapshotNumber = state.Versions.Count;
        }
        else
        {
            RequireProperties(document.RootElement, "lastSnapshotNumber");
            long previous = 0;
            foreach (var version in state.Versions)
            {
                if (version.Number <= previous) throw new InvalidDataException("快照编号缺失、重复或顺序错误。");
                previous = version.Number;
            }
            if (state.LastSnapshotNumber < previous) throw new InvalidDataException("历史库的快照编号计数无效。");
        }
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
        state.SchemaVersion = 3;
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
            throw new InvalidDataException("历史库必需字段缺失，已停止写入和清理。");
    }

    internal static FileStream Lock(string root)
    {
        var path = Path.Combine(HistoryPaths.HistoryDirectory(root), "operation.lock");
        HistoryPaths.CheckPath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static int Limit(RepositoryState state) => state.SchemaVersion < 3 ? LegacyStateLimit :
        state.PendingDeleteIds is { Count: > 0 } ? CleanupStateLimit : StateLimit;

    private static void WriteFile(string path, RepositoryState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        if (bytes.Length > Limit(state)) throw new InvalidDataException("历史库元数据已达到本版容量上限，未覆盖原记录。");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }
}
