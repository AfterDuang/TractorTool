using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TractorTool.Plugins.SystemHud;

// Only the visible profile is requested. This is not a background uptime monitor.
internal sealed class NetworkMetrics : IDisposable
{
    internal const int MaxBytes = 64 * 1024;
    private readonly HttpClient _client;

    internal NetworkMetrics(HttpMessageHandler? handler = null)
    {
        _client = new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxResponseHeadersLength = 16
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static bool ValidAddress(string? address, bool ping)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048 || address.Any(char.IsWhiteSpace)) return false;
        if (ping) return IPAddress.TryParse(address, out _) ||
            address.Length <= 253 && Uri.CheckHostName(address) == UriHostNameType.Dns;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }

    internal static bool ValidPointer(string? pointer)
    {
        if (pointer is null || pointer.Length > 256 || pointer.Any(char.IsControl)) return false;
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;
        for (var i = 0; i < pointer.Length; i++)
            if (pointer[i] == '~' && (++i == pointer.Length || pointer[i] is not ('0' or '1'))) return false;
        return true;
    }

    internal async Task<(HudReading Reading, string? Error)?> ReadAsync(HudProfile profile, CancellationToken token, string deepSeekKey = "")
    {
        var label = profile.Metric switch { 5 => "网络延迟", 6 => "HTTP 服务", 9 => "DeepSeek 余额", _ => "JSON 指标" };
        (HudReading, string) Failure(string reason) => (new(label, "—", reason), reason);
        if (profile.Error.Length != 0) return Failure(profile.Error);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            if (profile.Metric == 5)
            {
                if (profile.ProbeProtocol == 1)
                {
                    using var tcp = new TcpClient();
                    var start = Stopwatch.GetTimestamp();
                    await tcp.ConnectAsync(profile.Address, profile.Port, deadline.Token).ConfigureAwait(false);
                    return (new(label, $"{Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms", "TCP 连接耗时 · " + Updated(), Unit: "ms", ObservedAt: DateTimeOffset.Now), null);
                }
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(profile.Address, TimeSpan.FromSeconds(4),
                    cancellationToken: deadline.Token).ConfigureAwait(false);
                if (reply.Status != IPStatus.Success) return Failure("未收到 ICMP 回应（" + reply.Status + "）；目标可能禁用 Ping，不代表服务离线。");
                return (new(label, $"{reply.RoundtripTime} ms", "ICMP 往返 · " + Updated(), Unit: "ms", ObservedAt: DateTimeOffset.Now), null);
            }

            var started = Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, profile.Metric == 9 ? "https://api.deepseek.com/user/balance" : profile.Address);
            if (profile.Metric == 9)
            {
                if (!HudSecrets.ValidKey(deepSeekKey)) return Failure("请在插件设置中保存 DeepSeek API Key。");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deepSeekKey);
            }
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (profile.Metric == 9 && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure("余额接口拒绝访问，请在插件设置中检查 API Key 及账号权限。");
            if (!response.IsSuccessStatusCode) return Failure($"HTTP {(int)response.StatusCode}；请检查地址或服务状态（不自动跟随重定向）。");
            if (profile.Metric == 6)
                return (new(label, $"{milliseconds:0} ms", $"HTTP {(int)response.StatusCode} · 响应头耗时 · " + Updated(), Unit: "ms", ObservedAt: DateTimeOffset.Now), null);

            if (response.Content.Headers.ContentLength > MaxBytes) return Failure("JSON 响应超过 64 KiB 上限。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[MaxBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBytes) return Failure("JSON 响应超过 64 KiB 上限。");
            return profile.Metric == 9 ? DeepSeekBalance(bytes.AsMemory(0, count), profile.Currency) : ExtractJson(bytes.AsMemory(0, count), profile);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { return Failure("请求超过 4 秒；请检查网络或服务状态。"); }
        catch (UnauthorizedAccessException) { return Failure("需要管理员权限，请退出后以管理员身份运行 TractorTool。"); }
        catch (PingException) { return Failure("无法发送 ICMP 请求；请检查主机名、网络和防火墙。"); }
        catch (SocketException) { return Failure("TCP 连接失败，请检查主机、端口和防火墙。"); }
        catch (HttpRequestException) { return Failure("HTTP 连接失败；请检查网络、地址和证书。"); }
        catch (IOException) { return Failure("读取响应失败，请检查网络或服务状态。"); }
        // Never log remote response bodies, query strings, or exception messages containing addresses.
    }

    internal static (HudReading Reading, string? Error) ExtractJson(ReadOnlyMemory<byte> bytes, HudProfile profile)
    {
        (HudReading, string) Failure(string reason) => (new("JSON 指标", "—", reason), reason);
        if (bytes.Length > MaxBytes) return Failure("JSON 响应超过 64 KiB 上限。");
        if (!ValidPointer(profile.JsonPointer)) return Failure("JSON 字段路径无效。");
        try
        {
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            var value = json.RootElement;
            foreach (var part in profile.JsonPointer.Split('/').Skip(1))
            {
                var key = part.Replace("~1", "/").Replace("~0", "~");
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child)) value = child;
                else if (value.ValueKind == JsonValueKind.Array && (key == "0" || !key.StartsWith('0')) &&
                    int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < value.GetArrayLength()) value = value[index];
                else return Failure("找不到指定 JSON 字段，请检查字段路径。");
            }
            double? percent = null;
            if (profile.JsonPercentage)
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 100)
                    return Failure("百分比字段必须是 0–100 的 JSON 数字。");
                percent = number;
            }
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                return Failure("字段必须是文字、数字或布尔值，不能是对象、数组或 null。");
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            text = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
            if (text.Length > 160) text = text[..159] + "…";
            return (new("JSON 指标", text, Updated(), percent, percent.HasValue ? "%" : "", DateTimeOffset.Now), null);
        }
        catch (JsonException) { return Failure("响应不是有效 JSON，或嵌套超过 32 层。"); }
    }

    internal static (HudReading Reading, string? Error) DeepSeekBalance(ReadOnlyMemory<byte> bytes, string currency)
    {
        const string invalid = "余额响应缺少所选币种或格式无效。";
        if (bytes.Length <= MaxBytes)
        {
            try
            {
                using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("balance_infos", out var balances) && balances.ValueKind == JsonValueKind.Array)
                    foreach (var item in balances.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("currency", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() == currency &&
                            item.TryGetProperty("total_balance", out var total) && total.ValueKind == JsonValueKind.String &&
                            decimal.TryParse(total.GetString(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
                            return (new("DeepSeek 余额", $"{amount:0.00} {currency}", Updated(), Unit: currency, ObservedAt: DateTimeOffset.Now), null);
            }
            catch (JsonException) { }
        }
        return (new("DeepSeek 余额", "—", invalid), invalid);
    }

    private static string Updated() => "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public void Dispose() => _client.Dispose();
}
