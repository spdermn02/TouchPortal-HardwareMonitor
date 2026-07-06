using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace TouchPortalHardwareMonitor.Services;

/// <summary>
/// In-process ETW FPS backend. Opens a real-time ETW session and counts DXGI /
/// D3D9 present events per process; a process's FPS is the number of present
/// events seen in the last sampling window. Requires elevation (ETW real-time
/// sessions do). If it can't start (not elevated, blocked, trimmed away), it
/// fails gracefully and callers fall back to RTSS.
///
/// NOTE: The present-event filter here is intentionally simple (event name
/// contains "present"). It has NOT been validated against a broad set of real
/// games/APIs and may need refinement (present model, Vulkan/GL coverage).
/// </summary>
public sealed class EtwFpsProvider : IDisposable
{
    private const string SessionName = "TPHM-FPS-Session";

    private TraceEventSession? _session;
    private Thread? _processThread;
    private System.Threading.Timer? _sampleTimer;

    private readonly ConcurrentDictionary<int, int> _counts = new();
    private readonly ConcurrentDictionary<int, string> _names = new();
    private volatile Dictionary<int, float> _fpsSnapshot = new();
    private DateTime _lastSample = DateTime.UtcNow;

    public string? LastError { get; private set; }
    public bool Running { get; private set; }

    public bool Start()
    {
        try
        {
            // A pre-existing session with this name (e.g. after a crash) blocks
            // us; recreating with the same name stops the stale one.
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider("Microsoft-Windows-DXGI");
            _session.EnableProvider("Microsoft-Windows-D3D9");
            _session.Source.Dynamic.All += OnEvent;

            _processThread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { LastError = ex.Message; }
            })
            {
                IsBackground = true,
                Name = "TPHM-FPS-ETW"
            };
            _processThread.Start();

            _sampleTimer = new System.Threading.Timer(_ => Sample(), null, 1000, 1000);
            Running = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
            return false;
        }
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.ProcessID <= 0) return;

        var name = data.EventName;
        if (name != null && name.IndexOf("present", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _counts.AddOrUpdate(data.ProcessID, 1, (_, c) => c + 1);
            if (!string.IsNullOrEmpty(data.ProcessName))
            {
                _names[data.ProcessID] = data.ProcessName;
            }
        }
    }

    private void Sample()
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _lastSample).TotalSeconds;
        _lastSample = now;
        if (seconds <= 0.001) seconds = 1;

        var snapshot = new Dictionary<int, float>();
        foreach (var pid in _counts.Keys.ToArray())
        {
            if (_counts.TryRemove(pid, out var count) && count > 0)
            {
                snapshot[pid] = (float)(count / seconds);
            }
        }
        _fpsSnapshot = snapshot; // atomic reference swap
    }

    public FpsReading? GetFps(int foregroundPid)
    {
        var snapshot = _fpsSnapshot;

        if (foregroundPid > 0 && snapshot.TryGetValue(foregroundPid, out var fg))
        {
            return new FpsReading(fg, NameFor(foregroundPid), "Built-in");
        }

        // Otherwise the busiest presenting process.
        int bestPid = 0;
        float bestFps = 0;
        foreach (var kv in snapshot)
        {
            if (kv.Value > bestFps)
            {
                bestFps = kv.Value;
                bestPid = kv.Key;
            }
        }

        return bestPid != 0 ? new FpsReading(bestFps, NameFor(bestPid), "Built-in") : null;
    }

    private string NameFor(int pid)
    {
        if (_names.TryGetValue(pid, out var n) && !string.IsNullOrEmpty(n))
        {
            return n;
        }
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return pid.ToString();
        }
    }

    private void Stop()
    {
        Running = false;
        try { _sampleTimer?.Dispose(); } catch { }
        _sampleTimer = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    public void Dispose() => Stop();
}
