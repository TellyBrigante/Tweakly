using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    /// <summary>
    /// Page d'accueil de Tweakly (v1.4.3). Tableau de bord 3×2 avec tuiles denses :
    /// Tweakly Score + sous-scores, Santé système + 3 derniers incidents, dernière session
    /// jeu + stats détaillées, matériel en direct avec barres, stockage, réseau + débits.
    ///
    /// Chargement INTELLIGENT :
    ///   • Score / Stockage / Dernière session : lecture instantanée locale (JSON / WMI).
    ///   • Santé système : scan EventLog uniquement sur demande, pour garder l'accueil léger.
    ///   • Matériel : SystemMonitor.Collect léger sur un timer 5 s.
    ///   • Réseau : NetworkMonitor.CollectAsync en arrière-plan.
    ///
    /// Cohérence visuelle : UNIQUEMENT les rôles thémés (DynamicResource ThBg / ThPanel /
    /// ThText* / ThAccentIcon / ThOk / ThWarn / ThCrit / ThTrack / ThHover) → suit le thème
    /// actif sans toucher au ThemeManager.
    /// </summary>
    public partial class PageAccueil : UserControl
    {
        private readonly MainWindow _main;
        private readonly DispatcherTimer _hwTimer;
        private bool _hwBusy;
        private bool _loaded;
        private bool _heavyLoaded;
        private bool _healthBusy;

        // ── Timeline « Activité récente » (bench, sessions jeu, incidents) ─────
        // Agrégée à partir de plusieurs sources, triée chronologiquement, re-rendue à
        // chaque ajout (les sources arrivent à des moments différents : bench/session
        // instantanés, incidents après scan EventLog).
        private sealed class ActivityEntry
        {
            public DateTime Time;
            public string Icon = "";
            public string IconRole = "";
            public string Text = "";
            public string TargetTag = "";
        }
        private readonly List<ActivityEntry> _activity = new();

        public PageAccueil(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            _hwTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _hwTimer.Tick += async (_, _) => await TickHardwareAsync();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
            {
                _loaded = true;
                LoadScore();
                LoadGame();
                LoadStorage();
                LoadActivity();   // benchmarks + sessions jeu (lecture instantanée locale)
                SetHealthIdle();
                UpdateHeader();
            }
            StartDeferredLoads();
            await TickHardwareAsync();
            _hwTimer.Start();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e) => _hwTimer.Stop();

        private void StartDeferredLoads()
        {
            if (_heavyLoaded || !_main.IsLiveSamplingAllowed()) return;
            _heavyLoaded = true;
            _ = LoadNetworkAsync();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  En-tête : date + indicateur d'état plat (LED carrée + texte, pas de pilule)
        // ═══════════════════════════════════════════════════════════════════════

        private void UpdateHeader()
        {
            var jours = new[] { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };
            var mois  = new[] { "", "janvier", "février", "mars", "avril", "mai", "juin", "juillet",
                                "août", "septembre", "octobre", "novembre", "décembre" };
            var now   = DateTime.Now;
            TxtHeaderSub.Text = $"{jours[(int)now.DayOfWeek]} {now.Day} {mois[now.Month]} {now.Year}  ·  Tweakly v{PageReglages.AppVersion}";
        }

        private void ApplyHealthBadge(int alerts)
        {
            HeaderBadge.Visibility = Visibility.Visible;
            string role; string txt;
            if (alerts == 0) { role = "ThOk";   txt = "Tout va bien"; }
            else if (alerts <= 2) { role = "ThWarn"; txt = $"{alerts} alerte{(alerts > 1 ? "s" : "")} mineure{(alerts > 1 ? "s" : "")}"; }
            else { role = "ThCrit"; txt = $"{alerts} alertes à traiter"; }
            HeaderBadgeLed.SetResourceReference(BackgroundProperty, role);
            TxtHeaderBadge.Text = txt;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Tuile 1 : Tweakly Score + sous-scores CPU / Système / Réseau
        // ═══════════════════════════════════════════════════════════════════════

        private void LoadScore()
        {
            try
            {
                var hist = BenchmarkStore.Load().OrderByDescending(b => b.Timestamp).ToList();
                if (hist.Count == 0)
                {
                    TxtScoreValue.Text = "—";
                    TxtScoreDelta.Text = "";
                    TxtScoreSub.Text   = "Lance un benchmark pour avoir un score.";
                    ScoreSubGrid.Visibility = Visibility.Collapsed;
                    return;
                }
                var last = hist[0];
                TxtScoreValue.Text = last.TotalScore.ToString("F0");

                if (hist.Count >= 2)
                {
                    double delta = last.TotalScore - hist[1].TotalScore;
                    if (Math.Abs(delta) < 1)
                    {
                        TxtScoreDelta.Text = "stable";
                        TxtScoreDelta.SetResourceReference(ForegroundProperty, "ThTextDim");
                    }
                    else
                    {
                        TxtScoreDelta.Text = (delta > 0 ? "+" : "") + delta.ToString("F0");
                        TxtScoreDelta.SetResourceReference(ForegroundProperty, delta > 0 ? "ThOk" : "ThCrit");
                    }
                }
                else TxtScoreDelta.Text = "";

                int days = Math.Max(0, (int)(DateTime.Now - last.Timestamp).TotalDays);
                TxtScoreSub.Text = days == 0 ? "Mesuré aujourd'hui"
                                 : days == 1 ? "Mesuré hier"
                                 : $"Mesuré il y a {days} j";

                // Sous-scores (0 à 150, on borne à 150 % pour les barres)
                SetScoreBar(ScoreCpuBar, TxtScoreCpu, last.CpuScore);
                SetScoreBar(ScoreSysBar, TxtScoreSys, last.SysScore);
                SetScoreBar(ScoreNetBar, TxtScoreNet, last.NetScore);
                ScoreSubGrid.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-score", "Accueil : affichage du dernier benchmark", ex);
            }
        }

        private static void SetScoreBar(Grid bar, TextBlock val, double score)
        {
            double pct = Math.Max(0, Math.Min(100, score / 150.0 * 100.0));
            bar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            val.Text = score > 0 ? score.ToString("F0") : "—";
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Tuile 2 : Santé système + 3 derniers incidents
        // ═══════════════════════════════════════════════════════════════════════

        private void SetHealthIdle()
        {
            HeaderBadge.Visibility = Visibility.Collapsed;

            TxtHealthValue.Text = "";
            TxtHealthValue.SetResourceReference(ForegroundProperty, "ThTextTitle");
            TxtHealthSub.Text = "Analyse à la demande.";
            SetHealthRefreshState(false);

            HealthList.Children.Clear();
            var row = new TextBlock
            {
                Text = "Rafraîchis cette tuile pour afficher un résumé.",
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };
            row.SetResourceReference(ForegroundProperty, "ThTextDim");
            HealthList.Children.Add(row);
        }

        private void SetHealthRefreshState(bool busy)
        {
            TxtHealthRefresh.Text = busy ? "Analyse..." : "Rafraîchir";
            TxtHealthRefreshIcon.Text = busy ? "\uE895" : "\uE72C";
            HealthRefreshChip.Opacity = busy ? 0.65 : 1.0;
            HealthRefreshChip.Cursor = busy ? Cursors.Arrow : Cursors.Hand;
        }

        private void HealthRefreshChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private async void HealthRefreshChip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await RefreshHealthAsync();
        }

        private async Task RefreshHealthAsync()
        {
            if (_healthBusy) return;
            _healthBusy = true;
            try
            {
                SetHealthRefreshState(true);
                TxtHealthValue.Text = "";
                TxtHealthSub.Text = "Analyse des journaux Windows...";
                HealthList.Children.Clear();
                await LoadHealthAsync();
            }
            finally
            {
                _healthBusy = false;
                SetHealthRefreshState(false);
            }
        }

        private async Task LoadHealthAsync()
        {
            try
            {
                var incidents = await Task.Run(() => EventLogDecoder.ScanIncidents(7));
                int critical = incidents.Count(i => i.Sev == LogSev.Serious);
                int total    = incidents.Count;

                HealthList.Children.Clear();

                // Alimenter aussi la timeline « Activité récente » avec les incidents trouvés
                AddIncidentsToActivity(incidents);

                if (total == 0)
                {
                    TxtHealthValue.Text = "RAS";
                    TxtHealthValue.SetResourceReference(ForegroundProperty, "ThOk");
                    TxtHealthSub.Text   = "Aucun incident sur 7 jours.";
                    ApplyHealthBadge(0);
                }
                else
                {
                    TxtHealthValue.Text = total.ToString();
                    TxtHealthValue.SetResourceReference(ForegroundProperty, critical > 0 ? "ThCrit" : "ThWarn");
                    TxtHealthSub.Text   = critical > 0
                        ? $"{critical} critique{(critical > 1 ? "s" : "")} · {total - critical} mineur{(total - critical > 1 ? "s" : "")} (7 j)"
                        : $"{total} mineur{(total > 1 ? "s" : "")} sur 7 jours";
                    ApplyHealthBadge(critical);

                    // Lister les 3 derniers incidents (plus récents) en mini-format
                    foreach (var inc in incidents.OrderByDescending(i => i.Start).Take(3))
                    {
                        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        var when = new TextBlock
                        {
                            Text = inc.Start.ToString("dd/MM"),
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        when.SetResourceReference(ForegroundProperty, "ThTextDim");
                        Grid.SetColumn(when, 0);
                        row.Children.Add(when);

                        var title = new TextBlock
                        {
                            Text = inc.Title,
                            FontFamily = (FontFamily)FindResource("AppFont"),
                            FontSize = 11,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        title.SetResourceReference(ForegroundProperty,
                            inc.Sev == LogSev.Serious ? "ThCrit" : "ThTextBody");
                        Grid.SetColumn(title, 1);
                        row.Children.Add(title);

                        HealthList.Children.Add(row);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-health", "Accueil : résumé des erreurs Windows", ex);
                TxtHealthValue.Text = "—";
                TxtHealthSub.Text   = "Analyse impossible — voir Erreurs Windows.";
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Tuile 3 : Dernière session jeu + 0.1 % low + stabilité frametime
        // ═══════════════════════════════════════════════════════════════════════

        private void LoadGame()
        {
            try
            {
                var sessions = SessionStore.Load();
                if (sessions.Count == 0)
                {
                    TxtGameValue.Text = "—";
                    TxtGameSub.Text   = "Lance une session pour analyser tes FPS.";
                    GameStatsGrid.Visibility = Visibility.Collapsed;
                    return;
                }
                var last = sessions.OrderByDescending(s => s.CapturedAtUtc).First();
                TxtGameValue.Text = last.FpsP50 > 0 ? last.FpsP50.ToString("F0") : "—";

                string game = !string.IsNullOrWhiteSpace(last.GameDisplay) ? last.GameDisplay :
                              !string.IsNullOrWhiteSpace(last.GameExe)     ? last.GameExe     : "jeu inconnu";
                int days = Math.Max(0, (int)(DateTime.Now - last.CapturedAtUtc.ToLocalTime()).TotalDays);
                string when = days == 0 ? "aujourd'hui" : days == 1 ? "hier" : $"il y a {days} j";
                TxtGameSub.Text = $"{game} · {when}";

                // Stats détaillées
                TxtGameLow.Text = last.FpsZeroOnePctLow > 0 ? $"{last.FpsZeroOnePctLow:F0} fps" : "—";
                if (last.FrametimeCvPct > 0)
                {
                    string stab = last.FrametimeCvPct <= 8  ? "très stable"
                                : last.FrametimeCvPct <= 14 ? "correct"
                                : last.FrametimeCvPct <= 22 ? "irrégulier"
                                                            : "instable";
                    TxtGameCv.Text = $"{stab} ({last.FrametimeCvPct:F0} %)";
                    TxtGameCv.SetResourceReference(ForegroundProperty,
                        last.FrametimeCvPct <= 8  ? "ThOk"
                      : last.FrametimeCvPct <= 14 ? "ThTextBody"
                      : last.FrametimeCvPct <= 22 ? "ThWarn"
                                                  : "ThCrit");
                }
                else TxtGameCv.Text = "—";
                GameStatsGrid.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-game-session", "Accueil : affichage de la dernière session de jeu", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Tuile 4 : Matériel temps réel (1 Hz) avec mini-barres
        // ═══════════════════════════════════════════════════════════════════════

        private async Task TickHardwareAsync()
        {
            if (!_main.IsLiveSamplingAllowed()) return;
            StartDeferredLoads();
            if (_hwBusy) return;
            _hwBusy = true;
            try
            {
                var s = await SystemMonitor.CollectAsync(MonCollectParts.Light);
                TxtHwCpu.Text = $"{s.CpuUsage:F0} %";
                SetHwBar(HwCpuBar, s.CpuUsage);
                TxtHwGpu.Text = s.GpuOk ? $"{s.GpuUsage:F0} %" : "—";
                SetHwBar(HwGpuBar, s.GpuOk ? s.GpuUsage : 0);
                TxtHwRam.Text = s.RamPct > 0 ? $"{s.RamPct:F0} %" : "—";
                SetHwBar(HwRamBar, s.RamPct);

                // Températures regroupées sur une ligne
                string temps = "";
                if (s.GpuOk && s.GpuTemp > 0) temps += $"GPU {s.GpuTemp:F0} °C";
                if (s.CpuTempC.HasValue) temps += (temps.Length > 0 ? "  ·  " : "") + $"CPU {s.CpuTempC.Value:F0} °C";
                TxtHwTemp.Text = temps.Length > 0 ? temps : "—";
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-live-hardware", "Accueil : mesure du matériel en direct", ex);
            }
            finally { _hwBusy = false; }
        }

        private static void SetHwBar(Grid bar, double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            bar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Tuile 5 : Stockage
        // ═══════════════════════════════════════════════════════════════════════

        private void LoadStorage()
        {
            try
            {
                StorageList.Children.Clear();
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0)
                    .OrderBy(d => d.Name)
                    .Take(4)
                    .ToList();
                if (drives.Count == 0)
                {
                    var t = new TextBlock
                    {
                        Text = "Aucun disque détecté",
                        FontFamily = (FontFamily)FindResource("AppFont"),
                        FontSize = 11,
                    };
                    t.SetResourceReference(ForegroundProperty, "ThTextDim");
                    StorageList.Children.Add(t);
                    return;
                }
                foreach (var d in drives)
                {
                    double total = d.TotalSize / (1024.0 * 1024 * 1024);
                    double free  = d.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                    double pct   = (total - free) / total * 100.0;
                    string roleColor = pct >= 92 ? "ThCrit" :
                                       pct >= 80 ? "ThWarn" : "ThAccentIcon";

                    var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var letter = new TextBlock
                    {
                        Text = d.Name.TrimEnd('\\'),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    letter.SetResourceReference(ForegroundProperty, "ThTextBody");
                    Grid.SetColumn(letter, 0);
                    row.Children.Add(letter);

                    var barOuter = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    var track = new Border { CornerRadius = new CornerRadius(2) };
                    track.SetResourceReference(BackgroundProperty, "ThTrack");
                    barOuter.Children.Add(track);
                    var fillGrid = new Grid();
                    fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) });
                    fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - pct, GridUnitType.Star) });
                    var fill = new Border { CornerRadius = new CornerRadius(2) };
                    fill.SetResourceReference(BackgroundProperty, roleColor);
                    Grid.SetColumn(fill, 0);
                    fillGrid.Children.Add(fill);
                    barOuter.Children.Add(fillGrid);
                    Grid.SetColumn(barOuter, 1);
                    row.Children.Add(barOuter);

                    var size = new TextBlock
                    {
                        Text = $"{free:F0} Go libres",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    size.SetResourceReference(ForegroundProperty, "ThTextDim");
                    Grid.SetColumn(size, 2);
                    row.Children.Add(size);

                    StorageList.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-storage", "Accueil : affichage du stockage", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Tuile 6 : Réseau (ping + débits visibles)
        // ═══════════════════════════════════════════════════════════════════════

        private async Task LoadNetworkAsync()
        {
            try
            {
                var s = await NetworkMonitor.Instance.CollectAsync("1.1.1.1");
                if (s.PingMs < 0)
                {
                    TxtNetValue.Text = "—";
                    TxtNetValue.SetResourceReference(ForegroundProperty, "ThCrit");
                    TxtNetSub.Text   = "Hors ligne ou ping perdu.";
                    NetSpeedGrid.Visibility = Visibility.Collapsed;
                    return;
                }
                TxtNetValue.Text = s.PingMs.ToString("F0");
                string roleColor = s.PingMs >= 150 ? "ThCrit" :
                                   s.PingMs >= 80  ? "ThWarn" : "ThOk";
                string verdict   = s.PingMs >= 150 ? "Latence élevée" :
                                   s.PingMs >= 80  ? "Latence correcte" :
                                                     "Excellent";
                TxtNetValue.SetResourceReference(ForegroundProperty, roleColor);
                TxtNetSub.Text = verdict;

                // Débits — n'apparaissent que si on a une mesure (>0)
                if (s.DownMbps > 0 || s.UpMbps > 0)
                {
                    TxtNetDown.Text = s.DownMbps > 0 ? $"{s.DownMbps:F1} Mbps" : "—";
                    TxtNetUp.Text   = s.UpMbps   > 0 ? $"{s.UpMbps:F1} Mbps"   : "—";
                    NetSpeedGrid.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-network", "Accueil : mesure réseau", ex);
                TxtNetValue.Text = "—";
                TxtNetSub.Text   = "Mesure impossible.";
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Activité récente : agrégation bench + sessions + incidents
        // ═══════════════════════════════════════════════════════════════════════

        private void LoadActivity()
        {
            try
            {
                _activity.Clear();
                foreach (var b in BenchmarkStore.Load())
                {
                    _activity.Add(new ActivityEntry
                    {
                        Time      = b.Timestamp,
                        Icon      = "",   // SpeedHigh (compteur)
                        IconRole  = "ThAccentIcon",
                        Text      = $"Benchmark — {b.TotalScore} points",
                        TargetTag = "Benchmark",
                    });
                }
                foreach (var s in SessionStore.Load())
                {
                    string game = !string.IsNullOrWhiteSpace(s.GameDisplay) ? s.GameDisplay
                                : !string.IsNullOrWhiteSpace(s.GameExe)     ? s.GameExe
                                                                            : "jeu inconnu";
                    _activity.Add(new ActivityEntry
                    {
                        Time      = s.CapturedAtUtc.ToLocalTime(),
                        Icon      = "",   // gamepad
                        IconRole  = "ThAccentIcon",
                        Text      = $"Session {game} — {s.FpsP50:F0} fps médian",
                        TargetTag = "GameSession",
                    });
                }
                RenderActivity();
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("home-activity", "Accueil : activité récente", ex);
            }
        }

        private void AddIncidentsToActivity(IEnumerable<Incident> incidents)
        {
            // Retire les éventuels incidents déjà ajoutés (idempotent si LoadHealthAsync re-tourne)
            _activity.RemoveAll(a => a.TargetTag == "EventLog");
            foreach (var inc in incidents)
            {
                _activity.Add(new ActivityEntry
                {
                    Time      = inc.Start,
                    Icon      = inc.Sev == LogSev.Serious ? "" : "",   // warning ou info
                    IconRole  = inc.Sev == LogSev.Serious ? "ThCrit" : "ThWarn",
                    Text      = inc.Title,
                    TargetTag = "EventLog",
                });
            }
            RenderActivity();
        }

        private void RenderActivity()
        {
            ActivityList.Children.Clear();
            var sorted = _activity
                .Where(a => string.IsNullOrEmpty(a.TargetTag) || _main.IsNavigationTargetVisible(a.TargetTag))
                .OrderByDescending(a => a.Time)
                .Take(12)
                .ToList();
            if (sorted.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "Rien à montrer pour l'instant — fais un benchmark, lance une capture, ou attends qu'un incident apparaisse.",
                    FontFamily = (FontFamily)FindResource("AppFont"),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 4, 8, 4),
                };
                empty.SetResourceReference(ForegroundProperty, "ThTextDim");
                ActivityList.Children.Add(empty);
                return;
            }
            foreach (var entry in sorted)
            {
                var btn = new Button { Style = (Style)FindResource("ActivityRow") };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var icon = new TextBlock { Text = entry.Icon, Style = (Style)FindResource("ActivityIcon") };
                icon.SetResourceReference(ForegroundProperty, entry.IconRole);
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var date = new TextBlock { Text = entry.Time.ToString("dd/MM HH:mm"), Style = (Style)FindResource("ActivityDate") };
                Grid.SetColumn(date, 1);
                grid.Children.Add(date);

                var text = new TextBlock { Text = entry.Text, Style = (Style)FindResource("ActivityText") };
                Grid.SetColumn(text, 2);
                grid.Children.Add(text);

                btn.Content = grid;
                string targetTag = entry.TargetTag;
                if (targetTag.Length > 0) btn.Click += (_, _) => _main.NavigateToTag(targetTag);
                ActivityList.Children.Add(btn);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Navigation : chaque tuile pointe vers la page complète
        // ═══════════════════════════════════════════════════════════════════════

        private void NavigateDashboard(string targetTag, string fallbackTag = "")
        {
            if (_main.IsNavigationTargetVisible(targetTag))
            {
                _main.NavigateToTag(targetTag);
                return;
            }
            if (!string.IsNullOrEmpty(fallbackTag))
                _main.NavigateToTag(fallbackTag);
        }

        private void TileScore_Click   (object s, RoutedEventArgs e) => NavigateDashboard("Benchmark");
        private void TileHealth_Click  (object s, RoutedEventArgs e) => NavigateDashboard("EventLog", "Diagnostic");
        private void TileGame_Click    (object s, RoutedEventArgs e) => NavigateDashboard("GameSession", "Monitoring");
        private void TileHardware_Click(object s, RoutedEventArgs e) => _main.NavigateToTag("Monitoring");
        private void TileStorage_Click (object s, RoutedEventArgs e) => _main.NavigateToTag("Diagnostic");
        private void TileNetwork_Click (object s, RoutedEventArgs e) => _main.NavigateToTag("ReseauMon");
    }
}
