# Touch Portal Hardware Monitor 2.2.0 — C# Rewrite

> [!WARNING]
> **This is a complete rewrite and is NOT compatible with v1.x.**
> The Node.js plugin has been replaced by a self-contained C# / .NET 10 application. Existing v1.x buttons, states, and setup **will not carry over cleanly** — read [Breaking Changes](#-breaking-changes-read-before-upgrading) before installing, and back up your pages first.

## What this is

The plugin has been rebuilt from the ground up in **C# (.NET 10)** and now embeds **LibreHardwareMonitor** directly. You no longer install or run LibreHardwareMonitor (or OpenHardwareMonitor) separately — the plugin reads your hardware itself, in-process, and ships as a single self-contained `.exe` (no Node.js runtime, no external dependencies).

2.2.0 rolls up everything since the rewrite began: the core 2.0.0 engine, robustness and diagnostics fixes, and two brand-new non-LibreHardwareMonitor data sources — **display refresh rate** and **FPS**.

---

## ✨ Highlights

- 🧩 **Self-contained** — one trimmed, single-file `.exe`; nothing else to install.
- 🔌 **LibreHardwareMonitor built in** (LibreHardwareMonitorLib **0.9.6**) — sensors read directly, not over WMI.
- 🛡️ **Runs elevated** — a small launcher requests UAC so MSR/SuperIO sensors (CPU temps, clocks, voltages, motherboard, fans) are visible.
- 🖥️ **NEW — Display states** — refresh rate (Hz), resolution, name, and primary flag for every connected monitor. No driver, no elevation.
- 🎮 **NEW — FPS states** — foreground-app frame rate, with a selectable source (RTSS / in-process ETW / Auto).
- ⏱️ **NEW — Live capture interval** — changing "Sensor Capture Time (ms)" now applies immediately, no restart.
- 🩺 **NEW — Sensor-access diagnostics** — the plugin tells you (and the dump tells you) when CPU/motherboard sensors are missing because the kernel driver didn't load, and why.
- 📊 **Richer states** — every sensor also exposes `.unit`, `.min`, and `.max` companion states.
- 🔢 **Stable hardware numbering** — persistent per-device indexes saved to `%AppData%\TouchPortalHardwareMonitor`, so `CPU1`, `GPU1`, `Network1`, … stay pointed at the same device across restarts and reinstalls.
- 🧹 **Cleaner device list** — Windows network filter drivers and virtual/inactive adapters are filtered out.

---

## 🆕 New since 2.0.0

### 🖥️ Display refresh-rate states *(new category: `Displays`)*

Sourced from Win32 display APIs — **independent of LibreHardwareMonitor**, needs no kernel driver and no elevation, so it works even when hardware sensors don't.

| State | Example |
|---|---|
| `tp-hm.state.display.{n}.refresh_rate` (+ `.refresh_rate.unit`) | `144` / `Hz` |
| `tp-hm.state.display.{n}.resolution` | `2560x1440` |
| `tp-hm.state.display.{n}.name` | monitor name |
| `tp-hm.state.display.{n}.primary` | `true` / `false` |
| `tp-hm.state.display.count` | `2` |

Values update automatically when you change resolution or refresh rate.

### 🎮 FPS states *(new category: `FPS`)*

Foreground-application frame rate, with a **selectable backend** controlled by the new **`FPS Source (Off/RTSS/Built-in/Auto)`** setting (default `Auto`):

- **RTSS** — reads RivaTuner Statistics Server / MSI Afterburner shared memory. No admin, no driver; works while RTSS is running.
- **Built-in** — in-process ETW capture of DXGI/D3D9 present events. Uses the elevation the plugin already has.
- **Auto** — prefer RTSS if it's running, otherwise fall back to Built-in.

| State | Example |
|---|---|
| `tp-hm.state.fps.value` (+ `.unit`) | `144` / `FPS` |
| `tp-hm.state.fps.process` | `game.exe` |
| `tp-hm.state.fps.source` | `RTSS` or `Built-in` |

> [!NOTE]
> The **Built-in (ETW)** backend is **experimental** in this release. It's validated to load and start correctly, but its present-event coverage across every game/API (DX12, Vulkan, OpenGL, fullscreen vs. borderless) has not yet been broadly tested. For the most reliable numbers today, run RTSS/MSI Afterburner and use `Auto` or `RTSS`.

### ⏱️ Live capture interval

Changing **Sensor Capture Time (ms)** now re-arms the polling loop immediately — no plugin restart required.

### 🩺 Sensor-access diagnostics

