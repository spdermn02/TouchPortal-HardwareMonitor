using System.Text.Json.Serialization;

namespace TouchPortalHardwareMonitor.Models;

// Base message received from Touch Portal
public class TPMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

// Info message received after pairing
public class TPInfoMessage : TPMessage
{
    [JsonPropertyName("sdkVersion")]
    public int SdkVersion { get; set; }

    [JsonPropertyName("tpVersionString")]
    public string TpVersionString { get; set; } = string.Empty;

    [JsonPropertyName("tpVersionCode")]
    public int TpVersionCode { get; set; }

    [JsonPropertyName("pluginVersion")]
    public int PluginVersion { get; set; }

    [JsonPropertyName("settings")]
    public List<Dictionary<string, string>> Settings { get; set; } = new();
}

// Settings message
public class TPSettingsMessage : TPMessage
{
    [JsonPropertyName("values")]
    public List<Dictionary<string, string>> Values { get; set; } = new();
}

// Pair request sent to Touch Portal
public class TPPairMessage
{
    [JsonPropertyName("type")]
    public string Type { get; } = "pair";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

// State update message
public class TPStateUpdate
{
    [JsonPropertyName("type")]
    public string Type { get; } = "stateUpdate";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

// Bulk state update
public class TPStateUpdateMany
{
    [JsonPropertyName("type")]
    public string Type { get; } = "stateUpdateMany";

    [JsonPropertyName("states")]
    public List<TPStateValue> States { get; set; } = new();
}

public class TPStateValue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

// Remove state message
public class TPRemoveState
{
    [JsonPropertyName("type")]
    public string Type { get; } = "removeState";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

// Create state message
public class TPCreateState
{
    [JsonPropertyName("type")]
    public string Type { get; } = "createState";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = string.Empty;

    [JsonPropertyName("defaultValue")]
    public string DefaultValue { get; set; } = string.Empty;

    [JsonPropertyName("parentGroup")]
    public string? ParentGroup { get; set; }
}

// Bulk create states
public class TPCreateStateMany
{
    [JsonPropertyName("type")]
    public string Type { get; } = "createStateMany";

    [JsonPropertyName("states")]
    public List<TPStateDefinition> States { get; set; } = new();
}

public class TPStateDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = string.Empty;

    [JsonPropertyName("defaultValue")]
    public string DefaultValue { get; set; } = string.Empty;

    [JsonPropertyName("parentGroup")]
    public string? ParentGroup { get; set; }
}

// Notification message
public class TPNotification
{
    [JsonPropertyName("type")]
    public string Type { get; } = "showNotification";

    [JsonPropertyName("notificationId")]
    public string NotificationId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<TPNotificationOption> Options { get; set; } = new();
}

public class TPNotificationOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

// Notification clicked message
public class TPNotificationClickedMessage : TPMessage
{
    [JsonPropertyName("notificationId")]
    public string NotificationId { get; set; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; set; } = string.Empty;
}

