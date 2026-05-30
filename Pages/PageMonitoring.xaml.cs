using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageMonitoring : UserControl
    {
        private readonly MainWindow _main;
        private readonly DispatcherTimer _timer;
        private bool _busy;
        private bool _ramNameSet;

        private const int MaxPoints = 60;
        private readonly List<double> _cpuHist = new();
        private readonly List<double> _gpuHist = new();
        private readonly List<double> _ramHist = new();
        private bool _cpuNameSet;

        public PageMonitoring(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => await TickAsync();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            DrawGrid();
            _timer.Start();
            await TickAsync();   // premier rafraîchissement immédiat
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

        private async Task TickAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var s = await Task.Run(SystemMonitor.Collect);
                UpdateUI(s);
            }
            catch { }
            finally { _busy = false; }
        }

        // ── Mise à jour des tuiles + graphe ────────────────────────────────────

        private void UpdateUI(MonSnapshot s)
        {
            // CPU
            if (!_cpuNameSet && s.CpuName.Length > 0)
            {
                var cores = s.CpuCores > 0 ? $"  ·  {s.CpuCores} cœur{(s.CpuCores > 1 ? "s" : "")}" : "";
                TxtCpuName.Text = s.CpuName + cores;
                TxtCpuBase.Text = s.CpuBaseMHz > 0 ? $"{s.CpuBaseMHz / 1000.0:F2} GHz" : "—";
                _cpuNameSet = true;
            }
            TxtCpuUsage.Text = $"{s.CpuUsage:F0}";
            TxtCpuFreq.Text  = s.CpuMHz > 0 ? $"{s.CpuMHz / 1000.0:F2} GHz" : "—";
            TxtCpuProc.Text  = s.Processes > 0 ? s.Processes.ToString() : "—";
            TxtCpuTop.Text   = s.TopCpuName.Length > 0 ? $"{s.TopCpuName} · {s.TopCpuPct:F0} %" : "—";
            SetBar(BarCpu, s.CpuUsage);

            // RAM
            TxtRamUsage.Text  = $"{s.RamPct:F0}";
            TxtRamFree.Text   = s.RamTotalGB > 0 ? $"{s.RamFreeGB:F1} Go" : "—";
            var ramTopPct = s.RamTotalGB > 0 ? s.TopRamMB / (s.RamTotalGB * 1024.0) * 100.0 : 0;
            TxtRamTop.Text    = s.TopRamName.Length > 0 ? $"{s.TopRamName} · {ramTopPct:F0} %" : "—";
            if (!_ramNameSet && s.RamInstalledGB > 0)
            {
                TxtRamName.Text   = $"{Math.Round(s.RamInstalledGB)} Go installés";
                TxtRamType.Text   = s.RamSpeed > 0 ? $"{s.RamType} · {s.RamSpeed} MHz" : s.RamType;
                TxtRamSticks.Text = s.RamSticks > 0 ? s.RamSticks.ToString() : "—";
                _ramNameSet = true;
            }
            SetBar(BarRam, s.RamPct);

            // GPU
            if (s.GpuOk)
            {
                TxtGpuName.Text  = s.GpuName;
                TxtGpuUsage.Text = $"{s.GpuUsage:F0}";
                TxtGpuVram.Text  = $"{s.GpuVramUsedMB / 1024.0:F1} / {s.GpuVramTotalMB / 1024.0:F1} Go";
                TxtGpuTemp.Text  = $"{s.GpuTemp:F0} °C";
                TxtGpuWatts.Text = s.GpuWatts > 0 ? $"{s.GpuWatts:F0} W" : "—";
                TxtGpuFreq.Text  = s.GpuMHz > 0 ? $"{s.GpuMHz:F0} MHz" : "—";
                SetBar(BarGpu, s.GpuUsage);
            }
            else
            {
                TxtGpuName.Text  = "GPU NVIDIA non détecté";
                TxtGpuUsage.Text = "—";
                TxtGpuVram.Text  = TxtGpuTemp.Text = TxtGpuWatts.Text = TxtGpuFreq.Text = "—";
            }

            // Historique + tracé
            Push(_cpuHist, s.CpuUsage);
            Push(_gpuHist, s.GpuOk ? s.GpuUsage : 0);
            Push(_ramHist, s.RamPct);
            DrawLine(CpuLine, _cpuHist);
            DrawLine(GpuLine, _gpuHist);
            DrawLine(RamLine, _ramHist);
        }

        private static void Push(List<double> list, double v)
        {
            list.Add(v);
            if (list.Count > MaxPoints) list.RemoveAt(0);
        }

        private static void SetBar(Grid bar, double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            bar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
        }

        // ── Graphique ──────────────────────────────────────────────────────────

        private void GraphArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawGrid();
            DrawLine(CpuLine, _cpuHist);
            DrawLine(GpuLine, _gpuHist);
            DrawLine(RamLine, _ramHist);
        }

        private void DrawGrid()
        {
            GridCanvas.Children.Clear();
            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var brush = (Brush)(FindResource("ThBorder"));
            foreach (var p in new[] { 25, 50, 75 })
            {
                double y = h - (p / 100.0) * h;
                GridCanvas.Children.Add(new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = brush, StrokeThickness = 1, Opacity = 0.5
                });
            }
        }

        private void DrawLine(Polyline line, List<double> data)
        {
            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0 || data.Count < 2) { line.Points = new PointCollection(); return; }

            double step = w / (MaxPoints - 1);
            int count = data.Count;
            var pts = new PointCollection(count);
            for (int i = 0; i < count; i++)
            {
                // newest ancré à droite, défile vers la gauche
                double x = w - (count - 1 - i) * step;
                double y = h - (Math.Max(0, Math.Min(100, data[i])) / 100.0) * h;
                pts.Add(new Point(x, y));
            }
            line.Points = pts;
        }
    }
}
