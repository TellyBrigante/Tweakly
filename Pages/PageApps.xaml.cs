using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using System.Text.Json;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    // ── Statut de mise à jour ──────────────────────────────────────────────────

    public enum UpdateStatus { Unknown, Checking, UpToDate, UpdateAvailable, Updating, Updated, Failed }

    // ── Modèle ────────────────────────────────────────────────────────────────

    public class AppItem : INotifyPropertyChanged
    {
        private string       _wingetId        = "";
        private UpdateStatus _updateStatus    = UpdateStatus.Unknown;
        private string       _availableVersion = "";

        public string Name            { get; set; } = "";
        public string Publisher       { get; set; } = "";
        public string Version         { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string InstallLocation { get; set; } = "";

        public string WingetId
        {
            get => _wingetId;
            set { if (_wingetId == value) return; _wingetId = value; Notify(nameof(WingetId)); }
        }

        public UpdateStatus UpdateStatus
        {
            get => _updateStatus;
            set
            {
                if (_updateStatus == value) return;
                _updateStatus = value;
                Notify(nameof(UpdateStatus));
                Notify(nameof(StatusText));
            }
        }

        public string AvailableVersion
        {
            get => _availableVersion;
            set
            {
                if (_availableVersion == value) return;
                _availableVersion = value;
                Notify(nameof(AvailableVersion));
                Notify(nameof(StatusText));
            }
        }

        public string StatusText => UpdateStatus switch
        {
            UpdateStatus.Checking        => "Vérification…",
            UpdateStatus.UpToDate        => "À jour",
            UpdateStatus.UpdateAvailable => string.IsNullOrEmpty(AvailableVersion) ? "Disponible" : AvailableVersion,
            UpdateStatus.Updating        => "Mise à jour…",
            UpdateStatus.Updated         => "Mis à jour",
            UpdateStatus.Failed          => "Échec",
            _                            => "—",
        };

        private void Notify(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ── Résidu détecté après désinstallation ───────────────────────────────────

    public sealed class Leftover
    {
        public enum LType { Reg, File, Task }
        public LType  Type    { get; init; }
        public string Target  { get; init; } = "";
        public string Display { get; init; } = "";
    }

    // ── Page ──────────────────────────────────────────────────────────────────

    public partial class PageApps : UserControl
    {
        private readonly MainWindow                          _main;
        private readonly ObservableCollection<AppItem>      _apps = new();
        private          ICollectionView?                   _view;
        private          bool                               _loaded   = false;
        private          bool                               _updating = false;
        private          List<Leftover>                     _pendingCleanup = new();

        public PageApps(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;

            _view = CollectionViewSource.GetDefaultView(_apps);
            _view.Filter = FilterApp;
            DgApps.ItemsSource = _view;

            await LoadAppsAsync();
        }

        private bool FilterApp(object obj)
        {
            if (obj is not AppItem app) return false;
            var q = TxtSearch.Text.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return app.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || app.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        // ── Chargement ────────────────────────────────────────────────────────

        private async Task LoadAppsAsync()
        {
            BtnRefresh.IsEnabled      = false;
            BtnDesinstaller.IsEnabled = false;
            BtnUpdateAll.IsEnabled    = false;
            TxtStatus.Text            = "Chargement de la liste des applications…";
            TxtAppCount.Text          = "";
            _apps.Clear();

            var list = await Task.Run(LoadFromRegistry);

            foreach (var a in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                _apps.Add(a);

            UpdateCount();
            BtnRefresh.IsEnabled = true;
            TxtStatus.Text       = $"{_apps.Count} application(s) chargée(s). Récupération des IDs Winget…";
            _main.Log($"Applications : {_apps.Count} application(s) trouvée(s).");

            // Phase 2 : IDs Winget en arrière-plan
            var ids = await Task.Run(() => LoadWingetIds());

            if (ids.Count > 0)
            {
                foreach (var a in _apps)
                    if (ids.TryGetValue(a.Name, out var id)) a.WingetId = id;

                _main.Log($"Applications : {ids.Count} ID(s) Winget récupéré(s).");
            }
            else
            {
                TxtStatus.Text = "Prêt (Winget non disponible ou liste vide).";
                return;
            }

            // Phase 3 : vérification des mises à jour
            await CheckAllUpdatesAsync();
        }

        // ── Vérification des mises à jour ─────────────────────────────────────

        private async Task CheckAllUpdatesAsync()
        {
            var withId = _apps.Where(a => !string.IsNullOrEmpty(a.WingetId)).ToList();
            if (withId.Count == 0)
            {
                TxtStatus.Text = "Prêt.";
                return;
            }

            TxtStatus.Text = "Vérification des mises à jour…";
            foreach (var a in withId) a.UpdateStatus = UpdateStatus.Checking;

            var upgrades = await Task.Run(() => LoadWingetUpgrades());

            foreach (var a in withId)
            {
                if (upgrades.TryGetValue(a.WingetId, out var newVer))
                {
                    a.AvailableVersion = newVer;
                    a.UpdateStatus     = UpdateStatus.UpdateAvailable;
                }
                else
                {
                    a.UpdateStatus = UpdateStatus.UpToDate;
                }
            }

            int count = withId.Count(a => a.UpdateStatus == UpdateStatus.UpdateAvailable);
            BtnUpdateAll.IsEnabled = count > 0;

            TxtStatus.Text = count > 0
                ? $"{count} mise(s) à jour disponible(s)."
                : "Toutes les applications gérées sont à jour.";
            _main.Log($"Applications : vérification terminée — {count} mise(s) à jour disponible(s).");
        }

        private Dictionary<string, string> LoadWingetUpgrades()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var raw = RunWinget("upgrade --include-unknown --accept-source-agreements --disable-interactivity");
                if (!string.IsNullOrWhiteSpace(raw))
                    ParseWingetUpgrade(raw, dict);
            }
            catch (Exception ex) { _main.Log($"Applications : LoadWingetUpgrades — {ex.Message}"); }
            return dict;
        }

        // Trouve la position du premier tableau JSON ('[') qui précède un objet ('{').
        // Ignore les '[' qui proviennent de codes ANSI résiduels ou de texte de progression.
        private static int FindJsonArrayStart(string s)
        {
            for (int k = s.IndexOf('['); k >= 0; k = s.IndexOf('[', k + 1))
            {
                var after = s.Substring(k + 1).TrimStart(' ', '\r', '\n');
                if (after.StartsWith("{") || after.StartsWith("]"))
                    return k;
            }
            return -1;
        }

        // Lance winget et retourne sa sortie.
        // Tente d'abord en contexte utilisateur (de-élevé) car winget ne fonctionne pas bien
        // dans un process admin. chcp 65001 force UTF-8 pour éviter les problèmes d'encodage.
        // Fallback direct si la de-élévation échoue.
        private string RunWinget(string arguments)
        {
            // Tentative 1 : de-élevé via token explorer
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"tweakly_wg_{Guid.NewGuid():N}.txt");
                try
                {
                    int exit = DeElevatedLauncher.StartAndWait(
                        "cmd.exe",
                        $"/c chcp 65001 > nul && winget {arguments} > \"{tmp}\" 2>&1",
                        70_000);
                    if (File.Exists(tmp))
                    {
                        var raw = File.ReadAllText(tmp, Encoding.UTF8);
                        raw = Regex.Replace(raw, "\x1B\\[[0-9;?]*[A-Za-z]", ""); // strip ANSI colors
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            _main.Log($"Applications : winget OK — {raw.Length} chars.");
                            return raw;
                        }
                    }
                    _main.Log($"Applications : winget de-élevé exit={exit}, sortie vide.");
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            catch (Exception ex) { _main.Log($"Applications : de-élévation — {ex.Message}"); }

            // Tentative 2 : direct (fallback)
            try
            {
                using var p = Process.Start(new ProcessStartInfo("winget", arguments)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                });
                if (p != null)
                {
                    var raw = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(60_000);
                    raw = Regex.Replace(raw ?? "", "\x1B\\[[0-9;?]*[A-Za-z]", "");
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        _main.Log($"Applications : winget direct OK — {raw.Length} chars.");
                        return raw;
                    }
                    _main.Log($"Applications : winget direct exit={p.ExitCode}, sortie vide.");
                }
            }
            catch (Exception ex) { _main.Log($"Applications : winget direct — {ex.Message}"); }

            return "";
        }

        private static void ParseWingetUpgrade(string output, Dictionary<string, string> dict)
        {
            // Structure d'une ligne winget upgrade : Nom...  Id  VersionActuelle  Disponible  [Source]
            // On repère le token ID, puis la version disponible = 2 tokens après l'ID.
            foreach (var rawLine in output.Split('\n'))
            {
                var line   = rawLine.TrimEnd('\r').TrimEnd();
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                int idIdx = -1;
                for (int k = 0; k < tokens.Length; k++)
                    if (IsWingetId(tokens[k])) { idIdx = k; break; }

                if (idIdx < 1 || idIdx + 2 >= tokens.Length) continue;

                var id    = tokens[idIdx];
                var avail = tokens[idIdx + 2];   // [Id][VersionActuelle][Disponible]
                if (!string.IsNullOrEmpty(avail) && avail != "<")
                    dict.TryAdd(id, avail);
            }
        }

        // ── Mise à jour individuelle ───────────────────────────────────────────

        private async Task UpdateSingleAppAsync(AppItem app)
        {
            if (string.IsNullOrEmpty(app.WingetId)) return;

            app.UpdateStatus = UpdateStatus.Updating;
            TxtStatus.Text   = $"Mise à jour de « {app.Name} »…";
            _main.Log($"Applications : mise à jour de « {app.Name} »…");

            var ok = await Task.Run(() =>
            {
                try
                {
                    int exit = DeElevatedLauncher.StartAndWait(
                        "cmd.exe",
                        $"/c winget upgrade --id \"{app.WingetId}\" --silent --accept-source-agreements --disable-interactivity",
                        300_000);
                    return exit == 0;
                }
                catch { return false; }
            });

            if (ok)
            {
                app.AvailableVersion = "";
                app.UpdateStatus     = UpdateStatus.Updated;
                _main.Log($"Applications : « {app.Name} » mise à jour avec succès.");
            }
            else
            {
                app.UpdateStatus = UpdateStatus.Failed;
                _main.Log($"Applications : échec de la mise à jour de « {app.Name} ».");
            }
        }

        private async void BtnUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            if ((sender as Button)?.Tag is not AppItem app) return;
            if (app.UpdateStatus is UpdateStatus.Updating or UpdateStatus.Updated) return;

            _updating              = true;
            BtnUpdateAll.IsEnabled = false;
            BtnRefresh.IsEnabled   = false;

            await UpdateSingleAppAsync(app);

            BtnUpdateAll.IsEnabled = _apps.Any(a => a.UpdateStatus == UpdateStatus.UpdateAvailable);
            BtnRefresh.IsEnabled   = true;
            _updating              = false;

            TxtStatus.Text = app.UpdateStatus == UpdateStatus.Updated
                ? $"« {app.Name} » mise à jour avec succès."
                : $"Échec de la mise à jour de « {app.Name} ».";
        }

        private async void BtnUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            var toUpdate = _apps.Where(a => a.UpdateStatus == UpdateStatus.UpdateAvailable).ToList();
            if (toUpdate.Count == 0) return;

            _updating                 = true;
            BtnUpdateAll.IsEnabled    = false;
            BtnRefresh.IsEnabled      = false;
            BtnDesinstaller.IsEnabled = false;
            _main.Log($"Applications : mise à jour groupée — {toUpdate.Count} application(s)…");

            foreach (var app in toUpdate)
                await UpdateSingleAppAsync(app);

            int updated = toUpdate.Count(a => a.UpdateStatus == UpdateStatus.Updated);
            int failed  = toUpdate.Count(a => a.UpdateStatus == UpdateStatus.Failed);

            BtnUpdateAll.IsEnabled    = _apps.Any(a => a.UpdateStatus == UpdateStatus.UpdateAvailable);
            BtnRefresh.IsEnabled      = true;
            BtnDesinstaller.IsEnabled = DgApps.SelectedItem != null;
            _updating                 = false;

            TxtStatus.Text = failed > 0
                ? $"Mises à jour : {updated} réussie(s), {failed} échouée(s)."
                : $"{updated} application(s) mise(s) à jour avec succès.";
            _main.Log($"Applications : MAJ groupée — {updated} réussie(s), {failed} échouée(s).");
        }

        // ── Lecture registre ──────────────────────────────────────────────────

        private static List<AppItem> LoadFromRegistry()
        {
            var result = new List<AppItem>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] hklmPaths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var path in hklmPaths)
            {
                try
                {
                    using var root = Registry.LocalMachine.OpenSubKey(path);
                    if (root == null) continue;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = root.OpenSubKey(sub);
                            if (k == null) continue;
                            var item = KeyToAppItem(k);
                            if (item != null && seen.Add(item.Name)) result.Add(item);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root != null)
                {
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = root.OpenSubKey(sub);
                            if (k == null) continue;
                            var item = KeyToAppItem(k);
                            if (item != null && seen.Add(item.Name)) result.Add(item);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return result;
        }

        private static AppItem? KeyToAppItem(RegistryKey k)
        {
            var name = k.GetValue("DisplayName")?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) return null;

            if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;

            if (Regex.IsMatch(name, @"^KB\d{6,}", RegexOptions.IgnoreCase)) return null;
            if (name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Update for",      StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Hotfix for",      StringComparison.OrdinalIgnoreCase)) return null;

            return new AppItem
            {
                Name            = name,
                Publisher       = k.GetValue("Publisher")?.ToString()?.Trim()      ?? "",
                Version         = k.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "",
                InstallLocation = k.GetValue("InstallLocation")?.ToString()?.Trim() ?? "",
                UninstallString = k.GetValue("QuietUninstallString")?.ToString()?.Trim()
                               ?? k.GetValue("UninstallString")?.ToString()?.Trim()
                               ?? "",
            };
        }

        // ── Lecture Winget ────────────────────────────────────────────────────

        private Dictionary<string, string> LoadWingetIds()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var raw = RunWinget("list --accept-source-agreements --disable-interactivity");
                if (!string.IsNullOrWhiteSpace(raw))
                    ParseWingetList(raw, dict);
            }
            catch (Exception ex) { _main.Log($"Applications : LoadWingetIds — {ex.Message}"); }
            return dict;
        }

        private static bool IsWingetId(string tok)
        {
            // ID winget = contient un point, et le 1er segment a au moins une lettre.
            // Distingue "Google.Chrome" (ID) de "19.4.0.10" (version).
            if (!tok.Contains('.') || tok.Length < 4) return false;
            // Rejette les pseudo-IDs internes ARP (ex. "ARP\Machine\X86\Battle.net") : non installables
            if (tok.Contains('\\') || tok.StartsWith("ARP", StringComparison.OrdinalIgnoreCase)) return false;
            return tok.Substring(0, tok.IndexOf('.')).Any(char.IsLetter);
        }

        private static void ParseWingetList(string output, Dictionary<string, string> dict)
        {
            // Pour chaque ligne : on cherche le 1er token au format winget ID (Éditeur.App).
            // Tout ce qui précède = le nom. Indépendant du header, du séparateur et de la langue.
            foreach (var rawLine in output.Split('\n'))
            {
                var line   = rawLine.TrimEnd('\r').TrimEnd();
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                int cur = 0;
                foreach (var tok in tokens)
                {
                    int p = line.IndexOf(tok, Math.Min(cur, line.Length), StringComparison.Ordinal);
                    if (p < 0) continue;
                    if (IsWingetId(tok) && p > 0)
                    {
                        var name = line.Substring(0, p).Trim();
                        if (name.Length > 0) dict.TryAdd(name, tok);
                        break;
                    }
                    cur = p + tok.Length;
                }
            }
        }

        // ── Événements UI ─────────────────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtSearchHint.Visibility = TxtSearch.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            _view?.Refresh();
            UpdateCount();
        }

        private void DgApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnDesinstaller.IsEnabled = DgApps.SelectedItem != null;
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            DgApps.SelectedItem    = null;
            TxtSearch.Text         = "";
            BtnUpdateAll.IsEnabled = false;
            _loaded                = false;
            _loaded                = true;
            await LoadAppsAsync();
        }

        private async void BtnDesinstaller_Click(object sender, RoutedEventArgs e)
        {
            if (DgApps.SelectedItem is not AppItem app) return;

            var confirm = MessageBox.Show(
                $"Désinstaller « {app.Name} » ?\n\nCette action est irréversible.",
                "Confirmation de désinstallation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            BtnDesinstaller.IsEnabled = false;
            BtnRefresh.IsEnabled      = false;
            TxtStatus.Text            = $"Désinstallation de « {app.Name} »…";
            _main.Log($"Applications : désinstallation de « {app.Name} »…");

            _pendingCleanup        = new();
            BtnNettoyer.Visibility = Visibility.Collapsed;

            var ok = await Task.Run(() => Uninstall(app));

            if (ok)
            {
                _apps.Remove(app);
                UpdateCount();
                TxtStatus.Text = $"« {app.Name} » désinstallée avec succès.";
                _main.Log($"Applications : « {app.Name} » désinstallée.");
            }
            else
            {
                TxtStatus.Text = $"Échec de la désinstallation de « {app.Name} ».";
                _main.Log($"Applications : échec désinstallation « {app.Name} ».");
            }

            if (ok)
            {
                TxtStatus.Text = "Scan des résidus en cours…";
                var leftovers = await Task.Run(
                    () => FindLeftovers(app.Name, app.Publisher, app.InstallLocation));

                if (leftovers.Count > 0)
                {
                    _pendingCleanup        = leftovers;
                    int n                  = leftovers.Count;
                    BtnNettoyer.Content    = $"NETTOYER {n} RÉSIDU{(n > 1 ? "S" : "")}";
                    BtnNettoyer.Visibility = Visibility.Visible;
                    BtnNettoyer.IsEnabled  = true;
                    TxtStatus.Text         = $"Désinstallée — {n} résidu(s) détecté(s), cliquez pour nettoyer.";
                    _main.Log($"Applications : {n} résidu(s) détecté(s) après désinstallation.");
                }
                else
                {
                    TxtStatus.Text = "Désinstallée — aucun résidu détecté.";
                    _main.Log("Applications : aucun résidu détecté.");
                }
            }

            BtnRefresh.IsEnabled      = true;
            BtnDesinstaller.IsEnabled = DgApps.SelectedItem != null;
        }

        // ── Scan + nettoyage des résidus ──────────────────────────────────────

        private static readonly string[] _exclWords =
        {
            "Windows","Microsoft","System","Service","Update","Setup","Install",
            "Application","Software","Program","Driver","Runtime","Package",
            "Redistributable","Visual","Studio","Tools","Framework","Component",
            "Corp","Corporation","Inc","Ltd","Group","Technologies","Technology",
            "Version","Release","Build",
        };

        private static List<Leftover> FindLeftovers(string appName, string publisher, string installLoc)
        {
            var found = new List<Leftover>();
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excl  = new HashSet<string>(_exclWords, StringComparer.OrdinalIgnoreCase);

            var cands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in Regex.Split(appName ?? "", @"[\s\-_\.]+"))
                if (w.Length >= 4 && !excl.Contains(w)) cands.Add(w);

            if (!string.IsNullOrWhiteSpace(publisher) && publisher.Trim().Length >= 4)
            {
                var pt = publisher.Trim();
                if (!excl.Contains(pt)) cands.Add(pt);
                var pf = Regex.Split(pt, @"[\s\-_\.]+").FirstOrDefault() ?? "";
                if (pf.Length >= 4 && !excl.Contains(pf)) cands.Add(pf);
            }

            if (cands.Count == 0) return found;

            var regRoots = new (RegistryKey hive, string prefix, string sub)[]
            {
                (Registry.CurrentUser,  "HKCU", @"SOFTWARE"),
                (Registry.LocalMachine, "HKLM", @"SOFTWARE"),
                (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node"),
            };

            foreach (var (hive, prefix, sub) in regRoots)
            {
                foreach (var kw in cands)
                {
                    var rel = $@"{sub}\{kw}";
                    try
                    {
                        using var k = hive.OpenSubKey(rel);
                        if (k != null && seen.Add($"{prefix}\\{rel}"))
                            found.Add(new Leftover { Type = Leftover.LType.Reg, Target = $@"{prefix}\{rel}", Display = $@"Registre : {prefix}\{rel}" });
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(publisher))
                {
                    var pf = Regex.Split(publisher.Trim(), @"[\s\-_\.]+").FirstOrDefault() ?? "";
                    var af = Regex.Split((appName ?? "").Trim(), @"[\s\-_\.]+").FirstOrDefault() ?? "";
                    if (pf.Length >= 4 && af.Length >= 4 && !excl.Contains(pf) && !excl.Contains(af))
                    {
                        var rel = $@"{sub}\{pf}\{af}";
                        try
                        {
                            using var k = hive.OpenSubKey(rel);
                            if (k != null && seen.Add($"{prefix}\\{rel}"))
                                found.Add(new Leftover { Type = Leftover.LType.Reg, Target = $@"{prefix}\{rel}", Display = $@"Registre : {prefix}\{rel}" });
                        }
                        catch { }
                    }
                }
            }

            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };

            foreach (var b in bases)
            {
                if (string.IsNullOrEmpty(b)) continue;
                foreach (var kw in cands)
                {
                    var p = Path.Combine(b, kw);
                    try
                    {
                        if (Directory.Exists(p) && seen.Add(p))
                            found.Add(new Leftover { Type = Leftover.LType.File, Target = p, Display = $"Dossier : {p}" });
                    }
                    catch { }
                }
            }

            if (!string.IsNullOrWhiteSpace(installLoc) && installLoc.Trim().Length > 5)
            {
                var il = installLoc.Trim().Trim('"').TrimEnd('\\');
                try
                {
                    if (Directory.Exists(il) && seen.Add(il))
                        found.Add(new Leftover { Type = Leftover.LType.File, Target = il, Display = $"Dossier install : {il}" });
                }
                catch { }
            }

            try
            {
                var tasksRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "Tasks");

                if (Directory.Exists(tasksRoot))
                {
                    foreach (var file in Directory.GetFiles(tasksRoot, "*", SearchOption.AllDirectories))
                    {
                        var taskName = Path.GetFileName(file);
                        bool match   = cands.Any(kw => taskName.Contains(kw, StringComparison.OrdinalIgnoreCase));

                        if (!match)
                        {
                            try
                            {
                                var content = File.ReadAllText(file);
                                match = cands.Any(kw => content.Contains(kw, StringComparison.OrdinalIgnoreCase));
                            }
                            catch { }
                        }

                        if (!match) continue;

                        var rel = file.Substring(tasksRoot.Length).Replace('/', '\\');
                        if (!rel.StartsWith("\\")) rel = "\\" + rel;
                        if (seen.Add("TASK:" + rel))
                            found.Add(new Leftover { Type = Leftover.LType.Task, Target = rel, Display = $"Tâche planifiée : {taskName}" });
                    }
                }
            }
            catch { }

            return found;
        }

        private async void BtnNettoyer_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingCleanup.Count == 0) return;

            var details = string.Join("\n", _pendingCleanup.Select(l => $"  • {l.Display}"));
            var confirm = MessageBox.Show(
                $"{_pendingCleanup.Count} résidu(s) trouvé(s) :\n\n{details}\n\nSuppression définitive. Continuer ?",
                "Nettoyage des résidus",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            BtnNettoyer.IsEnabled = false;
            BtnNettoyer.Content   = "Nettoyage en cours…";
            TxtStatus.Text        = "Nettoyage des résidus en cours…";
            _main.Log("Applications : nettoyage des résidus en cours…");

            var items          = _pendingCleanup;
            var (cleaned, errors) = await Task.Run(() => CleanLeftovers(items));

            _pendingCleanup        = new();
            BtnNettoyer.Visibility = Visibility.Collapsed;
            BtnNettoyer.IsEnabled  = true;

            if (errors > 0)
            {
                TxtStatus.Text = $"Nettoyage : {cleaned} supprimé(s), {errors} erreur(s).";
                _main.Log($"Applications : nettoyage — {cleaned} supprimé(s), {errors} erreur(s).");
            }
            else
            {
                TxtStatus.Text = $"Nettoyage complet : {cleaned} élément(s) supprimé(s).";
                _main.Log($"Applications : nettoyage complet — {cleaned} élément(s) supprimé(s).");
            }
        }

        private static (int cleaned, int errors) CleanLeftovers(List<Leftover> items)
        {
            int cleaned = 0, errors = 0;
            foreach (var item in items)
            {
                try
                {
                    switch (item.Type)
                    {
                        case Leftover.LType.Reg:
                            DeleteRegistryKey(item.Target);
                            cleaned++;
                            break;
                        case Leftover.LType.File:
                            if (Directory.Exists(item.Target))
                                Directory.Delete(item.Target, recursive: true);
                            else if (File.Exists(item.Target))
                                File.Delete(item.Target);
                            cleaned++;
                            break;
                        case Leftover.LType.Task:
                            DeleteScheduledTask(item.Target);
                            cleaned++;
                            break;
                    }
                }
                catch { errors++; }
            }
            return (cleaned, errors);
        }

        private static void DeleteRegistryKey(string fullPath)
        {
            var idx = fullPath.IndexOf('\\');
            if (idx <= 0) return;
            var prefix = fullPath.Substring(0, idx);
            var sub    = fullPath.Substring(idx + 1);
            var hive   = prefix.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                ? Registry.CurrentUser
                : Registry.LocalMachine;
            hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }

        private static void DeleteScheduledTask(string taskPath)
        {
            using var p = Process.Start(new ProcessStartInfo(
                "schtasks", $"/delete /tn \"{taskPath}\" /f")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });
            p?.WaitForExit(15_000);
        }

        // ── Désinstallation ───────────────────────────────────────────────────

        private static bool Uninstall(AppItem app)
        {
            if (!string.IsNullOrEmpty(app.WingetId))
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(
                        "winget",
                        $"uninstall --id \"{app.WingetId}\" --silent --accept-source-agreements --disable-interactivity")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                    });
                    p?.WaitForExit(120_000);
                    if (p?.ExitCode == 0) return true;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(app.UninstallString))
            {
                try
                {
                    var us = app.UninstallString.Trim();
                    string exe, args;
                    if (us.StartsWith("\"", StringComparison.Ordinal))
                    {
                        var end = us.IndexOf('"', 1);
                        exe  = end > 0 ? us.Substring(1, end - 1) : us.Trim('"');
                        args = end > 0 && end + 1 < us.Length ? us.Substring(end + 1).Trim() : "";
                    }
                    else
                    {
                        var sp = us.IndexOf(' ');
                        exe  = sp > 0 ? us.Substring(0, sp) : us;
                        args = sp > 0 ? us.Substring(sp + 1).Trim() : "";
                    }
                    using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                    p?.WaitForExit(120_000);
                    return true;
                }
                catch { }
            }

            try
            {
                using var p = Process.Start(new ProcessStartInfo(
                    "winget",
                    $"uninstall --name \"{app.Name}\" --silent --accept-source-agreements --disable-interactivity")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                });
                p?.WaitForExit(120_000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdateCount()
        {
            if (_view == null) return;
            int visible = _view.Cast<object>().Count();
            int total   = _apps.Count;
            TxtAppCount.Text = visible == total
                ? $"{total} application{(total > 1 ? "s" : "")}"
                : $"{visible} / {total} application{(total > 1 ? "s" : "")}";
        }
    }
}
