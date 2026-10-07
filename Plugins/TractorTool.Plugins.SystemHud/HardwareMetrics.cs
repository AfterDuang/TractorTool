using LibreHardwareMonitor.Hardware;

namespace TractorTool.Plugins.SystemHud;

public sealed record SensorChoice(string Id, string Name);

internal sealed class HardwareMetrics : IDisposable
{
    private readonly object _gate = new();
    private Computer? _computer;
    private readonly Dictionary<string, (IHardware Hardware, ISensor Sensor)> _sensors = [];

    internal SensorChoice[] Scan()
    {
        lock (_gate)
        {
            Close();
            Open();
            return _sensors.Select(pair => new SensorChoice(pair.Key,
                $"{pair.Value.Hardware.Name} · {pair.Value.Sensor.Name} ({Unit(pair.Value.Sensor.SensorType)})")).ToArray();
        }
    }

    private void Open()
    {
        if (_computer is not null) return;
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
            entry.Hardware.Update();
            var raw = entry.Sensor.Value;
            if (raw is not { } value || !float.IsFinite(value))
                return new(entry.Sensor.Name, "—", "传感器无读数。部分传感器需要管理员权限和已安装的 PawnIO；驱动或设备也可能不支持。");
            var unit = Unit(entry.Sensor.SensorType);
            return new(entry.Sensor.Name, $"{value:0.#} {unit}", entry.Hardware.Name,
                entry.Sensor.SensorType == SensorType.Load ? Math.Clamp(value, 0, 100) : null, unit);
        }
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
