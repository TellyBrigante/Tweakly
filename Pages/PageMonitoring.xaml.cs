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
            public Brush        Color    = Brushes.Gray;   // couleur signature (vive, mode sombre)
            public TextBlock    UsageBig = null!;
            public TextBlock    PctLabel = null!;            // le « % »
            public Grid         UsageBar = null!;
            public Border       BarFill  = null!;            // remplissage de la barre
            public Ellipse      Dot      = null!;            // pastille de légende
            public TextBlock    TempVal  = null!;
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

        // ── Libération de la RAM (cache fichiers + working sets inactifs) ───────
        private async void BtnFreeRam_Click(object sender, RoutedEventArgs e)
        {
            var original = BtnFreeRam.Content;
            BtnFreeRam.IsEnabled = false;
            BtnFreeRam.Content   = "...";
            _main.Log("Monitoring : libération de la mémoire (cache + working sets inactifs)…");

            try
            {
                double gained = await Task.Run(MemoryCleaner.FreeMemory);

                await TickAsync();   // rafraîchit la tuile tout de suite → le % descend visiblement

                BtnFreeRam.Content = gained >= 0.1 ? $"✓ {gained:F1} Go" : "✓ Fait";
                _main.Log(gained >= 0.1
                    ? $"Monitoring : ~{gained:F1} Go de RAM rendus disponibles."
                    : "Monitoring : mémoire libérée (peu de RAM récupérable — déjà optimal).");
            }
            catch (Exception ex)
            {
                BtnFreeRam.Content = "Échec";
                _main.Log($"Monitoring : échec libération mémoire — {ex.Message}");
            }

            await Task.Delay(2600);
            BtnFreeRam.Content   = original;
            BtnFreeRam.IsEnabled = true;
        }

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
                _cpuNameSet = true;
            }
            TxtCpuUsage.Text = $"{s.CpuUsage:F0}";
            TxtCpuFreq.Text  = s.CpuMHz > 0 ? $"{s.CpuMHz / 1000.0:F2} GHz" : "—";

            // Ligne du bas : température CPU si activée + lisible (pilote PawnIO), sinon fréquence de base
            if (s.CpuTempC.HasValue)
            {
                TxtCpuBaseLbl.Text = "Température";
                TxtCpuBase.Text    = $"{s.CpuTempC.Value:F0} °C";
            }
            else
            {
                TxtCpuBaseLbl.Text = "Fréq. de base";
                TxtCpuBase.Text    = s.CpuBaseMHz > 0 ? $"{s.CpuBaseMHz / 1000.0:F2} GHz" : "—";
            }
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

            // GPU — carte dédiée (données riches via nvidia-smi) ou IGP/AMD (usage + nom, reste « — »)
            if (s.GpuOk)
            {
                TxtGpuName.Text  = s.GpuIsIntegrated ? $"{s.GpuName} (intégré)" : s.GpuName;
                TxtGpuUsage.Text = $"{s.GpuUsage:F0}";
                // VRAM : « used / total » si dispo, sinon « total » seule, sinon « — » (IGP sans VRAM dédiée)
                if (s.GpuVramUsedMB > 0 && s.GpuVramTotalMB > 0)
                    TxtGpuVram.Text = $"{s.GpuVramUsedMB / 1024.0:F1} / {s.GpuVramTotalMB / 1024.0:F1} Go";
                else if (s.GpuVramTotalMB > 0)
                    TxtGpuVram.Text = $"{s.GpuVramTotalMB / 1024.0:F1} Go";
                else
                    TxtGpuVram.Text = "—";
                TxtGpuTemp.Text  = s.GpuTemp  > 0 ? $"{s.GpuTemp:F0} °C"  : "—";
                TxtGpuWatts.Text = s.GpuWatts > 0 ? $"{s.GpuWatts:F0} W"  : "—";
                TxtGpuFreq.Text  = s.GpuMHz   > 0 ? $"{s.GpuMHz:F0} MHz"  : "—";
                SetBar(BarGpu, s.GpuUsage);
            }
            else
            {
                TxtGpuName.Text  = "Aucun GPU détecté";
                TxtGpuUsage.Text = "—";
                TxtGpuVram.Text  = TxtGpuTemp.Text = TxtGpuWatts.Text = TxtGpuFreq.Text = "—";
            }

            // NVMe
            UpdateNvme(s.Nvmes);

            // Historique + tracé
            Push(_cpuHist, s.CpuUsage);
            Push(_gpuHist, s.GpuOk ? s.GpuUsage : 0);
            Push(_ramHist, s.RamPct);
            DrawLine(CpuLine, null, _cpuHist);
            DrawLine(GpuLine, null, _gpuHist);
            DrawLine(RamLine, null, _ramHist);
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
                v.UsageBig.Text = $"{n.UsagePct:F0}";
                var sigCol = LegibleText(v.Color);   // signature, assombrie en mode clair (cohérent partout)
                v.UsageBig.Foreground = sigCol;
                v.PctLabel.Foreground = sigCol;
                v.BarFill.Background  = BarGradient(sigCol);   // dégradé style barre de MAJ (v1.3.5)
                v.Line.Stroke         = sigCol;
                v.Dot.Fill            = sigCol;
                SetBar(v.UsageBar, n.UsagePct);
                v.TempVal.Text       = $"{n.TempC} °C";
                v.TempVal.Foreground = TempColor(n.TempC);

                Push(v.Hist, n.UsagePct);
                DrawLine(v.Line, null, v.Hist);
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
                v.Line = new Polyline
                {
                    Stroke = color, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                GraphArea.Children.Add(v.Line);

                var card = BuildNvmeTile(n, v);
                card.Margin = new Thickness(i == 0 ? 0 : 6, 0, i == nvmes.Count - 1 ? 0 : 6, 0);
                Grid.SetColumn(card, i);
                NvmeGrid.Children.Add(card);

                AddNvmeLegend(n.Name, v);
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
            v.PctLabel = new TextBlock
            {
                Text = "%", Foreground = v.Color, FontSize = 16, FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)FindResource("AppFont"),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 5),
            };
            bigRow.Children.Add(v.PctLabel);
            stack.Children.Add(bigRow);

            // Barre d'utilisation (couleur signature)
            var barOuter = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 12) };
            var track = new Border { CornerRadius = new CornerRadius(3) };
            track.SetResourceReference(Border.BackgroundProperty, "ThTrack");   // suit le thème
            barOuter.Children.Add(track);
            v.UsageBar = new Grid();
            v.UsageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0,   GridUnitType.Star) });
            v.UsageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });
            v.BarFill = new Border { Background = v.Color, CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(v.BarFill, 0);
            v.UsageBar.Children.Add(v.BarFill);
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

        private void AddNvmeLegend(string fullName, NvmeVisual v)
        {
            var shortName = fullName.Split(' ')[0];   // marque : PNY, Samsung…
            v.Dot = new Ellipse
            {
                Width = 8, Height = 8, Fill = v.Color, VerticalAlignment = VerticalAlignment.Center,
            };
            NvmeLegend.Children.Add(v.Dot);
            var lbl = new TextBlock
            {
                Text = shortName,
                FontFamily = (FontFamily)FindResource("AppFont"), FontSize = 11,
                Margin = new Thickness(5, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextNav");   // suit le thème
            NvmeLegend.Children.Add(lbl);
        }

        // Vert < 50 °C, ambre 50-64 °C, rouge >= 65 °C — couleurs thémables (lisibles en clair)
        private static Brush TempColor(int t)
        {
            string role = t >= 65 ? "ThCrit" : t >= 50 ? "ThWarn" : "ThOk";
            return new SolidColorBrush(ThemeManager.C(role));
        }

        // Rend une couleur de signature lisible sur fond CLAIR (le jaune/cyan vif y est illisible).
        // En mode sombre on garde la couleur vive d'origine.
        private static Brush LegibleText(Brush sig)
        {
            if (ThemeManager.Current != ThemeManager.Mode.Light || sig is not SolidColorBrush sb) return sig;
            var c = sb.Color;
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (lum <= 130) return sig;                 // déjà assez foncé pour le fond clair
            double f = 110.0 / lum;                       // ramène la luminance autour de 110
            return new SolidColorBrush(Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f)));
        }

        /// <summary>Dégradé horizontal sombre→couleur→clair à partir d'une couleur signature
        /// (la recette de la barre de MAJ, déclinée par tuile — v1.3.5).</summary>
        private static LinearGradientBrush BarGradient(Brush sig)
        {
            var c = (sig as SolidColorBrush)?.Color ?? Colors.Gray;
            Color Shade(double f) => Color.FromRgb(
                (byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));
            Color Tint(double f) => Color.FromRgb(
                (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Shade(0.78), 0),
                    new GradientStop(c,           0.6),
                    new GradientStop(Tint(0.35),  1),
                },
            };
            b.Freeze();
            return b;
        }

        private static void Push(List<double> list, double v)
        {
            list.Add(v);
            if (list.Count > MaxPoints) list.RemoveAt(0);
        }

        private static void SetBar(Grid bar, double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            double cur = bar.ColumnDefinitions[0].Width.Value;
            // Skip de l'animation 300 ms si la barre n'a quasi pas bougé (delta < 1 %) : sinon
            // on cumule 4-8 anims actives à chaque tick 1 s pour rien et l'UI thread bouge en
            // continu sans bénéfice visuel (v1.4.3).
            if (Math.Abs(cur - pct) < 1.0)
            {
                bar.ColumnDefinitions[0].BeginAnimation(ColumnDefinition.WidthProperty, null);
                bar.ColumnDefinitions[1].BeginAnimation(ColumnDefinition.WidthProperty, null);
                bar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
                bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
                return;
            }
            var dur = new Duration(TimeSpan.FromMilliseconds(300));
            bar.ColumnDefinitions[0].BeginAnimation(ColumnDefinition.WidthProperty,
                new GridLengthAnimation { From = cur, To = pct, Duration = dur });
            bar.ColumnDefinitions[1].BeginAnimation(ColumnDefinition.WidthProperty,
                new GridLengthAnimation { From = 100 - cur, To = 100 - pct, Duration = dur });
        }

        // ── Graphique ──────────────────────────────────────────────────────────

        private void GraphArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawGrid();
            DrawLine(CpuLine, null, _cpuHist);
            DrawLine(GpuLine, null, _gpuHist);
            DrawLine(RamLine, null, _ramHist);
            foreach (var v in _nvme.Values) DrawLine(v.Line, null, v.Hist);
        }

        private void DrawGrid()
        {
            GridCanvas.Children.Clear();
            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // v1.3.5 : grille en POINTILLÉS discrets (la grille pleine faisait
            // « Gestionnaire des tâches »)
            foreach (var p in new[] { 25, 50, 75 })
            {
                double y = h - (p / 100.0) * h;
                var line = new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    StrokeThickness = 1, Opacity = 0.35,
                    StrokeDashArray = new DoubleCollection { 3, 5 },
                };
                line.SetResourceReference(Shape.StrokeProperty, "ThBorder");   // suit le thème
                GridCanvas.Children.Add(line);
            }
        }

        /// <summary>
        /// v1.3.5 : courbe LISSÉE (interpolation Catmull-Rom, 6 sous-points par segment —
        /// fini les angles « Gestionnaire des tâches ») + AIRE optionnelle sous la courbe
        /// (Polygon fermé jusqu'au bas du graphe, rempli d'un dégradé couleur→transparent).
        /// </summary>
        private void DrawLine(Polyline line, Polygon? fill, List<double> data)
        {
            double w = GraphArea.ActualWidth, h = GraphArea.ActualHeight;
            if (w <= 0 || h <= 0 || data.Count < 2)
            {
                line.Points = new PointCollection();
                if (fill != null) fill.Points = new PointCollection();
                return;
            }

            double step = w / (MaxPoints - 1);
            int count = data.Count;
            var raw = new Point[count];
            for (int i = 0; i < count; i++)
            {
                // newest ancré à droite, défile vers la gauche
                double x = w - (count - 1 - i) * step;
                double y = h - (Math.Max(0, Math.Min(100, data[i])) / 100.0) * h;
                raw[i] = new Point(x, y);
            }

            var smooth = CatmullRom(raw, 6, h);
            line.Points = smooth;

            if (fill != null)
            {
                var area = new PointCollection(smooth.Count + 2);
                foreach (var pt in smooth) area.Add(pt);
                area.Add(new Point(smooth[smooth.Count - 1].X, h));   // descend au sol à droite
                area.Add(new Point(smooth[0].X, h));                   // referme au sol à gauche
                fill.Points = area;
            }
        }

        // Interpolation Catmull-Rom : passe par TOUS les points mesurés (aucune triche
        // sur les données), n'arrondit que le trajet entre eux. Y borné au graphe.
        private static PointCollection CatmullRom(Point[] p, int subdiv, double maxY)
        {
            var outPts = new PointCollection((p.Length - 1) * subdiv + 1);
            for (int i = 0; i < p.Length - 1; i++)
            {
                var p0 = p[Math.Max(0, i - 1)];
                var p1 = p[i];
                var p2 = p[i + 1];
                var p3 = p[Math.Min(p.Length - 1, i + 2)];
                for (int s = 0; s < subdiv; s++)
                {
                    double t = s / (double)subdiv, t2 = t * t, t3 = t2 * t;
                    double x = 0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t
                             + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2
                             + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                    double y = 0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t
                             + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2
                             + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                    outPts.Add(new Point(x, Math.Max(0, Math.Min(maxY, y))));
                }
            }
            outPts.Add(p[p.Length - 1]);
            return outPts;
        }
    }
}
