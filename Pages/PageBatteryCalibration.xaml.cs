using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
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
        private readonly DateTime? _systemBootTime;
        private BatteryCalibrationSession _session;
        private BatterySnapshot _lastSnapshot = new();
        private CancellationTokenSource? _drainCts;
        private readonly List<Thread> _drainThreads = new();
        private volatile int _drainDutyPercent = DrainStartDutyPercent;
        private bool _idleSleepGuardActive;
        private bool _telemetryGapLogged;
#if DEBUG
        private Button? _debugSimulationButton;
        private bool _debugSimulationEnabled;
        private const double DebugChargeSeconds = 20.0;
        private const double DebugBalanceSeconds = 20.0;
        private const double DebugDrainSeconds = 45.0;
        private const double DebugRestSeconds = 20.0;
        private const double DebugRechargeSeconds = 25.0;
        private const int DebugDesignCapacityMWh = 48000;
        private const int DebugFullCapacityMWh = 41760;
#endif

        private const int BalanceHours = 2;
        private const int RestHours = 8;
        private const int SampleSeconds = 5;
        private const int DrainStartDutyPercent = 85;
        private const int DrainMinDutyPercent = 45;
        private const int DrainWarmCapDutyPercent = 60;
        private const int DrainMaxDutyPercent = 95;
        private const double DrainWarmBatteryC = 45.0;
        private const double DrainHotBatteryC = 50.0;
        private const double DrainTargetMinW = 18.0;
        private const double DrainTargetMaxW = 45.0;

        public PageBatteryCalibration(MainWindow main)
        {
            _main = main;
            InitializeComponent();

            _session = BatteryCalibrationStore.Load();
            _systemBootTime = ReadSystemBootTime();
#if DEBUG
            _debugSimulationEnabled = ShouldEnableDebugSimulationByDefault();
            InstallDebugSimulationButton();
#endif
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SampleSeconds) };
            _timer.Tick += (_, _) => RefreshAndLog();
            SizeChanged += (_, _) => UpdateMetricsLayout();
            Unloaded += (_, _) => HandlePageUnloaded();
        }

        public void PrepareForAppShutdown()
        {
            _timer.Stop();
            StopDrainLoad();
            RestorePowerPlanGuardIfNeeded();
            ReleaseIdleSleepGuard();
            BatteryCalibrationStore.Save(_session);
        }

        public void ResumeActiveSession()
        {
            if (!IsActive(_session.Phase)) return;

            RefreshAndLog();
            if (IsActive(_session.Phase) && !_timer.IsEnabled)
                _timer.Start();

            AppLog.Write($"Calibrage batterie repris au démarrage : phase={_session.Phase}, points={_session.Samples.Count}.");
        }

        private void HandlePageUnloaded()
        {
            if (_main.ShuttingDown)
            {
                PrepareForAppShutdown();
                return;
            }

            if (IsActive(_session.Phase))
            {
                UpdatePowerGuards(_lastSnapshot);
                if (_session.Phase == BatteryCalibrationPhase.Drain && _lastSnapshot.OnAcPower == false)
                {
                    EnsureDrainPowerPlanGuard();
                    EnsureDrainLoad();
                }
                return;
            }

            _timer.Stop();
            StopDrainLoad();
            RestorePowerPlanGuardIfNeeded();
            ReleaseIdleSleepGuard();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateMetricsLayout();
            RefreshAndLog();
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void UpdateMetricsLayout()
        {
            bool compact = ActualWidth > 0 && ActualWidth < 800;
            MetricsGrid.Columns = compact ? 2 : 4;
            MetricsGrid.Rows = compact ? 2 : 1;

            DetailsGrid.ColumnDefinitions[0].Width = compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(1.02, GridUnitType.Star);
            DetailsGrid.ColumnDefinitions[1].Width = compact
                ? new GridLength(0)
                : new GridLength(14);
            DetailsGrid.ColumnDefinitions[2].Width = compact
                ? new GridLength(0)
                : new GridLength(0.98, GridUnitType.Star);
            DetailsGrid.RowDefinitions[1].Height = compact
                ? new GridLength(14)
                : new GridLength(0);
            Grid.SetColumn(ReportColumn, compact ? 0 : 2);
            Grid.SetRow(ReportColumn, compact ? 2 : 0);

            var cards = MetricsGrid.Children.OfType<Border>().ToArray();
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i].Margin = compact
                    ? i switch
                    {
                        0 => new Thickness(0, 0, 6, 6),
                        1 => new Thickness(6, 0, 0, 6),
                        2 => new Thickness(0, 6, 6, 0),
                        _ => new Thickness(6, 6, 0, 0)
                    }
                    : i < cards.Length - 1
                        ? new Thickness(0, 0, 6, 0)
                        : new Thickness(0);
            }
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

            ClearResolvedPowerPlanWarning();
            UpdatePhaseUi();
            DrawGraph();
            if (!IsVisible && !IsActive(_session.Phase))
                _timer.Stop();
        }

        private BatterySnapshot ReadSnapshot()
        {
#if DEBUG
            if (_debugSimulationEnabled) return ReadDebugSimulationSnapshot();
#endif
            return BatteryProbe.Read();
        }