If CPU temperature/clock/voltage or motherboard sensors are missing, it's almost always because LibreHardwareMonitor's **kernel driver (WinRing0) didn't load** — GPU (NVAPI), storage (SMART) and OS load/memory don't need it, but MSR/SuperIO reads do. This release makes that diagnosable:

- New **`tp-hm.state.plugin.sensor_status`** state — put it on a button to see, in plain language, whether sensors are accessible and why not.
- Startup log now records whether the plugin is **elevated**.
- `hardware_dump.json` now includes `isElevated`, a `sensorAccess` summary, and **null-valued sensors** (with a `valuePresent` flag) so "missing" vs. "present-but-unreadable" is visible.

---

## ⚙️ Settings

| Setting | Default | Notes |
|---|---|---|
| Sensor Capture Time (ms) | `2000` | 500–99999. **Now applies live.** |
| Temperature Unit (C/F) | `C` | |
| Normalize Throughput (B/s, KB/s, MB/s, GB/s) | `No` | |
| Normalize Data (MB, GB) | `No` | |
| **FPS Source (Off/RTSS/Built-in/Auto)** | `Auto` | **New.** Applies live. |

---

## 🧪 Troubleshooting: missing CPU / motherboard temperatures

If GPU temps show up but **CPU temps don't**, the kernel driver isn't loading. Work through these in order:

1. **Run as administrator.** Accept the UAC prompt when the plugin starts (Task Manager → Details → *Elevated* column should say **Yes** for `TouchPortalHardwareMonitor.exe`).
2. **Windows Memory Integrity** (Settings → Privacy & security → Windows Security → Device security → Core isolation). If **On**, it blocks the driver — turn it Off and reboot. *(Security trade-off; it's what allows CPU sensor access.)*
3. **Close other monitoring apps** that hold the driver (HWiNFO, MSI Afterburner/RTSS, Armoury Crate, OpenRGB, Ryzen Master), then restart.
4. **Check antivirus** for a quarantined `WinRing0` driver.

Check the **`tp-hm.state.plugin.sensor_status`** state, or drop an empty `dump_sensors.txt` next to the plugin and restart to produce a fresh `hardware_dump.json`.

---

## 💥 Breaking Changes (read before upgrading)

- **Not an in-place upgrade.** The plugin folder/executable changed (`touchportal-hardwaremonitor` → `TouchPortalHardwareMonitor`, launched via `TouchPortalHardwareMonitor-Launcher.exe`). **Remove the v1.x plugin first**, then install 2.2.0.
- **Hardware indexes may differ from v1.x.** 2.x assigns persistent indexes on first run, so a state like `tp-hm.state.GPU1.…` may map to a different device than before. **Buttons referencing specific sensor states will likely need to be re-pointed.**
- **The "Hardware Monitor To Use" setting is gone.** LibreHardwareMonitor is embedded; the old WMI namespace setting is ignored.
- **Administrator/UAC is required.** The plugin elevates on launch; declining the prompt prevents startup.

> [!NOTE]
> Preserved by name: Sensor Capture Time (ms), Temperature Unit (C/F), Normalize Throughput, Normalize Data. The base sensor state-ID format (`tp-hm.state.{TYPE}{index}.{SensorType}.{name}`) is unchanged — only the index→device mapping may move.

---

## 📋 Requirements

- Windows 10/11 (x64)
- Administrator rights (for UAC elevation)
- Touch Portal with SDK 6
- *(Optional)* RivaTuner Statistics Server / MSI Afterburner for the RTSS FPS source

---

## 📥 Install

1. Remove any v1.x install and back up / export your pages.
2. In Touch Portal: **Settings → Plug-ins → Import plug-in…** and select `TouchPortalHardwareMonitor-Windows-2.2.0.tpp`.
3. Accept the UAC prompt when the plugin starts.
4. Re-point any buttons that referenced specific sensor states (indexes may have moved from v1.x).

---

## 🐞 Known limitations

- **Built-in (ETW) FPS is experimental** — see the FPS note above; prefer RTSS for now.
- **Display index** is enumeration order (1..N) and is not persistent across monitor hot-plug (unlike hardware indexes).
- FPS reflects the **foreground** presenting app; it's not per-monitor.

---

## 🙏 Feedback

Please report issues with a `hardware_dump.json` and a `DEBUG`-level `plugin.log` (create `loglevel.txt` containing `DEBUG` next to the plugin). For FPS issues, run `TouchPortalHardwareMonitor.exe --fps` from an elevated prompt with your game open and include the output.
