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

namespace Optimisation_Tool.Pages
{
    // ── Modèle ────────────────────────────────────────────────────────────────

    public class AppItem : INotifyPropertyChanged
    {
        private string _wingetId = "";

        public string Name             { get; set; } = "";
        public string Publisher        { get; set; } = "";
        public string Version          { get; set; } = "";
        public string UninstallString  { get; set; } = "";
        public string InstallLocation  { get; set; } = "";

        public string WingetId
        {
            get => _wingetId;
            set
            {
                if (_wingetId == value) return;
                _wingetId = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WingetId)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ── Résidu détecté après désinstallation ───────────────────────────────────

    public sealed class Leftover
    {
        public enum LType { Reg, File, Task }
        public LType  Type    { get; init; }
        public string Target  { get; init; } = "";   // chemin reg / chemin fichier / "\Chemin\Tache"
        public string Display { get; init; } = "";
    }

    // ── Page ──────────────────────────────────────────────────────────────────

    public partial class PageApps : UserControl
    {
        private readonly MainWindow _main;
        private readonly ObservableCollection<AppItem> _apps = new();
        private ICollectionView? _view;
        private bool _loaded = false;
        private List<Leftover> _pendingCleanup = new();

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
            TxtStatus.Text            = "Chargement de la liste des applications…";
            TxtAppCount.Text          = "";
            _apps.Clear();

            var list = await Task.Run(LoadFromRegistry);

            foreach (var a in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                _apps.Add(a);

            UpdateCount();
            BtnRefresh.IsEnabled = true;
            TxtStatus.Text       = $"{_apps.Count} application(s) chargée(s). Récupération des IDs Winget…";
            _main.Log($"Applications : {_apps.Count} application(s) trouvée(s).");

            // Phase 2 : IDs Winget en arrière-plan
            var ids = await Task.Run(LoadWingetIds);

            if (ids.Count > 0)
            {
                foreach (var a in _apps)
                {
                    if (ids.TryGetValue(a.Name, out var id))
                        a.WingetId = id;
                }
                TxtStatus.Text = "Prêt.";
                _main.Log($"Applications : {ids.Count} ID(s) Winget récupéré(s).");
            }
            else
            {
                TxtStatus.Text = "Prêt (Winget non disponible ou liste vide).";
            }
        }

        // ── Lecture registre ──────────────────────────────────────────────────

        private static List<AppItem> LoadFromRegistry()
        {
            var result = new List<AppItem>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // HKLM : apps 64-bit et 32-bit (WOW6432Node)
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
                            if (item != null && seen.Add(item.Name))
                                result.Add(item);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // HKCU : apps utilisateur (ex. applis Microsoft Store, winget user scope)
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
                            if (item != null && seen.Add(item.Name))
                                result.Add(item);
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

            // Filtrer : composants système
            if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;

            // Filtrer : mises à jour Windows (KB + numéro, "Security Update for …", etc.)
            if (Regex.IsMatch(name, @"^KB\d{6,}", RegexOptions.IgnoreCase)) return null;
            if (name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Update for",      StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("Hotfix for",      StringComparison.OrdinalIgnoreCase)) return null;

            return new AppItem
            {
                Name            = name,
                Publisher       = k.GetValue("Publisher")?.ToString()?.Trim()     ?? "",
                Version         = k.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "",
                InstallLocation = k.GetValue("InstallLocation")?.ToString()?.Trim() ?? "",
                UninstallString = k.GetValue("QuietUninstallString")?.ToString()?.Trim()
                               ?? k.GetValue("UninstallString")?.ToString()?.Trim()
                               ?? "",
            };
        }

        // ── Lecture Winget ────────────────────────────────────────────────────

        private static Dictionary<string, string> LoadWingetIds()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Vérifier rapidement si winget est disponible
                using var pChk = Process.Start(new ProcessStartInfo("winget", "--version")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                });
                pChk?.WaitForExit(4_000);
                if (pChk == null || pChk.ExitCode != 0) return dict;

                // Récupérer la liste complète
                using var p = Process.Start(new ProcessStartInfo(
                    "winget", "list --accept-source-agreements --disable-interactivity")
                {
                    UseShellExecute         = false,
                    CreateNoWindow          = true,
                    RedirectStandardOutput  = true,
                    StandardOutputEncoding  = Encoding.UTF8,
                });
                if (p == null) return dict;

                var raw = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60_000);

                ParseWingetList(raw, dict);
            }
            catch { }
            return dict;
        }

        private static void ParseWingetList(string output, Dictionary<string, string> dict)
        {
            // Winget affiche un tableau texte dont les colonnes sont délimitées par des espaces.
            // On repère la ligne d'en-tête (contient "Name" et "Id") pour connaître les offsets.
            var lines    = output.Split('\n');
            int nameCol  = -1;
            int idCol    = -1;
            bool pastSep = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                // Chercher l'en-tête
                if (nameCol < 0)
                {
                    var n = line.IndexOf("Name", StringComparison.Ordinal);
                    var d = line.IndexOf("Id",   StringComparison.Ordinal);
                    if (n >= 0 && d > n + 4)
                    {
                        nameCol = n;
                        idCol   = d;
                    }
                    continue;
                }

                // Sauter la ligne séparateur (suite de tirets)
                if (!pastSep)
                {
                    if (line.TrimStart().StartsWith("-", StringComparison.Ordinal))
                        pastSep = true;
                    continue;
                }

                if (line.Length <= idCol) continue;

                var namePart = line
                    .Substring(nameCol, Math.Min(idCol - nameCol, line.Length - nameCol))
                    .Trim();

                var idPart = line
                    .Substring(idCol)
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";

                // Les IDs winget contiennent toujours au moins un point (ex. "7zip.7zip")
                if (!string.IsNullOrEmpty(namePart) && idPart.Contains('.'))
                    dict.TryAdd(namePart, idPart);
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
            DgApps.SelectedItem = null;
            TxtSearch.Text      = "";
            _loaded             = false;   // autorise le rechargement complet
            _loaded             = true;
            await LoadAppsAsync();
        }

