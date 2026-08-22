using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageNettoyage : UserControl
    {
        private readonly MainWindow _main;
        private bool _isRunning;
        private int _estimateGeneration;
        private readonly Dictionary<string, CleanupEstimateResult> _estimates = new();
        private readonly SemaphoreSlim _estimateLock = new(1, 1);
        private DateTime _estimatesUpdatedAt = DateTime.MinValue;

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
            public required Func<CleanupOperationResult> Work { get; init; }
        }

        public PageNettoyage(MainWindow main)
        {
            _main = main;
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateSelectionVisuals();
            await RefreshEstimatesAsync();
        }

        private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isRunning) return;

            for (var d = e.OriginalSource as DependencyObject; d != null;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is CheckBox) return;
            }

            if (sender is Border row && row.Tag is CheckBox chk && chk.IsEnabled)
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
            int residuesRemoved = 0;
            int skipped = 0;
            int errors = 0;
            int done = 0;

            foreach (var step in selected)
            {
                SetStepRunning(step.Ui, step.RunningText);

                CleanupOperationResult result;
                try
                {
                    result = await Task.Run(step.Work);
                }
                catch (Exception ex)
                {
                    result = new CleanupOperationResult
                    {
                        Errors = 1,
                        Summary = "Erreur",
                        Details = { ex.Message }
                    };
                }

                freed += result.Freed;
                ops += result.Ops;
                residues += result.Residues;
                residuesRemoved += result.ResiduesRemoved;
                skipped += result.Skipped;
                errors += result.Errors;

                foreach (var d in result.Details)
                    _main.Log("Nettoyage : " + d);

                SetStepDone(step.Ui, result);
                done++;
                TxtGlobalProgress.Text = $"{done} / {selected.Count} etape(s)";
            }

            _main.Log($"Nettoyage terminé - {FormatBytes(freed)} libérés ({ops} opération(s)).");

            string summary = BuildSummary(selected, freed, residues, residuesRemoved, skipped, errors);
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
            await RefreshEstimatesAsync(force: true);
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
            var uis = GetStepUis().ToDictionary(x => x.Check);
            var steps = new List<StepPlan>();

            if (ChkTemp.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkTemp],
                    RunningText = "Nettoyage...",
                    Work = () => CleanupOperations.CleanFolder(Path.GetTempPath())
                });

            if (ChkSystemTemp.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkSystemTemp],
                    RunningText = "Nettoyage...",
                    Work = () => CleanupOperations.CleanFolder(@"C:\Windows\Temp")
                });

            if (ChkPrefetch.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkPrefetch],
                    RunningText = "Nettoyage...",
                    Work = () => CleanupOperations.CleanFolder(@"C:\Windows\Prefetch")
                });

            if (ChkDXCache.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkDXCache],
                    RunningText = "Nettoyage...",
                    Work = () => CleanupOperations.CleanFolder(Path.Combine(local, "D3DSCache"))
                });

            if (ChkNvCache.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkNvCache],
                    RunningText = "Nettoyage...",
                    Work = () => CleanupOperationResult.Combine(
                        NvidiaCachePaths(local)
                            .Select(CleanupOperations.CleanFolder)
                            .ToArray())
                });

            if (ChkTrimSSD.IsChecked == true)
                steps.Add(new StepPlan { Ui = uis[ChkTrimSSD], RunningText = "Optimisation...", Work = CleanupOperations.RunTrim });

            if (ChkEventLogs.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkEventLogs],
                    RunningText = "Vidage...",
                    Work = CleanupOperations.ClearEventLogs
                });

            if (ChkResidues.IsChecked == true)
                steps.Add(new StepPlan
                {
                    Ui = uis[ChkResidues],
                    RunningText = "Recherche...",
                    Work = CleanupOperations.CleanSoftwareResidues
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
            ui.Status.Text = GetIdleStatus(ui);
            bool secondary = ui.Check.Name is nameof(ChkTrimSSD) or nameof(ChkResidues)
                || (_estimates.TryGetValue(ui.Check.Name, out CleanupEstimateResult? estimate)
                    && !estimate.Available);
            ui.Status.SetResourceReference(ForegroundProperty, secondary ? "ThTextDim" : "ThTextSub");
        }

        private async Task RefreshEstimatesAsync(bool force = false)
        {
            if (_isRunning) return;

            await _estimateLock.WaitAsync();
            try
            {
                if (_isRunning) return;
                if (!force
                    && _estimates.Count > 0
                    && DateTime.UtcNow - _estimatesUpdatedAt < TimeSpan.FromMinutes(1))
                {
                    UpdateSelectionVisuals();
                    return;
                }

                int generation = Interlocked.Increment(ref _estimateGeneration);
                foreach (StepUi ui in GetStepUis())
                {
                    if (ui.Check.Name == nameof(ChkTrimSSD))
                    {
                        ui.Status.Text = "Optimisation, aucun fichier supprimé";
                        ui.Status.SetResourceReference(ForegroundProperty, "ThTextDim");
                        continue;
                    }

                    if (ui.Check.Name == nameof(ChkResidues))
                    {
                        ui.Status.Text = "Registre sauvegardé avant suppression";
                        ui.Status.SetResourceReference(ForegroundProperty, "ThTextDim");
                        continue;
                    }
                    ui.Status.Text = "Calcul en cours...";
                    ui.Status.SetResourceReference(ForegroundProperty, "ThTextDim");
                }

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string eventLogs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "winevt", "Logs");

                var work = new (StepUi Ui, Func<CleanupEstimateResult> Measure)[]
                {
                    (GetStepUi(ChkTemp), () => CleanupOperations.EstimateFolder(Path.GetTempPath())),
                    (GetStepUi(ChkSystemTemp), () => CleanupOperations.EstimateFolder(@"C:\Windows\Temp")),
                    (GetStepUi(ChkPrefetch), () => CleanupOperations.EstimateFolder(@"C:\Windows\Prefetch")),
                    (GetStepUi(ChkDXCache), () => CleanupOperations.EstimateFolder(Path.Combine(local, "D3DSCache"))),
                    (GetStepUi(ChkNvCache), () => CleanupEstimateResult.Combine(
                        NvidiaCachePaths(local)
                            .Select(static path => CleanupOperations.EstimateFolder(path))
                            .ToArray())),
                    (GetStepUi(ChkEventLogs), () => CleanupOperations.EstimateFolder(eventLogs, "*.evtx", recursive: false)),
                };

                foreach ((StepUi ui, Func<CleanupEstimateResult> measure) in work)
                {
                    CleanupEstimateResult estimate = await Task.Run(measure);
                    if (generation != _estimateGeneration || _isRunning) return;

                    _estimates[ui.Check.Name] = estimate;
                    ui.Status.Text = FormatEstimate(estimate);
                    ui.Status.SetResourceReference(ForegroundProperty, "ThTextSub");
                }

                _estimatesUpdatedAt = DateTime.UtcNow;
            }
            finally
            {
                _estimateLock.Release();
            }
        }

        private StepUi GetStepUi(CheckBox check) =>
            GetStepUis().First(ui => ReferenceEquals(ui.Check, check));

        private static string[] NvidiaCachePaths(string localApplicationData) =>
        [
            Path.Combine(localApplicationData, "NVIDIA", "DXCache"),
            Path.Combine(localApplicationData, "NVIDIA", "GLCache"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NVIDIA Corporation",
                "NV_Cache"),
        ];

        private string GetIdleStatus(StepUi ui)
        {
            if (ui.Check.Name == nameof(ChkTrimSSD))
                return "Optimisation, aucun fichier supprimé";
            if (ui.Check.Name == nameof(ChkResidues))
                return "Registre et raccourcis, taille négligeable";
            return _estimates.TryGetValue(ui.Check.Name, out CleanupEstimateResult? estimate)
                ? FormatEstimate(estimate)
                : "Calcul en cours...";
        }

        private static string FormatEstimate(CleanupEstimateResult estimate)
        {
            if (!estimate.Available)
                return "Analyse indisponible";

            string size = FormatBytes(estimate.Bytes);
            return estimate.Skipped > 0
                ? $"Jusqu'à {size} récupérables"
                : $"{size} récupérables";
        }

        private static void SetStepRunning(StepUi ui, string text)
        {
            ui.Status.Text = text;
            ui.Status.SetResourceReference(ForegroundProperty, "ThAccentIcon");
            ui.Progress.IsIndeterminate = false;
            ui.Progress.Value = 35;
            ui.Progress.SetResourceReference(Control.ForegroundProperty, "ThAccentIcon");
        }

        private static void SetStepDone(StepUi ui, CleanupOperationResult result)
        {
            ui.Progress.IsIndeterminate = false;
            ui.Progress.Value = 100;
            string brush = result.Errors > 0 ? "ThWarn" : "ThOk";
            ui.Status.Text = string.IsNullOrWhiteSpace(result.Summary) ? "Terminé" : result.Summary;
            ui.Status.SetResourceReference(ForegroundProperty, brush);
            ui.Progress.SetResourceReference(Control.ForegroundProperty, brush);
        }

        private static string BuildSummary(
            IEnumerable<StepPlan> selected,
            long freed,
            int residues,
            int residuesRemoved,
            int skipped,
            int errors)
        {
            var list = selected.ToList();
            var parts = new List<string>();
            if (freed > 0) parts.Add($"{FormatBytes(freed)} libérés");
            if (skipped > 0) parts.Add($"{skipped} ignoré(s)");
            if (list.Any(s => s.Ui.Check.Name == nameof(ChkTrimSSD))) parts.Add("TRIM terminé");
            if (list.Any(s => s.Ui.Check.Name == nameof(ChkEventLogs))) parts.Add("journaux Windows traités");
            if (list.Any(s => s.Ui.Check.Name == nameof(ChkResidues)))
                parts.Add(residues > 0
                    ? $"{residuesRemoved} traité(s) sur {residues} résidu(s) détecté(s)"
                    : "aucun résidu sûr détecté");

            string summary = parts.Count > 0
                ? "Terminé - " + string.Join(" | ", parts)
                : "Terminé - 0 octet à nettoyer";
            if (errors > 0) summary += $" | {errors} erreur(s)";
            return summary;
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
