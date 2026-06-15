using System.Text.Json.Serialization;
using TouchPortalHardwareMonitor.Models;

namespace TouchPortalHardwareMonitor;

/// <summary>
/// Source-generated JSON serialization context for trim-safe serialization
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<HardwareMapping>))]
[JsonSerializable(typeof(TPInfoMessage))]
[JsonSerializable(typeof(TPSettingsMessage))]
[JsonSerializable(typeof(TPNotificationClickedMessage))]
[JsonSerializable(typeof(TPPairMessage))]
[JsonSerializable(typeof(TPStateUpdate))]
[JsonSerializable(typeof(TPRemoveState))]
[JsonSerializable(typeof(TPCreateState))]
[JsonSerializable(typeof(TPNotification))]
[JsonSerializable(typeof(DiagnosticDump))]
public partial class AppJsonContext : JsonSerializerContext
{
}
