using System.Diagnostics;

namespace Launcher;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            // Get the directory where the launcher is located
            var launcherPath = AppContext.BaseDirectory;
            var mainExePath = Path.Combine(launcherPath, "TouchPortalHardwareMonitor.exe");

            if (!File.Exists(mainExePath))
            {
                Console.WriteLine($"Error: Could not find {mainExePath}");
                return 1;
            }

            Console.WriteLine($"Launching {mainExePath} with elevation...");

            var startInfo = new ProcessStartInfo
            {
                FileName = mainExePath,
                UseShellExecute = true,
                Verb = "runas",  // This triggers UAC elevation
                WorkingDirectory = launcherPath
            };

            var process = Process.Start(startInfo);

            if (process == null)
            {
                Console.WriteLine("Error: Failed to start process");
                return 1;
            }

            Console.WriteLine($"Plugin started with PID {process.Id}");

            // Wait for the elevated process to exit
            // This keeps the launcher alive so Touch Portal knows the plugin is running
            process.WaitForExit();

            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled UAC prompt
            Console.WriteLine("UAC elevation was cancelled by user");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
