using System.Text.Json;
using System.Text.Json.Serialization;
using TractorTool.Contracts;

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
    public int SchemaVersion { get; set; } = 1;
    [JsonRequired]
    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired]
    public string Engine { get; set; } = "Duplicati";
    [JsonRequired]
    public List<VersionReceipt> Versions { get; set; } = [];
}

internal sealed record VersionReceipt([property: JsonRequired] string Id, [property: JsonRequired] string SourceRoot,
    [property: JsonRequired] string Note, [property: JsonRequired] bool Pinned);

internal static class HistoryStore
{
    private const string StateName = "history.json";
    private const int StateLimit = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, AllowDuplicateProperties = false };

    internal static RepositoryState Read(string root)
    {
        var directory = HistoryPaths.HistoryDirectory(root);
        HistoryPaths.RequireDirectory(directory);
        var file = Path.Combine(directory, StateName);
        HistoryPaths.CheckPath(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > StateLimit) throw new InvalidDataException("历史库元数据过大，已停止读取。");
        var state = JsonSerializer.Deserialize<RepositoryState>(input, Json) ?? throw new InvalidDataException("历史库元数据为空。");
        if (state.SchemaVersion != 1 || state.Id == Guid.Empty || state.Engine != "Duplicati" || state.Versions is null ||
            state.Versions.Any(v => v is null || string.IsNullOrWhiteSpace(v.Id) || Path.GetFileName(v.Id) != v.Id ||
                string.IsNullOrWhiteSpace(v.SourceRoot) || !Path.IsPathFullyQualified(v.SourceRoot) || v.Note is null) ||
            state.Versions.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Versions.Count)
            throw new InvalidDataException("历史库格式不兼容或元数据损坏；未覆盖原内容。");
        return state;
    }

    internal static RepositoryState Create(string root)
    {
        var destination = HistoryPaths.HistoryDirectory(root);
        HistoryPaths.CheckPath(destination);
        if (HistoryPaths.Attributes(destination) is not null)
            throw new IOException("历史目录名称已被占用，请先检查原内容。");
        var temporary = Path.Combine(root, ".tractortool-history-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var state = new RepositoryState();
            WriteFile(Path.Combine(temporary, StateName), state);
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
