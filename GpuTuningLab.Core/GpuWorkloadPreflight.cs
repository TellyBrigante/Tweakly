using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GpuTuningLab.Core;

public sealed record ActiveGpuProcess(
    int ProcessId,
    string Name,
    double? ComputePercent,
    double? MemoryPercent,
    double? EncoderPercent = null,
    double? DecoderPercent = null);

public sealed record GpuWorkloadPreflightResult(
    bool Allowed,
    IReadOnlyList<ActiveGpuProcess> BusyProcesses,
    string Reason);

public static class GpuWorkloadPreflight
{
    public const double BusyThresholdPercent = 5;
    public const double VideoBusyThresholdPercent = 0;

    public static async Task<GpuWorkloadPreflightResult> CheckAsync(
        IReadOnlySet<int>? allowedProcessIds = null,
        CancellationToken cancellationToken = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var info = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem
        };
        info.ArgumentList.Add("pmon");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("1");
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add("um");
        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ProcessSupport.WaitForExitAfterStopAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            timedOut = true;
        }
        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (timedOut)
            return new(false, [], "GPU preflight exceeded 10 s.");
        if (process.ExitCode != 0)
            return new(false, [], $"GPU preflight failed: {error.Trim()}");

        ActiveGpuProcess[] busy = SelectContaminatingProcesses(
            Parse(output).Select(ResolveProcessName),
            allowedProcessIds,
            Environment.ProcessId);
        if (busy.Length == 0) return new(true, [], "GPU is idle enough for a controlled test.");
        string details = string.Join(", ", busy.Select(item =>
            $"{item.Name} (PID {item.ProcessId}, compute {Percent(item.ComputePercent)}, memory {Percent(item.MemoryPercent)}, encode {Percent(item.EncoderPercent)}, decode {Percent(item.DecoderPercent)})"));
        return new(false, busy, "GPU workload already active: " + details);
    }

    public static ActiveGpuProcess[] SelectContaminatingProcesses(
        IEnumerable<ActiveGpuProcess> processes,
        IReadOnlySet<int>? allowedProcessIds,
        int hostProcessId)
        => processes
            .Where(item => item.ProcessId != hostProcessId)
            .Where(item => allowedProcessIds == null || !allowedProcessIds.Contains(item.ProcessId))
            .Where(IsContaminating)
            .OrderByDescending(Peak)
            .ToArray();

    public static IReadOnlyList<ActiveGpuProcess> Parse(string output)
    {
        var results = new List<ActiveGpuProcess>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 12 || !int.TryParse(fields[1], out int processId)) continue;
            results.Add(new ActiveGpuProcess(
                processId,
                fields[11],
                Number(fields[3]),
                Number(fields[4]),
                Number(fields[5]),
                Number(fields[6])));
        }
        return results;
    }

    public static bool IsContaminating(ActiveGpuProcess process)
        => process.ComputePercent > BusyThresholdPercent
           || process.MemoryPercent > BusyThresholdPercent
           || process.EncoderPercent > VideoBusyThresholdPercent
           || process.DecoderPercent > VideoBusyThresholdPercent;

    private static double Peak(ActiveGpuProcess process)
        => new[]
        {
            process.ComputePercent ?? 0,
            process.MemoryPercent ?? 0,
            process.EncoderPercent ?? 0,
            process.DecoderPercent ?? 0
        }.Max();

    private static double? Number(string value)
        => value == "-"
            ? null
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? number
                : null;

    private static ActiveGpuProcess ResolveProcessName(ActiveGpuProcess item)
    {
        try
        {
            using Process process = Process.GetProcessById(item.ProcessId);
            return item with { Name = process.ProcessName + ".exe" };
        }
        catch (ArgumentException)
        {
            return item with { Name = SanitizeReportedProcessName(item.Name) };
        }
        catch (InvalidOperationException)
        {
            return item with { Name = SanitizeReportedProcessName(item.Name) };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return item with { Name = SanitizeReportedProcessName(item.Name) };
        }
    }

    public static string SanitizeReportedProcessName(string value)
    {
        string sanitized = new(value
            .Take(16)
            .TakeWhile(static character => character is >= '!' and <= '~')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown-process" : sanitized;
    }

    private static string Percent(double? value) => value.HasValue ? $"{value.Value:0} %" : "N/A %";
}

public sealed record GpuContaminationResult(
    bool MonitoringSucceeded,
    IReadOnlyList<ActiveGpuProcess> BusyProcesses,
    string FailureReason,
    IReadOnlyList<GpuContaminationEvidence>? Evidence = null);

public sealed record GpuContaminationEvidence(
    ActiveGpuProcess Process,
    int TotalObservations,
    int MaximumConsecutiveObservations);

public interface IGpuContaminationMonitor
{
    Task<GpuContaminationResult> ObserveAsync(
        int workloadProcessId,
        CancellationToken cancellationToken);
}

