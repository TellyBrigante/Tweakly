using System.ComponentModel;
using System.Runtime.InteropServices;
using FanControl.Core;
using LibreHardwareMonitor.Hardware;

namespace Optimisation_Tool.Helpers;

public sealed record FanCalibrationProgress(
    string ChannelId,
    string Stage,
    int Percent,
    double? DutyPercent,
    double? Rpm,
    double? HottestTemperatureC);

public sealed record FanCalibrationExecution(
    string ChannelId,
    FanCalibrationResult Result,
    IReadOnlyList<FanResponseSample> ResponseSamples,
    IReadOnlyList<FanRestartSample> RestartSamples);

public sealed record FanTelemetrySample(string ChannelId, double Rpm, double ControlPercent);

public sealed record FanHardwareRestoreReport(
    int RequestedControls,
    int MatchedControls,
    int RestoredControls,
    IReadOnlyList<string> Errors)
{
    public bool Success => RequestedControls > 0 &&
                           MatchedControls == RequestedControls &&
                           RestoredControls == RequestedControls &&
                           Errors.Count == 0;
}

public sealed record FanControlSnapshot(
    DateTimeOffset CapturedAt,
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    IReadOnlyList<FanTelemetrySample> Fans)
{
    public double? HottestTemperatureC => CpuTemperatureC.HasValue && GpuTemperatureC.HasValue
        ? Math.Max(CpuTemperatureC.Value, GpuTemperatureC.Value)
        : CpuTemperatureC ?? GpuTemperatureC;
}

public sealed class FanHardwareSession : IDisposable
{
    private const double CalibrationTemperatureLimitC = 70;
    private const int MaximumStabilitySamples = 30;
    private const int RequiredStableWindows = 2;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    private readonly Computer _computer;
    private readonly IDisposable _hardwareLease;
    private readonly Dictionary<string, ChannelHandle> _channels = new(StringComparer.Ordinal);
    private readonly HashSet<IControl> _touchedControls = [];
    private readonly object _sync = new();
    private bool _disposed;

    private sealed record ChannelHandle(IHardware Hardware, ISensor Tachometer, ISensor ControlSensor)
    {
        public IControl Control => ControlSensor.Control
            ?? throw new InvalidOperationException("Fan control is no longer writable.");
    }

    private sealed record FanDutyObservation(
        FanResponseSample Sample,
        double ElapsedSeconds,
        double SpreadPercent,
        double MinimumRpm,
        double MaximumRpm);

    private FanHardwareSession(Computer computer, IDisposable hardwareLease)
    {
        _computer = computer;
        _hardwareLease = hardwareLease;
        BuildChannelMap();
    }

    public static FanHardwareSession Open()
    {
        IDisposable hardwareLease = HardwareMonitorAccess.Enter();
        CpuTemperature.SuspendForExclusiveHardwareAccess();

        var computer = new Computer
        {
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };
        try
        {
            using IDisposable pawnIoLease = BundledFileTrust.OpenVerifiedLease(PathLayout.PawnIoLib);
            if (!SetDllDirectory(PathLayout.DataDrv))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO directory is unavailable.");
            computer.Open();
            return new FanHardwareSession(computer, hardwareLease);
        }
        catch
        {
            try { computer.Close(); } catch { }
            hardwareLease.Dispose();
            throw;
        }
    }

    public async Task<FanCalibrationExecution> CalibrateAsync(
        SavedFanChannel channel,
        IProgress<FanCalibrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Role is FanRole.Unknown or FanRole.Pump or FanRole.Gpu)
            throw new InvalidOperationException("Ce canal ne peut pas \u00eatre calibr\u00e9 automatiquement.");
        if (!_channels.TryGetValue(channel.Id, out ChannelHandle? handle))
            throw new InvalidOperationException("Le canal s\u00e9lectionn\u00e9 n'est plus disponible.");

