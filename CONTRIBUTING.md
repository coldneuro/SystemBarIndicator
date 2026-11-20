# Contributing

Thanks for taking a look. Issues and pull requests are welcome.

## Getting set up

```powershell
git clone https://github.com/coldneuro/SystemBarIndicator.git
cd SystemBarIndicator
dotnet build -c Release
dotnet test  -c Release
dotnet run --project src/SystemBarIndicator.App
```

You need the .NET 10 SDK and Windows. Running from `dotnet run` gives you an unelevated
process, so the CPU temperature will be missing (GPU temperatures still show on NVIDIA
cards) — that is expected, and it is the quickest way to work on everything else. To test
the CPU temperature path, install with `pwsh -File deploy/Install.ps1` and launch through the
desktop shortcut.

## Ground rules

- **`TreatWarningsAsErrors` is on** (`Directory.Build.props`). The build must stay
  warning-free; `nullable` is enabled everywhere.
- **Put testable logic in `Core`.** `SystemBarIndicator.Core` targets plain `net10.0` and
  has no WinForms or hardware dependencies, so it can be unit-tested. Formatting rules,
  thresholds, smoothing and aggregation belong there, with tests. `App` holds the UI and
  everything that touches the OS (P/Invoke, `PerformanceCounter`, LibreHardwareMonitor).
- **Read `CLAUDE.md` before touching the rendering or window code.** It documents the
  non-obvious constraints — why slot widths cannot be cached between frames, why the bar
  stores its right edge rather than its left, how the click gesture is decoded out of
  `SC_MOVE`, and more. Several of those look like redundant complexity until you hit the
  bug they prevent.
- **Keep `.ps1` files in `deploy/` ASCII-only.** A script with non-ASCII characters saved
  as UTF-8 without a BOM and re-read by `powershell.exe` under the ANSI code page gets
  mangled — this once broke the scheduled task registration.
- **English** for code, comments, documentation and commit messages.

## Pull requests

1. Branch off `main`.
2. Keep the change focused; unrelated cleanups are easier to review separately.
3. Make sure `dotnet build -c Release` and `dotnet test -c Release` both pass — CI runs
   exactly these on `windows-latest`.
4. Add tests for anything you put in `Core`.
5. Add an entry under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) if the change is
   visible to users. Internal refactors and test-only changes do not need one.
6. Describe what you changed and how you verified it. For UI changes, a screenshot helps.

## Releasing

Maintainer-only, and manual: bump `<Version>`, close the `Unreleased` section of the
changelog, tag, then publish a `win-x64` zip with the release. The exact steps are in
[deploy/README.md](deploy/README.md#releasing).

## Commit messages

Conventional-commit style prefixes are used: `feat:`, `fix:`, `chore:`, `docs:`, `ci:`,
`refactor:`, `test:`. Write the subject in the imperative mood.

## Reporting bugs

Include your Windows version, whether the app was elevated, your CPU/GPU model, and the
relevant part of `%LOCALAPPDATA%\SystemBarIndicator\app.log`. For anything with a security
angle, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.