public sealed class NvidiaGpuContaminationMonitor : IGpuContaminationMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<GpuContaminationResult> ObserveAsync(
        int workloadProcessId,
        CancellationToken cancellationToken)
    {
        var observed = new Dictionary<int, ContaminationObservation>();
        var allowed = new HashSet<int> { workloadProcessId };
        string failure = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                GpuWorkloadPreflightResult result = await GpuWorkloadPreflight.CheckAsync(
                    allowed,
                    cancellationToken).ConfigureAwait(false);
                var seen = new HashSet<int>();
                foreach (ActiveGpuProcess process in result.BusyProcesses)
                {
                    seen.Add(process.ProcessId);
                    if (!observed.TryGetValue(process.ProcessId, out ContaminationObservation? observation))
                    {
                        observation = new ContaminationObservation(process);
                        observed.Add(process.ProcessId, observation);
                    }
                    observation.Add(process);
                }
                foreach ((int processId, ContaminationObservation observation) in observed)
                    if (!seen.Contains(processId)) observation.BreakSequence();

                if (!result.Allowed && result.BusyProcesses.Count == 0)
                {
                    failure = result.Reason;
                    break;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        GpuContaminationEvidence[] evidence = observed.Values
            .Where(static observation => observation.IsSignificant)
            .Select(static observation => new GpuContaminationEvidence(
                observation.PeakProcess,
                observation.TotalObservations,
                observation.MaximumConsecutiveObservations))
            .OrderByDescending(static item => Peak(item.Process))
            .ToArray();
        return new GpuContaminationResult(
            string.IsNullOrWhiteSpace(failure),
            evidence.Select(static item => item.Process).ToArray(),
            failure,
            evidence);
    }

    private static double Peak(ActiveGpuProcess process)
        => new[]
        {
            process.ComputePercent ?? 0,
            process.MemoryPercent ?? 0,
            process.EncoderPercent ?? 0,
            process.DecoderPercent ?? 0
        }.Max();

    private sealed class ContaminationObservation
    {
        public ContaminationObservation(ActiveGpuProcess process)
        {
            PeakProcess = process;
        }

        public ActiveGpuProcess PeakProcess { get; private set; }
        public int TotalObservations { get; private set; }
        public int ConsecutiveObservations { get; private set; }
        public int MaximumConsecutiveObservations { get; private set; }
        public bool IsSignificant => GpuContaminationPolicy.IsSignificant(
            PeakProcess,
            TotalObservations,
            MaximumConsecutiveObservations);

        public void Add(ActiveGpuProcess process)
        {
            TotalObservations++;
            ConsecutiveObservations++;
            MaximumConsecutiveObservations = Math.Max(MaximumConsecutiveObservations, ConsecutiveObservations);
            if (Peak(process) > Peak(PeakProcess)) PeakProcess = process;
        }

        public void BreakSequence() => ConsecutiveObservations = 0;
    }
}

public static class GpuContaminationPolicy
{
    public const double ImmediateComputeOrMemoryPercent = 20;
    public const int RequiredConsecutiveObservations = 2;
    public const int RequiredTotalObservations = 3;
    public const int DesktopRequiredConsecutiveObservations = 5;

    private static readonly HashSet<string> WindowsDesktopProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe",
        "explorer.exe",
        "SearchHost.exe",
        "ShellHost.exe",
        "StartMenuExperienceHost.exe",
        "TextInputHost.exe"
    };

    public static bool IsSignificant(
        ActiveGpuProcess process,
        int totalObservations,
        int maximumConsecutiveObservations)
    {
        double computeOrMemoryPeak = Math.Max(process.ComputePercent ?? 0, process.MemoryPercent ?? 0);
        bool videoEngineActive = process.EncoderPercent > 0 || process.DecoderPercent > 0;
        if (WindowsDesktopProcesses.Contains(process.Name))
        {
            return videoEngineActive
                   || computeOrMemoryPeak >= ImmediateComputeOrMemoryPercent
                   || maximumConsecutiveObservations >= DesktopRequiredConsecutiveObservations;
        }

        return videoEngineActive
               || computeOrMemoryPeak >= ImmediateComputeOrMemoryPercent
               || maximumConsecutiveObservations >= RequiredConsecutiveObservations
               || totalObservations >= RequiredTotalObservations;
    }
}

public static class GpuContaminationFormatter
{
    public static string Failure(GpuContaminationResult contamination)
    {
        if (!contamination.MonitoringSucceeded)
            return $"GPU contamination monitoring failed: {contamination.FailureReason}";
        var evidenceByPid = (contamination.Evidence ?? [])
            .ToDictionary(static item => item.Process.ProcessId);
        string processes = string.Join(", ", contamination.BusyProcesses.Select(process =>
        {
            string observations = evidenceByPid.TryGetValue(process.ProcessId, out GpuContaminationEvidence? evidence)
                ? $", observations {evidence.TotalObservations}, consecutive {evidence.MaximumConsecutiveObservations}"
                : "";
            return $"{process.Name} (PID {process.ProcessId}, compute {Percent(process.ComputePercent)}, memory {Percent(process.MemoryPercent)}, encode {Percent(process.EncoderPercent)}, decode {Percent(process.DecoderPercent)}{observations})";
        }));
        return "Concurrent GPU workload detected during measurement: " + processes;
    }

    private static string Percent(double? value) => value.HasValue ? $"{value.Value:0} %" : "N/A %";
}
