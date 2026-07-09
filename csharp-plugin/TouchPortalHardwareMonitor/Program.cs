using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TouchPortalHardwareMonitor.Models;
using TouchPortalHardwareMonitor.Services;

namespace TouchPortalHardwareMonitor;

class Program
{
    // Config folder in AppData (persists across plugin reinstalls)
    private static readonly string ConfigFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TouchPortalHardwareMonitor");
    private static readonly string HardwareMappingFile = Path.Combine(ConfigFolder, "hardware_mapping.json");
    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "plugin.log");
    private static readonly string LogLevelFile = Path.Combine(AppContext.BaseDirectory, "loglevel.txt");
    private static readonly string DumpTriggerFile = Path.Combine(AppContext.BaseDirectory, "dump_sensors.txt");
    private static readonly string DumpOutputFile = Path.Combine(AppContext.BaseDirectory, "hardware_dump.json");
    private static readonly object LogLock = new();

    // Log level - read from loglevel.txt at startup
    private static bool _debugLogging = false;

    // Whether the process is running elevated (administrator). The
    // LibreHardwareMonitor kernel driver (WinRing0) only loads when elevated.
    private static bool _isElevated = false;

    // Persistent hardware identifier to index mapping
    private static Dictionary<string, HardwareMapping> _hardwareMapping = new();

    private static void InitializeLogLevel()
    {
        try
        {
            if (File.Exists(LogLevelFile))
            {
                var level = File.ReadAllText(LogLevelFile).Trim().ToUpperInvariant();
                _debugLogging = level == "DEBUG";
                Log($"Log level set to: {(_debugLogging ? "DEBUG" : "INFO")}");
            }
            else
            {
                Log("No loglevel.txt found, using INFO level. Create loglevel.txt with 'DEBUG' to enable verbose logging.");
            }
        }
        catch (Exception ex)
        {
            Log($"Error reading log level: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch { /* Ignore file write errors */ }
    }

    private static void LogDebug(string message)
    {
        if (_debugLogging)
        {
            Log($"[DEBUG] {message}");
        }
    }

    private static bool _dumpRequested = false;

    private static void CheckDumpTrigger()
    {
        _dumpRequested = File.Exists(DumpTriggerFile);
        if (_dumpRequested)
        {
            Log("Sensor dump requested — dump_sensors.txt found. Will write hardware_dump.json after first capture.");
        }
    }

    private static void WriteDiagnosticDump(
        List<HardwareItem> rawHardwareData,
        List<SensorItem> rawSensorData,
        HashSet<string> hardwareWithSensors,
        HashSet<string> activeNetworkAdapters)
    {
        try
        {
            var dump = new DiagnosticDump
            {
                Timestamp = DateTime.Now.ToString("o"),
                PluginVersion = "2.2.1",
                IsElevated = _isElevated,
                SensorAccess = DetectSensorAccessStatus(rawSensorData).Message,
                Settings = new DiagnosticSettings
                {
                    CaptureInterval = _captureInterval,
                    TempUnit = _tempUnit,
                    NormalizeThroughput = _normalizeThroughput,
                    NormalizeData = _normalizeData,
                    DebugLogging = _debugLogging
                },
                HardwareMapping = _hardwareMapping.Values.ToList(),
                ActiveNetworkAdapters = activeNetworkAdapters.ToList()
            };

            // Build sensor counts per hardware
            var sensorCountByParent = rawSensorData
                .GroupBy(s => s.Parent)
                .ToDictionary(g => g.Key, g => g.Count());

            // Hardware entries with inclusion/skip reasons
            foreach (var hw in rawHardwareData)
            {
                var entry = new DiagnosticHardware
                {
                    Identifier = hw.Identifier,
                    Name = hw.Name,
                    HardwareType = hw.HardwareType,
                    SensorCount = sensorCountByParent.GetValueOrDefault(hw.Identifier, 0)
                };

                if (!hardwareWithSensors.Contains(hw.Identifier))
                {
                    entry.Included = false;
                    entry.SkipReason = "No sensors reported by LibreHardwareMonitor";
                }
                else if (hw.HardwareType.Equals("Network", StringComparison.OrdinalIgnoreCase) && IsNetworkFilterDriver(hw.Name))
                {
                    entry.Included = false;
                    entry.SkipReason = "Network filter driver";
                }
                else
                {
                    entry.Included = true;
                    var normalizedType = NormalizeHardwareType(hw.HardwareType);
                    entry.NormalizedType = normalizedType;
                    if (_hardwareMapping.TryGetValue(hw.Identifier, out var mapping))
                    {
                        entry.AssignedIndex = mapping.AssignedIndex;
                    }
                }

                dump.Hardware.Add(entry);
            }

            // Sensor entries with raw + converted values
            foreach (var sensor in rawSensorData)
            {
                var matched = _hardware.ContainsKey(sensor.Parent);
                var diagSensor = new DiagnosticSensor
                {
                    Parent = sensor.Parent,
                    Identifier = sensor.Identifier,
                    Name = sensor.Name,
                    SensorType = sensor.SensorType,
                    RawValue = sensor.Value,
                    ValuePresent = sensor.ValuePresent,
                    Min = sensor.Min,
                    Max = sensor.Max,
                    Matched = matched
                };

                // Look up parent name
                var parentHw = rawHardwareData.FirstOrDefault(h => h.Identifier == sensor.Parent);
                if (parentHw != null)
                {
                    diagSensor.ParentName = parentHw.Name;
                }

                // Compute what the converted value and unit would be
                if (matched)
                {
                    var clone = new SensorItem
                    {
                        Parent = sensor.Parent,
                        Identifier = sensor.Identifier,
                        Name = sensor.Name,
                        SensorType = sensor.SensorType,
                        Value = sensor.Value,
                        Min = sensor.Min,
                        Max = sensor.Max
                    };
                    RunSensorConversions(clone);
                    diagSensor.ConvertedValue = clone.Value;
                    diagSensor.Unit = clone.Unit;

                    var hw = _hardware[sensor.Parent];
                    var stateInfo = BuildSensorStateId(sensor.Parent, clone);
                    diagSensor.StateId = stateInfo.Id;
                }
                else
                {
                    // Still show what unit would be
                    if (SensorTypeUnits.TryGetValue(sensor.SensorType, out var unit))
                    {
                        diagSensor.Unit = unit;
                    }
                }

                dump.Sensors.Add(diagSensor);
            }

            using (var stream = File.Create(DumpOutputFile))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, dump, AppJsonContext.Default.DiagnosticDump);
            }

            Log($"Diagnostic dump written to {DumpOutputFile} ({dump.Hardware.Count} hardware, {dump.Sensors.Count} sensors)");

            // Remove the trigger file so it doesn't dump every restart
            try { File.Delete(DumpTriggerFile); }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            Log($"Error writing diagnostic dump: {ex.Message}");
        }
    }

    private const string PluginId = "TP_HM";
    private const string UpdateUrl = "https://raw.githubusercontent.com/spdermn02/TouchPortal-HardwareMonitor/main/package.json";
    private const string ReleaseUrl = "https://github.com/spdermn02/TouchPortal-HardwareMonitor/releases";
    private const int MaxWaitTime = 60000;
    private const int StartCaptureWaitTime = 2000;

    private static TouchPortalClient? _tpClient;
    private static HardwareMonitorService? _hwService;
    private static readonly Dictionary<string, HardwareItem> _hardware = new();

    private static int _captureInterval = 2000;
    private static string _tempUnit = "C";
    private static string _normalizeThroughput = "No";
    private static string _normalizeData = "No";
    private static int _waitTime = 1000;
    private static Timer? _captureTimer;
    private static bool _isCapturing = false;
    private static int _isShuttingDown = 0;

    // Default units for each sensor type (matching LibreHardwareMonitor)
    // Note: Using ASCII-safe characters to avoid encoding issues with Touch Portal
    private static readonly Dictionary<string, string> SensorTypeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Voltage", "V" },
        { "Current", "A" },
        { "Power", "W" },
        { "Clock", "MHz" },
        { "Temperature", "C" },
        { "Load", "%" },
        { "Frequency", "Hz" },
        { "Fan", "RPM" },
        { "Flow", "L/h" },
        { "Control", "%" },
        { "Level", "%" },
        { "Factor", "" },
        { "Data", "GB" },
        { "SmallData", "MB" },
        { "Throughput", "B/s" },
        { "TimeSpan", "s" },
        { "Timing", "ns" },
        { "Energy", "mWh" },
        { "Noise", "dBA" },
        { "Conductivity", "uS/cm" },
        { "Humidity", "%" }
    };

    private static bool IsProcessElevated()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // Infers whether the LibreHardwareMonitor kernel driver loaded by checking
    // for readable CPU temperature. CPU temp/clock/voltage and motherboard
    // sensors come from MSR/SuperIO reads that require the driver; GPU (NVAPI),
    // storage (SMART) and OS load/memory do not. A CPU with no readable
    // temperature is the reliable signal that the driver did not load.
    private static (bool Healthy, string Message) DetectSensorAccessStatus(List<SensorItem> sensors)
    {
        bool cpuPresent = _hardware.Values.Any(h => h.HardwareType == "CPU")
            || _hardwareMapping.Values.Any(m => m.HardwareType.Equals("CPU", StringComparison.OrdinalIgnoreCase));

        if (!cpuPresent)
        {
            return (true, "OK (no CPU detected to verify)");
        }

        bool cpuHasTemperature = sensors.Any(s =>
            s.SensorType == "Temperature"
            && s.ValuePresent
            && s.Parent.Contains("cpu", StringComparison.OrdinalIgnoreCase));

        if (cpuHasTemperature)
        {
            return (true, "OK - CPU sensors accessible");
        }

        var message = _isElevated
            ? "CPU temperature/clock/voltage and motherboard sensors are unavailable even though the plugin is running as administrator. "
              + "The LibreHardwareMonitor kernel driver (WinRing0) was blocked from loading - most likely Windows Core Isolation > Memory Integrity is ON, "
              + "or antivirus / another monitoring app (HWiNFO, MSI Afterburner, Armoury Crate, OpenRGB, Ryzen Master) is holding the driver."
            : "CPU temperature/clock/voltage and motherboard sensors are unavailable because the plugin is NOT running as administrator, "
              + "so the LibreHardwareMonitor kernel driver could not load. Accept the UAC prompt when the plugin starts, or run Touch Portal as administrator.";
        return (false, message);
    }

    private static void LoadHardwareMapping()
    {
        try
        {
            if (File.Exists(HardwareMappingFile))
            {
                var json = File.ReadAllText(HardwareMappingFile);
                var mappings = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListHardwareMapping);
                if (mappings != null)
                {
                    _hardwareMapping = mappings.ToDictionary(m => m.Identifier);
                    Log($"Loaded {_hardwareMapping.Count} hardware mappings from {HardwareMappingFile}");
                }
            }
            else
            {
                Log("No existing hardware mapping file found, will create on first run");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading hardware mapping: {ex.Message}");
            _hardwareMapping = new Dictionary<string, HardwareMapping>();
        }
    }

    private static void SaveHardwareMapping()
    {
        try
        {
            // Ensure config folder exists
            if (!Directory.Exists(ConfigFolder))
            {
                Directory.CreateDirectory(ConfigFolder);
                Log($"Created config folder: {ConfigFolder}");
            }

            var mappings = _hardwareMapping.Values.ToList();
            var json = JsonSerializer.Serialize(mappings, AppJsonContext.Default.ListHardwareMapping);
            File.WriteAllText(HardwareMappingFile, json);
            Log($"Saved {mappings.Count} hardware mappings to {HardwareMappingFile}");
        }
        catch (Exception ex)
        {
            Log($"Error saving hardware mapping: {ex.Message}");
        }
    }

    private static int GetOrAssignHardwareIndex(string identifier, string name, string hardwareType)
    {
        // Check if we already have a persistent mapping for this hardware
        if (_hardwareMapping.TryGetValue(identifier, out var existingMapping))
        {
            Log($"Using persistent index {existingMapping.AssignedIndex} for {name} ({hardwareType})");
            return existingMapping.AssignedIndex;
        }

        // Find the next available index for this hardware type
        var existingIndicesForType = _hardwareMapping.Values
            .Where(m => m.HardwareType.Equals(hardwareType, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.AssignedIndex)
            .ToList();

        int nextIndex = 1;
        while (existingIndicesForType.Contains(nextIndex))
        {
            nextIndex++;
        }

        // Create and store the new mapping
        var newMapping = new HardwareMapping
        {
            Identifier = identifier,
            Name = name,
            HardwareType = hardwareType,
            AssignedIndex = nextIndex
        };
        _hardwareMapping[identifier] = newMapping;

        Log($"Assigned new persistent index {nextIndex} to {name} ({hardwareType})");

        // Save the updated mapping
        SaveHardwareMapping();

        return nextIndex;
    }

    static async Task Main(string[] args)
    {
        // Quick standalone probe: list displays and exit (no TP connection).
        if (args.Contains("--displays"))
        {
            foreach (var d in DisplayInfoService.GetDisplays())
            {
                Console.WriteLine($"Display {d.Index}{(d.IsPrimary ? " (Primary)" : "")}: {d.Width}x{d.Height} @ {d.RefreshRateHz} Hz - {d.Name}");
            }
            return;
        }

        // Quick standalone probe: sample FPS for ~10s and exit. Run elevated
        // with a game in the foreground to test the ETW backend.
        if (args.Contains("--fps"))
        {
            _isElevated = IsProcessElevated();
            Console.WriteLine($"Elevated: {_isElevated}");
            FpsService.DebugLog = Console.WriteLine;   // verbose backend logging for the probe
            using var probe = new FpsService();
            probe.Configure(FpsMode.Auto);
            if (probe.EtwError != null) Console.WriteLine($"ETW backend error: {probe.EtwError}");
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                var r = probe.GetForegroundFps();
                Console.WriteLine(r.HasValue
                    ? $"FPS={r.Value.Fps:F1}  source={r.Value.Source}  app={r.Value.ProcessName}"
                    : "FPS=(none - no presenting app / backend unavailable)");
            }
            return;
        }

        Log("Touch Portal Hardware Monitor v2.2.1 starting...");

        // Initialize log level from file (before anything else)
        InitializeLogLevel();

        // Record elevation - CPU/motherboard sensors depend on it
        _isElevated = IsProcessElevated();
        Log($"Running as administrator (elevated): {_isElevated}");
        if (!_isElevated)
        {
            Log("WARNING: Not elevated. CPU temperature/clock/voltage and motherboard sensors will be unavailable because the LibreHardwareMonitor kernel driver cannot load.");
        }

        // Check if a sensor dump was requested
        CheckDumpTrigger();

        // Set debug flag for TouchPortalClient
        TouchPortalClient.DebugLogging = _debugLogging;

        // Handle shutdown gracefully
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Shutdown();
        };

        try
        {
            // Load persistent hardware mapping
            LoadHardwareMapping();

            // Initialize hardware monitor service
            Log("Initializing LibreHardwareMonitor...");
            _hwService = new HardwareMonitorService();
            Log("LibreHardwareMonitor initialized successfully");

            // Initialize FPS reporting (Auto by default; a setting may change it)
            _fpsService = new FpsService();
            FpsService.DebugLog = LogDebug;   // DEBUG-gated backend logging
            _fpsService.Configure(_fpsMode);
            if (_fpsService.PresentMonError != null)
            {
                Log($"FPS: PresentMon backend unavailable ({_fpsService.PresentMonError}).");
            }
            if (_fpsService.EtwError != null)
            {
                Log($"FPS: in-process ETW backend unavailable ({_fpsService.EtwError}). RTSS will be used if it is running.");
            }

            // Initialize Touch Portal client
            _tpClient = new TouchPortalClient(PluginId);
            _tpClient.OnInfo += HandleInfo;
            _tpClient.OnSettings += HandleSettings;
            _tpClient.OnNotificationClicked += HandleNotificationClicked;
            _tpClient.OnDisconnected += () =>
            {
                Log("Disconnected from Touch Portal");
                // Only shutdown if we didn't initiate the disconnect
                if (_isShuttingDown == 0)
                {
                    Shutdown();
                }
            };

            Log("Connecting to Touch Portal...");
            await _tpClient.ConnectAsync();
            Log("Connected to Touch Portal");

            // Keep running
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Log($"Fatal error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void HandleInfo(TPInfoMessage info)
    {
        LogDebug( "Received info from Touch Portal");
        LogDebug( $"SDK Version: {info.SdkVersion}, TP Version: {info.TpVersionString}");
        LogDebug( $"Plugin Version: {info.PluginVersion}");
        LogDebug( $"Settings count in info: {info.Settings.Count}");

        foreach (var setting in info.Settings)
        {
            foreach (var kvp in setting)
            {
                LogDebug( $"  Info Setting: {kvp.Key} = {kvp.Value}");
            }
        }
    }

    private static void HandleSettings(List<Dictionary<string, string>> settings)
    {
        LogDebug($"[Settings] HandleSettings called with {settings.Count} setting groups");
        LogDebug( "Received settings from Touch Portal");

        // Remember the interval so we can re-arm the timer live if it changes
        var previousInterval = _captureInterval;

        foreach (var setting in settings)
        {
            foreach (var kvp in setting)
            {
                LogDebug($"[Settings] Processing: {kvp.Key} = {kvp.Value}");
                LogDebug( $"Setting: {kvp.Key} = {kvp.Value}");

                switch (kvp.Key)
                {
                    case "Sensor Capture Time (ms)":
                        if (int.TryParse(kvp.Value, out var interval))
                        {
                            _captureInterval = interval;
                            LogDebug($"[Settings] Capture interval set to: {_captureInterval}");
                        }
                        break;
                    case "Temperature Unit (C/F)":
                        _tempUnit = kvp.Value;
                        break;
                    case "Normalize Throughput (B/s, KB/s, MB/s, GB/s)":
                        _normalizeThroughput = kvp.Value;
                        break;
                    case "Normalize Data (MB, GB)":
                        _normalizeData = kvp.Value;
                        break;
                    case "FPS Source (Off/RTSS/PresentMon/Built-in/Auto)":
                        var newMode = FpsService.ParseMode(kvp.Value);
                        if (newMode != _fpsMode)
                        {
                            _fpsMode = newMode;
                            _fpsService?.Configure(_fpsMode);
                            LogDebug($"[Settings] FPS source set to: {_fpsMode}");
                        }
                        break;
                }
            }
        }

        // Start hardware discovery after receiving settings
        LogDebug($"[Settings] _isCapturing = {_isCapturing}");
        if (!_isCapturing)
        {
            LogDebug("[Settings] Starting BuildHardwareList task...");
            _ = Task.Run(BuildHardwareList);
        }
        else
        {
            LogDebug("[Settings] Already capturing, skipping BuildHardwareList");

            // Apply a changed capture interval live, without a plugin restart.
            if (_captureInterval > 0 && _captureInterval != previousInterval && _captureTimer != null)
            {
                _captureTimer.Change(0, _captureInterval);
                Log($"Capture interval changed live: {previousInterval}ms -> {_captureInterval}ms");
            }
        }
    }

    private static void HandleNotificationClicked(string notificationId, string optionId)
    {
        if (optionId == $"{PluginId}_update_notification_go_to_download")
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _tpClient?.LogIt("ERROR", $"Failed to open release URL: {ex.Message}");
            }
        }
    }

    private static async Task BuildHardwareList()
    {
        LogDebug("[Hardware] BuildHardwareList started");
        LogDebug( "Building hardware list...");

        try
        {
            LogDebug("[Hardware] Calling _hwService.GetHardware()...");
            var hardwareData = _hwService?.GetHardware();
            var sensorData = _hwService?.GetSensors();
            LogDebug($"[Hardware] GetHardware returned: {hardwareData?.Count ?? -1} items");
            LogDebug($"[Hardware] GetSensors returned: {sensorData?.Count ?? -1} sensors");

            if (hardwareData == null || hardwareData.Count == 0)
            {
                LogDebug("[Hardware] No hardware data, will retry...");
                _tpClient?.LogIt("ERROR", "No hardware data received, will retry...");
                if (_waitTime <= MaxWaitTime)
                {
                    await Task.Delay(_waitTime);
                    _waitTime += 1000;
                    _ = Task.Run(BuildHardwareList);
                }
                return;
            }

            // Build a set of hardware identifiers that have sensors
            var hardwareWithSensors = new HashSet<string>();
            if (sensorData != null)
            {
                foreach (var sensor in sensorData)
                {
                    hardwareWithSensors.Add(sensor.Parent);
                }
            }
            LogDebug($"[Hardware] {hardwareWithSensors.Count} hardware items have sensors");

            // Get active network adapters (Up status)
            LogDebug("[Hardware] === Active Network Adapters ===");
            var activeNetworkAdapters = GetActiveNetworkAdapters();
            LogDebug($"[Hardware] {activeNetworkAdapters.Count} network adapters are Up");

            // Sort hardware
            hardwareData.Sort((a, b) =>
            {
                var typeCompare = string.Compare(a.HardwareType, b.HardwareType, StringComparison.OrdinalIgnoreCase);
                if (typeCompare != 0) return typeCompare;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            LogDebug( $"Found {hardwareData.Count} hardware items");

            LogDebug("[Hardware] === Hardware Discovery ===");
            foreach (var hw in hardwareData)
            {
                LogDebug($"[Hardware] LHM: Type={hw.HardwareType}, Name={hw.Name}, Id={hw.Identifier}");

                // Skip hardware that has no sensors first
                if (!hardwareWithSensors.Contains(hw.Identifier))
                {
                    LogDebug($"[Hardware] -> SKIPPED (no sensors)");
                    continue;
                }

                // Filter out network filter drivers (but keep all adapters that have sensors)
                if (hw.HardwareType.Equals("Network", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsNetworkFilterDriver(hw.Name))
                    {
                        LogDebug($"[Hardware] -> SKIPPED (network filter driver)");
                        continue;
                    }
                    // Note: We no longer filter by "Up" status - if LHM reports sensors, include it
                }

                var key = hw.Identifier;
                if (!_hardware.ContainsKey(key))
                {
                    var normalizedType = NormalizeHardwareType(hw.HardwareType);

                    // Get or assign a persistent index for this hardware
                    var persistentIndex = GetOrAssignHardwareIndex(hw.Identifier, hw.Name, normalizedType);

                    var item = new HardwareItem
                    {
                        Identifier = hw.Identifier,
                        Name = hw.Name,
                        HardwareType = normalizedType,
                        Index = persistentIndex
                    };

                    LogDebug($"[Hardware] -> Final: {item.HardwareType}{item.Index} = {item.Name}");
                    _hardware[key] = item;
                }
            }
            LogDebug("[Hardware] === End Hardware Discovery ===");

            // Write diagnostic dump if requested
            if (_dumpRequested)
            {
                // Use the diagnostic collection so sensors LHM couldn't read
                // (null values, e.g. CPU temp when the driver didn't load) show
                // up in the dump instead of silently disappearing.
                var diagnosticSensors = _hwService?.GetSensorsForDiagnostics() ?? sensorData!;
                WriteDiagnosticDump(hardwareData, diagnosticSensors, hardwareWithSensors, activeNetworkAdapters);
                _dumpRequested = false;
            }

            LogDebug( $"Waiting {StartCaptureWaitTime}ms before starting capture...");
            await Task.Delay(StartCaptureWaitTime);
            StartCapture();
        }
        catch (Exception ex)
        {
            _tpClient?.LogIt("ERROR", $"Error building hardware list: {ex.Message}");
            if (_waitTime <= MaxWaitTime)
            {
                await Task.Delay(_waitTime);
                _waitTime += 1000;
                _ = Task.Run(BuildHardwareList);
            }
        }
    }

    private static string NormalizeHardwareType(string type)
    {
        // Normalize GPU types
        var normalized = Regex.Replace(type.ToLower(), @"gpu.*", "GPU", RegexOptions.IgnoreCase);
        return normalized.ToUpper();
    }

    private static bool IsNetworkFilterDriver(string name)
    {
        // These are Windows network filter drivers, not real adapters
        // They appear as separate "hardware" in LHM but are just filters attached to real NICs
        var filterPatterns = new[]
        {
            "-QoS Packet Scheduler-",
            "-WFP 802.3 MAC Layer LightWeight Filter-",
            "-WFP Native MAC Layer LightWeight Filter-",
            "-Native WiFi Filter Driver-",
            "-Hyper-V Virtual Switch Extension Filter-",
            "-Virtual Filtering Platform VMSwitch Extension-"
        };

        foreach (var pattern in filterPatterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Filter virtual/inactive adapters that LHM GUI doesn't show
        var virtualPatterns = new[]
        {
            "Local Area Connection*",    // Wi-Fi Direct virtual adapters
            "(Kernel Debugger)",         // Debug adapter
            "vSwitch",                   // Hyper-V internal switches (keep vEthernet)
        };

        foreach (var pattern in virtualPatterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> GetActiveNetworkAdapters()
    {
        var activeAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    activeAdapters.Add(ni.Name);
                    LogDebug($"[Network] Active adapter: {ni.Name} ({ni.NetworkInterfaceType})");
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[Network] Error getting network interfaces: {ex.Message}");
        }

        return activeAdapters;
    }

    private static void StartCapture()
    {
        _captureTimer?.Dispose();
        _isCapturing = true;

        LogDebug( $"Starting sensor capture with interval {_captureInterval}ms");

        _captureTimer = new Timer(async _ => await CaptureAsync(), null, 0, _captureInterval);
    }

    private static bool _firstCapture = true;
    private static DateTime _lastCreateStateTime = DateTime.MinValue;
    private static readonly TimeSpan _createStateCooldown = TimeSpan.FromSeconds(5);

    // Last-sent value per display/FPS state id, for change detection.
    private static readonly Dictionary<string, string> _displayStateCache = new();

    // FPS (frames-per-second) reporting - independent of LibreHardwareMonitor.
    private static FpsService? _fpsService;
    private static FpsMode _fpsMode = FpsMode.Auto;

    private static async Task CaptureAsync()
    {
        try
        {
            // Check if we're still in cooldown after sending createStates
            var timeSinceLastCreate = DateTime.Now - _lastCreateStateTime;
            if (timeSinceLastCreate < _createStateCooldown)
            {
                LogDebug($"[Capture] In cooldown after createState ({timeSinceLastCreate.TotalSeconds:F1}s / {_createStateCooldown.TotalSeconds}s), skipping this cycle...");
                return;
            }

            var sensorData = _hwService?.GetSensors();
            if (sensorData == null || sensorData.Count == 0)
            {
                _tpClient?.LogIt("ERROR", "No sensor data received");
                return;
            }

            // Log sensor type summary on first capture for debugging
            if (_firstCapture)
            {
                var sensorTypeCounts = sensorData.GroupBy(s => s.SensorType)
                    .ToDictionary(g => g.Key, g => g.Count());
                var summary = string.Join(", ", sensorTypeCounts.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value}"));
                _tpClient?.LogIt("INFO", $"Sensor types detected: {summary}");
            }

            var isFirstCapture = _firstCapture;
            _firstCapture = false;

            // On first capture, check whether MSR/SuperIO sensors are accessible
            // (i.e. whether the kernel driver loaded) and warn if not.
            (bool Healthy, string Message) sensorAccess = (true, "OK");
            if (isFirstCapture)
            {
                sensorAccess = DetectSensorAccessStatus(sensorData);
                if (!sensorAccess.Healthy)
                {
                    Log($"[SensorAccess] {sensorAccess.Message}");
                    _tpClient?.LogIt("WARN", sensorAccess.Message);
                }
            }

            var newStates = new List<TPStateDefinition>();
            var stateUpdates = new List<TPStateValue>();

            // Publish a plugin status state users can display on a button so they
            // can self-diagnose missing CPU/motherboard sensors without a dump.
            if (isFirstCapture)
            {
                newStates.Add(new TPStateDefinition
                {
                    Id = "tp-hm.state.plugin.sensor_status",
                    Desc = "TP Hardware Monitor > Plugin > Sensor Access Status",
                    DefaultValue = sensorAccess.Message,
                    ParentGroup = "TP Hardware Monitor"
                });
            }

            foreach (var sensor in sensorData)
            {
                var hardwareKey = sensor.Parent;
                if (!_hardware.ContainsKey(hardwareKey))
                {
                    // Only log skipped sensors on first capture and when debug is enabled
                    if (isFirstCapture && _debugLogging)
                    {
                        LogDebug($"Skipped sensor: {sensor.Name} ({sensor.SensorType}) - hardware not found: {hardwareKey}");
                    }
                    continue;
                }

                // Skip non-finite values (Infinity/NaN). These occur e.g. when
                // LibreHardwareMonitor computes utilization against a 0 link
                // speed. Sending them to Touch Portal shows garbage ("Infinity"/
                // "NaN") that never clears, because the change check below
                // (Math.Abs(existing - NaN) > 0.01) is always false.
                if (!float.IsFinite(sensor.Value))
                {
                    if (isFirstCapture && _debugLogging)
                    {
                        LogDebug($"Skipped sensor with non-finite value: {sensor.Name} ({sensor.SensorType}) on {hardwareKey} = {sensor.Value}");
                    }
                    continue;
                }

                var stateInfo = BuildSensorStateId(hardwareKey, sensor);
                sensor.StateId = stateInfo;

                // Make identifier unique by appending name
                var uniqueId = sensor.Identifier + "/" + sensor.Name.ToLower().Replace(" ", ".");

                // Apply conversions
                RunSensorConversions(sensor);

                // Format value
                var formattedValue = sensor.Value % 1 != 0
                    ? sensor.Value.ToString("F1")
                    : sensor.Value.ToString("F0");

                var hw = _hardware[hardwareKey];

                if (!hw.Sensors.ContainsKey(uniqueId))
                {
                    // New sensor - create state (don't send update, defaultValue handles initial value)
                    sensor.StateId.DefaultValue = formattedValue;
                    hw.Sensors[uniqueId] = sensor;

                    newStates.Add(new TPStateDefinition
                    {
                        Id = stateInfo.Id,
                        Desc = stateInfo.Desc,
                        DefaultValue = formattedValue,
                        ParentGroup = stateInfo.ParentGroup
                    });

                    // NOTE: Don't add to stateUpdates here - createState's defaultValue sets the initial value
                    // Updates will be sent on subsequent captures when the value changes

                    // Add unit state if applicable (defaultValue is the actual unit)
                    if (!string.IsNullOrEmpty(sensor.Unit))
                    {
                        newStates.Add(new TPStateDefinition
                        {
                            Id = stateInfo.Id + ".unit",
                            Desc = stateInfo.Desc + " Unit",
                            DefaultValue = sensor.Unit,
                            ParentGroup = stateInfo.ParentGroup
                        });
                    }

                    // Add min state if available (defaultValue is the actual min)
                    if (sensor.Min.HasValue && float.IsFinite(sensor.Min.Value))
                    {
                        var minValue = sensor.Min.Value % 1 != 0
                            ? sensor.Min.Value.ToString("F1")
                            : sensor.Min.Value.ToString("F0");

                        newStates.Add(new TPStateDefinition
                        {
                            Id = stateInfo.Id + ".min",
                            Desc = stateInfo.Desc + " Min",
                            DefaultValue = minValue,
                            ParentGroup = stateInfo.ParentGroup
                        });
                    }

                    // Add max state if available (defaultValue is the actual max)
                    if (sensor.Max.HasValue && float.IsFinite(sensor.Max.Value))
                    {
                        var maxValue = sensor.Max.Value % 1 != 0
                            ? sensor.Max.Value.ToString("F1")
                            : sensor.Max.Value.ToString("F0");

                        newStates.Add(new TPStateDefinition
                        {
                            Id = stateInfo.Id + ".max",
                            Desc = stateInfo.Desc + " Max",
                            DefaultValue = maxValue,
                            ParentGroup = stateInfo.ParentGroup
                        });
                    }
                }
                else
                {
                    // Existing sensor - update if changed
                    var existing = hw.Sensors[uniqueId];
                    var valueChanged = Math.Abs(existing.Value - sensor.Value) > 0.01f;
                    var minChanged = sensor.Min.HasValue && float.IsFinite(sensor.Min.Value) && (!existing.Min.HasValue || Math.Abs(existing.Min.Value - sensor.Min.Value) > 0.01f);
                    var maxChanged = sensor.Max.HasValue && float.IsFinite(sensor.Max.Value) && (!existing.Max.HasValue || Math.Abs(existing.Max.Value - sensor.Max.Value) > 0.01f);

                    if (valueChanged || minChanged || maxChanged)
                    {
                        hw.Sensors[uniqueId] = sensor;

                        if (valueChanged)
                        {
                            stateUpdates.Add(new TPStateValue
                            {
                                Id = stateInfo.Id,
                                Value = formattedValue
                            });

                            if (!string.IsNullOrEmpty(sensor.Unit))
                            {
                                stateUpdates.Add(new TPStateValue
                                {
                                    Id = stateInfo.Id + ".unit",
                                    Value = sensor.Unit
                                });
                            }
                        }

                        // Update min if changed
                        if (minChanged)
                        {
                            var minValue = sensor.Min!.Value % 1 != 0
                                ? sensor.Min.Value.ToString("F1")
                                : sensor.Min.Value.ToString("F0");

                            stateUpdates.Add(new TPStateValue
                            {
                                Id = stateInfo.Id + ".min",
                                Value = minValue
                            });
                        }

                        // Update max if changed
                        if (maxChanged)
                        {
                            var maxValue = sensor.Max!.Value % 1 != 0
                                ? sensor.Max.Value.ToString("F1")
                                : sensor.Max.Value.ToString("F0");

                            stateUpdates.Add(new TPStateValue
                            {
                                Id = stateInfo.Id + ".max",
                                Value = maxValue
                            });
                        }
                    }
                }
            }

            // Display states (refresh rate / resolution) - not from LibreHardwareMonitor
            AddDisplayStates(newStates, stateUpdates);

            // FPS states (foreground app frame rate) - not from LibreHardwareMonitor
            AddFpsStates(newStates, stateUpdates);

            // Send state creates to Touch Portal
            LogDebug($"[Capture] newStates.Count = {newStates.Count}, stateUpdates.Count = {stateUpdates.Count}");

            if (newStates.Count > 0)
            {
                // IMPORTANT: When creating new states, ONLY send createState messages this cycle.
                // Do NOT send any stateUpdates until cooldown expires to ensure TP has processed all creates.
                LogDebug($"[Capture] Sending {newStates.Count} createState messages (no updates until cooldown)...");
                if (_tpClient != null)
                {
                    await _tpClient.CreateStateManyAsync(newStates);
                    _lastCreateStateTime = DateTime.Now; // Start cooldown
                    LogDebug($"[Capture] createState messages sent. {_createStateCooldown.TotalSeconds}s cooldown started.");
                }
                // Skip stateUpdates - cooldown will block updates until TP has processed all createState messages
                return;
            }

            // Only send updates when no new states were created this cycle
            if (stateUpdates.Count > 0)
            {
                LogDebug($"[Capture] Sending {stateUpdates.Count} stateUpdate messages...");
                _tpClient?.StateUpdateMany(stateUpdates);
            }
        }
        catch (Exception ex)
        {
            _tpClient?.LogIt("ERROR", $"Capture error: {ex.Message}");
        }
    }

    // Display-friendly sensor type names (matching LHM GUI)
    private static readonly Dictionary<string, string> SensorTypeDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "SmallData", "Data" },
        { "Throughput", "Throughput" },
        { "Temperature", "Temperatures" },
        { "Load", "Load" },
        { "Clock", "Clocks" },
        { "Voltage", "Voltages" },
        { "Current", "Currents" },
        { "Power", "Powers" },
        { "Fan", "Fans" },
        { "Flow", "Flows" },
        { "Control", "Controls" },
        { "Level", "Levels" },
        { "Factor", "Factors" },
        { "Data", "Data" },
        { "Frequency", "Frequencies" },
        { "TimeSpan", "Time" },
        { "Timing", "Timings" },
        { "Energy", "Energy" },
        { "Noise", "Noise" },
        { "Conductivity", "Conductivity" },
        { "Humidity", "Humidity" }
    };

    // Build the sensor-name portion of a Touch Portal state ID. TP state IDs
    // must be plain ASCII identifiers, but LibreHardwareMonitor names can carry
    // non-ASCII (e.g. a French cooler's "T° Eau 360" / "Débitmetre"), which TP
    // rejects. Transliterate accents to ASCII and drop remaining non-ASCII, but
    // keep every ASCII character exactly as before so existing state IDs are
    // unchanged (no broken buttons). The original name is kept in the state
    // description elsewhere.
    private static string SanitizeStateSegment(string name)
    {
        var lowered = name.ToLowerInvariant().Replace(" ", ".").Replace("#", "");
        var decomposed = lowered.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            // Drop combining accent marks left by decomposition (é -> e).
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            // Keep ASCII as-is; drop non-ASCII symbols (e.g. the degree sign).
            if (ch <= '\x7F') sb.Append(ch);
        }
        return sb.ToString();
    }

    private static SensorStateInfo BuildSensorStateId(string hardwareKey, SensorItem sensor)
    {
        var hw = _hardware[hardwareKey];
        var sensorType = sensor.SensorType;
        var sensorName = SanitizeStateSegment(sensor.Name);
        var indexNum = hw.Index > 0 ? hw.Index.ToString() : "";

        // State ID remains unchanged for backwards compatibility
        var stateId = $"tp-hm.state.{hw.HardwareType}{indexNum}.{sensorType}.{sensorName}";

        // Get display-friendly sensor type name
        var displaySensorType = SensorTypeDisplayNames.TryGetValue(sensorType, out var displayName)
            ? displayName
            : sensorType;

        // Parent group: Hardware name with type/index suffix for clarity
        var parentGroup = string.IsNullOrEmpty(indexNum)
            ? $"{hw.Name} ({hw.HardwareType})"
            : $"{hw.Name} ({hw.HardwareType} {indexNum})";

        // Description: Matches LHM tree hierarchy format
        // Format: "Hardware Name > Sensor Category > Sensor Name"
        var desc = $"{hw.Name} > {displaySensorType} > {sensor.Name}";

        return new SensorStateInfo
        {
            Id = stateId,
            Desc = desc,
            DefaultValue = "0",
            ParentGroup = parentGroup
        };
    }

    private static void RunSensorConversions(SensorItem sensor)
    {
        // Always set default unit first based on sensor type
        if (SensorTypeUnits.TryGetValue(sensor.SensorType, out var defaultUnit))
        {
            sensor.Unit = defaultUnit;
        }

        // Temperature conversion
        if (sensor.SensorType == "Temperature")
        {
            if (_tempUnit.Equals("F", StringComparison.OrdinalIgnoreCase))
            {
                sensor.Value = (sensor.Value * 9.0f / 5.0f) + 32.0f;
                sensor.Unit = "F";  // Override unit for Fahrenheit
            }
        }
        // Throughput normalization (only if enabled, otherwise keeps default B/s)
        else if (sensor.SensorType == "Throughput" && _normalizeThroughput.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            var value = sensor.Value;
            var count = 0;
            while (value > 1024.0f && count < 4)
            {
                value /= 1024.0f;
                count++;
            }
            sensor.Value = value;
            sensor.Unit = GetThroughputUnit(count);  // Override with normalized unit
        }
        // Data normalization (only if enabled, otherwise keeps default MB)
        else if (sensor.SensorType == "SmallData" && _normalizeData.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            var value = sensor.Value;
            var count = 2; // LHM reads these as MB minimum
            while (value > 1024.0f && count < 4)
            {
                value /= 1024.0f;
                count++;
            }
            sensor.Value = value;
            sensor.Unit = GetDataUnit(count);  // Override with normalized unit
        }
    }

    private static string GetDataUnit(int count)
    {
        return count switch
        {
            3 => "GB",
            2 => "MB",
            1 => "KB",
            _ => "B"
        };
    }

    private static string GetThroughputUnit(int count)
    {
        return GetDataUnit(count) + "/s";
    }

    // Build/refresh display states (refresh rate, resolution, name) sourced
    // from Win32 - independent of LibreHardwareMonitor. New displays create
    // states; changed values (e.g. a mode switch) push updates.
    private static void AddDisplayStates(List<TPStateDefinition> newStates, List<TPStateValue> stateUpdates)
    {
        List<DisplayInfo> displays;
        try
        {
            displays = DisplayInfoService.GetDisplays();
        }
        catch (Exception ex)
        {
            LogDebug($"[Display] Failed to enumerate displays: {ex.Message}");
            return;
        }

        const string group = "Displays";

        foreach (var d in displays)
        {
            var label = d.IsPrimary ? $"Display {d.Index} (Primary)" : $"Display {d.Index}";
            var baseId = $"tp-hm.state.display.{d.Index}";

            UpsertDisplayState(newStates, stateUpdates, $"{baseId}.refresh_rate", $"{group} > {label} > Refresh Rate (Hz)", group, d.RefreshRateHz.ToString());
            UpsertDisplayState(newStates, stateUpdates, $"{baseId}.refresh_rate.unit", $"{group} > {label} > Refresh Rate Unit", group, "Hz");
            UpsertDisplayState(newStates, stateUpdates, $"{baseId}.resolution", $"{group} > {label} > Resolution", group, $"{d.Width}x{d.Height}");
            UpsertDisplayState(newStates, stateUpdates, $"{baseId}.name", $"{group} > {label} > Name", group, d.Name);
            UpsertDisplayState(newStates, stateUpdates, $"{baseId}.primary", $"{group} > {label} > Is Primary", group, d.IsPrimary ? "true" : "false");
        }

        UpsertDisplayState(newStates, stateUpdates, "tp-hm.state.display.count", $"{group} > Display Count", group, displays.Count.ToString());
    }

    // Build/refresh FPS states for the foreground app. Uses the configured
    // FPS source (RTSS / in-process ETW / Auto); independent of LibreHardwareMonitor.
    private static void AddFpsStates(List<TPStateDefinition> newStates, List<TPStateValue> stateUpdates)
    {
        if (_fpsService == null || _fpsMode == FpsMode.Off)
        {
            return;
        }

        FpsReading? reading;
        try
        {
            reading = _fpsService.GetForegroundFps();
        }
        catch (Exception ex)
        {
            LogDebug($"[FPS] read failed: {ex.Message}");
            return;
        }

        if (_debugLogging)
        {
            LogDebug(reading.HasValue
                ? $"[FPS] value={reading.Value.Fps:F1} source={reading.Value.Source} app={reading.Value.ProcessName}"
                : $"[FPS] no reading (mode={_fpsMode}, rtssAvail={_fpsService.RtssAvailable}, pmRunning={_fpsService.PresentMonRunning}, etwRunning={_fpsService.EtwRunning})");
        }

        const string group = "FPS";
        var value = reading.HasValue ? Math.Round(reading.Value.Fps).ToString("F0") : "0";
        var app = reading?.ProcessName ?? "";
        var source = reading?.Source ?? "";

        UpsertDisplayState(newStates, stateUpdates, "tp-hm.state.fps.value", $"{group} > Current FPS", group, value);
        UpsertDisplayState(newStates, stateUpdates, "tp-hm.state.fps.unit", $"{group} > Current FPS Unit", group, "FPS");
        UpsertDisplayState(newStates, stateUpdates, "tp-hm.state.fps.process", $"{group} > Foreground App", group, app);
        UpsertDisplayState(newStates, stateUpdates, "tp-hm.state.fps.source", $"{group} > FPS Source", group, source);
    }

    // Upsert helper shared by display and FPS states: create the state the
    // first time an id is seen, then push an update only when its value changes.
    private static void UpsertDisplayState(List<TPStateDefinition> newStates, List<TPStateValue> stateUpdates,
        string id, string desc, string group, string value)
    {
        if (!_displayStateCache.TryGetValue(id, out var existing))
        {
            _displayStateCache[id] = value;
            newStates.Add(new TPStateDefinition
            {
                Id = id,
                Desc = desc,
                DefaultValue = value,
                ParentGroup = group
            });
        }
        else if (existing != value)
        {
            _displayStateCache[id] = value;
            stateUpdates.Add(new TPStateValue { Id = id, Value = value });
        }
    }

    private static void Shutdown()
    {
        // Ensure shutdown only runs once
        if (Interlocked.CompareExchange(ref _isShuttingDown, 1, 0) != 0)
        {
            return;
        }

        Log("Shutting down...");

        _captureTimer?.Dispose();
        _captureTimer = null;

        _hwService?.Dispose();
        _hwService = null;

        _fpsService?.Dispose();
        _fpsService = null;

        _tpClient?.Dispose();
        _tpClient = null;

        Environment.Exit(0);
    }
}
