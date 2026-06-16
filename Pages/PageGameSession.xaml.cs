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

        // Géométrie du dernier rendu de graphe — réutilisée par le survol (règle + valeurs)
        // pour mapper un X souris → temps, sans recalculer ni redessiner la courbe.
        private double _chLeft, _chPlotW, _chTEnd, _chTop, _chPlotH, _chW;
        private bool _chReady;
        // Drops effectivement dessinés (après anti-chevauchement) — lus par le survol pour
        // que la règle posée sur une pastille affiche la VALEUR DU DROP (et non la médiane).
        private readonly List<(double x, double fps, string role)> _chDrops = new();

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
            // Annule un compte à rebours en cours (l'utilisateur a quitté la page).
            if (_countdown != null) { _countdown.Stop(); _countdown = null; }
            if (_rec.IsRecording)
            {
                _tick?.Stop(); _tick = null;
                _rec.Abort();
                RecOverlay.Visibility = Visibility.Collapsed;
                AppLog.Write("PageGameSession : capture abandonnée (page quittée).");
            }
        }

        // ───────────────────────── démarrage / arrêt ─────────────────────────

        private DispatcherTimer? _countdown;       // retardateur avant démarrage
        private int _autoStopSec;                  // durée auto-stop (0 = manuel)

        private static int ComboSec(ComboBox c)
        {
            if (c.SelectedItem is ComboBoxItem cbi && int.TryParse(cbi.Tag?.ToString(), out int s)) return s;
            return 0;
        }

        private void BtnRec_Click(object sender, RoutedEventArgs e)
        {
            if (_rec.IsRecording) return;
            // Re-clic pendant le compte à rebours = ANNULATION (utile si on a cliqué par erreur).
            if (_countdown != null)
            {
                _countdown.Stop(); _countdown = null;
                BtnRec.Content = "ENREGISTRER UNE SESSION";
                TxtStatus.Text = "Enregistre une session pour mesurer les FPS, les chutes et leur origine.";
                return;
            }

            int delay = ComboSec(CmbDelay);
            _autoStopSec = ComboSec(CmbDuration);

            if (delay <= 0) { StartCaptureNow(); return; }

            // Compte à rebours visible dans le titre du bouton + statut au-dessus, sans bloquer
            // l'UI. Permet d'alt-tab vers le jeu avant la capture (PresentMon ne mesure que ce
            // qui est présenté à l'écran). Annulable d'un nouveau clic sur le bouton.
            BtnRec.Content = $"DÉMARRE DANS {delay}…";
            TxtStatus.Text = "Bascule dans le jeu maintenant. La capture démarre automatiquement.";
            int remaining = delay;
            _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdown.Tick += (_, _) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    _countdown?.Stop(); _countdown = null;
                    StartCaptureNow();
                    return;
                }
                BtnRec.Content = $"DÉMARRE DANS {remaining}…";
            };
            _countdown.Start();
        }

        private void StartCaptureNow()
        {
            if (!_rec.Start(null, out string error))
            {
                BtnRec.Content = "ENREGISTRER UNE SESSION";
                TxtStatus.Text = "Enregistre une session pour mesurer les FPS, les chutes et leur origine.";
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
            _tick.Tick += async (_, _) =>
            {
                var d = DateTime.UtcNow - _recStart;
                TxtRecElapsed.Text = $"{(int)d.TotalMinutes:D2}:{d.Seconds:D2}";
                UpdateLiveTiles();
                // Auto-stop à la durée choisie (si l'utilisateur n'a pas pris « Manuel »).
                if (_autoStopSec > 0 && d.TotalSeconds >= _autoStopSec)
                {
                    _tick?.Stop(); _tick = null;
                    await StopAndAnalyzeAsync();
                }
            };
            _tick.Start();
            AppLog.Write($"PageGameSession : capture démarrée (auto-stop = {_autoStopSec}s).");
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

        private async void BtnStopRec_Click(object sender, RoutedEventArgs e) => await StopAndAnalyzeAsync();

        /// <summary>
        /// Arrêt + analyse + persistance. Appelé par le bouton ARRÊTER ET ANALYSER OU par
        /// l'auto-stop (durée atteinte). Idempotent : un 2e appel pendant l'analyse ne fait rien.
        /// </summary>
        private async Task StopAndAnalyzeAsync()
        {
            if (!_rec.IsRecording) return;
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
                BtnRec.Content = "ENREGISTRER UNE SESSION";
                TxtStatus.Text = "Enregistre une session pour mesurer les FPS, les chutes et leur origine.";
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
            // Chips de télémétrie : seulement si la session porte des samples (pas le vieil
            // historique, dont les samples ne sont pas persistés).
            TeleChips.Visibility = (r.Samples != null && r.Samples.Count >= 2)
                ? Visibility.Visible : Visibility.Collapsed;
            RenderChart();

            TxtScore.Text = r.Score.ToString();
            string scoreRole = ScoreRole(r.Score);
            TxtScore.SetResourceReference(TextBlock.ForegroundProperty, scoreRole);
            TxtGameName.Text = r.GameDisplay;
            GameKnownBadge.Visibility = r.GameKnown ? Visibility.Visible : Visibility.Collapsed;
            // Pastille de grade (un mot, couleur = niveau du score) + pastille ronde devant la
            // ligne de verdict : le jugement d'un coup d'œil, le détail dans les tuiles.
            TxtGrade.Text = GradeWord(r.Score);
            TxtGrade.SetResourceReference(TextBlock.ForegroundProperty, scoreRole);
            GradeBadge.SetResourceReference(Border.BorderBrushProperty, scoreRole);
            GradeBadge.Visibility = Visibility.Visible;
            VerdictDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, scoreRole);
            TxtVerdict.Text = r.Verdict;

            TxtFpsAvg.Text = ((int)Math.Round(r.FpsAvg)).ToString();
            TxtDuration.Text = $"médian {r.FpsP50:0} fps · {r.FrameCount} frames / {r.DurationSec:0} s";
            // RÉGULARITÉ : CV% + verdict + bande perçue. Couleur selon la constance.
            TxtRegularity.Text = $"CV {r.FrametimeCvPct:0} %";
            string regWord = r.FrametimeCvPct <= 8 ? "très régulier"
                           : r.FrametimeCvPct <= 14 ? "assez régulier"
                           : r.FrametimeCvPct <= 22 ? "irrégulier" : "très irrégulier";
            TxtRegularity.SetResourceReference(TextBlock.ForegroundProperty,
                r.FrametimeCvPct <= 8 ? "ThOk" : r.FrametimeCvPct <= 14 ? "ThWarn" : "ThCrit");
            TxtRegularityHint.Text = $"{regWord} · varie de {r.PerceivedFpsLow:0} à {r.PerceivedFpsHigh:0} fps";
            // PIRE FRAME : fps min réellement atteint (hors bordures déjà trimées) = ce que
            // l'utilisateur voit chuter en jeu, ≠ 0,1 % low qui est un seuil percentile.
            int worstFps = r.FrametimeMaxMs > 0 ? (int)Math.Round(1000.0 / r.FrametimeMaxMs) : 0;
            TxtWorstFps.Text = worstFps.ToString();
            TxtWorstFps.SetResourceReference(TextBlock.ForegroundProperty,
                worstFps < r.CompetitiveFps * 0.5 ? "ThCrit" : worstFps < r.CompetitiveFps ? "ThWarn" : "ThOk");
            int worstCount = r.Drops.Count(d => d.Cause != SessionAnalyzer.DropCause.ShaderCompile
                                             && 1000.0 / d.FrameTimeMs < r.CompetitiveFps);
            TxtWorstHint.Text = worstCount > 0 ? $"{worstCount} frames sous {r.CompetitiveFps} fps" : "le plus bas atteint en jeu";
            // Présentation : on affiche un libellé EN CLAIR (le terme brut PresentMon reste en
            // infobulle pour qui veut). Voir PresentModeFr.
            TxtMode.Text = PresentModeFr(r.PresentMode);
            TxtMode.ToolTip = string.IsNullOrEmpty(r.PresentMode) ? null : r.PresentMode;
            if (r.PresentModeOptimal)
            {
                TxtModeHint.Text = "chemin direct au GPU, aucun compositeur";
                TxtMode.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
            }
            else if (!string.IsNullOrEmpty(r.PresentMode))
            {
                TxtModeHint.Text = "composé par Windows — léger surcoût de latence";
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
            // Fond translucide via RÔLE de thème (les tints suivent le mode clair/sombre).
            string tintRole = reco.Severity switch
            {
                SessionAnalyzer.RecoSeverity.Crit => "ThCritTint",
                SessionAnalyzer.RecoSeverity.Warn => "ThWarnTint",
                _                                 => "ThInfoTint",
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
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
            };
            border.SetResourceReference(Border.BackgroundProperty, tintRole);
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
            string pillTintRole = d.Cause switch
            {
                SessionAnalyzer.DropCause.CpuBound      => "ThWarnTint",
                SessionAnalyzer.DropCause.GpuBound      => "ThCritTint",
                SessionAnalyzer.DropCause.ShaderCompile => "ThInfoTint",
                _                                       => "ThNeutralTint",
            };
            int fps = (int)Math.Round(1000.0 / d.FrameTimeMs);
            string evidence = $"CPU {d.CpuBusyMs:0.#} ms — GPU {d.GpuBusyMs:0.#} ms";

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
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 1, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = pillTxt,
            };
            pill.SetResourceReference(Border.BackgroundProperty, pillTintRole);
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
                // Masquer aussi le rapport en cours d'affichage : laisser un résultat à l'écran
                // alors que l'historique vient d'être vidé est incohérent.
                _currentReport = null;
                VerdictTile.Visibility = Visibility.Collapsed;
                StatsRow.Visibility = Visibility.Collapsed;
                ChartTile.Visibility = Visibility.Collapsed;
                RecoTile.Visibility = Visibility.Collapsed;
                DropsTile.Visibility = Visibility.Collapsed;
                TeleChips.Visibility = Visibility.Collapsed;
                ChartCanvas.Children.Clear();
                ScrubCanvas.Children.Clear();
                _chReady = false;
                _chDrops.Clear();
            }
        }

        // ───────────────────────── couleurs score ─────────────────────────
        private static string ScoreRole(int score) => score >= 80 ? "ThOk" : (score >= 50 ? "ThWarn" : "ThCrit");

        // Mot de grade affiché dans la pastille (même barème que la couleur du score).
        private static string GradeWord(int score) => score >= 80 ? "stable" : (score >= 50 ? "moyen" : "instable");

        // Traduit le PresentMode brut de PresentMon en libellé clair (le terme technique
        // reste en infobulle). « Independent Flip » = chemin direct GPU = optimal ;
        // « Legacy Flip » = plein écran exclusif ; « Composed: … » = passé par le compositeur.
        private static string PresentModeFr(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "—";
            if (mode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)) return "Plein écran direct";
            if (mode.Contains("Legacy Flip", StringComparison.OrdinalIgnoreCase)) return "Plein écran exclusif";
            if (mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase)) return "Fenêtré";
            return mode;
        }

        // ───────────────────────── graphe frametime ─────────────────────────
        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

        // Toggle d'une courbe de télémétrie superposée → on redessine.
        private void TeleChip_Changed(object sender, RoutedEventArgs e) => RenderChart();

        // ── Survol : règle verticale + valeurs exactes à l'instant pointé ──────────
        // Lit TOUTES les métriques (FPS + télémétrie) quel que soit l'état des chips :
        // plus besoin de cocher pour comparer des chiffres réels.
        private void ScrubCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
            => ScrubCanvas.Children.Clear();

        private void ScrubCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ScrubCanvas.Children.Clear();
            var r = _currentReport;
            if (!_chReady || r == null || r.Chart.Count < 2 || _chPlotW <= 0) return;

            double x = Math.Clamp(e.GetPosition(ScrubCanvas).X, _chLeft, _chW);
            double t = (x - _chLeft) / _chPlotW * _chTEnd;   // secondes sur l'axe du graphe

            // Règle verticale
            var rule = new System.Windows.Shapes.Line
            { X1 = x, X2 = x, Y1 = _chTop, Y2 = _chTop + _chPlotH, StrokeThickness = 1, Opacity = 0.75 };
            rule.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThTextDim");
            ScrubCanvas.Children.Add(rule);

            // FPS lu : si la souris est sur (ou très près de) une pastille de drop, on affiche
            // la VRAIE valeur du drop (et un libellé « DROP »). Sinon = médiane par paquet.
            // Le bug majeur d'avant : un drop à 71 lisait la médiane (~315) → mensonge.
            double fps = double.NaN; bool isDrop = false; string dropRole = "";
            const double DropPickPx = 8;
            foreach (var dr in _chDrops)
            {
                if (Math.Abs(dr.x - x) <= DropPickPx) { fps = dr.fps; isDrop = true; dropRole = dr.role; break; }
            }
            if (!isDrop)
            {
                double bd = double.MaxValue;
                foreach (var cp in r.Chart)
                {
                    double d = Math.Abs(cp.T - t);
                    if (d < bd) { bd = d; fps = cp.Ft > 0 ? 1000.0 / cp.Ft : double.NaN; }
                }
            }
            // Échantillon télémétrie au plus proche (aligné via ChartStartMs)
            Helpers.SysSample? s = null; double bs = double.MaxValue;
            foreach (var sm in r.Samples)
            {
                double st = (sm.ElapsedMs - r.ChartStartMs) / 1000.0;
                double d = Math.Abs(st - t);
                if (d < bs) { bs = d; s = sm; }
            }

            // Panneau de valeurs
            var panel = new StackPanel();
            void Line(string label, string val, string role, bool blue = false)
            {
                var tb = new TextBlock
                {
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 11.5, Margin = new Thickness(0, 1, 0, 1),
                };
                var lbl = new System.Windows.Documents.Run(label + "  ");
                lbl.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, "ThTextDim");
                tb.Inlines.Add(lbl);
                var v = new System.Windows.Documents.Run(val) { FontWeight = FontWeights.SemiBold };
                v.SetResourceReference(System.Windows.Documents.Run.ForegroundProperty, blue ? "ThChartLine" : role);
                tb.Inlines.Add(v);
                panel.Children.Add(tb);
            }
            var head = new TextBlock
            {
                Text = $"{t:0.0} s",
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 10.5, Margin = new Thickness(0, 0, 0, 3),
            };
            head.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            panel.Children.Add(head);

            if (isDrop)
                Line("DROP", double.IsNaN(fps) ? "—" : fps.ToString("0") + " fps", dropRole);
            else
                Line("FPS", double.IsNaN(fps) ? "—" : fps.ToString("0"), "", blue: true);
            if (s != null)
            {
                if (!double.IsNaN(s.GpuTempC))      Line("GPU temp",  s.GpuTempC.ToString("0") + " °C",                "ThPink");
                if (!double.IsNaN(s.GpuCoreMhz))    Line("GPU clock", s.GpuCoreMhz.ToString("0") + " MHz",             "ThCyan");
                if (!double.IsNaN(s.GpuVramUsedMB)) Line("VRAM",      (s.GpuVramUsedMB / 1024.0).ToString("0.0") + " Go", "ThViolet");
                if (!double.IsNaN(s.CpuLoadPct))    Line("CPU",       s.CpuLoadPct.ToString("0") + " %",               "ThOk");
            }

            var box = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 12, 8),
                Child = panel,
            };
            box.SetResourceReference(Border.BackgroundProperty, "ThBg");
            box.SetResourceReference(Border.BorderBrushProperty, "ThBorder");

            // Position : à droite du curseur si la place le permet, sinon à gauche.
            box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = box.DesiredSize.Width, bh = box.DesiredSize.Height;
            double bx = x + 12; if (bx + bw > _chW) bx = x - 12 - bw;
            bx = Math.Max(_chLeft, bx);
            double by = Math.Clamp(_chTop + 2, 0, Math.Max(0, _chTop + _chPlotH - bh));
            Canvas.SetLeft(box, bx); Canvas.SetTop(box, by);
            ScrubCanvas.Children.Add(box);
        }

        private void RenderChart()
        {
            ChartCanvas.Children.Clear();
            ScrubCanvas.Children.Clear();   // une règle de survol stale n'a plus de sens après un re-render
            _chReady = false;
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
            double leftPad = 78;                            // marge gauche élargie pour la PILULE seuil
                                                            // (la pilule ne déborde plus jamais dans le plot)
            double topPad = 10, botPad = 10;                // la courbe ne touche pas les bords
            double plotW = w - leftPad;
            double plotH = h - topPad - botPad;

            double YfromFps(double fps) => topPad + (fpsTop - Math.Clamp(fps, fpsFloor, fpsTop)) / (fpsTop - fpsFloor) * plotH;
            double XfromT(double t) => leftPad + t / Math.Max(1e-6, tEnd) * plotW;

            // Mémorise la géométrie pour le survol (mapping X souris → temps).
            _chLeft = leftPad; _chPlotW = plotW; _chTEnd = tEnd;
            _chTop = topPad; _chPlotH = plotH; _chW = w; _chReady = true;

            // Courbe = bleu signature (lisible sur les deux thèmes) ; grille/seuil/points =
            // rôles de thème via SetResourceReference (jaune→amber foncé en clair, vivant au toggle).
            // ⚠️ Plus de `FindResource("ThAccentIcon")` : il retourne l'OBJET brush au moment T →
            // si la page a été construite avant ThemeManager.Apply (ou que le thème change après),
            // on chope la valeur DÉFAUT du App.xaml (#8FC0FF = clair en sombre) → courbe pâle en
            // light. Toutes les couleurs thémées sont désormais attachées via SetResourceReference
            // (équivalent DynamicResource en code) → toujours bon brush, dans les deux modes.
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
                // Éviter le doublon avec la pilule « X seuil » (zone réservée ±14 px en Y autour du seuil).
                if (r.CompetitiveFps > 0 && Math.Abs(YfromFps(fps) - YfromFps(r.CompetitiveFps)) < 14) continue;
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

            // (2) Seuil compétitif : tracé en TOUTE FIN du rendu (après le plancher transparent
            // et la courbe principale) pour ne plus être recouvert. Voir bloc (5) ci-dessous.

            // (2.5) Télémétrie superposée (chips) : chaque série sur SA propre échelle (unités
            // différentes — °C, MHz, Mo, %), alignée sur l'axe temps via ElapsedMs − ChartStartMs.
            // Dessinée AVANT la courbe FPS → le FPS et les drops restent au premier plan.
            if (r.Samples != null && r.Samples.Count >= 2)
            {
                void DrawSeries(bool on, Func<Helpers.SysSample, double> sel, string role)
                {
                    if (!on) return;
                    var vals = new List<(double t, double v)>();
                    foreach (var s in r.Samples)
                    {
                        double v = sel(s);
                        if (double.IsNaN(v)) continue;
                        double t = (s.ElapsedMs - r.ChartStartMs) / 1000.0;
                        if (t < 0 || t > tEnd) continue;
                        vals.Add((t, v));
                    }
                    if (vals.Count < 2) return;
                    double vMin = vals.Min(p => p.v), vMax = vals.Max(p => p.v);
                    double range = vMax - vMin; if (range < 1e-6) range = 1;
                    // Bande réservée : ~6 % de marge haut/bas pour ne pas coller aux bords.
                    double bandTop = topPad + plotH * 0.06, bandH = plotH * 0.88;
                    double Yv(double v) => bandTop + (1 - (v - vMin) / range) * bandH;
                    var sp = new PointCollection();
                    foreach (var (t, v) in vals) sp.Add(new Point(XfromT(t), Yv(v)));
                    var pl = new System.Windows.Shapes.Polyline
                    { StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round, Points = sp, Opacity = 0.9 };
                    pl.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, role);
                    ChartCanvas.Children.Add(pl);
                }
                DrawSeries(ChkTemp.IsChecked  == true, s => s.GpuTempC,      "ThPink");
                DrawSeries(ChkClock.IsChecked == true, s => s.GpuCoreMhz,    "ThCyan");
                DrawSeries(ChkVram.IsChecked  == true, s => s.GpuVramUsedMB, "ThViolet");
                DrawSeries(ChkCpu.IsChecked   == true, s => s.CpuLoadPct,    "ThOk");
            }

            // (3a) Courbe « plancher » = pire frametime de chaque paquet → la VRAIE variance des
            // frames. En GRIS thémé (ThTextDim) pour ne PAS concurrencer la courbe vive en bleu
            // (sinon en light mode, plancher bleu translucide + courbe bleu foncé fine = flou
            // bleu pâle illisible : la courbe disparaissait derrière son propre brouillard).
            if (r.Chart.Any(p => p.FtFloor > 0))
            {
                var floor = new PointCollection();
                foreach (var p in r.Chart)
                {
                    double ftFloor = p.FtFloor > 0 ? p.FtFloor : p.Ft;
                    double fps = ftFloor > 0 ? 1000.0 / ftFloor : fpsTop;
                    floor.Add(new Point(XfromT(p.T), YfromFps(fps)));
                }
                var floorPl = new System.Windows.Shapes.Polyline
                {
                    StrokeThickness = 0.6, StrokeLineJoin = PenLineJoin.Round,
                    Points = floor, Opacity = 0.45,
                };
                floorPl.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThTextDim");
                ChartCanvas.Children.Add(floorPl);
            }

            // (3b) Courbe FPS médiane = ce que l'utilisateur joue (matche son compteur in-game)
            var pts = new PointCollection();
            foreach (var p in r.Chart)
            {
                double fps = p.Ft > 0 ? 1000.0 / p.Ft : fpsTop;
                pts.Add(new Point(XfromT(p.T), YfromFps(fps)));
            }
            var mainCurve = new System.Windows.Shapes.Polyline
            {
                StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Points = pts,
            };
            mainCurve.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThChartLine");
            ChartCanvas.Children.Add(mainCurve);

            // (4) Pastilles de drop : pires d'abord, FUSIONNÉES si trop proches en X (on garde
            // la pire = frametime max → fps min). Plus de pastilles empilées illisibles.
            // Sauvegardées pour que le survol affiche la VRAIE valeur du drop quand on est dessus,
            // au lieu de lire la médiane de la courbe (qui donnait 315 sur un point à 71).
            _chDrops.Clear();
            const double DotMergePx = 11;   // pastilles séparées d'au moins 11 px
            const double LblMergePx = 28;   // libellés chiffrés séparés d'au moins 28 px
            var worstAll = r.Drops
                .Where(d => d.Cause != SessionAnalyzer.DropCause.ShaderCompile)
                .OrderByDescending(d => d.FrameTimeMs).Take(20)
                .Select(d => new { d, t = (d.TimeMs - r.ChartOriginMs) / 1000.0 })
                .Where(z => z.t >= 0 && z.t <= tEnd)
                .Select(z => new { z.d, x = XfromT(z.t), ft = z.d.FrameTimeMs })
                .OrderByDescending(z => z.ft)   // commence par la PIRE → elle s'impose en cas de fusion
                .ToList();
            var kept = new List<(double x, double ft, SessionAnalyzer.DropCause cause)>();
            foreach (var z in worstAll)
            {
                if (kept.Any(k => Math.Abs(k.x - z.x) < DotMergePx)) continue;
                kept.Add((z.x, z.ft, z.d.Cause));
                if (kept.Count >= 8) break;
            }
            foreach (var k in kept.OrderBy(k => k.x))
            {
                double fps = 1000.0 / k.ft;
                double y = YfromFps(fps);
                string markerRole = k.cause == SessionAnalyzer.DropCause.GpuBound ? "ThCrit" : "ThWarn";
                var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9 };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, markerRole);
                Canvas.SetLeft(dot, k.x - 4.5); Canvas.SetTop(dot, y - 4.5);
                ChartCanvas.Children.Add(dot);
                _chDrops.Add((k.x, fps, markerRole));
            }
            // Libellés chiffrés : anti-chevauchement séparé (28 px), seulement si la pastille
            // n'est pas trop proche du voisin déjà labellé. Le survol couvre les autres.
            double lastLblX = double.MinValue;
            foreach (var k in kept.OrderBy(k => k.x))
            {
                if (k.x - lastLblX < LblMergePx) continue;
                lastLblX = k.x;
                double fps = 1000.0 / k.ft;
                double y = YfromFps(fps);
                string markerRole = k.cause == SessionAnalyzer.DropCause.GpuBound ? "ThCrit" : "ThWarn";
                var lbl = new TextBlock { Text = $"{fps:0}", FontFamily = font, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, markerRole);
                Canvas.SetLeft(lbl, Math.Clamp(k.x - 8, leftPad, w - 24));
                Canvas.SetTop(lbl, Math.Clamp(y + 7, 0, h - 14));
                ChartCanvas.Children.Add(lbl);
            }

            // (5) Seuil — tracé tout à la fin pour être AU-DESSUS du plancher et de la courbe.
            // Pilule dans la marge gauche élargie (leftPad = 78), garantie hors plot.
            if (r.CompetitiveFps > fpsFloor && r.CompetitiveFps < fpsTop)
            {
                double y = YfromFps(r.CompetitiveFps);
                var tl = new System.Windows.Shapes.Line
                {
                    X1 = leftPad, X2 = w, Y1 = y, Y2 = y,
                    StrokeThickness = 1.1, StrokeDashArray = new DoubleCollection { 5, 4 }, Opacity = 0.85,
                };
                tl.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ThWarn");
                ChartCanvas.Children.Add(tl);

                var lblPill = new TextBlock { Text = $"{r.CompetitiveFps} seuil", FontFamily = font, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 1, 7, 2) };
                lblPill.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                var pill = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = lblPill };
                pill.SetResourceReference(Border.BackgroundProperty, "ThBg");
                pill.SetResourceReference(Border.BorderBrushProperty, "ThWarn");
                pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // Cale la pilule sur la ligne, dans la marge (jamais dans le plot car
                // pill.Width ≤ leftPad-4 et leftPad = 78 ≫ taille de la pilule).
                double px = Math.Max(2, leftPad - pill.DesiredSize.Width - 2);
                Canvas.SetLeft(pill, px);
                Canvas.SetTop(pill, Math.Clamp(y - pill.DesiredSize.Height / 2, 0, h - pill.DesiredSize.Height));
                ChartCanvas.Children.Add(pill);
            }

            TxtChartLegend.Text = "courbe bleue = fps joué · trait gris = plancher des frames · jaune = drop CPU · rouge = drop GPU";
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
