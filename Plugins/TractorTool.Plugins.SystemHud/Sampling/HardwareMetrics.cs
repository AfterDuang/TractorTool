using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using System.Security.Principal;

namespace TractorTool.Plugins.SystemHud;

public sealed record SensorChoice(string Id, string Name, string Issue = "");

internal sealed class HardwareMetrics : IDisposable
{
    private readonly object _gate = new();
    private Computer? _computer;
    private string _cpuAccessIssue = "";
    private readonly Dictionary<string, (IHardware Hardware, ISensor Sensor)> _sensors = [];

    internal SensorChoice[] Scan()
    {
        lock (_gate)
        {
            Close();
            Open();
            return _sensors.Select(pair =>
            {
                var (hardware, sensor) = pair.Value;
                var issue = AccessIssue(hardware.HardwareType, sensor.SensorType, _cpuAccessIssue);
                return new SensorChoice(pair.Key, $"{hardware.Name} · {sensor.Name} ({Unit(sensor.SensorType)})"
                    + (issue.Length == 0 ? "" : " · 暂不可读"), issue);
            }).ToArray();
        }
    }

    private void Open()
    {
        if (_computer is not null) return;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            _cpuAccessIssue = CpuAccessIssue(PawnIo.IsInstalled,
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        }
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        try
        {
            computer.Open();
            foreach (var hardware in computer.Hardware) Add(hardware);
            _computer = computer;
        }
        catch { computer.Close(); _sensors.Clear(); throw; }
    }

    private void Add(IHardware hardware)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
            if (Unit(sensor.SensorType).Length > 0) _sensors[sensor.Identifier.ToString()] = (hardware, sensor);
        foreach (var child in hardware.SubHardware) Add(child);
    }

    internal HudReading Read(string id)
    {
        lock (_gate)
        {
            Open();
            if (!_sensors.TryGetValue(id, out var entry))
                return new("硬件传感器", "—", "找不到已选传感器，请重新检测并选择；设备或驱动可能已变化。");
            var issue = AccessIssue(entry.Hardware.HardwareType, entry.Sensor.SensorType, _cpuAccessIssue);
            if (issue.Length == 0) entry.Hardware.Update();
            return FormatReading(entry.Sensor.Name, entry.Hardware.Name, entry.Sensor.SensorType, entry.Sensor.Value, issue);
        }
    }

    internal static string CpuAccessIssue(bool installed, bool elevated) => !installed
        ? "未检测到 PawnIO 驱动，CPU 温度/功率/电压不可读。请从 pawnio.eu 安装后，显式退出并以管理员身份重启 TractorTool。"
        : !elevated ? "当前程序未以管理员身份运行，CPU 温度/功率/电压不可读。请显式退出并以管理员身份重启 TractorTool。" : "";

    internal static string AccessIssue(HardwareType hardware, SensorType type, string cpuIssue) =>
        hardware == HardwareType.Cpu && type is SensorType.Temperature or SensorType.Power or SensorType.Voltage ? cpuIssue : "";

    internal static HudReading FormatReading(string label, string device, SensorType type, float? raw, string issue)
    {
        var unit = Unit(type);
        if (issue.Length != 0) return new(label, "—", issue, Unit: unit);
        if (raw is not { } value || !float.IsFinite(value))
            return new(label, "—", "传感器无读数。请检查 PawnIO 驱动、管理员权限及设备支持情况，再重新检测传感器。", Unit: unit);
        return new(label, $"{value:0.#} {unit}", device, type == SensorType.Load ? Math.Clamp(value, 0, 100) : null, unit);
    }

    internal static string UnitFor(string? id)
    {
        // LibreHardwareMonitor identifiers end in /sensor-type/index, including saved profiles before a scan.
        var parts = id?.Split('/');
        return parts is { Length: >= 2 } && Enum.TryParse<SensorType>(parts[^2], true, out var type) ? Unit(type) : "";
    }

    internal static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => "°C", SensorType.Load => "%", SensorType.Clock => "MHz",
        SensorType.Power => "W", SensorType.Fan => "RPM", SensorType.Voltage => "V",
        SensorType.Data => "GiB", SensorType.SmallData => "MiB", _ => ""
    };

    private void Close() { _computer?.Close(); _computer = null; _sensors.Clear(); }
    public void Dispose() { lock (_gate) Close(); }
}
