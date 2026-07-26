using FanControl.Core;

using System.IO;

namespace Optimisation_Tool.Helpers;

public sealed record FanRuntimeUpdate(
    FanControlSnapshot Snapshot,
    IReadOnlyDictionary<string, double> AppliedDuties);

public static class FanRuntimeController
{
    private sealed record RuntimeConfiguration(
        SavedFanChannel[] Channels,
        double TemperatureHysteresisC,
        double RampUpPercentPerSecond,
        double RampDownPercentPerSecond);

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim TransitionGate = new(1, 1);
    private static FanHardwareSession? _session;
    private static FanSafetyWatchdogClient? _watchdog;
    private static CancellationTokenSource? _cancellation;
    private static Task<FanRestorationOutcome>? _loopTask;
    private static RuntimeConfiguration? _configuration;

    public static event EventHandler<FanRuntimeUpdate>? Updated;
    public static event EventHandler<string>? Stopped;

    public static bool IsRunning
    {
        get
        {
            lock (Sync) return _session is not null && _loopTask is { IsCompleted: false };
        }
    }

    public static async Task TryStartSavedProfileAsync()
    {
        try
        {
            FanProfileDocument? document = FanProfileStore.Load();
            if (document is not { StartWithTweakly: true } || !HasUsableCurves(document))
                return;

            document = FanProfileStore.RefreshAutomaticCurves(document);
            if (!FanProfileStore.Save(document, out string saveError))
                throw new IOException("Le profil actualise ne peut pas etre enregistre : " + saveError);
            await StartAsync(document).ConfigureAwait(false);
            AppLog.Write("Ventilation : profil automatique active au demarrage.");
        }
        catch (Exception ex)
        {
            StopAndRestore();
            AppLog.Error("Ventilation : activation au demarrage ignoree, controle BIOS conserve", ex);
        }
    }