        var responseSamples = new List<FanResponseSample>();
        var restartSamples = new List<FanRestartSample>();
        try
        {
            RefreshAll();
            EnsureSafeTemperature();
            EnsureRotating(handle);

            progress?.Report(new(channel.Id, "Mesure du regime maximal", 4, 100, null, ReadHottestTemperature()));
            FanDutyObservation maximumObservation = await ObserveAtDutyAsync(handle, 100, cancellationToken);
            FanResponseSample maximum = maximumObservation.Sample;
            responseSamples.Add(maximum);
            if (!maximum.Stable)
                throw new InvalidOperationException(
                    $"La vitesse ne s'est pas stabilis\u00e9e \u00e0 100 % apr\u00e8s {maximumObservation.ElapsedSeconds:0} s " +
                    $"({maximumObservation.MinimumRpm:0} \u00e0 {maximumObservation.MaximumRpm:0} tr/min)." );

            double minimumCommand = Math.Max(handle.Control.MinSoftwareValue, 10);
            double[] descendingDuties = Enumerable.Range(0, 9)
                .Select(step => 90 - (step * 10.0))
                .Where(duty => duty >= minimumCommand)
                .ToArray();

            for (int i = 0; i < descendingDuties.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double duty = descendingDuties[i];
                int percent = 10 + (int)Math.Round((i + 1) * 50.0 / Math.Max(1, descendingDuties.Length));
                progress?.Report(new(channel.Id, $"Recherche du regime minimal - {duty:0} %", percent, duty, null, ReadHottestTemperature()));
                FanResponseSample sample = (await ObserveAtDutyAsync(handle, duty, cancellationToken)).Sample;
                if (!sample.Stable)
                    break;
                responseSamples.Add(sample);
            }

            FanResponseSample[] stable = responseSamples.Where(x => x.Stable).OrderBy(x => x.DutyPercent).ToArray();
            if (stable.Length < 3)
                throw new InvalidOperationException("Pas assez de points de vitesse stables ont \u00e9t\u00e9 mesur\u00e9s.");

            double safetyMargin = channel.DriveMode == FanDriveMode.Pwm ? 5 : 10;
            double restartCandidate = Math.Clamp(stable[0].DutyPercent + safetyMargin, minimumCommand, 90);
            bool fanCanStop = await CanStopBrieflyAsync(handle, cancellationToken);
            if (!fanCanStop)
            {
                restartSamples.Add(new FanRestartSample
                {
                    DutyPercent = restartCandidate,
                    Attempts = 3,
                    SuccessfulStarts = 3
                });
            }
            else
            {
                for (double duty = restartCandidate; duty <= Math.Min(80, restartCandidate + 30); duty += 5)
                {
                    int successes = 0;
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int percent = 65 + (int)Math.Round(((duty - restartCandidate) / 30.0) * 25);
                        progress?.Report(new(channel.Id, $"Verification du redemarrage - essai {attempt}/3", percent, duty, null, ReadHottestTemperature()));
                        if (await RestartOnceAsync(handle, duty, cancellationToken)) successes++;
                    }

                    restartSamples.Add(new FanRestartSample
                    {
                        DutyPercent = duty,
                        Attempts = 3,
                        SuccessfulStarts = successes
                    });
                    if (successes == 3) break;
                }
            }

            FanCalibrationResult result = FanCalibrationAnalyzer.Analyze(
                channel.DriveMode,
                responseSamples,
                restartSamples);
            if (!result.IsValid)
                throw new InvalidOperationException(result.FailureReason);

