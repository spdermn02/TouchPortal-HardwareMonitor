using System.Text.Json.Serialization;

namespace LHMBridge.Models;

public class Command
{
    [JsonPropertyName("command")]
    public string CommandName { get; set; } = string.Empty;
}
