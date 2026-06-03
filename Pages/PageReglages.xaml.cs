using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        private bool _loading = false;

        // Source unique de la version + dépôt GitHub
        public const string AppVersion = "1.1.9";
        private const string RepoOwner = "TellyBrigante";
        private const string RepoName  = "Tweakly";
        private static readonly string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";

        private string _updateUrl = "";   // page de la release (fallback)
        private string _assetUrl  = "";   // .zip à télécharger
        private string _lastTag   = "";   // tag de la dernière MAJ détectée

        private static readonly HttpClient Http = new();

        public PageReglages(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _loading = true;
            TxtVersionLine.Text        = $"Version installée : Tweakly v{AppVersion}";
            ChkStartup.IsChecked       = IsStartupEnabled();
            ChkStartMinimized.IsChecked = MainWindow.Settings.StartMinimized;
            ChkAutoUpdate.IsChecked    = MainWindow.Settings.AutoUpdate;
            ChkSounds.IsChecked        = MainWindow.Settings.SoundsEnabled;
            UpdateThemeSegment(ThemeManager.Current);
            _loading = false;
        }

        // ── Toggle MAJ automatiques ─────────────────────────────────────────────

        private void ChkAutoUpdate_Changed(object sender, RoutedEventArgs e)
        {
            MainWindow.Settings.AutoUpdate = ChkAutoUpdate.IsChecked == true;
            MainWindow.Settings.Save();
        }

        // ── Toggle sons d'interface ─────────────────────────────────────────────

        private void ChkSounds_Changed(object sender, RoutedEventArgs e)
        {
            MainWindow.Settings.SoundsEnabled = ChkSounds.IsChecked == true;
            MainWindow.Settings.Save();
            Helpers.UiSound.Enabled = MainWindow.Settings.SoundsEnabled;
        }

        // ── Check de MAJ (appelé au démarrage + manuellement) ──────────────────

        public static async Task<(bool hasUpdate, string tag, string url, string assetUrl)> CheckForUpdateAsync()
        {
            try
            {
                var apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.UserAgent.ParseAdd("Tweakly-Updater");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return (false, "", "", "");

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var url = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;

                // Trouver le 1er asset .zip
                var assetUrl = "";
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            a.TryGetProperty("browser_download_url", out var d))
                        {
                            assetUrl = d.GetString() ?? "";
                            break;
                        }
                    }
                }

                var remote = ParseVersion(tag);
                var local  = ParseVersion(AppVersion);

                if (remote != null && local != null && remote > local)
                    return (true, tag, url, assetUrl);
            }
            catch { }
            return (false, "", "", "");
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
                var (hasUpdate, tag, url, assetUrl) = await CheckForUpdateAsync();

                if (hasUpdate)
                {
                    _updateUrl = url;
                    _assetUrl  = assetUrl;
                    _lastTag   = tag;
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
        {
            // Pas d'asset .zip → fallback navigateur
            if (string.IsNullOrEmpty(_assetUrl))
            {
                OpenUrl(string.IsNullOrEmpty(_updateUrl) ? RepoUrl : _updateUrl);
                return;
            }
            // Délègue à l'overlay plein écran de MainWindow
            _main.StartUpdate(_assetUrl, _lastTag);
        }

        /// <summary>
        /// Télécharge le ZIP (avec progression %), l'extrait et écrit le script de
        /// remplacement. Ne redémarre PAS — retourne le chemin du script à lancer.
        /// </summary>
        public static async Task<string> PrepareUpdateAsync(string assetUrl, IProgress<double> progress)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "Tweakly_update");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var zipPath = Path.Combine(tmp, "update.zip");

            // Téléchargement avec progression
            using (var req = new HttpRequestMessage(HttpMethod.Get, assetUrl))
            {
                req.Headers.UserAgent.ParseAdd("Tweakly-Updater");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var fs = File.Create(zipPath);
                var buffer = new byte[81920];
                long readTotal = 0; int n;
                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, n);
                    readTotal += n;
                    if (total > 0) progress.Report((double)readTotal / total * 100.0);
                }
                progress.Report(100);
            }

            // Extraction
            var extractDir = Path.Combine(tmp, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            var srcDir = FindExeDir(extractDir) ?? throw new Exception("Tweakly.exe introuvable dans l'archive.");

            var exePath    = Process.GetCurrentProcess().MainModule!.FileName;
            var installDir = Path.GetDirectoryName(exePath)!;

            // Script : attend la fermeture, remplace les fichiers, relance
            var bat = Path.Combine(tmp, "update.bat");
            var script =
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /fi \"imagename eq Tweakly.exe\" 2>nul | find /i \"Tweakly.exe\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                $"xcopy /E /Y /I \"{srcDir}\\*\" \"{installDir}\\\" >nul\r\n" +
                $"start \"\" \"{exePath}\"\r\n" +
                "del \"%~f0\"\r\n";
            File.WriteAllText(bat, script);
            return bat;
        }

        /// <summary>Lance le script de mise à jour (caché) et ferme l'application.</summary>
        public static void LaunchUpdaterAndExit(string batPath)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
            Application.Current.Shutdown();
        }

        // Cherche récursivement le dossier contenant Tweakly.exe
        private static string? FindExeDir(string root)
        {
            try
            {
                var exe = Directory.GetFiles(root, "Tweakly.exe", SearchOption.AllDirectories).FirstOrDefault();
                return exe != null ? Path.GetDirectoryName(exe) : null;
            }
            catch { return null; }
        }

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
            if (_loading) return;
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

        private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            MainWindow.Settings.StartMinimized = ChkStartMinimized.IsChecked == true;
            MainWindow.Settings.Save();
            _main.Log($"Réglages : démarrage minimisé {(MainWindow.Settings.StartMinimized ? "activé" : "désactivé")}.");
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
