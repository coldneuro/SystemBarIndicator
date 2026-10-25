using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using SystemBarIndicator.Configuration;
using SystemBarIndicator.Models;
using SystemBarIndicator.Monitoring;
using SystemBarIndicator.Sensors;
using SystemBarIndicator.Updates;
using SystemBarIndicator.Util;

namespace SystemBarIndicator
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // One instance per session: a second launch (including a double-click on the
            // shortcut) simply exits, so two bars never appear.
            System.Threading.Mutex instanceLock;
            bool isNewInstance;
            try
            {
                instanceLock = new System.Threading.Mutex(initiallyOwned: true, @"Local\SystemBarIndicator_SingleInstance", out isNewInstance);
            }
            catch (UnauthorizedAccessException)
            {
                return; // an instance is already running (possibly at another elevation level)
            }

            using (instanceLock)
            {
                if (!isNewInstance)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.Run(new MainForm());
            }
        }
    }

    public class MainForm : Form
    {
        public enum TaskbarEdge { Bottom, Top }

        // --- Win32: window management ---
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const uint ABE_TOP = 1;
        private const uint ABE_BOTTOM = 3;

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCAPTION = 2;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_DISPLAYCHANGE = 0x007E;
        // The window returns HTCAPTION for any hit-test, so the system turns a press straight
        // into SC_MOVE: client-area WM_LBUTTONDOWN/UP never occur, the non-client
        // WM_NCLBUTTONUP is swallowed, and WM_ENTERSIZEMOVE does not arrive at all for a
        // click without movement. The only place the whole gesture is visible is SC_MOVE.
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        // The other side of that coin: the system reads the remaining title-bar gestures the same
        // way — a double-click maximizes the window and a right-click opens the system menu (which
        // offers "Maximize" as well). Both are intercepted in WndProc.
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_NCRBUTTONUP = 0x00A5;
        private const int SC_MAXIMIZE = 0xF030;

        // --- Bar geometry ---
        private const int EdgePad = 8;      // padding from the window edges
        private const int ValueGap = 10;    // after a percentage value
        private const int TempGap = 6;      // after a temperature
        private const int SeparatorGap = 8; // after a separator

        // Update checks: the first one shortly after start, then daily while running.
        private const int FirstUpdateCheckDelayMs = 60_000;
        private const int UpdateCheckIntervalMs = 24 * 60 * 60 * 1000;

        // Auto-update is compiled out of Debug builds: a developer copy running from bin
        // must never replace itself with a release download. The manual tray-menu check
        // stays available everywhere. A readonly field, not a const — a const would turn
        // the checks into unreachable code and fail the warning-free build.
#if DEBUG
        private static readonly bool AutoUpdateSupported = false;
#else
        private static readonly bool AutoUpdateSupported = true;
#endif

        // The base smoothing coefficients are calibrated for a 1 s tick; at any other
        // interval they are recomputed (SmoothingRates).
        private const float NetAlphaPerSecond = 0.4f;
        private const float DiskAlphaPerSecond = 0.4f;
        private const float ScaleDecayPerSecond = 0.97f;

        // Numeric values are drawn right-aligned in a fixed-width slot, so sections do not
        // breathe as the digit count changes (9% ↔ 10%).
        private static readonly StringFormat ValueRightAligned = new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = StringAlignment.Far,
            FormatFlags = StringFormatFlags.NoWrap
        };


        // --- Sensors ---
        private CpuSensor? _cpuSensor;
        private readonly RamSensor _ramSensor = new();
        private DiskSensor? _diskSensor;
        private NetworkSensor? _networkSensor;
        private HardwareSensor? _hardwareSensor;

        // --- State and UI ---
        private readonly Timer _timer;
        private readonly Timer _topmostTimer;
        private readonly Timer _saveTimer;
        private readonly Timer _updateCheckTimer;
        private bool _updateBusy; // one update check or install at a time
        // Identity of the last logged check failure, so a permanently unreachable network
        // writes one line instead of one per check.
        private string? _lastUpdateFailureKey;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _menu; // shared by the tray icon and the bar itself
        private readonly DataHistory _history;
        private GraphOverlay? _graphOverlay;
        private SettingsForm? _settingsForm;
        private readonly Icon _appIcon;

        private readonly SensorData _current = new();

        // --- Settings ---
