namespace TractorTool.Core.Packages;

/// <summary>Native loaders can retain handles after a lease, plugin or service is disposed.</summary>
internal static class NativePackageRetention
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<string>> Owners = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static void Retain(string directory, string owner)
    {
        lock (Gate)
        {
            if (!Owners.TryGetValue(directory, out var owners)) Owners.Add(directory, owners = []);
            owners.Add(owner);
        }
    }

    public static bool IsRetained(string directory) { lock (Gate) return Owners.ContainsKey(directory); }
    public static IReadOnlyList<string> GetOwners(string directory)
    {
        lock (Gate) return Owners.TryGetValue(directory, out var owners)
            ? Array.AsReadOnly(owners.Order(StringComparer.Ordinal).ToArray()) : Array.Empty<string>();
    }
}
