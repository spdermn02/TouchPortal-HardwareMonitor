# Plan: Normalize Output to Match Libre Hardware Monitor Display

## Current C# Plugin Architecture

```
TouchPortalHardwareMonitor/
├── Program.cs                    # Main entry, orchestrates hardware discovery & capture
├── Models/
│   ├── HardwareModels.cs         # HardwareItem, SensorItem, SensorStateInfo
│   └── TouchPortalMessages.cs    # Touch Portal protocol messages
└── Services/
    ├── HardwareMonitorService.cs # LibreHardwareMonitor wrapper
    └── TouchPortalClient.cs      # Touch Portal TCP communication
```

## What the Plugin Currently Does

1. **Collects hardware** via `HardwareMonitorService.GetHardware()` → returns `List<HardwareItem>`
2. **Collects sensors** via `HardwareMonitorService.GetSensors()` → returns `List<SensorItem>`
3. **Filters out** network filter drivers and inactive adapters
4. **Creates TouchPortal states** for each sensor with format: `tp-hm.state.{Type}{Index}.{SensorType}.{name}`
5. **Applies conversions** for temperature (C/F) and normalization (Throughput, Data)

---

## Current Output Issues vs LHM Display

| Issue | Current Behavior | Libre Hardware Monitor |
|-------|-----------------|------------------------|
| **Units** | Only shown when normalization enabled | Always shows units (%, °C, MHz, W, RPM, etc.) |
| **Min/Max** | Not captured | Shows Min, Max columns alongside Value |
| **Sensor Names** | Lowercased, spaces→dots, #→removed | Original names preserved |
| **Value Format** | Always 0 or 1 decimal | Variable precision based on type |

---

## LibreHardwareMonitor SensorType → Unit Mapping

From [LibreHardwareMonitor ISensor.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor):

| SensorType | Unit | Notes |
|------------|------|-------|
| Voltage | V | Volts |
| Current | A | Amps |
| Power | W | Watts |
| Clock | MHz | Megahertz |
| Temperature | °C | Celsius (or °F if converted) |
| Load | % | Percentage |
| Frequency | Hz | Hertz |
| Fan | RPM | Revolutions per minute |
| Flow | L/h | Liters per hour |
| Control | % | Fan/pump control percentage |
| Level | % | Battery level, etc. |
| Factor | (none) | Multiplier |
| Data | GB | Gigabytes |
| SmallData | MB | Megabytes |
| Throughput | B/s | Bytes per second |
| TimeSpan | s | Seconds |
| Timing | ns | Nanoseconds |
| Energy | mWh | Milliwatt-hours |
| Noise | dBA | Decibels |
| Conductivity | µS/cm | Microsiemens per cm |
| Humidity | % | Relative humidity |

---

## Proposed Changes

### Phase 1: Add Default Units to All Sensors (High Priority)

**File: `Program.cs`**

Add a static dictionary mapping sensor types to their default units:

```csharp
private static readonly Dictionary<string, string> SensorTypeUnits = new(StringComparer.OrdinalIgnoreCase)
{
    { "Voltage", "V" },
    { "Current", "A" },
    { "Power", "W" },
    { "Clock", "MHz" },
    { "Temperature", "°C" },
    { "Load", "%" },
    { "Frequency", "Hz" },
    { "Fan", "RPM" },
    { "Flow", "L/h" },
    { "Control", "%" },
    { "Level", "%" },
    { "Factor", "" },
    { "Data", "GB" },
    { "SmallData", "MB" },
    { "Throughput", "B/s" },
    { "TimeSpan", "s" },
    { "Timing", "ns" },
    { "Energy", "mWh" },
    { "Noise", "dBA" },
    { "Conductivity", "µS/cm" },
    { "Humidity", "%" }
};
```

**Modify `RunSensorConversions()`** to always set the unit:

```csharp
private static void RunSensorConversions(SensorItem sensor)
{
    // Set default unit first
    if (SensorTypeUnits.TryGetValue(sensor.SensorType, out var defaultUnit))
    {
        sensor.Unit = defaultUnit;
    }

    // Temperature conversion
    if (sensor.SensorType == "Temperature")
    {
        if (_tempUnit.Equals("F", StringComparison.OrdinalIgnoreCase))
        {
            sensor.Value = (sensor.Value * 9.0f / 5.0f) + 32.0f;
            sensor.Unit = "°F";  // Override unit
        }
    }
    // Throughput normalization (only if enabled)
    else if (sensor.SensorType == "Throughput" &&
             _normalizeThroughput.Equals("Yes", StringComparison.OrdinalIgnoreCase))
    {
        // ... existing normalization code ...
        sensor.Unit = GetThroughputUnit(count);  // Override with normalized unit
    }
    // Data normalization (only if enabled)
    else if (sensor.SensorType == "SmallData" &&
             _normalizeData.Equals("Yes", StringComparison.OrdinalIgnoreCase))
    {
        // ... existing normalization code ...
        sensor.Unit = GetDataUnit(count);  // Override with normalized unit
    }
}
```

**Modify `CaptureAsync()`** to always create unit states (not just when `!string.IsNullOrEmpty(sensor.Unit)`).

---

### Phase 2: Add Min/Max Value Tracking (Medium Priority)

**File: `Models/HardwareModels.cs`**

