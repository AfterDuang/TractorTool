using System.Text;
using TractorTool.Contracts;
using TractorTool.Contracts.Processes;

namespace TractorTool.Core.Packages.Environments;

internal interface IEnvironmentDefinition
{
    string Identity { get; }
    string PluginId { get; }
    string PluginVersion { get; }
    string PluginName { get; }
    string Id { get; }
    string Rid { get; }
    string Source { get; }
    string Description { get; }
}

internal static class EnvironmentPolicy
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

// Only the language-specific boundary varies; storage and lifetime rules stay shared.
internal abstract class EnvironmentPolicy<TDefinition> where TDefinition : class, IEnvironmentDefinition
{
    internal abstract string Name { get; }
    internal abstract string Folder { get; }
    internal abstract string OwnerPrefix { get; }
    internal string RootMarker => "." + OwnerPrefix.ToLowerInvariant() + "-packages";
    internal string RootOwner => "TractorTool." + OwnerPrefix + "Packages.v1";
    internal string WorkMarker => "." + OwnerPrefix.ToLowerInvariant() + "-work";
    internal string WorkOwner => "TractorTool." + OwnerPrefix + "Work.v1";
    internal string CacheMarker => "." + OwnerPrefix.ToLowerInvariant() + "-cache";
    internal string CacheOwner => "TractorTool." + OwnerPrefix + "Cache.v1";
    internal string EnvironmentOwner => "TractorTool." + OwnerPrefix + "Environment.v1\n";
    internal abstract void Validate(TDefinition definition);
    internal abstract string RuntimePath(string generation, TDefinition definition);
    internal virtual string RuntimeDirectory(string generation) => Path.Combine(generation, "environment");
    internal abstract ProcessRequest PrepareRequest(ProcessRequest request);
    internal virtual void PrepareGenerationRemoval(string generation, TDefinition definition) { }
    internal virtual bool CacheCleanerNeedsRuntime => false;
    internal virtual Task CleanCacheAsync(string cache, string generation, IProcessRunner processes, IAppLog log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PackageFiles.DeleteOwnedTree(Path.GetDirectoryName(cache)!, cache, CacheMarker);
        return Task.CompletedTask;
    }
}
