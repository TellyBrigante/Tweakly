using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageReglages : UserControl
    {
        private readonly MainWindow _main;

        // Source unique de la version + dépôt GitHub
        public const string AppVersion = "1.0.0";
        private const string RepoOwner = "TellyBrigante";
        private const string RepoName  = "Tweakly";
        private static readonly string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";

        private string _updateUrl = "";

        private static readonly HttpClient Http = new();

        public PageReglages(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            TxtVersionLine.Text   = $"Version installée : Tweakly v{AppVersion}";
            ChkStartup.IsChecked  = IsStartupEnabled();
            ChkAutoUpdate.IsChecked = MainWindow.Settings.AutoUpdate;
            UpdateThemeSegment(ThemeManager.Current);
        }

        // ── Toggle MAJ automatiques ─────────────────────────────────────────────

        private void ChkAutoUpdate_Changed(object sender, RoutedEventArgs e)
        {
            MainWindow.Settings.AutoUpdate = ChkAutoUpdate.IsChecked == true;
            MainWindow.Settings.Save();
        }

        // ── Check de MAJ (appelé au démarrage + manuellement) ──────────────────

        public static async Task<(bool hasUpdate, string tag, string url)> CheckForUpdateAsync()
        {
            try
            {
                var apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.UserAgent.ParseAdd("Tweakly-Updater");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return (false, "", "");

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tag  = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var url  = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;

                var remote = ParseVersion(tag);
                var local  = ParseVersion(AppVersion);

                if (remote != null && local != null && remote > local)
                    return (true, tag, url);
            }
            catch { }
            return (false, "", "");
        }

        // Colore le segment actif (fond accent) et grise l'inactif
        private void UpdateThemeSegment(ThemeManager.Mode mode)
        {
            StyleSegment(BtnThemeDark,  mode == ThemeManager.Mode.Dark);
            StyleSegment(BtnThemeLight, mode == ThemeManager.Mode.Light);
        }

        private static void StyleSegment(Button btn, bool active)
        {
            if (btn.Template.FindName("Bg",  btn) is not Border bg)  return;
            if (btn.Template.FindName("Lbl", btn) is not TextBlock lbl) return;

            bg.Background  = active
                ? new SolidColorBrush(Color.FromRgb(0x25, 0x4E, 0x8C))
                : new SolidColorBrush(Colors.Transparent);

            lbl.Foreground = active
                ? new SolidColorBrush(Colors.White)
                : ThemeManager.Brush("ThTextDim");
        }

        // ── Mises à jour (GitHub Releases) ──────────────────────────────────────

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled     = false;
            BtnDownloadUpdate.Visibility = Visibility.Collapsed;
            TxtUpdateStatus.Text         = "Vérification en cours…";
            _main.Log("Réglages : vérification des mises à jour…");

            try
            {
                var (hasUpdate, tag, url) = await CheckForUpdateAsync();

                if (hasUpdate)
                {
                    _updateUrl           = url;
                    TxtUpdateStatus.Text = $"Mise à jour disponible : {tag}  —  vous avez v{AppVersion}";
                    BtnDownloadUpdate.Visibility = Visibility.Visible;
                    _main.Log($"Réglages : mise à jour disponible — {tag}.");
                }
                else
                {
                    TxtUpdateStatus.Text = $"Tweakly est à jour (v{AppVersion}).";
                    _main.Log("Réglages : déjà à jour.");
                }
            }
            catch (Exception ex)
            {
                TxtUpdateStatus.Text = $"Impossible de vérifier. {ex.Message}";
                _main.Log($"Réglages : erreur vérification MAJ — {ex.Message}");
            }
            finally { BtnCheckUpdate.IsEnabled = true; }
        }

        private void BtnDownloadUpdate_Click(object sender, RoutedEventArgs e)
            => OpenUrl(string.IsNullOrEmpty(_updateUrl) ? RepoUrl : _updateUrl);

        private static Version? ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            tag = tag.TrimStart('v', 'V').Trim();
            return Version.TryParse(tag, out var v) ? v : null;
        }

        // ── Démarrage avec Windows ─────────────────────────────────────────────

        private const string RunKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "Tweakly";

        private static bool IsStartupEnabled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
        {
            bool enable = ChkStartup.IsChecked == true;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (k == null) return;
                if (enable)
                    k.SetValue(RunValue, $"\"{Process.GetCurrentProcess().MainModule!.FileName}\"");
                else
                    k.DeleteValue(RunValue, throwOnMissingValue: false);

                _main.Log($"Réglages : démarrage Windows {(enable ? "activé" : "désactivé")}.");
            }
            catch (Exception ex)
            {
                _main.Log($"Réglages : erreur démarrage Windows — {ex.Message}");
            }
        }

        // ── Raccourci bureau ────────────────────────────────────────────────────

        private void BtnShortcut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe     = Process.GetCurrentProcess().MainModule!.FileName;
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var lnk     = Path.Combine(desktop, "Tweakly.lnk");

                // WScript.Shell COM — disponible sur toutes les versions Windows
                var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic sc    = shell.CreateShortcut(lnk);
                sc.TargetPath        = exe;
                sc.WorkingDirectory  = Path.GetDirectoryName(exe);
                sc.Description       = "Tweakly — Optimisation Windows";
                sc.IconLocation      = $"{exe},0";
                sc.Save();

                _main.Log($"Réglages : raccourci créé → {lnk}");
                BtnShortcut.Content = "✓  Raccourci créé";
                BtnShortcut.IsEnabled = false;
            }
            catch (Exception ex)
            {
                _main.Log($"Réglages : erreur raccourci — {ex.Message}");
            }
        }

        // ── Apparence ───────────────────────────────────────────────────────────

        private void BtnThemeDark_Click(object sender, RoutedEventArgs e)
        {
            _main.ApplyTheme(ThemeManager.Mode.Dark);
            UpdateThemeSegment(ThemeManager.Mode.Dark);
        }

        private void BtnThemeLight_Click(object sender, RoutedEventArgs e)
        {
            _main.ApplyTheme(ThemeManager.Mode.Light);
            UpdateThemeSegment(ThemeManager.Mode.Light);
        }

        // ── Liens & dossiers ────────────────────────────────────────────────────

        private void BtnGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); }
            catch (Exception ex) { _main.Log($"Réglages : erreur ouverture dossier — {ex.Message}"); }
        }

        private void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _main.Log($"Réglages : erreur ouverture lien — {ex.Message}"); }
        }
    }
}
