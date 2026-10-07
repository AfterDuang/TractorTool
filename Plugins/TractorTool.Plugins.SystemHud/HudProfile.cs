using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TractorTool.Contracts;

namespace TractorTool.Plugins.SystemHud;

public sealed record HudProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "默认方案";
    public int Metric { get; init; }
    public string Title { get; init; } = "{label}";
    public string Value { get; init; } = "{value}";
    public string Detail { get; init; } = "{detail}";
    public string Color { get; init; } = "#FFE66B";
    public bool RuleEnabled { get; init; }
    public int Comparison { get; init; }
    public double Threshold { get; init; } = 20;
    public string RuleColor { get; init; } = "#FF8A90";

    private static readonly Regex Token = new(@"\{(label|value|detail|percent|name)\}", RegexOptions.CultureInvariant);
    public static bool ValidColor(string? text) => text is { Length: 7 } && text[0] == '#'
        && text.Skip(1).All(Uri.IsHexDigit);
    public static bool ValidTemplate(string? text, int limit)
    {
        if (text is null || text.Length > limit || text.Contains('\n') || text.Contains('\r')) return false;
        var remaining = Token.Replace(text, "");
        return !remaining.Contains('{') && !remaining.Contains('}');
    }
    [JsonIgnore] public string Error => Id == Guid.Empty ? "方案标识无效。"
        : string.IsNullOrWhiteSpace(Name) || Name.Length > 40 ? "方案名称需要 1–40 个字符。"
        : Metric is < 0 or > 4 ? "请选择支持的指标。"
        : !ValidTemplate(Title, 60) || !ValidTemplate(Value, 80) || !ValidTemplate(Detail, 160)
            ? "模板仅支持列出的变量；标题/数值/说明上限为 60/80/160 个字符，不支持换行。"
        : !ValidColor(Color) || !ValidColor(RuleColor) ? "颜色请填写 #RRGGBB，例如 #FFE66B。"
        : Comparison is < 0 or > 1 || !double.IsFinite(Threshold) || Threshold is < 0 or > 100
            ? "百分比阈值必须在 0–100 之间。" : "";

    public HudDisplay Render(HudReading reading)
    {
        string Expand(string template) => Token.Replace(template, m => m.Groups[1].Value switch
        {
            "label" => reading.Label, "value" => reading.Value, "detail" => reading.Detail,
            "name" => Name, "percent" => reading.Percent?.ToString("0.#", CultureInfo.CurrentCulture) ?? "—", _ => ""
        });
        // ponytail: one percentage rule per profile; add a rule list only when real layouts need it.
        var match = RuleEnabled && reading.Percent is { } p && (Comparison == 0 ? p <= Threshold : p >= Threshold);
        return new(Expand(Title), Expand(Value), Expand(Detail), reading.Percent,
            new ImmutableSolidColorBrush(Avalonia.Media.Color.Parse(match ? RuleColor : Color)));
    }
}

public sealed record HudDisplay(string Title, string Value, string Detail, double? Percent, IBrush Accent)
{
    public bool HasPercent => Percent.HasValue;
    public double Progress => Percent ?? 0;
}

public sealed record HudProfiles : IVersionedSettings
{
    public int SchemaVersion { get; init; } = 1;
    public HudProfile[] Profiles { get; init; } = [];
    public Guid ActiveId { get; init; }
    public bool Rotate { get; init; }
    public int Seconds { get; init; } = 10;

    public HudProfiles Normalize(int legacyMetric)
    {
        var profiles = Profiles is { Length: 0 } ? [new HudProfile { Metric = legacyMetric }] : Profiles;
        if (profiles is null || profiles.Length > 32 || profiles.Any(p => p is null || p.Error.Length != 0)
            || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length)
            throw new InvalidDataException("方案文件包含无效、重复或过多的方案；原文件保留。");
        return this with { Profiles = profiles, ActiveId = profiles.Any(p => p.Id == ActiveId) ? ActiveId : profiles[0].Id,
            Seconds = Math.Clamp(Seconds, 3, 300) };
    }

    [JsonIgnore] public HudProfile Active => Profiles.First(p => p.Id == ActiveId);
    public HudProfile Next(Guid current)
    {
        var index = Array.FindIndex(Profiles, p => p.Id == current);
        return Profiles[(index + 1) % Profiles.Length];
    }
}
