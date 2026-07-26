using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GpuTuningLab.Core;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageGpuTuning : UserControl
    {
        private readonly EvaluationPolicy _policy;
        private readonly LocalGpuLabService _service;
        private readonly string _policyLoadError;
        private CancellationTokenSource? _runCancellation;
        private bool _loaded;
        private bool _readyForBaseline;
        private bool _baselineValid;
        private bool _runningProfile;
        private bool _actionPending;
        private GpuProfileSuggestion? _profileSuggestion;

        public PageGpuTuning()
        {
            (_policy, _policyLoadError) = LoadPolicy();
            _service = new LocalGpuLabService(_policy);
            InitializeComponent();
            Application.Current.Exit += (_, _) => _runCancellation?.Cancel();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (!TryBeginAction()) return;
            _readyForBaseline = false;
            _baselineValid = false;
            _profileSuggestion = null;
            AdviceCard.Visibility = Visibility.Collapsed;
            TxtProtocolState.Text = "Vérification du GPU et des outils de test…";
            ProfileResult.Visibility = Visibility.Collapsed;
            try
            {
                if (_policyLoadError.Length > 0)
                {
                    _readyForBaseline = false;
                    _baselineValid = false;
                    TxtProtocolState.Text =
                        "Mesures bloquées : les règles d'évaluation GPU ne sont pas valides. " +
                        _policyLoadError;
                    TxtProtocolState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                    UpdateActionAvailability();
                    return;
                }

                GpuLabReadiness readiness = await _service.InspectAsync(PathLayout.GpuTuningTools);
                RenderReadiness(readiness);
                GpuBaselineStatus baseline = await _service.GetLatestBaselineStatusAsync(
                    PathLayout.GpuTuningSession,
                    readiness.Package.Fingerprint,
                    readiness.Identity);
                RenderBaseline(baseline);
                try
                {
                    GpuAdviceStatus advice = await _service.GetInitialProfileAdviceAsync(
                        PathLayout.GpuTuningSession,
                        PathLayout.GpuTuningEvidence,
                        readiness.Identity,
                        readiness.Package.Fingerprint);
                    RenderAdvice(advice);
                }
                catch (Exception ex)
                {
                    AdviceCard.Visibility = Visibility.Collapsed;
                    _profileSuggestion = null;
                    AppLog.Error("Optimisation GPU : calcul du point de départ", ex);
                }

                if (_baselineValid)
                {
                    try
                    {
                        GpuProfileMeasurementResult? latest =
                            await _service.GetLatestProfileMeasurementResultAsync(
                                PathLayout.GpuTuningSession,
                                PathLayout.GpuTuningEvidence,
                                readiness.Identity,
                                readiness.Package.Fingerprint);
                        if (latest != null)
                            RenderProfileResult(latest);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Optimisation GPU : rechargement du dernier resultat", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                TxtProtocolState.Text = "Impossible de vérifier le protocole : " + ex.Message;
                TxtProtocolState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Error("Optimisation GPU : vérification", ex);
            }
            finally
            {
                EndAction();
            }
        }

        private void RenderAdvice(GpuAdviceStatus status)
        {
            _profileSuggestion = status.Suggestion;
            AdviceCard.Visibility = status.Available && status.Suggestion != null
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (status.Suggestion is not GpuProfileSuggestion suggestion) return;

            GpuTuningProfile profile = suggestion.Profile;
            TxtAdviceTitle.Text = "Point de départ calculé";
            TxtAdviceConfidence.Text = $"Confiance {suggestion.Confidence.ToLowerInvariant()}";
            TxtAdviceValues.Text = SuggestionValues(profile);
            TxtAdviceDetail.Text = status.Message +
                $" Base publique : {suggestion.IndependentUnits} carte(s), {suggestion.IndependentSources} source(s), " +
                $"{suggestion.SupportingPoints} mesure(s) retenue(s), {suggestion.ExcludedFailurePoints} échec(s) écarté(s). " +
                "Ce point lance la recherche ; il ne valide pas encore la stabilité.";
        }

        private void BtnUseAdvice_Click(object sender, RoutedEventArgs e)
        {
            if (_profileSuggestion?.Profile is not GpuTuningProfile profile) return;
            TxtProfileName.Text = profile.Name;
            TxtProfileVoltage.Text = profile.TargetVoltageMv?.ToString(CultureInfo.InvariantCulture) ?? "";
            TxtProfileClock.Text = profile.TargetClockMhz?.ToString(CultureInfo.InvariantCulture) ?? "";
            TxtProfileMemory.Text = profile.MemoryOffsetMhz?.ToString(CultureInfo.InvariantCulture) ?? "0";
            TxtProfilePower.Text = profile.PowerLimitPercent?.ToString("0", CultureInfo.InvariantCulture) ?? "100";
            TxtProfileState.Text = "Valeurs copiées. Applique-les manuellement, puis lance la mesure.";
            TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
        }

        private void RenderReadiness(GpuLabReadiness readiness)
        {
            TxtGpu.Text = readiness.Identity?.Name ?? "GPU NVIDIA non détecté";
            TxtDriver.Text = readiness.Identity == null
                ? "—"
                : $"{readiness.Identity.DriverVersion} · {readiness.Identity.VbiosVersion}";
            TxtPower.Text = Watts(readiness.LatestSample?.EnforcedPowerLimitW);
            TxtMemory.Text = Megahertz(readiness.LatestSample?.MemoryClockMhz);
            _readyForBaseline = readiness.ReadyForBaseline;

            if (readiness.ReadyForBaseline)
            {
                TxtProtocolState.Text = "Prêt. Le GPU est à l'état attendu et aucun autre processus ne le charge actuellement.";
                TxtProtocolState.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
            }
            else if (readiness.ReadyForProfile)
            {
                TxtProtocolState.Text = "GPU disponible. Les valeurs visibles ne correspondent pas au stock : la référence ne peut pas être refaite, mais le profil actif peut être mesuré.";
                TxtProtocolState.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
            }
            else
            {
                TxtProtocolState.Text = readiness.BlockingReasons.Count == 0
                    ? "Le protocole n'est pas prêt. Actualise pour refaire la vérification."
                    : string.Join(Environment.NewLine, readiness.BlockingReasons.Select(ToFrenchReason));
                TxtProtocolState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
            }
            UpdateActionAvailability();
        }

        private void RenderBaseline(GpuBaselineStatus status)
        {
            if (!status.Exists)
            {
                _baselineValid = false;
                TxtBaselineStatus.Text = "Aucune mesure de référence enregistrée.";
                TxtBaselineBadge.Text = "À mesurer";
                TxtBaselineBadge.SetResourceReference(TextBlock.ForegroundProperty, "ThTextNav");
                UpdateActionAvailability();
                return;
            }

            string date = status.StartedAt?.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) ?? "date inconnue";
            bool valid = status.Validation?.Valid == true;
            _baselineValid = valid;
            string invalidReason = status.Validation?.Reasons
                .Select(ToFrenchReason)
                .FirstOrDefault() ?? "La mesure stock doit être refaite.";
            TxtBaselineStatus.Text = valid
                ? $"Référence valide du {date} · 3 passages terminés · variation maximale {status.Validation!.ScoreCoefficientOfVariationPercent:0.00} %."
                : $"Référence du {date} inutilisable. {invalidReason}";
            TxtBaselineBadge.Text = valid ? "Valide" : "À refaire";
            TxtBaselineBadge.SetResourceReference(TextBlock.ForegroundProperty, valid ? "ThOk" : "ThWarn");
            TxtProfileState.Text = valid
                ? "Référence stock prête. Applique ton profil, renseigne ses valeurs puis lance la mesure."
                : "La référence stock doit être refaite avant de mesurer un profil.";
            UpdateActionAvailability();
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBeginAction()) return;
            if (!EnsurePolicyAvailable(TxtRunState))
            {
                EndAction();
                return;
            }
            GpuLabReadiness readiness;
            try
            {
                readiness = await _service.InspectAsync(PathLayout.GpuTuningTools);
            }
            catch (Exception ex)
            {
                TxtRunState.Text = "Vérification impossible : " + ToFrenchReason(ex.Message);
                TxtRunState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Error("Optimisation GPU : vérification avant mesure stock", ex);
                EndAction();
                return;
            }
            RenderReadiness(readiness);
            if (!readiness.ReadyForBaseline)
            {
                EndAction();
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "FONCTION EXPÉRIMENTALE\n\n" +
                "Continue uniquement si tu sais remettre ton GPU à stock.\n\n" +
                "Confirme que le GPU est revenu à ses réglages d'origine dans MSI Afterburner ou l'outil utilisé.\n\n" +
                "La mesure dure environ 15 min et doit rester seule à utiliser le GPU. Tweakly effectuera 3 passages de 5 tests.",
                "Mesure GPU à stock",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                EndAction();
                return;
            }

            _runCancellation = new CancellationTokenSource();
            SetRunning(true, profile: false);
            EndAction();
            PbBaseline.Value = 0;
            TxtProgressPercent.Text = "0 %";
            var progress = new Progress<GpuBaselineProgress>(UpdateProgress);
            try
            {
                GpuBaselineExecutionResult result = await _service.RunStockBaselineAsync(
                    PathLayout.GpuTuningTools,
                    PathLayout.GpuTuningSession,
                    stockResetConfirmed: true,
                    workloadSeconds: 60,
                    progress: progress,
                    cancellationToken: _runCancellation.Token);
                if (result.Validation.Valid)
                {
                    PbBaseline.Value = 100;
                    TxtProgressPercent.Text = "100 %";
                    TxtRunState.Text = $"Mesure terminée. Variation maximale : {result.Validation.ScoreCoefficientOfVariationPercent:0.00} %.";
                    TxtRunState.SetResourceReference(TextBlock.ForegroundProperty, "ThOk");
                }
                else
                {
                    TxtRunState.Text = "Mesure refusée : " + string.Join(" · ", result.Validation.Reasons.Select(ToFrenchReason));
                    TxtRunState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                }
            }
            catch (OperationCanceledException)
            {
                TxtRunState.Text = "Mesure annulée. Les passages déjà terminés restent enregistrés.";
                TxtRunState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
            }
            catch (Exception ex)
            {
                TxtRunState.Text = "Mesure interrompue : " + ToFrenchReason(ex.Message);
                TxtRunState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Error("Optimisation GPU : mesure stock", ex);
            }
            finally
            {
                _runCancellation.Dispose();
                _runCancellation = null;
                SetRunning(false, profile: false);
                await RefreshAsync();
            }
        }

        private async void BtnProfileStart_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBeginAction()) return;
            if (!EnsurePolicyAvailable(TxtProfileState))
            {
                EndAction();
                return;
            }
            if (ChkStartupDisabled.IsChecked != true)
            {
                TxtProfileState.Text = "Désactive d'abord l'application automatique du profil au démarrage de Windows, puis confirme la ligne ci-dessus.";
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                EndAction();
                return;
            }
            string name = TxtProfileName.Text.Trim();
            if (name.Length is < 2 or > 60
                || !TryInt(TxtProfileVoltage.Text, out int voltage) || voltage is < 600 or > 1200
                || !TryInt(TxtProfileClock.Text, out int clock) || clock is < 300 or > 4000
                || !TryInt(TxtProfileMemory.Text, out int memory) || memory is < -4000 or > 4000
                || !TryDouble(TxtProfilePower.Text, out double power) || power is < 20 or > 150)
            {
                TxtProfileState.Text = "Vérifie les valeurs : tension 600–1 200 mV, fréquence 300–4 000 MHz, mémoire -4 000 à +4 000 MHz et Power Limit 20–150 %.";
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                EndAction();
                return;
            }

            GpuLabReadiness readiness;
            try
            {
                readiness = await _service.InspectAsync(PathLayout.GpuTuningTools);
            }
            catch (Exception ex)
            {
                TxtProfileState.Text = "Vérification impossible : " + ToFrenchReason(ex.Message);
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Error("Optimisation GPU : vérification avant mesure profil", ex);
                EndAction();
                return;
            }
            RenderReadiness(readiness);
            if (!_baselineValid)
            {
                TxtProfileState.Text = "La référence stock doit être valide avant de mesurer un profil.";
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                EndAction();
                return;
            }
            if (!readiness.ReadyForProfile)
            {
                TxtProfileState.Text = readiness.ProfileBlockingReasons.Count == 0
                    ? "La mesure ne peut pas démarrer actuellement. Actualise puis réessaie."
                    : "Mesure impossible actuellement : " + string.Join(
                        " · ",
                        readiness.ProfileBlockingReasons.Select(ToFrenchReason));
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
                EndAction();
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "FONCTION EXPÉRIMENTALE\n\n" +
                "Continue uniquement si tu sais revenir manuellement au dernier profil stable après un crash du pilote ou un écran noir.\n\n" +
                "L'application automatique du profil au démarrage doit rester désactivée pendant toute la recherche.\n\n" +
                $"Confirme que le profil « {name} » est déjà appliqué dans ton outil habituel.\n\n" +
                $"Tweakly n'écrira aucun réglage. La mesure dure environ 5 min et compare ce profil à la référence stock.",
                "Mesurer le profil GPU",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                EndAction();
                return;
            }

            var profile = new GpuTuningProfile
            {
                Name = name,
                Kind = ProfileKind.Undervolt,
                TargetVoltageMv = voltage,
                TargetClockMhz = clock,
                MemoryOffsetMhz = memory,
                PowerLimitPercent = power,
                AppliedBy = "manual-confirmed",
                VerificationEvidence = ["User confirmed that the documented profile was applied manually before measurement."]
            };
            _runCancellation = new CancellationTokenSource();
            SetRunning(true, profile: true);
            EndAction();
            PbProfile.Value = 0;
            TxtProfileProgress.Text = "0 %";
            ProfileResult.Visibility = Visibility.Collapsed;
            var progress = new Progress<GpuBaselineProgress>(UpdateProfileProgress);
            try
            {
                GpuProfileMeasurementResult result = await _service.RunProfileMeasurementAsync(
                    PathLayout.GpuTuningTools,
                    PathLayout.GpuTuningSession,
                    profile,
                    workloadSeconds: 60,
                    progress: progress,
                    cancellationToken: _runCancellation.Token,
                    evidencePath: PathLayout.GpuTuningEvidence);
                RenderProfileResult(result);
            }
            catch (OperationCanceledException)
            {
                TxtProfileState.Text = "Mesure annulée. Aucun résultat incomplet n'est comparé au stock.";
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThWarn");
            }
            catch (Exception ex)
            {
                TxtProfileState.Text = "Mesure interrompue : " + ToFrenchReason(ex.Message);
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Error("Optimisation GPU : mesure profil", ex);
            }
            finally
            {
                _runCancellation.Dispose();
                _runCancellation = null;
                SetRunning(false, profile: true);
            }
        }

        private void UpdateProgress(GpuBaselineProgress progress)
        {
            double percent = progress.TotalSteps == 0 ? 0 : progress.CompletedSteps * 100.0 / progress.TotalSteps;
            PbBaseline.Value = percent;
            TxtProgressPercent.Text = $"{percent:0} %";
            TxtRunState.Text = progress.WorkloadCompleted
                ? $"Passage {progress.SuiteIndex}/{progress.SuiteCount} · {WorkloadLabel(progress.WorkloadName)} terminé."
                : $"Passage {progress.SuiteIndex}/{progress.SuiteCount} · {WorkloadLabel(progress.WorkloadName)} en cours…";
            TxtRunState.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
        }

        private void UpdateProfileProgress(GpuBaselineProgress progress)
        {
            double percent = progress.TotalSteps == 0 ? 0 : progress.CompletedSteps * 100.0 / progress.TotalSteps;
            PbProfile.Value = percent;
            TxtProfileProgress.Text = $"{percent:0} %";
            TxtProfileState.Text = progress.WorkloadCompleted
                ? $"{WorkloadLabel(progress.WorkloadName)} terminé."
                : $"{WorkloadLabel(progress.WorkloadName)} en cours…";
            TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThTextBody");
        }

        private void RenderProfileResult(GpuProfileMeasurementResult result)
        {
            WorkloadResult? incomplete = result.Run.Workloads
                .FirstOrDefault(static workload => !workload.Completed);
            if (incomplete != null)
            {
                ProfileResult.Visibility = Visibility.Collapsed;
                TxtProfileState.Text = string.IsNullOrWhiteSpace(incomplete.FailureReason)
                    ? "La mesure s'est arrêtée avant la fin de la suite. Aucun résultat incomplet n'est comparé au stock."
                    : "Mesure arrêtée : " + ToFrenchReason(incomplete.FailureReason);
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Write("Optimisation GPU : profil incomplet — " +
                             (string.IsNullOrWhiteSpace(incomplete.FailureReason)
                                 ? "cause non fournie"
                                 : incomplete.FailureReason));
                return;
            }

            if (!result.Application.Verified)
            {
                ProfileResult.Visibility = Visibility.Collapsed;
                TxtProfileState.Text =
                    "Profil non vérifié : " +
                    string.Join(" ", result.Application.BlockingReasons.Select(ToFrenchReason));
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Write(
                    "Optimisation GPU : profil déclaré différent des valeurs mesurées — " +
                    string.Join(" | ", result.Application.BlockingReasons));
                return;
            }

            if (result.Comparison is not ProfileComparison comparison)
            {
                ProfileResult.Visibility = Visibility.Collapsed;
                TxtProfileState.Text = result.Summary.Reasons.Count > 0
                    ? "Mesure non exploitable : " +
                      string.Join(" ", result.Summary.Reasons.Select(ToFrenchReason))
                    : "Mesure non exploitable : aucune comparaison fiable n'a pu être calculée.";
                TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
                AppLog.Write("Optimisation GPU : comparaison refusée — " +
                             string.Join(" | ", result.Summary.Reasons));
                return;
            }

            PbProfile.Value = 100;
            TxtProfileProgress.Text = "100 %";
            TxtProfileName.Text = result.Run.Profile.Name;
            TxtProfileVoltage.Text = result.Run.Profile.TargetVoltageMv?.ToString(CultureInfo.InvariantCulture) ?? "";
            TxtProfileClock.Text = result.Run.Profile.TargetClockMhz?.ToString(CultureInfo.InvariantCulture) ?? "";
            TxtProfileMemory.Text = result.Run.Profile.MemoryOffsetMhz?.ToString(CultureInfo.InvariantCulture) ?? "0";
            TxtProfilePower.Text = result.Run.Profile.PowerLimitPercent?.ToString("0", CultureInfo.InvariantCulture) ?? "100";
            TxtResultPerformance.Text = $"{comparison.PerformanceIndex:0.0} %";
            TxtResultPower.Text = $"{comparison.PowerIndex - 100:+0.0;-0.0;0.0} %";
            TxtResultEfficiency.Text = $"{comparison.EfficiencyIndex - 100:+0.0;-0.0;0.0} %";
            TxtResultTemperature.Text = comparison.TemperatureDeltaC.HasValue
                ? $"{comparison.TemperatureDeltaC:+0.0;-0.0;0.0} °C"
                : "Non comparable";
            TxtResultPerformance.SetResourceReference(TextBlock.ForegroundProperty,
                comparison.MeetsPerformanceFloor ? "ThOk" : "ThCrit");
            TxtResultPower.SetResourceReference(TextBlock.ForegroundProperty,
                comparison.PowerIndex <= 100 ? "ThOk" : "ThWarn");
            TxtResultEfficiency.SetResourceReference(TextBlock.ForegroundProperty,
                comparison.EfficiencyIndex >= 100 ? "ThOk" : "ThWarn");
            TxtResultTemperature.SetResourceReference(
                TextBlock.ForegroundProperty,
                comparison.TemperatureDeltaC.HasValue
                    ? comparison.TemperatureDeltaC <= 0 ? "ThOk" : "ThWarn"
                    : "ThTextMuted");
            ProfileResult.Visibility = Visibility.Visible;
            TxtProfileState.Text = result.NextSuggestion is not null && !string.IsNullOrWhiteSpace(result.NextSuggestionMessage)
                ? result.NextSuggestionMessage
                : MeasuredConclusion(comparison, result.Recommendation);
            TxtProfileState.SetResourceReference(TextBlock.ForegroundProperty,
                comparison.MeetsPerformanceFloor ? "ThTextBody" : "ThCrit");
            if (result.NextSuggestion is GpuProfileSuggestion next)
                RenderSuggestion(next, result.NextSuggestionMessage);
        }

        private void RenderSuggestion(GpuProfileSuggestion suggestion, string message)
        {
            _profileSuggestion = suggestion;
            AdviceCard.Visibility = Visibility.Visible;
            TxtAdviceTitle.Text = "Prochain essai calculé";
            TxtAdviceConfidence.Text = $"Confiance {suggestion.Confidence.ToLowerInvariant()}";
            TxtAdviceValues.Text = SuggestionValues(suggestion.Profile);
            TxtAdviceDetail.Text = message;
        }

        private static string SuggestionValues(GpuTuningProfile profile)
            => $"{profile.TargetVoltageMv:0} mV · {profile.TargetClockMhz:0} MHz · mémoire {profile.MemoryOffsetMhz ?? 0:+0;-0;0} MHz · Power Limit {profile.PowerLimitPercent ?? 100:0} %";

        private string MeasuredConclusion(ProfileComparison comparison, Recommendation recommendation)
        {
            if (!comparison.MeetsPerformanceFloor)
                return
                    $"Profil à écarter : moyenne {comparison.PerformanceIndex:0.0} % du stock, " +
                    $"{WorkloadLabel(comparison.WeakestWorkloadName)} {comparison.MinimumWorkloadPerformanceIndex:0.0} %. " +
                    $"Minimums requis : {_policy.MinimumPerformanceRetentionPercent:0.0} % en moyenne et " +
                    $"{_policy.MinimumIndividualWorkloadRetentionPercent:0.0} % par test.";
            return RecommendationText(recommendation);
        }

        private bool EnsurePolicyAvailable(TextBlock target)
        {
            if (_policyLoadError.Length == 0)
                return true;

            target.Text = "Mesure bloquée : règles d'évaluation GPU invalides. " + _policyLoadError;
            target.SetResourceReference(TextBlock.ForegroundProperty, "ThCrit");
            return false;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            BtnCancel.IsEnabled = false;
            BtnProfileCancel.IsEnabled = false;
            if (_runningProfile) TxtProfileState.Text = "Annulation en cours…";
            else TxtRunState.Text = "Annulation en cours…";
            _runCancellation?.Cancel();
        }

        private void SetRunning(bool running, bool profile)
        {
            _runningProfile = running && profile;
            BtnStart.IsEnabled = false;
            BtnProfileStart.IsEnabled = false;
            BtnRefresh.IsEnabled = !running;
            BtnCancel.Visibility = running && !profile ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.IsEnabled = running && !profile;
            BtnProfileCancel.Visibility = running && profile ? Visibility.Visible : Visibility.Collapsed;
            BtnProfileCancel.IsEnabled = running && profile;
            TxtProfileName.IsEnabled = !running;
            TxtProfileVoltage.IsEnabled = !running;
            TxtProfileClock.IsEnabled = !running;
            TxtProfileMemory.IsEnabled = !running;
            TxtProfilePower.IsEnabled = !running;
            if (!running)
                UpdateActionAvailability();
            else
                UpdateProfileButtonHint();
        }

        private void UpdateActionAvailability()
        {
            bool idle = _runCancellation == null && !_actionPending;
            BtnStart.IsEnabled = idle && _readyForBaseline;
            // La charge GPU est transitoire : elle est contrôlée à nouveau au clic.
            // Un relevé ancien ne doit pas condamner le bouton jusqu'au prochain chargement de page.
            BtnProfileStart.IsEnabled = idle && _baselineValid;
            UpdateProfileButtonHint();
        }

        private void UpdateProfileButtonHint()
        {
            if (BtnProfileStart.IsEnabled)
            {
                TxtProfileButtonHint.Visibility = Visibility.Collapsed;
                return;
            }

            TxtProfileButtonHint.Text = _runningProfile
                ? "Mesure en cours"
                : _runCancellation != null
                    ? "Mesure de référence en cours"
                    : _actionPending
                        ? "Vérification en cours"
                        : "Référence stock requise";
            TxtProfileButtonHint.Visibility = Visibility.Visible;
        }

        private bool TryBeginAction()
        {
            if (_actionPending || _runCancellation != null) return false;
            _actionPending = true;
            BtnRefresh.IsEnabled = false;
            BtnStart.IsEnabled = false;
            BtnProfileStart.IsEnabled = false;
            UpdateProfileButtonHint();
            return true;
        }

        private void EndAction()
        {
            _actionPending = false;
            BtnRefresh.IsEnabled = _runCancellation == null;
            UpdateActionAvailability();
        }

        private static string Watts(double? value) => value.HasValue ? $"{value.Value:0.0} W" : "— W";
        private static string Megahertz(double? value) => value.HasValue ? $"{value.Value:0} MHz" : "— MHz";

        private static string WorkloadLabel(string name) => name switch
        {
            var value when value.Contains("compute", StringComparison.OrdinalIgnoreCase) => "Calcul",
            var value when value.Contains("graphics", StringComparison.OrdinalIgnoreCase) => "Rendu graphique",
            var value when value.Contains("ray tracing", StringComparison.OrdinalIgnoreCase) => "Ray tracing",
            var value when value.Contains("vram", StringComparison.OrdinalIgnoreCase) => "Mémoire vidéo",
            var value when value.Contains("transient", StringComparison.OrdinalIgnoreCase) => "Transitions de charge",
            _ => name
        };

        private static string ToFrenchReason(string reason)
        {
            if (reason.Contains("GPU workload already active", StringComparison.OrdinalIgnoreCase))
                return reason.Replace(
                    "GPU workload already active:",
                    "Le GPU est déjà utilisé :",
                    StringComparison.OrdinalIgnoreCase);
            if (reason.Contains("Concurrent GPU workload detected during measurement", StringComparison.OrdinalIgnoreCase))
                return reason
                    .Replace(
                        "Concurrent GPU workload detected during measurement:",
                        "une autre application a utilisé le GPU pendant le test :",
                        StringComparison.OrdinalIgnoreCase)
                    .Replace("compute", "calcul", StringComparison.OrdinalIgnoreCase)
                    .Replace("memory", "mémoire", StringComparison.OrdinalIgnoreCase)
                    .Replace("encode", "encodage", StringComparison.OrdinalIgnoreCase)
                    .Replace("decode", "décodage", StringComparison.OrdinalIgnoreCase)
                    .Replace("observations", "relevés", StringComparison.OrdinalIgnoreCase)
                    .Replace("consecutive", "consécutifs", StringComparison.OrdinalIgnoreCase);
            if (reason.Contains("different workload package", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("predates workload package fingerprinting", StringComparison.OrdinalIgnoreCase))
                return "Les outils de test ont changé depuis cette mesure. Refais la référence stock avant toute comparaison.";
            if (reason.Contains("stock baseline", StringComparison.OrdinalIgnoreCase))
                return "La référence stock n'est pas encore valide.";
            if (reason.Contains("different GPU, VBIOS, or driver", StringComparison.OrdinalIgnoreCase))
                return "La référence stock a été mesurée avec un autre GPU, VBIOS ou pilote NVIDIA. Refais la mesure stock.";
            if (reason.Contains("GPU model is not supported", StringComparison.OrdinalIgnoreCase))
                return "Carte graphique non prise en charge. Cette fonction est réservée aux GeForce RTX 3000, 4000 et 5000 de bureau.";
            if (reason.Contains("manifest", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("workload", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("SHA-256", StringComparison.OrdinalIgnoreCase))
                return "Les outils de test GPU sont incomplets ou leur intégrité n'est pas valide.";
            if (reason.Contains("power limit", StringComparison.OrdinalIgnoreCase))
                return "La limite de puissance ne correspond pas à la valeur d'origine du GPU.";
            if (reason.Contains("memory clock", StringComparison.OrdinalIgnoreCase))
                return "La fréquence mémoire dépasse la valeur d'origine détectée.";
            if (reason.Contains("loaded voltage", StringComparison.OrdinalIgnoreCase))
                return "La tension réellement observée sous charge ne correspond pas à la tension saisie.";
            if (reason.Contains("loaded GPU clock", StringComparison.OrdinalIgnoreCase))
                return "La fréquence réellement observée sous charge ne correspond pas à la fréquence saisie.";
            if (reason.Contains("observed memory offset", StringComparison.OrdinalIgnoreCase))
                return "Le décalage mémoire réellement observé ne correspond pas à la valeur saisie.";
            if (reason.Contains("observed Power Limit", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("power-limit telemetry", StringComparison.OrdinalIgnoreCase))
                return "Le Power Limit réellement observé ne correspond pas à la valeur saisie.";
            if (reason.Contains("telemetry coverage", StringComparison.OrdinalIgnoreCase))
                return "La télémétrie est incomplète. Le résultat n'est pas comparé au stock.";
            if (reason.Contains("stability evidence", StringComparison.OrdinalIgnoreCase))
                return "Les journaux de stabilité Windows n'ont pas pu être vérifiés. Le résultat est refusé.";
            return reason;
        }

        private static string RecommendationText(Recommendation recommendation) => recommendation.Kind switch
        {
            RecommendationKind.RestoreLastStable => "Instabilité détectée. Reviens au dernier profil stable avant un nouvel essai.",
            RecommendationKind.RepeatRun => "La télémétrie n'est pas assez complète. Cette mesure doit être refaite.",
            RecommendationKind.IncreaseVoltageOrReduceClock => "La perte de performance est trop importante. Réduis la fréquence ou augmente légèrement la tension.",
            RecommendationKind.ReducePowerOrVoltage => "Le profil atteint trop souvent une limite thermique ou de puissance.",
            RecommendationKind.ValidateLonger => "Résultat comparable au stock. Une validation longue reste nécessaire avant de considérer ce profil comme stable.",
            RecommendationKind.KeepProfile => "Le profil mesuré est équilibré. Aucune étape plus agressive n'est proposée sans référence fiable pour ce modèle.",
            _ => "Mesure enregistrée."
        };

        private static bool TryInt(string value, out int result)
            => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out result)
               || int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        private static bool TryDouble(string value, out double result)
            => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out result)
               || double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        private static (EvaluationPolicy Policy, string Error) LoadPolicy()
        {
            try
            {
                return (EvaluationPolicyStore.Load(PathLayout.GpuTuningPolicy), "");
            }
            catch (Exception ex)
            {
                AppLog.Error("Optimisation GPU : politique d'évaluation", ex);
                return (
                    new EvaluationPolicy { SamplingIntervalMs = 500 },
                    ex.GetBaseException().Message);
            }
        }
    }
}
