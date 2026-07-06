using System.Text.Json.Serialization;

namespace LHMBridge.Models;

public class SensorInfo
{
    [JsonPropertyName("Parent")]
    public string Parent { get; set; } = string.Empty;

    [JsonPropertyName("Identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("SensorType")]
    public string SensorType { get; set; } = string.Empty;

    [JsonPropertyName("Value")]
    public float Value { get; set; }
}