    public static async Task StartAsync(FanProfileDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        RuntimeConfiguration configuration = BuildConfiguration(document);
        Task startupTask;
        await TransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FanRestorationOutcome previous = await StopCoreAsync().ConfigureAwait(false);
            if (!previous.Success)
                throw new InvalidOperationException(previous.Message);
            FanHardwareSession session = await Task.Run(
                FanHardwareSession.Open,
                cancellationToken).ConfigureAwait(false);
            FanSafetyWatchdogClient watchdog;
            try
            {
                watchdog = FanSafetyWatchdogClient.Arm(
                    configuration.Channels.Select(static channel => channel.Id));
            }
            catch
            {
                FanSafetyRestore.RestoreAndClose(session, null);
                throw;
            }
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                watchdog.FailureToken);
            var startup = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<FanRestorationOutcome> loop;
            lock (Sync)
            {
                _session = session;
                _watchdog = watchdog;
                _cancellation = linkedCancellation;
                _configuration = configuration;
                loop = RunLoopAsync(session, watchdog, startup, linkedCancellation.Token);
                _loopTask = loop;
            }
            startupTask = startup.Task;
        }
        finally
        {
            TransitionGate.Release();
        }

        // La première écriture matérielle confirme réellement le démarrage, mais elle ne doit
        // pas retenir le verrou de transition : une fermeture peut ainsi annuler proprement
        // un pilote bloqué au lieu de figer l'application.
        await startupTask.ConfigureAwait(false);
    }

    public static bool UpdateProfile(FanProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RuntimeConfiguration configuration = BuildConfiguration(document);
        lock (Sync)
        {
            if (_session is null || _loopTask is not { IsCompleted: false })
                return false;
            _configuration = configuration;
            return true;
        }
    }

    public static async Task<FanRestorationOutcome> StopAsync()
    {
        await TransitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            TransitionGate.Release();
        }
    }

    private static async Task<FanRestorationOutcome> StopCoreAsync()
    {
        CancellationTokenSource? cancellation;
        Task<FanRestorationOutcome>? loop;
        lock (Sync)
        {
            cancellation = _cancellation;
            loop = _loopTask;
        }

        try { cancellation?.Cancel(); } catch { }
        if (loop is not null)
        {
            return await loop.ConfigureAwait(false);
        }

        return new(true, "Aucun controle logiciel actif.");
    }

    public static FanRestorationOutcome StopAndRestore()
    {
        TransitionGate.Wait();
        try
        {
            FanHardwareSession? session;
            FanSafetyWatchdogClient? watchdog;
            CancellationTokenSource? cancellation;
            lock (Sync)
            {
                session = _session;
                watchdog = _watchdog;
                cancellation = _cancellation;
                _session = null;
                _watchdog = null;
                _cancellation = null;
                _loopTask = null;
                _configuration = null;
            }

            try { cancellation?.Cancel(); } catch { }
            FanRestorationOutcome outcome = FanSafetyRestore.RestoreAndClose(session, watchdog);
            cancellation?.Dispose();
            if (!outcome.Success)
                AppLog.Write("Ventilation : " + outcome.Message);
            return outcome;
        }
        finally
        {
            TransitionGate.Release();
        }
    }

    private static async Task<FanRestorationOutcome> RunLoopAsync(
        FanHardwareSession session,
        FanSafetyWatchdogClient watchdog,
        TaskCompletionSource startup,
        CancellationToken cancellationToken)
    {
        RuntimeConfiguration initialConfiguration = GetConfiguration(session);
        var currentDuty = initialConfiguration.Channels.ToDictionary(
            static channel => channel.Id,
            static channel => Math.Max(
                channel.Calibration!.MinimumStableDutyPercent,
                channel.Calibration.RestartDutyPercent),
            StringComparer.Ordinal);
        var controlTemperature = initialConfiguration.Channels.ToDictionary(
            static channel => channel.Id,
            static _ => double.NaN,
            StringComparer.Ordinal);
        DateTimeOffset previous = DateTimeOffset.UtcNow;
        bool firstTick = true;
        string stopReason = "Le controle du BIOS a ete restaure.";
        FanRestorationOutcome restoration = new(false, "Retour au BIOS non execute.");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!cancellationToken.IsCancellationRequested)
            {
                FanControlSnapshot snapshot = session.ReadControlSnapshot();
                CpuTemperature.PublishExternalReading(snapshot.CpuTemperatureC);
                RuntimeConfiguration configuration = GetConfiguration(session);
                SavedFanChannel[] channels = configuration.Channels;
                var activeIds = channels.Select(static channel => channel.Id).ToHashSet(StringComparer.Ordinal);
                foreach (string staleId in currentDuty.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                {
                    currentDuty.Remove(staleId);
                    controlTemperature.Remove(staleId);
                }
                foreach (SavedFanChannel channel in channels)
                {
                    double floor = Math.Max(
                        channel.Calibration!.MinimumStableDutyPercent,
                        channel.Calibration.RestartDutyPercent);
                    currentDuty.TryAdd(channel.Id, floor);
                    controlTemperature.TryAdd(channel.Id, double.NaN);
                }
                if (firstTick)
                {
                    foreach (SavedFanChannel channel in channels)
                    {
                        FanTelemetrySample? telemetry = snapshot.Fans.FirstOrDefault(fan =>
                            string.Equals(fan.ChannelId, channel.Id, StringComparison.Ordinal));
                        if (telemetry is not null && double.IsFinite(telemetry.ControlPercent) &&
                            telemetry.ControlPercent > 0 && telemetry.ControlPercent <= 100)
                            currentDuty[channel.Id] = telemetry.ControlPercent;
                    }
                    firstTick = false;
                }
                var requested = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (SavedFanChannel channel in channels)
                {
                    double measuredTemperature = SelectTemperature(snapshot, channel.Source) ?? double.NaN;
                    double effectiveTemperature = FanControlLoop.ApplyTemperatureHysteresis(
                        measuredTemperature,
                        controlTemperature[channel.Id],
                        configuration.TemperatureHysteresisC);
                    controlTemperature[channel.Id] = effectiveTemperature;
                    FanControlDecision decision = FanControlLoop.Decide(new FanControlTick
                    {
                        Now = snapshot.CapturedAt,
                        LastTelemetryAt = snapshot.CapturedAt,
                        TemperatureC = effectiveTemperature,
                        CurrentDutyPercent = currentDuty[channel.Id],
                        ElapsedSeconds = Math.Max(0, (snapshot.CapturedAt - previous).TotalSeconds),
                        Curve = channel.Curve,
                        EmergencyTemperatureC = channel.Curve[^1].TemperatureC,
                        RampUpPercentPerSecond = configuration.RampUpPercentPerSecond,
                        RampDownPercentPerSecond = configuration.RampDownPercentPerSecond
                    });
                    if (decision.Action == FanControlActionKind.RestoreHardwareDefault)
                        throw new InvalidOperationException(decision.Reason);

                    double floor = Math.Max(
                        channel.Calibration!.MinimumStableDutyPercent,
                        channel.Calibration.RestartDutyPercent);
                    requested[channel.Id] = Math.Clamp(Math.Max(decision.DutyPercent, floor), floor, 100);
                }

                cancellationToken.ThrowIfCancellationRequested();
                session.ApplyDuties(requested);
                watchdog.Pulse();
                foreach ((string channelId, double duty) in requested)
                    currentDuty[channelId] = duty;
                previous = snapshot.CapturedAt;
                startup.TrySetResult();
                RaiseUpdated(new FanRuntimeUpdate(snapshot, requested));

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) when (watchdog.FailureToken.IsCancellationRequested)
        {
            startup.TrySetException(
                new InvalidOperationException("Le watchdog de securite ne repond plus."));
            stopReason = "Controle automatique arrete : le watchdog de securite ne repond plus.";
            AppLog.Write("Ventilation : " + stopReason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startup.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            startup.TrySetException(ex);
            stopReason = "Contr\u00f4le automatique arr\u00eat\u00e9 : " + ex.GetBaseException().Message;
            AppLog.Error("Ventilation : controle automatique interrompu", ex);
        }
        finally
        {
            startup.TrySetException(
                new InvalidOperationException("Le controle automatique s'est arrete avant sa premiere application."));
            bool ownsCleanup;
            lock (Sync)
            {
                ownsCleanup = ReferenceEquals(_session, session);
                if (ownsCleanup)
                {
                    _session = null;
                    _watchdog = null;
                    _cancellation?.Dispose();
                    _cancellation = null;
                    _loopTask = null;
                    _configuration = null;
                }
            }

            if (ownsCleanup)
            {
                restoration = FanSafetyRestore.RestoreAndClose(session, watchdog);
                if (!restoration.Success)
                    stopReason = restoration.Message;
                RaiseStopped(stopReason);
            }
            else
            {
                restoration = new(true, "Le controle du BIOS a deja ete restaure.");
            }
        }

        return restoration;
    }

    private static void RaiseUpdated(FanRuntimeUpdate update)
    {
        foreach (EventHandler<FanRuntimeUpdate> handler in Updated?.GetInvocationList()
                     .Cast<EventHandler<FanRuntimeUpdate>>() ?? [])
        {
            try
            {
                handler(null, update);
            }
            catch (Exception ex)
            {
                AppLog.Error("Ventilation : mise a jour de l'interface ignoree", ex);
            }
        }
    }

    private static void RaiseStopped(string reason)
    {
        foreach (EventHandler<string> handler in Stopped?.GetInvocationList()
                     .Cast<EventHandler<string>>() ?? [])
        {
            try
            {
                handler(null, reason);
            }
            catch (Exception ex)
            {
                AppLog.Error("Ventilation : notification d'arret ignoree", ex);
            }
        }
    }

    private static double? SelectTemperature(FanControlSnapshot snapshot, ThermalSource source) => source switch
    {
        ThermalSource.Cpu => snapshot.CpuTemperatureC,
        ThermalSource.Gpu => snapshot.GpuTemperatureC,
        ThermalSource.Mixed => snapshot.HottestTemperatureC,
        _ => snapshot.HottestTemperatureC
    };

    private static bool HasUsableCurves(FanProfileDocument document) => document.Channels.Any(static channel =>
        channel.Calibration is { IsValid: true } && channel.Curve.Count >= 2 &&
        channel.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator);

    private static RuntimeConfiguration BuildConfiguration(FanProfileDocument document)
    {
        SavedFanChannel[] channels = document.Channels
            .Where(static channel => channel.Calibration is { IsValid: true } && channel.Curve.Count >= 2 &&
                                     channel.Role is FanRole.Cpu or FanRole.Chassis or FanRole.Radiator)
            .ToArray();
        if (channels.Length == 0)
            throw new InvalidOperationException("Aucune courbe valide n'est disponible.");

        return new RuntimeConfiguration(
            channels,
            Math.Clamp(document.TemperatureHysteresisC, 0, 5),
            Math.Clamp(document.RampUpPercentPerSecond, 2, 30),
            Math.Clamp(document.RampDownPercentPerSecond, 1, 15));
    }

    private static RuntimeConfiguration GetConfiguration(FanHardwareSession session)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_session, session) || _configuration is null)
                throw new OperationCanceledException("Le controle de ventilation a ete arrete.");
            return _configuration;
        }
    }
}
