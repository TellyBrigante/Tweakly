using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Optimisation_Tool.Pages
{
    public partial class PageNettoyage : UserControl
    {
        private readonly MainWindow _main;
        private bool _isRunning;

        private sealed class StepUi
        {
            public required CheckBox Check { get; init; }
            public required TextBlock Status { get; init; }
            public required ProgressBar Progress { get; init; }
        }

        private sealed class StepPlan
        {
            public required StepUi Ui { get; init; }
            public required string RunningText { get; init; }
            public required Func<CleanupResult> Work { get; init; }
        }

        private sealed class CleanupResult
        {
            public long Freed { get; init; }
            public int Ops { get; init; }
            public int Residues { get; init; }
            public int Skipped { get; init; }
            public int Errors { get; init; }
            public string Summary { get; init; } = "";
            public List<string> Details { get; init; } = new();
        }

        public PageNettoyage(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateSelectionVisuals();
        }

        private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isRunning) return;

            for (var d = e.OriginalSource as DependencyObject; d != null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is CheckBox) return;
            }

            if (sender is Border row && row.Tag is CheckBox chk)
                chk.IsChecked = chk.IsChecked != true;
        }

        private void Chk_Changed(object sender, RoutedEventArgs e)
        {
            if (BtnLancer == null) return;
            int n = new[] { ChkTemp, ChkSystemTemp, ChkPrefetch, ChkDXCache,
                            ChkNvCache, ChkTrimSSD, ChkEventLogs, ChkResidues }
                .Count(c => c?.IsChecked == true);

            BtnLancer.Content = n > 0 ? $"EXÉCUTER LE NETTOYAGE ({n})" : "EXÉCUTER LE NETTOYAGE";
            UpdateSelectionVisuals();
        }

        private async void BtnLancer_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            var selected = BuildSelectedSteps();
            if (selected.Count == 0)
            {
                _main.Log("Nettoyage : aucune option sélectionnée.");
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, false,
                    "", "Sélectionne au moins une cible.");
                return;
            }

            _isRunning = true;
            BtnLancer.IsEnabled = false;
            TxtLastSummary.Text = "";
            TxtGlobalProgress.Text = $"0 / {selected.Count} étape(s)";
            StatusBanner.Visibility = Visibility.Collapsed;

            foreach (var ui in GetStepUis())
                SetStepIdle(ui);

            _main.Log("Nettoyage en cours...");

            long freed = 0;
            int ops = 0;
            int residues = 0;
            int skipped = 0;
            int errors = 0;
            int done = 0;

            foreach (var step in selected)
            {
                SetStepRunning(step.Ui, step.RunningText);

                CleanupResult result;
                try
                {
                    result = await Task.Run(step.Work);
                }
                catch (Exception ex)
                {
                    result = new CleanupResult
                    {
                        Errors = 1,
                        Summary = "Erreur",
                        Details = { ex.Message }
                    };
                }

                freed += result.Freed;
                ops += result.Ops;
                residues += result.Residues;
                skipped += result.Skipped;
                errors += result.Errors;

                foreach (var d in result.Details)
                    _main.Log("Nettoyage : " + d);

                SetStepDone(step.Ui, result);
                done++;
                TxtGlobalProgress.Text = $"{done} / {selected.Count} etape(s)";
            }

            _main.Log($"Nettoyage terminé - {FormatBytes(freed)} libérés ({ops} opération(s)).");

            string summary = BuildSummary(selected, freed, residues, skipped, errors);
            TxtLastSummary.Text = summary;
            if (errors == 0)
            {
                string okText = skipped > 0
                    ? summary + ". Fichiers ouverts ignorés, aucune action requise."
                    : summary;
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, true, okText, "");
            }
            else
            {
                Helpers.TweakFeedback.ShowSimple(StatusBanner, StatusDot, StatusText, false, summary,
                    "Nettoyage incomplet : une cible n'a pas pu être ouverte.");
            }

            foreach (var step in selected)
                step.Ui.Check.IsChecked = false;

            _isRunning = false;
            BtnLancer.IsEnabled = true;
            UpdateSelectionVisuals(keepFinished: true);
        }

        private StepUi[] GetStepUis() =>
        [
            new StepUi { Check = ChkTemp,       Status = TxtTempStep,       Progress = PbTempStep },
            new StepUi { Check = ChkSystemTemp, Status = TxtSystemTempStep, Progress = PbSystemTempStep },
            new StepUi { Check = ChkPrefetch,   Status = TxtPrefetchStep,   Progress = PbPrefetchStep },
            new StepUi { Check = ChkDXCache,    Status = TxtDXStep,         Progress = PbDXStep },
            new StepUi { Check = ChkNvCache,    Status = TxtNvStep,         Progress = PbNvStep },
            new StepUi { Check = ChkTrimSSD,    Status = TxtTrimStep,       Progress = PbTrimStep },
            new StepUi { Check = ChkEventLogs,  Status = TxtEventLogsStep,  Progress = PbEventLogsStep },
            new StepUi { Check = ChkResidues,   Status = TxtResiduesStep,   Progress = PbResiduesStep },
        ];

        private List<StepPlan> BuildSelectedSteps()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var uis = GetStepUis().ToDictionary(x => x.Check);
            var steps = new List<StepPlan>();

            if (ChkTemp.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkTemp],
                    RunningText = "Nettoyage...",
                    Work = () => CleanFolderStep(Path.GetTempPath())
                });

            if (ChkSystemTemp.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkSystemTemp],
                    RunningText = "Nettoyage...",
                    Work = () => CleanFolderStep(@"C:\Windows\Temp")
                });

            if (ChkPrefetch.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkPrefetch],
                    RunningText = "Nettoyage...",
                    Work = () => CleanFolderStep(@"C:\Windows\Prefetch")
                });

            if (ChkDXCache.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkDXCache],
                    RunningText = "Nettoyage...",
                    Work = () => CleanFolderStep(Path.Combine(local, "D3DSCache"))
                });

            if (ChkNvCache.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkNvCache],
                    RunningText = "Nettoyage...",
                    Work = () => CombineResults(
                        CleanFolderStep(Path.Combine(local, "NVIDIA", "DXCache")),
                        CleanFolderStep(Path.Combine(roaming, "NVIDIA", "GLCache")))
                });

            if (ChkTrimSSD.IsChecked == true)
                steps.Add(new StepPlan { Ui = uis[ChkTrimSSD], RunningText = "Optimisation...", Work = RunTrimStep });

            if (ChkEventLogs.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkEventLogs],
                    RunningText = "Vidage...",
                    Work = () =>
                    {
                        int n = ClearEventLogs();
                        return new CleanupResult { Ops = n, Summary = $"{n} journal(aux) traité(s)" };
                    }
                });

            if (ChkResidues.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkResidues],
                    RunningText = "Recherche...",
                    Work = () =>
                    {
                        int n = CleanSoftwareResidues();
                        return new CleanupResult
                        {
                            Ops = n,
                            Residues = n,
                            Summary = n > 0 ? $"{n} résidu(s)" : "0 résidu sûr"
                        };
                    }
                });

            return steps;
        }

        private void UpdateSelectionVisuals(bool keepFinished = false)
        {
            if (_isRunning) return;

            int selected = 0;
            foreach (var ui in GetStepUis())
            {
                if (ui.Check.IsChecked == true) selected++;
                if (!keepFinished || ui.Check.IsChecked == true)
                    SetStepIdle(ui);
            }

            TxtGlobalProgress.Text = selected > 0
                ? $"{selected} cible(s) sélectionnée(s)"
                : "Aucune cible sélectionnée";
        }

        private void SetStepIdle(StepUi ui)
        {
            ui.Progress.IsIndeterminate = false;
            ui.Progress.Value = 0;
            ui.Progress.SetResourceReference(Control.ForegroundProperty, "ThAccentIcon");
            ui.Status.Text = ui.Check.IsChecked == true ? "Prêt" : "Désactivé";
            ui.Status.SetResourceReference(ForegroundProperty,
                ui.Check.IsChecked == true ? "ThTextSub" : "ThTextDim");
        }

        private static void SetStepRunning(StepUi ui, string text)
        {
            ui.Status.Text = text;
            ui.Status.SetResourceReference(ForegroundProperty, "ThAccentIcon");
            ui.Progress.IsIndeterminate = false;
            ui.Progress.Value = 35;
            ui.Progress.SetResourceReference(Control.ForegroundProperty, "ThAccentIcon");
        }

        private static void SetStepDone(StepUi ui, CleanupResult result)
        {
            ui.Progress.IsIndeterminate = false;
            ui.Progress.Value = 100;
            string brush = result.Errors > 0 ? "ThWarn" : "ThOk";
            ui.Status.Text = string.IsNullOrWhiteSpace(result.Summary) ? "Terminé" : result.Summary;
            ui.Status.SetResourceReference(ForegroundProperty, brush);
            ui.Progress.SetResourceReference(Control.ForegroundProperty, brush);
        }

        private static string BuildSummary(IEnumerable<StepPlan> selected, long freed, int residues, int skipped, int errors)
        {
            var list = selected.ToList();
            var parts = new List<string>();
            if (freed > 0) parts.Add($"{FormatBytes(freed)} libérés");
            if (skipped > 0) parts.Add($"{skipped} ignoré(s)");
            if (list.Any(s => s.Ui.Check.Name == nameof(ChkTrimSSD))) parts.Add("TRIM terminé");
            if (list.Any(s => s.Ui.Check.Name == nameof(ChkEventLogs))) parts.Add("journaux Windows traités");
            if (list.Any(s => s.Ui.Check.Name == nameof(ChkResidues)))
                parts.Add(residues > 0 ? $"{residues} résidu(s) retiré(s)" : "0 résidu sûr");

            string summary = parts.Count > 0
                ? "Terminé - " + string.Join(" | ", parts)
                : "Terminé - 0 octet à nettoyer";
            if (errors > 0) summary += $" | {errors} erreur(s)";
            return summary;
        }

        private static CleanupResult CleanFolderStep(string path)
        {
            if (!Directory.Exists(path))
                return new CleanupResult { Summary = "Introuvable" };

            long freed = 0;
            int ops = 0;
            int skipped = 0;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*", opts).ToList(); }
            catch { return new CleanupResult { Errors = 1, Summary = "Accès refusé" }; }

            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    long len = fi.Exists ? fi.Length : 0;
                    fi.Delete();
                    freed += len;
                    ops++;
                }
                catch { skipped++; }
            }

            try
            {
                foreach (var d in Directory.EnumerateDirectories(path).ToList())
                {
                    try { Directory.Delete(d, true); ops++; }
                    catch { skipped++; }
                }
            }
            catch { skipped++; }

            string summary = FormatStepSummary(freed, ops, skipped, errors: 0);

            return new CleanupResult { Freed = freed, Ops = ops, Skipped = skipped, Summary = summary };
        }

        private static CleanupResult CombineResults(params CleanupResult[] results)
        {
            long freed = results.Sum(r => r.Freed);
            int ops = results.Sum(r => r.Ops);
            int skipped = results.Sum(r => r.Skipped);
            int errors = results.Sum(r => r.Errors);
            var details = results.SelectMany(r => r.Details).ToList();
            string summary = FormatStepSummary(freed, ops, skipped, errors);

            return new CleanupResult { Freed = freed, Ops = ops, Skipped = skipped, Errors = errors, Summary = summary, Details = details };
        }

        private static CleanupResult RunTrimStep()
        {
            int total = 0;
            int okCount = 0;
            int skipped = 0;
            int errors = 0;
            var details = new List<string>();

            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;

                total++;
                var letter = di.Name.TrimEnd('\\');
                try
                {
                    var psi = new ProcessStartInfo("defrag", $"{letter} /L")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    using var p = Process.Start(psi);
                    _ = (p?.StandardOutput.ReadToEnd() ?? "") + (p?.StandardError.ReadToEnd() ?? "");
                    p?.WaitForExit(120_000);

                    bool ok = p != null && p.ExitCode == 0;
                    if (ok) okCount++; else skipped++;
                    details.Add(ok
                        ? $"TRIM (optimisation SSD) - {letter} : terminé"
                        : $"TRIM - {letter} : ignoré (volume non compatible ou occupé)");
                }
                catch (Exception ex)
                {
                    errors++;
                    details.Add($"TRIM - {letter} : erreur ({ex.Message})");
                }
            }

            string summary = total == 0 ? "0 volume" : $"{okCount}/{total} volume(s)";
            if (skipped > 0) summary += $", {skipped} ignoré(s)";
            if (errors > 0) summary += $" | {errors} erreur(s)";
            return new CleanupResult { Ops = total, Skipped = skipped, Errors = errors, Summary = summary, Details = details };
        }

        private static string FormatStepSummary(long freed, int ops, int skipped, int errors)
        {
            var parts = new List<string>();
            parts.Add(freed > 0 ? FormatBytes(freed) : "0 octet");
            if (ops > 0) parts.Add($"{ops} élément(s)");
            if (skipped > 0) parts.Add($"{skipped} ignoré(s)");
            if (errors > 0) parts.Add($"{errors} erreur(s)");
            return string.Join(" | ", parts);
        }

        private static int ClearEventLogs()
        {
            int count = 0;
            try
            {
                using var listProc = Process.Start(new ProcessStartInfo("wevtutil", "el")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });
                if (listProc == null) return 0;

                var logs = listProc.StandardOutput.ReadToEnd();
                listProc.WaitForExit(15_000);

                foreach (var log in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = log.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo("wevtutil", $"cl \"{name}\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        p?.WaitForExit(5_000);
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        private static int CleanSoftwareResidues()
        {
            int n = 0;
            try { n += CleanOrphanUninstallEntries(); } catch { }

            try
            {
                int shortcuts = 0;
                var th = new Thread(() => { try { shortcuts = CleanBrokenShortcuts(); } catch { } });
                th.SetApartmentState(ApartmentState.STA);
                th.IsBackground = true;
                th.Start();
                th.Join(20_000);
                n += shortcuts;
            }
            catch { }
            return n;
        }

        private static int CleanOrphanUninstallEntries()
        {
            int n = 0;
            var roots = new (RegistryKey hive, string path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach (var (hive, path) in roots)
            {
                try
                {
                    using var root = hive.OpenSubKey(path, writable: true);
                    if (root == null) continue;

                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            string? name, loc;
                            using (var k = root.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                name = k.GetValue("DisplayName") as string;
                                loc = k.GetValue("InstallLocation") as string;
                            }

                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) continue;
                            loc = loc.Trim().Trim('"').TrimEnd('\\');
                            if (loc.Length < 4 || !loc.Contains(":\\")) continue;
                            var drive = Path.GetPathRoot(loc);
                            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) continue;
                            if (Directory.Exists(loc)) continue;

                            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                            n++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return n;
        }

        private static int CleanBrokenShortcuts()
        {
            int n = 0;
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }.Where(d => !string.IsNullOrEmpty(d)).Distinct();

            dynamic? shell;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return 0;
                shell = Activator.CreateInstance(t);
            }
            catch { return 0; }
            if (shell == null) return 0;

            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                IEnumerable<string> lnks;
                try { lnks = Directory.EnumerateFiles(d, "*.lnk", opts).ToList(); }
                catch { continue; }

                foreach (var lnk in lnks)
                {
                    try
                    {
                        dynamic sc = shell.CreateShortcut(lnk);
                        string target = sc.TargetPath as string ?? "";
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var drive = Path.GetPathRoot(target);
                        if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) continue;
                        if (File.Exists(target)) continue;
                        File.Delete(lnk);
                        n++;
                    }
                    catch { }
                }
            }
            return n;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} Go";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} Mo";
            if (bytes >= 1_024) return $"{bytes / 1_024.0:F0} Ko";
            return $"{bytes} octets";
        }
    }
}
