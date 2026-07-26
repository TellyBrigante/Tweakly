using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GpuTuningLab.Core;

public sealed record ExternalWorkloadDefinition
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required WorkloadKind Kind { get; init; }
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required TimeSpan Timeout { get; init; }
    public required string ScorePattern { get; init; }
    public required string ScoreUnit { get; init; }
    public string? DurationPattern { get; init; }
    public string? WorkingDirectory { get; init; }
}

public sealed record WorkloadExecution(
    WorkloadResult Result,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

public interface IWorkloadRunner
{
    Task<WorkloadExecution> RunAsync(
        ExternalWorkloadDefinition definition,
        CancellationToken cancellationToken = default);
}

public sealed record WorkloadSuiteProgress(
    int WorkloadIndex,
    int WorkloadCount,
    string WorkloadName,
    bool Completed);

public sealed class ExternalWorkloadRunner : IWorkloadRunner
{
    private readonly IGpuContaminationMonitor? _contaminationMonitor;

    public ExternalWorkloadRunner(IGpuContaminationMonitor? contaminationMonitor = null)
    {
        _contaminationMonitor = contaminationMonitor;
    }

    public async Task<WorkloadExecution> RunAsync(
        ExternalWorkloadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!File.Exists(definition.ExecutablePath))
            throw new FileNotFoundException("Workload executable not found.", definition.ExecutablePath);

        var info = new ProcessStartInfo
        {
            FileName = definition.ExecutablePath,
            WorkingDirectory = definition.WorkingDirectory
                ?? Path.GetDirectoryName(Path.GetFullPath(definition.ExecutablePath))!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in definition.Arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        DateTimeOffset startedAt = DateTimeOffset.Now;
        var started = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException($"Unable to start workload '{definition.Name}'.");
        using var contaminationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<GpuContaminationResult>? contaminationTask = _contaminationMonitor?.ObserveAsync(
            process.Id,
            contaminationStop.Token);
        // Les flux doivent etre vides apres l'arret du processus, meme si l'appelant
        // annule. Le processus est tue par le chemin d'annulation ci-dessous.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(definition.Timeout);
        bool timedOut = false;
        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
            await ProcessSupport.WaitForExitAfterStopAsync(process).ConfigureAwait(false);
        }
        started.Stop();
        DateTimeOffset endedAt = DateTimeOffset.Now;
        await contaminationStop.CancelAsync().ConfigureAwait(false);
        GpuContaminationResult contamination = contaminationTask == null
            ? new GpuContaminationResult(true, [], "")
            : await contaminationTask.ConfigureAwait(false);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (cancelled)
            throw new OperationCanceledException(cancellationToken);

        string combinedOutput = stdout + Environment.NewLine + stderr;
        bool hasScore = TryParseScore(combinedOutput, definition.ScorePattern, out double score);
        bool validScore = hasScore && score > 0 && double.IsFinite(score);
        TimeSpan measuredDuration = TryParseDuration(
            combinedOutput,
            definition.DurationPattern,
            out TimeSpan parsedDuration)
            ? parsedDuration
            : started.Elapsed;
        bool uncontaminated = contamination.MonitoringSucceeded && contamination.BusyProcesses.Count == 0;
        bool completed = !timedOut && process.ExitCode == 0 && validScore && uncontaminated;
        return new WorkloadExecution(
            new WorkloadResult
            {
                Name = definition.Name,
                Version = definition.Version,
                Kind = definition.Kind,
                Score = hasScore ? score : 0,
                ScoreUnit = definition.ScoreUnit,
                Duration = measuredDuration,
                Completed = completed,
                FailureReason = completed
                    ? ""
                    : timedOut
                        ? $"Workload exceeded {definition.Timeout.TotalSeconds:0} s."
                        : process.ExitCode != 0
                            ? ExitFailure(process.ExitCode, stderr)
                            : !validScore
                                ? "Workload output did not contain a positive finite score."
                                : GpuContaminationFormatter.Failure(contamination)
            },
            process.ExitCode,
            stdout,
            stderr,
            timedOut,
            startedAt,
            endedAt);
    }

    private static string ExitFailure(int exitCode, string standardError)
    {
        string detail = standardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return string.IsNullOrWhiteSpace(detail)
            ? $"Workload exited with code {exitCode}."
            : $"Workload exited with code {exitCode}: {detail}";
    }

    public static bool TryParseScore(string output, string pattern, out double score)
    {
        score = 0;
        var match = Regex.Match(output, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups.Count < 2) return false;
        string normalized = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out score);
    }

    public static bool TryParseDuration(string output, string? pattern, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var match = Regex.Match(output, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups.Count < 2) return false;
        string normalized = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || seconds <= 0
            || !double.IsFinite(seconds)
            || seconds > TimeSpan.MaxValue.TotalSeconds)
            return false;
        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }
}

public sealed class GpuTestOrchestrator
{
    private readonly IGpuTelemetrySource _telemetry;
    private readonly IWorkloadRunner _workload;
    private readonly IStabilityEvidenceCollector _evidence;
    private readonly EvaluationPolicy _policy;

