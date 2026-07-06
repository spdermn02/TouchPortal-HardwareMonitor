using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace TouchPortalHardwareMonitor.Services;

/// <summary>
/// FPS via Intel <b>PresentMon</b> (bundled next to the plugin as
/// <c>PresentMon.exe</c>). Runs PresentMon as a child process capturing all
/// processes and parses its CSV stream, computing per-process FPS from
/// <c>msBetweenPresents</c>. PresentMon is the reference implementation for
/// present timing, so this is far more accurate than the in-process ETW
/// counter. Requires elevation (PresentMon uses ETW) — the plugin already runs
/// elevated. If <c>PresentMon.exe</c> isn't present it reports unavailable.
/// </summary>
public sealed class PresentMonFpsProvider : IDisposable
{
    private sealed class Entry
    {
        public double EmaFrametimeMs;
        public long LastTick;
        public string Name = "";
    }

    private const long StaleMs = 2000;

    private readonly string _exePath;
    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    private Process? _proc;
    private int _pidCol = -1;
    private int _msCol = -1;
    private int _appCol = -1;
    private volatile bool _headerParsed;

    public string? LastError { get; private set; }
    public bool Running { get; private set; }

    public PresentMonFpsProvider(string exePath) => _exePath = exePath;

    public static string DefaultExePath() => Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");

    public bool Start()
    {
        if (!File.Exists(_exePath))
        {
            LastError = $"PresentMon.exe not found at {_exePath}";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                // Stream CSV to stdout, capture all processes, suppress the
                // interactive console output, and reclaim any stale session.
                Arguments = "-output_stdout -stop_existing_session -no_top",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? AppContext.BaseDirectory
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += OnOutput;
            _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data; };
            _proc.Exited += (_, _) => Running = false;

            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
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

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrEmpty(line)) return;

        if (!_headerParsed)
        {
            if (line.IndexOf("ProcessID", StringComparison.OrdinalIgnoreCase) < 0) return;

            var cols = line.Split(',');
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i].Trim();
                if (c.Equals("ProcessID", StringComparison.OrdinalIgnoreCase)) _pidCol = i;
                else if (c.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase)) _msCol = i;
                else if (c.Equals("Application", StringComparison.OrdinalIgnoreCase)) _appCol = i;
            }
            _headerParsed = _pidCol >= 0 && _msCol >= 0;
            return;
        }

        var f = line.Split(',');
        if (f.Length <= _pidCol || f.Length <= _msCol) return;
        if (!int.TryParse(f[_pidCol], out var pid)) return;
        if (!double.TryParse(f[_msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return;
        if (ms <= 0 || double.IsNaN(ms) || double.IsInfinity(ms)) return;

        var entry = _entries.GetOrAdd(pid, _ => new Entry());
        // Smooth frametime so the reported FPS isn't jittery frame-to-frame.
        entry.EmaFrametimeMs = entry.EmaFrametimeMs <= 0 ? ms : (entry.EmaFrametimeMs * 0.8) + (ms * 0.2);
        entry.LastTick = Environment.TickCount64;
        if (_appCol >= 0 && f.Length > _appCol && !string.IsNullOrWhiteSpace(f[_appCol]))
        {
            entry.Name = f[_appCol];
        }
    }

    public FpsReading? GetFps(int foregroundPid)
    {
        long now = Environment.TickCount64;

        if (foregroundPid > 0
            && _entries.TryGetValue(foregroundPid, out var fe)
            && now - fe.LastTick <= StaleMs
            && fe.EmaFrametimeMs > 0)
        {
            return new FpsReading((float)(1000.0 / fe.EmaFrametimeMs), NameOf(foregroundPid, fe), "PresentMon");
        }

        // Otherwise the busiest fresh presenter.
        Entry? best = null;
        int bestPid = 0;
        double bestFps = 0;
        foreach (var kv in _entries)
        {
            if (now - kv.Value.LastTick > StaleMs || kv.Value.EmaFrametimeMs <= 0) continue;
            var fps = 1000.0 / kv.Value.EmaFrametimeMs;
            if (fps > bestFps)
            {
                bestFps = fps;
                best = kv.Value;
                bestPid = kv.Key;
            }
        }

        return best != null ? new FpsReading((float)bestFps, NameOf(bestPid, best), "PresentMon") : null;
    }

    private static string NameOf(int pid, Entry e)
    {
        if (!string.IsNullOrEmpty(e.Name) && !e.Name.Equals("<error>", StringComparison.OrdinalIgnoreCase))
        {
            return e.Name;
        }
        try
        {
            using var p = Process.GetProcessById(pid);
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
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() => Stop();
}
