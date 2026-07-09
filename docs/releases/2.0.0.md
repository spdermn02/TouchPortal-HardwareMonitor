# Touch Portal Hardware Monitor 2.0.0 — Pre-Release (Full Rebuild)

> [!WARNING]
> **This is a pre-release and a complete rewrite. It is NOT compatible with v1.x.**
> Version 2.0.0 replaces the Node.js plugin with a brand-new, self-contained C# / .NET application. Existing v1.x buttons, states, and setup **will not carry over cleanly** — please read the Breaking Changes before installing.

## What this is

The plugin has been rebuilt from the ground up in C# (.NET 10) and now embeds **LibreHardwareMonitor** directly. You no longer need to install or run LibreHardwareMonitor (or OpenHardwareMonitor) separately — the plugin reads your hardware itself, in-process.

It ships as a single self-contained executable (no Node.js runtime, no external dependencies) and launches elevated so it can read every available sensor.

## ✨ Highlights

- **Self-contained** — one trimmed, single-file `.exe`; nothing else to install. No separate hardware-monitor app required.
- **LibreHardwareMonitor built in** (LibreHardwareMonitorLib 0.9.5) — sensors are read directly instead of over WMI.
- **Runs elevated** — a small launcher requests UAC elevation so all sensors (temps, fans, voltages, etc.) are visible.
- **Richer states** — every sensor now also exposes companion states:
  - `….unit` — the unit is **always** available now (°C/°F, %, W, MHz, RPM, GB, …), not just when normalization is on
  - `….min` — minimum observed value
  - `….max` — maximum observed value
- **Stable hardware numbering** — each piece of hardware gets a persistent index saved to `%AppData%\TouchPortalHardwareMonitor`, so `CPU1`, `GPU1`, `Network1`, etc. stay pointing at the same device across restarts and reinstalls.
- **Cleaner device list** — Windows network filter drivers and virtual/inactive adapters are filtered out; only hardware that actually reports sensors is published.
- **Built-in diagnostics** — drop a `dump_sensors.txt` next to the plugin to get a full `hardware_dump.json` (raw + converted values, units, assigned indices, skip reasons); set `loglevel.txt` to `DEBUG` for verbose logging.

## 💥 Breaking Changes (read before upgrading)

- **Not an in-place upgrade.** The plugin folder and executable changed (`touchportal-hardwaremonitor` → `TouchPortalHardwareMonitor`, launched via `TouchPortalHardwareMonitor-Launcher.exe`). You should **remove the v1.x plugin first**, then install 2.0.0.
- **Hardware indexes may differ.** v1.x assigned indexes by sorting at runtime; 2.0.0 assigns persistent indexes on first run. As a result a state like `tp-hm.state.GPU1.…` may now map to a *different* device than it did in v1.x. **Existing buttons that reference specific sensor states will likely need to be re-pointed.**
- **The "Hardware Monitor To Use" setting is gone.** Because LibreHardwareMonitor is now embedded, the old WMI namespace setting (`root/LibreHardwareMonitor`) no longer exists and is ignored.
- **Administrator/UAC is now required.** The plugin elevates on launch; if you decline the UAC prompt, it will not start.
- **No separate LibreHardwareMonitor instance is used.** If your previous setup relied on an external LHM/OHM session feeding the plugin, that path no longer applies.

> [!NOTE]
> Settings that *are* preserved by name: Sensor Capture Time (ms), Temperature Unit (C/F), Normalize Throughput, Normalize Data. The base sensor state-ID format (`tp-hm.state.{TYPE}{index}.{SensorType}.{name}`) is unchanged — only the **index→device** mapping may move.

## 📋 Requirements

- Windows 10/11 (x64)
- Administrator rights (for UAC elevation)
- Touch Portal with SDK 6

## 🧪 Pre-release notes

This is an early build of the rewrite and is provided for testing. Please report issues — and if something looks wrong, attach a `hardware_dump.json` (see Diagnostics above) and a `DEBUG`-level `plugin.log`. **Back up / export your existing pages before upgrading** so you can re-link any affected buttons.
