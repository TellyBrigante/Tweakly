using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageBatteryCalibration : UserControl
    {
        private readonly MainWindow _main;
        private readonly DispatcherTimer _timer;
        private BatteryCalibrationSession _session;
        private BatterySnapshot _lastSnapshot = new();
        private CancellationTokenSource? _drainCts;
        private readonly List<Thread> _drainThreads = new();
        private volatile int _drainDutyPercent = 55;
        private bool _idleSleepGuardActive;

        private const int BalanceHours = 2;
        private const int RestHours = 8;
        private const int SampleSeconds = 5;

        public PageBatteryCalibration(MainWindow main)
        {
            _main = main;
            InitializeComponent();

            _session = BatteryCalibrationStore.Load();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SampleSeconds) };
            _timer.Tick += (_, _) => RefreshAndLog();
            Unloaded += (_, _) =>
            {
                StopDrainLoad();
                ReleaseIdleSleepGuard();
            };
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            RecoverInterruptedDrain();
            RefreshAndLog();
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshAndLog();

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_lastSnapshot.HasBattery)
            {
                MessageBox.Show("Aucune batterie détectée sur ce PC.", "Calibrage batterie", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_session.Phase is BatteryCalibrationPhase.Idle or BatteryCalibrationPhase.Complete)
            {
                _session = new BatteryCalibrationSession
                {
                    StartedAt = DateTime.Now,
                    Phase = BatteryCalibrationPhase.ChargeToFull,
                    PhaseStartedAt = DateTime.Now,
                    TargetBalanceHours = BalanceHours,
                    TargetRestHours = RestHours,
                    SampleIntervalSeconds = SampleSeconds
                };
                CaptureBatteryIdentity(_lastSnapshot);
                BatteryCalibrationStore.Save(_session);
            }

            RefreshAndLog();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Réinitialiser le calibrage batterie en cours ?\nLes points enregistrés seront supprimés.",
                "Calibrage batterie",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            StopDrainLoad();
            RestorePowerPlanGuardIfNeeded();
            BatteryCalibrationStore.Reset();
            _session = new BatteryCalibrationSession();
            RefreshAndLog();
        }

        private void BtnShowReport_Click(object sender, RoutedEventArgs e)
        {
            var report = new BatteryCalibrationReportWindow(_session, _lastSnapshot)
            {
                Owner = Window.GetWindow(this)
            };
            report.ShowDialog();
        }

        private void RefreshAndLog()
        {
            _lastSnapshot = ReadSnapshot();
            UpdateBatteryUi(_lastSnapshot);

            if (!_lastSnapshot.HasBattery)
            {
                StopDrainLoad();
                NoBatteryPanel.Visibility = Visibility.Visible;
                BtnStart.IsEnabled = false;
                BtnShowReport.IsEnabled = false;
                UpdatePhaseUi();
                DrawGraph();
                return;
            }

            NoBatteryPanel.Visibility = Visibility.Collapsed;
            BtnStart.IsEnabled = true;
            BtnShowReport.IsEnabled = _session.Samples.Count > 0;

            RecoverInterruptedDrain();
            AdvancePhase(_lastSnapshot);
            UpdatePowerGuards(_lastSnapshot);

            if (IsActive(_session.Phase) && ShouldRecordSample())
            {
                CaptureBatteryIdentity(_lastSnapshot);
                _session.Samples.Add(BatteryCalibrationStore.FromSnapshot(_lastSnapshot, _session.Phase));
                BatteryCalibrationStore.Save(_session);
            }

            UpdatePhaseUi();
            DrawGraph();
        }

        private BatterySnapshot ReadSnapshot()
        {
            return BatteryProbe.Read();
        }

        private void CaptureBatteryIdentity(BatterySnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Name)) _session.BatteryName = snapshot.Name;
            if (!string.IsNullOrWhiteSpace(snapshot.Manufacturer)) _session.BatteryManufacturer = snapshot.Manufacturer;
            if (!string.IsNullOrWhiteSpace(snapshot.Chemistry)) _session.BatteryChemistry = snapshot.Chemistry;
            _session.DesignCapacityMWh ??= snapshot.DesignCapacityMWh;
            _session.FullChargeCapacityMWh ??= snapshot.FullChargeCapacityMWh;
            _session.CycleCount ??= snapshot.CycleCount;
        }

        private bool ShouldRecordSample()
        {
            var last = _session.LastSample;
            return last == null || DateTime.Now - last.Timestamp >= TimeSpan.FromSeconds(_session.SampleIntervalSeconds);
        }

        private static bool IsActive(BatteryCalibrationPhase phase)
            => phase is not BatteryCalibrationPhase.Idle and not BatteryCalibrationPhase.Complete;

        private void RecoverInterruptedDrain()
        {
            var last = _session.LastSample;
            if (last == null) return;

            if (_session.Phase != BatteryCalibrationPhase.Complete
                && IsActive(last.Phase)
                && PhaseOrder(_session.Phase) < PhaseOrder(last.Phase))
            {
                _session.Phase = last.Phase;
                _session.PhaseStartedAt = last.Timestamp;
                BatteryCalibrationStore.Save(_session);
            }

            var gap = DateTime.Now - last.Timestamp;
            if (_session.Phase == BatteryCalibrationPhase.Drain && gap >= TimeSpan.FromMinutes(30))
            {
                if (gap >= TimeSpan.FromHours(RestHours))
                    SetPhase(BatteryCalibrationPhase.Recharge, save: true);
                else
                    SetPhase(BatteryCalibrationPhase.Rest, save: true, phaseStartedAt: last.Timestamp);

                RestorePowerPlanGuardIfNeeded();
            }
            else if (_session.Phase == BatteryCalibrationPhase.Rest)
            {
                if (_session.PhaseStartedAt > last.Timestamp)
                {
                    _session.PhaseStartedAt = last.Timestamp;
                    BatteryCalibrationStore.Save(_session);
                }

                if (DateTime.Now - _session.PhaseStartedAt >= TimeSpan.FromHours(RestHours))
                {
                    SetPhase(BatteryCalibrationPhase.Recharge, save: true);
                    RestorePowerPlanGuardIfNeeded();
                }
            }
        }

        private void AdvancePhase(BatterySnapshot snapshot)
        {
            switch (_session.Phase)
            {
                case BatteryCalibrationPhase.ChargeToFull:
                    StopDrainLoad();
                    if (snapshot.OnAcPower == true && snapshot.ChargePercent >= 100)
                        SetPhase(BatteryCalibrationPhase.CellBalance, save: true);
                    break;

                case BatteryCalibrationPhase.CellBalance:
                    StopDrainLoad();
                    if (snapshot.OnAcPower != true)
                    {
                        MarkProtocolWarning("Équilibrage interrompu : secteur retiré. Le compteur 2 h repartira après rebranchement.");
                        _session.BalanceInterrupted = true;
                        _session.PhaseStartedAt = DateTime.Now;
                        BatteryCalibrationStore.Save(_session);
                    }
                    else if (DateTime.Now - _session.PhaseStartedAt >= TimeSpan.FromHours(BalanceHours))
                        SetPhase(BatteryCalibrationPhase.Drain, save: true);
                    break;

                case BatteryCalibrationPhase.Drain:
                    if (snapshot.OnAcPower == true)
                    {
                        StopDrainLoad();
                        RestorePowerPlanGuardIfNeeded();
                    }
                    else if (snapshot.TemperatureC >= 50.0)
                    {
                        _drainDutyPercent = 20;
                        StopDrainLoad();
                        RestorePowerPlanGuardIfNeeded();
                    }
                    else
                    {
                        EnsureDrainPowerPlanGuard();
                        AdjustDrainLoad(snapshot);
                        EnsureDrainLoad();
                    }
                    break;

                case BatteryCalibrationPhase.Rest:
                    StopDrainLoad();
                    RestorePowerPlanGuardIfNeeded();
                    if (_session.LastSample != null && DateTime.Now - _session.PhaseStartedAt >= TimeSpan.FromHours(RestHours))
                        SetPhase(BatteryCalibrationPhase.Recharge, save: true);
                    break;

                case BatteryCalibrationPhase.Recharge:
                    StopDrainLoad();
                    RestorePowerPlanGuardIfNeeded();
                    if (snapshot.OnAcPower != true)
                    {
                        MarkProtocolWarning("Recharge finale interrompue : secteur retiré avant 100 %.");
                        _session.RechargeInterrupted = true;
                        BatteryCalibrationStore.Save(_session);
                    }
                    else if (snapshot.ChargePercent >= 100)
                        SetPhase(BatteryCalibrationPhase.Complete, save: true);
                    break;

                default:
                    StopDrainLoad();
                    RestorePowerPlanGuardIfNeeded();
                    break;
            }
        }

        private void SetPhase(BatteryCalibrationPhase phase, bool save, DateTime? phaseStartedAt = null)
        {
            if (_session.Phase == phase)
            {
                if (phaseStartedAt.HasValue && _session.PhaseStartedAt != phaseStartedAt.Value)
                {
                    _session.PhaseStartedAt = phaseStartedAt.Value;
                    if (save) BatteryCalibrationStore.Save(_session);
                }
                return;
            }

            _session.Phase = phase;
            _session.PhaseStartedAt = phaseStartedAt ?? DateTime.Now;
            if (phase == BatteryCalibrationPhase.Complete)
                _session.CompletedAt = DateTime.Now;
            if (phase != BatteryCalibrationPhase.Drain)
            {
                StopDrainLoad();
                RestorePowerPlanGuardIfNeeded();
            }
            if (phase is BatteryCalibrationPhase.CellBalance or BatteryCalibrationPhase.Drain or BatteryCalibrationPhase.Rest or BatteryCalibrationPhase.Recharge or BatteryCalibrationPhase.Complete)
                _session.LastWarning = "";
            if (save) BatteryCalibrationStore.Save(_session);
        }

        private void UpdatePowerGuards(BatterySnapshot snapshot)
        {
            bool shouldHoldIdleSleep = _session.Phase is BatteryCalibrationPhase.ChargeToFull
                or BatteryCalibrationPhase.CellBalance
                or BatteryCalibrationPhase.Drain
                or BatteryCalibrationPhase.Recharge;

            if (shouldHoldIdleSleep)
                EnsureIdleSleepGuard();
            else
                ReleaseIdleSleepGuard();

            if (_session.Phase != BatteryCalibrationPhase.Drain || snapshot.OnAcPower == true)
                RestorePowerPlanGuardIfNeeded();
        }

        private void EnsureIdleSleepGuard()
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            _idleSleepGuardActive = true;
        }

        private void ReleaseIdleSleepGuard()
        {
            if (!_idleSleepGuardActive) return;
            SetThreadExecutionState(ES_CONTINUOUS);
            _idleSleepGuardActive = false;
        }

        private void EnsureDrainPowerPlanGuard()
        {
            if (_session.PowerPlanGuardApplied) return;

            var snapshot = BatteryPowerPlanGuard.Read();
            _session.OriginalDcCriticalBatteryAction = snapshot.DcCriticalAction;
            _session.OriginalDcLowBatteryAction = snapshot.DcLowAction;
            _session.PowerPlanGuardError = snapshot.Error;

            if (BatteryPowerPlanGuard.ApplyDrainSettings(out var error))
            {
                _session.PowerPlanGuardApplied = true;
                _session.PowerPlanGuardError = "";
            }
            else
            {
                _session.PowerPlanGuardError = error;
                MarkProtocolWarning("Action batterie critique Windows non modifiée : " + error);
            }

            BatteryCalibrationStore.Save(_session);
        }

        private void RestorePowerPlanGuardIfNeeded()
        {
            if (!_session.PowerPlanGuardApplied) return;

            if (BatteryPowerPlanGuard.RestoreDrainSettings(
                    _session.OriginalDcCriticalBatteryAction,
                    _session.OriginalDcLowBatteryAction,
                    out var error))
            {
                _session.PowerPlanGuardApplied = false;
                _session.PowerPlanGuardError = "";
            }
            else
            {
                _session.PowerPlanGuardError = error;
                MarkProtocolWarning("Restauration du plan d'alimentation à vérifier : " + error);
            }

            BatteryCalibrationStore.Save(_session);
        }

        private void MarkProtocolWarning(string warning)
        {
            if (string.Equals(_session.LastWarning, warning, StringComparison.Ordinal)) return;
            _session.LastWarning = warning;
        }

        private void UpdateBatteryUi(BatterySnapshot s)
        {
            TxtCharge.Text = FormatPercent(s.ChargePercent);
            PbCharge.Value = s.ChargePercent ?? 0;
            TxtBatteryState.Text = $"{s.StateText} | {AcText(s.OnAcPower)}";

            TxtHealth.Text = s.HealthPercent.HasValue ? $"{s.HealthPercent.Value:0} %" : "-- %";
            TxtCapacity.Text =
                $"Capacité : {FormatMWh(s.FullChargeCapacityMWh)} / {FormatMWh(s.DesignCapacityMWh)}";

            TxtVoltage.Text = s.VoltageV.HasValue ? $"{s.VoltageV.Value:0.000} V" : "-- V";
            TxtPower.Text = $"Puissance : {FormatW(s.PowerW)} | Courant : {FormatA(s.CurrentA)}";

            TxtSource.Text = FormatSource(s.Source);
            TxtCycles.Text = $"État : {s.StateText} | Secteur : {AcText(s.OnAcPower)}";
            TxtBatteryTemperature.Text = ControllerCapacityLine(s);
            TxtControllerDetail.Text = ControllerDetail(s);
        }

        private void UpdatePhaseUi()
        {
            BtnStart.Content = _session.Phase switch
            {
                BatteryCalibrationPhase.Idle => "DÉMARRER LE CALIBRAGE",
                BatteryCalibrationPhase.Complete => "NOUVEAU CALIBRAGE",
                _ => "CALIBRAGE EN COURS"
            };

            TxtPhaseTitle.Text = PhaseTitle(_session.Phase);
            TxtPhaseDetail.Text = string.IsNullOrWhiteSpace(_session.LastWarning)
                ? PhaseDetail(_session.Phase)
                : _session.LastWarning;

            SetStep(StepCharge, PbStepCharge, BatteryCalibrationPhase.ChargeToFull);
            SetStep(StepBalance, PbStepBalance, BatteryCalibrationPhase.CellBalance);
            SetStep(StepDrain, PbStepDrain, BatteryCalibrationPhase.Drain);
            SetStep(StepRest, PbStepRest, BatteryCalibrationPhase.Rest);
            SetStep(StepRecharge, PbStepRecharge, BatteryCalibrationPhase.Recharge);

            TxtStepCharge.Text = $"Charge jusqu'à 100 %. Actuel : {FormatPercent(_lastSnapshot.ChargePercent)}.";
            TxtStepBalance.Text = _session.BalanceInterrupted && _session.Phase == BatteryCalibrationPhase.CellBalance
                ? $"Maintien branché : compteur réinitialisé après coupure secteur. {ElapsedPhaseText(BatteryCalibrationPhase.CellBalance)} / {BalanceHours} h."
                : $"Maintien branché : {ElapsedPhaseText(BatteryCalibrationPhase.CellBalance)} / {BalanceHours} h.";
            TxtStepDrain.Text = _session.Phase == BatteryCalibrationPhase.Drain && _lastSnapshot.TemperatureC >= 50.0
                ? $"Drain en pause sécurité : température batterie {FormatC(_lastSnapshot.TemperatureC)}."
                : _session.Phase == BatteryCalibrationPhase.Drain && _lastSnapshot.OnAcPower == false
                ? $"Drain CPU contrôlé actif : {_drainThreads.Count} thread(s), intensité {_drainDutyPercent} %. Dernier point : {FormatPercent(_lastSnapshot.ChargePercent)}."
                : "Débranche le secteur. Tweakly appliquera une charge CPU contrôlée et sauvegardera chaque point.";
            TxtStepRest.Text = $"Repos total requis : {RestElapsedText()} / {RestHours} h.";
            TxtStepRecharge.Text = _session.RechargeInterrupted && _session.Phase == BatteryCalibrationPhase.Recharge
                ? $"Recharge finale interrompue au moins une fois. Actuel : {FormatPercent(_lastSnapshot.ChargePercent)}."
                : $"Recharge finale jusqu'à 100 %. Actuel : {FormatPercent(_lastSnapshot.ChargePercent)}.";

            TxtPointCount.Text = $"{_session.Samples.Count} point(s)";
            TxtLastSample.Text = BuildLastSampleText();
        }

        private void SetStep(Border card, ProgressBar bar, BatteryCalibrationPhase step)
        {
            var current = _session.Phase;
            bool done = PhaseOrder(current) > PhaseOrder(step) || current == BatteryCalibrationPhase.Complete;
            bool active = current == step;

            card.BorderBrush = Brush(done ? "ThOk" : active ? "ThAccentIcon" : "ThBorder");
            bar.Foreground = Brush(done ? "ThOk" : active ? "ThAccentIcon" : "ThTextDim");
            bar.Value = done ? 100 : active ? PhaseProgress(step) : 0;
        }

        private double PhaseProgress(BatteryCalibrationPhase phase)
        {
            return phase switch
            {
                BatteryCalibrationPhase.ChargeToFull => _lastSnapshot.ChargePercent ?? 0,
                BatteryCalibrationPhase.CellBalance => PercentOf(DateTime.Now - _session.PhaseStartedAt, TimeSpan.FromHours(BalanceHours)),
                BatteryCalibrationPhase.Drain => _lastSnapshot.ChargePercent.HasValue ? Math.Clamp(100 - _lastSnapshot.ChargePercent.Value, 0, 100) : 0,
                BatteryCalibrationPhase.Rest => _session.LastSample != null
                    ? PercentOf(DateTime.Now - _session.PhaseStartedAt, TimeSpan.FromHours(RestHours))
                    : 0,
                BatteryCalibrationPhase.Recharge => _lastSnapshot.ChargePercent ?? 0,
                _ => 0
            };
        }

        private static double PercentOf(TimeSpan elapsed, TimeSpan target)
            => target.TotalSeconds <= 0 ? 0 : Math.Clamp(elapsed.TotalSeconds * 100.0 / target.TotalSeconds, 0, 100);

        private static int PhaseOrder(BatteryCalibrationPhase phase) => phase switch
        {
            BatteryCalibrationPhase.Idle => 0,
            BatteryCalibrationPhase.ChargeToFull => 1,
            BatteryCalibrationPhase.CellBalance => 2,
            BatteryCalibrationPhase.Drain => 3,
            BatteryCalibrationPhase.Rest => 4,
            BatteryCalibrationPhase.Recharge => 5,
            BatteryCalibrationPhase.Complete => 6,
            _ => 0
        };

        private string PhaseTitle(BatteryCalibrationPhase phase) => phase switch
        {
            BatteryCalibrationPhase.ChargeToFull => "Phase 1 : charge complète",
            BatteryCalibrationPhase.CellBalance => "Phase 2 : équilibrage cellules",
            BatteryCalibrationPhase.Drain => "Phase 3 : drain contrôlé",
            BatteryCalibrationPhase.Rest => "Phase 4 : repos total",
            BatteryCalibrationPhase.Recharge => "Phase 5 : recharge complète",
            BatteryCalibrationPhase.Complete => "Calibrage terminé",
            _ => "Prêt"
        };

        private string PhaseDetail(BatteryCalibrationPhase phase) => phase switch
        {
            BatteryCalibrationPhase.ChargeToFull => _lastSnapshot.OnAcPower == true
                ? "Le PC doit rester branché jusqu'à 100 %. Les mesures sont enregistrées toutes les 5 s."
                : "Branche le chargeur pour démarrer la phase de charge.",
            BatteryCalibrationPhase.CellBalance => _lastSnapshot.OnAcPower == true
                ? "Le PC reste branché 2 h pour stabiliser la jauge et équilibrer les cellules."
                : "Le secteur a été retiré. Rebranche le PC pour terminer l'équilibrage.",
            BatteryCalibrationPhase.Drain => _lastSnapshot.OnAcPower == false
                ? "Drain CPU contrôlé en cours. Tweakly sauvegarde chaque mesure pour retrouver le dernier point avant extinction."
                : "Débranche le secteur pour lancer le drain contrôlé.",
            BatteryCalibrationPhase.Rest => "Ne redémarre pas le PC avant 8 h. Au prochain lancement, Tweakly vérifiera la durée réelle depuis le dernier point.",
            BatteryCalibrationPhase.Recharge => _lastSnapshot.OnAcPower == true
                ? "Recharge jusqu'à 100 % sans coupure secteur. Tweakly continue le graphique et le rapport final."
                : "Branche le chargeur pour terminer la recharge complète.",
            BatteryCalibrationPhase.Complete => "Le rapport contient les points de charge, drain, repos et recharge finale.",
            _ => "Lance le calibrage pour démarrer le protocole complet."
        };

        private string BuildLastSampleText()
        {
            var s = _session.LastSample;
            if (s == null) return "Aucune mesure enregistrée.";

            return
                $"Heure : {s.Timestamp:yyyy-MM-dd HH:mm:ss}\n" +
                $"Phase : {PhaseTitle(s.Phase)}\n" +
                $"Charge : {FormatPercent(s.ChargePercent)} | Tension : {FormatV(s.VoltageV)} | Puissance : {FormatW(s.PowerW)}\n" +
                $"Capacité restante : {FormatMWh(s.RemainingCapacityMWh)} | Température : {FormatC(s.TemperatureC)}\n" +
                $"Source : {s.Source} | Secteur : {AcText(s.OnAcPower)}";
        }

        private string BuildReport()
        {
            var sb = new StringBuilder();
            var samples = _session.Samples;
            var first = samples.FirstOrDefault();
            var last = samples.LastOrDefault();
            var drainLast = samples.LastOrDefault(s => s.Phase == BatteryCalibrationPhase.Drain);

            sb.AppendLine("RAPPORT CALIBRAGE BATTERIE - TWEAKLY");
            sb.AppendLine(new string('=', 42));
            sb.AppendLine($"Généré le        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Session démarrée : {_session.StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"État             : {PhaseTitle(_session.Phase)}");
            sb.AppendLine($"Points mesurés   : {samples.Count}");
            sb.AppendLine();

            sb.AppendLine("BATTERIE");
            sb.AppendLine(new string('-', 42));
            sb.AppendLine($"Nom              : {FirstNonEmpty(_session.BatteryName, _lastSnapshot.Name, "Batterie")}");
            sb.AppendLine($"Fabricant        : {FirstNonEmpty(_session.BatteryManufacturer, _lastSnapshot.Manufacturer, "Non renseigné")}");
            sb.AppendLine($"Chimie           : {FirstNonEmpty(_session.BatteryChemistry, _lastSnapshot.Chemistry, "Non renseignée")}");
            sb.AppendLine($"Source lecture   : {_lastSnapshot.Source}");
            sb.AppendLine($"Santé            : {FormatHealth(_lastSnapshot.HealthPercent)}");
            sb.AppendLine($"Capacité pleine  : {FormatMWh(_lastSnapshot.FullChargeCapacityMWh ?? _session.FullChargeCapacityMWh)}");
            sb.AppendLine($"Capacité origine : {FormatMWh(_lastSnapshot.DesignCapacityMWh ?? _session.DesignCapacityMWh)}");
            sb.AppendLine($"Cycles           : {FormatCycles(_lastSnapshot.CycleCount ?? _session.CycleCount)}");
            sb.AppendLine();

            sb.AppendLine("RÉSUMÉ");
            sb.AppendLine(new string('-', 42));
            sb.AppendLine($"Équilibrage 2 h continu : {YesNo(!_session.BalanceInterrupted)}");
            sb.AppendLine($"Recharge finale continue : {YesNo(!_session.RechargeInterrupted)}");
            sb.AppendLine($"Action critique Windows restaurée : {YesNo(!_session.PowerPlanGuardApplied)}");
            if (!string.IsNullOrWhiteSpace(_session.PowerPlanGuardError))
                sb.AppendLine($"Powercfg          : {_session.PowerPlanGuardError}");
            AppendReportPoint(sb, "Premier point", first);
            AppendReportPoint(sb, "Dernier point", last);
            AppendReportPoint(sb, "Dernier drain", drainLast);
            sb.AppendLine();

            sb.AppendLine("PHASES");
            sb.AppendLine(new string('-', 42));
            AppendPhaseSummary(sb, BatteryCalibrationPhase.ChargeToFull, "1. Charge complète");
            AppendPhaseSummary(sb, BatteryCalibrationPhase.CellBalance, "2. Équilibrage cellules");
            AppendPhaseSummary(sb, BatteryCalibrationPhase.Drain, "3. Drain contrôlé");
            AppendPhaseSummary(sb, BatteryCalibrationPhase.Rest, "4. Repos total");
            AppendPhaseSummary(sb, BatteryCalibrationPhase.Recharge, "5. Recharge complète");
            sb.AppendLine();

            sb.AppendLine("MESURES");
            sb.AppendLine(new string('-', 104));
            sb.AppendLine("Heure               Phase        %      V        W        A        °C       mWh       Secteur");
            sb.AppendLine(new string('-', 104));

            foreach (var s in _session.Samples)
            {
                sb.AppendLine(
                    $"{s.Timestamp:yyyy-MM-dd HH:mm:ss}  " +
                    $"{ShortPhase(s.Phase),-11} " +
                    $"{FormatPercent(s.ChargePercent),6} " +
                    $"{FormatV(s.VoltageV),8} " +
                    $"{FormatW(s.PowerW),8} " +
                    $"{FormatA(s.CurrentA),8} " +
                    $"{FormatC(s.TemperatureC),8} " +
                    $"{FormatMWh(s.RemainingCapacityMWh),9} " +
                    $"{AcText(s.OnAcPower)}");
            }

            return sb.ToString();
        }

        private static void AppendReportPoint(StringBuilder sb, string label, BatteryCalibrationSample? s)
        {
            if (s == null)
            {
                sb.AppendLine($"{label,-15}: aucun point");
                return;
            }

            sb.AppendLine(
                $"{label,-15}: {s.Timestamp:yyyy-MM-dd HH:mm:ss} | {ShortPhase(s.Phase)} | " +
                $"{FormatPercent(s.ChargePercent)} | {FormatV(s.VoltageV)} | {FormatW(s.PowerW)} | {FormatC(s.TemperatureC)}");
        }

        private void AppendPhaseSummary(StringBuilder sb, BatteryCalibrationPhase phase, string label)
        {
            var items = _session.Samples.Where(s => s.Phase == phase).ToList();
            if (items.Count == 0)
            {
                sb.AppendLine($"{label,-24}: aucun point");
                return;
            }

            var first = items[0];
            var last = items[^1];
            var duration = last.Timestamp - first.Timestamp;
            sb.AppendLine(
                $"{label,-24}: {items.Count,3} point(s), {FormatDuration(duration),10}, " +
                $"{FormatPercent(first.ChargePercent)} -> {FormatPercent(last.ChargePercent)}, " +
                $"{FormatV(first.VoltageV)} -> {FormatV(last.VoltageV)}");
        }

        private static string ShortPhase(BatteryCalibrationPhase phase) => phase switch
        {
            BatteryCalibrationPhase.ChargeToFull => "Charge",
            BatteryCalibrationPhase.CellBalance => "Equilibrage",
            BatteryCalibrationPhase.Drain => "Drain",
            BatteryCalibrationPhase.Rest => "Repos",
            BatteryCalibrationPhase.Recharge => "Recharge",
            BatteryCalibrationPhase.Complete => "Termine",
            _ => "Pret"
        };

        private void EnsureDrainLoad()
        {
            if (_drainCts != null) return;

            _drainCts = new CancellationTokenSource();
            int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            _drainThreads.Clear();

            for (int i = 0; i < workers; i++)
            {
                var thread = new Thread(() => DrainLoop(_drainCts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "TweaklyBatteryDrain"
                };
                _drainThreads.Add(thread);
                thread.Start();
            }

            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        }

        private void StopDrainLoad()
        {
            if (_drainCts == null) return;

            try { _drainCts.Cancel(); } catch { }
            _drainCts.Dispose();
            _drainCts = null;
            _drainThreads.Clear();
            SetThreadExecutionState(ES_CONTINUOUS);
        }

        private void AdjustDrainLoad(BatterySnapshot snapshot)
        {
            if (snapshot.TemperatureC >= 50.0)
            {
                _drainDutyPercent = 20;
                return;
            }

            if (snapshot.TemperatureC >= 45.0)
                _drainDutyPercent = Math.Min(_drainDutyPercent, 35);

            var watts = Math.Abs(snapshot.PowerW ?? 0);
            if (watts <= 0) return;

            if (watts < 8.0) _drainDutyPercent = Math.Min(90, _drainDutyPercent + 5);
            else if (watts > 30.0) _drainDutyPercent = Math.Max(30, _drainDutyPercent - 5);
        }

        private void DrainLoop(CancellationToken token)
        {
            const int cycleMs = 200;
            double sink = 0;

            while (!token.IsCancellationRequested)
            {
                int duty = Math.Clamp(_drainDutyPercent, 20, 90);
                int busyMs = cycleMs * duty / 100;
                var sw = Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < busyMs && !token.IsCancellationRequested)
                {
                    for (int i = 1; i < 5000; i++)
                        sink += Math.Sqrt(i) * Math.Sin(i);
                    if (sink > 1_000_000) sink = 0;
                }

                int sleepMs = cycleMs - busyMs;
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
            }
        }

        private void DrawGraph()
        {
            if (GraphCanvas.ActualWidth <= 0) return;

            GraphCanvas.Children.Clear();
            var samples = _session.Samples;
            if (samples.Count == 0)
            {
                AddGraphText("Aucun point enregistré.", 16, 16, "ThTextDim");
                return;
            }

            double width = GraphCanvas.ActualWidth;
            double height = GraphCanvas.ActualHeight > 0 ? GraphCanvas.ActualHeight : GraphCanvas.Height;
            double left = 42;
            double right = 18;
            double top = 18;
            double bottom = 30;
            double plotW = Math.Max(20, width - left - right);
            double plotH = Math.Max(20, height - top - bottom);

            DrawGrid(left, top, plotW, plotH);

            var minTime = samples.Min(s => s.Timestamp);
            var maxTime = samples.Max(s => s.Timestamp);
            if (maxTime <= minTime) maxTime = minTime.AddSeconds(1);

            double X(DateTime t) => left + (t - minTime).TotalSeconds / (maxTime - minTime).TotalSeconds * plotW;
            double YPercent(int p) => top + (100 - Math.Clamp(p, 0, 100)) / 100.0 * plotH;

            var volts = samples.Where(s => s.VoltageV.HasValue).Select(s => s.VoltageV!.Value).ToList();
            double minV = volts.Count > 0 ? volts.Min() : 0;
            double maxV = volts.Count > 0 ? volts.Max() : 1;
            if (maxV - minV < 0.2) { minV -= 0.1; maxV += 0.1; }
            double YVolt(double v) => top + (maxV - v) / (maxV - minV) * plotH;

            var powers = samples.Where(s => s.PowerW.HasValue).Select(s => Math.Abs(s.PowerW!.Value)).ToList();
            double maxW = Math.Max(1, powers.Count > 0 ? powers.Max() : 1);
            double YPower(double w) => top + (1 - Math.Abs(w) / maxW) * plotH;

            DrawPolyline(samples.Where(s => s.ChargePercent.HasValue).Select(s => new Point(X(s.Timestamp), YPercent(s.ChargePercent!.Value))), "ThAccentIcon", 2.2);
            DrawPolyline(samples.Where(s => s.VoltageV.HasValue).Select(s => new Point(X(s.Timestamp), YVolt(s.VoltageV!.Value))), "ThWarn", 1.8);
            DrawPolyline(samples.Where(s => s.PowerW.HasValue).Select(s => new Point(X(s.Timestamp), YPower(s.PowerW!.Value))), "ThCyan", 1.5);

            AddGraphText("100 %", 0, top - 4, "ThTextDim");
            AddGraphText("0 %", 8, top + plotH - 8, "ThTextDim");
            if (volts.Count > 0)
                AddGraphText($"{maxV:0.00} V / {minV:0.00} V", left + 4, top + 4, "ThWarn");
        }

        private void DrawGrid(double left, double top, double width, double height)
        {
            var gridBrush = Brush("ThBorder");
            for (int i = 0; i <= 4; i++)
            {
                double y = top + height * i / 4.0;
                GraphCanvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = left + width,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    Opacity = 0.55
                });
            }
        }

        private void DrawPolyline(IEnumerable<Point> points, string role, double thickness)
        {
            var list = points.ToList();
            if (list.Count == 0) return;
            if (list.Count == 1)
            {
                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Brush(role)
                };
                Canvas.SetLeft(dot, list[0].X - 3.5);
                Canvas.SetTop(dot, list[0].Y - 3.5);
                GraphCanvas.Children.Add(dot);
                return;
            }

            var line = new Polyline
            {
                Stroke = Brush(role),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            foreach (var p in list) line.Points.Add(p);
            GraphCanvas.Children.Add(line);
        }

        private void AddGraphText(string text, double x, double y, string role)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = Brush(role),
                FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
                FontSize = 10.5
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            GraphCanvas.Children.Add(tb);
        }

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

        private string ElapsedPhaseText(BatteryCalibrationPhase phase)
        {
            if (PhaseOrder(_session.Phase) > PhaseOrder(phase)) return phase == BatteryCalibrationPhase.CellBalance ? $"{BalanceHours} h" : "terminé";
            if (_session.Phase != phase) return "0 min";
            var elapsed = DateTime.Now - _session.PhaseStartedAt;
            return FormatDuration(elapsed);
        }

        private string RestElapsedText()
        {
            if (PhaseOrder(_session.Phase) > PhaseOrder(BatteryCalibrationPhase.Rest)) return $"{RestHours} h";
            var last = _session.LastSample;
            if (last == null || PhaseOrder(_session.Phase) < PhaseOrder(BatteryCalibrationPhase.Rest)) return "0 min";
            return FormatDuration(DateTime.Now - _session.PhaseStartedAt);
        }

        private static string ControllerCapacityLine(BatterySnapshot s)
        {
            if (!s.HasBattery) return "Capacité : -- mWh / -- mWh";
            if (s.RemainingCapacityMWh.HasValue || s.FullChargeCapacityMWh.HasValue)
                return $"Capacité : {FormatMWh(s.RemainingCapacityMWh)} / {FormatMWh(s.FullChargeCapacityMWh)}";
            return $"Charge : {FormatPercent(s.ChargePercent)} | Santé : {FormatHealth(s.HealthPercent)}";
        }

        private static string ControllerDetail(BatterySnapshot s)
        {
            if (!s.HasBattery) return "Tension : -- V | Puissance : -- W";
            if (s.CycleCount.HasValue || s.TemperatureC.HasValue)
                return $"Cycles : {FormatCycles(s.CycleCount)} | Température : {FormatC(s.TemperatureC)}";
            if (s.VoltageV.HasValue || s.PowerW.HasValue)
                return $"Tension : {FormatV(s.VoltageV)} | Puissance : {FormatW(s.PowerW)}";
            return $"Charge : {FormatPercent(s.ChargePercent)} | Courant : {FormatA(s.CurrentA)}";
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00} min";
            return $"{Math.Max(0, (int)t.TotalMinutes)} min {t.Seconds:00} s";
        }

        private static string FormatPercent(int? value) => value.HasValue ? $"{value.Value} %" : "-- %";
        private static string FormatV(double? value) => value.HasValue ? $"{value.Value:0.000} V" : "-- V";
        private static string FormatW(double? value) => value.HasValue ? $"{value.Value:0.0} W" : "-- W";
        private static string FormatA(double? value) => value.HasValue ? $"{value.Value:0.000} A" : "-- A";
        private static string FormatC(double? value) => value.HasValue ? $"{value.Value:0.0} °C" : "-- °C";
        private static string FormatMWh(int? value) => value.HasValue ? $"{value.Value:N0} mWh" : "-- mWh";
        private static string FormatCycles(int? value) => value.HasValue ? $"{value.Value} cycle(s)" : "-- cycle(s)";
        private static string FormatHealth(double? value) => value.HasValue ? $"{value.Value:0} %" : "-- %";
        private static string FormatSource(string value)
            => string.IsNullOrWhiteSpace(value)
                ? "Windows"
                : value.Replace("Battery API", "API").Replace("ACPI WMI", "ACPI").Replace("Win32_Battery", "Win32");
        private static string AcText(bool? onAc) => onAc == true ? "branché" : onAc == false ? "batterie" : "inconnu";
        private static string YesNo(bool value) => value ? "oui" : "non";
        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        private static SolidColorBrush Brush(string role)
        {
            if (Application.Current.Resources[role] is SolidColorBrush brush) return brush;
            return ThemeManager.Brush(role);
        }

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
