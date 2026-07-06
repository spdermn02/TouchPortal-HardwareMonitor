using System.Text.Json.Serialization;
using TouchPortalHardwareMonitor.Models;

namespace TouchPortalHardwareMonitor;

/// <summary>
/// Source-generated JSON serialization context for trim-safe serialization
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    // Sensors can report non-finite values (e.g. Infinity/NaN from a
    // divide-by-zero on adapters with 0 link speed). Without this, the
    // diagnostic dump throws mid-write and produces a truncated, invalid
    // file. Writing them as "Infinity"/"NaN" keeps the dump valid and
    // preserves the signal for diagnosis.
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
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
