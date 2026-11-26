using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace TractorTool.Core.Packages.Node;

/// <summary>Limits installation inputs; pnpm remains responsible for resolving and verifying the locked graph.</summary>
internal static partial class NodeInputPolicy
{
    internal static string Validate(NodeEnvironmentDefinition definition)
    {
        var versions = new List<string>(); var ranges = new List<string>(); var requirements = new List<string[]>();
        using var project = JsonDocument.Parse(ParsingText(definition.ProjectText), new JsonDocumentOptions { MaxDepth = 48 });
        var metadata = project.RootElement;
        JsonKeys(metadata, "name", "version", "private", "description", "type", "license", "packageManager", "engines", "dependencies", "optionalDependencies");
        PackageName(JsonText(metadata.GetProperty("name")));
        Version(JsonText(metadata.GetProperty("version")));
        versions.Add(JsonText(metadata.GetProperty("version")));
        if (metadata.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind != JsonValueKind.True) Fail();
        if (metadata.TryGetProperty("type", out var type) && JsonText(type) is not ("module" or "commonjs")) Fail();
        if (metadata.TryGetProperty("packageManager", out var manager) && JsonText(manager) != "pnpm@" + definition.PnpmVersion) Fail();
        if (metadata.TryGetProperty("engines", out var engines))
        {
            JsonKeys(engines, "node", "pnpm");
            foreach (var engine in engines.EnumerateObject()) { Range(JsonText(engine.Value)); ranges.Add(JsonText(engine.Value)); }
        }
        foreach (var group in new[] { "dependencies", "optionalDependencies" })
            if (metadata.TryGetProperty(group, out var dependencies))
            {
                if (dependencies.ValueKind != JsonValueKind.Object) Fail();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (!names.Add(dependency.Name) || names.Count > 256) Fail();
                    PackageName(dependency.Name); Range(JsonText(dependency.Value));
                }
            }

