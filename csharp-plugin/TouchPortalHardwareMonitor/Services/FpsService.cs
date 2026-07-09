using System.Runtime.InteropServices;

namespace TouchPortalHardwareMonitor.Services;

public enum FpsMode
{
    Off,
    Rtss,
    Builtin,
    PresentMon,
    Auto
}

/// <summary>FPS reading for the foreground app.</summary>
public readonly record struct FpsReading(float Fps, string ProcessName, string Source);

/// <summary>
/// Orchestrates the FPS backends. Auto prefers RTSS (accurate, no elevation),
/// then PresentMon (accurate, bundled exe), then the in-process ETW counter
/// (approximate). Only the backend(s) needed for the selected mode are started.
/// </summary>
public sealed class FpsService : IDisposable
{
    private readonly RtssFpsProvider _rtss = new();
    private EtwFpsProvider? _etw;
    private PresentMonFpsProvider? _pm;
    private FpsMode _mode = FpsMode.Auto;

    public string? EtwError { get; private set; }
    public string? PresentMonError { get; private set; }
    public bool EtwRunning => _etw?.Running == true;
    public bool PresentMonRunning => _pm?.Running == true;
    public bool RtssAvailable => _rtss.IsAvailable();
    public FpsMode Mode => _mode;

    /// <summary>
    /// Optional DEBUG sink shared by the FPS backends. Program wires this to
    /// LogDebug (only active when loglevel.txt = DEBUG).
    /// </summary>
    public static Action<string>? DebugLog { get; set; }

    internal static void Dbg(string message) => DebugLog?.Invoke(message);

    // Processes that present frames but aren't a user-facing "game" - reporting
    // their FPS is misleading (e.g. dwm.exe is the desktop compositor, which
    // presents at the refresh rate whenever you're on the desktop).
    private static readonly HashSet<string> ExcludedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe",
        "dwm"
    };

    internal static bool IsExcludedApp(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name;
        int slash = n.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) n = n[(slash + 1)..];
        return ExcludedApps.Contains(n);
    }

    public static FpsMode ParseMode(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "off" => FpsMode.Off,
            "rtss" => FpsMode.Rtss,
            "built-in" or "builtin" => FpsMode.Builtin,
            "presentmon" or "present-mon" => FpsMode.PresentMon,
            _ => FpsMode.Auto
        };
    }

    /// <summary>(Re)configure for the selected mode, starting/stopping backends as needed.</summary>
    public void Configure(FpsMode mode)
    {
        _mode = mode;
        switch (mode)
        {
            case FpsMode.Off:
            case FpsMode.Rtss:
                StopPresentMon();
                StopEtw();
                break;

            case FpsMode.PresentMon:
                StopEtw();
                EnsurePresentMon();
                break;

            case FpsMode.Builtin:
                StopPresentMon();
                EnsureEtw();
                break;

            case FpsMode.Auto:
                // Prefer PresentMon as the self-contained backend; fall back to
                // the ETW counter only if PresentMon can't run. (RTSS needs no
                // backend started - it's checked at read time.)
                EnsurePresentMon();
                if (_pm?.Running == true)
                {
                    StopEtw();
                }
                else
                {
                    StopPresentMon();
                    EnsureEtw();
                }
                break;
        }

        Dbg($"[FPS] mode={_mode} | RTSS avail={_rtss.IsAvailable()} | " +
            $"PresentMon={(_pm?.Running == true ? "running" : "off")}{(PresentMonError != null ? $" (err: {PresentMonError})" : "")} | " +
            $"ETW={(_etw?.Running == true ? "running" : "off")}{(EtwError != null ? $" (err: {EtwError})" : "")}");
    }

    public FpsReading? GetForegroundFps()
    {
        if (_mode == FpsMode.Off) return null;

        int pid = GetForegroundPid();

        switch (_mode)
        {
            case FpsMode.Rtss:
                return _rtss.GetFps(pid);

            case FpsMode.PresentMon:
                return _pm?.GetFps(pid);

            case FpsMode.Builtin:
                return _etw?.GetFps(pid);

            case FpsMode.Auto:
                if (_rtss.IsAvailable())
                {
                    var r = _rtss.GetFps(pid);
                    if (r != null) return r;
                }
                if (_pm?.Running == true)
                {
                    var r = _pm.GetFps(pid);
                    if (r != null) return r;
                }
                return _etw?.GetFps(pid);

            default:
                return null;
        }
    }

    private void EnsureEtw()
    {
        if (_etw != null) return;
        _etw = new EtwFpsProvider();
        if (!_etw.Start())
        {
            EtwError = _etw.LastError;
            _etw.Dispose();
            _etw = null;
        }
    }

    private void StopEtw()
    {
        _etw?.Dispose();
        _etw = null;
    }

    private void EnsurePresentMon()
    {
        if (_pm != null) return;
        _pm = new PresentMonFpsProvider(PresentMonFpsProvider.DefaultExePath());
        if (!_pm.Start())
        {
            PresentMonError = _pm.LastError;
            _pm.Dispose();
            _pm = null;
        }
    }

    private void StopPresentMon()
    {
        _pm?.Dispose();
        _pm = null;
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

    public void Dispose()
    {
        StopPresentMon();
        StopEtw();
    }
}
