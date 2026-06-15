using Optimisation_Tool.Helpers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Optimisation_Tool.Pages
{
    /// <summary>
    /// Page « Suivi en jeu » — capture une session via PresentMon, l'analyse avec
    /// <see cref="SessionAnalyzer"/> (corrélateur multi-process, classification par
    /// frame, recommandations personnalisées), affiche le rapport.
    ///
    /// Anti-impact sur le jeu : PresentMon est un consommateur ETW passif lancé en
    /// priorité basse. Pendant la capture, la PAGE elle-même se contente d'un timer
    /// 1 s (mise à jour du chrono à l'écran) — aucun monitoring n'est démarré.
    /// </summary>
    public partial class PageGameSession : UserControl
    {
        private readonly GameSessionRecorder _rec = new();
        private DispatcherTimer? _tick;
        private DateTime _recStart;
        private SessionAnalyzer.Report? _currentReport;   // pour redessiner au resize

        public PageGameSession() { InitializeComponent(); }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshHistory();
            // Animation discrète du dot rouge (pulsation 1.2 s)
            var pulse = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
            RecDot.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        /// <summary>
        /// Quitter la page pendant une capture = on ABANDONNE proprement (tue PresentMon +
        /// le timer + le CSV partiel). Sinon PresentMon continuerait à tourner en fond.
        /// </summary>
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_rec.IsRecording)
            {
                _tick?.Stop(); _tick = null;
                _rec.Abort();
                RecOverlay.Visibility = Visibility.Collapsed;
                AppLog.Write("PageGameSession : capture abandonnée (page quittée).");
            }
        }

        // ───────────────────────── démarrage / arrêt ─────────────────────────
        private void BtnRec_Click(object sender, RoutedEventArgs e)
        {
            if (_rec.IsRecording) return;
            if (!_rec.Start(null, out string error))
            {
                MessageBox.Show("Impossible de démarrer la capture :\n" + error,
                                "Tweakly — Suivi en jeu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _recStart = DateTime.UtcNow;
            RecOverlay.Visibility = Visibility.Visible;
            TxtRecElapsed.Text = "00:00";
            LiveFps.Text = "—";
            _fpsHist.Clear();
            FpsSpark.Children.Clear();
            _tick?.Stop();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) =>
            {
                var d = DateTime.UtcNow - _recStart;
                TxtRecElapsed.Text = $"{(int)d.TotalMinutes:D2}:{d.Seconds:D2}";
                UpdateLiveTiles();
            };
            _tick.Start();
            AppLog.Write("PageGameSession : capture démarrée.");
        }

        private static string Live(double v, string unit, string fmt = "0") =>
            double.IsNaN(v) ? "—" : v.ToString(fmt) + unit;

        // Historique FPS LIVE pour la sparkline (fenêtre glissante ~2 min à 1 Hz).
        private readonly List<double> _fpsHist = new();

        private void UpdateLiveTiles()
        {
            var s = _rec.LastSample;
            if (s == null) return;
            LiveCpu.Text      = Live(s.CpuLoadPct, " %");
            LiveGpu.Text      = Live(s.GpuUsagePct, " %");
            LiveGpuClock.Text = Live(s.GpuCoreMhz, " MHz");
            LiveGpuMem.Text   = Live(s.GpuMemMhz, " MHz");
            LiveGpuTemp.Text  = Live(s.GpuTempC, " °C");
            LiveVram.Text     = double.IsNaN(s.GpuVramUsedMB) ? "—" : (s.GpuVramUsedMB / 1024.0).ToString("0.0") + " Go";
            LiveRam.Text      = double.IsNaN(s.RamAvailMb) ? "—" : (s.RamAvailMb / 1024.0).ToString("0.0") + " Go";
            LiveFps.Text      = double.IsNaN(s.Fps) ? "—" : s.Fps.ToString("0");

            _fpsHist.Add(s.Fps);
            while (_fpsHist.Count > 120) _fpsHist.RemoveAt(0);
            DrawFpsSpark();
        }

        private void FpsSpark_SizeChanged(object sender, SizeChangedEventArgs e) => DrawFpsSpark();

        // Sparkline FPS : tracée à partir du buffer roulant (déjà échantillonné, 0 coût supplémentaire).
        private void DrawFpsSpark()
        {
            FpsSpark.Children.Clear();
            double w = FpsSpark.ActualWidth, h = FpsSpark.ActualHeight;
            if (w < 20 || h < 12) return;
            var pts = _fpsHist.Where(v => !double.IsNaN(v) && v > 0).ToList();
            if (pts.Count < 2) return;

            double max = pts.Max(), min = pts.Min();
            if (max - min < 1) { max += 5; min = Math.Max(0, min - 5); }
            double range = max - min, pad = 4, plotH = h - 2 * pad;

            var coll = new PointCollection();
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                double x = n == 1 ? 0 : i / (double)(n - 1) * w;
                double y = pad + (max - pts[i]) / range * plotH;
                coll.Add(new Point(x, y));
            }
            var poly = new System.Windows.Shapes.Polyline
            {
                StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Points = coll,
            };
            poly.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThAccentIcon");
            FpsSpark.Children.Add(poly);
        }

        private async void BtnStopRec_Click(object sender, RoutedEventArgs e)
        {
            _tick?.Stop(); _tick = null;
            BtnStopRec.IsEnabled = false; BtnStopRec.Content = "ANALYSE EN COURS…";
            try
            {
                var capture = await _rec.StopAsync();
                if (capture == null)
                {
                    MessageBox.Show("Aucune frame n'a été capturée. Vérifie que le jeu présentait bien à l'écran.",
                                    "Tweakly — Suivi en jeu", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var report = SessionAnalyzer.Analyze(capture);
                SessionStore.Append(report);
                ShowReport(report);
                RefreshHistory();
            }
            catch (Exception ex)
            {
                AppLog.Write("PageGameSession : erreur analyse — " + ex.Message);
                MessageBox.Show("Erreur d'analyse : " + ex.Message,
                                "Tweakly — Suivi en jeu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RecOverlay.Visibility = Visibility.Collapsed;
                BtnStopRec.IsEnabled = true; BtnStopRec.Content = "ARRÊTER ET ANALYSER";
            }
        }

        // ───────────────────────── rendu d'un rapport ─────────────────────────
        private void ShowReport(SessionAnalyzer.Report r)
        {
            _currentReport = r;
            VerdictTile.Visibility = Visibility.Visible;
            StatsRow.Visibility = Visibility.Visible;
            ChartTile.Visibility = r.Chart.Count > 5 ? Visibility.Visible : Visibility.Collapsed;
            RecoTile.Visibility = r.Recommendations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DropsTile.Visibility = r.Drops.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderChart();

            TxtScore.Text = r.Score.ToString();
            TxtScore.SetResourceReference(TextBlock.ForegroundProperty, ScoreRole(r.Score));
            TxtGameName.Text = r.GameDisplay;
            GameKnownBadge.Visibility = r.GameKnown ? Visibility.Visible : Visibility.Collapsed;
            TxtVerdict.Text = r.Verdict;

            TxtFpsAvg.Text = ((int)Math.Round(r.FpsAvg)).ToString();
            TxtDuration.Text = $"médian {r.FpsP50:0} fps · {r.FrameCount} frames / {r.DurationSec:0} s";
            // RÉGULARITÉ : CV% + verdict + bande perçue. Couleur selon la constance.
            TxtRegularity.Text = $"CV {r.FrametimeCvPct:0} %";
            string regWord = r.FrametimeCvPct <= 8 ? "très régulier"
                           : r.FrametimeCvPct <= 14 ? "un peu irrégulier"
                           : r.FrametimeCvPct <= 22 ? "jitter perceptible" : "instable";
            TxtRegularity.SetResourceReference(TextBlock.ForegroundProperty,
                r.FrametimeCvPct <= 8 ? "ThOk" : r.FrametimeCvPct <= 14 ? "ThWarn" : "ThCrit");
            TxtRegularityHint.Text = $"{regWord} · oscille {r.PerceivedFpsLow:0}–{r.PerceivedFpsHigh:0} fps";
            // PIRE FRAME : fps min réellement atteint (hors bordures déjà trimées) = ce que
            // l'utilisateur voit chuter en jeu, ≠ 0,1 % low qui est un seuil percentile.
            int worstFps = r.FrametimeMaxMs > 0 ? (int)Math.Round(1000.0 / r.FrametimeMaxMs) : 0;
            TxtWorstFps.Text = worstFps.ToString();
            TxtWorstFps.SetResourceReference(TextBlock.ForegroundProperty,
                worstFps < r.CompetitiveFps * 0.5 ? "ThCrit" : worstFps < r.CompetitiveFps ? "ThWarn" : "ThOk");
            int worstCount = r.Drops.Count(d => d.Cause != SessionAnalyzer.DropCause.ShaderCompile
                                             && 1000.0 / d.FrameTimeMs < r.CompetitiveFps);
            TxtWorstHint.Text = worstCount > 0 ? $"{worstCount} frames sous {r.CompetitiveFps} fps" : "le plus bas atteint en jeu";
            TxtMode.Text = string.IsNullOrEmpty(r.PresentMode) ? "—" : r.PresentMode;
            if (r.PresentModeOptimal)
            {
                TxtModeHint.Text = "présentation optimale, aucun compositeur dans le chemin";
                TxtMode.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
            }
            else if (!string.IsNullOrEmpty(r.PresentMode))
            {
                TxtModeHint.Text = "compositeur Windows dans le chemin — voir le diagnostic";
                TxtMode.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
            }

            // Recommandations
            RecoPanel.Children.Clear();
            foreach (var reco in r.Recommendations)
                RecoPanel.Children.Add(BuildRecoCard(reco));

            // Drops
            DropsPanel.Children.Clear();
            TxtDropsCount.Text = $"{r.Drops.Count} drops sur la session";
            foreach (var d in r.Drops.OrderBy(x => x.TimeMs))
                DropsPanel.Children.Add(BuildDropRow(d));
        }

        private Border BuildRecoCard(SessionAnalyzer.Recommendation reco)
        {
            // Fond translucide : tint léger qui reste lisible sur les DEUX thèmes.
            string colorBg = reco.Severity switch
            {
                SessionAnalyzer.RecoSeverity.Crit => "#33E05555",
                SessionAnalyzer.RecoSeverity.Warn => "#33F5C24A",
                _                                 => "#22315FA0",
            };
            // Accent (bordure + titre) = RÔLE de thème → assombri en clair (le rouge/jaune/bleu
            // vifs étaient illisibles en mode clair) ET vivant au basculement de thème.
            string accentRole = reco.Severity switch
            {
                SessionAnalyzer.RecoSeverity.Crit => "ThCrit",
                SessionAnalyzer.RecoSeverity.Warn => "ThWarn",
                _                                 => "ThAccentIcon",
            };

            var border = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(colorBg)!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
            };
            border.SetResourceReference(Border.BorderBrushProperty, accentRole);

            var stack = new StackPanel();
            var title = new TextBlock
            {
                Text = reco.Title,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, accentRole);
            stack.Children.Add(title);
            var expl = new TextBlock
            {
                Text = reco.Explanation,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
            };
            expl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
            stack.Children.Add(expl);
            if (!string.IsNullOrEmpty(reco.ActionLabel) && !string.IsNullOrEmpty(reco.ActionTarget))
            {
                var btn = new Button
                {
                    Content = reco.ActionLabel,
                    Style = (Style)FindResource("SecondaryBtnStyle"),
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Tag = reco.ActionTarget,
                };
                btn.Click += RecoAction_Click;
                stack.Children.Add(btn);
            }
            border.Child = stack;
            return border;
        }

        private void RecoAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string target) return;
            try
            {
                if (target.StartsWith("nav:", StringComparison.Ordinal))
                {
                    string tag = target.Substring("nav:".Length);
                    if (Window.GetWindow(this) is MainWindow mw)
                    {
                        var btn = FindNavButton(mw, tag);
                        if (btn != null) mw.NavigateTo(btn);
                    }
                }
                else if (target.StartsWith("openurl:", StringComparison.Ordinal))
                {
                    string url = target.Substring("openurl:".Length);
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("PageGameSession : erreur action — " + ex.Message);
            }
        }

        private Border BuildDropRow(SessionAnalyzer.Drop d)
        {
            string causeLabel = d.Cause switch
            {
                SessionAnalyzer.DropCause.CpuBound      => "CPU-bound",
                SessionAnalyzer.DropCause.GpuBound      => "GPU-bound",
                SessionAnalyzer.DropCause.ShaderCompile => "Compilation shader",
                SessionAnalyzer.DropCause.DisplaySync   => "Synchro affichage",
                _                                       => "Mixte",
            };
            // Couleur de cause = RÔLE de thème (lisible en clair ET en sombre, vivant au toggle) ;
            // le fond de la pilule reste un tint translucide fixe et discret.
            string causeRole = d.Cause switch
            {
                SessionAnalyzer.DropCause.CpuBound      => "ThWarn",
                SessionAnalyzer.DropCause.GpuBound      => "ThCrit",
                SessionAnalyzer.DropCause.ShaderCompile => "ThAccentIcon",
                _                                       => "ThTextDim",
            };
            string pillTint = d.Cause switch
            {
                SessionAnalyzer.DropCause.CpuBound      => "#22F5C24A",
                SessionAnalyzer.DropCause.GpuBound      => "#22E05555",
                SessionAnalyzer.DropCause.ShaderCompile => "#22315FA0",
                _                                       => "#22A0A0A0",
            };
            int fps = (int)Math.Round(1000.0 / d.FrameTimeMs);
            string evidence = d.CulpritProcess != null
                ? $"coïncide avec un spike de {d.CulpritProcess} ({d.CulpritFrametimeMs:0.#} ms)"
                : $"CPU {d.CpuBusyMs:0.#} ms — GPU {d.GpuBusyMs:0.#} ms";

            var border = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 8, 0, 8),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "ThBorder");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var txTime = new TextBlock
            {
                Text = $"{d.TimeMs / 1000:0.#} s",
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            txTime.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            Grid.SetColumn(txTime, 0);
            var txFt = new TextBlock
            {
                Text = $"{d.FrameTimeMs:0.#} ms  ({fps} fps)",
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            txFt.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
            Grid.SetColumn(txFt, 1);
            var pillTxt = new TextBlock
            {
                Text = causeLabel,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            };
            pillTxt.SetResourceReference(TextBlock.ForegroundProperty, causeRole);
            var pill = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(pillTint)!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 1, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = pillTxt,
            };
            pill.SetResourceReference(Border.BorderBrushProperty, causeRole);
            Grid.SetColumn(pill, 2);
            var txEv = new TextBlock
            {
                Text = evidence,
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            txEv.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
            Grid.SetColumn(txEv, 3);
            grid.Children.Add(txTime); grid.Children.Add(txFt); grid.Children.Add(pill); grid.Children.Add(txEv);
            border.Child = grid;
            return border;
        }

        // ───────────────────────── historique ─────────────────────────
        private void RefreshHistory()
        {
            var list = SessionStore.Load().OrderByDescending(r => r.CapturedAtUtc).ToList();
            HistoryTile.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryPanel.Children.Clear();
            foreach (var r in list)
            {
                var b = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 8, 0, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = r,
                };
                b.SetResourceReference(Border.BorderBrushProperty, "ThBorder");
                b.MouseLeftButtonUp += (_, _) => ShowReport((SessionAnalyzer.Report)b.Tag);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var sc = new TextBlock
                {
                    Text = r.Score.ToString(),
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 20, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                sc.SetResourceReference(TextBlock.ForegroundProperty, ScoreRole(r.Score));
                Grid.SetColumn(sc, 0);
                var when = new TextBlock
                {
                    Text = r.CapturedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                when.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                Grid.SetColumn(when, 1);
                var info = new TextBlock
                {
                    Text = $"{r.GameDisplay} — {(int)Math.Round(r.FpsAvg)} fps moy / pire {(r.FrametimeMaxMs > 0 ? (int)Math.Round(1000.0 / r.FrametimeMaxMs) : 0)} fps — {r.Drops.Count} drops",
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };
                info.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
                Grid.SetColumn(info, 2);
                grid.Children.Add(sc); grid.Children.Add(when); grid.Children.Add(info);
                b.Child = grid;
                HistoryPanel.Children.Add(b);
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Effacer tout l'historique des sessions ?",
                                    "Tweakly", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK)
            {
                SessionStore.Clear();
                RefreshHistory();
            }
        }

        // ───────────────────────── couleurs score ─────────────────────────
        private static string ScoreRole(int score) => score >= 80 ? "ThOk" : (score >= 50 ? "ThWarn" : "ThCrit");

        // ───────────────────────── graphe frametime ─────────────────────────
        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

        private void RenderChart()
        {
            ChartCanvas.Children.Clear();
            var r = _currentReport;
            if (r == null || r.Chart.Count < 2) return;
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w < 50 || h < 50) return;

            // AXE EN FPS (intuitif : haut = fluide, un drop PLONGE vers le bas comme
            // sur tout compteur FPS). Le downsample garde le MAX frametime par bucket
            // = le MIN fps = la pire dip de l'intervalle, donc les drops ressortent.
            double tEnd = r.Chart[^1].T;
            double medFps = r.FpsP50 > 0 ? r.FpsP50 : 60;
            double fpsTop = medFps * 1.08;                 // un peu au-dessus du médian
            double fpsFloor = 0;                            // 0 en bas = lecture directe
            double leftPad = 38;                            // marge pour les labels FPS
            double topPad = 10, botPad = 10;                // la courbe ne touche pas les bords
            double plotW = w - leftPad;
            double plotH = h - topPad - botPad;

            double YfromFps(double fps) => topPad + (fpsTop - Math.Clamp(fps, fpsFloor, fpsTop)) / (fpsTop - fpsFloor) * plotH;
            double XfromT(double t) => leftPad + t / Math.Max(1e-6, tEnd) * plotW;

            // Courbe = bleu signature (lisible sur les deux thèmes) ; grille/seuil/points =
            // rôles de thème via SetResourceReference (jaune→amber foncé en clair, vivant au toggle).
            var blue = (Brush)new BrushConverter().ConvertFromString("#5BA0FF")!;
            var font = (FontFamily)FindResource("AppFont");

            // (1) Graduations FPS : valeurs « rondes » dédupliquées + espacées (≥ 18 px)
            //     pour éviter le chevauchement (bug 343/318 vu en réel).
            int step = medFps >= 200 ? 100 : medFps >= 100 ? 50 : 20;
            var tickVals = new List<int>();
            for (int v = step; v < fpsTop; v += step) tickVals.Add(v);
            tickVals.Add((int)Math.Round(medFps));          // toujours le médian
            double lastY = double.MinValue;
            foreach (int fps in tickVals.Distinct().OrderBy(v => v))
            {
                if (fps <= fpsFloor || fps >= fpsTop) continue;
                double y = YfromFps(fps);
                if (Math.Abs(y - lastY) < 18) continue;      // anti-chevauchement vertical
                lastY = y;
                var gl = new System.Windows.Shapes.Line { X1 = leftPad, X2 = w, Y1 = y, Y2 = y,
                    StrokeThickness = 0.5, StrokeDashArray = new DoubleCollection { 2, 4 }, Opacity = 0.28 };
                gl.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThTextDim");
                ChartCanvas.Children.Add(gl);
                var lbl = new TextBlock { Text = $"{fps}", FontFamily = font, FontSize = 9, Opacity = 0.7 };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, Math.Clamp(y - 7, 0, h - 12));
                ChartCanvas.Children.Add(lbl);
            }

            // (2) Ligne seuil COMPÉTITIF (label à DROITE pour ne pas chevaucher l'axe gauche)
            if (r.CompetitiveFps > fpsFloor && r.CompetitiveFps < fpsTop)
            {
                double y = YfromFps(r.CompetitiveFps);
                var tl = new System.Windows.Shapes.Line { X1 = leftPad, X2 = w, Y1 = y, Y2 = y,
                    StrokeThickness = 0.9, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.6 };
                tl.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThWarn");
                ChartCanvas.Children.Add(tl);
                var lbl = new TextBlock { Text = $"seuil {r.CompetitiveFps} fps", FontFamily = font, FontSize = 9.5, Opacity = 0.9 };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(lbl, w - lbl.DesiredSize.Width - 6); Canvas.SetTop(lbl, Math.Clamp(y - 13, 0, h - 14));
                ChartCanvas.Children.Add(lbl);
            }

            // (3) Courbe FPS
            var pts = new PointCollection();
            foreach (var p in r.Chart)
            {
                double fps = p.Ft > 0 ? 1000.0 / p.Ft : fpsTop;
                pts.Add(new Point(XfromT(p.T), YfromFps(fps)));
            }
            ChartCanvas.Children.Add(new System.Windows.Shapes.Polyline
            {
                Stroke = blue, StrokeThickness = 1.1, StrokeLineJoin = PenLineJoin.Round, Points = pts,
            });

            // (4) Annoter les 5 PIRES drops avec leur valeur fps + pastille couleur de cause
            double t0 = r.Chart[0].T;
            var worst = r.Drops
                .Where(d => d.Cause != SessionAnalyzer.DropCause.ShaderCompile)
                .OrderByDescending(d => d.FrameTimeMs).Take(5).ToList();
            foreach (var d in worst)
            {
                double tDrop = d.TimeMs / 1000.0 - t0;
                if (tDrop < 0 || tDrop > tEnd) continue;
                double fps = 1000.0 / d.FrameTimeMs;
                double x = XfromT(tDrop), y = YfromFps(fps);
                string markerRole = d.Cause == SessionAnalyzer.DropCause.GpuBound ? "ThCrit" : "ThWarn";
                var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8 };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, markerRole);
                Canvas.SetLeft(dot, x - 4); Canvas.SetTop(dot, y - 4);
                ChartCanvas.Children.Add(dot);
                var lbl = new TextBlock { Text = $"{fps:0}", FontFamily = font, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, markerRole);
                Canvas.SetLeft(lbl, Math.Clamp(x - 8, leftPad, w - 24)); Canvas.SetTop(lbl, Math.Clamp(y + 6, 0, h - 14));
                ChartCanvas.Children.Add(lbl);
            }

            TxtChartLegend.Text = "courbe = fps dans le temps · jaune = drop CPU · rouge = drop GPU · pointillé orange = seuil";
            TxtChartStart.Text = "0 s";
            TxtChartEnd.Text = $"{tEnd:0} s";
        }

        private static Button? FindNavButton(DependencyObject root, string tag)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Button b && b.Tag is string s && s == tag) return b;
                var found = FindNavButton(child, tag);
                if (found != null) return found;
            }
            return null;
        }
    }
}
