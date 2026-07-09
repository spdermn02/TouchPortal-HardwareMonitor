using LibreHardwareMonitor.Hardware;
using TouchPortalHardwareMonitor.Models;

namespace TouchPortalHardwareMonitor.Services;

public class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private bool _disposed;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true
        };
        _computer.Open();
    }

    // LibreHardwareMonitor sometimes returns hardware/sensor names with embedded
    // control characters (e.g. NUL or form-feed in garbled DIMM part numbers).
    // Strip them so they don't corrupt state IDs, logs, or Touch Portal display.
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();

    public List<HardwareItem> GetHardware()
    {
        var result = new List<HardwareItem>();
        CollectHardware(_computer.Hardware, result);
        return result;
    }

    private void CollectHardware(IEnumerable<IHardware> hardwareList, List<HardwareItem> result)
    {
        foreach (var hardware in hardwareList)
        {
            result.Add(new HardwareItem
            {
                Identifier = hardware.Identifier.ToString(),
                Name = Clean(hardware.Name),
                HardwareType = hardware.HardwareType.ToString()
            });

            if (hardware.SubHardware.Length > 0)
            {
                CollectHardware(hardware.SubHardware, result);
            }
        }
    }

    public List<SensorItem> GetSensors() => CollectAll(includeValueless: false);

    // Diagnostic variant: also includes sensors LHM created but couldn't read
    // (Value == null). A CPU/motherboard whose sensors all come back null is the
    // tell-tale of a kernel driver (WinRing0/Ring0) that failed to load - the
    // live path filters these out, so the dump needs them to diagnose the cause.
    public List<SensorItem> GetSensorsForDiagnostics() => CollectAll(includeValueless: true);

    private List<SensorItem> CollectAll(bool includeValueless)
    {
        var result = new List<SensorItem>();
        CollectSensors(_computer.Hardware, result, includeValueless);
        return result;
    }

    private void CollectSensors(IEnumerable<IHardware> hardwareList, List<SensorItem> result, bool includeValueless)
    {
        foreach (var hardware in hardwareList)
        {
            hardware.Update();

            foreach (var sensor in hardware.Sensors)
            {
                var hasValue = sensor.Value.HasValue;
                if (!hasValue && !includeValueless)
                {
                    continue;
                }

                result.Add(new SensorItem
                {
                    Parent = hardware.Identifier.ToString(),
                    Identifier = sensor.Identifier.ToString(),
                    Name = Clean(sensor.Name),
                    SensorType = sensor.SensorType.ToString(),
                    Value = hasValue ? sensor.Value!.Value : float.NaN,
                    ValuePresent = hasValue,
                    Min = sensor.Min,
                    Max = sensor.Max
                });
            }

            if (hardware.SubHardware.Length > 0)
            {
                CollectSensors(hardware.SubHardware, result, includeValueless);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _computer.Close();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
