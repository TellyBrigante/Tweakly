using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                        CleanupOperations.CleanFolder(Path.Combine(local, "NVIDIA", "DXCache")),
                        CleanupOperations.CleanFolder(Path.Combine(roaming, "NVIDIA", "GLCache")))
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

        private static void SetStepDone(StepUi ui, CleanupOperationResult result)
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

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} Go";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} Mo";
            if (bytes >= 1_024) return $"{bytes / 1_024.0:F0} Ko";
            return $"{bytes} octets";
        }
    }
}