#if DEBUG
        private void InstallDebugSimulationButton()
        {
            if (_debugSimulationButton != null) return;

            _debugSimulationButton = new Button
            {
                Style = (Style)FindResource("SecondaryBtnStyle"),
                Padding = new Thickness(16, 9, 16, 9),
                Margin = new Thickness(0)
            };
            _debugSimulationButton.Click += (_, _) =>
            {
                _debugSimulationEnabled = !_debugSimulationEnabled;
                UpdateDebugSimulationButton();
                RefreshAndLog();
            };

            DebugSimulationHost.Children.Add(_debugSimulationButton);
            DebugSimulationHost.Visibility = Visibility.Visible;
            AppLog.Write("Calibrage batterie Debug : bouton simulation séparé des actions Release.");
            UpdateDebugSimulationButton();
        }

        private static bool ShouldEnableDebugSimulationByDefault()
        {
            try { return !BatteryProbe.HasBattery(); }
            catch { return true; }
        }

        private void UpdateDebugSimulationButton()
        {
            if (_debugSimulationButton == null) return;
            _debugSimulationButton.Content = _debugSimulationEnabled ? "SIMULATION ACTIVE" : "SIMULATION DEBUG";
        }

        private BatterySnapshot ReadDebugSimulationSnapshot()
        {
            var now = DateTime.Now;
            var phase = _session.Phase;
            var elapsed = Math.Max(0, (now - _session.PhaseStartedAt).TotalSeconds);

            double charge = phase switch
            {
                BatteryCalibrationPhase.ChargeToFull => Lerp(58, 100, elapsed / DebugChargeSeconds),
                BatteryCalibrationPhase.CellBalance => 100,
                BatteryCalibrationPhase.Drain => Lerp(100, 4, elapsed / DebugDrainSeconds),
                BatteryCalibrationPhase.Rest => 4,
                BatteryCalibrationPhase.Recharge => Lerp(4, 100, elapsed / DebugRechargeSeconds),
                BatteryCalibrationPhase.Complete => 100,
                _ => 58
            };

            charge = Math.Clamp(charge, 0, 100);
            bool onAc = phase is BatteryCalibrationPhase.Idle
                or BatteryCalibrationPhase.ChargeToFull
                or BatteryCalibrationPhase.CellBalance
                or BatteryCalibrationPhase.Recharge
                or BatteryCalibrationPhase.Complete;
            bool charging = onAc && phase is (BatteryCalibrationPhase.ChargeToFull or BatteryCalibrationPhase.Recharge);
            bool discharging = !onAc && phase == BatteryCalibrationPhase.Drain;

            int percent = (int)Math.Round(charge);
            int remaining = (int)Math.Round(DebugFullCapacityMWh * charge / 100.0);
            int voltage = (int)Math.Round(10800 + charge / 100.0 * 1800);
            int rate = phase switch
            {
                BatteryCalibrationPhase.ChargeToFull => 28000,
                BatteryCalibrationPhase.CellBalance => 1200,
                BatteryCalibrationPhase.Drain => -28000,
                BatteryCalibrationPhase.Recharge => 24000,
                _ => 0
            };
            double temp = phase == BatteryCalibrationPhase.Drain
                ? 34.0 + Math.Min(8.0, elapsed / DebugDrainSeconds * 8.0)
                : 31.5;

            return new BatterySnapshot
            {
                HasBattery = true,
                Source = "Simulation Debug",
                Name = "Batterie debug Tweakly",
                Manufacturer = "Tweakly",
                Chemistry = "Lithium-ion",
                ChargePercent = percent,
                RemainingCapacityMWh = remaining,
                FullChargeCapacityMWh = DebugFullCapacityMWh,
                DesignCapacityMWh = DebugDesignCapacityMWh,
                CycleCount = 524,
                VoltageMv = voltage,
                RateMw = rate,
                TemperatureC = Math.Round(temp, 1),
                OnAcPower = onAc,
                IsCharging = charging,
                IsDischarging = discharging,
                IsCritical = percent <= 5 && phase == BatteryCalibrationPhase.Drain
            };
        }

        private static double Lerp(double from, double to, double t)
            => from + (to - from) * Math.Clamp(t, 0, 1);