        var root = ParseLock(ParsingText(definition.LockText));
        Keys(root, "lockfileVersion", "settings", "importers", "packages", "snapshots");
        if (Text(Get(root, "lockfileVersion")) != "9.0") Fail();
        var settings = Map(Get(root, "settings")); Keys(settings, "autoInstallPeers", "excludeLinksFromLockfile");
        if (!Boolean(Get(settings, "autoInstallPeers")) || Boolean(Get(settings, "excludeLinksFromLockfile"))) Fail();
        var importers = Map(Get(root, "importers")); Keys(importers, ".");
        var importer = Map(Get(importers, ".")); Keys(importer, "dependencies", "optionalDependencies");
        foreach (var group in new[] { "dependencies", "optionalDependencies" })
        {
            var declaredRequirements = metadata.TryGetProperty(group, out var declared)
                ? declared.EnumerateObject().ToDictionary(p => p.Name, p => JsonText(p.Value), StringComparer.Ordinal) : [];
            var resolved = TryGet(importer, group, out var section) ? Map(section) : new YamlMappingNode();
            if (resolved.Children.Count != declaredRequirements.Count) Fail();
            foreach (var pair in resolved.Children)
            {
                var name = Text(pair.Key); PackageName(name);
                var item = Map(pair.Value); Keys(item, "specifier", "version");
                if (!declaredRequirements.TryGetValue(name, out var requirement) || Text(Get(item, "specifier")) != requirement) Fail();
                Reference(name, Text(Get(item, "version")));
                ranges.Add(requirement!);
                requirements.Add([requirement!, Text(Get(item, "version")).Split('(')[0]]);
            }
        }
        var packages = TryGet(root, "packages", out var packageNode) ? Map(packageNode) : new YamlMappingNode();
        var snapshots = TryGet(root, "snapshots", out var snapshotNode) ? Map(snapshotNode) : new YamlMappingNode();
        if (packages.Children.Count > 4096 || snapshots.Children.Count > 8192) Fail();
        foreach (var pair in packages.Children)
        {
            var key = Text(pair.Key); PackageKey(key, allowPeers: false);
            versions.Add(key[(key.LastIndexOf('@') + 1)..]);
            var item = Map(pair.Value);
            Keys(item, "resolution", "engines", "os", "cpu", "libc", "hasBin", "deprecated", "peerDependencies", "peerDependenciesMeta");
            var resolution = Map(Get(item, "resolution")); Keys(resolution, "integrity");
            Integrity(Text(Get(resolution, "integrity")));
            if (TryGet(item, "peerDependencies", out var peers))
                foreach (var peer in Map(peers).Children) { PackageName(Text(peer.Key)); Range(Text(peer.Value)); ranges.Add(Text(peer.Value)); }
        }
        foreach (var pair in snapshots.Children)
        {
            var key = Text(pair.Key); PackageKey(key, allowPeers: true);
            if (!TryGet(packages, key.Split('(')[0], out _)) Fail();
            var item = Map(pair.Value); Keys(item, "dependencies", "optionalDependencies", "transitivePeerDependencies", "optional");
            if (TryGet(item, "optional", out var optional)) _ = Boolean(optional);
            foreach (var group in new[] { "dependencies", "optionalDependencies" })
                if (TryGet(item, group, out var dependencies))
                    foreach (var dependency in Map(dependencies).Children)
                    {
                        var name = Text(dependency.Key); var version = Text(dependency.Value); Reference(name, version);
                        if (!TryGet(snapshots, name + "@" + version, out _)) Fail();
                    }
        }
        foreach (var section in importer.Children)
            foreach (var pair in Map(section.Value).Children)
                if (!TryGet(snapshots, Text(pair.Key) + "@" + Text(Get(Map(pair.Value), "version")), out _)) Fail();
        // pnpm may skip failed optional downloads. A corrupted lock must not downgrade a required dependency.
        var required = new Queue<string>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        if (TryGet(importer, "dependencies", out var direct))
            foreach (var pair in Map(direct).Children) required.Enqueue(Text(pair.Key) + "@" + Text(Get(Map(pair.Value), "version")));
        while (required.TryDequeue(out var key))
        {
            if (!visited.Add(key)) continue;
            var item = Map(Get(snapshots, key));
            if (TryGet(item, "optional", out var optional) && Boolean(optional)) Fail();
            if (TryGet(item, "dependencies", out var dependencies))
                foreach (var pair in Map(dependencies).Children) required.Enqueue(Text(pair.Key) + "@" + Text(pair.Value));
        }
        return JsonSerializer.Serialize(new { versions, ranges, requirements });
    }

    private static YamlMappingNode ParseLock(string text)
    {
        try
        {
            var parser = new Parser(new StringReader(text));
            var depth = 0; var count = 0; var documents = 0;
            // Reject YAML indirection before constructing a graph, keeping memory and recursion bounded.
            while (parser.MoveNext())
            {
                if (++count > 200000) Fail();
                if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)) Fail();
                if (parser.Current is MappingStart or SequenceStart) { if (++depth > 48) Fail(); }
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
                if (parser.Current is DocumentStart && ++documents > 1) Fail();
            }
            var stream = new YamlStream(); stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1) Fail();
            return Map(stream.Documents[0].RootNode);
        }
        catch (YamlException ex) { throw new InvalidDataException("pnpm 锁文件不是有效的单一 YAML 文档。", ex); }
    }

    private static void PackageName(string value)
    {
        if (value.Length > 214 || !NamePattern().IsMatch(value) || value.Split('/').Any(p => p is "." or "..")) Fail();
    }
    private static void Version(string value) { if (value.Length > 128 || !VersionPattern().IsMatch(value)) Fail(); }
    private static void Range(string value)
    {
        // This is only a source/tag filter. The pinned Node distribution's node-semver validates exact npm semantics.
        if (value.Length is 0 or > 512 || !(char.IsAsciiDigit(value[0]) || "~^<>=*xX".Contains(value[0])) ||
            !Regex.IsMatch(value, "^[0-9A-Za-z.*+~^<>=| -]+$", RegexOptions.CultureInvariant)) Fail();
    }
    private static void Reference(string name, string version) { PackageName(name); PackageKey(name + "@" + version, allowPeers: true); }
    private static void PackageKey(string key, bool allowPeers)
    {
        if (key.Length > 4096) Fail();
        var parenthesis = key.IndexOf('(');
        var primary = parenthesis < 0 ? key : key[..parenthesis];
        var at = primary.LastIndexOf('@');
        if (at <= 0 || (!allowPeers && parenthesis >= 0)) Fail();
        PackageName(primary[..at]); Version(primary[(at + 1)..]);
        if (parenthesis >= 0)
        {
            var depth = 0;
            foreach (var character in key[parenthesis..])
            {
                if (character == '(') { if (++depth > 32) Fail(); }
                else if (character == ')') { if (--depth < 0) Fail(); }
                else if (!char.IsAsciiLetterOrDigit(character) && character is not ('@' or '/' or '.' or '_' or '-' or '+')) Fail();
            }
            if (depth != 0 || key[^1] != ')') Fail();
        }
    }
    private static void Integrity(string value)
    {
        var parts = value.Split('-', 2);
        if (parts.Length != 2 || parts[0] is not ("sha256" or "sha512")) Fail();
        try
        {
            var bytes = Convert.FromBase64String(parts[1]);
            if (bytes.Length != (parts[0] == "sha256" ? 32 : 64) || Convert.ToBase64String(bytes) != parts[1]) Fail();
        }
        catch (FormatException ex) { throw new InvalidDataException("npm 锁文件的完整性摘要无效。", ex); }
    }
    private static void JsonKeys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || !allowed.Contains(property.Name)) Fail();
    }
    private static string JsonText(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : throw Invalid();
    private static string ParsingText(string text) => text.StartsWith('\uFEFF') ? text[1..] : text;
    private static YamlMappingNode Map(YamlNode value) => value as YamlMappingNode ?? throw Invalid();
    private static string Text(YamlNode value) => value is YamlScalarNode { Value: { } text } ? text : throw Invalid();
    private static bool Boolean(YamlNode value) => value is YamlScalarNode { Style: ScalarStyle.Plain, Value: "true" or "false" } scalar
        ? scalar.Value == "true" : throw Invalid();
    private static void Keys(YamlMappingNode value, params string[] allowed)
    { foreach (var pair in value.Children) if (!allowed.Contains(Text(pair.Key), StringComparer.Ordinal)) Fail(); }
    private static bool TryGet(YamlMappingNode value, string key, out YamlNode node) => value.Children.TryGetValue(new YamlScalarNode(key), out node!);
    private static YamlNode Get(YamlMappingNode value, string key) => TryGet(value, key, out var node) ? node : throw Invalid();
    private static InvalidDataException Invalid() => new("Node 锁定资料超出支持范围：只接受单项目、普通 registry 包及完整性摘要；不支持脚本、workspace、本地/Git/直接 URL、patch 或配置钩子。");
    private static void Fail() => throw Invalid();
    [GeneratedRegex("^(?:@[a-z0-9._-]+/)?[a-z0-9._-]+$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)] private static partial Regex VersionPattern();
}
