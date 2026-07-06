using System.Text.Json.Serialization;

namespace LHMBridge.Models;

public class HardwareInfo
{
    [JsonPropertyName("Identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("HardwareType")]
    public string HardwareType { get; set; } = string.Empty;
}
