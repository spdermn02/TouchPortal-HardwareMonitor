using System.Text.Json.Serialization;

namespace TouchPortalHardwareMonitor.Models;

// Persistent mapping of hardware identifier to assigned index
public class HardwareMapping
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hardwareType")]
    public string HardwareType { get; set; } = string.Empty;

    [JsonPropertyName("assignedIndex")]
    public int AssignedIndex { get; set; }
}

public class HardwareItem
{
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HardwareType { get; set; } = string.Empty;
    public int Index { get; set; }
    public Dictionary<string, SensorItem> Sensors { get; set; } = new();
}

public class SensorItem
{
    public string Parent { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SensorType { get; set; } = string.Empty;
    public float Value { get; set; }
    // False when LibreHardwareMonitor reported the sensor but had no readable
    // value (Value == null); Value is then NaN. Used only by the diagnostic dump.
    public bool ValuePresent { get; set; } = true;
    public float? Min { get; set; }
    public float? Max { get; set; }
    public string? Unit { get; set; }
    public SensorStateInfo? StateId { get; set; }
}

// Minimal shape of the GitHub "latest release" API response, for update checks.
public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;
}

// A connected display, sourced from Win32 (not LibreHardwareMonitor).
public class DisplayInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRateHz { get; set; }
}

public class SensorStateInfo
{
    public string Id { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = "0";
    public string ParentGroup { get; set; } = string.Empty;
}

// Diagnostic dump models
public class DiagnosticDump
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; set; } = string.Empty;

    // Whether the plugin process is running elevated. The LibreHardwareMonitor
    // kernel driver (WinRing0) only loads when elevated, so this is the first
    // thing to check when CPU/motherboard sensors are missing.
    [JsonPropertyName("isElevated")]
    public bool IsElevated { get; set; }

    // Human-readable summary of whether MSR/SuperIO sensors are accessible
    // (i.e. whether the kernel driver appears to have loaded).
    [JsonPropertyName("sensorAccess")]
    public string SensorAccess { get; set; } = string.Empty;

    [JsonPropertyName("settings")]
    public DiagnosticSettings Settings { get; set; } = new();

    [JsonPropertyName("hardwareMapping")]
    public List<HardwareMapping> HardwareMapping { get; set; } = new();

    [JsonPropertyName("hardware")]
    public List<DiagnosticHardware> Hardware { get; set; } = new();

    [JsonPropertyName("sensors")]
    public List<DiagnosticSensor> Sensors { get; set; } = new();

    [JsonPropertyName("activeNetworkAdapters")]
    public List<string> ActiveNetworkAdapters { get; set; } = new();
}

public class DiagnosticSettings
{
    [JsonPropertyName("captureInterval")]
    public int CaptureInterval { get; set; }

    [JsonPropertyName("tempUnit")]
    public string TempUnit { get; set; } = string.Empty;

    [JsonPropertyName("normalizeThroughput")]
    public string NormalizeThroughput { get; set; } = string.Empty;

    [JsonPropertyName("normalizeData")]
    public string NormalizeData { get; set; } = string.Empty;

    [JsonPropertyName("debugLogging")]
    public bool DebugLogging { get; set; }
}

public class DiagnosticHardware
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hardwareType")]
    public string HardwareType { get; set; } = string.Empty;

    [JsonPropertyName("included")]
    public bool Included { get; set; }

    [JsonPropertyName("skipReason")]
    public string? SkipReason { get; set; }

    [JsonPropertyName("assignedIndex")]
    public int? AssignedIndex { get; set; }

    [JsonPropertyName("normalizedType")]
    public string? NormalizedType { get; set; }

    [JsonPropertyName("sensorCount")]
    public int SensorCount { get; set; }
}

public class DiagnosticSensor
{
    [JsonPropertyName("parent")]
    public string Parent { get; set; } = string.Empty;

    [JsonPropertyName("parentName")]
    public string? ParentName { get; set; }

    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sensorType")]
    public string SensorType { get; set; } = string.Empty;

    [JsonPropertyName("rawValue")]
    public float RawValue { get; set; }

    // False when LHM reported this sensor but couldn't read a value (RawValue
    // is NaN). Many null CPU/motherboard sensors => kernel driver didn't load.
    [JsonPropertyName("valuePresent")]
    public bool ValuePresent { get; set; } = true;

    [JsonPropertyName("convertedValue")]
    public float? ConvertedValue { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("min")]
    public float? Min { get; set; }

    [JsonPropertyName("max")]
    public float? Max { get; set; }

    [JsonPropertyName("stateId")]
    public string? StateId { get; set; }

    [JsonPropertyName("matched")]
    public bool Matched { get; set; }
}
