using System.Text.Json;

namespace TractorTool.Core.Packages.Python;

/// <summary>Checks the supported subset before uv is allowed to consume the snapshots.</summary>
internal static class PythonInputPolicy
{
    // The interpreter and this script are host-controlled. No plugin Python code or site hooks execute here.
    internal const string ParseScript = "import json,pathlib,sys,tomllib; print(json.dumps([tomllib.loads(pathlib.Path(p).read_text(encoding='utf-8')) for p in sys.argv[1:]],ensure_ascii=True))";

    internal static void Validate(string parsed, PythonEnvironmentDefinition definition)
    {
        using var document = JsonDocument.Parse(parsed, new JsonDocumentOptions { MaxDepth = 48 });
        var inputs = document.RootElement;
        if (inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() != 2) Fail();
        var project = inputs[0]; var locked = inputs[1];
        RequireKeys(project, "project", "tool");
        if (project.TryGetProperty("tool", out var tool))
        {
            RequireKeys(tool, "uv");
            var uv = tool.GetProperty("uv"); RequireKeys(uv, "package");
            if (uv.GetProperty("package").ValueKind != JsonValueKind.False) Fail();
        }
        var metadata = project.GetProperty("project");
        RequireKeys(metadata, "name", "version", "requires-python", "dependencies", "description");
        var projectName = Text(metadata, "name");
        _ = Text(metadata, "version"); _ = Text(metadata, "requires-python");
        if (metadata.TryGetProperty("dependencies", out var requirements))
        {
            if (requirements.ValueKind != JsonValueKind.Array || requirements.GetArrayLength() > 256) Fail();
            foreach (var requirement in requirements.EnumerateArray())
                if (requirement.ValueKind != JsonValueKind.String || requirement.GetString() is not { Length: > 0 and <= 2048 } text ||
                    text.IndexOfAny(['@', '/', '\\']) >= 0) Fail();
        }
        RequireKeys(locked, "version", "revision", "requires-python", "resolution-markers", "supported-markers", "required-markers", "package");
        if (locked.GetProperty("version").GetInt32() != 1) Fail();
        var packages = locked.GetProperty("package");
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() is < 1 or > 2048) Fail();
        var roots = 0;
        foreach (var package in packages.EnumerateArray())
        {
            RequireKeys(package, "name", "version", "source", "dependencies", "sdist", "wheels", "metadata", "resolution-markers");
            var source = package.GetProperty("source");
            if (source.TryGetProperty("virtual", out var local))
            {
                RequireKeys(source, "virtual");
                if (local.GetString() != "." || Normalize(Text(package, "name")) != Normalize(projectName)) Fail();
                roots++;
            }
            else
            {
                RequireKeys(source, "registry");
                if (PythonEnvironmentDefinitions.IndexUri(Text(source, "registry")).AbsoluteUri != definition.Source) Fail();
                if (!package.TryGetProperty("wheels", out var wheels) || wheels.ValueKind != JsonValueKind.Array || wheels.GetArrayLength() == 0) Fail();
                foreach (var wheel in wheels.EnumerateArray()) ValidateArtifact(wheel, definition.Source, wheel: true);
                if (package.TryGetProperty("sdist", out var sdist)) ValidateArtifact(sdist, definition.Source, wheel: false);
            }
        }
        if (roots != 1) Fail();
    }

    private static void ValidateArtifact(JsonElement value, string index, bool wheel)
    {
        RequireKeys(value, "url", "hash", "size", "upload-time");
        if (!Uri.TryCreate(Text(value, "url"), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            (wheel && !uri.AbsolutePath.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))) Fail();
        var expected = new Uri(index);
        if (uri!.Host != expected.Host && !(index == PythonEnvironmentDefinitions.PyPiUrl && uri.Host == "files.pythonhosted.org")) Fail();
        var hash = Text(value, "hash");
        if (!hash.StartsWith("sha256:", StringComparison.Ordinal) || !PythonEnvironmentDefinitions.IsHash(hash[7..])) Fail();
        if (value.TryGetProperty("size", out var size) && (!size.TryGetInt64(out var length) || length is <= 0 or > 1024L * 1024 * 1024)) Fail();
    }

    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var item) &&
        item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text ? text : throw new InvalidDataException("Python 项目或锁文件字段无效。");
    private static string Normalize(string value) => value.ToLowerInvariant().Replace('_', '-').Replace('.', '-');
    private static void RequireKeys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) Fail();
    }
    private static void Fail() => throw new InvalidDataException("Python 锁定资料超出支持范围：仅允许普通项目、单个已配置索引及带 SHA256 的预编译 wheel；不接受本地/Git/直接地址依赖、构建配置或安装钩子。");
}
