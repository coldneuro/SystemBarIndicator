# CLAUDE.md

Guidance for working in this repository.

## Overview

SystemBar Indicator — a small Windows 11 tray/taskbar system monitor (CPU, RAM, GPU,
VRAM, disk, network usage + CPU/GPU/VRAM temperature) with a pop-up history-graph overlay.
WinForms + GDI+ on .NET 10. Hardware temperatures via LibreHardwareMonitorLib.

## Solution layout

```
SystemBarIndicator.sln
├── src/
│   ├── SystemBarIndicator.Core/   # net10.0 — platform-independent core (no WinForms)
│   │   ├── Models/                #   SensorData, DataPoint, DataHistory
│   │   ├── Monitoring/            #   LoadLevel + thresholds, CpuLoad, VramLoad, CpuTemperaturePicker
│   │   ├── Configuration/         #   AppSettings, SettingsStore, WindowPlacement, ColorCodec
│   │   ├── Updates/               #   UpdateVersion, UpdateAssets, ReleaseChecksums
│   │   └── Util/                  #   Formatting, Ema, AutoScale, SmoothingRates, RepeatedFailureFilter, RefreshThrottle
│   └── SystemBarIndicator.App/    # net10.0-windows — WinForms UI + Windows sensors (WinExe)
│       ├── Program.cs             #   MainForm: the taskbar bar, drawing, tray
│       ├── GraphOverlay.cs        #   pop-up graphs
│       ├── SettingsForm.cs        #   appearance settings window
│       ├── BarTheme.cs            #   swappable GDI+ resources (theme + font scale)
│       ├── Sensors/               #   SensorPoller + Cpu/Ram/Disk/Network/Hardware/NvmlGpu (PerfCounter, P/Invoke, LHM, NVML)
│       ├── Updates/               #   UpdateChecker (GitHub API), UpdateInstaller (self-update)
│       └── Logging/Logger.cs      #   file logger
└── tests/
    └── SystemBarIndicator.Tests/  # net10.0 — xUnit v3 (an Exe, run via VSTest), references Core only
```

## Commands

```bash
dotnet build -c Release        # build (TreatWarningsAsErrors — fails on any warning)
dotnet test  -c Release        # unit tests (xUnit v3 through VSTest; the built test exe also runs on its own)
dotnet run --project src/SystemBarIndicator.App   # run the app
```

## Architecture notes

- **Core vs App.** Any logic that does not depend on WinForms or hardware lives in `Core`
  and is covered by tests. UI and OS access (P/Invoke, PerformanceCounter, LHM) live in
  `App`. Put new testable rules (formatting, thresholds, aggregation) in `Core`.
- **Performance counters are optional.** `Processor`/`PhysicalDisk` counters can be disabled
  machine-wide (`Perflib\Disable Performance Counters`, Win32 error 1058) or simply damaged,
  and they can start failing **after** a successful start — which, uncaught, would abort the
  whole polling tick every second. `PerfCounters.IsExpectedFailure` marks those cases;
  `CpuSensor` then switches one-way to `SystemTimesCpuSource` (`GetSystemTimes`, no registry,
  no elevation; the arithmetic lives in `Core/Monitoring/CpuLoad` — the deltas are **unsigned**,
  so the idle-vs-total guards must come before the subtraction), and `DiskSensor` latches
  `Available = false`, which hides the section instead of drawing an idle-looking disk. Both
  log once, not once per tick. `DiskSensor` reads only the `_Total` instance: per-disk
  instance names are renumbered whenever a drive comes or goes (USB, resume from sleep), and
  a counter bound to a vanished instance throws `Instance 'N' does not exist` — an
  `InvalidOperationException`, i.e. an "expected" failure that would latch the section off
  for the rest of the run although nothing is wrong with the counters.
- **GPU sensor names.** LibreHardwareMonitor files several different numbers under similar
  names, so the pickers in `HardwareSensor` compare names **exactly** where it matters:
  `GPU Memory` (a `Load` sensor) is how full the VRAM is, `GPU Memory Controller` is the
  load on the memory bus, and `GPU Core` is the load of the GPU itself. Same for the
  used/total pair — a `Contains("D3D")` test also matches `D3D Shared Memory Used`, which is
  system RAM lent to the GPU rather than video memory. Nothing guarantees the order in which
  sensors are enumerated, so a loose match silently takes whichever happens to come last.
  Cards that report no ready-made VRAM percentage fall back to `VramLoad.Percent` (Core,
  covered by tests) over that pair. LHM keeps a device's sensors in a `HashSet`, so "order"
  really is unspecified. Across devices the order is known and unhelpful: LHM lists AMD GPUs
  **before** NVIDIA ones, so `GpuHardware()` sorts NVIDIA first — otherwise a Ryzen iGPU
  would win over the discrete card.
