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

        // Tray icon (zone de notification Windows) — gérée par TrayIconManager.
        // Null si l'init a échoué (cas rare : RDP sans tray, etc.) → on retombe sur le
        // comportement standard (WindowState.Minimized vers la barre des tâches).
        private TrayIconManager? _tray;

        // Groupes de navigation repliables (Optimisations, Diagnostic)
        private sealed class NavGroup
        {
            public Button Header = null!;
            public Border Panel  = null!;
            public HashSet<string> Tags = new();
            public string Label  = "";
            public bool   Expanded;
        }
        private List<NavGroup> _groups = new();

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
                ["Nettoyage"] = new Lazy<UserControl>(() => new PageNettoyage(this)),
                ["Nvidia"]    = new Lazy<UserControl>(() => new PageNvidia(this)),
                ["CPU"]       = new Lazy<UserControl>(() => new PageCPU(this)),
                ["Windows"]   = new Lazy<UserControl>(() => new PageWindows(this)),
                ["Reseau"]    = new Lazy<UserControl>(() => new PageReseau(this)),
                ["Privacy"]   = new Lazy<UserControl>(() => new PagePrivacy(this)),
                ["Apps"]      = new Lazy<UserControl>(() => new PageApps(this)),
                ["Fresh"]     = new Lazy<UserControl>(() => new PageFresh(this)),
                ["Info"]      = new Lazy<UserControl>(() => new PageSpecs(this)),
                ["Monitoring"]= new Lazy<UserControl>(() => new PageMonitoring(this)),
                ["ReseauMon"] = new Lazy<UserControl>(() => new PageReseauMonitoring(this)),
                ["Diagnostic"]= new Lazy<UserControl>(() => new PageDiagnostic(this)),
                ["EventLog"]  = new Lazy<UserControl>(() => new PageEventLog(this)),
                ["Benchmark"] = new Lazy<UserControl>(() => new PageBenchmark(this)),
                ["Reglages"]  = new Lazy<UserControl>(() => new PageReglages(this)),
            };

            _groups = new List<NavGroup>
            {
                new NavGroup { Header = BtnNavOptGroup,  Panel = OptGroupPanel,  Label = "Optimisations",
                               Tags = new HashSet<string> { "Nettoyage", "Nvidia", "CPU", "Windows", "Reseau", "Privacy" } },
                new NavGroup { Header = BtnNavDiagGroup, Panel = DiagGroupPanel, Label = "Diagnostic",
                               Tags = new HashSet<string> { "Diagnostic", "EventLog" } },
                new NavGroup { Header = BtnNavToolsGroup, Panel = ToolsGroupPanel, Label = "Boîte à outils",
                               Tags = new HashSet<string> { "Apps", "Fresh" } },
                new NavGroup { Header = BtnNavMonGroup,   Panel = MonGroupPanel,   Label = "Surveillance",
                               Tags = new HashSet<string> { "Monitoring", "ReseauMon" } },
            };

            Loaded += (_, _) =>
            {
                // Migration silencieuse vers le layout v1.2.8 (config\ + data\<sous-dossiers>\).
                // DOIT être appelée AVANT tout helper qui touche aux fichiers. Idempotente, blindée.
                try { Helpers.PathLayout.MigrateIfNeeded(); } catch { }

                // Charger + appliquer les settings sauvegardés
                Settings = AppSettings.Load();
                Helpers.UiSound.Enabled = Settings.SoundsEnabled;
                Helpers.CpuTemperature.Enabled = Settings.CpuTempEnabled;
                var mode = Settings.Theme == "Light" ? ThemeManager.Mode.Light : ThemeManager.Mode.Dark;
                ApplyTheme(mode);
                // Tray déjà initialisé dans le ctor (voir commentaire là-bas). Ici on
                // applique juste « démarrer minimisé » : si tray dispo → on cache fenêtre
                // + barre des tâches (seule la tray icon reste visible). Sinon fallback :
                // minimisation classique.
                if (Settings.StartMinimized)
                {
                    if (_tray?.IsAvailable == true) _tray.HideToTray();
                    else                            this.WindowState = WindowState.Minimized;
                }

                // Version affichée = source unique (PageReglages.AppVersion)
                TxtVersion.Text = "v" + Pages.PageReglages.AppVersion;

                Log("Outil prêt.");

                // Onglet Nvidia verrouillé si aucune carte Nvidia (PC en IGP / AMD seul) → pas d'éditeur
                // de pilote Nvidia inutile. Fail-open : en cas de doute, l'onglet reste accessible.
                try
                {
                    if (Helpers.SystemMonitor.ShouldLockNvidiaTab())
                    {
                        BtnNavNvidia.IsEnabled = false;
                        BtnNavNvidia.ToolTip   = "Aucune carte graphique Nvidia détectée sur ce PC.";
                        // Indicateur visuel explicite : préfixe « 🔒 » + suffixe « (indisponible) »
                        // → l'utilisateur comprend que c'est verrouillé et pourquoi, pas juste « grisé ».
                        BtnNavNvidia.Content   = "🔒  Nvidia  (indisponible)";
                        BtnNavNvidia.Opacity   = 0.55;
                    }
                }
                catch { }

                // Démarrage : page d'accueil = Tweakly Score (Benchmark)
                NavigateTo(BtnNavBenchmark);

                // Forcer la fenêtre au PREMIER PLAN (pas seulement Topmost flicker, qui ne
                // suffit pas si une autre app détient le foreground). On utilise
                // AttachThreadInput pour bypass la protection Win10/11. Voir ForceForeground.
                if (!Settings.StartMinimized)
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

        // ── Mise à jour : overlay plein écran ─────────────────────────────────

        private string? _pendingBat;
        private System.Windows.Threading.DispatcherTimer? _updateWatcher;

        private async Task CheckUpdateSilentAsync()
        {
            try
            {
                await Task.Delay(2500);   // laisser l'UI charger d'abord
                await TryOfferUpdateAsync();
            }
            catch (Exception ex) { Log($"MAJ auto : erreur — {ex.Message}"); }
        }

        // Vérification périodique « en direct » pendant que l'app reste ouverte
        private void StartUpdateWatcher()
        {
            _updateWatcher = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(30)
            };
            _updateWatcher.Tick += async (_, _) => await TryOfferUpdateAsync();
            _updateWatcher.Start();
        }

        private async Task TryOfferUpdateAsync()
        {
            if (!Settings.AutoUpdate) return;
            if (UpdateOverlay.Visibility == Visibility.Visible) return; // déjà en cours
            try
            {
                var (hasUpdate, tag, _, assetUrl, sha256) = await PageReglages.CheckForUpdateAsync();
                if (hasUpdate && !string.IsNullOrEmpty(assetUrl))
                {
                    Log($"Mise à jour disponible : {tag}.");
                    StartUpdate(assetUrl, tag, sha256);   // overlay + téléchargement direct
                }
            }
            catch (Exception ex) { Log($"MAJ auto : erreur — {ex.Message}"); }
        }

        /// <summary>Affiche l'overlay, télécharge la MAJ avec progression, puis propose CONTINUER.
        /// sha256 (optionnel) : hash publié dans le body de la release — vérifié après téléchargement,
        /// mismatch = échec propre (aucun fichier remplacé). Vide = pas de vérification (compat).</summary>
        public async void StartUpdate(string assetUrl, string tag, string sha256 = "")
        {
            UpdateOverlay.Visibility  = Visibility.Visible;
            TxtUpdTitle.Text          = $"Mise à jour {tag}";
            TxtUpdStatus.Text         = "Téléchargement de la mise à jour…";
            BtnUpdContinue.Visibility = Visibility.Collapsed;
            SetUpdateBar(0);

            try
            {
                var progress = new Progress<double>(SetUpdateBar);
                _pendingBat = await PageReglages.PrepareUpdateAsync(assetUrl, progress, sha256);

                SetUpdateBar(100);
                TxtUpdStatus.Text         = "Téléchargement terminé. Clique sur CONTINUER pour redémarrer.";
                BtnUpdContinue.Visibility = Visibility.Visible;
                Log($"Mise à jour {tag} téléchargée — en attente de redémarrage.");
            }
            catch (Exception ex)
            {
                TxtUpdStatus.Text = $"Échec du téléchargement : {ex.Message}";
                Log($"MAJ : erreur téléchargement — {ex.Message}");
            }
        }

        private void SetUpdateBar(double pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            UpdBar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
            UpdBar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            TxtUpdPct.Text = $"{pct:F0} %";
        }

        private void BtnUpdContinue_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingBat))
                PageReglages.LaunchUpdaterAndExit(_pendingBat);
        }

        // ── Barre de progression dans la barre des tâches Windows ─────────────

        public void SetTaskbarProgress(double pct)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                TaskbarInfo.ProgressValue = Math.Max(0, Math.Min(1, pct / 100.0));
            });
        }

        public void ClearTaskbarProgress()
        {
            Dispatcher.BeginInvoke(() =>
            {
                TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                TaskbarInfo.ProgressValue = 0;
            });
        }

        // ── Navigation ────────────────────────────────────────────────────────

        private void NavBtn_Click(object sender, RoutedEventArgs e)
            => NavigateTo((Button)sender);

        // Groupes repliables (Optimisations, Diagnostic) — même mécanisme pour les deux
        private void BtnNavOptGroup_Click(object sender, RoutedEventArgs e)   => ToggleGroup((Button)sender);
        private void BtnNavDiagGroup_Click(object sender, RoutedEventArgs e)  => ToggleGroup((Button)sender);
        private void BtnNavToolsGroup_Click(object sender, RoutedEventArgs e) => ToggleGroup((Button)sender);
        private void BtnNavMonGroup_Click(object sender, RoutedEventArgs e)   => ToggleGroup((Button)sender);

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

        public void NavigateTo(Button btn)
        {
            if (btn.Tag is not string tag || !_pages.ContainsKey(tag)) return;

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
            Helpers.Anim.PageIn(MainContent);
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
                btn.Foreground = ThemeManager.Brush("ThTextTitle");
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

        /// <summary>Applique un thème et resynchronise les éléments posés en code.</summary>
        public void ApplyTheme(ThemeManager.Mode mode)
        {
            ThemeManager.Apply(mode);

            // Persister le choix
            Settings.Theme = mode == ThemeManager.Mode.Dark ? "Dark" : "Light";
            Settings.Save();

            // Le bouton affiche le mode CIBLE (l'inverse du mode courant)
            // Mode sombre actif → bouton "Mode clair" + soleil. Mode clair → "Mode sombre" + lune.
            bool goingToLight = mode == ThemeManager.Mode.Dark;
            if (BtnTheme.Template.FindName("ThemeIcon", BtnTheme) is System.Windows.Controls.TextBlock ico)
            {
                ico.Text = goingToLight ? "\uE706" : "\uE708";   // E706 soleil, E708 lune (MDL2)
                ico.Foreground = new SolidColorBrush(goingToLight
                    ? Color.FromRgb(0xF5, 0xC2, 0x4A)            // soleil doré
                    : Color.FromRgb(0xAE, 0xB8, 0xE8));          // lune bleu clair
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
        }

        // ── Fenêtre ───────────────────────────────────────────────────────────

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
            base.OnStateChanged(e);
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
            try { _tray = new TrayIconManager(this, hwnd); } catch { _tray = null; }
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
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
        private const int SW_SHOW    = 5;
        private const int SW_RESTORE = 9;
        private const byte VK_MENU              = 0x12;   // Alt
        private const uint KEYEVENTF_KEYUP       = 0x0002;

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

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            ShuttingDown = true;
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
            Dispatcher.BeginInvoke(() =>
            {
                var ts = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.Text += $"[{ts}] {message}\n";
                LogScroll.ScrollToBottom();
            });
        }
    }
}