            progress?.Report(new(channel.Id, "Calibration validee", 100, result.MinimumStableDutyPercent,
                result.MaximumObservedRpm, ReadHottestTemperature()));
            return new FanCalibrationExecution(channel.Id, result, responseSamples, restartSamples);
        }
        finally
        {
            RestoreDefault(handle);
        }
    }

    public IReadOnlyList<FanTelemetrySample> ReadTelemetry()
        => ReadControlSnapshot().Fans;

    public FanControlSnapshot ReadControlSnapshot()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshAll();
            IReadOnlyList<FanTelemetrySample> fans = _channels.Select(pair => new FanTelemetrySample(
                    pair.Key,
                    pair.Value.Tachometer.Value ?? 0,
                    pair.Value.ControlSensor.Value ?? 0))
                .ToArray();
            return new FanControlSnapshot(
                DateTimeOffset.UtcNow,
                ReadTemperature(HardwareType.Cpu),
                ReadGpuTemperature(),
                fans);
        }
    }

    public void ApplyDuties(IReadOnlyDictionary<string, double> dutyByChannel)
    {
        ArgumentNullException.ThrowIfNull(dutyByChannel);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requested = new List<(ChannelHandle Handle, double DutyPercent)>(dutyByChannel.Count);
            foreach ((string channelId, double dutyPercent) in dutyByChannel)
            {
                if (!_channels.TryGetValue(channelId, out ChannelHandle? handle))
                    throw new InvalidOperationException($"Le canal {channelId} n'est plus disponible.");
                if (!double.IsFinite(dutyPercent))
                    throw new InvalidOperationException($"La commande du canal {channelId} est invalide.");
                requested.Add((handle, dutyPercent));
            }
            foreach ((ChannelHandle handle, double dutyPercent) in requested)
                SetDutyForControl(handle, dutyPercent);
        }
    }

    public void RestoreAllDefaults()
    {
        lock (_sync)
        {
            var failures = new List<Exception>();
            foreach (IControl control in _touchedControls.ToArray())
            {
                try
                {
                    control.SetDefault();
                    _touchedControls.Remove(control);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count > 0)
                throw new AggregateException(
                    $"Le retour au BIOS a echoue pour {failures.Count} canal(aux).",
                    failures);
        }
    }

    public void Dispose()
    {
        Exception? restoreError = null;
        lock (_sync)
        {
            if (_disposed) return;
            try { RestoreAllDefaults(); }
            catch (Exception ex) { restoreError = ex; }
            _disposed = true;
            try { _computer.Close(); } catch { }
            _hardwareLease.Dispose();
        }

        if (restoreError is not null)
            throw new InvalidOperationException("Le controle du BIOS n'a pas pu etre confirme.", restoreError);
    }

    public static FanHardwareRestoreReport RestoreControlsToDefault(IReadOnlyCollection<string> controlIds)
    {
        ArgumentNullException.ThrowIfNull(controlIds);
        string[] requested = controlIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
            return new(0, 0, 0, ["Aucun canal de ventilation n'a ete transmis au watchdog."]);

        var errors = new List<string>();
        int matched = 0;
        int restored = 0;
        var remaining = requested.ToHashSet(StringComparer.Ordinal);
        var computer = new Computer
        {
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };

        try
        {
            using IDisposable pawnIoLease = BundledFileTrust.OpenVerifiedLease(PathLayout.PawnIoLib);
            if (!SetDllDirectory(PathLayout.DataDrv))
                return new(requested.Length, 0, 0, ["Le dossier PawnIO est indisponible."]);

            computer.Open();
            foreach (IHardware hardware in Enumerate(computer.Hardware))
            {
                hardware.Update();
                foreach (ISensor sensor in hardware.Sensors.Where(static sensor =>
                             sensor.SensorType == SensorType.Control && sensor.Control is not null))
                {
                    string id = sensor.Identifier.ToString();
                    if (!remaining.Remove(id))
                        continue;

                    matched++;
                    try
                    {
                        sensor.Control!.SetDefault();
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{id} : {ex.GetBaseException().Message}");
                    }
                }
            }

            foreach (string missing in remaining)
                errors.Add($"{missing} : canal introuvable.");
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetBaseException().Message);
        }
        finally
        {
            try { computer.Close(); } catch { }
        }

        return new(requested.Length, matched, restored, errors);
    }

    private async Task<FanDutyObservation> ObserveAtDutyAsync(
        ChannelHandle handle,
        double dutyPercent,
        CancellationToken cancellationToken)
    {
        SetDuty(handle, dutyPercent);
        var rpm = new List<double>(MaximumStabilitySamples);
        double hottest = 0;
        int stableWindows = 0;
        FanRpmStabilityResult stability = new(false, 0, double.PositiveInfinity, double.PositiveInfinity);
        for (int i = 0; i < MaximumStabilitySamples; i++)
        {
            await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            RefreshAll();
            EnsureSafeTemperature();
            hottest = Math.Max(hottest, ReadHottestTemperature() ?? 0);
            if (handle.Tachometer.Value is float value && value >= 0)
                rpm.Add(value);

            stability = FanRpmStabilityAnalyzer.AnalyzeWindow(rpm);
            stableWindows = stability.Stable ? stableWindows + 1 : 0;
            if (stableWindows >= RequiredStableWindows)
            {
                double elapsed = (i + 1) * SampleInterval.TotalSeconds;
                LogDutyObservation(handle, dutyPercent, rpm, stability, elapsed, true);
                return CreateDutyObservation(dutyPercent, hottest, rpm, stability, elapsed, true);
            }
        }

        double timeout = MaximumStabilitySamples * SampleInterval.TotalSeconds;
        LogDutyObservation(handle, dutyPercent, rpm, stability, timeout, false);
        return CreateDutyObservation(dutyPercent, hottest, rpm, stability, timeout, false);
    }

    private static FanDutyObservation CreateDutyObservation(
        double dutyPercent,
        double hottest,
        IReadOnlyList<double> rpm,
        FanRpmStabilityResult stability,
        double elapsedSeconds,
        bool stable)
    {
        double representative = stability.RepresentativeRpm > 0
            ? stability.RepresentativeRpm
            : rpm.LastOrDefault();
        double minimum = rpm.Count == 0 ? 0 : rpm.Min();
        double maximum = rpm.Count == 0 ? 0 : rpm.Max();
        var sample = new FanResponseSample
        {
            DutyPercent = dutyPercent,
            Rpm = representative,
            TemperatureC = hottest,
            Stable = stable
        };
        return new FanDutyObservation(
            sample,
            elapsedSeconds,
            double.IsFinite(stability.SpreadRatio) ? stability.SpreadRatio * 100 : 100,
            minimum,
            maximum);
    }

    private static void LogDutyObservation(
        ChannelHandle handle,
        double dutyPercent,
        IReadOnlyList<double> rpm,
        FanRpmStabilityResult stability,
        double elapsedSeconds,
        bool stable)
    {
        string readings = string.Join(", ", rpm.TakeLast(10).Select(value => value.ToString("0")));
        string spread = double.IsFinite(stability.SpreadRatio)
            ? $"{stability.SpreadRatio * 100:0.0} %"
            : "indisponible";
        AppLog.Write(
            $"Ventilation : {handle.Tachometer.Name} | commande {dutyPercent:0} % | " +
            $"{(stable ? "stable" : "non stable")} en {elapsedSeconds:0.0} s | dispersion {spread} | " +
            $"RPM [{readings}]");
    }

    private async Task<bool> CanStopBrieflyAsync(ChannelHandle handle, CancellationToken cancellationToken)
    {
        SetDuty(handle, Math.Max(0, handle.Control.MinSoftwareValue));
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            RefreshAll();
            EnsureSafeTemperature();
            if ((handle.Tachometer.Value ?? 0) < FanSafetyPolicy.MinimumReadableRpm)
                return true;
        }

        return false;
    }

    private async Task<bool> RestartOnceAsync(
        ChannelHandle handle,
        double dutyPercent,
        CancellationToken cancellationToken)
    {
        SetDuty(handle, Math.Max(0, handle.Control.MinSoftwareValue));
        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        RefreshAll();
        EnsureSafeTemperature();

        SetDuty(handle, dutyPercent);
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            RefreshAll();
            EnsureSafeTemperature();
            if ((handle.Tachometer.Value ?? 0) >= FanSafetyPolicy.MinimumReadableRpm)
            {
                SetDuty(handle, 100);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        SetDuty(handle, 100);
        return false;
    }

    private void SetDuty(ChannelHandle handle, double dutyPercent)
    {
        EnsureSafeTemperature();
        SetDutyForControl(handle, dutyPercent);
    }

    private void SetDutyForControl(ChannelHandle handle, double dutyPercent)
    {
        IControl control = handle.Control;
        double bounded = Math.Clamp(dutyPercent, control.MinSoftwareValue, control.MaxSoftwareValue);
        control.SetSoftware((float)bounded);
        _touchedControls.Add(control);
    }

    private void RestoreDefault(ChannelHandle handle)
    {
        handle.Control.SetDefault();
        _touchedControls.Remove(handle.Control);
    }

    private void EnsureSafeTemperature()
    {
        double? hottest = ReadHottestTemperature();
        if (hottest is null)
            throw new InvalidOperationException("Les temp\u00e9ratures CPU et GPU sont indisponibles.");
        if (hottest > CalibrationTemperatureLimitC)
            throw new InvalidOperationException($"Calibration arr\u00eat\u00e9e \u00e0 {hottest:0.0} \u00b0C.");
    }

    private static void EnsureRotating(ChannelHandle handle)
    {
        if ((handle.Tachometer.Value ?? 0) < FanSafetyPolicy.MinimumReadableRpm)
            throw new InvalidOperationException("Le ventilateur ne tourne plus.");
    }

    private double? ReadHottestTemperature()
    {
        double? cpu = ReadTemperature(HardwareType.Cpu);
        double? gpu = ReadGpuTemperature();
        return cpu.HasValue && gpu.HasValue ? Math.Max(cpu.Value, gpu.Value) : cpu ?? gpu;
    }

    private double? ReadGpuTemperature()
    {
        double? hottest = null;
        foreach (HardwareType type in new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel })
        {
            double? value = ReadTemperature(type);
            if (value.HasValue)
                hottest = hottest.HasValue ? Math.Max(hottest.Value, value.Value) : value.Value;
        }
        return hottest;
    }

    private double? ReadTemperature(HardwareType hardwareType)
    {
        double? hottest = null;
        foreach (IHardware hardware in Enumerate(_computer.Hardware))
        {
            if (hardware.HardwareType != hardwareType)
                continue;
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value ||
                    value <= 0 || value >= 130 || sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                    continue;
                hottest = hottest.HasValue ? Math.Max(hottest.Value, value) : value;
            }
        }
        return hottest;
    }

    private void RefreshAll()
    {
        foreach (IHardware hardware in Enumerate(_computer.Hardware))
            hardware.Update();
    }

    private void BuildChannelMap()
    {
        foreach (IHardware hardware in Enumerate(_computer.Hardware)
                     .Where(x => x.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO))
        {
            hardware.Update();
            Dictionary<int, ISensor> tachometers = hardware.Sensors
                .Where(x => x.SensorType == SensorType.Fan && (x.Value ?? 0) >= FanSafetyPolicy.MinimumReadableRpm)
                .ToDictionary(x => x.Index);
            foreach (ISensor controlSensor in hardware.Sensors.Where(x => x.SensorType == SensorType.Control && x.Control is not null))
            {
                if (tachometers.TryGetValue(controlSensor.Index, out ISensor? tachometer))
                    _channels[controlSensor.Identifier.ToString()] = new ChannelHandle(hardware, tachometer, controlSensor);
            }
        }
    }

    private static IEnumerable<IHardware> Enumerate(IEnumerable<IHardware> roots)
    {
        foreach (IHardware hardware in roots)
        {
            yield return hardware;
            foreach (IHardware child in Enumerate(hardware.SubHardware))
                yield return child;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
