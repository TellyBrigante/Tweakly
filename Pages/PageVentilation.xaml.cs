using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanControl.Core;
using Optimisation_Tool.Controls;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages;

public sealed class FanChannelItem : INotifyPropertyChanged
{
    private double _rpm;
    private double _controlPercent;

    public required DetectedFanChannel Channel { get; init; }
    public string Name => Channel.DisplayName;
    public string TechnicalName => $"{Channel.HardwareName} - canal mat\u00e9riel {Channel.Index}";
    public string RpmText => $"{_rpm:0} tr/min";
    public string ControlText => $"{_controlPercent:0} %";
    public string IdentificationText => Channel.RoleSource;
    public FanRole Role => Channel.SuggestedRole;
    public string RoleText => Role switch
    {
        FanRole.Cpu => "Ventilateur processeur",
        FanRole.Chassis => "Ventilateurs bo\u00eetier / hub",
        FanRole.Radiator => "Ventilateur radiateur",
        FanRole.Pump => "Pompe - exclue de la calibration",
        _ => "Non identifi\u00e9 automatiquement"
    };

    public void InitializeTelemetry()
    {
        _rpm = Channel.Rpm;
        _controlPercent = Channel.ControlPercent;
    }

    public void UpdateTelemetry(double rpm, double controlPercent)
    {
        if (Math.Abs(_rpm - rpm) >= 0.5)
        {
            _rpm = rpm;
            OnPropertyChanged(nameof(RpmText));
        }

        if (Math.Abs(_controlPercent - controlPercent) >= 0.05)
        {
            _controlPercent = controlPercent;
            OnPropertyChanged(nameof(ControlText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class FanCurveItem : INotifyPropertyChanged
{
    private double _currentTemperature = double.NaN;
    private double _currentDuty = double.NaN;

    public required string ChannelId { get; init; }
    public required string Name { get; init; }
    public required string SourceText { get; init; }
    public required string CalibrationText { get; init; }
    public required double MinimumDuty { get; init; }
    public required ThermalSource Source { get; init; }
    public required ObservableCollection<FanCurvePoint> Points { get; init; }
    public required IReadOnlyList<FanCurvePoint> AutomaticPoints { get; init; }

    public double CurrentTemperature
    {
        get => _currentTemperature;
        private set
        {
            if (double.IsNaN(_currentTemperature) && double.IsNaN(value) || Math.Abs(_currentTemperature - value) < 0.05) return;
            _currentTemperature = value;
            OnPropertyChanged();
        }
    }

    public double CurrentDuty
    {
        get => _currentDuty;
        private set
        {
            if (double.IsNaN(_currentDuty) && double.IsNaN(value) || Math.Abs(_currentDuty - value) < 0.05) return;
            _currentDuty = value;
            OnPropertyChanged();
        }
    }

    public void UpdateLivePosition(double? temperature)
    {
        CurrentTemperature = temperature is double t && double.IsFinite(t) ? t : double.NaN;
        CurrentDuty = double.IsFinite(CurrentTemperature)
            ? InterpolateDuty(CurrentTemperature)
            : double.NaN;
    }

    private double InterpolateDuty(double temperatureC)
    {
        if (Points.Count == 0) return double.NaN;
        if (temperatureC <= Points[0].TemperatureC) return Points[0].DutyPercent;
        for (int index = 1; index < Points.Count; index++)
        {
            FanCurvePoint upper = Points[index];
            if (temperatureC > upper.TemperatureC) continue;
            FanCurvePoint lower = Points[index - 1];
            double span = upper.TemperatureC - lower.TemperatureC;
            double ratio = span <= 0 ? 0 : (temperatureC - lower.TemperatureC) / span;
            return lower.DutyPercent + ((upper.DutyPercent - lower.DutyPercent) * ratio);
        }
        return Points[^1].DutyPercent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class PageVentilation : UserControl
{
    private readonly MainWindow _main;
    private readonly ObservableCollection<FanChannelItem> _channels = [];
    private readonly ObservableCollection<FanCurveItem> _curveItems = [];
    private FanHardwareInventoryResult? _inventory;
    private CancellationTokenSource? _calibrationCancellation;
    private FanHardwareSession? _activeSession;
    private FanSafetyWatchdogClient? _activeWatchdog;
    private CancellationTokenSource? _telemetryCancellation;
    private Task? _telemetryTask;
    private FanHardwareSession? _telemetrySession;
    private Task<FanHardwareInventoryResult>? _inventoryTask;
    private bool _loaded;
    private FanProfileDocument? _currentDocument;
    private double _temperatureHysteresisC = 2;
    private double _rampUpPercentPerSecond = 12;
    private double _rampDownPercentPerSecond = 3;

    public PageVentilation(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        FanList.ItemsSource = _channels;
        CurveList.ItemsSource = _curveItems;
        FanRuntimeController.Updated += FanRuntimeController_Updated;
        FanRuntimeController.Stopped += FanRuntimeController_Stopped;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshThemeVisuals();
        if (_loaded) return;
        _loaded = true;
        if (_inventory is not null)
        {
            UpdateActions();
            UpdateControlModeVisuals();
            if (!FanRuntimeController.IsRunning)
                await StartTelemetryAsync();
            return;
        }
        await RefreshInventoryAsync();
    }

    public void RefreshThemeVisuals()
    {
        foreach (FanCurveEditor editor in FindVisualChildren<FanCurveEditor>(CurveList))
            editor.RefreshThemeVisuals();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshInventoryAsync();
    }

    private async Task RefreshInventoryAsync()
    {
        bool restartAutomaticControl = FanRuntimeController.IsRunning;
        FanProfileDocument? profileBeforeRefresh = _currentDocument;
        if (restartAutomaticControl && !await StopAutomaticControlAsync())
            return;
        await StopTelemetryAsync();
        SetBusy(true);
        TxtInventoryStatus.Text = "Lecture des ventilateurs de la carte m\u00e8re...";
        SetActionStatus("D\u00e9tection en cours.", false);
        NoFanCard.Visibility = Visibility.Collapsed;

        var stopwatch = Stopwatch.StartNew();
        bool inventoryLoaded = false;
        try
        {
            _inventoryTask ??= Task.Run(FanHardwareInventory.Read);
            FanHardwareInventoryResult inventory = await _inventoryTask.WaitAsync(TimeSpan.FromSeconds(15));
            _inventoryTask = null;
            _inventory = inventory;

            _channels.Clear();
            for (int channelIndex = 0; channelIndex < inventory.Channels.Count; channelIndex++)
            {
                DetectedFanChannel channel = inventory.Channels[channelIndex];
                var item = new FanChannelItem
                {
                    Channel = channel
                };
                item.InitializeTelemetry();
                _channels.Add(item);
            }

            TxtMotherboard.Text = inventory.MotherboardName;
            TxtController.Text = inventory.ControllerName;
            TxtChannelCount.Text = inventory.Channels.Count.ToString();
            TxtInventoryStatus.Text = inventory.Message;
            NoFanCard.Visibility = inventory.Channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtNoFan.Text = inventory.Message;
            inventoryLoaded = true;
            _currentDocument = BuildDocument();
            LoadCurveItems(_currentDocument);
            _main.Log($"Ventilation : {inventory.Message} ({stopwatch.Elapsed.TotalSeconds:0.0} s). ");
        }
        catch (TimeoutException)
        {
            TxtInventoryStatus.Text = "La lecture materielle prend trop de temps.";
            SetActionStatus("Tweakly reste utilisable. Attends la fin de la lecture avant de reessayer.", true);
            NoFanCard.Visibility = Visibility.Visible;
            TxtNoFan.Text = "Le controleur de ventilation ne repond pas dans le delai de 15 s.";
            Helpers.AppLog.Write("Ventilation : inventaire toujours en cours apres 15 s.");
        }
        catch (Exception ex)
        {
            _inventoryTask = null;
            TxtInventoryStatus.Text = "Lecture materielle impossible.";
            SetActionStatus("Aucun reglage n'a ete modifie.", true);
            NoFanCard.Visibility = Visibility.Visible;
            TxtNoFan.Text = ex.GetBaseException().Message;
            Helpers.AppLog.Error("Ventilation : chargement de la page", ex);
        }
        finally
        {
            SetBusy(false);
            if (inventoryLoaded)
            {
                UpdateActions();
            }

            FanProfileDocument? profileToRestart = inventoryLoaded
                ? _currentDocument
                : profileBeforeRefresh;
            if (restartAutomaticControl && profileToRestart is not null && HasUsableCurves(profileToRestart))
            {
                try
                {
                    await FanRuntimeController.StartAsync(profileToRestart);
                    _currentDocument = profileToRestart;
                    TxtCurveStatus.Text = inventoryLoaded
                        ? "Auto Tweakly actif."
                        : "Lecture materielle interrompue. Les courbes precedentes restent actives.";
                    UpdateControlModeVisuals();
                }
                catch (Exception ex)
                {
                    _currentDocument = profileToRestart with { AutomaticControlEnabled = false };
                    if (!FanProfileStore.Save(_currentDocument, out string persistenceError))
                        AppLog.Write("Ventilation : etat inactif non enregistre : " + persistenceError);
                    TxtCurveStatus.Text = "Mode Auto Tweakly non relance : " + ex.GetBaseException().Message;
                    AppLog.Error("Ventilation : reprise du controle apres inventaire", ex);
                    await StartTelemetryAsync();
                }
            }
            else
            {
                await StartSavedControlOrTelemetryAsync();
            }
        }
    }

    private async Task StartTelemetryAsync()
    {
        if (!_loaded || _channels.Count == 0 || _telemetrySession is not null)
            return;

        try
        {
            FanHardwareSession session = await Task.Run(FanHardwareSession.Open);
            if (!_loaded)
            {
                session.Dispose();
                return;
            }

            _telemetrySession = session;
            _telemetryCancellation = new CancellationTokenSource();
            _telemetryTask = RunTelemetryLoopAsync(session, _telemetryCancellation.Token);
        }
        catch (Exception ex)
        {
            AppLog.ErrorOnce("fan-live-telemetry-open", "Ventilation : suivi en direct indisponible", ex);
        }
    }

    private async Task RunTelemetryLoopAsync(FanHardwareSession session, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            do
            {
                FanControlSnapshot snapshot = await Task.Run(session.ReadControlSnapshot, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (FanTelemetrySample sample in snapshot.Fans)
                    {
                        FanChannelItem? item = _channels.FirstOrDefault(channel =>
                            string.Equals(channel.Channel.Id, sample.ChannelId, StringComparison.Ordinal));
                        item?.UpdateTelemetry(sample.Rpm, sample.ControlPercent);
                    }
                    UpdateLiveCurvePositions(snapshot);
                });
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.ErrorOnce("fan-live-telemetry-read", "Ventilation : actualisation des vitesses interrompue", ex);
        }
    }

    private async Task StopTelemetryAsync()
    {
        CancellationTokenSource? cancellation = _telemetryCancellation;
        Task? task = _telemetryTask;
        FanHardwareSession? session = _telemetrySession;
        _telemetryCancellation = null;
        _telemetryTask = null;
        _telemetrySession = null;

        try { cancellation?.Cancel(); } catch { }
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
        try { session?.Dispose(); } catch { }
        cancellation?.Dispose();
    }

    private async Task StartSavedControlOrTelemetryAsync()
    {
        if (FanRuntimeController.IsRunning)
        {
            UpdateControlModeVisuals();
            TxtCurveStatus.Text = "Auto Tweakly actif.";
            return;
        }

        UpdateControlModeVisuals();
        await StartTelemetryAsync();
    }

    private void LoadCurveItems(FanProfileDocument? document)
    {
        _curveItems.Clear();
        if (document is null)
        {
            CurveSection.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (SavedFanChannel channel in document.Channels.Where(static channel => channel.Curve.Count >= 2))
        {
            double minimum = channel.Calibration is { IsValid: true } calibration
                ? Math.Max(calibration.MinimumStableDutyPercent, calibration.RestartDutyPercent)
                : 0;
            _curveItems.Add(new FanCurveItem
            {
                ChannelId = channel.Id,
                Name = channel.DisplayName,
                SourceText = channel.Source switch
                {
                    ThermalSource.Cpu => "Pilot\u00e9e par la temp\u00e9rature CPU",
                    ThermalSource.Gpu => "Pilot\u00e9e par la temp\u00e9rature GPU",
                    _ => "Pilot\u00e9e par la temp\u00e9rature la plus haute (CPU ou GPU)"
                },
                CalibrationText = channel.Calibration is null
                    ? "Calibration indisponible"
                    : $"Plancher mesur\u00e9 : {minimum:0} %  |  Maximum mesur\u00e9 : {channel.Calibration.MaximumObservedRpm:0} tr/min",
                MinimumDuty = minimum,
                Source = channel.Source,
                Points = new ObservableCollection<FanCurvePoint>(channel.Curve),
                AutomaticPoints = (channel.AutomaticCurve.Count >= 2 ? channel.AutomaticCurve : channel.Curve).ToArray()
            });
        }

        CurveSection.Visibility = _curveItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChkStartWithTweakly.IsChecked = document.StartWithTweakly;
        _temperatureHysteresisC = Math.Clamp(document.TemperatureHysteresisC, 0, 5);
        _rampUpPercentPerSecond = Math.Clamp(document.RampUpPercentPerSecond, 2, 30);
        _rampDownPercentPerSecond = Math.Clamp(document.RampDownPercentPerSecond, 1, 15);
        UpdateResponseSettingsText();
        UpdateControlModeVisuals();
        UpdateSavedProfileSummary();
        UpdateRestoreAutomaticButton();
    }

    private FanProfileDocument BuildDocumentFromCurves(bool? automaticControlEnabled = null)
    {
        FanProfileDocument document = _currentDocument
            ?? throw new InvalidOperationException("Le profil de ventilation n'est pas charge.");
        var byId = _curveItems.ToDictionary(static item => item.ChannelId, StringComparer.Ordinal);
        return document with
        {
            AutomaticControlEnabled = automaticControlEnabled ?? document.AutomaticControlEnabled,
            StartWithTweakly = ChkStartWithTweakly.IsChecked == true,
            TemperatureHysteresisC = _temperatureHysteresisC,
            RampUpPercentPerSecond = _rampUpPercentPerSecond,
            RampDownPercentPerSecond = _rampDownPercentPerSecond,
            Channels = document.Channels.Select(channel => byId.TryGetValue(channel.Id, out FanCurveItem? item)
                ? channel with { Curve = FanCurvePlanner.Normalize(item.Points) }
                : channel).ToArray()
        };
    }

    private void FanCurveEditor_CurveChanged(object sender, EventArgs e)
    {
        TxtCurveStatus.Text = "Courbe modifi\u00e9e. Enregistre le profil pour conserver et appliquer ce r\u00e9glage.";
        UpdateRestoreAutomaticButton();
    }

    private void BtnResetCurves_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (FanCurveItem item in _curveItems)
            {
                item.Points.Clear();
                foreach (FanCurvePoint point in item.AutomaticPoints)
                    item.Points.Add(point);
            }
            CurveList.Items.Refresh();
            bool applyToRuntime = FanRuntimeController.IsRunning;
            FanProfileDocument requestedDocument = BuildDocumentFromCurves(applyToRuntime) with
            {
                ProfileSavedAt = DateTimeOffset.UtcNow
            };
            if (!FanProfileStore.Save(requestedDocument, out string error))
                throw new IOException(error);
            if (applyToRuntime && !FanRuntimeController.UpdateProfile(requestedDocument))
            {
                _currentDocument = requestedDocument with { AutomaticControlEnabled = false };
                if (!FanProfileStore.Save(_currentDocument, out string inactiveSaveError))
                {
                    throw new IOException(
                        "Le controle automatique s'est arrete et son etat inactif n'a pas pu etre enregistre : " +
                        inactiveSaveError);
                }
                throw new InvalidOperationException(
                    "Le controle automatique s'est arrete avant l'application des courbes.");
            }
            _currentDocument = requestedDocument;
            TxtCurveStatus.Text = FanRuntimeController.IsRunning
                ? "Courbe automatique mesur\u00e9e restaur\u00e9e et appliqu\u00e9e."
                : "Courbe automatique mesur\u00e9e restaur\u00e9e. Le BIOS garde le contr\u00f4le.";
            UpdateSavedProfileSummary();
            UpdateRestoreAutomaticButton();
        }
        catch (Exception ex)
        {
            TxtCurveStatus.Text = "Restauration impossible : " + ex.GetBaseException().Message;
            AppLog.Error("Ventilation : restauration des courbes", ex);
        }
    }

    private async void BtnAutoMode_Click(object sender, RoutedEventArgs e)
    {
        SetCurveCommandsEnabled(false);
        FanProfileDocument? requestedDocument = null;
        try
        {
            requestedDocument = BuildDocumentFromCurves(true);
            await StopTelemetryAsync();
            await FanRuntimeController.StartAsync(requestedDocument);
            if (!FanProfileStore.Save(requestedDocument, out string saveError))
            {
                FanRestorationOutcome restoration = await FanRuntimeController.StopAsync();
                string restorationDetail = restoration.Success ? "" : " " + restoration.Message;
                throw new IOException(saveError + restorationDetail);
            }
            _currentDocument = requestedDocument;
            TxtCurveStatus.Text = "Auto Tweakly actif.";
        }
        catch (Exception ex)
        {
            if (requestedDocument is not null)
            {
                _currentDocument = requestedDocument with { AutomaticControlEnabled = false };
                if (!FanProfileStore.Save(_currentDocument, out string persistenceError))
                    AppLog.Write("Ventilation : etat inactif non enregistre : " + persistenceError);
            }
            TxtCurveStatus.Text = "Commande impossible : " + ex.GetBaseException().Message;
            AppLog.Error("Ventilation : activation du controle automatique", ex);
            if (!FanRuntimeController.IsRunning)
                await StartTelemetryAsync();
        }
        finally
        {
            SetCurveCommandsEnabled(true);
            UpdateControlModeVisuals();
        }
    }

    private async void BtnBiosMode_Click(object sender, RoutedEventArgs e)
    {
        SetCurveCommandsEnabled(false);
        try
        {
            FanRestorationOutcome restoration = await FanRuntimeController.StopAsync();
            if (!restoration.Success)
                throw new InvalidOperationException(restoration.Message);
            _currentDocument = BuildDocumentFromCurves(false);
            if (!FanProfileStore.Save(_currentDocument, out string saveError))
                throw new IOException(saveError);
            TxtCurveStatus.Text = "R\u00e9glage BIOS actif.";
            await StartTelemetryAsync();
        }
        catch (Exception ex)
        {
            TxtCurveStatus.Text = "Retour au BIOS impossible : " + ex.GetBaseException().Message;
            AppLog.Error("Ventilation : retour au controle BIOS", ex);
        }
        finally
        {
            SetCurveCommandsEnabled(true);
            UpdateControlModeVisuals();
        }
    }

    private async void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
    {
        bool saved = false;
        SetCurveCommandsEnabled(false);
        try
        {
            bool applyToRuntime = FanRuntimeController.IsRunning;
            FanProfileDocument requestedDocument = BuildDocumentFromCurves(applyToRuntime) with
            {
                ProfileSavedAt = DateTimeOffset.UtcNow
            };
            if (!FanProfileStore.Save(requestedDocument, out string saveError))
                throw new IOException(saveError);
            if (applyToRuntime && !FanRuntimeController.UpdateProfile(requestedDocument))
            {
                _currentDocument = requestedDocument with { AutomaticControlEnabled = false };
                if (!FanProfileStore.Save(_currentDocument, out string inactiveSaveError))
                {
                    throw new IOException(
                        "Le controle automatique s'est arrete et son etat inactif n'a pas pu etre enregistre : " +
                        inactiveSaveError);
                }
                throw new InvalidOperationException(
                    "Le controle automatique s'est arrete avant l'application du profil.");
            }
            _currentDocument = requestedDocument;
            TxtCurveStatus.Text = FanRuntimeController.IsRunning
                ? "Profil enregistr\u00e9 et appliqu\u00e9."
                : "Profil enregistr\u00e9. Le BIOS garde le contr\u00f4le.";
            saved = true;
            UpdateSavedProfileSummary();
            UpdateRestoreAutomaticButton();
        }
        catch (Exception ex)
        {
            TxtCurveStatus.Text = "Enregistrement impossible : " + ex.GetBaseException().Message;
            AppLog.Error("Ventilation : enregistrement du profil", ex);
        }
        finally
        {
            SetCurveCommandsEnabled(true);
            UpdateControlModeVisuals();
        }

        if (saved)
        {
            BtnSaveProfile.Content = "PROFIL ENREGISTR\u00c9";
            await Task.Delay(1400);
            BtnSaveProfile.Content = "ENREGISTRER LE PROFIL";
        }
    }

    private async void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = MessageBox.Show(
            "Le profil enregistr\u00e9 et ses courbes seront supprim\u00e9s. Le BIOS reprendra imm\u00e9diatement le contr\u00f4le des ventilateurs. Continuer ?",
            "Supprimer le profil de ventilation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetCurveCommandsEnabled(false);
        BtnDeleteProfile.IsEnabled = false;
        try
        {
            FanRestorationOutcome restoration = await FanRuntimeController.StopAsync();
            if (!restoration.Success)
                throw new InvalidOperationException(restoration.Message);
            if (!FanProfileStore.Delete(out string deleteError))
                throw new IOException(deleteError);

            _currentDocument = BuildDocument();
            _curveItems.Clear();
            CurveSection.Visibility = Visibility.Collapsed;
            SetActionStatus("Profil supprim\u00e9. Le BIOS contr\u00f4le les ventilateurs.", true);
            await StartTelemetryAsync();
            UpdateActions();
        }
        catch (Exception ex)
        {
            TxtCurveStatus.Text = "Suppression impossible : " + ex.GetBaseException().Message;
            AppLog.Error("Ventilation : suppression du profil", ex);
        }
        finally
        {
            BtnDeleteProfile.IsEnabled = true;
            SetCurveCommandsEnabled(true);
            UpdateControlModeVisuals();
        }
    }

    private void UserControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (BtnResponseSettings.IsChecked != true ||
            e.OriginalSource is not DependencyObject source ||
            IsWithin(source, BtnResponseSettings))
            return;
        BtnResponseSettings.IsChecked = false;
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ResponseAdjustment_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "HysteresisDown":
                _temperatureHysteresisC = Math.Max(0, _temperatureHysteresisC - 0.5);
                break;
            case "HysteresisUp":
                _temperatureHysteresisC = Math.Min(5, _temperatureHysteresisC + 0.5);
                break;
            case "RampUpDown":
                _rampUpPercentPerSecond = Math.Max(2, _rampUpPercentPerSecond - 1);
                break;
            case "RampUpUp":
                _rampUpPercentPerSecond = Math.Min(30, _rampUpPercentPerSecond + 1);
                break;
            case "RampDownDown":
                _rampDownPercentPerSecond = Math.Max(1, _rampDownPercentPerSecond - 1);
                break;
            case "RampDownUp":
                _rampDownPercentPerSecond = Math.Min(15, _rampDownPercentPerSecond + 1);
                break;
            default:
                return;
        }

        UpdateResponseSettingsText();
        TxtCurveStatus.Text = "Reactivite modifiee. Enregistre le profil pour appliquer ce reglage.";
    }

    private void UpdateResponseSettingsText()
    {
        if (!IsInitialized) return;
        BtnResponseSettings.Tag = $"{_temperatureHysteresisC:0.#} \u00b0C  |  +{_rampUpPercentPerSecond:0} / -{_rampDownPercentPerSecond:0} %/s";
        TxtHysteresisValue.Text = $"{_temperatureHysteresisC:0.#} \u00b0C";
        TxtRampUpValue.Text = $"{_rampUpPercentPerSecond:0} %/s";
        TxtRampDownValue.Text = $"{_rampDownPercentPerSecond:0} %/s";
    }

    private void SetActionStatus(string text, bool visible)
    {
        TxtActionStatus.Text = text;
        TxtActionStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FanRuntimeController_Updated(object? sender, FanRuntimeUpdate update)
    {
        if (!_loaded) return;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (FanTelemetrySample sample in update.Snapshot.Fans)
            {
                FanChannelItem? item = _channels.FirstOrDefault(channel =>
                    string.Equals(channel.Channel.Id, sample.ChannelId, StringComparison.Ordinal));
                double control = update.AppliedDuties.TryGetValue(sample.ChannelId, out double applied)
                    ? applied
                    : sample.ControlPercent;
                item?.UpdateTelemetry(sample.Rpm, control);
            }

            string cpu = update.Snapshot.CpuTemperatureC.HasValue ? $"CPU {update.Snapshot.CpuTemperatureC:0.0} \u00b0C" : "CPU indisponible";
            string gpu = update.Snapshot.GpuTemperatureC.HasValue ? $"GPU {update.Snapshot.GpuTemperatureC:0.0} \u00b0C" : "GPU indisponible";
            TxtCurveStatus.Text = $"Auto Tweakly actif  |  {cpu}  |  {gpu}";
            UpdateLiveCurvePositions(update.Snapshot);
        });
    }

    private void FanRuntimeController_Stopped(object? sender, string reason)
    {
        void ApplyStoppedState(bool updateInterface)
        {
            // Une ancienne boucle peut notifier son arrêt pendant qu'une nouvelle vient
            // d'être lancée. Dans ce cas, ne pas écraser le nouvel état actif.
            if (FanRuntimeController.IsRunning)
                return;

            if (_currentDocument is { AutomaticControlEnabled: true })
            {
                _currentDocument = _currentDocument with { AutomaticControlEnabled = false };
                if (!FanProfileStore.Save(_currentDocument, out string persistenceError))
                {
                    reason += " Etat inactif non enregistre : " + persistenceError;
                    AppLog.Write("Ventilation : " + reason);
                }
            }
            if (updateInterface && _loaded)
            {
                TxtCurveStatus.Text = reason;
                UpdateControlModeVisuals();
            }
        }

        if (Dispatcher.HasShutdownStarted)
            ApplyStoppedState(updateInterface: false);
        else
            Dispatcher.BeginInvoke(() => ApplyStoppedState(updateInterface: true));
    }

    private void UpdateControlModeVisuals()
    {
        StyleModeButton(BtnAutoMode, FanRuntimeController.IsRunning);
        StyleModeButton(BtnBiosMode, !FanRuntimeController.IsRunning);
        UpdateSavedProfileSummary();
    }

    private void UpdateSavedProfileSummary()
    {
        if (!IsInitialized || _currentDocument is null || _curveItems.Count == 0)
        {
            if (IsInitialized)
                SavedProfileCard.Visibility = Visibility.Collapsed;
            return;
        }

        SavedProfileCard.Visibility = Visibility.Visible;
        TxtSavedProfileTitle.Text = _currentDocument.MotherboardName;
        DateTimeOffset? savedAt = _currentDocument.ProfileSavedAt?.ToLocalTime();
        TxtSavedProfileDate.Text = savedAt.HasValue
            ? $"Enregistr\u00e9 le {savedAt.Value:dd/MM/yyyy \u00e0 HH:mm}"
            : "Profil charg\u00e9";

    }

    private void UpdateRestoreAutomaticButton()
    {
        if (!IsInitialized)
            return;

        bool differs = _curveItems.Any(item => !CurvesEqual(item.Points, item.AutomaticPoints));
        BtnResetCurves.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool CurvesEqual(
        IReadOnlyList<FanCurvePoint> left,
        IReadOnlyList<FanCurvePoint> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (Math.Abs(left[index].TemperatureC - right[index].TemperatureC) > 0.01 ||
                Math.Abs(left[index].DutyPercent - right[index].DutyPercent) > 0.01)
                return false;
        }

        return true;
    }

    private static void StyleModeButton(Button button, bool active)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("Bg", button) is not Border background ||
            button.Template.FindName("Lbl", button) is not TextBlock label)
            return;
        if (active)
        {
            background.SetResourceReference(Border.BackgroundProperty, "ThTabSel");
            label.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
        }
        else
        {
            background.Background = System.Windows.Media.Brushes.Transparent;
            label.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
        }
    }

