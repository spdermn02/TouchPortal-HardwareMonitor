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
                Name = hardware.Name,
                HardwareType = hardware.HardwareType.ToString()
            });

            if (hardware.SubHardware.Length > 0)
            {
                CollectHardware(hardware.SubHardware, result);
            }
        }
    }

    public List<SensorItem> GetSensors()
    {
        var result = new List<SensorItem>();
        CollectSensors(_computer.Hardware, result);
        return result;
    }

    private void CollectSensors(IEnumerable<IHardware> hardwareList, List<SensorItem> result)
    {
        foreach (var hardware in hardwareList)
        {
            hardware.Update();

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.Value.HasValue)
                {
                    result.Add(new SensorItem
                    {
                        Parent = hardware.Identifier.ToString(),
                        Identifier = sensor.Identifier.ToString(),
                        Name = sensor.Name,
                        SensorType = sensor.SensorType.ToString(),
                        Value = sensor.Value.Value,
                        Min = sensor.Min,
                        Max = sensor.Max
                    });
                }
            }

            if (hardware.SubHardware.Length > 0)
            {
                CollectSensors(hardware.SubHardware, result);
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
