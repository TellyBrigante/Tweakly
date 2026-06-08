using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageBenchmark : UserControl
    {
        private readonly MainWindow _main;
        private List<BenchmarkResult> _history = new();
        private BenchmarkResult? _selA, _selB;
        private CancellationTokenSource? _cts;

        public PageBenchmark(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _history = BenchmarkStore.Load().OrderByDescending(x => x.Timestamp).ToList();
            if (_history.Count >= 2) { _selA = _history[1]; _selB = _history[0]; }
            RefreshHeader();
            RefreshSubScores();
            RefreshHistory();
            RefreshSparkline();
            RenderCompare();
        }

        // ── Lancement : confirmation, puis bench ─────────────────────────────

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            NoiseWarn.Visibility = Visibility.Collapsed;
            try
            {
                var p = Process.GetCurrentProcess();
                var t0 = p.TotalProcessorTime;
                await Task.Delay(700);
                var t1 = Process.GetCurrentProcess().TotalProcessorTime;
                double pct = (t1 - t0).TotalMilliseconds / (700.0 * Environment.ProcessorCount) * 100;
                if (pct > 5) NoiseWarn.Visibility = Visibility.Visible;
            }
            catch { }

            ConfirmOverlay.Visibility = Visibility.Visible;
        }

        private void BtnCancelConfirm_Click(object sender, RoutedEventArgs e)
            => ConfirmOverlay.Visibility = Visibility.Collapsed;

        private async void BtnGoConfirm_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            await RunBenchAsync();
        }

        private async Task RunBenchAsync()
        {
            BtnRun.IsEnabled = false;
            BenchOverlay.Visibility = Visibility.Visible;
            SetProgress(0); TxtOverlayPhase.Text = "Préparation…";
            _main.Log("Benchmark : démarrage…");
            _cts = new CancellationTokenSource();

            var progress = new Progress<(Benchmark.Phase phase, double pct)>(p =>
            {
                TxtOverlayPhase.Text = p.phase switch
                {
                    Benchmark.Phase.CpuMono  => "CPU — performance mono-thread…",
                    Benchmark.Phase.CpuMulti => "CPU — performance multi-thread…",
                    Benchmark.Phase.System   => "Système — réactivité (jitter timer)…",
                    Benchmark.Phase.Network  => "Réseau — ping & jitter (1.1.1.1)…",
                    Benchmark.Phase.Done     => "Calcul du score…",
                    _ => "…"
                };
                SetProgress(p.pct);
            });

            BenchmarkResult? r = null;
            try { r = await Benchmark.RunAsync(progress, _cts.Token); }
            catch (Exception ex) { _main.Log("Benchmark : erreur — " + ex.Message); }

            BenchOverlay.Visibility = Visibility.Collapsed;
            BtnRun.IsEnabled = true;
            if (r == null) return;

            BenchmarkStore.Append(r);
            _history = BenchmarkStore.Load().OrderByDescending(x => x.Timestamp).ToList();
            if (_history.Count >= 2) { _selA = _history[1]; _selB = _history[0]; }
            RefreshHeader();
            RefreshSubScores();
            RefreshHistory();
            RefreshSparkline();
            RenderCompare();
            _main.Log($"Benchmark : terminé — score {r.TotalScore} (CPU {r.CpuScore}, Sys {r.SysScore}, Net {r.NetScore}).");
        }

        // ── Effacer l'historique (confirmation obligatoire) ──────────────────
        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0) return;

            var r = MessageBox.Show(
                $"Effacer l'historique des {_history.Count} benchmark(s) ?\n\n" +
                "Cette action est irréversible. La référence du CPU (base externe) reste inchangée.",
                "Effacer l'historique",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            BenchmarkStore.Clear();
            _history = new();
            _selA = _selB = null;
            RefreshHeader();
            RefreshSubScores();
            RefreshHistory();
            RefreshSparkline();
            RenderCompare();
            _main.Log("Benchmark : historique effacé.");
        }

        private void SetProgress(double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            ProgressBar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            ProgressBar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            TxtOverlayPct.Text = $"{pct:F0} %";
        }

        // ── BLOC 1 : Tweakly Score (thermomètre + grand chiffre + verdict) ────

        private void RefreshHeader()
        {
            if (_history.Count == 0)
            {
                TxtScore.Text     = "—";
                TxtVerdict.Text   = "aucune mesure encore — lance ton premier bench";
                TxtLastWhen.Text  = "";
                SetScoreBar(0);
                return;
            }
            var last = _history[0];
            TxtScore.Text    = last.TotalScore.ToString();
            TxtVerdict.Text  = ScoreVerdict(last.TotalScore);
            TxtLastWhen.Text = $"dernière mesure : {last.Timestamp:dd/MM/yyyy HH:mm}";
            SetScoreBar(last.TotalScore);
        }

        // Thermomètre 0-150 (au-dessus de 100 = surperformance, contenu dans la zone "ThOk")
        private void SetScoreBar(int score)
        {
            double t = Math.Max(0, Math.Min(150, score));
            // Les DEUX colonnes doivent être ajustées ensemble pour garder un ratio correct :
            // sinon avec ScoreColRest figé à 150*, le remplissage = t/(t+150) au lieu de t/150.
            ScoreCol.Width     = new GridLength(t,         GridUnitType.Star);
            ScoreColRest.Width = new GridLength(150.0 - t, GridUnitType.Star);
            ScoreMarker.Margin = new Thickness(MarkerOffset(t), -4, 0, 0);
        }

        private double MarkerOffset(double scoreFrom0to150)
        {
            // ScoreBar a 150 colonnes étoile (0-150). Le marker doit suivre la même proportion.
            // Largeur réelle de la piste = celle du contrôle parent — on prend celle de ScoreBar à l'arrange.
            double w = ScoreBar.ActualWidth;
            if (w <= 0) return 0;
            return Math.Max(0, Math.Min(w, w * scoreFrom0to150 / 150.0));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            if (_history.Count > 0) SetScoreBar(_history[0].TotalScore);
            RefreshSparkline();
        }

        private static string ScoreVerdict(int s) => s switch
        {
            >= 110 => "excellent — tu surperformes le nominal",
            >= 95  => "dans la norme — ton matériel rend ce qu'il doit",
            >= 80  => "correct — léger en-dessous du nominal",
            >= 60  => "faible — quelque chose te ralentit",
            _      => "très faible — système en souffrance ou bruit pendant la mesure"
        };

        // ── BLOC 2 : 3 sous-scores (CPU, Système, Réseau) ────────────────────

        private void RefreshSubScores()
        {
            if (_history.Count == 0)
            {
                TxtCpuScore.Text = TxtSysScore.Text = TxtNetScore.Text = "—";
                TxtCpuRefModel.Text = "—";
                TxtCpuMeasured.Text = TxtCpuNominal.Text = "—";
                TxtSysMeasured.Text = TxtNetMeasured.Text = "—";
                CpuCol.Width = SysCol.Width = NetCol.Width = new GridLength(0, GridUnitType.Star);
                CpuColRest.Width = new GridLength(150, GridUnitType.Star);
                SysColRest.Width = NetColRest.Width = new GridLength(100, GridUnitType.Star);
                return;
            }
            var r = _history[0];

            // ── CPU (échelle 0-150) ─────────────────────────────────────────
            TxtCpuScore.Text = r.CpuScore.ToString();
            double cpuT = Math.Max(0, Math.Min(150, r.CpuScore));
            CpuCol.Width     = new GridLength(cpuT,         GridUnitType.Star);
            CpuColRest.Width = new GridLength(150.0 - cpuT, GridUnitType.Star);
            if (r.HasNominalRef)
            {
                TxtCpuRefModel.Text = $"Référence : {r.NominalRefModel}";
                TxtCpuMeasured.Text = $"{r.CpuMultiMops:F0} Mio/s";
                TxtCpuNominal.Text  = $"{r.NominalMultiMops:F0} Mio/s";
            }
            else
            {
                TxtCpuRefModel.Text = $"CPU non répertorié — référence personnelle (1re mesure = 100)";
                TxtCpuMeasured.Text = $"{r.CpuMultiMops:F0} Mio/s";
                TxtCpuNominal.Text  = "—";
            }

            // ── Système (échelle 0-100) ────────────────────────────────────
            TxtSysScore.Text = r.SysScore.ToString();
            double sysT = Math.Max(0, Math.Min(100, r.SysScore));
            SysCol.Width     = new GridLength(sysT,         GridUnitType.Star);
            SysColRest.Width = new GridLength(100.0 - sysT, GridUnitType.Star);
            TxtSysMeasured.Text = $"{r.SysJitterMicroSec:F0} µs";

            // ── Réseau (échelle 0-100) ─────────────────────────────────────
            TxtNetScore.Text = r.NetScore.ToString();
            double netT = Math.Max(0, Math.Min(100, r.NetScore));
            NetCol.Width     = new GridLength(netT,         GridUnitType.Star);
            NetColRest.Width = new GridLength(100.0 - netT, GridUnitType.Star);
            TxtNetMeasured.Text =
                r.NetPingMs < 0
                    ? "—"
                    : $"{r.NetPingMs:F0} ms · jitter {r.NetJitterMs:F1} ms · perte {r.NetLossPct:F0} %";
        }

        // ── BLOC 3 : Historique + sparkline ──────────────────────────────────

        private void RefreshHistory()
        {
            HistoryPanel.Children.Clear();
            if (_history.Count == 0)
            {
                HistoryPanel.Children.Add(new TextBlock
                {
                    Text = "Aucune mesure encore. Clique « LANCER LE BENCHMARK » pour démarrer.",
                    Foreground = (Brush)FindResource("ThTextDim"),
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0),
                });
                return;
            }
            foreach (var r in _history) HistoryPanel.Children.Add(BuildHistoryRow(r));
        }

        private Border BuildHistoryRow(BenchmarkResult r)
        {
            var card = new Border
            {
                CornerRadius   = new CornerRadius(8),
                Padding        = new Thickness(12, 10, 12, 10),
                Margin         = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(1),
                Tag            = r,
            };
            card.SetResourceReference(Border.BackgroundProperty, "ThSecBtn");
            card.SetResourceReference(Border.BorderBrushProperty,
                SameRow(r, _selA) || SameRow(r, _selB) ? "ThSelection" : "ThBorder");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var score = new TextBlock
            {
                Text = r.TotalScore.ToString(),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 22, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            score.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
            Grid.SetColumn(score, 0);
            grid.Children.Add(score);

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var date = new TextBlock
            {
                Text = r.Timestamp.ToString("dd/MM/yyyy HH:mm"),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            };
            date.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
            // Sous-ligne plus parlante : chiffres bruts CPU + système + réseau
            string sub = r.HasNominalRef
                ? $"CPU {r.CpuMultiMops:F0}/{r.NominalMultiMops:F0} Mio/s  ·  Jitter {r.SysJitterMicroSec:F0} µs  ·  Ping {(r.NetPingMs < 0 ? "—" : r.NetPingMs.ToString("F0") + " ms")}"
                : $"CPU {r.CpuMultiMops:F0} Mio/s  ·  Jitter {r.SysJitterMicroSec:F0} µs  ·  Ping {(r.NetPingMs < 0 ? "—" : r.NetPingMs.ToString("F0") + " ms")}";
            var subTb = new TextBlock
            {
                Text = sub,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            meta.Children.Add(date); meta.Children.Add(subTb);
            Grid.SetColumn(meta, 1);
            grid.Children.Add(meta);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (SameRow(r, _selA))
                actions.Children.Add(MakePill("AVANT", "ThOk"));
            else if (SameRow(r, _selB))
                actions.Children.Add(MakePill("APRÈS", "ThWarn"));
            else
            {
                actions.Children.Add(MakeMiniBtn("Avant", () => { _selA = r; RefreshHistory(); RenderCompare(); }));
                actions.Children.Add(MakeMiniBtn("Après", () => { _selB = r; RefreshHistory(); RenderCompare(); }));
            }
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            card.Child = grid;
            return card;
        }

        private static bool SameRow(BenchmarkResult r, BenchmarkResult? sel)
            => sel != null && r.Timestamp == sel.Timestamp;

        private Border MakePill(string txt, string roleKey)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 8, 3),
                Margin  = new Thickness(2, 0, 0, 0),
            };
            var col = ThemeManager.C(roleKey);
            b.Background = new SolidColorBrush(Color.FromArgb(0x33, col.R, col.G, col.B));
            var tb = new TextBlock
            {
                Text = txt,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 10.5, FontWeight = FontWeights.Bold,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, roleKey);
            b.Child = tb;
            return b;
        }

        private Button MakeMiniBtn(string txt, Action onClick)
        {
            var btn = new Button
            {
                Content = txt,
                Style   = (Style)FindResource("SecondaryBtnStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin  = new Thickness(4, 0, 0, 0),
                FontSize = 11,
            };
            btn.Click += (_, _) => onClick();
            return btn;
        }

        // Sparkline = mini-courbe d'évolution du Tweakly Score
        private void RefreshSparkline()
        {
            SparkCanvas.Children.Clear();
            if (_history.Count < 2) return;
            double w = SparkCanvas.ActualWidth, h = SparkCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // On dessine du + ancien (gauche) au + récent (droite)
            var ordered = _history.OrderBy(x => x.Timestamp).ToList();
            int n = ordered.Count;
            double step = n > 1 ? w / (n - 1) : 0;
            double minS = 0, maxS = 150;
            var pts = new PointCollection(n);
            for (int i = 0; i < n; i++)
            {
                double x = i * step;
                double y = h - (Math.Max(minS, Math.Min(maxS, ordered[i].TotalScore)) - minS) / (maxS - minS) * h;
                pts.Add(new Point(x, y));
            }
            var line = new Polyline
            {
                Points = pts,
                StrokeThickness = 1.8,
                Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0xA0, 0xFF)),
                StrokeLineJoin = PenLineJoin.Round,
            };
            SparkCanvas.Children.Add(line);
            // Ligne de référence 100
            double y100 = h - (100 - minS) / (maxS - minS) * h;
            var refLine = new Line
            {
                X1 = 0, X2 = w, Y1 = y100, Y2 = y100,
                StrokeThickness = 1, Opacity = 0.4,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0x6A)),
                StrokeDashArray = new DoubleCollection { 3, 3 },
            };
            SparkCanvas.Children.Insert(0, refLine);
        }

        // ── BLOC 4 : Comparateur ────────────────────────────────────────────

        private void RenderCompare()
        {
            ComparePanel.Children.Clear();
            TxtCompareRange.Text = "";

            if (_history.Count == 0)
            {
                ComparePanel.Children.Add(MakeText(
                    "Lance ton premier benchmark — il deviendra le point de départ.",
                    "ThTextDim", 12.5, wrap: true));
                return;
            }
            if (_history.Count == 1)
            {
                ComparePanel.Children.Add(MakeText(
                    "Une seule mesure pour l'instant. Applique tes tweaks, relance un benchmark, et tu verras l'évolution chiffrée ici.",
                    "ThTextDim", 12.5, wrap: true));
                return;
            }
            if (_selA == null || _selB == null)
            {
                ComparePanel.Children.Add(MakeText(
                    "Choisis dans l'historique l'entrée AVANT puis l'entrée APRÈS pour comparer.",
                    "ThTextDim", 12.5, wrap: true));
                return;
            }

            var c = BenchmarkStore.Compare(_selA, _selB);
            TxtCompareRange.Text = $"{_selA.Timestamp:dd/MM HH:mm}  →  {_selB.Timestamp:dd/MM HH:mm}";

            // Verdict en gros
            string role = Math.Abs(c.TotalDelta) < 2 ? "ThTextBody"
                        : c.TotalDelta > 0 ? "ThOk" : "ThCrit";
            ComparePanel.Children.Add(MakeText(c.Verdict, role, 20, bold: true, margin: new Thickness(0,0,0,2)));
            ComparePanel.Children.Add(MakeText(
                "Évolution de la mesure AVANT vers la mesure APRÈS.",
                "ThTextDim", 11.5, margin: new Thickness(0,0,0,16)));

            // ── 2 cartes côte-à-côte : AVANT (vert) et APRÈS (orange) ─────
            var cards = new Grid();
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cardA = BuildCompareCard(_selA, "AVANT", "ThOk");
            cardA.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(cardA, 0); cards.Children.Add(cardA);

            // Flèche au milieu
            var arrow = new TextBlock
            {
                Text = "→",
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 28, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            Grid.SetColumn(arrow, 1); cards.Children.Add(arrow);

            var cardB = BuildCompareCard(_selB, "APRÈS", "ThWarn");
            cardB.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(cardB, 2); cards.Children.Add(cardB);

            ComparePanel.Children.Add(cards);

            // ── Récap des deltas (sous les 2 cartes) ──────────────────────
            ComparePanel.Children.Add(MakeText("ÉVOLUTION", "ThTextDim", 11, bold: true,
                margin: new Thickness(0, 18, 0, 8)));

            AddDeltaRowRaw("Tweakly Score",
                _selA.TotalScore.ToString(), _selB.TotalScore.ToString(), c.TotalDelta, suffix: "");
            AddDeltaRowRaw("CPU (multi)",
                $"{_selA.CpuMultiMops:F0}", $"{_selB.CpuMultiMops:F0}",
                Delta(_selA.CpuMultiMops, _selB.CpuMultiMops), suffix: " Mio/s");
            AddDeltaRowRaw("Jitter système",
                $"{_selA.SysJitterMicroSec:F0}", $"{_selB.SysJitterMicroSec:F0}",
                -Delta(_selA.SysJitterMicroSec, _selB.SysJitterMicroSec), suffix: " µs (bas = mieux)");
            AddDeltaRowRaw("Ping",
                _selA.NetPingMs < 0 ? "—" : $"{_selA.NetPingMs:F0}",
                _selB.NetPingMs < 0 ? "—" : $"{_selB.NetPingMs:F0}",
                -Delta(_selA.NetPingMs, _selB.NetPingMs), suffix: " ms (bas = mieux)");

            ComparePanel.Children.Add(MakeText(
                "Le bench mesure CPU, réactivité système et latence réseau. La fluidité en jeu, le temps de boot et la latence in-game ne sont pas mesurés ici.",
                "ThTextDim", 11, wrap: true, italic: true,
                margin: new Thickness(0, 14, 0, 0)));
        }

        // Carte AVANT/APRÈS : entête coloré + score gros + chiffres bruts
        private Border BuildCompareCard(BenchmarkResult r, string label, string roleKey)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
            };
            border.SetResourceReference(Border.BackgroundProperty, "ThSecBtn");
            // Bordure colorée à la couleur du rôle (transparence ~70 %)
            var col = ThemeManager.C(roleKey);
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, col.R, col.G, col.B));

            var st = new StackPanel();

            // En-tête : pastille rôle + date
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,8) };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x33, col.R, col.G, col.B)),
            };
            var pillTb = new TextBlock
            {
                Text = label,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 10.5, FontWeight = FontWeights.Bold,
            };
            pillTb.SetResourceReference(TextBlock.ForegroundProperty, roleKey);
            pill.Child = pillTb;
            head.Children.Add(pill);
            var when = new TextBlock
            {
                Text = "  " + r.Timestamp.ToString("dd/MM/yyyy HH:mm"),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            };
            when.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            head.Children.Add(when);
            st.Children.Add(head);

            // Gros score + sous-scores
            var scoreLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,8) };
            var bigScore = new TextBlock
            {
                Text = r.TotalScore.ToString(),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 32, FontWeight = FontWeights.Bold,
            };
            bigScore.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
            scoreLine.Children.Add(bigScore);
            var subScores = new TextBlock
            {
                Text = $"CPU {r.CpuScore}  ·  Système {r.SysScore}  ·  Réseau {r.NetScore}",
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 0, 6),
            };
            subScores.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            scoreLine.Children.Add(subScores);
            st.Children.Add(scoreLine);

            // Chiffres bruts (mesuré uniquement, le nominal est ailleurs)
            AddRawLine(st, "CPU multi",      $"{r.CpuMultiMops:F0} Mio/s");
            AddRawLine(st, "Jitter système", $"{r.SysJitterMicroSec:F0} µs");
            AddRawLine(st, "Ping",           r.NetPingMs < 0 ? "—" : $"{r.NetPingMs:F0} ms");

            border.Child = st;
            return border;
        }

        private void AddRawLine(StackPanel host, string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lbl = MakeText(label, "ThTextDim", 11.5);
            Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
            var v = MakeText(value, "ThTextBody", 12);
            v.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(v, 1); g.Children.Add(v);
            host.Children.Add(g);
        }

        private static double Delta(double a, double b) => a == 0 ? 0 : (b - a) * 100.0 / a;

        private void AddDeltaRowRaw(string label, string a, string b, double pctForColor, string suffix)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = MakeText(label, "ThTextBody", 12.5);
            Grid.SetColumn(lbl, 0); g.Children.Add(lbl);

            var raw = MakeText($"{a} → {b}{suffix}", "ThTextDim", 12);
            Grid.SetColumn(raw, 1); g.Children.Add(raw);

            string role = Math.Abs(pctForColor) < 2 ? "ThTextDim" : pctForColor > 0 ? "ThOk" : "ThCrit";
            string sign = pctForColor > 0 ? "+" : "";
            var d = MakeText($"{sign}{pctForColor:F1} %", role, 12.5, bold: true);
            Grid.SetColumn(d, 2); g.Children.Add(d);
            ComparePanel.Children.Add(g);
        }

        private TextBlock MakeText(string text, string fgKey, double size,
                                   bool bold = false, bool italic = false, bool wrap = false,
                                   Thickness margin = default)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize   = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle  = italic ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Margin     = margin,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            return tb;
        }
    }
}
