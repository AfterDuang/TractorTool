using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
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
    public string Address { get; init; } = "";
    public string JsonPointer { get; init; } = "";
    public double SamplingSeconds { get; init; } = 1;
    // Legacy network-only interval; removed from JSON after plugin-local normalization.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RefreshSeconds { get; init; }
    public bool JsonPercentage { get; init; }
    public int ProbeProtocol { get; init; }
    public int Port { get; init; } = 443;
    public string SensorId { get; init; } = "";
    public string Currency { get; init; } = "CNY";
    public string Drive { get; init; } = "C:\\";
    [JsonIgnore] public bool IsNetwork => Metric is 5 or 6 or 7 or 9;
    public static double DefaultRefreshSeconds(int metric) => metric == 9 ? 60 : metric is 5 or 6 or 7 ? 30 : 1;

    internal static readonly Regex Token = new(@"\{(label|value|detail|percent|name|unit|updated)\}", RegexOptions.CultureInvariant);
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
        : Metric is < 0 or > 11 ? "请选择支持的指标。"
        : Metric is >= 5 and <= 7 && !NetworkMetrics.ValidAddress(Address, Metric == 5)
            ? Metric == 5 ? "请填写主机名或 IP，不包含协议、端口或路径。" : "请填写完整 HTTP/HTTPS 地址，不包含账号、密码或片段。"
        : !double.IsFinite(SamplingSeconds) || SamplingSeconds is < .2 or > 600 ? "采样间隔为 0.2–600 秒（每秒最多 5 次，最长 10 分钟一次）。"
        : Metric == 7 && !NetworkMetrics.ValidPointer(JsonPointer) ? "JSON 字段路径应留空或以 / 开头；使用 ~0 表示 ~，~1 表示 /。"
        : Metric == 5 && (ProbeProtocol is < 0 or > 1 || Port is < 1 or > 65535) ? "请选择 ICMP/TCP 协议及有效端口（1–65535）。"
        : Metric == 8 && (string.IsNullOrWhiteSpace(SensorId) || SensorId.Length > 512) ? "请检测并选择硬件传感器。"
        : Metric == 9 && Currency is not ("CNY" or "USD") ? "请选择 CNY/USD。"
        : Metric == 10 && (Drive is not { Length: 3 } || !char.IsAsciiLetter(Drive[0]) || Drive[1] != ':' || Drive[2] != '\\') ? "请填写本地盘符，例如 C:\\。"
        : !ValidTemplate(Title, 60) || !ValidTemplate(Value, 80) || !ValidTemplate(Detail, 160)
            ? "模板仅支持列出的变量；标题/数值/说明上限为 60/80/160 个字符，不支持换行。"
        : !ValidColor(Color) || !ValidColor(RuleColor) ? "颜色请填写 #RRGGBB，例如 #FFE66B。"
        : Comparison is < 0 or > 1 || !double.IsFinite(Threshold) || Threshold is < 0 or > 100
            ? "百分比阈值必须在 0–100 之间。" : "";

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
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的方案文件版本；原文件保留。");
        var profiles = Profiles is { Length: 0 }
            ? [new HudProfile { Metric = legacyMetric, SamplingSeconds = HudProfile.DefaultRefreshSeconds(legacyMetric) }] : Profiles;
        if (profiles is not null)
            profiles = profiles.Select(p => p?.RefreshSeconds is { } legacy ? p with
            {
                SamplingSeconds = p.IsNetwork && double.IsFinite(legacy) ? Math.Min(legacy, 600) : p.IsNetwork ? legacy : 1,
                RefreshSeconds = null
            } : p!).ToArray();
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
