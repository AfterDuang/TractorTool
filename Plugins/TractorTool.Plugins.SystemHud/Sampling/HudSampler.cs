using System.Collections.Concurrent;
using System.Diagnostics;

namespace TractorTool.Plugins.SystemHud;

// Plugin-owned adapters; no UI binding or settings persistence lives in the collectors.
internal sealed class HudSampler : IDisposable
{
    private readonly WindowsMetrics _windows = new();
    private readonly NetworkMetrics _network;
    private readonly HardwareMetrics _hardware = new();
    private readonly Func<HudProfile, HudReading>? _readLocal;
    private readonly ConcurrentDictionary<Guid, (HudProfile Profile, int Version, long Time, (HudReading Reading, string? Error) Result)> _cache = new();
    private int _cacheVersion;
    private bool _resetPending;
    internal HudSampler(NetworkMetrics? network = null, Func<HudProfile, HudReading>? readLocal = null)
    { _network = network ?? new(); _readLocal = readLocal; }
    internal void ResetCache() { Interlocked.Increment(ref _cacheVersion); _cache.Clear(); }

    internal TimeSpan GetRefreshDelay(HudProfile profile)
    {
        var remaining = profile.SamplingSeconds;
        if (_cache.TryGetValue(profile.Id, out var cached) && cached.Version == Volatile.Read(ref _cacheVersion) && cached.Profile == profile)
        {
            // A cancelled first baseline must not postpone the first usable reading for ten minutes.
            if (cached.Result.Reading is { IsInitializing: true, ObservedAt: null }) remaining = Math.Min(remaining, .25);
            remaining -= Stopwatch.GetElapsedTime(cached.Time).TotalSeconds;
        }
        return TimeSpan.FromSeconds(Math.Max(0, remaining));
    }

    internal Task<SensorChoice[]> ScanSensorsAsync() => Task.Run(_hardware.Scan);

    internal async Task<(HudReading Reading, string? Error)?> ReadAsync(HudProfile profile, bool reset,
        bool hardwareEnabled, string deepSeekKey, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        _resetPending |= reset;
        var version = Volatile.Read(ref _cacheVersion);
        if (_cache.TryGetValue(profile.Id, out var cached) && cached.Version == version && cached.Profile == profile &&
            GetRefreshDelay(profile) > TimeSpan.Zero) return cached.Result;
        var started = Stopwatch.GetTimestamp();
        // Count attempts too: switching, clicks and cancellation must not bypass the interval.
        _cache[profile.Id] = (profile, version, started,
            cached.Profile == profile && cached.Version == version ? cached.Result
                : (new("等待刷新", "—", "上次读取未完成，等待方案的下次采样时间。") { IsInitializing = !profile.IsNetwork }, null));
        (HudReading Reading, string? Error)? result;
        try
        {
            if (profile.IsNetwork) result = await _network.ReadAsync(profile, token, deepSeekKey).ConfigureAwait(false);
            else
            {
                var reading = await ReadReadyAsync(() =>
                {
                    if (_resetPending) { _windows.Reset(); _resetPending = false; }
                    started = Stopwatch.GetTimestamp();
                    return _readLocal?.Invoke(profile) ?? (profile.Metric switch
                    {
                        1 => ReadClock(),
                        8 => hardwareEnabled ? _hardware.Read(profile.SensorId)
                            : new HudReading("硬件传感器", "—", "请先在插件设置中启用硬件传感器。"),
                        10 => ReadDisk(profile.Drive),
                        11 => ReadUptime(),
                        _ => _windows.Read(profile.Metric)
                    });
                }, token).ConfigureAwait(false);
                result = (reading with { ObservedAt = DateTimeOffset.Now, Unit = profile.Metric is 2 or 3 ? "%" : reading.Unit },
                    reading.Value == "—" && profile.Metric is 8 or 10 ? reading.Detail : null);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            var error = profile.IsNetwork ? "网络指标读取失败，请检查配置或重试。" : FailureText(ex);
            result = (new("读取失败", "—", error), error);
        }
        if (result is { } completed && (!profile.IsNetwork || !token.IsCancellationRequested) && version == Volatile.Read(ref _cacheVersion))
            _cache[profile.Id] = (profile, version, started, completed);
        return result;
    }

    private static HudReading ReadClock()
    {
        var now = DateTimeOffset.Now;
        return new("时钟", now.ToString("HH:mm:ss"), now.ToString("yyyy/MM/dd dddd"));
    }

    private static HudReading ReadUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new("系统运行时间", $"{uptime.Days} 天 {uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}", "当前系统会话运行时长");
    }

    internal static async Task<HudReading> ReadReadyAsync(Func<HudReading> read, CancellationToken token)
    {
        var reading = await Task.Run(read, token).ConfigureAwait(false);
        if (!reading.IsInitializing) return reading;
        // One extra read establishes a rate; normal updates keep their existing interval.
        await Task.Delay(250, token).ConfigureAwait(false);
        return await Task.Run(read, token).ConfigureAwait(false);
    }

    private static HudReading ReadDisk(string path)
    {
        var drive = new DriveInfo(path);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            return new("磁盘空间", "—", "所选本地磁盘不可用。");
        var total = drive.TotalSize;
        var used = total - drive.TotalFreeSpace;
        return new("磁盘空间", $"{used / 1073741824d:0.0} GiB", $"{path} · 共 {total / 1073741824d:0.0} GiB",
            total > 0 ? used * 100d / total : null, "GiB");
    }

    internal static string FailureText(Exception ex) => ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
        ? "需要管理员权限，请退出后以管理员身份运行 TractorTool。" : ex.Message;

    public void Dispose() { _network.Dispose(); _hardware.Dispose(); }
}