    private void SetCurveCommandsEnabled(bool enabled)
    {
        BtnAutoMode.IsEnabled = enabled;
        BtnBiosMode.IsEnabled = enabled;
        BtnResponseSettings.IsEnabled = enabled;
        BtnSaveProfile.IsEnabled = enabled;
        BtnResetCurves.IsEnabled = enabled;
        BtnDeleteProfile.IsEnabled = enabled;
        ChkStartWithTweakly.IsEnabled = enabled;
    }

    private async Task<bool> StopAutomaticControlAsync()
    {
        FanRestorationOutcome restoration = await FanRuntimeController.StopAsync();
        if (restoration.Success)
            return true;

        TxtCurveStatus.Text = restoration.Message;
        SetActionStatus(restoration.Message, true);
        AppLog.Write("Ventilation : " + restoration.Message);
        return false;
    }

    private void UpdateLiveCurvePositions(FanControlSnapshot snapshot)
    {
        foreach (FanCurveItem curve in _curveItems)
        {
            double? temperature = curve.Source switch
            {
                ThermalSource.Cpu => snapshot.CpuTemperatureC,
                ThermalSource.Gpu => snapshot.GpuTemperatureC,
                _ => snapshot.HottestTemperatureC
            };
            curve.UpdateLivePosition(temperature);
        }
    }

