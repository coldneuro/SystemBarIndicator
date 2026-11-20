using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SystemBarIndicator.Configuration;
using SystemBarIndicator.Models;
using SystemBarIndicator.Util;

namespace SystemBarIndicator
{
    internal sealed class GraphOverlay : Form
    {
        private const int ChartHeight = 72;
        private const int OuterPadding = 8;
        private const int Gap = 4;
        // The overlay takes its width from the bar, which can become very narrow when most
        // sections are turned off — charts in such a sliver would be unreadable.
        private const int MinWidth = 320;

        private readonly DataHistory _history;
        private readonly Func<SensorData> _getCurrentData;
        private readonly MainForm _parentForm;

        private BarTheme _theme;
        private AppSettings _settings;

        private class ChartConfig
        {
            public required string Title;
            public Color LineColor;
            public required Func<DataPoint, float> GetValue;
            public required Func<SensorData, string> FormatValue;
            public float MaxDefault;
            public bool ShowOnlyIfPositive;
            /// <summary>Whether the section is enabled in settings — charts follow the bar.</summary>
            public required Func<AppSettings, bool> IsEnabled;

            // Per-chart coloured resources — created once, released in OnFormClosed.
            public SolidBrush LineBrush = null!;
            public Pen LinePen = null!;
            public Pen GlowPen = null!;
        }

        private readonly ChartConfig[] _charts;

        public GraphOverlay(DataHistory history, Func<SensorData> getCurrentData, MainForm parentForm,
            BarTheme theme, AppSettings settings)
        {
            _history = history;
            _getCurrentData = getCurrentData;
            _parentForm = parentForm;
            _theme = theme;
            _settings = settings;

            _charts = new ChartConfig[]
            {
                new ChartConfig { Title = "CPU", LineColor = Color.FromArgb(99, 179, 237), GetValue = p => p.CpuUsage, FormatValue = d => string.Format("{0:F0}%", d.CpuUsage), MaxDefault = 100, ShowOnlyIfPositive = false, IsEnabled = s => s.ShowCpu },
                new ChartConfig { Title = "CPU Temp", LineColor = Color.FromArgb(244, 143, 177), GetValue = p => p.CpuTemperature, FormatValue = d => FormatTemp(d.CpuTemperature), MaxDefault = 100, ShowOnlyIfPositive = true, IsEnabled = s => s.ShowCpuTemp },
                new ChartConfig { Title = "RAM", LineColor = Color.FromArgb(129, 140, 248), GetValue = p => p.RamUsage, FormatValue = d => string.Format("{0:F0}%", d.RamUsage), MaxDefault = 100, ShowOnlyIfPositive = false, IsEnabled = s => s.ShowRam },
                new ChartConfig { Title = "GPU", LineColor = Color.FromArgb(72, 187, 120), GetValue = p => p.GpuUsage, FormatValue = d => string.Format("{0:F0}%", d.GpuUsage), MaxDefault = 100, ShowOnlyIfPositive = true, IsEnabled = s => s.ShowGpu },
                new ChartConfig { Title = "GPU Temp", LineColor = Color.FromArgb(255, 167, 38), GetValue = p => p.GpuTemperature, FormatValue = d => FormatTemp(d.GpuTemperature), MaxDefault = 100, ShowOnlyIfPositive = true, IsEnabled = s => s.ShowGpuTemp },
                new ChartConfig { Title = "VRAM", LineColor = Color.FromArgb(77, 182, 172), GetValue = p => p.GpuVramUsage, FormatValue = d => string.Format("{0:F0}%", d.GpuVramUsage), MaxDefault = 100, ShowOnlyIfPositive = true, IsEnabled = s => s.ShowGpuVramUsage },
                new ChartConfig { Title = "VRAM Temp", LineColor = Color.FromArgb(255, 213, 79), GetValue = p => p.GpuVramTemperature, FormatValue = d => FormatTemp(d.GpuVramTemperature), MaxDefault = 100, ShowOnlyIfPositive = true, IsEnabled = s => s.ShowGpuVramTemp },
                new ChartConfig { Title = "Disk R/W", LineColor = Color.FromArgb(171, 71, 188), GetValue = p => p.DiskRead + p.DiskWrite, FormatValue = d => string.Format("{0:F0} / {1:F0} MB", d.DiskRead, d.DiskWrite), MaxDefault = 500, ShowOnlyIfPositive = false, IsEnabled = s => s.ShowDisk },
                new ChartConfig { Title = "Net DL", LineColor = Color.FromArgb(237, 137, 54), GetValue = p => p.NetDown, FormatValue = d => FormatSpeed(d.NetDown), MaxDefault = 10000, ShowOnlyIfPositive = false, IsEnabled = s => s.ShowNetwork },
                new ChartConfig { Title = "Net UL", LineColor = Color.FromArgb(38, 198, 218), GetValue = p => p.NetUp, FormatValue = d => FormatSpeed(d.NetUp), MaxDefault = 10000, ShowOnlyIfPositive = false, IsEnabled = s => s.ShowNetwork }
            };

            foreach (var c in _charts)
            {
                c.LineBrush = new SolidBrush(c.LineColor);
                c.LinePen = new Pen(c.LineColor, 1.5f) { LineJoin = LineJoin.Round };
                c.GlowPen = new Pen(Color.FromArgb(25, c.LineColor.R, c.LineColor.G, c.LineColor.B), 3.5f) { LineJoin = LineJoin.Round };
            }

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = _theme.OverlayBackground;
            this.Opacity = OverlayOpacity(_settings);
            this.StartPosition = FormStartPosition.Manual;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

            UpdateSize();

            // No timer of its own: the window follows the bar through the parent's
            // LocationChanged event, and the bar calls RefreshData with every new snapshot.
            // A 500 ms timer used to repaint all the charts twice per sample at the default
            // 1 s poll, and ten times per sample at 5 s, with nothing new to show.
        }

