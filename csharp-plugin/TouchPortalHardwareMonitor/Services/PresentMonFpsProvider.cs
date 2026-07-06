using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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

    private readonly string _bundledPath; // shipped in the plugin folder; never executed directly
    private readonly string _runPath;     // working copy (outside the plugin folder) we actually launch
    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    private Process? _proc;
    private IntPtr _job = IntPtr.Zero;
    private int _pidCol = -1;
    private int _msCol = -1;
    private int _appCol = -1;
    private volatile bool _headerParsed;

    public string? LastError { get; private set; }
    public bool Running { get; private set; }

    public PresentMonFpsProvider(string bundledExePath)
    {
        _bundledPath = bundledExePath;
        var workingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TouchPortalHardwareMonitor");
        _runPath = Path.Combine(workingDir, "PresentMon.exe");
    }

    public static string DefaultExePath() => Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");

    public bool Start()
    {
        if (!File.Exists(_bundledPath))
        {
            LastError = $"PresentMon.exe not found at {_bundledPath}";
            return false;
        }

        // Run PresentMon from a working copy OUTSIDE the plugin folder. Touch
        // Portal overwrites the plugin folder on re-import and fails if the
        // bundled PresentMon.exe is locked by a running process; keeping the
        // executing copy elsewhere means the plugin-folder file is never locked.
        if (!PrepareWorkingCopy())
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _runPath,
                // Stream CSV to stdout, capture all processes, suppress the
                // interactive console output, and reclaim any stale session.
                Arguments = "-output_stdout -stop_existing_session -no_top",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_runPath) ?? AppContext.BaseDirectory
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += OnOutput;
            _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data; };
            _proc.Exited += (_, _) => Running = false;

            _proc.Start();

            // Tie PresentMon to a kill-on-close job object owned by this
            // process. If the plugin dies for ANY reason - including Touch
            // Portal hard-killing it on stop/re-import (which bypasses our
            // graceful shutdown) - the OS closes the job handle and force-kills
            // PresentMon, so it can't linger and lock its own .exe.
            AssignToKillOnCloseJob(_proc);

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

        // Closing the job handle also force-kills anything still in it.
        if (_job != IntPtr.Zero)
        {
            try { CloseHandle(_job); } catch { }
            _job = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // Refresh the working copy we launch from, killing any leftover copy first.
    private bool PrepareWorkingCopy()
    {
        KillOrphans();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_runPath)!);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Copy(_bundledPath, _runPath, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    // A dying orphan may still hold the file briefly.
                    Thread.Sleep(200);
                }
            }
            // If we couldn't refresh it but a usable copy already exists, run that.
            if (File.Exists(_runPath)) return true;
            LastError = $"Could not prepare PresentMon working copy at {_runPath}";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    // Kill any leftover instance of *our* working-copy PresentMon.exe (matched
    // by full path, so a user's own PresentMon elsewhere is left alone).
    private void KillOrphans()
    {
        try
        {
            var full = Path.GetFullPath(_runPath);
            foreach (var p in Process.GetProcessesByName("PresentMon"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                }
                catch { /* access denied / already gone */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private void AssignToKillOnCloseJob(Process proc)
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)len);
                AssignProcessToJobObject(_job, proc.Handle);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // Best-effort; graceful shutdown + KillOrphans still cover most cases.
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
