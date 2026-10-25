# SystemBar Indicator

[![CI](https://github.com/coldneuro/SystemBarIndicator/actions/workflows/ci.yml/badge.svg)](https://github.com/coldneuro/SystemBarIndicator/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/coldneuro/SystemBarIndicator)](https://github.com/coldneuro/SystemBarIndicator/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](#requirements)

A small always-on-top system monitor that sits on the Windows 11 taskbar. It shows live
CPU, RAM, GPU, VRAM, disk and network load plus CPU/GPU/VRAM temperature, keeps itself up to
date from GitHub Releases, and expands into a history-graph overlay when you click it.

![The bar sitting on the taskbar: CPU load and temperature, RAM, GPU load and temperature, VRAM usage and temperature, then disk and network activity bars](docs/bar.png)

Reading left to right: CPU load and temperature, RAM usage, GPU load and temperature, how
full the video memory is and how hot it runs, then disk (`D`) and network (`N`). Each number
lives in a fixed-width slot, so nothing shifts sideways as values change, and the colour
follows the load level. Disk and network are drawn
as a pair of bars rather than numbers — ▼ is read/download, ▲ is write/upload — smoothed and
auto-scaled to recent peaks.

Click the bar and it unfolds into the history overlay, covering roughly the last two minutes:

<img src="docs/graphs.png" alt="The history overlay above the bar: charts for CPU, CPU temperature, RAM, GPU, GPU temperature, VRAM usage, VRAM temperature, disk read/write and network download/upload" width="420">

The overlay tracks the bar — it follows it around the screen, shows only the sections you
have enabled, and opens downwards instead of upwards if you drag the bar to the top half of
the screen.

## Features

- **CPU** — total load and temperature
- **RAM** — memory usage
- **GPU** — load and temperature (NVIDIA / AMD / Intel)
- **VRAM** — how full the video memory is, plus its temperature, where the card exposes
  those sensors
- **Disk** — read/write throughput as a smoothed dual bar
- **Network** — download/upload throughput as a smoothed dual bar
- **Graph overlay** — click the bar for ten history charts (CPU, CPU temp, RAM, GPU,
  GPU temp, VRAM, VRAM temp, disk R/W, net down, net up) over the last ~120 samples
- **Load colouring** — values change colour as load crosses the warning/critical thresholds
- **Always on top** — the bar keeps its z-order above the taskbar
- **Tray icon** — shows `CPU / RAM / GPU` in the tooltip, with a context menu
- **Remembered position** — drag the bar anywhere; it returns there after a restart, and
  is pulled back on screen if that spot no longer exists
- **Appearance settings** — theme, opacity, font scale, which sections to show, poll
  interval, °C/°F
- **Auto-update** — checks GitHub Releases daily and installs new versions by itself
  (can be turned off; **Check for Updates...** in the tray menu works on demand)

## Requirements

- Windows 10 (build 19041+) or Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — to run a prebuilt
  build; the [.NET 10 SDK](https://dotnet.microsoft.com/download) only if you build it
  yourself
- Administrator rights — required to read hardware temperatures (the sensor driver is
  loaded into the kernel)
- [PawnIO](https://pawnio.eu) — needed for **CPU** temperature, on AMD as well as Intel.
  Without it the CPU temperature reads 0 while GPU temperature still works; see
  [Troubleshooting](#troubleshooting)

## Install

### Option 1 — download a build

Grab `SystemBarIndicator-win-x64.zip` from the
[latest release](https://github.com/coldneuro/SystemBarIndicator/releases/latest), unpack it
anywhere and run `SystemBarIndicator.exe`. No SDK needed, only the .NET 10 desktop runtime.
The build is unsigned, so SmartScreen will ask once before it starts.

Launched this way the app is **not** elevated, so temperatures stay unavailable — for those,
use option 2.

### Option 2 — install script (recommended)

Publishes the app, then sets it up so a desktop shortcut launches it **elevated without a
UAC prompt every time**:

```powershell
git clone https://github.com/coldneuro/SystemBarIndicator.git
cd SystemBarIndicator
pwsh -File deploy/Install.ps1            # add -AutoStart to also start it at logon
```

You get exactly one UAC prompt during installation — for registering the scheduled task.
See [deploy/README.md](deploy/README.md) for how and why this works, and for uninstalling.

### Option 3 — run from source

Needs the .NET 10 **SDK**, not just the desktop runtime that a prebuilt release requires.

```powershell
git clone https://github.com/coldneuro/SystemBarIndicator.git
cd SystemBarIndicator
dotnet run --project src/SystemBarIndicator.App
```

Started this way the app is **not** elevated, so temperatures will be unavailable —
everything else works.

## Usage

Right-click the tray icon — or the bar itself — for the menu:

| Item | Action |
|------|--------|
| **Graphs** | Open/close the history overlay (same as clicking the bar itself) |
| **Settings...** | Appearance settings window |
| **Reset Position** | Snap the bar back to the taskbar and forget the saved position |
| **Check for Updates...** | Ask GitHub for a newer release and offer to install it |
| **Exit** | Quit |

**Moving it.** Drag the bar with the mouse — the position is remembered and restored on
the next start. Until you move it, the bar docks to the right edge of the taskbar and
follows it. If the saved spot becomes unreachable (monitor unplugged, resolution changed),
the bar is moved back into the visible area.

**Appearance** (`Settings...`, applied live):

- opacity, 30–100 %
- theme: dark, light, or a custom background colour
- font scale, 75–175 %
- which sections to show: CPU, CPU temp, RAM, GPU, GPU temp, VRAM, VRAM temp, disk, network
- poll interval: 0.5 / 1 / 2 / 5 s — smoothing rates are recalibrated with it
- temperature in °C or °F
- automatic update installation on or off

## Configuration files

| Path | Contents |
|------|----------|
| `%LOCALAPPDATA%\SystemBarIndicator\settings.json` | Settings — safe to edit by hand; invalid values are silently replaced with valid ones |
| `%LOCALAPPDATA%\SystemBarIndicator\app.log` | Application log |

Writes are atomic (temp file + move), and a corrupt or missing settings file falls back to
defaults rather than failing to start.

## Troubleshooting

**CPU temperature reads 0.** The CPU temperature comes from LibreHardwareMonitor, which
reads it through the kernel-level [PawnIO](https://pawnio.eu) driver — on AMD as well as
Intel. Install PawnIO once and run the app elevated (option 2 above). GPU temperature does
not need it and works without. If the app is not elevated, no temperature is available at
all.

**NVIDIA GPU core stays at 0% while the card is busy.** The app prefers the NVIDIA
Management Library (NVML) for GPU-busy percentage and falls back to
LibreHardwareMonitor's NVAPI sensor if NVML cannot provide a reading. NVML ships
with the NVIDIA driver; no additional package or administrator privilege is
required for this metric. To compare readings under a sustained workload, run
`nvidia-smi --query-gpu=name,utilization.gpu --format=csv -l 1`.

Check `%LOCALAPPDATA%\\SystemBarIndicator\\app.log` for the NVML source or
fallback message. A failed query temporarily uses the existing sensor and
retries NVML after 30 seconds without spamming logs. If both sources are
unavailable, the bar/tray displays `n/a`; true idle still displays `0%`.
The history graph currently records unavailable samples as zero. On systems
with multiple NVIDIA cards, NVML is not selected without unambiguous mapping.

**The disk section is missing, and `app.log` mentions performance counters.** Windows
performance counters are unavailable on the machine, so disk throughput cannot be read; the
section is hidden rather than drawn as permanently idle, and the settings window marks it
`(n/a)`. CPU load is unaffected — it falls back to a kernel call that needs no counters.

The counters can be *disabled* (a policy or registry setting) or *damaged* (broken
registration), and the two need different fixes — Microsoft's
[guide to rebuilding performance counters](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/manually-rebuild-performance-counters)
covers both. Check the disable flags first, and only then rebuild with `lodctr /R`;
rebuilding alone will not re-enable counters that are switched off.

**SmartScreen warns before the app starts.** The release builds are not code-signed. Click
"More info" → "Run anyway", or build from source yourself.

## Permissions and privacy

This app reads hardware sensors, so it is worth being explicit about what it does:

- **The only network access is the update check.** The app asks the GitHub Releases API
  whether a newer version exists, and downloads the release archive when it installs one.
  Nothing about you or your machine is sent: no telemetry, no analytics, no identifiers
  beyond the `User-Agent` GitHub requires. Turn it off with *Install updates automatically*
  in the settings and the app makes no network requests at all. Network counters are read
  locally from the OS either way.
- **Why administrator rights.** Temperatures come from
  [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
  which reads CPU sensors through a kernel driver — that needs elevation. Load, RAM, disk
  and network figures do not; without elevation the app simply omits temperatures.
- **PawnIO instead of WinRing0.** LibreHardwareMonitor 0.9.5+ uses the signed
  [PawnIO](https://pawnio.eu) driver rather than WinRing0, which Microsoft Defender flags
  as vulnerable. PawnIO is installed separately and only once. Without it you lose CPU
  temperature on both AMD and Intel — and importantly, the vulnerable driver is never loaded.
- **The bundled manifest is `asInvoker`**, not `requireAdministrator`. Elevation is
  arranged by the install script through a scheduled task, so nothing silently elevates
  itself.

## Tech stack

| Layer | Technology |
|-------|------------|
| Language | C# |
| UI | Windows Forms |
| Runtime | .NET 10 (`net10.0-windows`) |
| Drawing | GDI+ (`System.Drawing`) |
| CPU / RAM / disk / network | `PerformanceCounter`, `GlobalMemoryStatusEx`, `NetworkInterface` |
| Temperatures and GPU | [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 |
| CPU sensor access | [PawnIO](https://pawnio.eu) — signed kernel driver |

## Project layout

```
src/
├── SystemBarIndicator.Core/       # net10.0 — platform-independent, unit-tested
│   ├── Models/                    #   SensorData, DataPoint, DataHistory
│   ├── Monitoring/                #   Load levels and thresholds
│   ├── Configuration/             #   AppSettings, SettingsStore, WindowPlacement, ColorCodec
│   └── Util/                      #   Formatting, Ema, AutoScale, SmoothingRates
└── SystemBarIndicator.App/        # net10.0-windows — WinForms UI + Windows sensors
    ├── Program.cs                 #   Entry point + MainForm (the taskbar bar)
    ├── GraphOverlay.cs            #   History graph overlay
    ├── SettingsForm.cs            #   Appearance settings, live preview
    ├── BarTheme.cs                #   Swappable GDI+ resources (theme, font scale)
    ├── Sensors/                   #   Cpu, Ram, Disk, Network, Hardware (LHM)
    └── Logging/Logger.cs          #   File logger

tests/SystemBarIndicator.Tests/    # net10.0 — xUnit, covers Core
deploy/                            # Install/Uninstall scripts (see deploy/README.md)
```

Anything that does not depend on WinForms or on the OS lives in `Core` and is unit-tested;
UI and OS access (P/Invoke, `PerformanceCounter`, LHM) live in `App`. `CLAUDE.md` holds the
detailed architecture notes — the non-obvious constraints behind the current design.

## Build and test

```powershell
dotnet build -c Release      # warnings are errors — see Directory.Build.props
dotnet test  -c Release      # xUnit
```

CI runs the same two commands on `windows-latest` for every push and pull request.

## Contributing

Bug reports and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
For anything security-related, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