        /// <summary>
        /// A new snapshot has arrived: re-fit the window (charts that only appear once their
        /// sensor reports a value can change its height) and repaint.
        /// </summary>
        public void RefreshData()
        {
            PositionAboveParent();
            this.Invalidate();
        }

        /// <summary>The overlay is slightly more solid than the bar, but tracks its opacity.</summary>
        private static double OverlayOpacity(AppSettings settings) =>
            Math.Min(1.0, settings.Opacity + 0.03);

        /// <summary>Picks up theme and settings changes without recreating the window.</summary>
        public void ApplyTheme(BarTheme theme, AppSettings settings)
        {
            _theme = theme;
            _settings = settings;
            this.BackColor = _theme.OverlayBackground;
            double opacity = OverlayOpacity(settings);
            if (this.Opacity != opacity)
                this.Opacity = opacity;
            PositionAboveParent();
            this.Invalidate();
        }

        private void UpdateSize()
        {
            int visibleCharts = GetVisibleChartCount(_history.GetPoints());
            int totalHeight = OuterPadding * 2 + visibleCharts * ChartHeight + Math.Max(visibleCharts - 1, 0) * Gap;
            // Capped by the screen, not a constant: all nine charts already exceed the old
            // fixed 650 px, and the constant would silently clip the last one.
            int maxHeight = Screen.FromRectangle(_parentForm.Bounds).WorkingArea.Height;
            this.Size = new Size(Math.Max(_parentForm.Width, MinWidth), Math.Min(totalHeight, maxHeight));
        }

        private bool IsChartVisible(ChartConfig chart, List<DataPoint> data) =>
            chart.IsEnabled(_settings) && (!chart.ShowOnlyIfPositive || HasRecentPositive(chart, data));

        private int GetVisibleChartCount(List<DataPoint> data)
        {
            int count = 0;
            foreach (var c in _charts)
            {
                if (IsChartVisible(c, data))
                    count++;
            }
            return count;
        }

        private static bool HasRecentPositive(ChartConfig chart, List<DataPoint> data)
        {
            int checkCount = Math.Min(10, data.Count);
            for (int i = data.Count - 1; i >= data.Count - checkCount; i--)
            {
                if (i >= 0 && chart.GetValue(data[i]) > 0)
                    return true;
            }
            return false;
        }

        public void PositionAboveParent()
        {
            UpdateSize();

            // The screen is chosen from the bar's actual position: on a monitor above or to
            // the left of the primary one coordinates are negative, so clamping at zero
            // would be wrong.
            var screen = Screen.FromRectangle(_parentForm.Bounds).WorkingArea;

            int x = _parentForm.Left;
            if (x + this.Width > screen.Right) x = screen.Right - this.Width;
            if (x < screen.Left) x = screen.Left;

            int y;
            if (_parentForm.BarEdge == MainForm.TaskbarEdge.Top)
            {
                y = _parentForm.Bottom + Gap;
                if (y + this.Height > screen.Bottom) y = screen.Bottom - this.Height;
            }
            else
            {
                y = _parentForm.Top - this.Height - Gap;
                if (y < screen.Top) y = screen.Top;
            }

            var loc = new Point(x, y);
            if (this.Location != loc)
                this.Location = loc;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var data = _history.GetPoints(); // a single history snapshot per frame
            int y = OuterPadding;

            foreach (var chart in _charts)
            {
                if (!IsChartVisible(chart, data))
                    continue;

                DrawChart(e.Graphics, chart, data, OuterPadding, y, this.Width - OuterPadding * 2, ChartHeight);
                y += ChartHeight + Gap;
            }
        }

