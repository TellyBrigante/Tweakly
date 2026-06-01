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

        // Visuels NVMe générés dynamiquement (un par disque : bloc + courbe + couleur signature)
        private sealed class NvmeVisual
        {
            public Brush        Color    = Brushes.Gray;
            public TextBlock    UsageBig = null!;   // grosse valeur % d'utilisation
            public Grid         UsageBar = null!;   // barre d'utilisation
            public TextBlock    TempVal  = null!;   // ligne "Température"
            public List<double> Hist     = new();
            public Polyline     Line     = null!;
        }
        private readonly Dictionary<string, NvmeVisual> _nvme = new();

        // Couleurs NVMe — distinctes du CPU (#3B82E0), GPU (#2EC46A) et RAM (#C08CF0)
        private static readonly string[] NvmePalette =
        {
            "#F5A623",  // orange ambre
            "#29C7D6",  // cyan
            "#FF6B9D",  // rose
            "#E0C84A",  // jaune
        };

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

            // NVMe
            UpdateNvme(s.Nvmes);

            // Historique + tracé
            Push(_cpuHist, s.CpuUsage);
            Push(_gpuHist, s.GpuOk ? s.GpuUsage : 0);
            Push(_ramHist, s.RamPct);
            DrawLine(CpuLine, _cpuHist);
            DrawLine(GpuLine, _gpuHist);
            DrawLine(RamLine, _ramHist);
        }

        // ── Tuiles NVMe ─────────────────────────────────────────────────────────

        private void UpdateNvme(List<NvmeInfo> nvmes)
        {
            if (nvmes == null || nvmes.Count == 0)
            {
                // Cache éventuellement vide au tout premier tick : ne pas effacer si on a déjà des tuiles
                if (_nvme.Count == 0)
                {
                    NvmeEmptyTile.Visibility = Visibility.Visible;
                    NvmeGrid.Visibility      = Visibility.Collapsed;
                }
                return;
            }

            NvmeEmptyTile.Visibility = Visibility.Collapsed;
            NvmeGrid.Visibility      = Visibility.Visible;

            // Reconstruire seulement si la composition (les disques) a changé
            bool sameSet = nvmes.Count == _nvme.Count && nvmes.All(n => _nvme.ContainsKey(n.Name));
            if (!sameSet) RebuildNvme(nvmes);

            // Maj usage (gros chiffre + barre) + température (ligne) + courbe du graphe
            foreach (var n in nvmes)
            {
                if (!_nvme.TryGetValue(n.Name, out var v)) continue;
                v.UsageBig.Text      = $"{n.UsagePct:F0}";
                SetBar(v.UsageBar, n.UsagePct);
                v.TempVal.Text       = $"{n.TempC} °C";
                v.TempVal.Foreground = TempColor(n.TempC);

                Push(v.Hist, n.UsagePct);
                DrawLine(v.Line, v.Hist);
            }
        }

        private void RebuildNvme(List<NvmeInfo> nvmes)
        {
            NvmeGrid.Children.Clear();
            NvmeGrid.ColumnDefinitions.Clear();
            NvmeLegend.Children.Clear();
            foreach (var v in _nvme.Values) GraphArea.Children.Remove(v.Line);
            _nvme.Clear();

            // Une colonne égale par disque → les tuiles remplissent la largeur, comme la rangée du haut
            for (int c = 0; c < nvmes.Count; c++)
                NvmeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int i = 0;
            foreach (var n in nvmes)
            {
                var color = (Brush)new BrushConverter().ConvertFromString(NvmePalette[i % NvmePalette.Length])!;
                var v = new NvmeVisual { Color = color };

                // Courbe d'utilisation dans le graphe (couleur signature)
                v.Line = new Polyline { Stroke = color, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
                GraphArea.Children.Add(v.Line);

                var card = BuildNvmeTile(n, v);
                card.Margin = new Thickness(i == 0 ? 0 : 6, 0, i == nvmes.Count - 1 ? 0 : 6, 0);
                Grid.SetColumn(card, i);
                NvmeGrid.Children.Add(card);

                AddNvmeLegend(n.Name, color);
                _nvme[n.Name] = v;
                i++;
            }
        }

        // Tuile NVMe au même style que les tuiles CPU/GPU/RAM du haut.
        private Border BuildNvmeTile(NvmeInfo n, NvmeVisual v)
        {
            var card  = new Border { Style = (Style)FindResource("MTile") };
            var stack = new StackPanel();

            // En-tête (catégorie) + nom du disque — comme "PROCESSEUR" / "Intel Core…"
            stack.Children.Add(new TextBlock { Text = "DISQUE NVMe", Style = (Style)FindResource("MHdr") });
            stack.Children.Add(new TextBlock { Text = n.Name, Style = (Style)FindResource("MName") });

            // Grosse valeur : % d'utilisation (couleur signature) + "%"
            var bigRow = new StackPanel { Orientation = Orientation.Horizontal };
            v.UsageBig = new TextBlock
            {
                Text = $"{n.UsagePct:F0}", Style = (Style)FindResource("MBig"), Foreground = v.Color,
            };
            bigRow.Children.Add(v.UsageBig);
            bigRow.Children.Add(new TextBlock
            {
                Text = "%", Foreground = v.Color, FontSize = 16, FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)FindResource("AppFont"),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 5),
            });
            stack.Children.Add(bigRow);

            // Barre d'utilisation (couleur signature)
            var barOuter = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 12) };
            barOuter.Children.Add(new Border { Background = (Brush)FindResource("ThTrack"), CornerRadius = new CornerRadius(3) });
            v.UsageBar = new Grid();
            v.UsageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0,   GridUnitType.Star) });
            v.UsageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });
            var fill = new Border { Background = v.Color, CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(fill, 0);
            v.UsageBar.Children.Add(fill);
            barOuter.Children.Add(v.UsageBar);
            stack.Children.Add(barOuter);

            // Ligne détail : Température (libellé gauche / valeur droite, comme les tuiles du haut)
            var tempRow = new Grid();
            tempRow.Children.Add(new TextBlock { Text = "Température", Style = (Style)FindResource("MRowLbl") });
            v.TempVal = new TextBlock
            {
                Text = $"{n.TempC} °C", Style = (Style)FindResource("MRowVal"), Foreground = TempColor(n.TempC),
            };
            tempRow.Children.Add(v.TempVal);
            stack.Children.Add(tempRow);

            card.Child = stack;
            return card;
        }

        private void AddNvmeLegend(string fullName, Brush color)
        {
            var shortName = fullName.Split(' ')[0];   // marque : PNY, Samsung…
            NvmeLegend.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center,
            });
            NvmeLegend.Children.Add(new TextBlock
            {
                Text = shortName, Foreground = (Brush)FindResource("ThTextNav"),
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 11,
                Margin = new Thickness(5, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Vert < 50 °C, orange 50-64 °C, rouge >= 65 °C
        private static Brush TempColor(int t)
        {
            if (t >= 65) return new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55));
            if (t >= 50) return new SolidColorBrush(Color.FromRgb(0xF5, 0xC2, 0x4A));
            return new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0x6A));
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
            foreach (var v in _nvme.Values) DrawLine(v.Line, v.Hist);
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