private readonly string _settingsPath = SettingsStore.DefaultPath;
        private AppSettings _settings;
        private BarTheme _theme;
        private bool _settingsDirty;
        // The position counts as user-defined only if the user dragged the bar themselves.
        // Otherwise the bar must follow the taskbar on every start.
        private bool _positionUserDefined;
        private bool _positionTrackingReady;

        // Sensor availability latches: once a section has appeared it never disappears, so a
        // brief sensor dropout (null → 0) does not shift the layout.
        private bool _hasCpuTemp;
        private bool _hasGpu;
        private bool _gpuUsageAvailable;
        private bool _hasGpuTemp;
        private bool _hasGpuVramTemp;
        private bool _hasGpuVramUsage;
        // Disk works the other way round: it is available until the counters prove otherwise
        // (they can be disabled machine-wide), and it never comes back within a run.
        private bool _diskAvailable;

        private int _pendingBarWidth; // 0 means no width adjustment is scheduled

        // Smoothing and auto-scaling for the inherently jagged network and disk indicators.
        private readonly Ema _netDownEma = new(NetAlphaPerSecond);
        private readonly Ema _netUpEma = new(NetAlphaPerSecond);
        private readonly AutoScale _netScale = new(floor: 64f, decay: ScaleDecayPerSecond);   // KB/s
        private readonly Ema _diskReadEma = new(DiskAlphaPerSecond);
        private readonly Ema _diskWriteEma = new(DiskAlphaPerSecond);
        private readonly AutoScale _diskScale = new(floor: 8f, decay: ScaleDecayPerSecond);   // MB/s

        public TaskbarEdge BarEdge { get; private set; } = TaskbarEdge.Bottom;

        /// <summary>Sensor availability — the settings window marks unavailable sections with it.</summary>
        public bool HasCpuTempSensor => _hasCpuTemp;
        public bool HasGpuSensor => _hasGpu;
        public bool HasGpuTempSensor => _hasGpuTemp;
        public bool HasGpuVramTempSensor => _hasGpuVramTemp;
        public bool HasGpuVramUsageSensor => _hasGpuVramUsage;
        public bool HasDiskSensor => _diskAvailable;

        private SensorData GetCurrentData() => _current;

        public MainForm()
        {
            InitSensors();

            _history = new DataHistory(120);

            _settings = SettingsStore.Load(_settingsPath, out var settingsError);
            if (settingsError != null)
                Logger.Error("SettingsStore.Load", settingsError);
            _positionUserDefined = _settings.BarRight.HasValue && _settings.BarTop.HasValue;
            _theme = BarTheme.Create(_settings);

            _appIcon = LoadAppIcon();
            this.Icon = _appIcon;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = true;
            this.TopMost = true;
            this.BackColor = _theme.Background;
            this.Opacity = _settings.Opacity;
            this.DoubleBuffered = true;
            this.Size = new Size(520, 32);
            this.StartPosition = FormStartPosition.Manual;
            this.LocationChanged += (s, e) =>
            {
                if (_graphOverlay != null && !_graphOverlay.IsDisposed)
                    _graphOverlay.PositionAboveParent();
            };

            // The tray icon and its menu come first: the timers below refer to the menu.
            _trayIcon = new NotifyIcon
            {
                Visible = true,
                Text = "SystemBar Indicator",
                Icon = _appIcon
            };
            _trayIcon.MouseClick += TrayIconClick;

            _menu = new ContextMenuStrip();
            _menu.Items.Add("Graphs", null, (s, e) => OpenGraphOverlay());
            _menu.Items.Add("Settings...", null, (s, e) => OpenSettings());
            _menu.Items.Add("Reset Position", null, (s, e) => ResetPosition());
            _menu.Items.Add("Check for Updates...", null, async (s, e) => await RunUpdateCheckAsync(manual: true));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, (s, e) => { _trayIcon.Visible = false; Application.Exit(); });
            _trayIcon.ContextMenuStrip = _menu;

            _timer = new Timer { Interval = _settings.UpdateIntervalMs };
            _timer.Tick += TimerTick;
            ApplySmoothingRates(_settings.UpdateIntervalMs);
            _timer.Start();

            // Staying on top is handled by WM_WINDOWPOSCHANGING; this timer is only a rare
            // safety net (it used to run at an aggressive 200 ms interval).
            _topmostTimer = new Timer { Interval = 1000 };
            // Not while the menu is open: re-asserting HWND_TOPMOST would lift the bar above the
            // drop-down and cover it.
            _topmostTimer.Tick += (s, e) => { if (!_menu.Visible) ForceTopMost(); };
            _topmostTimer.Start();

            // Settings writes are deferred: dragging the bar and moving a slider must not
            // cause a storm of disk writes.
            _saveTimer = new Timer { Interval = 1500 };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); FlushSettings(); };

            // A WinForms timer, so the async continuations land back on the UI thread.
            _updateCheckTimer = new Timer { Interval = FirstUpdateCheckDelayMs };
            _updateCheckTimer.Tick += async (s, e) =>
            {
                _updateCheckTimer.Interval = UpdateCheckIntervalMs;
                if (_settings.AutoUpdate)
                    await RunUpdateCheckAsync(manual: false);
            };
            if (AutoUpdateSupported)
            {
                UpdateInstaller.TryCleanStaging(); // leftovers of the update that started this build
                _updateCheckTimer.Start();
            }
        }

        private void InitSensors()
        {
            try { _cpuSensor = new CpuSensor(); }
            catch (Exception ex) { Logger.Error("CpuSensor.init", ex); }

            try { _diskSensor = new DiskSensor(); }
            catch (Exception ex)
            {
                // Expected where the performance-counter service is disabled: the section is
                // hidden rather than drawn as permanently idle.
                if (PerfCounters.IsExpectedFailure(ex))
                    Logger.Warn($"disk performance counters unavailable ({PerfCounters.Describe(ex)}); " +
                                "disk activity will not be shown");
                else
                    Logger.Error("DiskSensor.init", ex);
            }

            _diskAvailable = _diskSensor?.Available == true;

            try { _networkSensor = new NetworkSensor(); }
            catch (Exception ex) { Logger.Error("NetworkSensor.init", ex); }

            try { _hardwareSensor = new HardwareSensor(); }
            catch (Exception ex) { Logger.Error("HardwareSensor.init", ex); }
        }

        // The icon is embedded in the assembly as a resource (see EmbeddedResource in the
        // .csproj). It is a multi-size .ico: Windows picks the right frame itself (16 px for
        // the tray, larger ones for the window and taskbar).
        private static Icon LoadAppIcon()
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("SystemBarIndicator.appicon.ico");
            return stream != null ? new Icon(stream) : SystemIcons.Application;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PositionOnTaskbar();
            TryRestoreSavedPosition();
            _positionTrackingReady = true;
            UpdateData();
            MaybeShowPerfCounterNotice();
        }

        /// <summary>
        /// Tells the user once why the disk section is missing. Deliberately narrow: it fires
        /// only when something visible was actually lost — CPU load survives on the
        /// GetSystemTimes fallback, so a working CPU section must not be called "limited".
        /// The notice is not a repair instruction either: a failed counter can mean disabled,
        /// damaged or unreadable counters, and those need different fixes (see the README).
        /// A tray balloon can be suppressed by Windows, so the settings "(n/a)" marker and the
        /// README remain the durable explanation.
        /// </summary>
        private void MaybeShowPerfCounterNotice()
        {
            if (_diskAvailable)
            {
                // Counters are healthy: re-arm, so a later breakage is reported again.
                if (_settings.PerfCounterNoticeShown)
                {
                    _settings.PerfCounterNoticeShown = false;
                    MarkSettingsDirty();
                }
                return;
            }

            if (_settings.PerfCounterNoticeShown || !_settings.ShowDisk)
                return;

            _settings.PerfCounterNoticeShown = true;
            MarkSettingsDirty();

            _trayIcon.ShowBalloonTip(10000, "SystemBar Indicator",
                "Windows performance counters are unavailable, so disk activity cannot be shown. " +
                "CPU usage is measured another way and is unaffected — see Troubleshooting in the README.",
                ToolTipIcon.Info);
        }

        private void PositionOnTaskbar()
        {
            // The real width is fitted to the content after the first measurement
            // (ApplyBarWidth); the current one is used here so "Reset Position" does not
            // snap back to the initial 520.
            int barWidth = this.Width;
            const int margin = 10;

            var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            {
                RECT rc = abd.rc;
                bool horizontal = abd.uEdge == ABE_TOP || abd.uEdge == ABE_BOTTOM;

                if (horizontal)
                {
                    int tbH = rc.Bottom - rc.Top;
                    int height = Math.Max(tbH - 4, 28);
                    this.Size = new Size(barWidth, height);
                    this.Location = new Point(rc.Right - barWidth - margin, rc.Top + (tbH - height) / 2);
                    BarEdge = abd.uEdge == ABE_TOP ? TaskbarEdge.Top : TaskbarEdge.Bottom;
                }
                else
                {
                    // Vertical taskbar (left/right, Windows 10): dock to the bottom edge of
                    // the screen's working area.
                    var wa = SystemInformation.WorkingArea;
                    this.Size = new Size(barWidth, 30);
                    this.Location = new Point(wa.Right - barWidth - margin, wa.Bottom - 34);
                    BarEdge = TaskbarEdge.Bottom;
                }
            }
            else
            {
                int sw = GetSystemMetrics(SM_CXSCREEN);
                int sh = GetSystemMetrics(SM_CYSCREEN);
                this.Size = new Size(barWidth, 32);
                this.Location = new Point(sw - barWidth - margin, sh - 40);
                BarEdge = TaskbarEdge.Bottom;
            }

            ForceTopMost();
        }

        /// <summary>
        /// Returns the bar to where the user left it. The RIGHT edge is what gets restored:
        /// the window width is still the initial estimate and will be fitted to the content
        /// after the first frame, and that fit (<see cref="ApplyPendingBarWidth"/>) holds the
        /// right edge. The height is not restored — it is derived from the taskbar.
        /// </summary>
        private void TryRestoreSavedPosition()
        {
            if (!_settings.BarRight.HasValue || !_settings.BarTop.HasValue)
                return;

            var screens = ScreenBounds();
            var candidate = new Rectangle(_settings.BarRight.Value - this.Width, _settings.BarTop.Value, this.Width, this.Height);

            if (!WindowPlacement.IsUsable(candidate, screens))
            {
                var clamped = WindowPlacement.ClampToScreens(candidate, screens);
                if (!WindowPlacement.IsUsable(clamped, screens))
                {
                    Logger.Warn($"Saved bar position is unreachable ({candidate}); docking to the taskbar.");
                    return;
                }

                // Remember the repaired position, otherwise the file would keep an
                // unreachable point forever and the clamp would repeat on every start.
                Logger.Warn($"Saved bar position adjusted to the current screens: {candidate} → {clamped}.");
                candidate = clamped;
                _settings.BarRight = clamped.Right;
                _settings.BarTop = clamped.Top;
                MarkSettingsDirty();
            }

            this.Location = candidate.Location;
            UpdateBarEdgeFromPosition();
            ForceTopMost();
        }

        /// <summary>
        /// Which way the overlay opens is decided from the bar's actual position: once
        /// dragged into the top half of the screen, the graphs must drop downwards.
        /// </summary>
        private void UpdateBarEdgeFromPosition()
        {
            var area = Screen.FromRectangle(this.Bounds).WorkingArea;
            BarEdge = this.Top + this.Height / 2 < area.Top + area.Height / 2
                ? TaskbarEdge.Top
                : TaskbarEdge.Bottom;
        }

        private static Rectangle[] ScreenBounds()
        {
            var screens = Screen.AllScreens;
            var bounds = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++)
                bounds[i] = screens[i].Bounds;
            return bounds;
        }

        private void ForceTopMost()
        {
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void TimerTick(object? sender, EventArgs e)
        {
            UpdateData();
        }

        private void UpdateData()
        {
            try
            {
                _current.CpuUsage = _cpuSensor?.GetUsage() ?? 0f;
                _current.GpuUsage = 0f;
                _gpuUsageAvailable = false;

                if (_hardwareSensor != null)
                {
                    _hardwareSensor.Update();
                    _current.CpuTemperature = _hardwareSensor.GetCpuTemperature() ?? 0f;
                    float? gpuUsage = _hardwareSensor.GetGpuUsage();
                    _gpuUsageAvailable = gpuUsage.HasValue;
                    _current.GpuUsage = gpuUsage ?? 0f;
                    _current.GpuTemperature = _hardwareSensor.GetGpuTemperature() ?? 0f;
                    _current.GpuVramTemperature = _hardwareSensor.GetGpuVramTemperature() ?? 0f;
                    _current.GpuVramUsage = _hardwareSensor.GetGpuVramUsage() ?? 0f;

                    var gpuMem = _hardwareSensor.GetGpuMemory();
                    if (gpuMem.HasValue)
                    {
                        _current.GpuMemoryUsed = gpuMem.Value.used ?? 0f;
                        _current.GpuMemoryTotal = gpuMem.Value.total ?? 0f;
                    }
                }

                _hasCpuTemp |= _current.CpuTemperature > 0f;
                _hasGpu |= _gpuUsageAvailable || _current.GpuTemperature > 0f;
                _hasGpuTemp |= _current.GpuTemperature > 0f;
                _hasGpuVramTemp |= _current.GpuVramTemperature > 0f;
                _hasGpuVramUsage |= _current.GpuVramUsage > 0f;

                var ram = _ramSensor.GetInfo();
                _current.RamUsage = ram.usagePercent;
                _current.RamUsed = ram.usedBytes;
                _current.RamTotal = ram.totalBytes;

                if (_diskSensor != null)
                {
                    var disk = _diskSensor.GetInfo();
                    _current.DiskRead = disk.readMBps;
                    _current.DiskWrite = disk.writeMBps;
                    _current.DiskUsagePercent = disk.usagePercent;
                    // The sensor gives up permanently if its counters break mid-run.
                    _diskAvailable &= _diskSensor.Available;
                }

                float dskR = _diskReadEma.Update(_current.DiskRead);
                float dskW = _diskWriteEma.Update(_current.DiskWrite);
                _diskScale.Update(Math.Max(dskR, dskW));

                if (_networkSensor != null)
                {
                    var net = _networkSensor.GetInfo();
                    _current.NetDown = net.downloadKBps;
                    _current.NetUp = net.uploadKBps;
                }

                // Smooth the instantaneous rate and update the shared bar scale from the peak.
                float netDl = _netDownEma.Update(_current.NetDown);
                float netUl = _netUpEma.Update(_current.NetUp);
                _netScale.Update(Math.Max(netDl, netUl));

                _trayIcon.Text = string.Format("CPU: {0:F0}% | RAM: {1:F0}% | GPU: {2}",
                    _current.CpuUsage, _current.RamUsage,
                    _gpuUsageAvailable ? Formatting.FormatPercent(_current.GpuUsage) : "n/a");

                this.Invalidate();

                _history.Add(new DataPoint
                {
                    Timestamp = DateTime.Now,
                    CpuUsage = _current.CpuUsage,
                    CpuTemperature = _current.CpuTemperature,
                    GpuUsage = _current.GpuUsage,
                    GpuTemperature = _current.GpuTemperature,
                    GpuVramTemperature = _current.GpuVramTemperature,
                    GpuVramUsage = _current.GpuVramUsage,
                    RamUsage = _current.RamUsage,
                    DiskRead = _current.DiskRead,
                    DiskWrite = _current.DiskWrite,
                    NetDown = _current.NetDown,
                    NetUp = _current.NetUp
                });
            }
            catch (Exception ex)
            {
                Logger.Error("UpdateData", ex);
            }
        }

        private void OpenGraphOverlay()
        {
            if (_graphOverlay == null || _graphOverlay.IsDisposed)
            {
                _graphOverlay = new GraphOverlay(_history, GetCurrentData, this, _theme, _settings);
                _graphOverlay.PositionAboveParent();
                _graphOverlay.FormClosed += (s, e) => { _graphOverlay = null; };
                _graphOverlay.Show();
            }
            else
            {
                _graphOverlay.Close();
                _graphOverlay = null;
            }
        }

        private void ShowBarMenu()
        {
            // A drop-down whose owner is not the foreground window does not close on a click
            // outside it, so the bar is pulled forward first.
            SetForegroundWindow(this.Handle);
            _menu.Show(Cursor.Position);
        }

        private void TrayIconClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenGraphOverlay();
            }
        }

        // --- Auto-update ---

        /// <summary>
        /// One pass of check → confirm → install. Runs on the UI thread (awaits keep it
        /// responsive); a manual pass reports every outcome, the daily automatic one only
        /// logs, so a machine that is offline for a week stays quiet.
        /// </summary>
        private async Task RunUpdateCheckAsync(bool manual)
        {
            if (_updateBusy)
                return;
            _updateBusy = true;
            try
            {
                UpdateInfo? update;
                try
                {
                    update = await new UpdateChecker().CheckAsync(System.Threading.CancellationToken.None);
                    _lastUpdateFailureKey = null; // a success re-arms the log for the next failure
                }
                catch (Exception ex)
                {
                    LogUpdateCheckFailure(ex, manual);
                    if (manual)
                        MessageBox.Show(this, "Could not check for updates — see app.log for details.",
                            "SystemBar Indicator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (update == null)
                {
                    if (manual)
                        MessageBox.Show(this,
                            $"You are running the latest version ({UpdateChecker.CurrentVersion().ToString(3)}).",
                            "SystemBar Indicator", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (manual)
                {
                    var answer = MessageBox.Show(this,
                        $"Version {update.Version.ToString(3)} is available (installed: {UpdateChecker.CurrentVersion().ToString(3)}).\n\n" +
                        "Install now? The bar will restart.",
                        "SystemBar Indicator", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes)
                        return;
                }
                else
                {
                    _trayIcon.ShowBalloonTip(5000, "SystemBar Indicator",
                        $"Updating to version {update.Version.ToString(3)}...", ToolTipIcon.Info);
                }

                try
                {
                    // True means the swap script is waiting for this process to exit.
                    if (await UpdateInstaller.InstallAsync(update, System.Threading.CancellationToken.None))
                    {
                        _trayIcon.Visible = false;
                        Application.Exit();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("UpdateInstaller.Install", ex);
                    if (manual)
                        MessageBox.Show(this, "The update failed — see app.log for details.",
                            "SystemBar Indicator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                _updateBusy = false;
            }
        }

        /// <summary>
        /// Logs a failed check at the severity it deserves. An unreachable host is a fact of
        /// life, not a defect, and repeating the same one daily would bury the log — but a
        /// 404 from a renamed repository must not be softened into a warning either.
        /// </summary>
        private void LogUpdateCheckFailure(Exception ex, bool manual)
        {
            var failure = UpdateFailures.Classify(ex, cancellationRequested: false);
            bool repeat = failure.Key == _lastUpdateFailureKey;
            _lastUpdateFailureKey = failure.Key;

            // A check the user asked for always leaves a trace; the daily one only when the
            // kind of failure changed.
            if (repeat && !manual)
                return;

            if (failure.IsTransient)
                Logger.Warn($"update check failed ({failure.Key}): {ex.GetType().Name}: {ex.Message}");
            else
                Logger.Error("UpdateChecker.Check", ex);
        }

        // --- Settings ---

        /// <summary>A copy of the current settings — the settings window edits this one, not the live copy.</summary>
        public AppSettings CurrentSettings => _settings.Clone();

        private void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.Activate();
                return;
            }

            // Ownership rather than TopMost: the bar forces itself to the top of the z-order
            // once a second and would cover an ordinary window, whereas ownership fixes the
            // order reliably.
            _settingsForm = new SettingsForm(this);
            _settingsForm.FormClosed += (s, e) => { _settingsForm = null; FlushSettings(); };
            _settingsForm.Show(this);
        }

        /// <summary>
        /// Applies the appearance settings. The bar position is deliberately excluded: it is
        /// governed only by dragging and "Reset Position", otherwise an open settings window
        /// would overwrite the position with its own stale snapshot.
        /// </summary>
        public void ApplySettings(AppSettings incoming)
        {
            ArgumentNullException.ThrowIfNull(incoming);

            var next = incoming.Normalized();
            next.BarRight = _settings.BarRight;
            next.BarTop = _settings.BarTop;

            bool rebuildTheme = next.DiffersInAppearance(_settings);
            var previous = _settings;
            _settings = next;

            if (rebuildTheme)
            {
                // Swap the reference first, then dispose the old resources: drawing happens
                // synchronously on this same thread, so a half-drawn frame is impossible.
                var oldTheme = _theme;
                _theme = BarTheme.Create(next);
                _graphOverlay?.ApplyTheme(_theme, next);
                oldTheme.Dispose();
                this.BackColor = _theme.Background;
            }
            else
            {
                _graphOverlay?.ApplyTheme(_theme, next);
            }

            if (this.Opacity != next.Opacity)
                this.Opacity = next.Opacity;

            if (previous.UpdateIntervalMs != next.UpdateIntervalMs)
            {
                _timer.Interval = next.UpdateIntervalMs;
                ApplySmoothingRates(next.UpdateIntervalMs);
            }

            this.Invalidate();
            MarkSettingsDirty();
        }

        /// <summary>
        /// Retunes the smoothing for a new poll step: the base coefficients are calibrated
        /// for 1 s, so without this the indicators would be twice as sluggish at 0.5 s and
        /// jagged at 5 s.
        /// </summary>
        private void ApplySmoothingRates(int intervalMs)
        {
            float netAlpha = SmoothingRates.AlphaForInterval(NetAlphaPerSecond, intervalMs);
            float diskAlpha = SmoothingRates.AlphaForInterval(DiskAlphaPerSecond, intervalMs);
            float decay = SmoothingRates.DecayForInterval(ScaleDecayPerSecond, intervalMs);

            _netDownEma.Alpha = netAlpha;
            _netUpEma.Alpha = netAlpha;
            _diskReadEma.Alpha = diskAlpha;
            _diskWriteEma.Alpha = diskAlpha;
            _netScale.Decay = decay;
            _diskScale.Decay = decay;
        }

        private void MarkSettingsDirty()
        {
            _settingsDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void FlushSettings()
        {
            if (!_settingsDirty)
                return;

            _settingsDirty = false;
            try
            {
                SettingsStore.Save(_settingsPath, _settings);
            }
            catch (Exception ex)
            {
                Logger.Error("SettingsStore.Save", ex);
            }
        }

        // --- Bar position ---

        private void ResetPosition()
        {
            _settings.BarRight = null;
            _settings.BarTop = null;
            _positionUserDefined = false;
            PositionOnTaskbar();
            MarkSettingsDirty();
            FlushSettings();
        }

        /// <summary>
        /// End of a drag. The position is written only when it actually changed — otherwise
        /// every touch of the bar would hit the disk.
        /// </summary>
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (!_positionTrackingReady)
                return;

            int right = this.Left + this.Width;
            if (_settings.BarRight == right && _settings.BarTop == this.Top)
                return;

            _settings.BarRight = right;
            _settings.BarTop = this.Top;
            _positionUserDefined = true;
            UpdateBarEdgeFromPosition();
            MarkSettingsDirty();
        }

        /// <summary>
        /// Monitor reconfiguration: the bar may have been left on a screen that is now gone.
        /// If the user never set a position, simply dock back to the taskbar.
        /// </summary>
        private void ReconcilePositionWithScreens()
        {
            if (this.IsDisposed || !this.IsHandleCreated)
                return;

            if (!_positionUserDefined)
            {
                PositionOnTaskbar();
                return;
            }

            var screens = ScreenBounds();
            if (!WindowPlacement.IsUsable(this.Bounds, screens))
            {
                var clamped = WindowPlacement.ClampToScreens(this.Bounds, screens);
                if (WindowPlacement.IsUsable(clamped, screens))
                {
                    this.Location = clamped.Location;
                    _settings.BarRight = this.Left + this.Width;
                    _settings.BarTop = this.Top;
                    MarkSettingsDirty();
                }
                else
                {
                    Logger.Warn("The bar no longer fits after the monitor change; returning to the taskbar.");
                    PositionOnTaskbar();
                    return;
                }
            }

            UpdateBarEdgeFromPosition();
        }

        // --- Drawing ---

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var s = _settings;

            // The line height is measured on the current Graphics: under PerMonitorV2 it
            // follows the DPI of whichever monitor the window is on, so it cannot be cached.
            int lineHeight = (int)Math.Ceiling(g.MeasureString("100%", _theme.MainFont).Height);
            int y = (this.Height - lineHeight) / 2;

            int x = EdgePad;
            int trailingGap = 0;
            bool anyDrawn = false;

            bool cpuTemp = s.ShowCpuTemp && _hasCpuTemp;
            if (s.ShowCpu || cpuTemp)
            {
                x = Separate(g, x, y, lineHeight, ref anyDrawn);
                if (s.ShowCpu)
                {
                    x = DrawSection(g, "CPU", Formatting.FormatPercent(_current.CpuUsage), _current.CpuUsage / 100f, x, y, lineHeight);
                    x += ValueGap;
                    trailingGap = ValueGap;
                }
                if (cpuTemp)
                {
                    x = DrawTemp(g, _current.CpuTemperature, x, y, lineHeight);
                    x += TempGap;
                    trailingGap = TempGap;
                }
            }

            if (s.ShowRam)
            {
                x = Separate(g, x, y, lineHeight, ref anyDrawn);
                x = DrawSection(g, "RAM", Formatting.FormatPercent(_current.RamUsage), _current.RamUsage / 100f, x, y, lineHeight);
                x += ValueGap;
                trailingGap = ValueGap;
            }

            bool gpu = s.ShowGpu && _hasGpu;
            bool gpuTemp = s.ShowGpuTemp && _hasGpuTemp;
            bool vramUsage = s.ShowGpuVramUsage && _hasGpuVramUsage;
            bool vramTemp = s.ShowGpuVramTemp && _hasGpuVramTemp;
            if (gpu || gpuTemp || vramUsage || vramTemp)
            {
                x = Separate(g, x, y, lineHeight, ref anyDrawn);
                if (gpu)
                {
                    string gpuText = _gpuUsageAvailable ? Formatting.FormatPercent(_current.GpuUsage) : "n/a";
                    x = DrawSection(g, "GPU", gpuText, _current.GpuUsage / 100f, x, y, lineHeight);
                    x += ValueGap;
                    trailingGap = ValueGap;
                }
                if (gpuTemp)
                {
                    x = DrawTemp(g, _current.GpuTemperature, x, y, lineHeight);
                    x += TempGap;
                    trailingGap = TempGap;
                }
                if (vramUsage || vramTemp)
                {
                    // One "VRAM" label in front of both values: a bare percentage and a bare
                    // temperature next to the GPU ones would be unreadable.
                    g.DrawString("VRAM", _theme.SmallFont, _theme.Dim, x, y);
                    x += (int)g.MeasureString("VRAM", _theme.SmallFont).Width + 2;
                    if (vramUsage)
                    {
                        x = DrawValueSlot(g, Formatting.FormatPercent(_current.GpuVramUsage), "100%",
                            GetLoadBrush(_current.GpuVramUsage / 100f), x, y, lineHeight);
                        x += ValueGap;
                        trailingGap = ValueGap;
                    }
                    if (vramTemp)
                    {
                        x = DrawTemp(g, _current.GpuVramTemperature, x, y, lineHeight);
                        x += TempGap;
                        trailingGap = TempGap;
                    }
                }
            }

            if (s.ShowDisk && _diskAvailable)
            {
                x = Separate(g, x, y, lineHeight, ref anyDrawn);
                x = DrawDualBars(g, "D", _diskScale.Fraction(_diskReadEma.Value), _diskScale.Fraction(_diskWriteEma.Value),
                    BarTheme.DiskRead, BarTheme.DiskWrite, x, y, lineHeight);
                trailingGap = 0;
            }

            if (s.ShowNetwork)
            {
                x = Separate(g, x, y, lineHeight, ref anyDrawn);
                x = DrawDualBars(g, "N", _netScale.Fraction(_netDownEma.Value), _netScale.Fraction(_netUpEma.Value),
                    BarTheme.NetDown, BarTheme.NetUp, x, y, lineHeight);
                trailingGap = 0;
            }

            // The content width is known only after measuring at the current DPI (slots are
            // fixed, but their pixel width depends on the monitor, and which sections exist
            // depends on the settings and the latches). The window width is adjusted outside
            // the paint loop, holding the right edge.
            int desiredWidth = x - trailingGap + EdgePad;
            if (desiredWidth != this.Width && _pendingBarWidth != desiredWidth)
            {
                // Coalescing: a single callback is queued and the last measurement wins (on a
                // DPI change the stale value is overwritten before it is applied).
                bool schedule = _pendingBarWidth == 0;
                _pendingBarWidth = desiredWidth;
                if (schedule)
                    BeginInvoke(new Action(ApplyPendingBarWidth));
            }
        }

        // The separator is placed BEFORE a section if anything has been drawn already:
        // otherwise disabled sections would leave dangling bars and stray edge padding.
        private int Separate(Graphics g, int x, int y, int lineHeight, ref bool anyDrawn)
        {
            if (anyDrawn)
            {
                g.DrawLine(_theme.Separator, x, y - 2, x, y + lineHeight);
                x += SeparatorGap;
            }
            anyDrawn = true;
            return x;
        }

        // Changes the window width while keeping the right edge in place (the bar sits at the
        // right side of the taskbar, so it must grow and shrink leftwards).
        private void ApplyPendingBarWidth()
        {
            int width = _pendingBarWidth;
            _pendingBarWidth = 0;
            if (width <= 0 || width == this.Width || this.IsDisposed || !this.IsHandleCreated)
                return;

            int right = this.Left + this.Width;
            var target = new Rectangle(right - width, this.Top, width, this.Height);
            // Growing leftwards must not drag the bar off the left edge of the screen.
            target = WindowPlacement.ClampTo(target, Screen.FromRectangle(target).Bounds);
            this.SetBounds(target.X, target.Y, target.Width, target.Height);
        }

        // Disk and network are drawn as a pair of horizontal bars: ▼ read/download,
        // ▲ write/upload. The values are smoothed (EMA), the bar width is fixed and each
        // resource has its own auto-scale — which is why nothing jumps around.
        private int DrawDualBars(Graphics g, string label, float frac1, float frac2, Brush color1, Brush color2, int x, int y, int lineHeight)
        {
            float scale = (float)_settings.FontScale;
            int barW = (int)Math.Round(34 * scale);
            int barH = (int)Math.Round(8 * scale);
            int barTop = y + (lineHeight - barH) / 2;

            g.DrawString(label, _theme.SmallFont, _theme.Dim, x, y);
            x += (int)g.MeasureString(label, _theme.SmallFont).Width + 4;

            x = DrawBar(g, x, barTop, barW, barH, frac1, color1, down: true, scale);
            x += 6;
            x = DrawBar(g, x, barTop, barW, barH, frac2, color2, down: false, scale);

            return x;
        }

        private int DrawBar(Graphics g, int x, int top, int w, int h, float fraction, Brush color, bool down, float scale)
        {
            float ts = 3f * scale;
            float cx = x + ts;
            float cy = top + h / 2f;
            PointF[] tri = down
                ? new[] { new PointF(cx - ts, cy - ts), new PointF(cx + ts, cy - ts), new PointF(cx, cy + ts) }
                : new[] { new PointF(cx - ts, cy + ts), new PointF(cx + ts, cy + ts), new PointF(cx, cy - ts) };
            g.FillPolygon(color, tri);
            x += (int)(ts * 2) + 4;

            FillRoundRect(g, _theme.Track, x, top, w, h, 3f);
            float fillW = w * fraction;
            if (fillW >= 1f)
                FillRoundRect(g, color, x, top, fillW, h, 3f);

            return x + w;
        }

        private static void FillRoundRect(Graphics g, Brush brush, float x, float y, float w, float h, float r)
        {
            if (w <= 0f || h <= 0f) return;
            r = Math.Min(r, Math.Min(w, h) / 2f);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            if (r <= 0.5f)
            {
                path.AddRectangle(new RectangleF(x, y, w, h));
            }
            else
            {
                float d = r * 2f;
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + w - d, y, d, d, 270, 90);
                path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                path.AddArc(x, y + h - d, d, d, 90, 90);
                path.CloseFigure();
            }
            g.FillPath(brush, path);
        }

        private int DrawSection(Graphics g, string label, string value, float loadPercent, int x, int y, int lineHeight)
        {
            Brush loadBrush = GetLoadBrush(loadPercent);

            g.DrawString(label, _theme.SmallFont, _theme.Dim, x, y);
            SizeF ls = g.MeasureString(label, _theme.SmallFont);
            x += (int)ls.Width + 2;

            // The slot is sized by the widest value: the right edge and the "%" never move.
            return DrawValueSlot(g, value, "100%", loadBrush, x, y, lineHeight);
        }

        private int DrawTemp(Graphics g, float celsius, int x, int y, int lineHeight)
        {
            // The colour is always computed from Celsius: the LoadLevels thresholds are
            // defined in it, and the display unit must not affect the colour coding.
            bool fahrenheit = _settings.UseFahrenheit;
            string text = Formatting.FormatTemperature(celsius, fahrenheit);
            string template = Formatting.TemperatureSlotTemplate(fahrenheit);
            return DrawValueSlot(g, text, template, GetTempBrush(celsius), x, y, lineHeight);
        }

        // Draws a value right-aligned in a fixed-width slot (sized by a template).
        // The slot width is measured on the current Graphics: under PerMonitorV2 it follows
        // the DPI, so the pixel width must not be cached between frames.
        private int DrawValueSlot(Graphics g, string value, string template, Brush brush, int x, int y, int lineHeight)
        {
            int slotW = (int)Math.Ceiling(g.MeasureString(template, _theme.MainFont).Width);
            var rect = new RectangleF(x, y, slotW, lineHeight);
            g.DrawString(value, _theme.MainFont, brush, rect, ValueRightAligned);
            return x + slotW;
        }

        private static Brush BrushFor(LoadLevel level) => level switch
        {
            LoadLevel.Critical => BarTheme.Red,
            LoadLevel.High => BarTheme.Orange,
            LoadLevel.Elevated => BarTheme.Yellow,
            _ => BarTheme.Green
        };

        private static Brush GetLoadBrush(float fraction) => BrushFor(LoadLevels.FromFraction(fraction));

        private static Brush GetTempBrush(float celsius) => BrushFor(LoadLevels.FromTemperature(celsius));

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND)
            {
                switch ((long)m.WParam & 0xFFF0)
                {
                    case SC_MOVE:
                    {
                        // The system turns a press on the bar into SC_MOVE. The default handler only
                        // returns once the button is released, so this is where the gesture resolves:
                        // if the bar did not move, it was a click (open the graphs).
                        var before = this.Location;
                        base.WndProc(ref m);
                        if (this.Location == before)
                            OpenGraphOverlay();
                        return;
                    }

                    case SC_MAXIMIZE:
                        // The bar is sized to fit its content; full screen is never wanted.
                        // Form.MaximizeBox cannot express that: WinForms fills in the title-bar
                        // button styles only when FormBorderStyle != None, so the flag never reaches
                        // the window and the command has to be dropped here.
                        return;
                }
            }

            if (m.Msg == WM_NCLBUTTONDBLCLK)
            {
                // A double-click on the "title bar" would maximize the window. There is nothing to
                // bind to the gesture either: the first click has already toggled the graphs, so
                // the second one is simply dropped.
                return;
            }

            if (m.Msg == WM_NCRBUTTONUP)
            {
                // The system menu of a title bar is useless for the bar (and offers "Maximize" as
                // one more route to the same trap) — show the application menu instead.
                ShowBarMenu();
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTCAPTION;
                return;
            }

            if (m.Msg == WM_DISPLAYCHANGE)
            {
                base.WndProc(ref m);
                // Deferred: by the time it runs, the system has refreshed the monitor list.
                if (this.IsHandleCreated && !this.IsDisposed)
                    BeginInvoke(new Action(ReconcilePositionWithScreens));
                return;
            }

            if (m.Msg == WM_WINDOWPOSCHANGING)
            {
                // Keep the window above the rest by modifying the z-order in the message
                // itself. This avoids recursion (calling SetWindowPos from the handler).
                var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                wp.hwndInsertAfter = HWND_TOPMOST;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }

            base.WndProc(ref m);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            ForceTopMost();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            _topmostTimer.Stop();
            _saveTimer.Stop();
            _updateCheckTimer.Stop();
            FlushSettings();

            if (_settingsForm != null && !_settingsForm.IsDisposed)
                _settingsForm.Close();

            if (_graphOverlay != null && !_graphOverlay.IsDisposed)
                _graphOverlay.Close();

            _cpuSensor?.Dispose();
            _diskSensor?.Dispose();
            _hardwareSensor?.Dispose();

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _appIcon.Dispose();

            _timer.Dispose();
            _topmostTimer.Dispose();
            _saveTimer.Dispose();
            _updateCheckTimer.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Fonts and brushes are released only once the window is destroyed: a WM_PAINT
            // can still arrive between FormClosing and FormClosed.
            _theme.Dispose();
            base.OnFormClosed(e);
        }
    }
}
