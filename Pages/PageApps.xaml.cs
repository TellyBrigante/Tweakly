using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        /// <summary>Initiale affichée dans l'avatar de la liste (refonte visuelle v1.3.4).</summary>
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "•"
            : char.ToUpperInvariant(Name.TrimStart()[0]).ToString();

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

    // ── Page ──────────────────────────────────────────────────────────────────

    internal sealed class Leftover
    {
        internal enum LType { Reg, File, Task }
        internal LType Type { get; init; }
        internal string Target { get; init; } = "";
        internal string Display { get; init; } = "";
    }

    public partial class PageApps : UserControl
    {
        private readonly MainWindow                          _main;
        private readonly ObservableCollection<AppItem>      _apps = new();
        private          ICollectionView?                   _view;
        private          bool                               _loaded   = false;
        private          bool                               _updating = false;
        private          CancellationTokenSource?           _updateCts;
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

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
            => _updateCts?.Cancel();

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
            {
                // Stats vivantes : chaque changement de statut re-calcule le bandeau
                // (les items sont recréés à chaque LoadAppsAsync → pas de fuite d'abonnement).
                a.PropertyChanged += (_, ev) =>
                {
                    if (ev.PropertyName == nameof(AppItem.UpdateStatus))
                        Dispatcher.BeginInvoke(UpdateStats);
                };
                _apps.Add(a);
            }

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
                        WindowsSystemTools.PathFor("cmd.exe"),
                        $"/d /c chcp 65001 > nul && winget.exe {arguments} > \"{tmp}\" 2>&1",
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
            ProcessCommandResult direct = ProcessCommand.Run(WingetCli.UserExecutablePath, arguments, 60_000);
            if (direct.Success)
            {
                string raw = Regex.Replace(direct.Output, "\x1B\\[[0-9;?]*[A-Za-z]", "");
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    _main.Log($"Applications : winget direct OK — {raw.Length} chars.");
                    return raw;
                }
                _main.Log("Applications : winget direct terminé, sortie vide.");
            }
            else
            {
                _main.Log("Applications : winget direct — " + direct.FailureDescription);
            }

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

        // Codes d'erreur winget courants — reconnaître les cas « déjà à jour » et « rien à faire »
        // pour ne pas afficher « Échec » alors que tout va bien.
        private static bool WingetIsBenign(int exitCode) =>
            exitCode ==  -1978335109   // APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE
         || exitCode ==  -1978335146   // ERROR_UPDATE_ALL_HAS_FAILURE … ou no apps to update
         || exitCode ==  -1978335189;  // ERROR_NO_APPLICABLE_UPDATE_FOUND

        private static bool IsRuntimeUpdateUnsafeForRunningTweakly(AppItem app) =>
            app.WingetId.StartsWith("Microsoft.DotNet.", StringComparison.OrdinalIgnoreCase)
            || app.Name.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase)
            || app.Name.Contains("Windows Desktop Runtime", StringComparison.OrdinalIgnoreCase);

        // Process .exe qui doit être fermé pour permettre la MAJ de telle app (mapping conservateur,
        // ajouts ponctuels au fil du retour utilisateur).
        private static readonly Dictionary<string, string[]> _appProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Discord.Discord"]       = new[] { "Discord", "DiscordPTB", "DiscordCanary" },
            ["TeamSpeakSystems.TeamSpeakClient"] = new[] { "ts3client_win64", "ts3client_win32" },
            ["Valve.Steam"]           = new[] { "steam", "steamwebhelper" },
            ["Spotify.Spotify"]       = new[] { "Spotify" },
            ["Slack.Slack"]           = new[] { "slack" },
            ["Microsoft.VisualStudioCode"] = new[] { "Code" },
        };

        private static string[]? RunningProcessesFor(string wingetId)
        {
            if (!_appProcesses.TryGetValue(wingetId, out var names)) return null;
            var running = new List<string>();
            foreach (var n in names)
            {
                Process[] processes = Process.GetProcessesByName(n);
                try
                {
                    if (processes.Length > 0) running.Add(n);
                }
                finally
                {
                    foreach (Process process in processes) process.Dispose();
                }
            }
            return running.Count > 0 ? running.ToArray() : null;
        }

        private async Task UpdateSingleAppAsync(AppItem app, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(app.WingetId)) return;
            if (!WingetCli.IsValidPackageId(app.WingetId))
            {
                app.UpdateStatus = UpdateStatus.Failed;
                _main.Log($"Applications : ID Winget invalide pour « {app.Name} ».");
                return;
            }
            if (IsRuntimeUpdateUnsafeForRunningTweakly(app))
            {
                app.UpdateStatus = UpdateStatus.Unknown;
                _main.Log($"Applications : MAJ ignorée pour « {app.Name} » — runtime .NET utilisé par Tweakly pendant l'exécution.");
                return;
            }

            app.UpdateStatus = UpdateStatus.Updating;
            TxtStatus.Text   = $"Mise à jour de « {app.Name} »…";
            _main.Log($"Applications : mise à jour de « {app.Name} »…");

            // ── 1) Pré-check : l'app est-elle en cours d'exécution ? ─────────────
            //    L'installeur winget ne peut pas remplacer un .exe verrouillé →
            //    cause #1 des « refus de MAJ » pour Discord/TeamSpeak/Steam etc.
            var running = RunningProcessesFor(app.WingetId);
            if (running != null)
            {
                _main.Log($"Applications : « {app.Name} » est en cours d'exécution ({string.Join(", ", running)}.exe) — ferme-le et réessaie.");
                app.UpdateStatus = UpdateStatus.Failed;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var (exit, output) = await Task.Run(() => RunWingetCmd(
                $"upgrade --id \"{app.WingetId}\" --exact --source {WingetCli.CommunitySource} " +
                "--silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                cancellationToken), cancellationToken);

            if (exit == 0)
            {
                app.AvailableVersion = "";
                app.UpdateStatus     = UpdateStatus.Updated;
                _main.Log($"Applications : « {app.Name} » mise à jour avec succès.");
                return;
            }
            if (WingetIsBenign(exit) ||
                output.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("déjà install", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("No applicable update", StringComparison.OrdinalIgnoreCase))
            {
                app.AvailableVersion = "";
                app.UpdateStatus     = UpdateStatus.Updated;
                _main.Log($"Applications : « {app.Name} » déjà à jour (winget ec={exit}).");
                return;
            }

            app.UpdateStatus = UpdateStatus.Failed;
            string firstLine = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            _main.Log($"Applications : échec MAJ « {app.Name} » — winget ec={exit} | {firstLine}");
        }

        // Lance winget (DÉ-ÉLEVÉ, contexte user) et capture stdout+stderr + code d'erreur.
        // Indispensable pour distinguer « rien à faire » d'un vrai échec.
        private (int exit, string output) RunWingetCmd(
            string args,
            CancellationToken cancellationToken)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"tweakly_wgu_{Guid.NewGuid():N}.txt");
            try
            {
                int exit = DeElevatedLauncher.StartAndWait(
                    WindowsSystemTools.PathFor("cmd.exe"),
                    $"/d /c chcp 65001 > nul && winget.exe {args} > \"{tmp}\" 2>&1",
                    300_000,
                    cancellationToken: cancellationToken);
                var output = File.Exists(tmp) ? File.ReadAllText(tmp, Encoding.UTF8) : "";
                output = Regex.Replace(output, "\x1B\\[[0-9;?]*[A-Za-z]", "");
                if (exit != 0 && string.IsNullOrWhiteSpace(output))
                {
                    ProcessCommandResult direct = ProcessCommand.Run(
                        WingetCli.UserExecutablePath,
                        args,
                        300_000,
                        cancellationToken);
                    string directOutput = Regex.Replace(
                        string.Join(Environment.NewLine, direct.Output, direct.Error),
                        "\x1B\\[[0-9;?]*[A-Za-z]",
                        "");
                    return (direct.ExitCode, directOutput);
                }
                return (exit, output);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (-1, "exception: " + ex.Message); }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private async void BtnUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            if ((sender as Button)?.Tag is not AppItem app) return;
            if (app.UpdateStatus is UpdateStatus.Updating or UpdateStatus.Updated) return;

            _updating              = true;
            BtnUpdateAll.IsEnabled = false;
            BtnRefresh.IsEnabled   = false;
            var operationCts = new CancellationTokenSource();
            _updateCts = operationCts;
            try
            {
                await UpdateSingleAppAsync(app, operationCts.Token);
                TxtStatus.Text = app.UpdateStatus == UpdateStatus.Updated
                    ? $"« {app.Name} » mise à jour avec succès."
                    : $"Échec de la mise à jour de « {app.Name} ».";
            }
            catch (OperationCanceledException)
            {
                if (app.UpdateStatus == UpdateStatus.Updating)
                    app.UpdateStatus = UpdateStatus.UpdateAvailable;
                TxtStatus.Text = $"Mise à jour de « {app.Name} » annulée.";
            }
            finally
            {
                BtnUpdateAll.IsEnabled = _apps.Any(a => a.UpdateStatus == UpdateStatus.UpdateAvailable);
                BtnRefresh.IsEnabled   = true;
                _updating              = false;
                if (ReferenceEquals(_updateCts, operationCts)) _updateCts = null;
                operationCts.Dispose();
            }
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
            var operationCts = new CancellationTokenSource();
            _updateCts = operationCts;
            try
            {
                foreach (var app in toUpdate)
                    await UpdateSingleAppAsync(app, operationCts.Token);

                int updated = toUpdate.Count(a => a.UpdateStatus == UpdateStatus.Updated);
                int failed  = toUpdate.Count(a => a.UpdateStatus == UpdateStatus.Failed);
                TxtStatus.Text = failed > 0
                    ? $"Mises à jour : {updated} réussie(s), {failed} échouée(s)."
                    : $"{updated} application(s) mise(s) à jour avec succès.";
                _main.Log($"Applications : MAJ groupée — {updated} réussie(s), {failed} échouée(s).");
            }
            catch (OperationCanceledException)
            {
                foreach (AppItem app in toUpdate.Where(a => a.UpdateStatus == UpdateStatus.Updating))
                    app.UpdateStatus = UpdateStatus.UpdateAvailable;
                TxtStatus.Text = "Mise à jour groupée annulée.";
                _main.Log("Applications : mise à jour groupée annulée.");
            }
            finally
            {
                BtnUpdateAll.IsEnabled    = _apps.Any(a => a.UpdateStatus == UpdateStatus.UpdateAvailable);
                BtnRefresh.IsEnabled      = true;
                BtnDesinstaller.IsEnabled = DgApps.SelectedItem != null;
                _updating                 = false;
                if (ReferenceEquals(_updateCts, operationCts)) _updateCts = null;
                operationCts.Dispose();
            }
        }

        // ── Lecture registre ──────────────────────────────────────────────────

        private static List<AppItem> LoadFromRegistry()
        {
            return InstalledApplicationInventory.Read()
                .Select(app => new AppItem
                {
                    Name = app.Name,
                    Publisher = app.Publisher,
                    Version = app.Version,
                    InstallLocation = app.InstallLocation,
                    UninstallString = app.UninstallString,
                })
                .ToList();
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
            => WingetCli.IsValidPackageId(tok);

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
                TxtStatus.Text = "Désinstallée. Aucun nettoyage générique n'est effectué.";
                _main.Log("Applications : nettoyage générique volontairement désactivé — aucune appartenance fiable ne peut être prouvée.");
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
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("apps-leftovers-registry", "Applications : recherche de résidus registre", ex);
                    }
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
                        catch (Exception ex)
                        {
                            AppLog.ErrorOnce("apps-leftovers-publisher", "Applications : recherche de résidus éditeur", ex);
                        }
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
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("apps-leftovers-folder", "Applications : recherche de dossiers résiduels", ex);
                    }
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
                catch (Exception ex)
                {
                    AppLog.ErrorOnce("apps-leftovers-install-folder", "Applications : dossier d'installation résiduel", ex);
                }
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
                            catch (Exception ex)
                            {
                                AppLog.ErrorOnce("apps-leftovers-task-content", "Applications : lecture d'une tâche planifiée", ex);
                            }
                        }

                        if (!match) continue;

                        var rel = file.Substring(tasksRoot.Length).Replace('/', '\\');
                        if (!rel.StartsWith("\\")) rel = "\\" + rel;
                        if (seen.Add("TASK:" + rel))
                            found.Add(new Leftover { Type = Leftover.LType.Task, Target = rel, Display = $"Tâche planifiée : {taskName}" });
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("apps-leftovers-tasks", "Applications : inventaire des tâches planifiées", ex);
            }

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
            // Une correspondance textuelle n'est pas une preuve d'appartenance.
            // Refus fail-closed : aucun fichier, aucune clé et aucune tâche ne
            // sont supprimés par ce mécanisme générique.
            return (0, items.Count);
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
            ProcessCommandResult result = ProcessCommand.Run(
                "schtasks", $"/delete /tn \"{taskPath}\" /f", 15_000);
            if (result.Success) return;

            string detail = result.Error.Length > 0
                ? result.Error
                : result.Output.Length > 0
                    ? result.Output.Trim()
                    : $"code {result.ExitCode}";
            throw new InvalidOperationException("Suppression de la tâche impossible : " + detail);
        }

        // ── Désinstallation ───────────────────────────────────────────────────

        private static bool Uninstall(AppItem app)
        {
            if (!string.IsNullOrEmpty(app.WingetId))
            {
                int byId = RunDeElevatedUninstaller(
                    WingetCli.UserExecutablePath,
                    $"uninstall --id \"{app.WingetId}\" --exact --source {WingetCli.CommunitySource} " +
                    "--silent --accept-source-agreements --disable-interactivity");
                if (byId == 0) return true;
                AppLog.Write($"Applications : winget n'a pas désinstallé {app.Name} par ID — "
                    + $"code {byId}.");
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
                    exe = ResolveUninstallerPath(exe);
                    int exitCode = RunDeElevatedUninstaller(exe, args);
                    if (exitCode == 0)
                    {
                        return true;
                    }
                    else
                    {
                        AppLog.Write($"Applications : désinstalleur de {app.Name} terminé avec le code {exitCode}.");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("Applications : désinstalleur de " + app.Name, ex);
                }
            }

            return false;
        }

        private static int RunDeElevatedUninstaller(string executablePath, string arguments)
        {
            return DeElevatedLauncher.StartAndWait(executablePath, arguments, 120_000,
                Path.GetDirectoryName(executablePath));
        }

        private static string ResolveUninstallerPath(string executable)
        {
            string expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
            if (string.Equals(expanded, "msiexec", StringComparison.OrdinalIgnoreCase)
                || string.Equals(expanded, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                return WindowsSystemTools.PathFor("msiexec.exe");
            }

            if (!Path.IsPathFullyQualified(expanded))
                throw new InvalidOperationException("Chemin de désinstallation non absolu refusé.");

            string fullPath = Path.GetFullPath(expanded);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Désinstalleur introuvable.", fullPath);

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Désinstalleur situé derrière un lien de redirection refusé.");

            return fullPath;
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
            UpdateStats();
        }

        /// <summary>
        /// Bandeau de stats (refonte visuelle v1.3.4) : Total / À jour / MAJ dispo / Échecs.
        /// Recalculé depuis _apps à chaque changement de statut (hook PropertyChanged posé
        /// dans LoadAppsAsync) et à chaque UpdateCount. La tuile Échecs n'apparaît que s'il
        /// y en a (pas d'alarme rouge à zéro).
        /// </summary>
        private void UpdateStats()
        {
            int total  = _apps.Count;
            int ok     = _apps.Count(a => a.UpdateStatus is UpdateStatus.UpToDate or UpdateStatus.Updated);
            int avail  = _apps.Count(a => a.UpdateStatus == UpdateStatus.UpdateAvailable);
            int failed = _apps.Count(a => a.UpdateStatus == UpdateStatus.Failed);

            TxtStatTotal.Text  = total.ToString();
            TxtStatOk.Text     = ok.ToString();
            TxtStatAvail.Text  = avail.ToString();
            TxtStatFailed.Text = failed.ToString();
            StatFailedCard.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
