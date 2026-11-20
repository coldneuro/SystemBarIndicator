# Roadmap

Where the project stands and what may come next. Items under **Ideas** are not commitments —
they are the things most likely to be picked up, in roughly that order.

## Done

- Taskbar bar with CPU, RAM, GPU, VRAM, disk and network sections, plus CPU/GPU/VRAM
  temperature
- Fixed-width value slots and availability latches, so the layout never jumps when a value
  changes width or a sensor drops out for a tick
- Content-driven bar width, measured per frame (correct under PerMonitorV2 DPI)
- History overlay with ten charts over the last ~120 samples
- Smoothed disk/network dual bars — EMA plus an auto-scaling full-scale value, recalibrated
  when the poll interval changes
- Tray icon with tooltip and context menu
- Appearance settings applied live: theme, opacity, font scale, section visibility, poll
  interval, °C/°F
- Position persistence, including recovery when the saved spot is no longer on screen
  (`WM_DISPLAYCHANGE`)
- Always-on-top via z-order in `WM_WINDOWPOSCHANGING` rather than a `SetWindowPos` loop
- Single-instance guard (named mutex)
- Elevation without a per-launch UAC prompt, via a scheduled task (`deploy/Install.ps1`)
- LibreHardwareMonitorLib 0.9.6 with the signed PawnIO driver instead of WinRing0
- Unit tests for all of `Core`; CI builds and tests on `windows-latest` with warnings as errors
- Prebuilt `win-x64` binaries on GitHub Releases, so running the app needs no .NET SDK
- Auto-update from GitHub Releases: daily check, SHA-256 verification, file swap by an
  external script with rollback, and restart through the scheduled task
- Sensor polling on a background thread, NVIDIA readings through NVML, per-source failure
  isolation — about a third of the earlier CPU cost and no UI stalls

## Ideas

**Distribution**
- Self-contained publish as an alternative to the framework-dependent one
- An install script that works against a downloaded build, so the elevated no-UAC setup does
  not require cloning the repository
- Publishing the release build from CI instead of a local machine
- Code signing, to avoid the SmartScreen warning

**Monitoring**
- Research whether OEM laptops expose CPU/GPU temperature through vendor firmware interfaces
  (ACPI/WMI) that need no kernel driver. One field report confirms an Acer Nitro does, via
  `root/WMI` `AcerGamingFunction`; whether usable equivalents exist across vendors is exactly
  what needs establishing before this becomes a second sensor backend to maintain
- An opt-in `-InstallPawnIO` switch for `Install.ps1`, gated on verifying the Authenticode
  signature of the downloaded installer before running it
- Disk throughput without performance counters (ETW or `IOCTL_DISK_PERFORMANCE`), so the
  section survives a machine where the counter registry is disabled
- CPU/GPU clock speeds
- Picking a specific disk or network interface instead of aggregating all of them
- Per-core CPU load in the overlay

**Behaviour**
- Auto-hide over fullscreen apps (games, video)
- Threshold notifications
- Exporting history to CSV/JSON

**Code**
- Extract an `ISensor` interface once a third sensor family justifies it
- Localisation, if there is demand for languages other than English
