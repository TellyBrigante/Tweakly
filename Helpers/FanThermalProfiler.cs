using System.Diagnostics;
using FanControl.Core;

namespace Optimisation_Tool.Helpers;

public sealed record FanThermalProgress(
    string Stage,
    int Percent,
    double? TemperatureC,
    double? DutyPercent);

public sealed record FanThermalProfileResult(
    double IdleTemperatureC,
    double ThermalLimitC,
    IReadOnlyDictionary<string, IReadOnlyList<ThermalTrial>> TrialsByChannel);

public static class FanThermalProfiler
{
    private const double ThermalLimitC = 90;
    private const double EmergencyTemperatureC = 88;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    private sealed record TrialDefinition(
        WorkloadLevel Level,
        double CpuLoad,
        double DutyPercent,
        TimeSpan Duration,
        string Label);

    private sealed record TrialRunResult(
        ThermalTrial Trial,
        IReadOnlyDictionary<string, double> AverageRpmByChannel);

    public static async Task<FanThermalProfileResult> RunAsync(
        FanHardwareSession session,
        IReadOnlyList<SavedFanChannel> channels,
        IProgress<FanThermalProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(channels);

        SavedFanChannel[] usable = channels
            .Where(static channel => channel.Calibration is { IsValid: true } &&
                                     channel.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator)
            .ToArray();
        if (usable.Length == 0)
            throw new InvalidOperationException("Aucun ventilateur calibre n'est disponible pour le test thermique.");

        var trials = usable.ToDictionary(
            static channel => channel.Id,
            static _ => new List<ThermalTrial>(),
            StringComparer.Ordinal);

        try
        {
            session.RestoreAllDefaults();
            progress?.Report(new("Mesure de la temp\u00e9rature au repos", 1, null, null));
            double idle = await MeasureIdleAsync(session, progress, cancellationToken).ConfigureAwait(false);

            const double moderateLoad = 0.45;
            const double heavyLoad = 0.85;
            TimeSpan moderateWarmup = TimeSpan.FromSeconds(20);
            TimeSpan heavyWarmup = TimeSpan.FromSeconds(30);
            double commonFloor = usable.Min(channel =>
                Math.Max(channel.Calibration!.MinimumStableDutyPercent, channel.Calibration.RestartDutyPercent));
            TrialDefinition[] definitions = BuildDefinitions(commonFloor);
            double totalSeconds = definitions.Sum(static item => item.Duration.TotalSeconds) +
                                  moderateWarmup.TotalSeconds + heavyWarmup.TotalSeconds;
            double completedSeconds = 0;
            bool skipModerateLowerDuties = false;
            bool skipHeavyLowerDuties = false;
            WorkloadLevel? preparedLevel = null;
            foreach (TrialDefinition definition in definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (preparedLevel != definition.Level)
                {
                    TimeSpan warmup = definition.Level == WorkloadLevel.Moderate ? moderateWarmup : heavyWarmup;
                    double load = definition.Level == WorkloadLevel.Moderate ? moderateLoad : heavyLoad;
                    session.ApplyDuties(usable.ToDictionary(
                        static channel => channel.Id,
                        static _ => 100.0,
                        StringComparer.Ordinal));
                    await WarmUpAsync(
                        session,
                        definition.Level,
                        load,
                        warmup,
                        completedSeconds,
                        totalSeconds,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    completedSeconds += warmup.TotalSeconds;
                    preparedLevel = definition.Level;
                }

                bool skip = definition.Level switch
                {
                    WorkloadLevel.Moderate => skipModerateLowerDuties,
                    WorkloadLevel.Heavy => skipHeavyLowerDuties,
                    _ => false
                };
                if (skip)
                {
                    completedSeconds += definition.Duration.TotalSeconds;
                    continue;
                }

                IReadOnlyDictionary<string, double> duties = usable.ToDictionary(
                    static channel => channel.Id,
                    channel => Math.Max(
                        definition.DutyPercent,
                        Math.Max(channel.Calibration!.MinimumStableDutyPercent, channel.Calibration.RestartDutyPercent)),
                    StringComparer.Ordinal);
                session.ApplyDuties(duties);

                TrialRunResult run = await RunTrialAsync(
                    session,
                    definition,
                    completedSeconds,
                    totalSeconds,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                foreach (SavedFanChannel channel in usable)
                {
                    trials[channel.Id].Add(run.Trial with
                    {
                        DutyPercent = duties[channel.Id],
                        ObservedRpm = run.AverageRpmByChannel.TryGetValue(channel.Id, out double rpm) ? rpm : null
                    });
                }
                completedSeconds += definition.Duration.TotalSeconds;

                if (run.Trial.Throttled)
                {
                    if (definition.Level == WorkloadLevel.Moderate)
                        skipModerateLowerDuties = true;
                    else
                        skipHeavyLowerDuties = true;
                    int progressPercent = 10 + (int)Math.Round(completedSeconds * 89.0 / totalSeconds);
                    await CoolDownAsync(
                        session,
                        usable,
                        idle,
                        Math.Clamp(progressPercent, 10, 99),
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            progress?.Report(new("Courbes automatiques calcul\u00e9es", 100, null, null));
            return new FanThermalProfileResult(
                idle,
                ThermalLimitC,
                trials.ToDictionary(
                    static pair => pair.Key,
                    static pair => (IReadOnlyList<ThermalTrial>)pair.Value.ToArray(),
                    StringComparer.Ordinal));
        }
        finally
        {
            session.RestoreAllDefaults();
        }
    }

    private static TrialDefinition[] BuildDefinitions(double floor) =>
    [
        .. new[] { 70d, 55d }.Where(duty => duty >= floor).Append(floor).Distinct().OrderByDescending(static duty => duty)
            .Select(duty => new TrialDefinition(
                WorkloadLevel.Moderate, 0.45, duty, TimeSpan.FromSeconds(18),
                $"Charge CPU mod\u00e9r\u00e9e - ventilation {duty:0} %")),
        .. new[] { 100d, 85d, 70d, 55d }.Where(duty => duty >= floor).Append(floor).Distinct().OrderByDescending(static duty => duty)
            .Select(duty => new TrialDefinition(
                WorkloadLevel.Heavy, 0.85, duty, TimeSpan.FromSeconds(24),
                $"Charge CPU soutenue - ventilation {duty:0} %"))
    ];

    private static async Task WarmUpAsync(
        FanHardwareSession session,
        WorkloadLevel level,
        double cpuLoad,
        TimeSpan duration,
        double completedSeconds,
        double totalSeconds,
        IProgress<FanThermalProgress>? progress,
        CancellationToken cancellationToken)
    {
        string loadName = level == WorkloadLevel.Moderate ? "moderee" : "soutenue";
        using var workload = new ControlledCpuLoad(cpuLoad);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            FanControlSnapshot snapshot = session.ReadControlSnapshot();
            double temperature = snapshot.CpuTemperatureC
                ?? throw new InvalidOperationException("La temperature CPU n'est pas disponible pendant la mise en temperature.");
            if (temperature >= EmergencyTemperatureC)
                throw new InvalidOperationException($"Limite de securite atteinte pendant la mise en temperature ({temperature:0.0} \u00b0C)." );

            double elapsed = completedSeconds + Math.Min(stopwatch.Elapsed.TotalSeconds, duration.TotalSeconds);
            int percent = 10 + (int)Math.Round(elapsed * 89.0 / totalSeconds);
            progress?.Report(new(
                $"Stabilisation avant charge {loadName}",
                Math.Clamp(percent, 10, 99),
                temperature,
                100));
        }
    }

    private static async Task<double> MeasureIdleAsync(
        FanHardwareSession session,
        IProgress<FanThermalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var readings = new List<double>();
        const int samples = 12;
        for (int i = 0; i < samples; i++)
        {
            await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            FanControlSnapshot snapshot = session.ReadControlSnapshot();
            double temperature = snapshot.CpuTemperatureC
                ?? throw new InvalidOperationException("La temperature CPU n'est pas disponible.");
            readings.Add(temperature);
            progress?.Report(new(
                "Mesure de la temp\u00e9rature au repos",
                Math.Clamp((int)Math.Round((i + 1) * 10.0 / samples), 1, 10),
                temperature,
                null));
        }

        return readings.TakeLast(6).Average();
    }

    private static async Task<TrialRunResult> RunTrialAsync(
        FanHardwareSession session,
        TrialDefinition definition,
        double completedSeconds,
        double totalSeconds,
        IProgress<FanThermalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temperatures = new List<double>();
        var rpmByChannel = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        bool safetyLimitReached = false;
        using var workload = new ControlledCpuLoad(definition.CpuLoad);
        var stopwatch = Stopwatch.StartNew();
        TimeSpan maximumDuration = definition.Duration + TimeSpan.FromSeconds(15);
        bool temperatureSettled = false;
        while (stopwatch.Elapsed < definition.Duration ||
               (!temperatureSettled && stopwatch.Elapsed < maximumDuration))
        {
            await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            FanControlSnapshot snapshot = session.ReadControlSnapshot();
            double temperature = snapshot.CpuTemperatureC
                ?? throw new InvalidOperationException("La temperature CPU n'est pas disponible pendant le test.");
            temperatures.Add(temperature);
            temperatureSettled = FanTemperatureStabilityAnalyzer.IsSettled(temperatures);
            foreach (FanTelemetrySample fan in snapshot.Fans)
            {
                if (!rpmByChannel.TryGetValue(fan.ChannelId, out List<double>? readings))
                {
                    readings = [];
                    rpmByChannel[fan.ChannelId] = readings;
                }
                readings.Add(fan.Rpm);
            }

            double elapsed = completedSeconds + Math.Min(stopwatch.Elapsed.TotalSeconds, definition.Duration.TotalSeconds);
            int percent = 10 + (int)Math.Round(elapsed * 89.0 / totalSeconds);
            progress?.Report(new(definition.Label, Math.Clamp(percent, 10, 99), temperature, definition.DutyPercent));

            if (temperature >= EmergencyTemperatureC)
            {
                session.ApplyDuties(session.ReadTelemetry().ToDictionary(
                    static fan => fan.ChannelId,
                    static _ => 100.0,
                    StringComparer.Ordinal));
                safetyLimitReached = true;
                progress?.Report(new(
                    $"Limite de s\u00e9curit\u00e9 atteinte \u00e0 {temperature:0.0} \u00b0C - refroidissement",
                    Math.Clamp(percent, 10, 99),
                    temperature,
                    100));
                break;
            }
        }

        if (temperatures.Count == 0)
            throw new InvalidOperationException("Aucune temperature n'a ete mesuree pendant le test thermique.");

        double maximum = temperatures.TakeLast(Math.Min(20, temperatures.Count)).Max();
        var trial = new ThermalTrial
        {
            Workload = definition.Level,
            DutyPercent = definition.DutyPercent,
            MaximumTemperatureC = maximum,
            Stable = !safetyLimitReached && temperatureSettled,
            Throttled = safetyLimitReached
        };
        return new TrialRunResult(
            trial,
            rpmByChannel.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.TakeLast(10).Average(),
                StringComparer.Ordinal));
    }

    private static async Task CoolDownAsync(
        FanHardwareSession session,
        IReadOnlyList<SavedFanChannel> channels,
        double idleTemperatureC,
        int progressPercent,
        IProgress<FanThermalProgress>? progress,
        CancellationToken cancellationToken)
    {
        session.ApplyDuties(channels.ToDictionary(
            static channel => channel.Id,
            static _ => 100.0,
            StringComparer.Ordinal));
        double target = Math.Min(75, idleTemperatureC + 12);
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            FanControlSnapshot snapshot = session.ReadControlSnapshot();
            double temperature = snapshot.CpuTemperatureC ?? 100;
            progress?.Report(new("Refroidissement de s\u00e9curit\u00e9", progressPercent, temperature, 100));
            if (temperature <= target)
                return;
        }

        throw new InvalidOperationException(
            $"Le processeur reste au-dessus de {target:0.0} \u00b0C apr\u00e8s 60 s de refroidissement.");
    }

    private sealed class ControlledCpuLoad : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Thread[] _workers;

        public ControlledCpuLoad(double loadRatio)
        {
            int activeMilliseconds = Math.Clamp((int)Math.Round(loadRatio * 100), 1, 99);
            _workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount))
                .Select(index =>
                {
                    var thread = new Thread(() => RunWorker(activeMilliseconds, _cancellation.Token))
                    {
                        IsBackground = true,
                        Name = $"Tweakly Fan Thermal Load {index}",
                        Priority = ThreadPriority.BelowNormal
                    };
                    thread.Start();
                    return thread;
                })
                .ToArray();
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            foreach (Thread worker in _workers)
                worker.Join(TimeSpan.FromSeconds(1));
            _cancellation.Dispose();
        }

        private static void RunWorker(int activeMilliseconds, CancellationToken cancellationToken)
        {
            int idleMilliseconds = 100 - activeMilliseconds;
            var stopwatch = new Stopwatch();
            while (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Restart();
                while (stopwatch.ElapsedMilliseconds < activeMilliseconds && !cancellationToken.IsCancellationRequested)
                    Thread.SpinWait(4000);
                if (idleMilliseconds > 0)
                    cancellationToken.WaitHandle.WaitOne(idleMilliseconds);
            }
        }
    }
}
