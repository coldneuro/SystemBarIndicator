# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- The disk section no longer disappears until the next restart when a drive is plugged in
  or removed, or the disks are renumbered after a resume from sleep. It now reads the
  combined counter for all disks, which survives those changes.
- CPU temperature is now taken from the package sensor on Intel and the die sensor on AMD,
  chosen by exact name. It could previously land on any core-like sensor — including a
  "Distance to TjMax" reading, which is headroom below the throttling point rather than a
  temperature.
- One sensor failing (a GPU driver reset, for instance) no longer freezes the whole bar: the
  other sections, the graphs and the tooltip keep updating, and the failure is logged once
  instead of every second.
- An update download that stalls is abandoned after 10 minutes instead of blocking every
  later update check until a restart.
- An update whose files cannot be copied into place no longer leaves the bar closed or
  half-updated: the previous build is restored and the bar is restarted either way, with the
  outcome written to `app.log`.

### Changed

- `app.log` records the stack trace of errors and is rolled over to `app.1.log` once it
  passes 1 MB.

## [1.2.0] - 2026-09-12

### Added

- VRAM usage: how full the video memory is, as a percentage in the bar in front of the VRAM
  temperature (`VRAM 31% 56C`), as a tenth chart in the history overlay, and as its own
  section checkbox in the settings. Read straight from the card where it is reported that
  way, derived from the used/total pair otherwise, and hidden where neither is available.

### Fixed

- GPU load could be read off a memory sensor on a card that does not enumerate "GPU Core"
  first: "GPU Memory" is how full the VRAM is and "GPU Memory Controller" is the load on the
  memory bus, and neither is the load of the GPU itself.

## [1.1.1] - 2026-08-31

### Added

- CPU load now survives a machine whose Windows performance counters are unavailable: it
  falls back to a kernel call (`GetSystemTimes`) that needs no counter registry, at startup
  or if the counter breaks later while running.

### Changed

- The disk section is hidden, and marked `(n/a)` in the settings, when the performance
  counters it needs are unavailable — instead of drawing bars that claim the disk is idle.
  A one-time notification explains it, with a Troubleshooting entry in the README.
- Failed update checks are logged by what actually went wrong: an unreachable network is a
  warning and is not repeated on every check, while an unexpected answer from GitHub stays
  an error. Connection attempts also give up sooner.

### Fixed

- A performance counter that broke *after* startup aborted the whole polling cycle — every
  sensor, the graph history and the repaint with it — once per second. CPU and disk counters
  now fail on their own.
- The README no longer claims the app makes no network requests; it has contacted the GitHub
  Releases API since 1.1.0. What is and is not sent is now spelled out.
- Performance counters left undisposed when a sensor failed partway through starting up.

## [1.1.0] - 2026-08-30

### Added

- Auto-update: the app checks GitHub Releases shortly after start and then daily, and
  installs a newer version by itself — download, SHA-256 verification (when the release
  ships `SHA256SUMS.txt`), file swap after exit and a restart through the scheduled task.
  "Install updates automatically" in the settings turns it off; "Check for Updates..." in
  the tray menu asks on demand and reports the outcome either way.
- VRAM temperature: shown in the bar next to the GPU temperature (under a dim `VRAM`
  label), as a ninth chart in the history overlay, and as its own section checkbox in the
  settings. Available where LibreHardwareMonitor exposes a GPU memory temperature sensor
  (NVIDIA "GPU Memory Junction", AMD "GPU Memory"); hidden otherwise.

## [1.0.0] - 2026-08-10

First public release. The feature set below grew over the project's private history; the
individual steps are in the commit log.

### Added

- Taskbar bar with CPU, RAM, GPU, disk and network sections plus CPU and GPU temperature,
  coloured by load level.
- History overlay with eight charts over the last ~120 samples — click the bar to open it.
  It follows the bar around the screen and flips to open downwards near the top edge.
- Smoothed disk and network dual bars: an EMA over the raw counters and an auto-scaling
  full-scale value, both recalibrated when the poll interval changes.
- Fixed-width value slots and sensor availability latches, so the layout never jumps when a
  number changes width or a sensor drops out for a tick.
- Content-driven bar width, measured every frame so it stays correct under PerMonitorV2 DPI.
- Appearance settings applied live: theme, opacity, font scale, section visibility, poll
  interval and °C/°F, stored in `%LOCALAPPDATA%\SystemBarIndicator\settings.json`.
- Position persistence, including recovery when the saved spot is no longer on screen
  (monitor unplugged, resolution changed).
- Tray icon with a load tooltip and a context menu; the same menu opens on a right-click on
  the bar itself.
- Always-on-top by adjusting the z-order in `WM_WINDOWPOSCHANGING` rather than looping on
  `SetWindowPos`.
- Single-instance guard (named mutex).
- Hardware temperatures via LibreHardwareMonitorLib 0.9.6 and the signed PawnIO driver, so
  the vulnerable WinRing0 is never loaded.
- `deploy/Install.ps1` — a reproducible install that launches the app elevated **without** a
  UAC prompt on every start (scheduled task plus a silent launcher).
- Prebuilt `win-x64` binaries attached to the release, so running the app no longer requires
  the .NET SDK.
- Unit tests covering all of `Core`; CI builds and tests on `windows-latest` with warnings
  as errors.

[Unreleased]: https://github.com/coldneuro/SystemBarIndicator/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/coldneuro/SystemBarIndicator/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/coldneuro/SystemBarIndicator/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/coldneuro/SystemBarIndicator/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/coldneuro/SystemBarIndicator/releases/tag/v1.0.0
