# Deployment

A reproducible desktop install: publish, icon, and **elevated launch with no UAC prompt**
on every double-click.

## Why it works this way

The app needs administrator rights to read CPU sensors (the **PawnIO** kernel driver, via
LibreHardwareMonitor). Its manifest is `asInvoker`, so a shortcut with the "run as
administrator" flag would prompt for UAC **every single time**. Instead:

- a **scheduled task** is registered (Task Scheduler) with `RunLevel=Highest`;
- the shortcut does not point at the exe but at a silent `launch.vbs`, which triggers that
  task (`wscript` → `schtasks /run`);
- triggering **your own** task does not require elevation → no UAC prompt.

Administrator rights are needed only to *register* the task, so the script self-elevates
for exactly that step — one UAC prompt for the whole installation.

## Install

```powershell
pwsh -File deploy/Install.ps1
```

With auto-start at Windows sign-in (also without UAC):

```powershell
pwsh -File deploy/Install.ps1 -AutoStart
```

What the script does:

1. `dotnet publish` into `%LOCALAPPDATA%\Programs\SystemBarIndicator` (flat layout — LHM
   0.9.6 ships as a RID-split package).
2. Writes `launch.vbs` (the hidden task launcher).
3. Registers the `SystemBarIndicator` task with highest privileges (**the one UAC prompt**).
4. Creates a `SystemBar Indicator` desktop shortcut with the app icon and the "run as
   administrator" flag explicitly **cleared**.

After that, double-clicking the shortcut starts the indicator elevated, with no prompt.

## Uninstall

```powershell
pwsh -File deploy/Uninstall.ps1               # remove task, shortcut and launch.vbs
pwsh -File deploy/Uninstall.ps1 -RemoveFiles  # also delete the install directory
```

## Requirements

- .NET 10 SDK (for `dotnet publish`).
- **PawnIO** — for CPU temperature, installed once from [pawnio.eu](https://pawnio.eu).
  Without it everything works except CPU temperature, and the vulnerable WinRing0 driver is
  never loaded.

## Note on encoding

`Install.ps1` and `Uninstall.ps1` are deliberately **ASCII-only**. A script containing
non-ASCII characters, saved as UTF-8 without a BOM and re-read by `powershell.exe`
(Windows PowerShell 5.1) under the ANSI code page, gets corrupted — that once caused the
task to be registered under a mojibake folder name. Keep executable `.ps1` text ASCII.

## Releasing

Maintainer steps for cutting a version. All of it is manual: CI builds and tests, it does
not publish. Every command below runs from the repository root.

1. Bump `<Version>` in `Directory.Build.props`. It stamps the assembly, so the exe reports
   the same number as the tag.
2. In [CHANGELOG.md](../CHANGELOG.md), turn `## [Unreleased]` into a
   `## [X.Y.Z] - YYYY-MM-DD` section, leave a fresh empty `## [Unreleased]` above it, and
   update the two link definitions at the bottom of the file.
3. `dotnet build -c Release` and `dotnet test -c Release` must both pass — the build fails
   on any warning.
4. Commit and push, then tag that commit:

   ```powershell
   git tag -a vX.Y.Z -m "SystemBar Indicator X.Y.Z"
   git push origin vX.Y.Z
   ```

5. Publish the build **outside OneDrive** and package it. `LICENSE` goes in because MIT
   requires the notice to travel with binaries; `README.txt` carries the quick start, the
   SmartScreen warning and the third-party licences (LibreHardwareMonitorLib is MPL-2.0):

   ```powershell
   $stage = "$env:TEMP\SystemBarIndicator-X.Y.Z"
   dotnet publish src\SystemBarIndicator.App -c Release -r win-x64 --self-contained false `
       -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $stage
   Copy-Item LICENSE -Destination $stage
   # write $stage\README.txt as well
   Compress-Archive -Path $stage -DestinationPath "$env:TEMP\SystemBarIndicator-win-x64.zip"
   ```

   Pack the **folder**, not its contents: the version then survives as the directory name
   inside the archive, while the asset name itself stays constant — the main README links to
   `/releases/latest`, so a version in the file name would break it on the next release.

6. Write the checksum file — the in-app auto-updater verifies the download against it
   (a release without one still installs, but with the verification skipped):

   ```powershell
   $hash = (Get-FileHash "$env:TEMP\SystemBarIndicator-win-x64.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
   Set-Content "$env:TEMP\SHA256SUMS.txt" -Value "$hash *SystemBarIndicator-win-x64.zip" -Encoding ascii
   ```

7. Create the release with notes and both assets:

   ```powershell
   gh release create vX.Y.Z --title "SystemBar Indicator X.Y.Z" `
       --notes-file notes.md --verify-tag `
       "$env:TEMP\SystemBarIndicator-win-x64.zip" "$env:TEMP\SHA256SUMS.txt"
   ```

   The auto-updater in installed copies relies on this shape: a plain `vX.Y.Z` tag (a
   suffixed tag like `v1.1.0-beta` is ignored), a single `.zip` asset with `win-x64` in
   its name, the exe at the archive root or one folder down, and `SHA256SUMS.txt` in the
   `<hash> *<filename>` format.

8. Unpack the published archive into an empty folder and start the exe once. A run from
   `bin` proves nothing about the packaged layout — LHM 0.9.6 is a RID-split package, and a
   file missing from the flat publish only shows up here.
