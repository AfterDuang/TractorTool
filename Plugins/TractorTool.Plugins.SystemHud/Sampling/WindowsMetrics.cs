using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TractorTool.Plugins.SystemHud;

internal sealed class WindowsMetrics
{
    private (ulong Idle, ulong Kernel, ulong User)? _cpu;
    private Dictionary<string, (long Received, long Sent)> _network = [];
    private long _networkTime;

    public void Reset() { _cpu = null; _network.Clear(); _networkTime = 0; }

    public HudReading Read(int metric) => metric switch
    {
        2 => Cpu(), 3 => Memory(), 4 => Network(),
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    private HudReading Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception();
        var percent = _cpu is { } old ? HudCounters.Cpu(idle, kernel, user, old.Idle, old.Kernel, old.User) : null;
        _cpu = (idle, kernel, user);
        // ponytail: GetSystemTimes covers one processor group; use PDH if >64-CPU aggregate is required.
        var scope = GetActiveProcessorGroupCount() > 1 ? "当前处理器组 · 每秒采样" : "处理器使用率 · 每秒采样";
        return new("CPU", percent is { } p ? $"{p:0}%" : "—", percent is null ? "正在建立采样基线…" : scope, percent);
    }

    private static HudReading Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception();
        var used = status.TotalPhysical - status.AvailablePhysical;
        return new("内存", $"{status.Load}%", $"{used / 1073741824d:0.0} / {status.TotalPhysical / 1073741824d:0.0} GiB", status.Load,
            Context: $"可用 {status.AvailablePhysical / 1073741824d:0.0} GiB · 物理内存");
    }

    private HudReading Network()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _networkTime == 0 ? 0 : Stopwatch.GetElapsedTime(_networkTime, now).TotalSeconds;
        var current = new Dictionary<string, (long Received, long Sent)>();
        double received = 0, sent = 0;
        var comparable = false;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var stats = adapter.GetIPStatistics();
            current[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
            if (!_network.TryGetValue(adapter.Id, out var old)) continue;
            var down = HudCounters.Rate(stats.BytesReceived, old.Received, seconds);
            var up = HudCounters.Rate(stats.BytesSent, old.Sent, seconds);
            if (down is null || up is null) continue;
            received += down.Value; sent += up.Value; comparable = true;
        }
        _network = current; _networkTime = now;
        // Aggregate interfaces, not Internet traffic: virtual adapters can count forwarded bytes twice.
        return new("网速", comparable ? $"↓ {HudCounters.Speed(received)}" : "—",
            current.Count == 0 ? "没有已连接的网络接口" : comparable ? $"↑ {HudCounters.Speed(sent)} · 活动接口合计" : "正在建立采样基线…",
            SecondaryValue: comparable ? $"↑ {HudCounters.Speed(sent)}" : "↑ —",
            Context: comparable ? $"{current.Count} 个活动接口的合计流量\n包含虚拟网卡，转发流量可能重复计入。" : null);
    }

    public static (byte Ac, HudReading Reading) Power()
    {
        if (!GetSystemPowerStatus(out var status)) throw new Win32Exception();
        var missing = status.BatteryFlags != 255 && (status.BatteryFlags & 128) != 0;
        var percent = !missing && status.Percent <= 100 ? (double?)status.Percent : null;
        var detail = status.Ac == 1 ? "已连接电源" : status.Ac == 0 ? "正在使用电池" : "电源状态未知";
        if (!missing && status.BatteryFlags != 255 && (status.BatteryFlags & 8) != 0) detail = "正在充电";
        return (status.Ac, new("电量", percent is { } p ? $"{p:0}%" : "—", missing ? "未检测到电池 · " + detail : detail, percent));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte Ac, BatteryFlags, Percent, Saver; public uint LifeTime, FullLifeTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
}
