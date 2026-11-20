# Security Policy

## Reporting a vulnerability

Please report security issues privately through GitHub's
[private vulnerability reporting](https://github.com/coldneuro/SystemBarIndicator/security/advisories/new)
rather than in a public issue. Include what you found, how to reproduce it, and what an
attacker could do with it. This is a hobby project maintained by one person — expect a
reply within a couple of weeks.

## What this app does with privileges

Worth stating plainly, because a system monitor asking for administrator rights deserves
scrutiny:

- **The only network access is the update check.** Release builds ask the GitHub Releases
  API for the latest release of this repository — 60 seconds after start, then once a
  day — and, when installing a newer version, download its archive and `SHA256SUMS.txt`
  from GitHub. No telemetry, no analytics, no identifiers beyond the `User-Agent` GitHub
  requires. With *Install updates automatically* turned off in the settings, nothing is
  requested unless you pick **Check for Updates...** in the menu. Debug builds never check
  on their own. The network figures shown are OS counters read locally.
- **Sensors are only read.** Hardware sensors, memory statistics and performance counters
  are read, never written to.
- **What it writes.** Settings and the log go to `%LOCALAPPDATA%\SystemBarIndicator\`.
  Installing an update additionally stages the download in
  `%LOCALAPPDATA%\SystemBarIndicator\update\`, then a generated PowerShell script
  (`swap.ps1`) waits for the app to exit, copies the new files over the folder the app runs
  from and restarts it through the scheduled task.
- **How updates are vetted.** Downloads are HTTPS-only and come from this repository's own
  release. If the release ships `SHA256SUMS.txt`, the archive must match it or the update is
  aborted; older releases without the file install with a warning in the log. The checksum
  lives in the same release as the archive, so it catches a corrupted or truncated download,
  not a compromised release or maintainer account — the builds are not code-signed either.
- **The manifest is `asInvoker`**, not `requireAdministrator`. The app never elevates
  itself; running it normally just means temperatures are unavailable.
- **Elevation, when used, is explicit.** `deploy/Install.ps1` registers a Task Scheduler
  task with `RunLevel=Highest` and self-elevates only for that step — one UAC prompt during
  installation. Afterwards the desktop shortcut triggers that task, which is why launching
  does not prompt again. If you would rather not have that, skip the install script and run
  the app unelevated.

### Know the trade-off of the no-UAC setup

The install script puts the app in `%LOCALAPPDATA%\Programs\SystemBarIndicator`, a folder
your own account can write to without elevation, and the scheduled task runs whatever
`SystemBarIndicator.exe` it finds there with the highest privileges. Any program running as
you can therefore replace that file and have it started as administrator, without a UAC
prompt, the next time the bar launches. The update staging folder and `swap.ps1` sit in a
user-writable location for the same reason. Microsoft does not treat UAC as a security
boundary, so this is a convenience trade-off rather than a vulnerability, but it is one you
should choose knowingly. If it matters on your machine, skip the install script and run
the app unelevated — you lose temperatures, nothing else.

## Kernel driver

Temperatures are read via
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
0.9.6, which accesses CPU sensors through the signed [PawnIO](https://pawnio.eu) kernel
driver. PawnIO is deliberate: the older WinRing0 driver has known vulnerabilities and is
flagged by Microsoft Defender. PawnIO is not bundled here — you install it yourself, once,
from its own site. Without it the app still runs and the vulnerable driver is never loaded;
you simply do not get CPU temperature.

## Release binaries

The `win-x64` zip attached to a release is built and packaged on a maintainer machine, not
by CI, and it is **not code-signed** — SmartScreen warns the first time you run it. If you
would rather not trust a stranger's binary, build from source instead: everything needed is
in this repository, and CI runs the same `dotnet build` and `dotnet test` on every push.
The auto-updater installs exactly these release archives, so the same applies to it — turn
off *Install updates automatically* if you build from source.

## Supported versions

The latest release and the current `main` branch. Fixes go into `main` and ship with the
next tag; there are no patch branches for older versions.
