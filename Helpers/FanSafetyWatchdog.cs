using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Optimisation_Tool.Helpers;

public sealed record FanRestorationOutcome(bool Success, string Message);

public sealed class FanSafetyWatchdogClient : IDisposable
{
    internal const string Argument = "--fan-safety-watchdog";
    internal const string SmokeArgument = "--fan-safety-watchdog-smoke";
    private const string EventPrefix = "Local\\Tweakly.FanSafety.";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly EventWaitHandle _disarmEvent;
    private readonly EventWaitHandle _restoreEvent;
    private readonly EventWaitHandle _heartbeatEvent;
    private readonly CancellationTokenSource _watchdogFailure = new();
    private int _finished;

    private FanSafetyWatchdogClient(
        Process process,
        EventWaitHandle disarmEvent,
        EventWaitHandle restoreEvent,
        EventWaitHandle heartbeatEvent)
    {
        _process = process;
        _disarmEvent = disarmEvent;
        _restoreEvent = restoreEvent;
        _heartbeatEvent = heartbeatEvent;
        _process.EnableRaisingEvents = true;
        _process.Exited += WatchdogProcess_Exited;
    }

    public CancellationToken FailureToken => _watchdogFailure.Token;

    public static FanSafetyWatchdogClient Arm(IEnumerable<string> controlIds)
    {
        string[] controls = controlIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (controls.Length == 0)
            throw new InvalidOperationException("Aucun canal ne peut etre protege par le watchdog.");

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("L'executable Tweakly est introuvable pour le watchdog.");
        if (!File.Exists(executable))
            throw new InvalidOperationException("L'executable Tweakly est introuvable pour le watchdog.");

        string token = Guid.NewGuid().ToString("N");
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "Ready"));
        var disarmEvent = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "Disarm"));
        var restoreEvent = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "Restore"));
        var heartbeatEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(token, "Heartbeat"));
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(controls)));

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(Argument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(token);
        startInfo.ArgumentList.Add(payload);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Le watchdog n'a pas pu etre lance.");
            if (!readyEvent.WaitOne(ReadyTimeout) || process.HasExited)
                throw new InvalidOperationException("Le watchdog n'a pas confirme sa surveillance.");
            var client = new FanSafetyWatchdogClient(process, disarmEvent, restoreEvent, heartbeatEvent);
            if (process.HasExited)
            {
                client.Dispose();
                throw new InvalidOperationException("Le watchdog s'est arrete juste apres son armement.");
            }
            client.Pulse();
            return client;
        }
        catch
        {
            try { disarmEvent.Set(); } catch { }
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            process?.Dispose();
            disarmEvent.Dispose();
            restoreEvent.Dispose();
            heartbeatEvent.Dispose();
            throw;
        }
    }

    public void Pulse()
    {
        if (Volatile.Read(ref _finished) != 0)
            return;
        try
        {
            _heartbeatEvent.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool Disarm()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return true;
        try
        {
            _disarmEvent.Set();
            return _process.WaitForExit((int)ExitTimeout.TotalMilliseconds) && _process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Ventilation : arret du watchdog impossible", ex);
            return false;
        }
    }

    public bool RestoreNow()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return false;
        try
        {
            _restoreEvent.Set();
            return _process.WaitForExit((int)ExitTimeout.TotalMilliseconds) && _process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Ventilation : restauration du watchdog impossible", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _process.Exited -= WatchdogProcess_Exited;
        _watchdogFailure.Dispose();
        _disarmEvent.Dispose();
        _restoreEvent.Dispose();
        _heartbeatEvent.Dispose();
        _process.Dispose();
    }

    private void WatchdogProcess_Exited(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _finished) == 0)
        {
            try { _watchdogFailure.Cancel(); } catch { }
        }
    }

    internal static bool IsInvocation(IReadOnlyList<string> args)
        => args.Count > 0 && string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSmokeInvocation(IReadOnlyList<string> args)
        => args.Count == 1 && string.Equals(args[0], SmokeArgument, StringComparison.OrdinalIgnoreCase);

    internal static int RunSmokeTest()
    {
        try
        {
            using FanSafetyWatchdogClient watchdog = Arm(["/tweakly/watchdog/smoke/control/0"]);
            return watchdog.Disarm() ? 0 : 30;
        }
        catch (Exception ex)
        {
            AppLog.Error("Ventilation : smoke test du watchdog", ex);
            return 31;
        }
    }

    internal static int RunWatchdog(IReadOnlyList<string> args)
    {
        if (args.Count != 4 ||
            !int.TryParse(args[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int parentPid) ||
            parentPid <= 0 || string.IsNullOrWhiteSpace(args[2]))
            return 10;

        string token = args[2];
        string[] controls;
        try
        {
            controls = JsonSerializer.Deserialize<string[]>(
                           Encoding.UTF8.GetString(Convert.FromBase64String(args[3])))
                       ?? [];
        }
        catch
        {
            return 11;
        }

        try
        {
            using Process parent = Process.GetProcessById(parentPid);
            string? parentPath = parent.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(Environment.ProcessPath) ||
                !string.Equals(Path.GetFullPath(parentPath), Path.GetFullPath(Environment.ProcessPath),
                    StringComparison.OrdinalIgnoreCase))
                return 12;

            using EventWaitHandle readyEvent = EventWaitHandle.OpenExisting(EventName(token, "Ready"));
            using EventWaitHandle disarmEvent = EventWaitHandle.OpenExisting(EventName(token, "Disarm"));
            using EventWaitHandle restoreEvent = EventWaitHandle.OpenExisting(EventName(token, "Restore"));
            using EventWaitHandle heartbeatEvent = EventWaitHandle.OpenExisting(EventName(token, "Heartbeat"));
            readyEvent.Set();
            long lastHeartbeat = Stopwatch.GetTimestamp();

            while (true)
            {
                if (restoreEvent.WaitOne(0))
                    return RestoreAndLog(controls, "demande directe");
                if (disarmEvent.WaitOne(0))
                    return 0;
                if (parent.HasExited)
                    return RestoreAndLog(controls, "arret inattendu de Tweakly");
                if (Stopwatch.GetElapsedTime(lastHeartbeat) > HeartbeatTimeout)
                    return RestoreAndLog(controls, "absence de signal de vie de Tweakly");

                int signal = WaitHandle.WaitAny([restoreEvent, disarmEvent, heartbeatEvent], 250);
                if (signal == 0)
                    return RestoreAndLog(controls, "demande directe");
                if (signal == 1)
                    return 0;
                if (signal == 2)
                    lastHeartbeat = Stopwatch.GetTimestamp();
            }
        }
        catch (ArgumentException)
        {
            // Le parent a disparu avant l'armement complet : aucune commande ventilateur
            // n'a encore ete appliquee, donc aucune restauration n'est necessaire.
            return 13;
        }
        catch (Exception ex)
        {
            WriteWatchdogLog("Watchdog interrompu : " + ex.GetBaseException().Message);
            return 14;
        }
    }

    private static int RestoreAndLog(string[] controls, string reason)
    {
        FanHardwareRestoreReport report = FanHardwareSession.RestoreControlsToDefault(controls);
        string details = report.Errors.Count == 0 ? "aucune erreur" : string.Join(" | ", report.Errors);
        WriteWatchdogLog(
            $"Restauration ({reason}) : {report.RestoredControls}/{report.RequestedControls} canal(aux), {details}.");
        return report.Success ? 0 : 20;
    }

    private static void WriteWatchdogLog(string message)
    {
        try
        {
            Directory.CreateDirectory(PathLayout.Config);
            File.AppendAllText(
                PathLayout.FanWatchdogLog,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static string EventName(string token, string suffix) => EventPrefix + token + "." + suffix;
}

public static class FanSafetyRestore
{
    public static FanRestorationOutcome RestoreAndClose(
        FanHardwareSession? session,
        FanSafetyWatchdogClient? watchdog)
    {
        if (session is null)
        {
            if (watchdog is null)
                return new(true, "Aucun controle logiciel actif.");
            bool fallbackOnly = watchdog.RestoreNow();
            watchdog.Dispose();
            return fallbackOnly
                ? new(true, "Le watchdog a restaure le controle du BIOS.")
                : new(false, "Le watchdog n'a pas confirme le retour au BIOS.");
        }

        Exception? localError = null;
        bool localRestored = false;
        try
        {
            session.RestoreAllDefaults();
            localRestored = true;
        }
        catch (Exception ex)
        {
            localError = ex;
            AppLog.Error("Ventilation : premiere restauration BIOS refusee", ex);
        }

        try
        {
            session.Dispose();
            localRestored = true;
        }
        catch (Exception ex)
        {
            localError = ex;
            AppLog.Error("Ventilation : seconde restauration BIOS refusee", ex);
        }

        if (localRestored)
        {
            bool disarmed = watchdog?.Disarm() ?? true;
            watchdog?.Dispose();
            if (!disarmed)
                AppLog.Write("Ventilation : controle BIOS restaure, mais watchdog encore actif jusqu'a la fermeture.");
            return new(true, "Le controle du BIOS a ete restaure.");
        }

        bool fallback = watchdog?.RestoreNow() ?? false;
        watchdog?.Dispose();
        if (fallback)
            return new(true, "Le watchdog a restaure le controle du BIOS.");

        string detail = localError?.GetBaseException().Message ?? "erreur inconnue";
        return new(false, "Retour au BIOS non confirme : " + detail);
    }
}
