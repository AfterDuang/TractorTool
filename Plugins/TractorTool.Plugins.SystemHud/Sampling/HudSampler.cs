using System.Collections.Concurrent;
using System.Diagnostics;

namespace TractorTool.Plugins.SystemHud;

// Plugin-owned adapters; no UI binding or settings persistence lives in the collectors.
internal sealed class HudSampler : IDisposable
{
    private readonly WindowsMetrics _windows = new();
    private readonly NetworkMetrics _network;
    private readonly HardwareMetrics _hardware = new();
    private readonly ConcurrentDictionary<Guid, (HudProfile Profile, int Version, long Time, (HudReading Reading, string? Error) Result)> _cache = new();
    private int _cacheVersion;
    internal HudSampler(NetworkMetrics? network = null) => _network = network ?? new();
    internal void ResetNetworkCache() { Interlocked.Increment(ref _cacheVersion); _cache.Clear(); }

    internal Task<SensorChoice[]> ScanSensorsAsync() => Task.Run(_hardware.Scan);

    internal async Task<(HudReading Reading, string? Error)?> ReadAsync(HudProfile profile, bool reset,
        bool hardwareEnabled, string deepSeekKey, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        if (profile.IsNetwork)
        {
            var version = Volatile.Read(ref _cacheVersion);
            if (_cache.TryGetValue(profile.Id, out var cached) && cached.Version == version && cached.Profile == profile &&
                Stopwatch.GetElapsedTime(cached.Time).TotalSeconds < profile.RefreshSeconds) return cached.Result;
            var started = Stopwatch.GetTimestamp();
            // Count attempts too: carousel cancellation must not bypass the request interval.
            _cache[profile.Id] = (profile, version, started,
                (new("等待刷新", "—", "上次请求未完成，等待方案的下次刷新时间。"), null));
            var result = await _network.ReadAsync(profile, token, deepSeekKey).ConfigureAwait(false);
            if (result is { } completed && !token.IsCancellationRequested && version == Volatile.Read(ref _cacheVersion))
                _cache[profile.Id] = (profile, version, started, completed);
            return result;
        }
        try
        {
            var reading = await ReadReadyAsync(() =>
            {
                if (reset) { _windows.Reset(); reset = false; }
                return profile.Metric switch
                {
                    8 => hardwareEnabled ? _hardware.Read(profile.SensorId)
                        : new HudReading("硬件传感器", "—", "请先在插件设置中启用硬件传感器。"),
                    10 => ReadDisk(profile.Drive),
                    _ => _windows.Read(profile.Metric)
                };
            }, token).ConfigureAwait(false);
            return (reading with { ObservedAt = DateTimeOffset.Now, Unit = profile.Metric is 2 or 3 ? "%" : reading.Unit },
                reading.Value == "—" && profile.Metric is 8 or 10 ? reading.Detail : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            var error = FailureText(ex);
            return (new("读取失败", "—", error), error);
        }
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
