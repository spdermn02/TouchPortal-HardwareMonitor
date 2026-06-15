using LibreHardwareMonitor.Hardware;
using LHMBridge.Models;

namespace LHMBridge.Services;

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

    public List<HardwareInfo> GetHardware()
    {
        var result = new List<HardwareInfo>();
        CollectHardware(_computer.Hardware, result);
        return result;
    }

    private void CollectHardware(IEnumerable<IHardware> hardwareList, List<HardwareInfo> result)
    {
        foreach (var hardware in hardwareList)
        {
            result.Add(new HardwareInfo
            {
                Identifier = hardware.Identifier.ToString(),
                Name = hardware.Name,
                HardwareType = hardware.HardwareType.ToString()
            });

            // Also collect sub-hardware (e.g., CPU cores)
            if (hardware.SubHardware.Length > 0)
            {
                CollectHardware(hardware.SubHardware, result);
            }
        }
    }

    public List<SensorInfo> GetSensors()
    {
        var result = new List<SensorInfo>();
        CollectSensors(_computer.Hardware, result);
        return result;
    }

    private void CollectSensors(IEnumerable<IHardware> hardwareList, List<SensorInfo> result)
    {
        foreach (var hardware in hardwareList)
        {
            // Update hardware to get fresh sensor values
            hardware.Update();

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.Value.HasValue)
                {
                    result.Add(new SensorInfo
                    {
                        Parent = hardware.Identifier.ToString(),
                        Identifier = sensor.Identifier.ToString(),
                        Name = sensor.Name,
                        SensorType = sensor.SensorType.ToString(),
                        Value = sensor.Value.Value
                    });
                }
            }

            // Also collect sensors from sub-hardware
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