Extend `SensorItem`:

```csharp
public class SensorItem
{
    // ... existing properties ...

    public float? Min { get; set; }
    public float? Max { get; set; }
}
```

**File: `Services/HardwareMonitorService.cs`**

Modify `CollectSensors()` to capture Min/Max:

```csharp
result.Add(new SensorItem
{
    Parent = hardware.Identifier.ToString(),
    Identifier = sensor.Identifier.ToString(),
    Name = sensor.Name,
    SensorType = sensor.SensorType.ToString(),
    Value = sensor.Value.Value,
    Min = sensor.Min,    // Add this
    Max = sensor.Max     // Add this
});
```

**File: `Program.cs`**

Create additional states for min/max in `CaptureAsync()`:

```csharp
// After creating the main state and unit state:
if (sensor.Min.HasValue)
{
    newStates.Add(new TPStateDefinition
    {
        Id = stateInfo.Id + ".min",
        Desc = stateInfo.Desc + " Min",
        DefaultValue = "0",
        ParentGroup = stateInfo.ParentGroup
    });
    stateUpdates.Add(new TPStateValue
    {
        Id = stateInfo.Id + ".min",
        Value = sensor.Min.Value.ToString("F1")
    });
}

if (sensor.Max.HasValue)
{
    newStates.Add(new TPStateDefinition
    {
        Id = stateInfo.Id + ".max",
        Desc = stateInfo.Desc + " Max",
        DefaultValue = "0",
        ParentGroup = stateInfo.ParentGroup
    });
    stateUpdates.Add(new TPStateValue
    {
        Id = stateInfo.Id + ".max",
        Value = sensor.Max.Value.ToString("F1")
    });
}
```

---

### Phase 3: Improve State Descriptions (Low Priority)

**Current format:**
```
Desc: "CPU 1 - Intel Core i9-9900K - Temperature - CPU Core #1"
```

**Proposed format (matching LHM tree):**
```
Desc: "Intel Core i9-9900K > Temperatures > CPU Core #1"
```

**File: `Program.cs` - `BuildSensorStateId()`**

```csharp
private static SensorStateInfo BuildSensorStateId(string hardwareKey, SensorItem sensor)
{
    var hw = _hardware[hardwareKey];
    var sensorType = sensor.SensorType;
    var sensorName = sensor.Name.ToLower().Replace(" ", ".").Replace("#", "");
    var indexNum = hw.Index > 0 ? hw.Index.ToString() : "";

    var stateId = $"tp-hm.state.{hw.HardwareType}{indexNum}.{sensorType}.{sensorName}";

    // Improved description matching LHM hierarchy
    var parentGroup = string.IsNullOrEmpty(indexNum)
        ? hw.Name
        : $"{hw.Name} ({hw.HardwareType} {indexNum})";

    var desc = $"{hw.Name} > {sensorType} > {sensor.Name}";

    return new SensorStateInfo
    {
        Id = stateId,
        Desc = desc,
        DefaultValue = "0",
        ParentGroup = parentGroup
    };
}
```

---

### Phase 4: Add Combined Value+Unit State (Optional)

Create an additional state that combines value and unit for easy display:

```
State: tp-hm.state.CPU1.Temperature.core1.formatted
Value: "45.2°C"
```

This would require adding a new state type but provides convenience for TouchPortal layouts.

---

## Files to Modify

| File | Changes |
|------|---------|
| `Program.cs` | Add `SensorTypeUnits` dictionary, modify `RunSensorConversions()`, modify `CaptureAsync()` to always create unit states, optionally create min/max states |
| `Models/HardwareModels.cs` | Add `Min` and `Max` properties to `SensorItem` |
| `Services/HardwareMonitorService.cs` | Capture `sensor.Min` and `sensor.Max` values |

---

## Example Output Comparison

### Current Output
```
State ID: tp-hm.state.CPU1.Temperature.cpu.core.1
Value: 45
Desc: CPU 1 - Intel Core i9 - Temperature - CPU Core #1
(No unit state unless temperature unit is F or normalization enabled)
```

### Proposed Output (Phase 1)
```
State ID: tp-hm.state.CPU1.Temperature.cpu.core.1
Value: 45.2
Unit: tp-hm.state.CPU1.Temperature.cpu.core.1.unit = "°C"
Desc: CPU 1 - Intel Core i9 - Temperature - CPU Core #1
```

### Proposed Output (Phase 2 - with Min/Max)
```
State ID: tp-hm.state.CPU1.Temperature.cpu.core.1
Value: 45.2
Unit: tp-hm.state.CPU1.Temperature.cpu.core.1.unit = "°C"
Min: tp-hm.state.CPU1.Temperature.cpu.core.1.min = "32.0"
Max: tp-hm.state.CPU1.Temperature.cpu.core.1.max = "78.5"
```

---

## Implementation Priority

1. **Phase 1: Add Default Units** - High impact, low effort
2. **Phase 2: Add Min/Max** - Medium impact, medium effort
3. **Phase 3: Improve Descriptions** - Low impact, low effort
4. **Phase 4: Combined States** - Optional, adds convenience

---

## Backwards Compatibility Notes

- State IDs remain unchanged (existing TouchPortal configurations continue to work)
- New unit states are additive (won't break existing setups)
- Min/Max states are additive
- Description changes are cosmetic only