        private async void BtnDesinstaller_Click(object sender, RoutedEventArgs e)
        {
            if (DgApps.SelectedItem is not AppItem app) return;

            var confirm = MessageBox.Show(
                $"Désinstaller « {app.Name} » ?\n\nCette action est irréversible.",
                "Confirmation de désinstallation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            BtnDesinstaller.IsEnabled = false;
            BtnRefresh.IsEnabled      = false;
            TxtStatus.Text            = $"Désinstallation de « {app.Name} »…";
            _main.Log($"Applications : désinstallation de « {app.Name} »…");

            // Reinitialiser un eventuel nettoyage precedent
            _pendingCleanup = new();
            BtnNettoyer.Visibility = Visibility.Collapsed;

            var ok = await Task.Run(() => Uninstall(app));

            if (ok)
            {
                _apps.Remove(app);
                UpdateCount();
                TxtStatus.Text = $"« {app.Name} » désinstallée avec succès.";
                _main.Log($"Applications : « {app.Name} » désinstallée.");
            }
            else
            {
                TxtStatus.Text = $"Échec de la désinstallation de « {app.Name} ».";
                _main.Log($"Applications : échec désinstallation « {app.Name} ».");
            }

            // ── Scan des résidus après une désinstallation réussie ──────────
            if (ok)
            {
                TxtStatus.Text = "Scan des résidus en cours…";
                var leftovers = await Task.Run(
                    () => FindLeftovers(app.Name, app.Publisher, app.InstallLocation));

                if (leftovers.Count > 0)
                {
                    _pendingCleanup = leftovers;
                    int n = leftovers.Count;
                    BtnNettoyer.Content    = $"NETTOYER {n} RÉSIDU{(n > 1 ? "S" : "")}";
                    BtnNettoyer.Visibility = Visibility.Visible;
                    BtnNettoyer.IsEnabled  = true;
                    TxtStatus.Text = $"Désinstallée — {n} résidu(s) détecté(s), cliquez pour nettoyer.";
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

        // ── Scan + nettoyage des résidus ───────────────────────────────────────

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

            // Mots-clés candidats (nom d'app + éditeur)
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

            // ── Registre ────────────────────────────────────────────────────
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
                            found.Add(new Leftover
                            {
                                Type = Leftover.LType.Reg,
                                Target = $@"{prefix}\{rel}",
                                Display = $@"Registre : {prefix}\{rel}",
                            });
                    }
                    catch { }
                }

                // Clé imbriquée Éditeur\App
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
                                found.Add(new Leftover
                                {
                                    Type = Leftover.LType.Reg,
                                    Target = $@"{prefix}\{rel}",
                                    Display = $@"Registre : {prefix}\{rel}",
                                });
                        }
                        catch { }
                    }
                }
            }

            // ── Système de fichiers ─────────────────────────────────────────
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
                            found.Add(new Leftover
                            {
                                Type = Leftover.LType.File,
                                Target = p,
                                Display = $"Dossier : {p}",
                            });
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
                        found.Add(new Leftover
                        {
                            Type = Leftover.LType.File,
                            Target = il,
                            Display = $"Dossier install : {il}",
                        });
                }
                catch { }
            }

            // ── Tâches planifiées ───────────────────────────────────────────
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
                        bool match = cands.Any(kw =>
                            taskName.Contains(kw, StringComparison.OrdinalIgnoreCase));

                        if (!match)
                        {
                            try
                            {
                                var content = File.ReadAllText(file);
                                match = cands.Any(kw =>
                                    content.Contains(kw, StringComparison.OrdinalIgnoreCase));
                            }
                            catch { }
                        }

                        if (!match) continue;

                        var rel = file.Substring(tasksRoot.Length).Replace('/', '\\');
                        if (!rel.StartsWith("\\")) rel = "\\" + rel;
                        if (seen.Add("TASK:" + rel))
                            found.Add(new Leftover
                            {
                                Type = Leftover.LType.Task,
                                Target = rel,
                                Display = $"Tâche planifiée : {taskName}",
                            });
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

            var items = _pendingCleanup;
            var (cleaned, errors) = await Task.Run(() => CleanLeftovers(items));

            _pendingCleanup = new();
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
            // fullPath ex. "HKLM\SOFTWARE\WOW6432Node\Discord"
            var idx = fullPath.IndexOf('\\');
            if (idx <= 0) return;
            var prefix = fullPath.Substring(0, idx);
            var sub    = fullPath.Substring(idx + 1);

            var hive = prefix.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
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
            // 1. Winget par ID (le plus propre)
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

            // 2. UninstallString du registre (MSI ou NSIS)
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
                    using var p = Process.Start(new ProcessStartInfo(exe, args)
                    { UseShellExecute = true });
                    p?.WaitForExit(120_000);
                    return true;   // l'installeur s'est lancé, on considère OK
                }
                catch { }
            }

            // 3. Winget par nom (dernier recours)
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
                ? $"{total} application{(total > 1 ? "s" : "")}"
                : $"{visible} / {total} application{(total > 1 ? "s" : "")}";
        }
    }
}
