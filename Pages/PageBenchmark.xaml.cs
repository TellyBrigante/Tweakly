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
            // Charge l'historique DÈS LA CONSTRUCTION (pas seulement au Loaded) : au démarrage
            // automatique avec Windows, la fenêtre est minimisée et le Loaded de la page peut ne
            // pas se déclencher tant qu'elle n'est pas affichée → l'historique restait vide.
            // try/catch (RÈGLE 3 anti-casse) : ça s'exécute dans le flux de démarrage.
            try { LoadAndRender(); } catch { }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) => LoadAndRender();

        // Charge l'historique stocké + (re)dessine tout. Idempotent (RefreshHistory vide le
        // panneau avant de le repeupler), donc appelable au ctor ET au Loaded sans doublon.
        private void LoadAndRender()
        {
            _history = BenchmarkStore.Load().OrderByDescending(x => x.Timestamp).ToList();
            if (_history.Count >= 2) { _selA = _history[1]; _selB = _history[0]; }
            RefreshHeader();
            RefreshSubScores();
            RefreshHistory();
            RefreshSparkline();
            RenderCompare();
            RefreshRebenchHint();
        }

        /// <summary>Boucle « mesurer → corriger → prouver » : propose un re-bench si des
        /// tweaks ont été appliqués depuis la dernière mesure (flag posé par TweakFeedback).</summary>
        private void RefreshRebenchHint()
        {
            try
            {
                TxtRebenchHint.Visibility =
                    Helpers.TweakFeedback.TweaksAppliedSinceBench && _history.Count > 0
                        ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
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

        /// <summary>Annule le bench en cours (les sondes observent le token et s'arrêtent
        /// en quelques secondes max — le temps de finir l'itération en cours).</summary>
        private void BtnCancelBench_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            BtnCancelBench.IsEnabled = false;
            BtnCancelBench.Content   = "Annulation…";
            TxtOverlayPhase.Text     = "Annulation en cours…";
        }

        private async void BtnGoConfirm_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            await RunBenchAsync();
        }

        private async Task RunBenchAsync()
        {
            BtnRun.IsEnabled = false;
            BenchOverlay.Visibility = Visibility.Visible;
            SetProgress(0); SetTotalRaw(0); TxtOverlayPhase.Text = "Préparation…";
            BtnCancelBench.IsEnabled = true; BtnCancelBench.Content = "ANNULER LE BENCHMARK";
            _main.Log("Benchmark : démarrage…");
            _cts = new CancellationTokenSource();

            var progress = new Progress<(Benchmark.Phase phase, double pct)>(p =>
            {
                TxtOverlayPhase.Text = p.phase switch
                {
                    // v1.3.0 : 7 sondes gaming-oriented x 3 runs (médiane)
                    Benchmark.Phase.CpuSingle => "CPU — single-thread (Mandelbrot) · 3 runs…",
                    Benchmark.Phase.CpuMulti  => "CPU — multi-thread (8 cœurs max) · 3 runs…",
                    Benchmark.Phase.CpuMem    => "CPU — accès mémoire (pointer-chase) · 3 runs…",
                    Benchmark.Phase.SysFrame  => "Système — stabilité 60 Hz (frame time) · 3 runs…",
                    Benchmark.Phase.SysInput  => "Système — latence d'entrée (jitter scheduler) · 3 runs…",
                    Benchmark.Phase.RamBand   => "RAM — bande passante (STREAM Copy+Triad) · 3 runs…",
                    Benchmark.Phase.RamLat    => "RAM — latence accès aléatoire · 3 runs…",
                    Benchmark.Phase.Network   => "Réseau — ping & jitter (1.1.1.1)…",
                    Benchmark.Phase.Done      => "Calcul du score…",
                    _ => "…"
                };
                SetProgress(p.pct);
                SetTotalProgress(p.phase, p.pct);
            });

            BenchmarkResult? r = null;
            try { r = await Benchmark.RunAsync(progress, _cts.Token); }
            catch (Exception ex) { _main.Log("Benchmark : erreur — " + ex.Message); }

            BenchOverlay.Visibility = Visibility.Collapsed;
            BtnRun.IsEnabled = true;
            if (r == null) return;

            // Annulé en cours de route : les sondes ont cassé leurs boucles → les mesures
            // sont PARTIELLES et le score n'a aucun sens. On jette, l'historique reste sain.
            if (_cts?.IsCancellationRequested == true)
            {
                _main.Log("Benchmark : annulé par l'utilisateur — résultat partiel ignoré.");
                return;
            }

            BenchmarkStore.Append(r);
            _history = BenchmarkStore.Load().OrderByDescending(x => x.Timestamp).ToList();
            if (_history.Count >= 2) { _selA = _history[1]; _selB = _history[0]; }
            RefreshHeader();
            RefreshSubScores();
            RefreshHistory();
            RefreshSparkline();
            RenderCompare();
            Helpers.TweakFeedback.TweaksAppliedSinceBench = false;   // mesure faite → boucle bouclée
            RefreshRebenchHint();
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

        // ── Barre TOTALE (v1.3.5) : 8 sondes pondérées également — un cran franchi
        //    par étape + la progression interne de l'étape en cours ─────────────
        private static int PhaseIndex(Benchmark.Phase ph) => ph switch
        {
            Benchmark.Phase.CpuSingle => 0,
            Benchmark.Phase.CpuMulti  => 1,
            Benchmark.Phase.CpuMem    => 2,
            Benchmark.Phase.SysFrame  => 3,
            Benchmark.Phase.SysInput  => 4,
            Benchmark.Phase.RamBand   => 5,
            Benchmark.Phase.RamLat    => 6,
            Benchmark.Phase.Network   => 7,
            _                         => 8,   // Done
        };

        private void SetTotalProgress(Benchmark.Phase phase, double phasePct)
        {
            double total = phase == Benchmark.Phase.Done
                ? 100
                : (PhaseIndex(phase) * 100.0 + Math.Max(0, Math.Min(100, phasePct))) / 8.0;
            SetTotalRaw(total);
        }

        private void SetTotalRaw(double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            TotalBar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            TotalBar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            TxtOverlayTotalPct.Text = $"{pct:F0} %";
        }

        // ── BLOC 1 : hero compact (grand chiffre + verdict). Refonte v1.3.2 :
        // le thermomètre 0-150 a été RETIRÉ — le classement type Cinebench montre la
        // position bien mieux (SetScoreBar/ScoreMarker supprimés avec lui).

        private void RefreshHeader()
        {
            if (_history.Count == 0)
            {
                TxtScore.Text     = "—";
                TxtVerdict.Text   = "Lance ton premier benchmark pour mesurer ta machine.";
                TxtLastWhen.Text  = "";
                return;
            }
            var last = _history[0];
            TxtScore.Text    = last.TotalScore.ToString();
            TxtVerdict.Text  = ScoreVerdict(last.TotalScore);
            TxtLastWhen.Text = $"dernière mesure : {last.Timestamp:dd/MM/yyyy HH:mm}";

#if DEBUG
            // Garde-fou anti-confusion : les pivots du bench sont calibrés sur le JIT
            // RELEASE (~2,9× plus rapide sur Mandelbrot). En Debug les scores CPU/RAM
            // s'effondrent mécaniquement (~36 au lieu de ~100) — on l'affiche pour ne
            // plus jamais croire à un bug de calibration en testant le build de dev.
            TxtVerdict.Text  = "⚠ BUILD DEBUG — scores non représentatifs (pivots calibrés Release)";
#endif
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            RefreshSparkline();
        }

        private static string ScoreVerdict(int s) => s switch
        {
            >= 110 => "au-dessus de la moyenne",
            >= 95  => "dans la norme",
            >= 80  => "un peu en dessous de la moyenne",
            >= 60  => "en dessous de la moyenne — quelque chose le freine",
            _      => "très en dessous — mesure parasitée, ou problème système réel"
        };

        /// <summary>
        /// Verdict en français clair pour une carte de sous-score (CPU, Système, RAM).
        /// Multi-ligne : ligne 1 = état général, ligne 2 = piste d'action.
        /// </summary>
        // v1.3.5 : verdicts réécrits en français HUMAIN (retour utilisateur : « réduis
        // l'autostart, ça veut dire quoi ? ») — on dit ce que le score MESURE, ce que ça
        // CHANGE concrètement, et pour le Système les causes réelles sont CONSTATÉES sur
        // la machine juste en dessous (BenchAdvisor) avec un bouton par correction.
        private static string Verdict(int score, string axis) => (score, axis) switch
        {
            ( >= 105, _      ) => "Au-dessus de la moyenne pour ce modèle.\nRien à corriger côté performance.",
            ( >=  90, _      ) => "Dans la norme du modèle.\nRien à corriger.",
            ( >=  75, "CPU"      ) => "Un peu en dessous de ce que ce processeur sait faire.\nCause la plus fréquente : il chauffe trop et se bride, ou le plan d'alimentation le freine.",
            ( >=  75, "Système"  ) => "Windows répond avec un léger retard.\nConcrètement : en jeu, ça peut se sentir comme des micro-saccades. Les causes trouvées sur TA machine sont listées ci-dessous.",
            ( >=  75, _          ) => "Légèrement en dessous.\nCompare avec ton historique pour voir si c'est nouveau.",
            ( >=  60, "CPU"      ) => "Nettement en dessous de sa performance attendue.\nRegarde sa température dans Monitoring : au-delà de ~90 °C il se bride tout seul.",
            ( >=  60, "Système"  ) => "Windows met trop de temps à réagir.\nConcrètement : saccades probables en jeu et lenteurs à l'usage. Corrige les causes listées ci-dessous, puis relance le bench.",
            ( >=  60, _          ) => "Nettement en dessous.\nQuelque chose t'empêche de rendre la performance attendue.",
            (    _,    "CPU"     ) => "Très en dessous — anormal.\nSoit le CPU surchauffe fortement, soit un programme tournait pendant la mesure : relance le bench, seul.",
            (    _,    "Système" ) => "Windows répond très mal.\nLe système est si irrégulier que tout paraît lent. Corrige les causes ci-dessous puis relance le bench.",
            (    _,    _         ) => "Très en dessous — résultat anormal.\nRelance le bench sans rien d'autre d'ouvert."
        };

        // ── Conseiller Système (v1.3.5) : causes CONSTATÉES + bouton par action ──
        // Score Système < 90 → BenchAdvisor vérifie les causes connues de micro-saccades
        // sur LA machine (plan d'alim, HVCI, Power Throttling, SystemResponsiveness,
        // Game DVR, programmes au démarrage NOMMÉS) et on affiche chaque constat avec
        // un bouton qui mène directement au réglage qui corrige.
        private async void RefreshSysAdvice(int sysScore)
        {
            try
            {
                SysAdvicePanel.Children.Clear();
                if (sysScore >= 90) return;

                var finds = await System.Threading.Tasks.Task.Run(Helpers.BenchAdvisor.Analyze);

                if (finds.Count == 0)
                {
                    var none = new TextBlock
                    {
                        Text = "Aucune cause logicielle évidente trouvée sur ta machine — un programme "
                             + "tournait peut-être pendant la mesure. Relance le bench sans rien d'autre d'ouvert.",
                        TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
                    };
                    none.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                    SysAdvicePanel.Children.Add(none);
                    return;
                }

                foreach (var f in finds)
                {
                    var row = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background   = new SolidColorBrush(Color.FromArgb(0x0A, 0x80, 0x80, 0x80)),
                        BorderBrush  = new SolidColorBrush(Color.FromArgb(0x1A, 0x80, 0x80, 0x80)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 9, 12, 9),
                        Margin  = new Thickness(0, 0, 0, 6),
                    };
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var txt = new TextBlock
                    {
                        Text = f.Text, TextWrapping = TextWrapping.Wrap,
                        FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 12, 0),
                    };
                    txt.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
                    g.Children.Add(txt);

                    var btn = new Button
                    {
                        Content = f.ActionLabel, Tag = f,
                        Style = (Style)FindResource("SecondaryBtnStyle"),
                        Padding = new Thickness(10, 6, 10, 6), FontSize = 10.5,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(btn, 1);
                    btn.Click += AdviceAction_Click;
                    g.Children.Add(btn);

                    row.Child = g;
                    SysAdvicePanel.Children.Add(row);
                }
            }
            catch { /* le conseiller ne doit jamais casser l'affichage du bench */ }
        }

        private void AdviceAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not Helpers.BenchAdvisor.Finding f) return;
            try
            {
                if (f.Uri.Length > 0)
                {
                    using var _ = System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(f.Uri) { UseShellExecute = true });
                    return;
                }
                // Navigation interne : bouton de nav cherché par Tag (même mécanisme que PageEventLog)
                var btn = FindNavButton(_main, f.NavTag);
                if (btn != null) _main.NavigateTo(btn);
            }
            catch { }
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

        /// <summary>Verdict court pour la RAM (affiché compact à côté du CPU).</summary>
        private static string ShortRam(int score) => score switch
        {
            >= 100 => "bande passante excellente",
            >=  85 => "bande passante correcte",
            >=  70 => "RAM un peu lente (XMP/EXPO activé ?)",
            _      => "RAM faible — vérifie dual channel, XMP",
        };

        // ── BLOC 3 : 4 pills compactes (CPU, Système, RAM, Réseau) ────────────
        // Refonte v1.3.2 : les 3 grosses cartes verbeuses sont devenues des pills
        // (score + mini-barre). Le détail (verdict, mesures) s'affiche au CLIC dans
        // la zone DetailZone partagée — voir Pill*_Click.

        private void RefreshSubScores()
        {
            if (_history.Count == 0)
            {
                TxtCpuScore.Text = TxtSysScore.Text = TxtRamScore.Text = TxtNetScore.Text = "—";
                TxtCpuRefModel.Text = "—";
                TxtCpuMeasured.Text = TxtSysMeasured.Text = TxtRamDetail.Text = TxtNetMeasured.Text = "—";
                CpuCol.Width = SysCol.Width = RamCol.Width = NetCol.Width = new GridLength(0, GridUnitType.Star);
                CpuColRest.Width = new GridLength(150, GridUnitType.Star);
                SysColRest.Width = RamColRest.Width = NetColRest.Width = new GridLength(100, GridUnitType.Star);
                _openDetail = null;
                DetailZone.Visibility = Visibility.Collapsed;
                return;
            }
            var r = _history[0];

            // ── CPU (échelle 0-150) ─────────────────────────────────────────
            TxtCpuScore.Text = r.CpuScore.ToString();
            double cpuT = Math.Max(0, Math.Min(150, r.CpuScore));
            CpuCol.Width     = new GridLength(cpuT,         GridUnitType.Star);
            CpuColRest.Width = new GridLength(150.0 - cpuT, GridUnitType.Star);
            TxtCpuRefModel.Text = r.HasNominalRef
                ? $"Référence : {r.NominalRefModel}"
                  + (string.IsNullOrEmpty(r.CpuTier) ? "" : $"  ·  Tier : {r.CpuTier}")
                : "CPU non répertorié — score relatif à la 1re mesure (= 100)";
            TxtCpuMeasured.Text = Verdict(r.CpuScore, "CPU");

            // ── Système (échelle 0-100) ────────────────────────────────────
            TxtSysScore.Text = r.SysScore.ToString();
            double sysT = Math.Max(0, Math.Min(100, r.SysScore));
            SysCol.Width     = new GridLength(sysT,         GridUnitType.Star);
            SysColRest.Width = new GridLength(100.0 - sysT, GridUnitType.Star);
            TxtSysMeasured.Text = Verdict(r.SysScore, "Système");
            RefreshSysAdvice(r.SysScore);

            // ── RAM (échelle 0-100) ────────────────────────────────────────
            TxtRamScore.Text = r.RamScore.ToString();
            double ramT = Math.Max(0, Math.Min(100, r.RamScore));
            RamCol.Width     = new GridLength(ramT,         GridUnitType.Star);
            RamColRest.Width = new GridLength(100.0 - ramT, GridUnitType.Star);
            // Affichage façon AIDA64 : Read / Write / Copy + latence. Multi-thread AVX2.
            TxtRamDetail.Text = $"{ShortRam(r.RamScore)}\n"
                              + $"Lecture {r.RamReadGBs:F0} GB/s · Écriture {r.RamWriteGBs:F0} GB/s · Copie {r.RamCopyGBs:F0} GB/s · latence {r.RamLatencyNs:F0} ns";

            // ── Réseau (échelle 0-100) ─────────────────────────────────────
            TxtNetScore.Text = r.NetScore.ToString();
            double netT = Math.Max(0, Math.Min(100, r.NetScore));
            NetCol.Width     = new GridLength(netT,         GridUnitType.Star);
            NetColRest.Width = new GridLength(100.0 - netT, GridUnitType.Star);
            TxtNetMeasured.Text =
                r.NetPingMs < 0
                    ? "—"
                    : $"{r.NetPingMs:F0} ms · jitter {r.NetJitterMs:F1} ms · perte {r.NetLossPct:F0} %";

            // ── Classement CPU type Cinebench (v1.3.0) ─────────────────────
            RefreshLadder(r);

            // ── DUMP COMPLET dans le journal (pour calibration des pivots) ──
            // L'utilisateur peut copier ces lignes pour qu'on ajuste les
            // Pivot265K_* dans CpuReference.cs et obtenir un score 100 exact.
            try
            {
                _main.Log($"Bench v2 — CPU '{r.CpuName}' threads={r.CpuThreads} ref={(r.HasNominalRef?r.NominalRefModel:"(aucune)")}");
                _main.Log($"  CPU  Single  = {r.CpuSingleMops:F2} Mpx/s   → score {r.CpuSingleScore}");
                _main.Log($"  CPU  Multi   = {r.CpuMultiMops:F2} Mpx/s   → score {r.CpuMultiScore}");
                _main.Log($"  CPU  Mem     = {r.CpuMemMops:F2} Mhops/s → score {r.CpuMemScore}");
                _main.Log($"  SYS  Frame   = {r.SysFrameJitterMs:F2} ms       → score {r.SysFrameScore}");
                _main.Log($"  SYS  Input   = {r.SysInputJitterUs:F0} µs       → score {r.SysInputScore}");
                _main.Log($"  RAM  R/W/Copy = {r.RamReadGBs:F0}/{r.RamWriteGBs:F0}/{r.RamCopyGBs:F0} GB/s  → score {r.RamBandwidthScore}");
                _main.Log($"  RAM  Latence = {r.RamLatencyNs:F1} ns         → score {r.RamLatencyScore}");
                _main.Log($"  NET  Ping    = {r.NetPingMs:F0} ms · jitter {r.NetJitterMs:F1} ms · perte {r.NetLossPct:F0}%");
                _main.Log($"  TOTAL = {r.TotalScore} (CPU {r.CpuScore} · SYS {r.SysScore} · RAM {r.RamScore} · NET {r.NetScore}){(r.Unstable?"  ⚠ INSTABLE (écart >30% entre runs)":"")}");
            }
            catch { }
        }

        // ── Pills : détail au clic (zone partagée, un panneau à la fois) ──────
        // Re-cliquer sur la même pill referme la zone. Cliquer sur une autre bascule.

        private System.Windows.Controls.StackPanel? _openDetail;

        private void TogglePillDetail(System.Windows.Controls.StackPanel panel)
        {
            if (_history.Count == 0) return;   // rien à détailler avant le 1er bench
            foreach (var p in new[] { DetailCpu, DetailSys, DetailRam, DetailNet })
                p.Visibility = Visibility.Collapsed;

            if (_openDetail == panel)
            {
                _openDetail = null;
                DetailZone.Visibility = Visibility.Collapsed;
                return;
            }
            _openDetail = panel;
            panel.Visibility      = Visibility.Visible;
            DetailZone.Visibility = Visibility.Visible;
        }

        private void PillCpu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => TogglePillDetail(DetailCpu);
        private void PillSys_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => TogglePillDetail(DetailSys);
        private void PillRam_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => TogglePillDetail(DetailRam);
        private void PillNet_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => TogglePillDetail(DetailNet);

        // ── BLOC 2bis : Classement CPU type Cinebench ─────────────────────────
        // Barres horizontales : CPUs voisins du tien (nominal = moyennes publiques
        // PassMark, échelle 265K = 100) + TA mesure réelle en accent bleu. Le but :
        // voir d'un coup d'œil où ta machine se situe dans le classement réel.

        private void RefreshLadder(BenchmarkResult r)
        {
            LadderPanel.Children.Clear();
            // Refonte v1.3.2 : la tuile reste TOUJOURS visible (c'est le bloc central de
            // la page d'accueil). Sans référence/données → message TxtLadderEmpty.
            if (!r.HasNominalRef)
            {
                TxtLadderEmpty.Text = "CPU non répertorié dans la base — classement indisponible (le score reste valable).";
                TxtLadderEmpty.Visibility = Visibility.Visible;
                return;
            }

            var ladder = Helpers.CpuReference.GetLadder(r.NominalRefModel, around: 3);
            if (ladder.Count == 0)
            {
                TxtLadderEmpty.Visibility = Visibility.Visible;
                return;
            }
            TxtLadderEmpty.Visibility = Visibility.Collapsed;

            // Ta mesure réelle convertie en points classement (échelle 265K = 100)
            double userPts = r.CpuMultiMops / Helpers.CpuReference.BaseMultiMpxsPublic * 100.0;

            // Échelle = max entre tous les nominaux affichés et ta mesure
            double maxPts = Math.Max(ladder.Max(e => e.Ratio), userPts) * 1.05;

            // Construire la liste finale ordonnée : on insère TA MESURE à sa place
            // dans le classement (entre les nominaux), marquée comme "user measure".
            var rows = new List<(string label, double pts, int kind)>(); // kind: 0=autre, 1=nominal de ton CPU, 2=ta mesure
            foreach (var (model, ratio, isUser) in ladder)
                rows.Add((model, ratio, isUser ? 1 : 0));
            rows.Add(("► TON PC (mesuré)", userPts, 2));
            rows = rows.OrderByDescending(x => x.pts).ToList();

            // ── Rendu FAÇON CINEBENCH (v1.3.5, demande utilisateur sur capture) ──
            // Chaque ligne EST la barre : rang + nom À L'INTÉRIEUR, score au bout droit
            // de la ligne, longueur proportionnelle au score. Classé décroissant.
            for (int i = 0; i < rows.Count; i++)
            {
                var (label, pts, kind) = rows[i];

                var row = new Grid { Height = 24, Margin = new Thickness(0, 0, 0, 5) };

                // 1) La barre proportionnelle (toute la hauteur de la ligne)
                var barHost = new Grid();
                barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.5, pts), GridUnitType.Star) });
                barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.5, maxPts - pts), GridUnitType.Star) });
                // Palette façon Cinebench (demande utilisateur) : TA mesure = ORANGE,
                // ton CPU (nominal attendu) = bleu Tweakly, les voisins = bleu acier.
                var bar = new Border { CornerRadius = new CornerRadius(4) };
                if (kind == 2)
                {
                    // ORANGE thémé : vif en sombre, brûlé profond en clair (sinon criard sur le bleu-ardoise).
                    var oc = Optimisation_Tool.Helpers.ThemeManager.C("ThOrange");
                    var ocDk = Color.FromRgb((byte)(oc.R * 0.82), (byte)(oc.G * 0.82), (byte)(oc.B * 0.82));
                    bar.Background = new LinearGradientBrush(ocDk, oc, 0);
                }
                else if (kind == 1)
                {
                    var bc = Optimisation_Tool.Helpers.ThemeManager.C("ThLadderCpu");
                    var bcDk = Color.FromRgb((byte)(bc.R * 0.74), (byte)(bc.G * 0.74), (byte)(bc.B * 0.88));
                    bar.Background = new LinearGradientBrush(bcDk, bc, 0);
                }
                else
                {
                    var sc = Optimisation_Tool.Helpers.ThemeManager.C("ThSteel");
                    bar.Background = new SolidColorBrush(Color.FromArgb(0x96, sc.R, sc.G, sc.B));
                }
                Grid.SetColumn(bar, 0); barHost.Children.Add(bar);
                row.Children.Add(barHost);

                // 2) Texte PAR-DESSUS : « N. Modèle » à gauche, score à droite.
                //    ThTextTitle (thémé) reste lisible sur la barre ET sur le fond,
                //    barres en alpha modéré pour les deux thèmes.
                var name = new TextBlock
                {
                    Text       = $"{i + 1}.  {label}",
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize   = 11.5,
                    FontWeight = kind != 0 ? FontWeights.Bold : FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(9, 0, 70, 0),
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "ThTextTitle");
                row.Children.Add(name);

                var val = new TextBlock
                {
                    Text       = pts.ToString("F0"),
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize   = 11.5,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                if (kind == 2) val.SetResourceReference(TextBlock.ForegroundProperty, "ThOrange");
                else           val.SetResourceReference(TextBlock.ForegroundProperty, kind == 1 ? "ThTextTitle" : "ThTextNav");
                row.Children.Add(val);

                LadderPanel.Children.Add(row);
            }

            // Légende discrète (façon Cinebench) : chaque carré ■ est colorié comme sa
            // barre (orange = ta mesure, bleu = ton CPU, bleu acier = voisins).
            var legend = new TextBlock
            {
                FontFamily   = (FontFamily)FindResource("AppFont"),
                FontSize     = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            legend.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            var orange = Optimisation_Tool.Helpers.ThemeManager.Brush("ThOrange");
            var blue   = Optimisation_Tool.Helpers.ThemeManager.Brush("ThLadderCpu");
            var steel  = Optimisation_Tool.Helpers.ThemeManager.Brush("ThSteel");
            void Sq(SolidColorBrush c) => legend.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = c });
            Sq(orange); legend.Inlines.Add(new System.Windows.Documents.Run("ta mesure réelle      "));
            Sq(blue);   legend.Inlines.Add(new System.Windows.Documents.Run("ton CPU (score attendu)      "));
            Sq(steel);  legend.Inlines.Add(new System.Windows.Documents.Run("CPUs voisins (moyennes publiques)      "));
            legend.Inlines.Add(new System.Windows.Documents.Run("— échelle : 265K = 100"));
            LadderPanel.Children.Add(legend);

            LadderTile.Visibility = Visibility.Visible;
        }

        // ── BLOC 3 : Historique + sparkline ──────────────────────────────────

        private void RefreshHistory()
        {
            HistoryPanel.Children.Clear();
            if (_history.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "Aucune mesure encore. Clique « LANCER LE BENCHMARK » pour démarrer.",
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0),
                };
                // SetResourceReference (et pas FindResource qui fige le brush) → suit le thème.
                empty.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                HistoryPanel.Children.Add(empty);
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
                ? $"CPU {r.CpuMultiMops:F0}/{r.NominalMultiMops:F0} Mpx/s  ·  Jitter {r.SysJitterMicroSec:F0} µs  ·  Ping {(r.NetPingMs < 0 ? "—" : r.NetPingMs.ToString("F0") + " ms")}"
                : $"CPU {r.CpuMultiMops:F0} Mpx/s  ·  Jitter {r.SysJitterMicroSec:F0} µs  ·  Ping {(r.NetPingMs < 0 ? "—" : r.NetPingMs.ToString("F0") + " ms")}";
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
                Stroke = Optimisation_Tool.Helpers.ThemeManager.Brush("ThLadderCpu"),
                StrokeLineJoin = PenLineJoin.Round,
            };
            SparkCanvas.Children.Add(line);
            // Ligne de référence 100
            double y100 = h - (100 - minS) / (maxS - minS) * h;
            var refLine = new Line
            {
                X1 = 0, X2 = w, Y1 = y100, Y2 = y100,
                StrokeThickness = 1, Opacity = 0.4,
                Stroke = Optimisation_Tool.Helpers.ThemeManager.Brush("ThOk"),
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
                    "Il faut un 2e benchmark pour comparer. La différence s'affichera ici.",
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
                Delta(_selA.CpuMultiMops, _selB.CpuMultiMops), suffix: " Mpx/s");
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
            AddRawLine(st, "CPU multi",      $"{r.CpuMultiMops:F0} Mpx/s");
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
