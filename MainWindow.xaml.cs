using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Runtime.InteropServices;
using Optimisation_Tool.Pages;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool
{
    public partial class MainWindow : Window
    {
        private Button?                                  _selectedNav;
        private readonly Dictionary<string, Lazy<UserControl>> _pages;

        // Mode mini (overlay compact) — voir EnterMiniMode / ExitMiniMode
        private MiniMonitor? _mini;
        private Button?      _returnNav;     // page à restaurer en sortant du mode mini
        public  bool         ShuttingDown { get; private set; }
        private WindowState   _lastWindowState = WindowState.Normal;
        private bool          _restoreAfterTaskbarQueued;

        // Tray icon (zone de notification Windows) — gérée par TrayIconManager.
        // Null si l'init a échoué (cas rare : RDP sans tray, etc.) → on retombe sur le
        // comportement standard (WindowState.Minimized vers la barre des tâches).
        private TrayIconManager? _tray;

        // Groupes de navigation repliables.
        private sealed class NavGroup
        {
            public Button Header = null!;
            public Border Panel  = null!;
            public HashSet<string> Tags = new();
            public string Label  = "";
            public bool   Expanded;
        }
        private List<NavGroup> _groups = new();

        private static readonly HashSet<string> EasyHiddenNavTags = new(StringComparer.Ordinal)
        {
            "EventLog",
            "GameSession",
            "CPU",
            "Nvidia",
            "GpuTuning",
        };

        // Settings persistants
        public static AppSettings Settings { get; private set; } = new();

        public MainWindow()
        {
            InitializeComponent();

            // Sons d'interface : uniquement les notifications (succès / avertissement).
            Helpers.UiSound.Init();

            // ⚠️ Tray icon : init dans OnSourceInitialized (PAS dans le ctor, PAS dans Loaded).
            // Voir TrayIconManager.cs pour l'historique des tentatives ratées (écran noir).

            _pages = new Dictionary<string, Lazy<UserControl>>
            {
                ["Accueil"]   = new Lazy<UserControl>(() => new PageAccueil(this)),
                ["Nettoyage"] = new Lazy<UserControl>(() => new PageNettoyage(this)),
                ["Nvidia"]    = new Lazy<UserControl>(() => new PageNvidia(this)),
                ["GpuTuning"] = new Lazy<UserControl>(() => new PageGpuTuning()),
                ["CPU"]       = new Lazy<UserControl>(() => new PageCPU(this)),
                ["Windows"]   = new Lazy<UserControl>(() => new PageWindows(this)),
                ["Battery"]   = new Lazy<UserControl>(() => new PageBatteryCalibration(this)),
                ["Reseau"]    = new Lazy<UserControl>(() => new PageReseau(this)),
                ["Privacy"]   = new Lazy<UserControl>(() => new PagePrivacy(this)),
                ["Apps"]      = new Lazy<UserControl>(() => new PageApps(this)),
                ["Fresh"]     = new Lazy<UserControl>(() => new PageFresh(this)),
                ["WinRepair"] = new Lazy<UserControl>(() => new PageWindowsRepair(this)),
                ["Info"]      = new Lazy<UserControl>(() => new PageSpecs(this)),
                ["Monitoring"]= new Lazy<UserControl>(() => new PageMonitoring(this)),
                ["ReseauMon"] = new Lazy<UserControl>(() => new PageReseauMonitoring(this)),
                ["Diagnostic"]= new Lazy<UserControl>(() => new PageDiagnostic(this)),
                ["EventLog"]  = new Lazy<UserControl>(() => new PageEventLog(this)),
                ["GameSession"]=new Lazy<UserControl>(() => new PageGameSession()),
                ["Benchmark"] = new Lazy<UserControl>(() => new PageBenchmark(this)),
                ["Reglages"]  = new Lazy<UserControl>(() => new PageReglages(this)),
            };

            _groups = new List<NavGroup>
            {
                new NavGroup { Header = BtnNavDiagGroup, Panel = DiagGroupPanel, Label = "Diagnostiquer",
                               Tags = new HashSet<string> { "EventLog", "Diagnostic", "Info", "Benchmark" } },
                new NavGroup { Header = BtnNavMonitorGroup, Panel = MonitorGroupPanel, Label = "Surveiller",
                               Tags = new HashSet<string> { "Monitoring", "ReseauMon", "GameSession" } },
                new NavGroup { Header = BtnNavOptimizeGroup, Panel = OptimizeGroupPanel, Label = "Optimiser",
                               Tags = new HashSet<string> { "Windows", "Battery", "CPU", "Nvidia", "GpuTuning", "Reseau", "Privacy" } },
                new NavGroup { Header = BtnNavRepairGroup, Panel = RepairGroupPanel, Label = "Réparer",
                               Tags = new HashSet<string> { "WinRepair", "Nettoyage", "Apps" } },
                new NavGroup { Header = BtnNavPrepareGroup, Panel = PrepareGroupPanel, Label = "Réinstaller",
                               Tags = new HashSet<string> { "Fresh" } },
            };

            Loaded += (_, _) =>
            {
                // Migration silencieuse vers le layout v1.2.8 (config\ + data\<sous-dossiers>\).
                // DOIT être appelée AVANT tout helper qui touche aux fichiers. Idempotente, blindée.
                try { Helpers.PathLayout.MigrateIfNeeded(); } catch { }
                try { Helpers.BatteryCalibrationStore.RestorePowerPlanGuardIfNeeded(); } catch { }

                // Répare en arrière-plan les tâches « démarrer avec Windows » créées par
                // d'anciennes versions (sans le flag --startup) — sinon l'app s'ouvrirait en
                // grand au boot au lieu de rester minimisée. Best-effort, ne bloque pas l'UI.
                _ = Task.Run(() => { try { Helpers.StartupManager.EnsureStartupArg(); } catch { } });

                // Charger + appliquer les settings sauvegardés
                Settings = AppSettings.Load();
                Helpers.UiSound.Enabled = Settings.SoundsEnabled;
                Helpers.CpuTemperature.Enabled = Settings.CpuTempEnabled;
                var mode = Settings.Theme == "Light" ? ThemeManager.Mode.Light : ThemeManager.Mode.Dark;
                ApplyTheme(mode, persist: false);
                ApplyNavigationMode(Settings.NavigationMode, persist: false);
                try { FitToWorkArea(); } catch { }
                // Version affichée = source unique (PageReglages.AppVersion)
                TxtVersion.Text = "v" + Pages.PageReglages.AppVersion;

                Log("Outil prêt.");

                // Onglet Nvidia verrouillé si aucune carte Nvidia (PC en IGP / AMD seul) → pas d'éditeur
                // de pilote Nvidia inutile. Fail-open : en cas de doute, l'onglet reste accessible.
                try
                {
                    if (Helpers.SystemMonitor.ShouldLockNvidiaTab())
                    {
                        LockNavButton(BtnNavNvidia, "Aucune carte graphique Nvidia détectée sur ce PC.");
                    }

                    if (Helpers.SystemMonitor.ShouldLockGpuTuningTab(out string gpuTuningLockReason))
                        LockNavButton(BtnNavGpuTuning, gpuTuningLockReason);
                }
                catch { }

                try
                {
#if !DEBUG
                    bool hasBattery = Helpers.BatteryProbe.HasBattery();
                    if (!hasBattery)
                        LockNavButton(BtnNavBattery, "Aucune batterie détectée sur ce PC.");
#else
                    BtnNavBattery.ToolTip = "Debug : simulation batterie disponible si aucune batterie réelle n'est détectée.";
#endif
                }
                catch { }

                // Démarrage : page d'accueil = Dashboard (v1.4.3)
                NavigateTo(BtnNavAccueil);
                ResumeActiveBatteryCalibrationIfNeeded();

                // Démarrage minimisé : réduire après la navigation et le premier cycle UI.
                // Le faire trop tôt dans Loaded peut laisser Windows avec une fenêtre
                // minimisée mal restaurée depuis la barre des tâches.
                if (App.ShouldStartMinimized(Settings.StartMinimized))
                {
                    TraceWindowRestore("startup-minimize scheduled");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_tray?.IsAvailable == true) _tray.HideToTray();
                            else                            WindowState = WindowState.Minimized;
                            TraceWindowRestore("startup-minimize applied");
                        }
                        catch { }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                // Forcer la fenêtre au PREMIER PLAN (pas seulement Topmost flicker, qui ne
                // suffit pas si une autre app détient le foreground). On utilise
                // AttachThreadInput pour bypass la protection Win10/11. Voir ForceForeground.
                // (Relance après MAJ + lancement manuel inclus : on veut voir l'app.)
                if (!App.ShouldStartMinimized(Settings.StartMinimized))
                {
                    // 1) Tentative immédiate (cas où on est déjà foreground ou rien ne bloque).
                    try
                    {
                        Topmost = true;
                        Topmost = false;
                    }
                    catch { }
                    ForceForeground();

                    // 2) Tentative DIFFÉRÉE en priorité ContextIdle : exécutée APRÈS le 1er
                    //    paint, donc même si une app concurrente nous a volé le focus pendant
                    //    le chargement, on le récupère. Sert aussi de filet anti-régression
                    //    pour le rendu (InvalidateVisual rattrape un éventuel paint manqué).
                    try
                    {
                        this.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                InvalidateVisual();
                                UpdateLayout();
                                ForceForeground();
                            }
                            catch { }
                        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                    }
                    catch { }
                }

#if DEBUG
                // APERÇU DESIGN (Debug uniquement, absent du build Release) : F12 affiche
                // l'overlay de MAJ avec des données factices — permet d'itérer sur le visuel
                // sans publier de release. Re-F12 / « Plus tard » pour fermer.
                PreviewKeyDown += (_, k) =>
                {
                    if (k.Key != System.Windows.Input.Key.F12) return;
                    if (UpdateOverlay.Visibility == Visibility.Visible)
                    {
                        UpdateOverlay.Visibility = Visibility.Collapsed;
                        return;
                    }
                    TxtUpdTitle.Text    = "Une mise à jour est disponible";
                    RunUpdFrom.Text     = $"v{Pages.PageReglages.AppVersion}";
                    RunUpdTo.Text       = "v9.9.9";
                    TxtUpdStatus.Text   = "Téléchargement de la mise à jour…";
                    SetUpdateNotes("- Refonte de l'écran de mise à jour : nouveautés affichées, bouton Plus tard.\n"
                                 + "- L'updater refuse désormais toute release sans hash d'intégrité (SHA-256).\n"
                                 + "- Corrections diverses et améliorations de stabilité.");
                    BtnUpdLater.Visibility   = Visibility.Visible;
                    BtnUpdContinue.Visibility = Visibility.Visible;
                    SetUpdateBar(67);
                    UpdateOverlay.Visibility = Visibility.Visible;
                };
#endif
#if !DEBUG
                // MAJ : vérification au démarrage + périodique (uniquement en build distribué).
                // En DEBUG (développement), on ne vérifie jamais : c'est nous la version de référence.
                if (Settings.AutoUpdate)
                {
                    _ = CheckUpdateSilentAsync();
                    StartUpdateWatcher();   // re-vérifie toutes les 30 min pendant que l'app tourne
                }
#endif
            };
        }

        private void ResumeActiveBatteryCalibrationIfNeeded()
        {
            try
            {
                var session = Helpers.BatteryCalibrationStore.Load();
                bool active = session.Phase is not Helpers.BatteryCalibrationPhase.Idle
                    and not Helpers.BatteryCalibrationPhase.Complete;
                if (!active) return;

                if (_pages.TryGetValue("Battery", out var batteryPage)
                    && batteryPage.Value is PageBatteryCalibration battery)
                    battery.ResumeActiveSession();
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Error("Calibrage batterie : reprise au démarrage", ex);
            }
        }

        // ── Mise à jour : overlay plein écran ─────────────────────────────────

        // ── Navigation ────────────────────────────────────────────────────────

        private void NavBtn_Click(object sender, RoutedEventArgs e)
            => NavigateTo((Button)sender);

        // Groupes repliables — même mécanisme pour tous.
        private void BtnNavDiagGroup_Click(object sender, RoutedEventArgs e)  => ToggleGroup((Button)sender);
        private void BtnNavMonitorGroup_Click(object sender, RoutedEventArgs e) => ToggleGroup((Button)sender);
        private void BtnNavOptimizeGroup_Click(object sender, RoutedEventArgs e) => ToggleGroup((Button)sender);
        private void BtnNavRepairGroup_Click(object sender, RoutedEventArgs e) => ToggleGroup((Button)sender);
        private void BtnNavPrepareGroup_Click(object sender, RoutedEventArgs e) => ToggleGroup((Button)sender);

        private NavGroup? GroupOf(string tag)     => _groups.FirstOrDefault(g => g.Tags.Contains(tag));
        private NavGroup? GroupByHeader(Button h)  => _groups.FirstOrDefault(g => g.Header == h);

        private void ToggleGroup(Button header)
        {
            var g = GroupByHeader(header);
            if (g == null) return;
            g.Expanded = !g.Expanded;
            g.Panel.Visibility = g.Expanded ? Visibility.Visible : Visibility.Collapsed;
            g.Header.Content   = (g.Expanded ? "▾  " : "▸  ") + g.Label;

            // Si on vient d'ouvrir un groupe, on REPLIE les autres (un seul ouvert à la fois).
            if (g.Expanded)
            {
                foreach (var other in _groups)
                {
                    if (other == g || !other.Expanded) continue;
                    other.Expanded = false;
                    other.Panel.Visibility = Visibility.Collapsed;
                    other.Header.Content = "▸  " + other.Label;
                    // Si un sous-item de l'autre groupe est actif → on resurligne son header replié
                    bool sub = _selectedNav != null && other.Tags.Contains(_selectedNav.Tag as string ?? "");
                    ApplyNavStyle(other.Header, selected: sub);
                }
            }

            // Surligner le groupe si un sous-item est actif ET que le groupe est replié
            bool subActive = _selectedNav != null && g.Tags.Contains(_selectedNav.Tag as string ?? "");
            ApplyNavStyle(g.Header, selected: subActive && !g.Expanded);
        }

        /// <summary>
        /// Navigation par TAG : helper utilisé par les tuiles cliquables du dashboard
        /// (PageAccueil) pour pointer vers une page sans avoir à récupérer la référence du
        /// bouton dans le visual tree. Cherche le bouton Nav portant ce Tag dans la sidebar
        /// et délègue à NavigateTo.
        /// </summary>
        public void NavigateToTag(string tag)
        {
            if (!IsNavTagVisible(tag)) return;
            var btn = FindNavButtonByTag(tag);
            if (btn != null) NavigateTo(btn);
        }

        private static void LockNavButton(Button button, string tooltip)
        {
            button.IsEnabled = false;
            button.ToolTip = tooltip;
            ToolTipService.SetShowOnDisabled(button, true);
            button.Opacity = 0.55;
        }

        public void ApplyNavigationMode(string mode, bool persist = true)
        {
            Settings.NavigationMode = NormalizeNavigationMode(mode);
            if (persist)
                Settings.Save();
            UpdateNavigationModeButton();

            foreach (var tag in _pages.Keys)
            {
                var btn = FindNavButtonByTag(tag);
                if (btn == null) continue;
                btn.Visibility = IsNavTagVisible(tag) ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var g in _groups)
            {
                bool hasVisibleChild = false;
                foreach (var tag in g.Tags)
                {
                    if (IsNavTagVisible(tag))
                    {
                        hasVisibleChild = true;
                        break;
                    }
                }

                g.Header.Visibility = hasVisibleChild ? Visibility.Visible : Visibility.Collapsed;
                g.Panel.Visibility  = hasVisibleChild && g.Expanded ? Visibility.Visible : Visibility.Collapsed;
                if (hasVisibleChild)
                    g.Header.Content = (g.Expanded ? "▾  " : "▸  ") + g.Label;
            }

            if (_selectedNav?.Tag is string selectedTag && !IsNavTagVisible(selectedTag))
                NavigateTo(BtnNavAccueil);

            if (MainContent.Content is PageReglages reglages)
                reglages.SyncNavigationModeSegment();
        }

        public static bool IsAdvancedNavigationMode(string mode)
            => string.Equals(NormalizeNavigationMode(mode), "Advanced", StringComparison.Ordinal);

        private static string NormalizeNavigationMode(string? mode)
            => string.Equals(mode, "Easy", StringComparison.OrdinalIgnoreCase) ? "Easy" : "Advanced";

        private static bool IsNavTagVisible(string tag)
            => IsAdvancedNavigationMode(Settings.NavigationMode) || !EasyHiddenNavTags.Contains(tag);

        public bool IsNavigationTargetVisible(string tag) => IsNavTagVisible(tag);

        private void UpdateNavigationModeButton()
        {
            bool advanced = IsAdvancedNavigationMode(Settings.NavigationMode);
            if (BtnNavModeToggle.Template.FindName("NavModeLbl", BtnNavModeToggle) is System.Windows.Controls.TextBlock lbl)
                lbl.Text = advanced ? "Mode simple" : "Mode avancé";
            if (BtnNavModeToggle.Template.FindName("NavModeIcon", BtnNavModeToggle) is System.Windows.Controls.TextBlock ico)
                ico.Text = advanced ? "\uE7EF" : "\uE713";
        }

        private Button? FindNavButtonByTag(string tag)
        {
            return FindNavButtonRecursive(this, tag);
        }

        private static Button? FindNavButtonRecursive(DependencyObject root, string tag)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Button b && b.Tag is string s && s == tag) return b;
                var found = FindNavButtonRecursive(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        public void NavigateTo(Button btn)
        {
            if (btn.Tag is not string tag || !_pages.ContainsKey(tag)) return;
            if (!IsNavTagVisible(tag)) return;
            if (!btn.IsEnabled) return;

            // Désélectionner l'ancien bouton
            if (_selectedNav != null)
            {
                ApplyNavStyle(_selectedNav, selected: false);
                // Désélectionner l'éventuel groupe parent
                var oldG = GroupOf(_selectedNav.Tag as string ?? "");
                if (oldG != null) ApplyNavStyle(oldG.Header, selected: false);
            }

            // Sélectionner le nouveau
            _selectedNav = btn;
            ApplyNavStyle(btn, selected: true);

            // Si sous-item + groupe replié → surligner le header du groupe
            var g = GroupOf(tag);
            if (g != null && !g.Expanded) ApplyNavStyle(g.Header, selected: true);

            // Titre de la page (supprimer le préfixe "  ·  " des sous-items)
            var raw = btn.Content?.ToString() ?? "";
            TxtPageTitle.Text = raw.TrimStart().TrimStart('·').Trim();

            // Charger et afficher la page (lazy) + transition d'entrée (fondu + glissement)
            MainContent.Content = _pages[tag].Value;
            SetActivityLogVisibilityForPage(tag);
            Helpers.Anim.PageIn(MainContent);
        }

        private void SetActivityLogVisibilityForPage(string tag)
        {
            bool hideBottomLog =
                string.Equals(tag, "Nettoyage", StringComparison.Ordinal) ||
                string.Equals(tag, "Windows",   StringComparison.Ordinal) ||
                string.Equals(tag, "CPU",       StringComparison.Ordinal) ||
                string.Equals(tag, "GpuTuning", StringComparison.Ordinal) ||
                string.Equals(tag, "Reseau",    StringComparison.Ordinal) ||
                string.Equals(tag, "Privacy",   StringComparison.Ordinal);
            ActivityLogPanel.Visibility = hideBottomLog ? Visibility.Collapsed : Visibility.Visible;
            if (!hideBottomLog)
                LogScroll.Visibility = _logVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void ApplyNavStyle(Button btn, bool selected)
        {
            if (selected)
            {
                // Surbrillance qui apparaît en fondu (l'ancien item s'efface en parallèle → effet de glissement)
                var target = (ThemeManager.Brush("ThSelection") as SolidColorBrush)?.Color
                             ?? Color.FromRgb(0x34, 0x40, 0x8A);
                var b = new SolidColorBrush(Color.FromArgb(0, target.R, target.G, target.B));
                btn.Background = b;
                b.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(target, TimeSpan.FromMilliseconds(200)));
                btn.SetResourceReference(ForegroundProperty, "ThTextTitle");
            }
            else
            {
                // Effacement en fondu vers transparent
                if (btn.Background is SolidColorBrush sb)
                {
                    var c = sb.Color;
                    var b = new SolidColorBrush(c);
                    btn.Background = b;
                    b.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromArgb(0, c.R, c.G, c.B), TimeSpan.FromMilliseconds(160)));
                }
                btn.ClearValue(ForegroundProperty);
            }
        }

        // ── Thème clair / sombre ───────────────────────────────────────────────

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
            => ApplyTheme(ThemeManager.Current == ThemeManager.Mode.Dark
                ? ThemeManager.Mode.Light
                : ThemeManager.Mode.Dark);

        private void BtnNavModeToggle_Click(object sender, RoutedEventArgs e)
        {
            bool advanced = IsAdvancedNavigationMode(Settings.NavigationMode);
            ApplyNavigationMode(advanced ? "Easy" : "Advanced");
            Log($"Réglages : mode d'utilisation {(advanced ? "Simple" : "Avancé")} activé.");
        }

        /// <summary>Applique un thème et resynchronise les éléments posés en code.</summary>
        public void ApplyTheme(ThemeManager.Mode mode, bool persist = true)
        {
            ThemeManager.Apply(mode);

            // Persister le choix
            Settings.Theme = mode == ThemeManager.Mode.Dark ? "Dark" : "Light";
            if (persist)
                Settings.Save();

            // Le bouton affiche le mode CIBLE (l'inverse du mode courant)
            // Mode sombre actif → bouton "Mode clair" + soleil. Mode clair → "Mode sombre" + lune.
            bool goingToLight = mode == ThemeManager.Mode.Dark;
            if (BtnTheme.Template.FindName("ThemeIcon", BtnTheme) is System.Windows.Controls.TextBlock ico)
            {
                ico.Text = goingToLight ? "\uE706" : "\uE708";   // E706 soleil, E708 lune (MDL2)
                ico.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                    goingToLight ? "ThWarn" : "ThAccentIcon");
            }
            if (BtnTheme.Template.FindName("ThemeLbl", BtnTheme) is System.Windows.Controls.TextBlock lbl)
                lbl.Text = goingToLight ? "Mode clair" : "Mode sombre";

            // Rafraîchir les couleurs des boutons nav posées en code
            if (_selectedNav != null)
            {
                ApplyNavStyle(_selectedNav, true);
                var g = GroupOf(_selectedNav.Tag as string ?? "");
                if (g != null && !g.Expanded) ApplyNavStyle(g.Header, true);
            }

            if (MainContent.Content is PageMonitoring monitoring)
                monitoring.RefreshThemeVisuals();

            if (MainContent.Content is PageSpecs specs)
                specs.RefreshThemeVisuals();

            _mini?.RefreshThemeVisuals();
        }

        // ── Fenêtre ───────────────────────────────────────────────────────────

        private void FitToWorkArea()
        {
            Helpers.WindowSizing.FitToCurrentWorkArea(
                this,
                desiredWidth: 1180,
                desiredHeight: 820,
                standardMinWidth: 940,
                standardMinHeight: 620,
                widthRatio: 0.90,
                heightRatio: 0.88,
                margin: 12);
        }

        private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && WindowState == WindowState.Normal)
                DragMove();
        }

        // Barre titre pleine largeur : double-clic = agrandir/restaurer, sinon déplacer
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                // Fenêtre agrandie : on la restaure et on suit le curseur
                if (WindowState == WindowState.Maximized)
                {
                    var mouse = PointToScreen(e.GetPosition(this));
                    WindowState = WindowState.Normal;
                    Left = mouse.X - (RestoreBounds.Width / 2);
                    Top  = mouse.Y - 20;
                }
                try { DragMove(); } catch { }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            // Comportement validé avec l'utilisateur (v1.3.0) : le bouton _ envoie dans
            // la tray icon (cache fenêtre + barre des tâches). Fallback si tray indispo.
            if (_tray?.IsAvailable == true) _tray.HideToTray();
            else                            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => ToggleMaximize();

        private void ToggleMaximize()
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        // Marge + glyphe selon l'état (compense l'overhang WindowChrome en plein écran)
        protected override void OnStateChanged(EventArgs e)
        {
            var previousState = _lastWindowState;
            base.OnStateChanged(e);
            _lastWindowState = WindowState;

            if (WindowState == WindowState.Maximized)
            {
                RootGrid.Margin     = new Thickness(0);
                BtnMaximize.Content = "\uE923";   // MDL2 : restaurer
            }
            else
            {
                RootGrid.Margin     = new Thickness(0);
                BtnMaximize.Content = "\uE922";   // MDL2 : agrandir
            }

            TraceWindowRestore($"state {previousState} -> {WindowState}");

            if (!ShuttingDown &&
                previousState == WindowState.Minimized &&
                WindowState != WindowState.Minimized &&
                !_restoreAfterTaskbarQueued)
            {
                _restoreAfterTaskbarQueued = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        TraceWindowRestore("taskbar-restore begin");
                        ShowInTaskbar = true;
                        if (!IsVisible) Show();
                        ForceForeground();
                        TraceWindowRestore("taskbar-restore end");
                    }
                    catch (Exception ex)
                    {
                        Helpers.AppLog.Error("Fenêtre : restauration depuis la barre des tâches", ex);
                    }
                    finally
                    {
                        _restoreAfterTaskbarQueued = false;
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        // ── Plein écran correct pour une fenêtre sans bordure ──────────────────
        // Sans ça, maximiser une fenêtre WindowStyle=None déborde de l'écran (contenu coupé) et
        // recouvre la barre des tâches. On contraint la maximisation à la ZONE DE TRAVAIL du moniteur
        // courant → taille exacte, barre des tâches visible, sur n'importe quel écran/DPI.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);

            // Init tray APRÈS création du HWND de MainWindow (le tray s'y attache,
            // pas de fenêtre supplémentaire). Voir TrayIconManager pour le pourquoi.
            try { _tray = new TrayIconManager(this, hwnd); }
            catch (Exception ex)
            {
                _tray = null;
                Helpers.AppLog.Error("Fenêtre : initialisation de l'icône de notification", ex);
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 0) INSTANCE UNIQUE : une 2e instance Tweakly a tenté de démarrer et broadcaste
            //    WM_TWEAKLY_SHOW (cf. App.OnStartup). On se montre + on revient au 1er plan
            //    pour que l'user voie qu'on est déjà là. Filtre strict sur l'ID enregistré
            //    → aucun risque de réagir à autre chose qu'à un nous-même.
            if (msg != 0 && (uint)msg == App.WM_TWEAKLY_SHOW)
            {
                try { RestoreFromUserRequest(); }
                catch (Exception ex)
                {
                    Helpers.AppLog.Error("Fenêtre : restauration demandée par une seconde instance", ex);
                }
                handled = true;
                return IntPtr.Zero;
            }

            // 1) Messages tray (clic / double-clic / clic droit sur l'icône tray).
            //    Le tray utilise WM_USER+1 comme callback custom.
            if (_tray != null && _tray.TryHandleMessage(msg, lParam))
            {
                handled = true;
                return IntPtr.Zero;
            }

            // 2) WM_GETMINMAXINFO : contraindre la maximisation à la zone de travail.
            if (msg == 0x0024)   // WM_GETMINMAXINFO
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr monitor = MonitorFromWindow(hwnd, 0x00000002);   // MONITOR_DEFAULTTONEAREST
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref mi))
                    {
                        var work = mi.rcWork;
                        var mon  = mi.rcMonitor;
                        mmi.ptMaxPosition.x  = work.left   - mon.left;
                        mmi.ptMaxPosition.y  = work.top    - mon.top;
                        mmi.ptMaxSize.x      = work.right  - work.left;
                        mmi.ptMaxSize.y      = work.bottom - work.top;
                        mmi.ptMaxTrackSize.x = work.right  - work.left;
                        mmi.ptMaxTrackSize.y = work.bottom - work.top;
                        Marshal.StructureToPtr(mmi, lParam, true);
                    }
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // -- Force au premier plan (anti foreground-lock Windows 10/11) -----------
        // Le simple Topmost flicker ne suffit pas si une autre app détient activement
        // le foreground. L'astuce documentée (utilisée par AutoHotkey, Notepad++,
        // PowerToys…) : on attache temporairement notre thread au thread foreground
        // pour bypass la protection LockSetForegroundWindow, on prend le focus,
        // on se détache. C'est la SEULE méthode fiable.

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const int SW_SHOW     = 5;
        private const byte VK_MENU              = 0x12;   // Alt
        private const uint KEYEVENTF_KEYUP       = 0x0002;

        /// <summary>
        /// v1.3.3 : les tentatives faites dans Loaded/ContextIdle arrivaient TROP TÔT
        /// (fenêtre pas encore peinte ; après l'élévation UAC, Windows rend le foreground
        /// à explorer). OnContentRendered = la fenêtre est RÉELLEMENT affichée → on
        /// attaque ici, avec 3 tentatives espacées (0 / 350 / 900 ms) pour couvrir le cas
        /// où une autre app reprend le focus pendant la première seconde.
        /// </summary>
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (App.ShouldStartMinimized(Settings.StartMinimized)) return;

            ForceForeground();
            int attempt = 0;
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350),
            };
            t.Tick += (_, _) =>
            {
                attempt++;
                ForceForeground();
                if (attempt == 1) t.Interval = TimeSpan.FromMilliseconds(550);
                if (attempt >= 2)
                {
                    t.Stop();
                    // Filet final sans minimize/restore : l'ancienne méthode pouvait provoquer
                    // un flash ou une disparition visuelle selon le timing Windows.
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (GetForegroundWindow() != hwnd)
                        {
                            ShowWindow(hwnd, SW_SHOW);
                            BringWindowToTop(hwnd);
                            SetForegroundWindow(hwnd);
                            Activate();
                        }
                    }
                    catch { }
                }
            };
            t.Start();
        }

        /// <summary>
        /// Passation depuis le SPLASH (v1.3.5) : quand le splash se ferme, Windows peut
        /// rendre le foreground à explorer au lieu de MainWindow → l'app « restait dans la
        /// barre des tâches » (régression vécue). Le splash appelle ceci après sa fermeture.
        /// </summary>
        public void EnsureForeground()
        {
            try
            {
                if (App.ShouldStartMinimized(Settings.StartMinimized)) return;
                ForceForeground();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && GetForegroundWindow() != hwnd)
                {
                    ShowWindow(hwnd, SW_SHOW);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    Activate();
                }
            }
            catch { }
        }

        /// <summary>
        /// Force MainWindow au PREMIER PLAN (pas seulement Topmost flicker). Bypass de
        /// la protection foreground-lock de Windows 10/11 via AttachThreadInput.
        /// Inoffensif si la fenêtre est déjà au premier plan (early return).
        /// </summary>
        private void ForceForeground()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var foreHwnd = GetForegroundWindow();
                if (foreHwnd == hwnd) return;   // déjà au premier plan, rien à faire

                uint foreThread = GetWindowThreadProcessId(foreHwnd, out _);
                uint myThread   = GetCurrentThreadId();

                bool attached = false;
                if (foreThread != 0 && foreThread != myThread)
                {
                    attached = AttachThreadInput(foreThread, myThread, true);
                }

                try
                {
                    // TRICK "ALT FANTÔME" (KB Q97925, utilisé par PowerToys/AutoHotkey) :
                    // simuler un appui Alt invisible légitimise notre process aux yeux de
                    // Windows → SetForegroundWindow est ensuite accepté. C'est LE bypass
                    // documenté de la protection LockSetForegroundWindow Win10/11.
                    try
                    {
                        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);                // Alt down
                        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);  // Alt up
                    }
                    catch { }

                    // Si la fenêtre était minimisée, la restaurer avant de la passer devant.
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    ShowWindow(hwnd, SW_SHOW);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    Activate();
                }
                finally
                {
                    if (attached) AttachThreadInput(foreThread, myThread, false);
                }
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Error("Fenêtre : RestoreFromUserRequest", ex);
            }
        }

        /// <summary>
        /// Restaure la fenêtre suite à une action explicite utilisateur (barre des tâches,
        /// 2e lancement, tray). Contrairement à EnsureForeground(), cette méthode ignore le
        /// flag de démarrage minimisé : après un clic utilisateur, Tweakly doit réapparaître.
        /// </summary>
        public void RestoreFromUserRequest()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RestoreFromUserRequest));
                return;
            }

            try
            {
                TraceWindowRestore("user-restore begin");
                if (IsMiniActive())
                {
                    ExitMiniMode();
                    TraceWindowRestore("user-restore mini-exit");
                    return;
                }

                ShowInTaskbar = true;
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                ForceForeground();
                TraceWindowRestore("user-restore end");
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Error("Fenêtre : restauration après une action utilisateur", ex);
            }
        }

        private void TraceWindowRestore(string step)
        {
            try
            {
                Helpers.AppLog.Write(
                    $"Fenetre : {step} | state={WindowState} | visible={IsVisible} | taskbar={ShowInTaskbar} | active={IsActive} | startup={App.LaunchedAtStartup} | afterUpdate={App.LaunchedAfterUpdate}");
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            ShuttingDown = true;
            Application.Current.Shutdown();
        }

        /// <summary>
        /// Demande la fermeture propre de l'app (utilisé par le menu « Fermer Tweakly »
        /// de la tray icon). Pose ShuttingDown pour que la mini ne capture pas le Closing.
        /// </summary>
        public void RequestShutdown()
        {
            ShuttingDown = true;
            try { _tray?.Dispose(); } catch { }
            Application.Current.Shutdown();
        }

        /// <summary>True si l'overlay mini est actuellement affiché (utilisé par le tray
        /// pour décider entre Show() main vs ExitMiniMode()).</summary>
        public bool IsMiniActive() => _mini != null && _mini.IsVisible;

        /// <summary>
        /// Autorise le monitoring live uniquement quand l'utilisateur regarde vraiment Tweakly.
        /// Si la fenêtre est minimisée, cachée ou passée derrière un jeu, on ne sonde pas WMI/NvAPI.
        /// Le mode mini garde son propre sampler léger, donc il est exclu ici.
        /// </summary>
        public bool IsLiveSamplingAllowed()
        {
            try { return IsVisible && WindowState != WindowState.Minimized && IsActive && !IsMiniActive(); }
            catch { return false; }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            ShuttingDown = true;
            try
            {
                if (_pages.TryGetValue("Battery", out var batteryPage)
                    && batteryPage.IsValueCreated
                    && batteryPage.Value is PageBatteryCalibration battery)
                    battery.PrepareForAppShutdown();

                if (_pages.TryGetValue("WinRepair", out var repairPage)
                    && repairPage.IsValueCreated
                    && repairPage.Value is PageWindowsRepair repair)
                    repair.CancelForAppShutdown();
            }
            catch { }
            // Retire la tray icon AVANT la fermeture (sinon elle reste affichée comme
            // fantôme jusqu'à un survol souris).
            try { _tray?.Dispose(); } catch { }
            base.OnClosing(e);
        }

        // ── Mode mini (overlay compact monitoring) ────────────────────────────

        private void BtnMiniMode_Click(object sender, RoutedEventArgs e) => EnterMiniMode();

        /// <summary>
        /// Bascule vers l'overlay compact. Vide d'abord la page active (son timer de
        /// sampling s'arrête via Unloaded) pour qu'un SEUL sampler tourne : celui de la mini.
        /// </summary>
        public void EnterMiniMode()
        {
            try
            {
                _returnNav = _selectedNav;
                MainContent.Content = null;   // décharge la page active → stoppe son timer

                _mini ??= new MiniMonitor(this);
                PositionMini(_mini);
                _mini.Show();
                _mini.StartSampling();
                _mini.Activate();
                Hide();
            }
            catch (Exception ex) { Log($"Mode mini : erreur — {ex.Message}"); }
        }

        /// <summary>Revient à la fenêtre complète et restaure la page d'où l'on venait.</summary>
        public void ExitMiniMode()
        {
            try
            {
                _mini?.StopSampling();
                _mini?.Hide();

                Show();
                WindowState = WindowState.Normal;
                Activate();

                if (_returnNav != null) NavigateTo(_returnNav);
            }
            catch (Exception ex) { Log($"Mode mini : erreur — {ex.Message}"); }
        }

        // Place la mini en bas à droite de la zone de travail du MÊME moniteur que MainWindow.
        // ⚠️ SystemParameters.WorkArea = écran PRIMAIRE → la mini saute sur l'écran de gauche si la
        // main est sur l'écran de droite. On résout via MonitorFromWindow + GetMonitorInfo (P/Invoke
        // déjà présents pour WM_GETMINMAXINFO).
        private void PositionMini(Window w)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr mon = MonitorFromWindow(hwnd, 0x00000002 /*MONITOR_DEFAULTTONEAREST*/);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                    {
                        // RECT en pixels → conversion en unités WPF (DIP) via le DPI du moniteur courant
                        var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                        double right  = mi.rcWork.right  / sx;
                        double bottom = mi.rcWork.bottom / sy;
                        w.Left = right  - w.Width  - 18;
                        w.Top  = bottom - 220       - 18;
                        return;
                    }
                }
                // Fallback : écran primaire (comportement historique)
                var wa = SystemParameters.WorkArea;
                w.Left = wa.Right  - w.Width  - 18;
                w.Top  = wa.Bottom - 220       - 18;
            }
            catch { }
        }

        // ── Journal d'activité ────────────────────────────────────────────────

        private bool _logVisible = false;   // replié par défaut

        private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            _logVisible = !_logVisible;
            LogScroll.Visibility = _logVisible ? Visibility.Visible : Visibility.Collapsed;
            // ▾ = chevron bas (replier) ; ▴ = chevron haut (déployer)
            BtnToggleLog.Content = _logVisible ? "▾" : "▴";
        }

        public void Log(string message)
        {
            // v1.3.3 : tout message du journal d'activité est AUSSI persisté dans le
            // journal fichier local (Helpers/AppLog, config\tweakly-log.txt) — permet
            // le diagnostic a posteriori sans rien envoyer nulle part.
            Helpers.AppLog.Write(message);
            Dispatcher.BeginInvoke(() =>
            {
                var ts = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.Text += $"[{ts}] {message}\n";
                LogScroll.ScrollToBottom();
            });
        }
    }
}
