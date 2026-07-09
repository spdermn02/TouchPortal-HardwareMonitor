using System.Diagnostics;
using System.Net.Http;

namespace TouchPortalHardwareMonitor.Services;

public enum PawnIoResult
{
    AlreadyInstalled,
    Installed,
    InstallFailed,
    NotElevated
}

/// <summary>
/// Ensures the PawnIO kernel driver is present. LibreHardwareMonitor reads CPU
/// MSR/SMN sensors (temperatures, clocks, voltages) through PawnIO; if it isn't
/// installed those sensors are unavailable. We don't redistribute the installer
/// (it has no declared license) - instead we fetch the official signed installer
/// from namazso's pinned release and run it silently. Requires elevation.
/// </summary>
public sealed class PawnIoService
{
    private const string ServiceName = "PawnIO";
    // Pinned official signed installer (github.com/namazso/PawnIO.Setup).
    private const string InstallerUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";

    public string? LastError { get; private set; }

    /// <summary>True if the PawnIO kernel-mode service is registered.</summary>
    public bool IsInstalled()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<PawnIoResult> EnsureInstalledAsync(bool isElevated, Action<string>? log = null)
    {
        if (IsInstalled())
        {
            return PawnIoResult.AlreadyInstalled;
        }

        if (!isElevated)
        {
            LastError = "Cannot install PawnIO because the plugin is not running as administrator.";
            return PawnIoResult.NotElevated;
        }

        var installer = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
        try
        {
            log?.Invoke($"[PawnIO] downloading official installer from {InstallerUrl}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                var bytes = await http.GetByteArrayAsync(InstallerUrl);
                await File.WriteAllBytesAsync(installer, bytes);
            }

            log?.Invoke("[PawnIO] running installer (-install -silent)...");
            using (var proc = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-install -silent",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                }
            }

            if (IsInstalled())
            {
                return PawnIoResult.Installed;
            }

            LastError = "PawnIO installer finished but the service was not registered.";
            return PawnIoResult.InstallFailed;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return PawnIoResult.InstallFailed;
        }
        finally
        {
            try { File.Delete(installer); } catch { /* best effort */ }
        }
    }
}