- **CPU temperature.** Chosen by `Core/Monitoring/CpuTemperaturePicker` (covered by tests)
  from an exact-name priority list — `CPU Package` (Intel), `Core (Tctl/Tdie)`, `Core (Tdie)`,
  `Core (Tctl)` (AMD Zen; Tctl carries a fan-control offset on some models), `Core Max`,
  `CPU Cores` (older AMD) — and only then the hottest core-like reading. The old loose
  `Contains("Core")` filter also matched `CPU Core #N Distance to TjMax`, which is headroom
  below the throttling point, not a temperature; such sensors are always excluded.
- **NVIDIA through NVML.** LHM's NVIDIA `Update()` measured ~85 ms wall / ~35 ms CPU per
  call: it refreshes clocks, fans, power topology, voltages and D3DKMT statistics through
  NVAPI, and takes two `nvmlDeviceGetPcieThroughput` samples that block ~20 ms each; on some
  cards (an RTX 3090 report, PR #2) its `GPU Core` load also stays at 0 %. When LHM and NVML
  both see **exactly one** NVIDIA card, `HardwareSensor` reads load, core temperature and
  VRAM used/total straight from NVML (`Sensors/NvmlGpu`, `nvml.dll` loaded from System32
  only — the process is elevated), which costs well under a millisecond, and skips every LHM
  GPU pass. With two NVIDIA cards nothing ties NVML's device index to LHM's order, so the
  readings could mix cards: LHM is used instead. The VRAM temperature (`GPU Memory Junction`)
  exists only through NVAPI, so LHM's NVIDIA pass still runs for it — at most every 5 s
  (`RefreshThrottle`, Core), only while that section is on, and always on the first read so
  the availability latch learns the sensor exists. If NVML stops answering mid-run (a driver
  update invalidates the session), the GPU falls back to LHM from that tick on and NVML is
  re-opened at most every 30 s; `NvmlGpu.OpenResult` separates that retryable case from the
  permanent ones (no DLL, not one NVIDIA card). The source in use is logged once (`INFO`).
  Without NVML everything stays on LHM, every tick.
- **Polling.** `Sensors/SensorPoller` owns all sensors and reads them on a dedicated
  background thread at the poll interval; the hardware pass (LHM's CPU read waits ~40 ms on
  per-core driver calls) used to hold the UI thread for ~100 ms a second, stalling clicks
  and drags. Threading contract: sensors are built on the UI thread, read once synchronously
  there in `OnLoad` (so the first frame has data), then only by the poller thread until
  `Dispose` joins it. Each tick produces a **new** `SensorData`, never mutated afterwards;
  `MainForm.OnReadingFromPoller` marshals it with `BeginInvoke`, and `ApplyReading` does the
  latches, smoothing, tooltip, history (`DataHistory`, a 120-point queue) and repaint on the
  UI thread. `HardwareSensor.Update()` must be called once per tick — without it LHM returns
  empty values. Each source is read through `SensorPoller.Sample` with its own `try`: one
  that throws (a GPU driver reset) reads as zero for that tick instead of aborting the rest.
  Failures are logged when they start, change or stop (`Core/Util/RepeatedFailureFilter`),
  never once per tick. The graph overlay has no timer of its own: the bar calls
  `GraphOverlay.RefreshData` with each snapshot.
- **Network.** `NetworkSensor` caches the interface list: `GetAllNetworkInterfaces` cost
  ~14 ms per tick, the per-interface counters are cheap. The list is rebuilt on
  `NetworkChange` events, when an interface stops answering, and every 30 s.
- **Temperatures and PawnIO.** LHM 0.9.6 reads CPU sensors through the signed **PawnIO**
  kernel driver (instead of the vulnerable WinRing0, which Microsoft Defender flags).
  PawnIO is installed separately (pawnio.eu); without it CPU temperature is unavailable,
  but the vulnerable driver is not loaded either.
- **Single instance.** `Program.Main` holds a named `Mutex`
  (`Local\SystemBarIndicator_SingleInstance`); a second launch exits immediately.
- **Always-on-top.** Kept on top by modifying the z-order in `WM_WINDOWPOSCHANGING`
  (no recursive `SetWindowPos`); the 1 s timer is only a safety net.
- **Positioning.** `SHAppBarMessage(ABM_GETTASKBARPOS)` determines the taskbar rectangle
  and edge; `MainForm.BarEdge` controls whether the overlay pops up above or below the bar
  (after a drag it is recomputed from the actual position — `UpdateBarEdgeFromPosition`).
  The window width is not fixed: after the content is measured in `OnPaint` it is adjusted
  (`ApplyPendingBarWidth`, deferred via `BeginInvoke` with coalescing) while the right edge
  stays put; the 520 px in the constructor is only a starting estimate.
- **Saved position.** Settings store the **right edge** (`BarRight`), not `Left`: the width
  is fitted to the content only after the first frame, and that fit holds the right edge —
  otherwise the bar would creep away on every start. The position is written **only** when
  the user dragged the bar themselves (`OnResizeEnd` + comparison against the saved pair):
  writing it unconditionally would permanently stop the bar from following the taskbar.
  `Reset Position` clears `BarRight`/`BarTop`. Point validity is checked by
  `WindowPlacement` (Core, covered by tests): an unreachable position is pushed back into
  the screen, and the corrected value is saved immediately. Monitor reconfiguration is
  caught via the `WM_DISPLAYCHANGE` message in `WndProc` (not `SystemEvents`: that arrives
  off the UI thread and requires unsubscribing).
- **Clicking the bar.** The window returns `HTCAPTION` for any hit-test, so the system turns
  a press straight into `WM_SYSCOMMAND`/`SC_MOVE`; client-area `WM_LBUTTONDOWN/UP` never
  arrive, `WM_NCLBUTTONUP` is swallowed, and `WM_ENTERSIZEMOVE` does not arrive at all for a
  click **without** movement. The gesture is therefore decoded inside `SC_MOVE` itself:
  `base.WndProc` returns once the button is released, and if `Location` did not change it
  was a click (open the graphs). The other two title-bar gestures are intercepted for the
  same reason: `WM_NCLBUTTONDBLCLK` is dropped (a double-click would maximize the bar to
  full screen; there is nothing to bind to it either, since the first click has already
  toggled the graphs), and `WM_NCRBUTTONUP` opens the app's own menu instead of the window
  system menu — which offers "Maximize" as a second route to the same trap. `SC_MAXIMIZE`
  is refused in `WM_SYSCOMMAND` as the backstop: `Form.MaximizeBox` is **not** an option,
  because WinForms fills in the title-bar button styles only when
  `FormBorderStyle != None`, so the flag never reaches the window.
- **Drawing.** GDI+ resources live in `BarTheme` (fonts and colours that depend on settings)
  and are recreated when the theme or scale changes; the fixed load-level colours are
  `static`, and per-graph coloured resources belong to their graph. History is taken as a
  single snapshot per frame. Numeric values (percentages, temperatures) are drawn in
  fixed-width slots using the templates "100%"/"999C", right-aligned (`DrawValueSlot`); the
  value range is guaranteed by `Formatting.FormatPercent/FormatTemperature` (Core, covered
  by tests). The CPU-temp and GPU sections are shown through availability latches, so the
  layout jumps neither when the digit count changes nor during a brief sensor dropout.
  **Neither the slot width nor the line height may be cached between frames:** under
  PerMonitorV2, `MeasureString` returns pixels for the current monitor, so both are measured
  in `OnPaint`. The separator is drawn **before** a section if anything has been drawn
  already — otherwise disabled sections would leave dangling bars.
- **Disk/network in the bar.** Shown as a pair of fixed-width bars (▼ read/download,
  ▲ write/upload). Jagged instantaneous values are smoothed with `Ema`, and the full-scale
  value is an adaptive `AutoScale` over the recent peak (both classes in `Core`, covered by
  tests). The shared drawing method is `DrawDualBars`. The base coefficients (alpha 0.4,
  decay 0.97) are calibrated for a 1 s tick; when the poll interval changes they are
  recomputed (`SmoothingRates`, Core) — otherwise at 0.5 s the indicators would be twice as
  sluggish, and at 5 s they would be jagged.
- **Settings.** `%LOCALAPPDATA%\SystemBarIndicator\settings.json` (next to `app.log`; the
  scheduled task runs as the same user, so the path does not change with elevation).
  Loading never throws: corrupt JSON, a `null` document or a missing file all yield
  defaults, with the reason logged. Writing is **atomic** (`.tmp` + `File.Move(overwrite)`)
  and always persists `Normalized()` — NaN/infinity are not valid JSON. `Normalized()` also
  repairs `null` in non-nullable string properties: nullable annotations do not affect
  deserialization (`RespectNullableAnnotations` is off by default). Serialization uses a
  source-generated `JsonSerializerContext`: not strictly required today (trim/AOT analyzers
  are not enabled and there are no warnings), but it stays correct if publishing is ever
  switched to trimming. Section flags are **auto-properties, not fields**:
  `System.Text.Json` silently ignores fields. Disk writes are deferred (`_saveTimer`,
  1.5 s) — dragging and sliders must not cause a storm of writes; the settings window
  applies changes immediately but recreates the theme only when fields that affect it
  changed (`AppSettings.DiffersInAppearance`). `MainForm.ApplySettings` deliberately
  **ignores** `BarRight`/`BarTop` from the incoming snapshot: otherwise an open settings
  window would overwrite the position with its own stale copy.

- **Auto-update.** `UpdateChecker` (App) asks the GitHub Releases API — first check 60 s
  after start, then daily; Debug builds never auto-update (`AutoUpdateSupported`), so a
  developer copy cannot replace itself with a release download. Tag-vs-assembly comparison,
  asset picking and `SHA256SUMS.txt` parsing live in `Core/Updates` (covered by tests); a
  pre-release tag like `v1.1.0-beta` deliberately parses to null and is ignored.
  `UpdateInstaller` downloads the constant-name `SystemBarIndicator-win-x64.zip` into
  `%LOCALAPPDATA%\SystemBarIndicator\update`, verifies it when the release ships
  `SHA256SUMS.txt` (a present-but-wrong checksum aborts; an absent one only logs), unpacks
  it (the exe sits one folder down — the release packs a versioned directory) and hands the
  swap to a generated `swap.ps1`, ASCII-only per the `.ps1` encoding rule: a running exe
  cannot replace its own files. The download and checksum run under a 10-minute deadline:
  `HttpClient.Timeout` ends at the response headers with `ResponseHeadersRead`, so a stalled
  body would otherwise hang forever and keep `_updateBusy` set. The script waits for the
  process to exit, backs up `AppContext.BaseDirectory` into `update\backup`, copies the new
  build over it with retries (restoring the backup if that fails, so no half-old/half-new
  mix is left), writes the outcome to `app.log`, and **always** restarts via
  `schtasks /run` (fallback: plain `Start-Process`, unelevated) — a failed update must not
  leave the user without the bar. The release shape the updater relies on is documented in
  `deploy/README.md#releasing`. Failed checks are classified by `Core/Updates/UpdateFailures`
  and de-duplicated on its stable `Key`: `EnsureSuccessStatusCode` raises the same
  `HttpRequestException` as a dead connection, so severity comes from `StatusCode` (null =
  transport = transient; 403/408/429/5xx transient; other 4xx and unreadable JSON are defects)
  rather than from the exception type, and repeats of the same key are not logged again.

## Conventions

- `nullable` is enabled everywhere; `TreatWarningsAsErrors=true` (see `Directory.Build.props`).
- The build must stay warning-free.
- English for code, comments, documentation and commit messages.
- Application log: `%LOCALAPPDATA%\SystemBarIndicator\app.log`. Errors carry the stack
  trace; past 1 MB the file is moved to `app.1.log` (replacing the previous one).
- `ROADMAP.md` separates what is implemented from what is merely an idea — keep that
  distinction accurate when it changes.
- User-visible changes get an entry under `## [Unreleased]` in `CHANGELOG.md`; internal
  refactors and test-only changes do not. Releasing (version bump, tag, `win-x64` zip) is
  manual and documented in `deploy/README.md#releasing`.
- **Dependency updates.** `.github/dependabot.yml` opens weekly PRs for NuGet and GitHub
  Actions, only for releases at least 14 days old (`cooldown`); test packages come grouped
  in one PR. Updating by hand, follow the same two-week rule. A `LibreHardwareMonitorLib`
  bump needs the packaged-build check (`deploy/README.md#releasing`, step 8) before merging.
- **Desktop deployment.** `LibreHardwareMonitorLib` 0.9.6 is a RID-split package (the real
  assemblies live in `runtimes/<rid>/lib/<tfm>/`). To get a clean flat layout of a single
  version, publish with a RID into a separate folder (outside OneDrive):
  `dotnet publish src/SystemBarIndicator.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o <folder>`.
  The regular `bin` sits in OneDrive and its contents can drift — do not rely on it.
  The full reproducible install is `deploy/Install.ps1` (see `deploy/README.md`).
- **Running as administrator without UAC.** The app needs admin rights (the PawnIO driver),
  but the manifest is `asInvoker`. A shortcut with the "run as administrator" flag would
  prompt for UAC every time, so a **scheduled task** is used instead (RunLevel=Highest) and
  the shortcut launches a silent `launch.vbs` → `schtasks /run` (triggering your own task
  does not require elevation). Registering the task is the only step needing admin rights
  (`Install.ps1` self-elevates for it).
- **Icon.** The multi-size `src/SystemBarIndicator.App/appicon.ico` is embedded both in the
  exe (`ApplicationIcon`) and as an `EmbeddedResource` — `LoadAppIcon()` loads it for the
  tray and the window.
- **Keep `.ps1` scripts that self-elevate ASCII-only.** Non-ASCII text in a `.ps1` (UTF-8
  without a BOM) re-read by `powershell.exe` under the ANSI code page gets corrupted, and
  once broke the scheduled task (registered under a mojibake folder). Text used for
  elevated relaunch stays ASCII.
