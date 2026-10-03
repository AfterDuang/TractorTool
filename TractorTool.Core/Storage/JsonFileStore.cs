using System.Collections.Concurrent;
using System.Text.Json;
using TractorTool.Contracts;

namespace TractorTool.Core.Storage;

public sealed class JsonFileStore<T>(string path, Action<T>? validate = null)
    where T : IVersionedSettings, new()
{
    public string FilePath { get; } = Path.GetFullPath(path);

    public T Load()
    {
        lock (FileLocks.For(FilePath))
        {
            var value = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), JsonOptions.Value)
                    ?? throw new InvalidDataException($"配置为空：{FilePath}")
                : new T();
            Check(value);
            return value;
        }
    }

    public void Save(T value)
    {
        Check(value);
        lock (FileLocks.For(FilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, JsonOptions.Value);
                    stream.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
                else File.Move(temp, FilePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private void Check(T value)
    {
        if (value.SchemaVersion != new T().SchemaVersion)
            throw new InvalidDataException($"不支持的配置版本 {value.SchemaVersion}：{FilePath}；原文件已保留。");
        validate?.Invoke(value);
    }
}

internal static class FileLocks
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static object For(string path) => Locks.GetOrAdd(path, _ => new object());
}

public static class JsonOptions
{
    public static JsonSerializerOptions Value { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
