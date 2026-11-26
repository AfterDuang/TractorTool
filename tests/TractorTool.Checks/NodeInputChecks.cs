using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TractorTool.Core.Packages.Node;

internal static class NodeInputChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var project = """{"name":"probe","version":"1.0.0","private":true,"dependencies":{"is-number":"7.0.0"}}""";
        var locked = """
            lockfileVersion: '9.0'
            settings:
              autoInstallPeers: true
              excludeLinksFromLockfile: false
            importers:
              .:
                dependencies:
                  is-number:
                    specifier: 7.0.0
                    version: 7.0.0
            packages:
              is-number@7.0.0:
                resolution: {integrity: sha512-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==}
                engines: {node: '>=0.12.0'}
            snapshots:
              is-number@7.0.0: {}
            """;
        var definition = new NodeEnvironmentDefinition("", "Test.Node", "1.0.0", "Node probe", "worker",
            NodeEnvironmentDefinitions.SupportedNodeVersion, "win-x64", NodeEnvironmentDefinitions.NpmUrl,
            "Input-only checks", project, locked, NodeEnvironmentDefinitions.PnpmVersion);
        check("Node input: ordinary locked registry package accepted", () => Validate(definition));
        check("Node input: UTF-8 BOM preserved in snapshots but accepted by parsers", () =>
            Validate(definition with { ProjectText = "\uFEFF" + project, LockText = "\uFEFF" + locked }));
        check("Node input: source overrides, hooks, aliases and graph holes rejected", () =>
        {
            foreach (var candidate in new[]
            {
                definition with { ProjectText = project.Replace("\"private\":true", "\"scripts\":{\"postinstall\":\"echo unexpected\"}") },
                definition with { ProjectText = project.Replace("\"private\":true", "\"pnpm\":{\"patchedDependencies\":{}}") },
                definition with { ProjectText = project.Replace("\"private\":true", "\"name\":\"duplicate\"") },
                definition with { ProjectText = project.Replace("\"7.0.0\"", "\"npm:other@7.0.0\"") },
                definition with { ProjectText = project.Replace("\"7.0.0\"", "\"beta2\"") },
                definition with { ProjectText = project.Replace("\"7.0.0\"", "\"https://example.com/package.tgz\"") },
                definition with { LockText = locked + "\npnpmfileChecksum: malicious\n" },
                definition with { LockText = locked.Replace("{integrity:", "{tarball: 'https://other.example/package.tgz', integrity:") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: {dependencies: {missing: 1.0.0}}") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: {optional: true}") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: {optional: 'invalid'}") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: {optional: 'false'}") },
                definition with { LockText = locked.Replace("specifier: 7.0.0", "specifier: 6.0.0") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: &alias {}") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: !!map {}") },
                definition with { LockText = locked + "\n---\nlockfileVersion: '9.0'\n" },
                definition with { LockText = locked.Replace("autoInstallPeers: true", "autoInstallPeers: true\n  autoInstallPeers: false") },
                definition with { LockText = locked.Replace("sha512-", "sha1-") },
                definition with { LockText = locked.Replace("is-number@7.0.0: {}", "is-number@7.0.0: {dependencies: {is-number: 'file:../escape'}}") }
            }) Throws<InvalidDataException>(() => Validate(candidate));
        });
        check("Node input: scoped packages remain valid", () => Validate(definition with
        {
            ProjectText = project.Replace("is-number", "@example/package"),
            LockText = locked.Replace("is-number", "@example/package")
                .Replace("@example/package:", "'@example/package':")
                .Replace("@example/package@7.0.0:", "'@example/package@7.0.0':")
        }));
        check("Node input: oversized dependency map rejected before graph validation", () =>
        {
            var dependencies = string.Join(',', Enumerable.Range(0, 257).Select(i => $"\"package-{i}\":\"1.0.0\""));
            Throws<InvalidDataException>(() => Validate(definition with { ProjectText =
                "{\"name\":\"probe\",\"version\":\"1.0.0\",\"dependencies\":{" + dependencies + "}}" }));
        });
        check("Node archive: tar traversal and links cannot write outside destination", () =>
        {
            var workspace = Path.Combine(root, "node-tar-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
            foreach (var item in new[] { (TarEntryType.RegularFile, "../escape.txt"), (TarEntryType.SymbolicLink, "package/link"),
                (TarEntryType.HardLink, "package/hard"), (TarEntryType.RegularFile, "package/../escape.txt") })
            {
                var archive = Path.Combine(workspace, Guid.NewGuid().ToString("N") + ".tgz");
                using (var output = File.Create(archive))
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                using (var writer = new TarWriter(gzip))
                {
                    var entry = new PaxTarEntry(item.Item1, item.Item2);
                    if (item.Item1 is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "../escape.txt";
                    else entry.DataStream = new MemoryStream([1, 2, 3]);
                    writer.WriteEntry(entry);
                }
                Throws<InvalidDataException>(() => ((Task)Invoke("NodeEnvironmentInstaller", "ExtractPnpmAsync",
                    archive, Path.Combine(workspace, "extracted"), CancellationToken.None)!).GetAwaiter().GetResult());
                if (File.Exists(Path.Combine(workspace, "escape.txt"))) throw new Exception("Archive escaped its destination.");
            }
        });
    }

    private static void Validate(NodeEnvironmentDefinition definition) => Invoke("NodeInputPolicy", "Validate", definition);
    private static object? Invoke(string typeName, string method, params object[] args)
    {
        var type = typeof(NodeEnvironmentDefinitions).Assembly.GetType("TractorTool.Core.Packages.Node." + typeName, throwOnError: true)!;
        try { return type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