    public GpuTestOrchestrator(
        IGpuTelemetrySource telemetry,
        IWorkloadRunner workload,
        IStabilityEvidenceCollector evidence,
        EvaluationPolicy policy)
    {
        _telemetry = telemetry;
        _workload = workload;
        _evidence = evidence;
        _policy = policy;
    }

    public async Task<TestRun> RunAsync(
        GpuTuningProfile profile,
        ExternalWorkloadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset startedAt = DateTimeOffset.Now;
        using var telemetryStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<TelemetryCapture> telemetryTask = _telemetry.CaptureAsync(
            definition.Timeout + TimeSpan.FromSeconds(5),
            _policy.SamplingIntervalMs,
            telemetryStop.Token);

        WorkloadExecution execution;
        TelemetryCapture? capture = null;
        Exception? executionFailure = null;
        try
        {
            execution = await _workload.RunAsync(definition, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            executionFailure = ex;
            throw;
        }
        finally
        {
            await telemetryStop.CancelAsync().ConfigureAwait(false);
            try
            {
                capture = await telemetryTask.ConfigureAwait(false);
            }
            catch when (executionFailure != null)
            {
                // Preserve the workload failure while still observing the telemetry task.
            }
        }

        capture ??= await telemetryTask.ConfigureAwait(false);
        GpuTelemetrySample[] measuredSamples = capture.Samples
            .Where(sample => sample.Timestamp >= execution.StartedAt && sample.Timestamp <= execution.EndedAt)
            .ToArray();
        if (measuredSamples.Length < 2)
            throw new InvalidOperationException(
                $"Only {measuredSamples.Length} telemetry sample(s) matched the workload execution window.");
        DateTimeOffset endedAt = DateTimeOffset.Now;
        string processName = Path.GetFileNameWithoutExtension(definition.ExecutablePath);
        var stabilityEvents = new List<StabilityEvent>();
        try
        {
            stabilityEvents.AddRange(await _evidence.CollectAsync(
                startedAt - TimeSpan.FromSeconds(2),
                endedAt + TimeSpan.FromSeconds(2),
                processName,
                capture.Identity,
                cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stabilityEvents.Add(new StabilityEvent
            {
                Timestamp = endedAt,
                Kind = StabilityEventKind.TelemetryGap,
                Evidence = "Windows stability evidence collection failed: " + ex.Message
            });
        }
        if (execution.TimedOut)
        {
            stabilityEvents.Add(new StabilityEvent
            {
                Timestamp = endedAt,
                Kind = StabilityEventKind.ManualStop,
                Evidence = $"{definition.Name} exceeded {definition.Timeout.TotalSeconds:0} s."
            });
        }

        return new TestRun
        {
            StartedAt = startedAt,
            Identity = capture.Identity,
            Profile = profile,
            Samples = measuredSamples,
            Workloads = [execution.Result],
            WorkloadWindows =
            [
                new WorkloadTelemetryWindow
                {
                    Name = definition.Name,
                    Kind = definition.Kind,
                    StartedAt = execution.StartedAt,
                    EndedAt = execution.EndedAt,
                    SampleCount = measuredSamples.Length
                }
            ],
            StabilityEvents = stabilityEvents,
            Notes = string.Join(" | ", capture.Warnings)
        };
    }

    public async Task<TestRun> RunSuiteAsync(
        GpuTuningProfile profile,
        IReadOnlyList<ExternalWorkloadDefinition> definitions,
        CancellationToken cancellationToken = default,
        IProgress<WorkloadSuiteProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0)
            throw new ArgumentException("At least one workload is required.", nameof(definitions));

        var runs = new List<TestRun>(definitions.Count);
        for (int index = 0; index < definitions.Count; index++)
        {
            ExternalWorkloadDefinition definition = definitions[index];
            progress?.Report(new WorkloadSuiteProgress(index, definitions.Count, definition.Name, false));
            TestRun run = await RunAsync(profile, definition, cancellationToken).ConfigureAwait(false);
            runs.Add(run);
            progress?.Report(new WorkloadSuiteProgress(index + 1, definitions.Count, definition.Name, true));
            if (run.Workloads.Any(static workload => !workload.Completed)) break;
        }

        GpuIdentity identity = runs[0].Identity;
        if (runs.Any(run => !GpuIdentityCompatibility.SameMeasurementEnvironment(identity, run.Identity)))
            throw new InvalidOperationException("GPU identity, VBIOS or driver changed during the workload suite.");

        return new TestRun
        {
            StartedAt = runs.Min(static run => run.StartedAt),
            Identity = identity,
            Profile = profile,
            Samples = runs.SelectMany(static run => run.Samples)
                .OrderBy(static sample => sample.Timestamp)
                .ToArray(),
            Workloads = runs.SelectMany(static run => run.Workloads).ToArray(),
            WorkloadWindows = runs.SelectMany(static run => run.WorkloadWindows)
                .OrderBy(static window => window.StartedAt)
                .ToArray(),
            StabilityEvents = runs.SelectMany(static run => run.StabilityEvents)
                .Distinct()
                .OrderBy(static item => item.Timestamp)
                .ToArray(),
            Notes = string.Join(" | ", runs.Select(static run => run.Notes)
                .Where(static note => !string.IsNullOrWhiteSpace(note))
                .Distinct())
        };
    }

}
