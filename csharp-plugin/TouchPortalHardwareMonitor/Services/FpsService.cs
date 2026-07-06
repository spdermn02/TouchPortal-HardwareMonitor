using System.Runtime.InteropServices;

namespace TouchPortalHardwareMonitor.Services;

public enum FpsMode
{
    Off,
    Rtss,
    Builtin,
    Auto
}

/// <summary>FPS reading for the foreground app.</summary>
public readonly record struct FpsReading(float Fps, string ProcessName, string Source);

/// <summary>
/// Orchestrates the FPS backends. Auto prefers RTSS (no elevation) and falls
/// back to the in-process ETW backend. The ETW session is only started when a
/// mode that needs it is selected.
/// </summary>
public sealed class FpsService : IDisposable
{
    private readonly RtssFpsProvider _rtss = new();
    private EtwFpsProvider? _etw;
    private FpsMode _mode = FpsMode.Auto;

    public string? EtwError { get; private set; }
    public bool EtwRunning => _etw?.Running == true;

    public static FpsMode ParseMode(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => FpsMode.Off,
            "rtss" => FpsMode.Rtss,
            "built-in" or "builtin" => FpsMode.Builtin,
            _ => FpsMode.Auto
        };
    }

    /// <summary>(Re)configure for the selected mode, starting/stopping ETW as needed.</summary>
    public void Configure(FpsMode mode)
    {
        _mode = mode;

        bool needsEtw = mode is FpsMode.Builtin or FpsMode.Auto;
        if (needsEtw && _etw == null)
        {
            _etw = new EtwFpsProvider();
            if (!_etw.Start())
            {
                EtwError = _etw.LastError;
            }
        }
        else if (!needsEtw && _etw != null)
        {
            _etw.Dispose();
            _etw = null;
        }
    }

    public FpsReading? GetForegroundFps()
    {
        if (_mode == FpsMode.Off) return null;

        int pid = GetForegroundPid();

        switch (_mode)
        {
            case FpsMode.Rtss:
                return _rtss.GetFps(pid);

            case FpsMode.Builtin:
                return _etw?.GetFps(pid);

            case FpsMode.Auto:
                if (_rtss.IsAvailable())
                {
                    var r = _rtss.GetFps(pid);
                    if (r != null) return r;
                }
                return _etw?.GetFps(pid);

            default:
                return null;
        }
    }

    private static int GetForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public void Dispose() => _etw?.Dispose();
}
