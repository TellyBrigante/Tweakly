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
        public const string AppVersion = "1.4.7";
        private const string RepoOwner = "TellyBrigante";
        private const string RepoName  = "Tweakly";
        private static readonly string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";

        private string _updateUrl = "";   // page de la release (fallback)
        private string _assetUrl  = "";   // .zip à télécharger
        private string _lastTag   = "";   // tag de la dernière MAJ détectée
        private string _sha256    = "";   // hash SHA-256 publié dans le body (OBLIGATOIRE depuis v1.3.4)
        private string _notes     = "";   // patch note de la release (affiché dans l'overlay)

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
            ChkCpuTemp.IsChecked       = MainWindow.Settings.CpuTempEnabled;
            UpdateNavigationModeSegment(MainWindow.Settings.NavigationMode);
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

        // ── Toggle température CPU (opt-in) ─────────────────────────────────────
        // À l'activation : on s'assure que le pilote PawnIO est présent, sinon on l'installe via
        // l'installeur officiel signé bundlé (en silence — l'app est déjà admin, donc AUCUN prompt
        // UAC). À la désactivation : on arrête juste la lecture, on NE désinstalle PAS le pilote
        // (évite le cycle "service marqué pour suppression / reboot").
        private async void ChkCpuTemp_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = ChkCpuTemp.IsChecked == true;

            if (!on)
            {
                MainWindow.Settings.CpuTempEnabled = false;
                MainWindow.Settings.Save();
                Helpers.CpuTemperature.Enabled = false;
                TxtCpuTempStatus.Text = "";
                _main.Log("Réglages : température CPU désactivée (pilote conservé).");
                return;
            }

            // Activation : installer le pilote au besoin
            ChkCpuTemp.IsEnabled  = false;
            TxtCpuTempStatus.Text = "Installation du pilote de capteur…";
            _main.Log("Réglages : activation température CPU — vérification/installation du pilote PawnIO…");

            var (ok, msg) = await Helpers.PawnIoDriver.EnsureInstalledAsync();

            if (ok)
            {
                MainWindow.Settings.CpuTempEnabled = true;
                MainWindow.Settings.Save();
                Helpers.CpuTemperature.Enabled = true;
                TxtCpuTempStatus.Text = "Activée — la température remplace la fréquence de base dans le Monitoring.";
                _main.Log($"Réglages : température CPU activée ({msg}).");
            }
            else
            {
                // Revert silencieux de la case (sans re-déclencher le handler)
                _loading = true; ChkCpuTemp.IsChecked = false; _loading = false;
                MainWindow.Settings.CpuTempEnabled = false;
                MainWindow.Settings.Save();
                Helpers.CpuTemperature.Enabled = false;
                TxtCpuTempStatus.Text = "Échec de l'installation du pilote — température indisponible. Voir le journal.";
                _main.Log($"Réglages : échec activation température CPU — {msg}.");
            }

            ChkCpuTemp.IsEnabled = true;
        }

        // ── Check de MAJ (appelé au démarrage + manuellement) ──────────────────

        public static async Task<(bool hasUpdate, string tag, string url, string assetUrl, string sha256, string notes)> CheckForUpdateAsync()
        {
            try
            {
                var apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.UserAgent.ParseAdd("Tweakly-Updater");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return (false, "", "", "", "", "");

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var url = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;

                // Trouver le 1er asset .zip.
                // SÉCURITÉ (audit C-2d) : on n'accepte QUE les assets hébergés sur les
                // releases de NOTRE repo — défense en profondeur si la réponse API était
                // altérée (un asset pointant ailleurs serait ignoré).
                var assetUrl = "";
                var expectedPrefix = $"https://github.com/{RepoOwner}/{RepoName}/releases/";
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            a.TryGetProperty("browser_download_url", out var d))
                        {
                            var candidate = d.GetString() ?? "";
                            if (!candidate.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                                continue;   // asset hors repo → ignoré
                            assetUrl = candidate;
                            break;
                        }
                    }
                }

                // SÉCURITÉ (audit C-2a, durci en v1.3.4 — audit 2026-06-11) : hash SHA-256
                // du zip publié dans le body de la release (ligne « SHA256: <64 hex> »).
                // Le hash est désormais OBLIGATOIRE : une release sans hash est IGNORÉE
                // (fail-CLOSED). Avant, l'absence de hash désactivait la vérification
                // (fail-open) → un attaquant contrôlant le compte GitHub pouvait simplement
                // OMETTRE la ligne pour contourner toute la vérif. Pas de souci de compat :
                // un client qui embarque ce code ne verra que des releases plus récentes que
                // lui, et le protocole (étape 5bis) publie le hash depuis la v1.3.0.
                var sha256 = "";
                var notes  = "";
                if (root.TryGetProperty("body", out var b))
                {
                    var body = b.GetString() ?? "";
                    var m = System.Text.RegularExpressions.Regex.Match(
                        body, @"SHA256\s*[:=]\s*([0-9a-fA-F]{64})");
                    if (m.Success) sha256 = m.Groups[1].Value.ToLowerInvariant();

                    // Patch note affiché dans l'overlay de MAJ (v1.3.4) : le body de la
                    // release SANS sa ligne technique « SHA256: … » (inutile pour l'utilisateur).
                    notes = System.Text.RegularExpressions.Regex
                        .Replace(body, @"^\s*SHA256\s*[:=].*$", "",
                                 System.Text.RegularExpressions.RegexOptions.Multiline)
                        .Trim();
                }

                var remote = ParseVersion(tag);
                var local  = ParseVersion(AppVersion);

                if (remote != null && local != null && remote > local)
                {
                    if (string.IsNullOrEmpty(sha256))
                    {
                        // Release plus récente MAIS sans hash d'intégrité → refusée. On trace
                        // pour que l'oubli (ou l'attaque) soit visible dans le journal local.
                        Helpers.AppLog.Write(
                            $"MAJ : release {tag} détectée mais SANS ligne « SHA256: » dans son descriptif — " +
                            "ignorée par sécurité (intégrité non vérifiable).");
                        return (false, "", "", "", "", "");
                    }
                    return (true, tag, url, assetUrl, sha256, notes);
                }
            }
            catch { }
            return (false, "", "", "", "", "");
        }

        // Colore le segment actif (fond accent) et grise l'inactif
        private void UpdateThemeSegment(ThemeManager.Mode mode)
        {
            StyleSegment(BtnThemeDark,  mode == ThemeManager.Mode.Dark);
            StyleSegment(BtnThemeLight, mode == ThemeManager.Mode.Light);
        }

        private void UpdateNavigationModeSegment(string mode)
        {
            bool advanced = MainWindow.IsAdvancedNavigationMode(mode);
            StyleSegment(BtnNavModeEasy,     !advanced);
            StyleSegment(BtnNavModeAdvanced,  advanced);
        }

        public void SyncNavigationModeSegment()
            => UpdateNavigationModeSegment(MainWindow.Settings.NavigationMode);

        private static void StyleSegment(Button btn, bool active)
        {
            btn.ApplyTemplate();
            if (btn.Template.FindName("Bg",  btn) is not Border bg)  return;
            if (btn.Template.FindName("Lbl", btn) is not TextBlock lbl) return;

            if (active)
            {
                bg.SetResourceReference(Border.BackgroundProperty, "ThTabSel");
                lbl.Foreground = Brushes.White;
            }
            else
            {
                bg.Background = Brushes.Transparent;
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
            }
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
                var (hasUpdate, tag, url, assetUrl, sha256, notes) = await CheckForUpdateAsync();

                if (hasUpdate)
                {
                    _updateUrl = url;
                    _assetUrl  = assetUrl;
                    _lastTag   = tag;
                    _sha256    = sha256;
                    _notes     = notes;
                    TxtUpdateStatus.Text = $"Mise à jour disponible : {tag}  —  tu as la v{AppVersion}";
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
            _main.StartUpdate(_assetUrl, _lastTag, _sha256, _notes);
        }

        /// <summary>
        /// Télécharge le ZIP (avec progression %), l'extrait et écrit le script de
        /// remplacement. Ne redémarre PAS — retourne le chemin du script à lancer.
        /// </summary>
        public static async Task<string> PrepareUpdateAsync(string assetUrl, IProgress<double> progress,
                                                            string expectedSha256 = "",
                                                            System.Threading.CancellationToken ct = default)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "Tweakly_update");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var zipPath = Path.Combine(tmp, "update.zip");

            // Téléchargement avec progression (annulable via ct — bouton « Plus tard »)
            using (var req = new HttpRequestMessage(HttpMethod.Get, assetUrl))
            {
                req.Headers.UserAgent.ParseAdd("Tweakly-Updater");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var fs = File.Create(zipPath);
                var buffer = new byte[81920];
                long readTotal = 0; int n;
                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, n, ct);
                    readTotal += n;
                    if (total > 0) progress.Report((double)readTotal / total * 100.0);
                }
                progress.Report(100);
            }
            ct.ThrowIfCancellationRequested();

            // SÉCURITÉ (audit C-2a, durci v1.3.4) : vérification d'intégrité SHA-256 du zip
            // téléchargé. Le hash est OBLIGATOIRE (défense en profondeur — CheckForUpdateAsync
            // filtre déjà les releases sans hash, mais si un autre appelant arrivait ici sans
            // hash, on refuse aussi). Mismatch ou absence → aucun batch écrit, aucun fichier
            // remplacé. L'exception remonte à StartUpdate (catch → message d'échec, app intacte).
            if (string.IsNullOrEmpty(expectedSha256))
                throw new Exception(
                    "Cette mise à jour ne publie pas de hash d'intégrité (SHA-256). " +
                    "Installation refusée par sécurité.");
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var zfs = File.OpenRead(zipPath))
            {
                var actual = Convert.ToHexString(await sha.ComputeHashAsync(zfs)).ToLowerInvariant();
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "Échec de la vérification d'intégrité du téléchargement (SHA-256 différent). " +
                        "Mise à jour annulée par sécurité.");
            }

            // Extraction
            var extractDir = Path.Combine(tmp, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            var srcDir = FindExeDir(extractDir) ?? throw new Exception("Tweakly.exe introuvable dans l'archive.");

            var exePath    = Process.GetCurrentProcess().MainModule!.FileName;
            var installDir = Path.GetDirectoryName(exePath)!;

            // Script : attend la fermeture, remplace les fichiers, relance.
            // ⚠️ MAJ-SENSIBLE — ce batch est généré par la version EN COURS d'exécution : toute
            // amélioration ne profite QU'AUX mises à jour partant de cette version (les versions déjà
            // installées chez les users gardent leur propre batch, intouchable). Conçu pour être
            // STRICTEMENT plus robuste que l'ancien `xcopy` (qui copiait une seule fois et abandonnait
            // en silence si le .exe était encore verrouillé — antivirus scannant le nouvel exe, ou
            // verrou du gros single-file pas encore relâché par l'OS → l'ancienne version relancée).
            //   • timeout 1s : petite tempo après la sortie du process pour laisser l'OS libérer le verrou.
            //   • robocopy /R:10 /W:2 : RÉESSAIE une cible verrouillée (10 essais × 2 s = 20 s de patience)
            //     au lieu d'abandonner. /E = arborescence complète (équiv. xcopy /E /Y, additif, ne supprime rien).
            //   • Pire cas (échec après 20 s) : on relance quand même → comportement IDENTIQUE à l'ancien
            //     batch (aucune régression possible). Au mieux : le blocage antivirus est absorbé.
            var bat = Path.Combine(tmp, "update.bat");
            var script =
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /fi \"imagename eq Tweakly.exe\" 2>nul | find /i \"Tweakly.exe\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                $"robocopy \"{srcDir}\" \"{installDir}\" /E /R:10 /W:2 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                // --after-update : signale à la nouvelle instance qu'elle revient d'une MAJ →
                // elle se ramène au premier plan même si « Démarrer minimisé » est coché (la
                // relance via cmd.exe sans fenêtre n'a aucun droit de foreground sinon).
                $"start \"\" \"{exePath}\" --after-update\r\n" +
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
        // ⚠️ Tweakly est requireAdministrator → la clé HKCU\…\Run ne marche PAS pour les apps
        // élevées (Windows refuse de les lancer au démarrage). Voie propre = tâche planifiée
        // avec « Run with highest privileges ». Voir Helpers/StartupManager pour le détail.

        private static bool IsStartupEnabled() => Helpers.StartupManager.IsEnabled();

        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool enable = ChkStartup.IsChecked == true;
            bool ok = enable ? Helpers.StartupManager.Enable() : Helpers.StartupManager.Disable();
            if (ok)
            {
                _main.Log($"Réglages : démarrage Windows {(enable ? "activé (tâche planifiée)" : "désactivé")}.");
            }
            else
            {
                // On expose le message d'erreur exact (LastError) pour faciliter le diagnostic
                // chez l'utilisateur. Bug v1.2.9 : Enable() retournait true sans créer la tâche
                // (cas compte Microsoft) → la case se redécochait au redémarrage de l'app sans
                // qu'on sache pourquoi. Maintenant on saura.
                var reason = Helpers.StartupManager.LastError;
                if (string.IsNullOrWhiteSpace(reason)) reason = "raison inconnue";
                _main.Log($"Réglages : erreur démarrage Windows ({(enable ? "activation" : "désactivation")}) — {reason}");
                // Revert silencieux de la case sans re-déclencher le handler
                _loading = true; ChkStartup.IsChecked = !enable; _loading = false;
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

        private void BtnNavModeEasy_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _main.ApplyNavigationMode("Easy");
            UpdateNavigationModeSegment("Easy");
            _main.Log("Réglages : mode d'utilisation Simple activé.");
        }

        private void BtnNavModeAdvanced_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _main.ApplyNavigationMode("Advanced");
            UpdateNavigationModeSegment("Advanced");
            _main.Log("Réglages : mode d'utilisation Avancé activé.");
        }

        // ── Liens & dossiers ────────────────────────────────────────────────────

        private void BtnGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { using var _ = Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); }
            catch (Exception ex) { _main.Log($"Réglages : erreur ouverture dossier — {ex.Message}"); }
        }

        /// <summary>Ouvre le journal technique dans le visualiseur INTÉGRÉ (v1.3.5 — le
        /// Bloc-notes externe cassait l'expérience, rejeté par l'utilisateur).</summary>
        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            try { new LogViewerWindow(Window.GetWindow(this)!).Show(); }
            catch (Exception ex) { _main.Log($"Réglages : erreur ouverture journal — {ex.Message}"); }
        }

        private void OpenUrl(string url)
        {
            try { using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _main.Log($"Réglages : erreur ouverture lien — {ex.Message}"); }
        }
    }
}