    private static bool HasUsableCurves(FanProfileDocument document) => document.Channels.Any(static channel =>
        channel.Calibration is { IsValid: true } && channel.Curve.Count >= 2 &&
        channel.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator);

    private async void BtnCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCancellation is not null)
        {
            _calibrationCancellation.Cancel();
            BtnCalibrate.IsEnabled = false;
            CalibrationCard.Visibility = Visibility.Collapsed;
            SetActionStatus("Annulation en cours. Le BIOS reprend le controle.", false);
            return;
        }

        FanProfileDocument? document = BuildDocument();
        if (document is null) return;
        FanProfileDocument profileBeforeCalibration = document;
        bool calibrationCompleted = false;
        bool controlWasRunning = FanRuntimeController.IsRunning;
        SavedFanChannel[] calibratable = document.Channels
            .Where(x => x.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator)
            .ToArray();
        if (calibratable.Length == 0) return;

        MessageBoxResult confirmation = MessageBox.Show(
            "Tweakly va d'abord mesurer la plage r\u00e9elle de chaque ventilateur, puis tester le PC au repos " +
            "et sous charge CPU contr\u00f4l\u00e9e pour construire les courbes. Le bruit et la charge processeur varieront " +
            "pendant plusieurs minutes. Ferme les jeux et les travaux importants avant de continuer.\n\n" +
            "En cas d'annulation ou d'erreur, le BIOS reprend imm\u00e9diatement le contr\u00f4le. Continuer ?",
            "Calibration de la ventilation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        if (!FanProfileStore.Save(document, out string saveError))
        {
            SetActionStatus("Enregistrement impossible : " + saveError, true);
            return;
        }

        if (controlWasRunning && !await StopAutomaticControlAsync())
            return;
        await StopTelemetryAsync();

        _calibrationCancellation = new CancellationTokenSource();
        CancellationToken token = _calibrationCancellation.Token;
        BtnRefresh.IsEnabled = false;
        BtnCalibrate.Content = "ANNULER";
        CalibrationCard.Visibility = Visibility.Visible;
        PbCalibration.Value = 0;
        CancellationTokenSource? watchdogProtection = null;

        try
        {
            _activeSession = await Task.Run(FanHardwareSession.Open, token);
            _activeWatchdog = FanSafetyWatchdogClient.Arm(calibratable.Select(static channel => channel.Id));
            _activeWatchdog.Pulse();
            watchdogProtection = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                _activeWatchdog.FailureToken);
            CancellationToken protectedToken = watchdogProtection.Token;
            var completed = new Dictionary<string, FanCalibrationExecution>(StringComparer.Ordinal);
            FanProfileDocument? completedProfile = null;
            for (int index = 0; index < calibratable.Length; index++)
            {
                SavedFanChannel channel = calibratable[index];
                int basePercent = (int)Math.Round(index * 55.0 / calibratable.Length);
                int spanPercent = (int)Math.Round(55.0 / calibratable.Length);
                var progress = new Progress<FanCalibrationProgress>(item =>
                {
                    _activeWatchdog?.Pulse();
                    int overall = Math.Clamp(basePercent + (item.Percent * spanPercent / 100), 0, 100);
                    PbCalibration.Value = overall;
                    TxtCalibrationPercent.Text = overall + " %";
                    TxtCalibrationStage.Text = channel.DisplayName + " - " + item.Stage;
                    string duty = item.DutyPercent.HasValue ? $"Commande {item.DutyPercent:0} %" : "Commande en lecture";
                    string rpm = item.Rpm.HasValue ? $" | {item.Rpm:0} tr/min" : "";
                    string temperature = item.HottestTemperatureC.HasValue ? $" | {item.HottestTemperatureC:0.0} \u00b0C" : "";
                    TxtCalibrationDetails.Text = duty + rpm + temperature;
                });

                FanCalibrationExecution execution = await _activeSession.CalibrateAsync(channel, progress, protectedToken);
                completed[channel.Id] = execution;

                document = document with
                {
                    Channels = document.Channels.Select(saved =>
                        completed.TryGetValue(saved.Id, out FanCalibrationExecution? result)
                            ? saved with { Calibration = result.Result }
                            : saved).ToArray()
                };
            }

            TxtCalibrationStage.Text = "Mesure thermique du PC";
            TxtCalibrationDetails.Text = "Mesure au repos, puis charges CPU mod\u00e9r\u00e9e et soutenue.";
            var thermalProgress = new Progress<FanThermalProgress>(item =>
            {
                _activeWatchdog?.Pulse();
                int overall = 55 + (item.Percent * 45 / 100);
                PbCalibration.Value = overall;
                TxtCalibrationPercent.Text = overall + " %";
                TxtCalibrationStage.Text = item.Stage;
                string temperature = item.TemperatureC.HasValue ? $"Temperature CPU {item.TemperatureC:0.0} \u00b0C" : "Preparation";
                string duty = item.DutyPercent.HasValue ? $" | Ventilation {item.DutyPercent:0} %" : "";
                TxtCalibrationDetails.Text = temperature + duty;
            });
            FanThermalProfileResult thermal = await FanThermalProfiler.RunAsync(
                _activeSession,
                document.Channels,
                thermalProgress,
                protectedToken);

            document = document with
            {
                AutomaticControlEnabled = true,
                ProfileSavedAt = DateTimeOffset.UtcNow,
                Channels = document.Channels.Select(channel => BuildCurveChannel(channel, thermal)).ToArray()
            };
            if (!FanProfileStore.Save(document, out saveError))
                throw new IOException("Courbes calculees mais non enregistrees : " + saveError);
            calibrationCompleted = true;
            completedProfile = document;
            controlWasRunning = false;
            _currentDocument = document;
            LoadCurveItems(document);

            PbCalibration.Value = 100;
            TxtCalibrationPercent.Text = "100 %";
            TxtCalibrationStage.Text = "Courbes de ventilation pr\u00eates";
            TxtCalibrationDetails.Text = "Plages stables, seuils de red\u00e9marrage et comportement thermique enregistr\u00e9s.";
            SetActionStatus("Analyse termin\u00e9e. Les courbes automatiques sont visibles et modifiables ci-dessous.", false);
            _main.Log("Ventilation : calibration et courbes thermiques terminees.");
            Helpers.UiSound.Success();

            try
            {
                FanRestorationOutcome restoration = FanSafetyRestore.RestoreAndClose(_activeSession, _activeWatchdog);
                _activeSession = null;
                _activeWatchdog = null;
                if (!restoration.Success)
                    throw new InvalidOperationException(restoration.Message);
                await FanRuntimeController.StartAsync(completedProfile, token);
                TxtCurveStatus.Text = "Auto Tweakly actif.";
            }
            catch (Exception ex)
            {
                completedProfile = completedProfile with { AutomaticControlEnabled = false };
                _currentDocument = completedProfile;
                string persistenceDetail = FanProfileStore.Save(completedProfile, out string persistenceError)
                    ? string.Empty
                    : " Etat inactif non enregistre : " + persistenceError;
                TxtCurveStatus.Text = "Courbes enregistrees, mais controle non demarre : " +
                                      ex.GetBaseException().Message + persistenceDetail;
                AppLog.Error("Ventilation : activation apres calibration", ex);
            }
        }
        catch (OperationCanceledException) when (_activeWatchdog?.FailureToken.IsCancellationRequested == true)
        {
            TxtCalibrationStage.Text = "Calibration interrompue";
            TxtCalibrationDetails.Text = "Le watchdog de securite ne repond plus.";
            SetActionStatus("Calibration arretee : watchdog de securite indisponible.", true);
            AppLog.Write("Ventilation : calibration arretee, watchdog indisponible.");
            Helpers.UiSound.Warn();
        }
        catch (OperationCanceledException)
        {
            TxtCalibrationStage.Text = "Calibration annulee";
            TxtCalibrationDetails.Text = "Le controle du BIOS a ete restaure.";
            SetActionStatus("Calibration annulee sans conserver de canal incomplet.", false);
            _main.Log("Ventilation : calibration annulee, controle BIOS restaure.");
        }
        catch (Exception ex)
        {
            TxtCalibrationStage.Text = "Calibration interrompue";
            TxtCalibrationDetails.Text = "Le controle du BIOS a ete restaure. " + ex.GetBaseException().Message;
            SetActionStatus("Calibration interrompue : " + ex.GetBaseException().Message, true);
            _main.Log("Ventilation : calibration interrompue - " + ex.GetBaseException().Message);
            Helpers.AppLog.Error("Ventilation : calibration", ex);
            Helpers.UiSound.Warn();
        }
        finally
        {
            FanRestorationOutcome restoration = FanSafetyRestore.RestoreAndClose(_activeSession, _activeWatchdog);
            watchdogProtection?.Dispose();
            _activeSession = null;
            _activeWatchdog = null;
            if (!restoration.Success)
            {
                TxtCalibrationDetails.Text = restoration.Message;
                SetActionStatus(restoration.Message, true);
                AppLog.Write("Ventilation : " + restoration.Message);
            }
            _calibrationCancellation.Dispose();
            _calibrationCancellation = null;
            CalibrationCard.Visibility = Visibility.Collapsed;
            BtnRefresh.IsEnabled = true;
            BtnCalibrate.Content = "CALIBRER LES VENTILATEURS";
            if (!calibrationCompleted)
            {
                _currentDocument = profileBeforeCalibration;
                LoadCurveItems(profileBeforeCalibration);
            }
            UpdateActions();
            if (restoration.Success &&
                !FanRuntimeController.IsRunning &&
                !calibrationCompleted &&
                controlWasRunning &&
                HasUsableCurves(profileBeforeCalibration))
            {
                try
                {
                    await FanRuntimeController.StartAsync(profileBeforeCalibration);
                    _currentDocument = profileBeforeCalibration;
                    TxtCurveStatus.Text = "Anciennes courbes remises en service apr\u00e8s l'interruption.";
                }
                catch (Exception ex)
                {
                    AppLog.Error("Ventilation : restauration du controle precedent", ex);
                }
            }
            if (restoration.Success && !FanRuntimeController.IsRunning)
                await StartTelemetryAsync();
            UpdateControlModeVisuals();
        }
    }

    private static SavedFanChannel BuildCurveChannel(
        SavedFanChannel channel,
        FanThermalProfileResult thermal)
    {
        if (channel.Calibration is not { IsValid: true } calibration ||
            !thermal.TrialsByChannel.TryGetValue(channel.Id, out IReadOnlyList<ThermalTrial>? trials))
            return channel;

        ThermalSource source = channel.Role is FanRole.Cpu or FanRole.Radiator
            ? ThermalSource.Cpu
            : ThermalSource.Mixed;
        FanCurvePlan plan = FanCurvePlanner.Build(new FanCurvePlanRequest
        {
            Source = source,
            Calibration = calibration,
            Trials = trials,
            IdleTemperatureC = thermal.IdleTemperatureC,
            ThermalLimitC = thermal.ThermalLimitC,
            SafetyMarginC = 12
        });
        return channel with
        {
            Source = source,
            IdleTemperatureC = thermal.IdleTemperatureC,
            ThermalTrials = trials,
            AutomaticCurve = plan.Points,
            Curve = plan.Points,
            CurveGeneratedAt = DateTimeOffset.UtcNow
        };
    }

    public void PrepareForAppShutdown()
    {
        try { _calibrationCancellation?.Cancel(); } catch { }
        try { _telemetryCancellation?.Cancel(); } catch { }
        FanRestorationOutcome restoration = FanSafetyRestore.RestoreAndClose(_activeSession, _activeWatchdog);
        if (!restoration.Success)
            AppLog.Write("Ventilation : " + restoration.Message);
        _activeSession = null;
        _activeWatchdog = null;
        try { _telemetrySession?.Dispose(); } catch { }
        FanRuntimeController.StopAndRestore();
    }

    private async void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        try { _calibrationCancellation?.Cancel(); } catch { }
        FanRestorationOutcome restoration = FanSafetyRestore.RestoreAndClose(_activeSession, _activeWatchdog);
        if (!restoration.Success)
            AppLog.Write("Ventilation : " + restoration.Message);
        _activeSession = null;
        _activeWatchdog = null;
        await StopTelemetryAsync();
    }

    private FanProfileDocument? BuildDocument()
    {
        if (_inventory is null || _channels.Count == 0) return null;
        FanProfileDocument? existing = FanProfileStore.Load();
        bool sameBoard = string.Equals(existing?.MotherboardName, _inventory.MotherboardName, StringComparison.OrdinalIgnoreCase);
        FanProfileDocument document = new()
        {
            MotherboardName = _inventory.MotherboardName,
            AutomaticControlEnabled = sameBoard && existing!.AutomaticControlEnabled,
            StartWithTweakly = sameBoard && existing!.StartWithTweakly,
            TemperatureHysteresisC = sameBoard ? existing!.TemperatureHysteresisC : 2,
            RampUpPercentPerSecond = sameBoard ? existing!.RampUpPercentPerSecond : 12,
            RampDownPercentPerSecond = sameBoard ? existing!.RampDownPercentPerSecond : 3,
            ProfileSavedAt = sameBoard ? existing!.ProfileSavedAt : null,
            Channels = _channels.Select(item =>
            {
                SavedFanChannel? previous = sameBoard
                    ? existing!.Channels.FirstOrDefault(x => string.Equals(x.Id, item.Channel.Id, StringComparison.Ordinal))
                    : null;
                return new SavedFanChannel
                {
                    Id = item.Channel.Id,
                    DisplayName = item.Channel.DisplayName,
                    Role = item.Role,
                    DriveMode = previous?.DriveMode ?? FanDriveMode.Unknown,
                    Calibration = previous?.Calibration,
                    Source = previous?.Source ?? ThermalSource.Mixed,
                    IdleTemperatureC = previous?.IdleTemperatureC,
                    ThermalTrials = previous?.ThermalTrials ?? [],
                    AutomaticCurve = previous?.AutomaticCurve ?? [],
                    Curve = previous?.Curve ?? [],
                    CurveGeneratedAt = previous?.CurveGeneratedAt
                };
            }).ToArray()
        };
        return FanProfileStore.RefreshAutomaticCurves(document);
    }

    private void UpdateActions()
    {
        bool hasChannels = _channels.Count > 0;
        bool allIdentified = hasChannels && _channels.All(item =>
            item.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator or FanRole.Pump);
        bool hasCalibratableFan = _channels.Any(item =>
            item.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator);

        BtnCalibrate.IsEnabled = BtnRefresh.IsEnabled && allIdentified && hasCalibratableFan;
        BtnCalibrate.Content = _curveItems.Count > 0 ? "RECALIBRER LES VENTILATEURS" : "CALIBRER LES VENTILATEURS";
        if (!hasChannels)
            SetActionStatus("Aucun canal utilisable n'est propose.", true);
        else if (!allIdentified)
            SetActionStatus("Carte mere non referencee : calibration bloquee pour eviter une mauvaise commande.", true);
        else if (!hasCalibratableFan)
            SetActionStatus("Tous les canaux sont identifies comme pompes : aucune calibration proposee.", true);
        else
            SetActionStatus("Canaux identifies automatiquement. La calibration peut commencer.", false);
    }

    private void SetBusy(bool busy)
    {
        BtnRefresh.IsEnabled = !busy;
        FanList.IsEnabled = !busy;
        CurveList.IsEnabled = !busy;
        BtnResetCurves.IsEnabled = !busy;
        BtnAutoMode.IsEnabled = !busy;
        BtnBiosMode.IsEnabled = !busy;
        BtnResponseSettings.IsEnabled = !busy;
        BtnSaveProfile.IsEnabled = !busy;
        BtnDeleteProfile.IsEnabled = !busy;
        ChkStartWithTweakly.IsEnabled = !busy;
        BtnCalibrate.IsEnabled = false;
    }
}
