using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Optimisation_Tool.Pages;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool
{
    public partial class MainWindow : Window
    {
        private Button?                                  _selectedNav;
        private bool                                     _optExpanded = false;
        private readonly HashSet<string>                 _subTags     = new() { "Nettoyage", "Nvidia", "CPU", "Windows", "Reseau" };
        private readonly Dictionary<string, Lazy<UserControl>> _pages;

        // Settings persistants
        public static AppSettings Settings { get; private set; } = new();

        public MainWindow()
        {
            InitializeComponent();

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
                ["Reglages"]  = new Lazy<UserControl>(() => new PageReglages(this)),
            };

            Loaded += (_, _) =>
            {
                // Charger + appliquer les settings sauvegardés
                Settings = AppSettings.Load();
                var mode = Settings.Theme == "Light" ? ThemeManager.Mode.Light : ThemeManager.Mode.Dark;
                ApplyTheme(mode);

                // Version affichée = source unique (PageReglages.AppVersion)
                TxtVersion.Text = "v" + Pages.PageReglages.AppVersion;

                Log("Outil prêt.");

                // Démarrage : groupe Optimisations replié (état par défaut du XAML) + page Monitoring active
                NavigateTo(BtnNavMonitoring);

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
                var (hasUpdate, tag, _, assetUrl) = await PageReglages.CheckForUpdateAsync();
                if (hasUpdate && !string.IsNullOrEmpty(assetUrl))
                {
                    Log($"Mise à jour disponible : {tag}.");
                    StartUpdate(assetUrl, tag);   // overlay + téléchargement direct
                }
            }
            catch (Exception ex) { Log($"MAJ auto : erreur — {ex.Message}"); }
        }

        /// <summary>Affiche l'overlay, télécharge la MAJ avec progression, puis propose CONTINUER.</summary>
        public async void StartUpdate(string assetUrl, string tag)
        {
            UpdateOverlay.Visibility  = Visibility.Visible;
            TxtUpdTitle.Text          = $"Mise à jour {tag}";
            TxtUpdStatus.Text         = "Téléchargement de la mise à jour…";
            BtnUpdContinue.Visibility = Visibility.Collapsed;
            SetUpdateBar(0);

            try
            {
                var progress = new Progress<double>(SetUpdateBar);
                _pendingBat = await PageReglages.PrepareUpdateAsync(assetUrl, progress);

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

        // Groupe Optimisations — expansion / repli
        private void BtnNavOptGroup_Click(object sender, RoutedEventArgs e)
        {
            _optExpanded = !_optExpanded;
            OptGroupPanel.Visibility = _optExpanded ? Visibility.Visible : Visibility.Collapsed;
            BtnNavOptGroup.Content   = _optExpanded ? "▾  Optimisations" : "▸  Optimisations";

            // Surligner le groupe si un sous-item est actif ET que le groupe est replié
            bool subActive = _selectedNav != null && _subTags.Contains(_selectedNav.Tag as string ?? "");
            ApplyNavStyle(BtnNavOptGroup, selected: subActive && !_optExpanded);
        }

        public void NavigateTo(Button btn)
        {
            if (btn.Tag is not string tag || !_pages.ContainsKey(tag)) return;

            // Désélectionner l'ancien bouton
            if (_selectedNav != null)
            {
                ApplyNavStyle(_selectedNav, selected: false);
                // Désélectionner le groupe si on quittait un sous-item
                if (_subTags.Contains(_selectedNav.Tag as string ?? ""))
                    ApplyNavStyle(BtnNavOptGroup, selected: false);
            }

            // Sélectionner le nouveau
            _selectedNav = btn;
            ApplyNavStyle(btn, selected: true);

            // Si sous-item + groupe replié → surligner le header du groupe
            if (_subTags.Contains(tag) && !_optExpanded)
                ApplyNavStyle(BtnNavOptGroup, selected: true);

            // Titre de la page (supprimer le préfixe "  ·  " des sous-items)
            var raw = btn.Content?.ToString() ?? "";
            TxtPageTitle.Text = raw.TrimStart().TrimStart('·').Trim();

            // Charger et afficher la page (lazy)
            MainContent.Content = _pages[tag].Value;
        }

        private static void ApplyNavStyle(Button btn, bool selected)
        {
            if (selected)
            {
                btn.Background = ThemeManager.Brush("ThSelection");
                btn.Foreground = ThemeManager.Brush("ThTextTitle");
            }
            else
            {
                // Retour au style par défaut (Transparent + DynamicResource ThTextNav)
                btn.ClearValue(BackgroundProperty);
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
                if (_subTags.Contains(_selectedNav.Tag as string ?? "") && !_optExpanded)
                    ApplyNavStyle(BtnNavOptGroup, true);
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
            => WindowState = WindowState.Minimized;

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
                RootGrid.Margin     = new Thickness(7);
                BtnMaximize.Content = "\uE923";   // MDL2 : restaurer
            }
            else
            {
                RootGrid.Margin     = new Thickness(0);
                BtnMaximize.Content = "\uE922";   // MDL2 : agrandir
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Application.Current.Shutdown();

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