#endif

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

            var decision = BatteryResumeEvaluator.Evaluate(
                _session.Phase,
                _session.PhaseStartedAt,
                _session.VerifiedRestSeconds,
                last.Phase,
                last.Timestamp,
                DateTime.Now,
                _systemBootTime,
                RestTargetDuration());

            if (decision.RecoveredPhase &&
                decision.Action is BatteryResumeAction.None or BatteryResumeAction.TelemetryGapWithoutRestart)
            {
                _session.Phase = decision.Phase;
                _session.PhaseStartedAt = decision.PhaseStartedAt;
                BatteryCalibrationStore.Save(_session);
            }

            if (decision.Action == BatteryResumeAction.TelemetryGapWithoutRestart)
            {
                if (!_telemetryGapLogged)
                {
                    var gap = DateTime.Now - last.Timestamp;
                    AppLog.Write($"Calibrage batterie : trou de télémétrie de {FormatDuration(gap)} sans redémarrage Windows, phase Drain conservée.");
                    _telemetryGapLogged = true;
                }
                return;
            }

            if (decision.Action is BatteryResumeAction.RestIncomplete or BatteryResumeAction.RestComplete)
            {
                _telemetryGapLogged = false;
                _session.VerifiedRestSeconds = decision.VerifiedRestSeconds;
                AppLog.Write($"Calibrage batterie : extinction confirmée, repos hors tension vérifié = {FormatDuration(decision.OfflineDuration)}.");
                if (decision.Action == BatteryResumeAction.RestComplete)
                    SetPhase(BatteryCalibrationPhase.Recharge, save: true);
                else
                {
                    SetPhase(BatteryCalibrationPhase.Rest, save: true, decision.PhaseStartedAt);
                    MarkProtocolWarning($"Repos incomplet : le PC a redémarré après {FormatDuration(decision.OfflineDuration)}. Éteins-le pendant {RestTargetLabel()} sans le rallumer.");
                    BatteryCalibrationStore.Save(_session);
                }
                RestorePowerPlanGuardIfNeeded();
            }
        }

        private static DateTime? ReadSystemBootTime()
        {
            try
            {
                using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
                foreach (ManagementObject item in query.Get())
                {
                    string? raw = item["LastBootUpTime"]?.ToString();
                    item.Dispose();
                    if (!string.IsNullOrWhiteSpace(raw))
                        return ManagementDateTimeConverter.ToDateTime(raw);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Calibrage batterie : lecture du dernier démarrage Windows", ex);
            }

            try { return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64); }
            catch { return null; }
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
                    else if (DateTime.Now - _session.PhaseStartedAt >= BalanceTargetDuration())
                        SetPhase(BatteryCalibrationPhase.Drain, save: true);
                    break;

                case BatteryCalibrationPhase.Drain:
#if DEBUG
                    if (_debugSimulationEnabled && snapshot.ChargePercent <= 5)
                    {
                        StopDrainLoad();
                        RestorePowerPlanGuardIfNeeded();
                        SetPhase(BatteryCalibrationPhase.Rest, save: true);
                        break;
                    }
#endif
                    if (snapshot.OnAcPower == true)
                    {
                        StopDrainLoad();
                        RestorePowerPlanGuardIfNeeded();
                    }
                    else if (snapshot.TemperatureC >= DrainHotBatteryC)
                    {
                        _drainDutyPercent = DrainMinDutyPercent;
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
#if DEBUG
                    if (_debugSimulationEnabled
                        && _session.LastSample != null
                        && DateTime.Now - _session.PhaseStartedAt >= RestTargetDuration())
                        SetPhase(BatteryCalibrationPhase.Recharge, save: true);
#endif
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
            NotifyPhaseActionIfNeeded(phase);
        }

        private void NotifyPhaseActionIfNeeded(BatteryCalibrationPhase phase)
        {
            switch (phase)
            {
                case BatteryCalibrationPhase.CellBalance:
                    if (_session.ChargeFullPromptShown) return;
                    _session.ChargeFullPromptShown = true;
                    BatteryCalibrationStore.Save(_session);
                    ShowCalibrationAlert(
                        "Charge complète",
                        $"La batterie est à 100 %. Garde le chargeur branché pendant {BalanceTargetLabel()} pour l'équilibrage des cellules.",
                        warning: false);
                    break;

                case BatteryCalibrationPhase.Drain:
                    if (_session.DrainPromptShown) return;
                    _session.DrainPromptShown = true;
                    BatteryCalibrationStore.Save(_session);
                    ShowCalibrationAlert(
                        "Débranche le chargeur",
                        "Équilibrage terminé. Débranche le chargeur maintenant : le drain contrôlé démarre dès que le PC passe sur batterie.",
                        warning: true);
                    break;

                case BatteryCalibrationPhase.Recharge:
                    if (_session.RechargePromptShown) return;
                    _session.RechargePromptShown = true;
                    BatteryCalibrationStore.Save(_session);
                    ShowCalibrationAlert(
                        "Rebranche le chargeur",
                        $"Repos terminé. Branche le chargeur maintenant et laisse monter jusqu'à 100 % sans coupure.",
                        warning: true);
                    break;

                case BatteryCalibrationPhase.Complete:
                    if (_session.CompletePromptShown) return;
                    _session.CompletePromptShown = true;
                    BatteryCalibrationStore.Save(_session);
                    ShowCalibrationAlert(
                        "Calibrage terminé",
                        "Le protocole est terminé. Tu peux ouvrir le rapport pour vérifier les mesures.",
                        warning: false);
                    break;
            }
        }

        private void ShowCalibrationAlert(string title, string message, bool warning)
        {
            if (warning) UiSound.Warn();
            else UiSound.Success();

            try
            {
                var owner = Window.GetWindow(this);
                if (owner != null)
                {
                    if (owner.WindowState == WindowState.Minimized)
                        owner.WindowState = WindowState.Normal;
                    owner.Activate();
                }

                MessageBox.Show(
                    owner,
                    message,
                    title,
                    MessageBoxButton.OK,
                    warning ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch
            {
                MessageBox.Show(
                    message,
                    title,
                    MessageBoxButton.OK,
                    warning ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
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
                ClearProtocolWarningPrefix("Action batterie critique Windows");
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
                ClearProtocolWarningPrefix("Restauration du plan d'alimentation");
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

        private void ClearProtocolWarningPrefix(string prefix)
        {
            if (!string.IsNullOrWhiteSpace(_session.LastWarning) &&
                _session.LastWarning.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _session.LastWarning = "";
        }

        private void ClearResolvedPowerPlanWarning()
        {
            if (_session.PowerPlanGuardApplied || !string.IsNullOrWhiteSpace(_session.PowerPlanGuardError)) return;
            ClearProtocolWarningPrefix("Action batterie critique Windows");
            ClearProtocolWarningPrefix("Restauration du plan d'alimentation");
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

            TxtSource.Text = s.BatteryCount > 1
                ? $"{s.BatteryCount} batteries | {FormatSource(s.Source)}"
                : FormatSource(s.Source);
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
                ? $"Maintien branché : compteur réinitialisé après coupure secteur. {ElapsedPhaseText(BatteryCalibrationPhase.CellBalance)} / {BalanceTargetLabel()}."
                : $"Maintien branché : {ElapsedPhaseText(BatteryCalibrationPhase.CellBalance)} / {BalanceTargetLabel()}.";
            TxtStepDrain.Text = _session.Phase == BatteryCalibrationPhase.Drain && _lastSnapshot.TemperatureC >= DrainHotBatteryC
                ? $"Drain en pause sécurité : température batterie {FormatC(_lastSnapshot.TemperatureC)}."
                : _session.Phase == BatteryCalibrationPhase.Drain && _lastSnapshot.OnAcPower == false
                ? $"Drain CPU contrôlé actif : {_drainThreads.Count} thread(s), intensité {_drainDutyPercent} %. Dernier point : {FormatPercent(_lastSnapshot.ChargePercent)}."
                : "Débranche le secteur. Tweakly appliquera une charge CPU contrôlée et sauvegardera chaque point.";
            TxtStepRest.Text = $"Repos total requis : {RestElapsedText()} / {RestTargetLabel()}.";
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
                BatteryCalibrationPhase.CellBalance => PercentOf(DateTime.Now - _session.PhaseStartedAt, BalanceTargetDuration()),
                BatteryCalibrationPhase.Drain => _lastSnapshot.ChargePercent.HasValue ? Math.Clamp(100 - _lastSnapshot.ChargePercent.Value, 0, 100) : 0,
                BatteryCalibrationPhase.Rest => RestProgress(),
                BatteryCalibrationPhase.Recharge => _lastSnapshot.ChargePercent ?? 0,
                _ => 0
            };
        }

        private double RestProgress()
        {
#if DEBUG
            if (_debugSimulationEnabled)
                return PercentOf(DateTime.Now - _session.PhaseStartedAt, RestTargetDuration());
#endif
            return PercentOf(TimeSpan.FromSeconds(Math.Max(0, _session.VerifiedRestSeconds)), RestTargetDuration());
        }

        private static double PercentOf(TimeSpan elapsed, TimeSpan target)
            => target.TotalSeconds <= 0 ? 0 : Math.Clamp(elapsed.TotalSeconds * 100.0 / target.TotalSeconds, 0, 100);

        private TimeSpan BalanceTargetDuration()
        {
#if DEBUG
            if (_debugSimulationEnabled) return TimeSpan.FromSeconds(DebugBalanceSeconds);
#endif
            return TimeSpan.FromHours(BalanceHours);
        }

        private TimeSpan RestTargetDuration()
        {
#if DEBUG
            if (_debugSimulationEnabled) return TimeSpan.FromSeconds(DebugRestSeconds);
#endif
            return TimeSpan.FromHours(RestHours);
        }

        private string BalanceTargetLabel()
        {
#if DEBUG
            if (_debugSimulationEnabled) return $"{BalanceHours} h simulees en {DebugBalanceSeconds:0} s";
#endif
            return $"{BalanceHours} h";
        }

        private string RestTargetLabel()
        {
#if DEBUG
            if (_debugSimulationEnabled) return $"{RestHours} h simulees en {DebugRestSeconds:0} s";
#endif
            return $"{RestHours} h";
        }

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
            BatteryCalibrationPhase.Rest => RestPhaseDetail(),
            BatteryCalibrationPhase.Recharge => _lastSnapshot.OnAcPower == true
                ? "Recharge jusqu'à 100 % sans coupure secteur. Tweakly continue le graphique et le rapport final."
                : "Branche le chargeur pour terminer la recharge complète.",
            BatteryCalibrationPhase.Complete => "Le rapport contient les points de charge, drain, repos et recharge finale.",
            _ => "Lance le calibrage pour démarrer le protocole complet."
        };

        private string RestPhaseDetail()
        {
#if DEBUG
            if (_debugSimulationEnabled)
                return "Simulation du repos total en cours.";
#endif
            return "Le repos ne compte que lorsque le PC est éteint. Éteins-le et laisse-le arrêté pendant 8 h.";
        }

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

        private void EnsureDrainLoad()
        {
            if (_drainCts != null) return;

            _drainCts = new CancellationTokenSource();
            int workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
            _drainThreads.Clear();

            for (int i = 0; i < workers; i++)
            {
                var thread = new Thread(() => DrainLoop(_drainCts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
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
            if (snapshot.TemperatureC >= DrainHotBatteryC)
            {
                _drainDutyPercent = DrainMinDutyPercent;
                return;
            }

            if (snapshot.TemperatureC >= DrainWarmBatteryC)
                _drainDutyPercent = Math.Min(_drainDutyPercent, DrainWarmCapDutyPercent);

            var watts = Math.Abs(snapshot.PowerW ?? 0);
            if (watts <= 0) return;

            if (watts < DrainTargetMinW) _drainDutyPercent = Math.Min(DrainMaxDutyPercent, _drainDutyPercent + 10);
            else if (watts > DrainTargetMaxW) _drainDutyPercent = Math.Max(DrainMinDutyPercent, _drainDutyPercent - 10);
        }

        private void DrainLoop(CancellationToken token)
        {
            const int cycleMs = 200;
            double sink = 0;

            while (!token.IsCancellationRequested)
            {
                int duty = Math.Clamp(_drainDutyPercent, DrainMinDutyPercent, DrainMaxDutyPercent);
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

        private string ElapsedPhaseText(BatteryCalibrationPhase phase)
        {
            if (PhaseOrder(_session.Phase) > PhaseOrder(phase)) return phase == BatteryCalibrationPhase.CellBalance ? BalanceTargetLabel() : "terminé";
            if (_session.Phase != phase) return "0 min";
            var elapsed = DateTime.Now - _session.PhaseStartedAt;
            return FormatDuration(elapsed);
        }

        private string RestElapsedText()
        {
            if (PhaseOrder(_session.Phase) > PhaseOrder(BatteryCalibrationPhase.Rest)) return RestTargetLabel();
            var last = _session.LastSample;
            if (last == null || PhaseOrder(_session.Phase) < PhaseOrder(BatteryCalibrationPhase.Rest)) return "0 min";
#if DEBUG
            if (_debugSimulationEnabled) return FormatDuration(DateTime.Now - _session.PhaseStartedAt);
#endif
            return FormatDuration(TimeSpan.FromSeconds(Math.Max(0, _session.VerifiedRestSeconds)));
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
                return $"Cycles : {FormatCycles(s.CycleCount)}\nTempérature : {FormatC(s.TemperatureC)}";
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
        private static string FormatC(double? value) => value.HasValue ? $"{value.Value:0.0} °\u2060C" : "-- °\u2060C";
        private static string FormatMWh(int? value) => value.HasValue ? $"{value.Value:N0} mWh" : "-- mWh";
        private static string FormatCycles(int? value) => value.HasValue ? $"{value.Value} cycle(s)" : "-- cycle(s)";
        private static string FormatHealth(double? value) => value.HasValue ? $"{value.Value:0} %" : "-- %";
        private static string FormatSource(string value)
            => string.IsNullOrWhiteSpace(value)
                ? "Windows"
                : value.Replace("Battery API", "API").Replace("ACPI WMI", "ACPI").Replace("Win32_Battery", "Win32");
        private static string AcText(bool? onAc) => onAc == true ? "branché" : onAc == false ? "batterie" : "inconnu";
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
