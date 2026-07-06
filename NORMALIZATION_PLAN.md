# Plan: Normalize Output to Match Libre Hardware Monitor Display

## Current State Analysis

### What the Plugin Currently Does
1. **Hardware Collection**: Collects hardware info (Identifier, Name, HardwareType) from LHMBridge
2. **Sensor Collection**: Collects sensors with (Parent, Identifier, Name, SensorType, Value)
3. **Transformations Applied**:
   - Temperature conversion (C/F) - optional
   - Throughput normalization (B/s → KB/s → MB/s → GB/s) - optional
   - Data normalization (MB → GB) - optional
   - Value rounding to 1 decimal place

### Current Output Format Issues
| Issue | Current Behavior | LHM Display |
|-------|-----------------|-------------|
| **No Units** | Units only shown when normalization enabled | Always shows units (%, °C, MHz, W, etc.) |
| **SensorType Names** | Uses raw enum names (e.g., "Temperatures") | Uses cleaner names (e.g., "Temperature") |
| **No Min/Max Values** | Only current value | Shows Value, Min, Max columns |
| **Flat Hierarchy** | Hardware type + index | Hierarchical tree structure |
| **State ID Format** | `tp-hm.state.CPU1.load.cpu.total` | Could be more descriptive |

---

## LibreHardwareMonitor SensorType → Unit Mapping

Based on [LibreHardwareMonitor source](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/ISensor.cs):

| SensorType | Unit | Display Name |
|------------|------|--------------|
| Voltage | V | Voltage |
| Current | A | Current |
| Power | W | Power |
| Clock | MHz | Clock |
| Temperature | °C | Temperature |
| Load | % | Load |
| Frequency | Hz | Frequency |
| Fan | RPM | Fan Speed |
| Flow | L/h | Flow Rate |
| Control | % | Control |
| Level | % | Level |
| Factor | (none) | Factor |
| Data | GB | Data |
| SmallData | MB | Data |
| Throughput | B/s | Throughput |
| TimeSpan | s | Time |
| Timing | ns | Timing |
| Energy | mWh | Energy |
| Noise | dBA | Noise |
| Conductivity | µS/cm | Conductivity |
| Humidity | % | Humidity |

---

## Proposed Changes

### Phase 1: Add Default Units to All Sensors

**File: `src/index.js`**

Create a sensor type to unit mapping:

```javascript
const SENSOR_UNITS = {
  'Voltage': 'V',
  'Current': 'A',
  'Power': 'W',
  'Clock': 'MHz',
  'Temperature': '°C',  // or °F based on setting
  'Temperatures': '°C', // Handle both forms
  'Load': '%',
  'Frequency': 'Hz',
  'Fan': 'RPM',
  'Flow': 'L/h',
  'Control': '%',
  'Level': '%',
  'Factor': '',
  'Data': 'GB',
  'SmallData': 'MB',
  'Throughput': 'B/s',
  'TimeSpan': 's',
  'Timing': 'ns',
  'Energy': 'mWh',
  'Noise': 'dBA',
  'Conductivity': 'µS/cm',
  'Humidity': '%'
};
```

**Always create unit states** - not just when normalization is enabled.

### Phase 2: Normalize SensorType Names

Map LHM's internal sensor type names to cleaner display names:

```javascript
const SENSOR_TYPE_DISPLAY = {
  'Temperatures': 'Temperature',
  'SmallData': 'Data',
  // ... other mappings
};
```

### Phase 3: Add Min/Max Value Tracking (Optional Enhancement)

**File: `csharp/LHMBridge/Models/SensorInfo.cs`**

Add Min/Max properties:

```csharp
public class SensorInfo
{
    // ... existing properties

    [JsonPropertyName("Min")]
    public float? Min { get; set; }

    [JsonPropertyName("Max")]
    public float? Max { get; set; }
}
```

**File: `csharp/LHMBridge/Services/HardwareMonitorService.cs`**

Collect min/max values:

```csharp
result.Add(new SensorInfo
{
    // ... existing
    Min = sensor.Min,
    Max = sensor.Max
});
```

**File: `src/index.js`**

Create additional states for min/max:
- `{stateId}.min`
- `{stateId}.max`

### Phase 4: Improve State ID and Description Format

**Current format:**
- ID: `tp-hm.state.CPU1.load.cpu.total`
- Desc: `CPU 1 - Intel Core i9 - Load - CPU Total`

**Proposed format:**
- ID: `tp-hm.state.CPU1.Load.CPUTotal`
- Desc: `[CPU 1] Intel Core i9 > Load > CPU Total`
- Value: `45.2%` (include unit in value or separate state)

### Phase 5: Preserve Hardware Hierarchy (Optional)

Currently sub-hardware is flattened. Could enhance to show:
- `Motherboard > SuperIO > Fan #1`
- `CPU > Core #1 > Temperature`

This requires modifying `HardwareMonitorService.cs` to track parent relationships.

---

## Implementation Priority

### High Priority (Core Normalization)
1. **Add SENSOR_UNITS mapping** - Always show appropriate units
2. **Normalize SensorType names** - Use display-friendly names
3. **Always create unit states** - Remove conditional unit state creation

### Medium Priority (Enhanced Display)
4. **Add Min/Max tracking** - Match LHM's full display
5. **Improve state descriptions** - Better hierarchy in descriptions

### Low Priority (Advanced)
6. **Full hardware hierarchy** - Tree-like state organization
7. **Combined value+unit states** - Single state showing "45.2%" instead of separate

---

## Files to Modify

| File | Changes |
|------|---------|
| `src/index.js` | Add SENSOR_UNITS map, normalize type names, always create unit states |
| `src/consts.js` | Add SENSOR_UNITS and SENSOR_TYPE_DISPLAY constants |
| `csharp/LHMBridge/Models/SensorInfo.cs` | Add Min/Max properties |
| `csharp/LHMBridge/Services/HardwareMonitorService.cs` | Collect Min/Max values |

---

## Example Output Comparison

### Current Output
```
State ID: tp-hm.state.CPU1.Temperatures.cpu.core.1
Value: 45.2
(No unit state unless normalization enabled)
```

### Proposed Output
```
State ID: tp-hm.state.CPU1.Temperature.Core1
Value: 45.2
Unit State: tp-hm.state.CPU1.Temperature.Core1.unit = "°C"
Min State: tp-hm.state.CPU1.Temperature.Core1.min = "32.0"
Max State: tp-hm.state.CPU1.Temperature.Core1.max = "78.5"
Description: "[CPU 1] Intel Core i9 > Temperature > Core #1"
```

---

## Testing Considerations

1. Verify all sensor types have correct unit mappings
2. Test temperature unit switching (C/F) still works
3. Test throughput/data normalization still overrides default units
4. Verify backwards compatibility with existing TouchPortal configurations
5. Test with multiple hardware types (CPU, GPU, RAM, Storage, Network)

---

## Questions to Clarify

1. Should combined "value + unit" states be created (e.g., "45.2°C")?
2. Should Min/Max states be optional via settings?
3. How important is preserving exact backwards compatibility for state IDs?
4. Should the hierarchy depth be configurable?
