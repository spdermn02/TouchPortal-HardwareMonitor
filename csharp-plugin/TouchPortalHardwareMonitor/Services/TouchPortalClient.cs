using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TouchPortalHardwareMonitor.Models;

namespace TouchPortalHardwareMonitor.Services;

public class TouchPortalClient : IDisposable
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 12136;

    private readonly string _pluginId;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // Static debug flag - set from Program.cs
    public static bool DebugLogging { get; set; } = false;

    public event Action<TPInfoMessage>? OnInfo;
    public event Action<List<Dictionary<string, string>>>? OnSettings;
    public event Action<string, string>? OnNotificationClicked;
    public event Action? OnDisconnected;

    public bool IsConnected => _client?.Connected ?? false;

    private static void LogDebug(string message)
    {
        if (DebugLogging)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [DEBUG] {message}";
            Console.WriteLine(line);
        }
    }

    public TouchPortalClient(string pluginId)
    {
        _pluginId = pluginId;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        LogDebug($"[TP] Connecting to {DefaultHost}:{DefaultPort}...");
        _client = new TcpClient();
        await _client.ConnectAsync(DefaultHost, DefaultPort, cancellationToken);
        LogDebug("[TP] TCP connection established");

        _stream = _client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };

        // Send pair message
        var pairMessage = new TPPairMessage { Id = _pluginId };
        LogDebug($"[TP] Sending pair message with ID: {_pluginId}");
        await SendMessageAsync(pairMessage, cancellationToken);

        // Start listening
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        LogDebug("[TP] Starting message listener...");
        _ = Task.Run(() => ListenAsync(_cts.Token), _cts.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        LogDebug("[TP] Listener started, waiting for messages...");
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader != null)
            {
                LogDebug("[TP] Waiting for next message...");
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    LogDebug("[TP] Received null line, connection closed");
                    break;
                }

                LogDebug($"[TP] Received ({line.Length} chars): {line.Substring(0, Math.Min(200, line.Length))}{(line.Length > 200 ? "..." : "")}");
                await ProcessMessageAsync(line);
            }
        }
        catch (OperationCanceledException)
        {
            LogDebug("[TP] Listener cancelled");
        }
        catch (Exception ex)
        {
            LogDebug($"[TP] Listen error: {ex.Message}");
            LogIt("ERROR", $"Listen error: {ex.Message}");
        }
        finally
        {
            LogDebug("[TP] Listener exiting, invoking OnDisconnected");
            OnDisconnected?.Invoke();
        }
    }

    private async Task ProcessMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            LogDebug($"[TP] Processing message type: {type}");

            switch (type)
            {
                case "info":
                    LogDebug("[TP] Parsing info message...");
                    var infoMessage = JsonSerializer.Deserialize(json, AppJsonContext.Default.TPInfoMessage);
                    if (infoMessage != null)
                    {
                        LogDebug($"[TP] Info parsed, settings count: {infoMessage.Settings.Count}, invoking OnInfo...");
                        OnInfo?.Invoke(infoMessage);
                        // Settings are included in info message
                        if (infoMessage.Settings.Count > 0)
                        {
                            LogDebug("[TP] Invoking OnSettings from info message...");
                            OnSettings?.Invoke(infoMessage.Settings);
                        }
                        else
                        {
                            LogDebug("[TP] No settings in info message");
                        }
                    }
                    else
                    {
                        LogDebug("[TP] Failed to parse info message");
                    }
                    break;

                case "settings":
                    LogDebug("[TP] Parsing settings message...");
                    var settingsMessage = JsonSerializer.Deserialize(json, AppJsonContext.Default.TPSettingsMessage);
                    if (settingsMessage != null)
                    {
                        LogDebug($"[TP] Settings parsed, count: {settingsMessage.Values.Count}, invoking OnSettings...");
                        OnSettings?.Invoke(settingsMessage.Values);
                    }
                    break;

                case "notificationOptionClicked":
                    var notifMessage = JsonSerializer.Deserialize(json, AppJsonContext.Default.TPNotificationClickedMessage);
                    if (notifMessage != null)
                    {
                        OnNotificationClicked?.Invoke(notifMessage.NotificationId, notifMessage.OptionId);
                    }
                    break;

                case "closePlugin":
                    LogIt("INFO", "Received closePlugin message, shutting down");
                    Environment.Exit(0);
                    break;

                default:
                    LogDebug($"[TP] Unhandled message type: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[TP] Error processing message: {ex.Message}");
            LogIt("ERROR", $"Error processing message: {ex.Message}");
        }
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        if (_writer == null) return;

        var json = JsonSerializer.Serialize(message, typeof(T), AppJsonContext.Default);
        LogDebug($"[TP] Sending: {json}\\n");
        await _writer.WriteAsync(json + "\n");
        await _writer.FlushAsync(cancellationToken);
    }

    public void StateUpdate(string id, string value)
    {
        var message = new TPStateUpdate { Id = id, Value = value };
        _ = SendMessageAsync(message);
    }

    public void StateUpdateMany(List<TPStateValue> states)
    {
        if (states.Count == 0) return;

        _ = SendBatchedUpdatesAsync(states);
    }

    private async Task SendBatchedUpdatesAsync(List<TPStateValue> states)
    {
        if (_writer == null) return;

        var sb = new StringBuilder();
        foreach (var state in states)
        {
            var message = new TPStateUpdate { Id = state.Id, Value = state.Value };
            var json = JsonSerializer.Serialize(message, AppJsonContext.Default.TPStateUpdate);
            LogDebug($"[TP] -> {json}");
            sb.Append(json);
            sb.Append('\n');
        }

        var batch = sb.ToString();
        await _writer.WriteAsync(batch);
        await _writer.FlushAsync();
    }

    public void CreateState(string id, string desc, string defaultValue, string? parentGroup = null)
    {
        var message = new TPCreateState
        {
            Id = id,
            Desc = desc,
            DefaultValue = defaultValue,
            ParentGroup = parentGroup
        };
        _ = SendMessageAsync(message);
    }

    public async Task CreateStateManyAsync(List<TPStateDefinition> states)
    {
        if (states.Count == 0) return;

        await SendBatchedCreatesAsync(states);
    }

    private async Task SendBatchedCreatesAsync(List<TPStateDefinition> states)
    {
        if (_stream == null)
        {
            LogDebug($"[TP] ERROR: _stream is null, cannot send createState messages!");
            return;
        }

        LogDebug($"[TP] -> Processing {states.Count} states (remove then create)...");

        // First, send removeState for all states to clean up any stale references
        // This handles the case where the plugin is reimported without TP restart
        var removeSb = new StringBuilder();
        foreach (var state in states)
        {
            var removeMsg = new TPRemoveState { Id = state.Id };
            var json = JsonSerializer.Serialize(removeMsg, AppJsonContext.Default.TPRemoveState);
            removeSb.Append(json);
            removeSb.Append('\n');
        }

        var removeBatch = removeSb.ToString();
        LogDebug($"[TP] -> Sending {states.Count} removeState messages first...");

        try
        {
            var removeBytes = Encoding.UTF8.GetBytes(removeBatch);
            await _stream.WriteAsync(removeBytes);
            await _stream.FlushAsync();
            LogDebug($"[TP] -> Sent removeState batch ({removeBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            LogDebug($"[TP] -> ERROR sending removeState batch: {ex.Message}");
        }

        // Small delay to let TP process the removes
        await Task.Delay(100);

        // Now send createState for all states
        var createSb = new StringBuilder();
        foreach (var state in states)
        {
            var message = new TPCreateState
            {
                Id = state.Id,
                Desc = state.Desc,
                DefaultValue = state.DefaultValue,
                ParentGroup = state.ParentGroup
            };
            var json = JsonSerializer.Serialize(message, AppJsonContext.Default.TPCreateState);
            createSb.Append(json);
            createSb.Append('\n');
        }

        var createBatch = createSb.ToString();
        LogDebug($"[TP] -> Sending {states.Count} createState messages...");

        try
        {
            var createBytes = Encoding.UTF8.GetBytes(createBatch);
            await _stream.WriteAsync(createBytes);
            await _stream.FlushAsync();
            LogDebug($"[TP] -> SUCCESS: Sent {states.Count} createState messages ({createBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            LogDebug($"[TP] -> ERROR sending createState batch: {ex.Message}");
        }
    }

    public void SendNotification(string notificationId, string title, string message, List<TPNotificationOption>? options = null)
    {
        var notification = new TPNotification
        {
            NotificationId = notificationId,
            Title = title,
            Message = message,
            Options = options ?? new List<TPNotificationOption>()
        };
        _ = SendMessageAsync(notification);
    }

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "plugin.log");
    private static readonly object LogLock = new();

    public void LogIt(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        Console.WriteLine(line);

        // Also write to log file
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch { /* Ignore file write errors */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _writer?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
    }
}
