using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using SystemBarIndicator.Configuration;

namespace SystemBarIndicator
{
    /// <summary>
    /// The appearance settings window. Changes apply immediately (live preview) and reach
    /// disk on a delay — the write is managed by <see cref="MainForm"/>.
    /// The bar's position is not configured here: it comes from dragging and "Reset Position".
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly MainForm _owner;
        private AppSettings _working;
        // While values are being loaded into the controls, their events must not push
        // half-assembled settings back to the bar.
        private bool _loading;

        private readonly TrackBar _opacity = new();
        private readonly Label _opacityValue = new();
        private readonly ComboBox _theme = new();
        private readonly Button _color = new();
        private readonly TrackBar _fontScale = new();
        private readonly Label _fontScaleValue = new();

        private readonly CheckBox _showCpu = new();
        private readonly CheckBox _showCpuTemp = new();
        private readonly CheckBox _showRam = new();
        private readonly CheckBox _showGpu = new();
        private readonly CheckBox _showGpuTemp = new();
        private readonly CheckBox _showGpuVramUsage = new();
        private readonly CheckBox _showGpuVramTemp = new();
        private readonly CheckBox _showDisk = new();
        private readonly CheckBox _showNetwork = new();

        private readonly ComboBox _interval = new();
        private readonly CheckBox _fahrenheit = new();
        private readonly CheckBox _autoUpdate = new();
        // Says why a section is "(n/a)" where the app can tell (the CPU temperature).
        private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };

        private static readonly string[] ThemeItems = { ThemeNames.Dark, ThemeNames.Light, ThemeNames.Custom };

        public SettingsForm(MainForm owner)
        {
            _owner = owner;
            _working = owner.CurrentSettings;

            Text = "SystemBar Indicator — Settings";
            Icon = owner.Icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 514);

            BuildAppearanceGroup();
            BuildSectionsGroup();
            BuildOtherGroup();
            BuildButtons();

            LoadFromSettings(_working);
        }

        private void BuildAppearanceGroup()
        {
            var box = new GroupBox { Text = "Appearance", Bounds = new Rectangle(12, 10, 356, 156) };

            box.Controls.Add(new Label { Text = "Opacity:", Bounds = new Rectangle(12, 28, 90, 20) });
            _opacity.Bounds = new Rectangle(102, 22, 190, 45);
            _opacity.Minimum = (int)Math.Round(AppSettings.MinOpacity * 100);
            _opacity.Maximum = (int)Math.Round(AppSettings.MaxOpacity * 100);
            _opacity.TickFrequency = 10;
            _opacity.SmallChange = 1;
            _opacity.LargeChange = 10;
            _opacity.ValueChanged += (s, e) => { _opacityValue.Text = _opacity.Value + "%"; Apply(); };
            box.Controls.Add(_opacity);
            _opacityValue.Bounds = new Rectangle(298, 28, 50, 20);
            box.Controls.Add(_opacityValue);

            box.Controls.Add(new Label { Text = "Theme:", Bounds = new Rectangle(12, 74, 90, 20) });
            _theme.Bounds = new Rectangle(102, 70, 120, 24);
            _theme.DropDownStyle = ComboBoxStyle.DropDownList;
            _theme.Items.AddRange(ThemeItems);
            _theme.SelectedIndexChanged += (s, e) => { UpdateColorButtonState(); Apply(); };
            box.Controls.Add(_theme);

            _color.Bounds = new Rectangle(232, 69, 110, 26);
            _color.Text = "Background...";
            _color.Click += PickBackgroundColor;
            box.Controls.Add(_color);

            box.Controls.Add(new Label { Text = "Font size:", Bounds = new Rectangle(12, 118, 90, 20) });
            _fontScale.Bounds = new Rectangle(102, 112, 190, 45);
            _fontScale.Minimum = (int)Math.Round(AppSettings.MinFontScale * 100);
            _fontScale.Maximum = (int)Math.Round(AppSettings.MaxFontScale * 100);
            _fontScale.TickFrequency = 25;
            _fontScale.SmallChange = 5;
            _fontScale.LargeChange = 25;
            _fontScale.ValueChanged += (s, e) => { _fontScaleValue.Text = _fontScale.Value + "%"; Apply(); };
            box.Controls.Add(_fontScale);
            _fontScaleValue.Bounds = new Rectangle(298, 118, 50, 20);
            box.Controls.Add(_fontScaleValue);

            Controls.Add(box);
        }

        private void BuildSectionsGroup()
        {
            var box = new GroupBox { Text = "Sections", Bounds = new Rectangle(12, 174, 356, 156) };

            // Listed in the order the sections appear in the bar.
            SetUpCheck(_showCpu, "CPU", new Rectangle(14, 24, 160, 22), available: true, box);
            SetUpCheck(_showCpuTemp, "CPU temperature", new Rectangle(180, 24, 165, 22), _owner.HasCpuTempSensor, box,
                _owner.CpuTempUnavailableReason is { } why
                    ? $"CPU temperature is unavailable: {why}.{Environment.NewLine}See Troubleshooting in the README."
                    : null);
            SetUpCheck(_showRam, "RAM", new Rectangle(14, 50, 160, 22), available: true, box);
            SetUpCheck(_showGpu, "GPU", new Rectangle(180, 50, 165, 22), _owner.HasGpuSensor, box);
            SetUpCheck(_showGpuTemp, "GPU temperature", new Rectangle(14, 76, 160, 22), _owner.HasGpuTempSensor, box);
            SetUpCheck(_showGpuVramUsage, "VRAM usage", new Rectangle(180, 76, 165, 22), _owner.HasGpuVramUsageSensor, box);
            SetUpCheck(_showGpuVramTemp, "VRAM temperature", new Rectangle(14, 102, 160, 22), _owner.HasGpuVramTempSensor, box);
            SetUpCheck(_showDisk, "Disk", new Rectangle(180, 102, 165, 22), _owner.HasDiskSensor, box);
            SetUpCheck(_showNetwork, "Network", new Rectangle(14, 128, 160, 22), available: true, box);

            Controls.Add(box);
        }

        // An unavailable sensor is marked but its checkbox is not disabled: it may show up
        // later (after installing PawnIO, say), and the setting must survive that.
        // AutoSize rather than a fixed width: a label longer than the column would otherwise
        // be clipped, and DPI scaling changes the text width on top of that.
        private void SetUpCheck(CheckBox check, string text, Rectangle bounds, bool available, GroupBox box,
            string? whyUnavailable = null)
        {
            check.Text = available ? text : text + " (n/a)";
            if (!available && whyUnavailable != null)
                _tips.SetToolTip(check, whyUnavailable);
            check.AutoSize = true;
            check.Location = bounds.Location;
            check.CheckedChanged += (s, e) => Apply();
            box.Controls.Add(check);
        }

        private void BuildOtherGroup()
        {
            var box = new GroupBox { Text = "Behaviour", Bounds = new Rectangle(12, 338, 356, 118) };

            box.Controls.Add(new Label { Text = "Update every:", Bounds = new Rectangle(12, 28, 100, 20) });
            _interval.Bounds = new Rectangle(118, 24, 100, 24);
            _interval.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int ms in AppSettings.AllowedIntervalsMs)
                _interval.Items.Add(FormatInterval(ms));
            _interval.SelectedIndexChanged += (s, e) => Apply();
            box.Controls.Add(_interval);

            _fahrenheit.Text = "Show temperature in °F";
            _fahrenheit.AutoSize = true;
            _fahrenheit.Location = new Point(14, 58);
            _fahrenheit.CheckedChanged += (s, e) => Apply();
            box.Controls.Add(_fahrenheit);

            _autoUpdate.Text = "Install updates automatically";
            _autoUpdate.AutoSize = true;
            _autoUpdate.Location = new Point(14, 84);
            _autoUpdate.CheckedChanged += (s, e) => Apply();
            box.Controls.Add(_autoUpdate);

            Controls.Add(box);
        }

        private void BuildButtons()
        {
            var defaults = new Button { Text = "Defaults", Bounds = new Rectangle(12, 466, 100, 30) };
            defaults.Click += (s, e) => LoadFromSettings(new AppSettings(), apply: true);
            Controls.Add(defaults);

            var close = new Button { Text = "Close", Bounds = new Rectangle(268, 466, 100, 30) };
            close.Click += (s, e) => Close();
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
        }

        private static string FormatInterval(int ms) => ms % 1000 == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} s", ms / 1000)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} s", ms / 1000.0);

        private void LoadFromSettings(AppSettings settings, bool apply = false)
        {
            _loading = true;
            try
            {
                var s = settings.Normalized();
                _working = s;

                _opacity.Value = Math.Clamp((int)Math.Round(s.Opacity * 100), _opacity.Minimum, _opacity.Maximum);
                _opacityValue.Text = _opacity.Value + "%";

                _theme.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeItems, s.Theme));

                _fontScale.Value = Math.Clamp((int)Math.Round(s.FontScale * 100), _fontScale.Minimum, _fontScale.Maximum);
                _fontScaleValue.Text = _fontScale.Value + "%";

                _showCpu.Checked = s.ShowCpu;
                _showCpuTemp.Checked = s.ShowCpuTemp;
                _showRam.Checked = s.ShowRam;
                _showGpu.Checked = s.ShowGpu;
                _showGpuTemp.Checked = s.ShowGpuTemp;
                _showGpuVramUsage.Checked = s.ShowGpuVramUsage;
                _showGpuVramTemp.Checked = s.ShowGpuVramTemp;
                _showDisk.Checked = s.ShowDisk;
                _showNetwork.Checked = s.ShowNetwork;

                _interval.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.AllowedIntervalsMs, s.UpdateIntervalMs));
                _fahrenheit.Checked = s.UseFahrenheit;
                _autoUpdate.Checked = s.AutoUpdate;

                UpdateColorButtonState();
            }
            finally
            {
                _loading = false;
            }

            if (apply)
                Apply();
        }

        private void UpdateColorButtonState()
        {
            _color.Enabled = _theme.SelectedIndex >= 0 &&
                string.Equals(ThemeItems[_theme.SelectedIndex], ThemeNames.Custom, StringComparison.Ordinal);
        }

        private void PickBackgroundColor(object? sender, EventArgs e)
        {
            using var dialog = new ColorDialog
            {
                FullOpen = true,
                Color = Color.FromArgb(ColorCodec.ParseOrDefault(_working.BackgroundColor, unchecked((int)0xFF191919)))
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            // The bar background is opaque — transparency is controlled by its own slider.
            _working.BackgroundColor = ColorCodec.ToHex(Color.FromArgb(255, dialog.Color).ToArgb());
            Apply();
        }

        /// <summary>Collects settings from the controls and hands them to the bar to apply at once.</summary>
        private void Apply()
        {
            if (_loading)
                return;

            var s = _working.Clone();
            s.Opacity = _opacity.Value / 100.0;
            s.Theme = _theme.SelectedIndex >= 0 ? ThemeItems[_theme.SelectedIndex] : ThemeNames.Dark;
            s.FontScale = _fontScale.Value / 100.0;
            s.ShowCpu = _showCpu.Checked;
            s.ShowCpuTemp = _showCpuTemp.Checked;
            s.ShowRam = _showRam.Checked;
            s.ShowGpu = _showGpu.Checked;
            s.ShowGpuTemp = _showGpuTemp.Checked;
            s.ShowGpuVramUsage = _showGpuVramUsage.Checked;
            s.ShowGpuVramTemp = _showGpuVramTemp.Checked;
            s.ShowDisk = _showDisk.Checked;
            s.ShowNetwork = _showNetwork.Checked;
            s.UpdateIntervalMs = _interval.SelectedIndex >= 0
                ? AppSettings.AllowedIntervalsMs[_interval.SelectedIndex]
                : AppSettings.DefaultIntervalMs;
            s.UseFahrenheit = _fahrenheit.Checked;
            s.AutoUpdate = _autoUpdate.Checked;

            _working = s;
            _owner.ApplySettings(s);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
