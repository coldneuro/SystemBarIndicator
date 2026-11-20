using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBarIndicator.Updates
{
    /// <summary>
    /// Downloads a release, verifies and unpacks it into a staging folder, then hands the
    /// file swap to an external script: a running exe cannot replace its own files. The
    /// script waits for the app to exit, copies the new build over the install folder and
    /// restarts the app through its scheduled task (elevated, no UAC prompt).
    /// </summary>
    public static class UpdateInstaller
    {
        private const string TaskName = "SystemBarIndicator";
        private const string ExeName = "SystemBarIndicator.exe";

        // Generous for an archive of a few tens of megabytes, yet finite.
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        private static string StagingRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemBarIndicator", "update");

        /// <summary>
        /// Stages the update and starts the swap script. True means the script is running
        /// and the app must exit now so its files can be replaced.
        /// </summary>
        public static async Task<bool> InstallAsync(UpdateInfo info, CancellationToken ct)
        {
            string staging = StagingRoot;
            TryCleanStaging();
            Directory.CreateDirectory(staging);

            // HttpClient.Timeout stops at the response headers: with ResponseHeadersRead the
            // body is streamed afterwards and nothing bounds it, so a stalled download would
            // hang forever — and with it the "one update at a time" guard, which would block
            // every later check until the app restarts.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(DownloadTimeout);

            string zipPath = Path.Combine(staging, info.ZipAssetName);
            await DownloadAsync(info.ZipUrl, zipPath, deadline.Token).ConfigureAwait(false);
            await VerifyAsync(info, zipPath, deadline.Token).ConfigureAwait(false);

            // ExtractToDirectory refuses entries that escape the destination (zip-slip).
            string extracted = Path.Combine(staging, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extracted);

            // The archive carries the publish inside a versioned folder (the release
            // procedure packs the folder, not its contents) — but accept a flat root too.
            string source = FindPayload(extracted)
                ?? throw new InvalidOperationException($"the downloaded archive contains no {ExeName}");

            string script = WriteSwapScript(staging);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-OwnerPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-Source");
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add("-Target");
            psi.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            psi.ArgumentList.Add("-Backup");
            psi.ArgumentList.Add(Path.Combine(staging, "backup"));
            psi.ArgumentList.Add("-TaskName");
            psi.ArgumentList.Add(TaskName);
            psi.ArgumentList.Add("-LogPath");
            psi.ArgumentList.Add(Logger.LogPath);

            using var process = Process.Start(psi);
            return process != null;
        }

        /// <summary>
        /// Removes leftovers of a finished (or failed) update. Called on startup and before
        /// each install; a locked file simply postpones the cleanup to the next run.
        /// </summary>
        public static void TryCleanStaging()
        {
            try
            {
                if (Directory.Exists(StagingRoot))
                    Directory.Delete(StagingRoot, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"update staging cleanup failed: {ex.Message}");
            }
        }

        private static async Task DownloadAsync(string url, string path, CancellationToken ct)
        {
            using var response = await UpdateChecker.Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        private static async Task VerifyAsync(UpdateInfo info, string zipPath, CancellationToken ct)
        {
            // Older releases have no checksum asset; the download still came over TLS from
            // the project's own release, so that only downgrades the check, not the update.
            if (info.ChecksumsUrl == null)
            {
                Logger.Warn("update: release has no SHA256SUMS.txt, skipping checksum verification");
                return;
            }

            // A published checksum file that fails to parse or match is an abort, not a
            // shrug: at that point something is genuinely wrong with the release.
            string checksums = await UpdateChecker.Http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
            string expected = ReleaseChecksums.FindHash(checksums, info.ZipAssetName)
                ?? throw new InvalidOperationException($"SHA256SUMS.txt has no entry for {info.ZipAssetName}");

            string actual;
            await using (var file = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false))
                    .ToLowerInvariant();
            }

            if (actual != expected)
                throw new InvalidOperationException($"checksum mismatch for {info.ZipAssetName}");
        }

        private static string? FindPayload(string extractedRoot)
        {
            if (File.Exists(Path.Combine(extractedRoot, ExeName)))
                return extractedRoot;

            foreach (var dir in Directory.GetDirectories(extractedRoot))
            {
                if (File.Exists(Path.Combine(dir, ExeName)))
                    return dir;
            }
            return null;
        }

        // The script is written in ASCII on purpose: powershell.exe re-reads BOM-less
        // files under the ANSI code page, and non-ASCII text gets corrupted (see
        // deploy/README.md#note-on-encoding).
        private static string WriteSwapScript(string staging)
        {
            const string script = """
                param(
                    [Parameter(Mandatory = $true)][int]$OwnerPid,
                    [Parameter(Mandatory = $true)][string]$Source,
                    [Parameter(Mandatory = $true)][string]$Target,
                    [Parameter(Mandatory = $true)][string]$Backup,
                    [Parameter(Mandatory = $true)][string]$TaskName,
                    [Parameter(Mandatory = $true)][string]$LogPath
                )

                $script:LastError = ''

                # Same line format as the app's own log.
                function Write-Log([string]$Level, [string]$Message) {
                    try {
                        $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
                        Add-Content -LiteralPath $LogPath -Value "$stamp $Level [swap.ps1] $Message" -Encoding ASCII
                    } catch { }
                }

                # The files can stay locked for a moment after the process object dies.
                function Copy-Tree([string]$From, [string]$To) {
                    for ($i = 0; $i -lt 10; $i++) {
                        try {
                            New-Item -ItemType Directory -Path $To -Force | Out-Null
                            Copy-Item -Path (Join-Path $From '*') -Destination $To -Recurse -Force -ErrorAction Stop
                            return $true
                        } catch {
                            $script:LastError = $_.Exception.Message
                            Start-Sleep -Seconds 1
                        }
                    }
                    return $false
                }

                # Wait for the app to let go of its files.
                Wait-Process -Id $OwnerPid -Timeout 60 -ErrorAction SilentlyContinue

                # Back up the current build first: a copy that fails halfway would otherwise
                # leave a mix of old and new files that may not start at all.
                if (-not (Copy-Tree $Target $Backup)) {
                    Write-Log 'ERROR' "could not back up the installed build, update abandoned: $script:LastError"
                } elseif (Copy-Tree $Source $Target) {
                    Write-Log 'WARN ' "installed the update from $Source"
                } else {
                    Write-Log 'ERROR' "could not copy the update, restoring the previous build: $script:LastError"
                    if (-not (Copy-Tree $Backup $Target)) {
                        Write-Log 'ERROR' "restoring the previous build failed as well: $script:LastError"
                    }
                }

                # Restart whatever ended up installed, even after a failure: an update that
                # did not take must not leave the user without the bar.
                # The scheduled task restarts the app elevated without a UAC prompt; a copy
                # installed without the task is started directly instead.
                schtasks.exe /run /tn $TaskName 2>$null | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Start-Process -FilePath (Join-Path $Target 'SystemBarIndicator.exe')
                }
                """;

            string path = Path.Combine(staging, "swap.ps1");
            File.WriteAllText(path, script, Encoding.ASCII);
            return path;
        }
    }
}