        private void DrawChart(Graphics g, ChartConfig chart, List<DataPoint> data, int x, int y, int w, int h)
        {
            using (var path = CreateRoundRect(x, y, w, h, 8))
            {
                g.FillPath(_theme.CardBrush, path);
            }

            string displayVal = chart.FormatValue(_getCurrentData());
            g.DrawString(chart.Title, _theme.TitleFont, _theme.TitleBrush, x + 10, y + 6);

            var size = g.MeasureString(displayVal, _theme.ValueFont);
            g.DrawString(displayVal, _theme.ValueFont, chart.LineBrush, x + w - size.Width - 10, y + 4);

            int chartX = x + 10;
            int chartY = y + 24;
            int chartW = w - 20;
            int chartH = h - 32;

            if (data.Count < 2) return;

            float maxVal = chart.MaxDefault;
            foreach (var p in data)
            {
                float v = chart.GetValue(p);
                if (v > maxVal) maxVal = v;
            }
            maxVal = Math.Max(maxVal, 1);

            for (int i = 1; i <= 3; i++)
            {
                int gy = chartY + (int)(chartH * i / 4f);
                g.DrawLine(_theme.GridPen, chartX, gy, chartX + chartW, gy);
            }

            var pts = new List<PointF>(data.Count);
            float stepX = (float)chartW / Math.Max(data.Count - 1, 1);
            for (int i = 0; i < data.Count; i++)
            {
                float val = chart.GetValue(data[i]);
                float normalized = Math.Max(0, Math.Min(val / maxVal, 1f));
                pts.Add(new PointF(chartX + i * stepX, chartY + chartH - (normalized * chartH)));
            }

            if (pts.Count >= 2)
            {
                using (var fillPath = CreateSmoothFillPath(pts, chartY + chartH))
                using (var gradientBrush = new LinearGradientBrush(
                    new PointF(0, chartY), new PointF(0, chartY + chartH),
                    Color.FromArgb(45, chart.LineColor.R, chart.LineColor.G, chart.LineColor.B),
                    Color.FromArgb(3, chart.LineColor.R, chart.LineColor.G, chart.LineColor.B)))
                {
                    g.FillPath(gradientBrush, fillPath);
                }

                using (var linePath = CreateSmoothLinePath(pts))
                {
                    g.DrawPath(chart.LinePen, linePath);
                    g.DrawPath(chart.GlowPen, linePath);
                }

                var lastPt = pts[pts.Count - 1];
                g.FillEllipse(chart.LineBrush, lastPt.X - 2.5f, lastPt.Y - 2.5f, 5, 5);
            }
        }

        private GraphicsPath CreateSmoothFillPath(List<PointF> points, float baseY)
        {
            var path = new GraphicsPath();
            if (points.Count < 2) return path;

            path.AddLine(points[0].X, baseY, points[0].X, points[0].Y);

            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                float cpx = (p1.X + p2.X) / 2;
                path.AddBezier(p1.X, p1.Y, cpx, p1.Y, cpx, p2.Y, p2.X, p2.Y);
            }

            path.AddLine(points[points.Count - 1].X, points[points.Count - 1].Y, points[points.Count - 1].X, baseY);
            path.CloseFigure();
            return path;
        }

        private GraphicsPath CreateSmoothLinePath(List<PointF> points)
        {
            var path = new GraphicsPath();
            if (points.Count < 2) return path;

            path.StartFigure();
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                float cpx = (p1.X + p2.X) / 2;
                path.AddBezier(p1.X, p1.Y, cpx, p1.Y, cpx, p2.Y, p2.X, p2.Y);
            }
            return path;
        }

        private GraphicsPath CreateRoundRect(int x, int y, int w, int h, int r)
        {
            var path = new GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static string FormatSpeed(float kbps) => Formatting.FormatSpeed(kbps);

        private string FormatTemp(float celsius) => Formatting.FormatTemperature(celsius, _settings.UseFahrenheit);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Drawing resources are released only after the window is destroyed: a WM_PAINT
            // can still arrive between FormClosing and FormClosed.
            foreach (var c in _charts)
            {
                c.LineBrush.Dispose();
                c.LinePen.Dispose();
                c.GlowPen.Dispose();
            }

            base.OnFormClosed(e);
        }
    }
}
