using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GpuTuningLab.Core;

public enum VramValidationStatus
{
    Incomplete,
    Passed,
    MemoryError,
    RuntimeError
}

public sealed record VramValidationSummary
{
    public required VramValidationStatus Status { get; init; }
    public string Device { get; init; } = "Unknown GPU";
    public int MemoryMiB { get; init; }
    public int Iterations { get; init; }
    public double WrittenGiB { get; init; }
    public double CheckedGiB { get; init; }
    public double WriteGiBPerSecond { get; init; }
    public double CheckGiBPerSecond { get; init; }
    public string FailureReason { get; init; } = "";
}

public static partial class MemtestVulkanOutputParser
{
    public static VramValidationSummary Parse(string output)
    {
        output ??= "";
        Match device = DeviceRegex().Match(output);
        Match[] iterations = IterationRegex().Matches(output).Cast<Match>().ToArray();
        Match last = iterations.LastOrDefault()!;
        bool memoryError = output.Contains("Error found.", StringComparison.OrdinalIgnoreCase);
        bool runtimeError = output.Contains("Runtime error:", StringComparison.OrdinalIgnoreCase)
                            || output.Contains("early exit during init", StringComparison.OrdinalIgnoreCase)
                            || output.Contains("testing failed", StringComparison.OrdinalIgnoreCase);
        bool passed = output.Contains("Standard 5-minute test PASSed!", StringComparison.Ordinal);
        VramValidationStatus status = memoryError
            ? VramValidationStatus.MemoryError
            : runtimeError
                ? VramValidationStatus.RuntimeError
                : passed
                    ? VramValidationStatus.Passed
                    : VramValidationStatus.Incomplete;

        string reason = status switch
        {
            VramValidationStatus.MemoryError => FirstMatchingLine(output, "Error found."),
            VramValidationStatus.RuntimeError => FirstMatchingLine(output, "Runtime error:", "early exit", "testing failed"),
            VramValidationStatus.Incomplete => "The official 5-minute validation marker was not reached.",
            _ => ""
        };
        return new VramValidationSummary
        {
            Status = status,
            Device = device.Success ? device.Groups[2].Value.Trim() : "Unknown GPU",
            MemoryMiB = device.Success
                ? int.Parse(device.Groups[1].Value, CultureInfo.InvariantCulture) * 1024
                : 0,
            Iterations = last?.Success == true ? Integer(last.Groups[1].Value) : 0,
            WrittenGiB = last?.Success == true ? Number(last.Groups[2].Value) : 0,
            WriteGiBPerSecond = last?.Success == true ? Number(last.Groups[3].Value) : 0,
            CheckedGiB = last?.Success == true ? Number(last.Groups[4].Value) : 0,
            CheckGiBPerSecond = last?.Success == true ? Number(last.Groups[5].Value) : 0,
            FailureReason = reason
        };
    }

    private static string FirstMatchingLine(string output, params string[] markers)
        => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
               .FirstOrDefault(line => markers.Any(marker =>
                   line.Contains(marker, StringComparison.OrdinalIgnoreCase)))?.Trim()
           ?? "VRAM validation failed.";

    private static int Integer(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static double Number(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

    [GeneratedRegex(@"Standard 5-minute test of \d+:.*?\s+(\d+)GB\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex DeviceRegex();

    [GeneratedRegex(@"^\s*(\d+) iteration\..*?written:\s*([0-9.]+)GB\s+([0-9.]+)GB/sec\s+checked:\s*([0-9.]+)GB\s+([0-9.]+)GB/sec", RegexOptions.Multiline)]
    private static partial Regex IterationRegex();
}

public sealed class MemtestVulkanWorkloadRunner : IWorkloadRunner
{
    public const string Version = "0.5.0";
    public const string OfficialSha256 = "09E67704210762AE8D8AD70FE6D71275EEFBE815CC2C0658CD5AFC8E231D0DF6";
    private readonly IGpuContaminationMonitor? _contaminationMonitor;

    public MemtestVulkanWorkloadRunner(IGpuContaminationMonitor? contaminationMonitor = null)
    {
        _contaminationMonitor = contaminationMonitor;
    }

    public async Task<WorkloadExecution> RunAsync(
        ExternalWorkloadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        VerifyBinary(definition.ExecutablePath);
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
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        DateTimeOffset startedAt = DateTimeOffset.Now;
        var timer = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException("memtest_vulkan could not be started.");
        using var contaminationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<GpuContaminationResult>? contaminationTask = _contaminationMonitor?.ObserveAsync(
            process.Id,
            contaminationStop.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(definition.Timeout);
        bool timedOut = false;
        bool cancelled = false;
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                string? line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line == null) break;
                output.AppendLine(line);
                if (IsTerminalLine(line)) break;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
        }
        finally
        {
            await ProcessSupport.WaitForExitAfterStopAsync(process).ConfigureAwait(false);
        }
        timer.Stop();
        DateTimeOffset endedAt = DateTimeOffset.Now;
        await contaminationStop.CancelAsync().ConfigureAwait(false);
        GpuContaminationResult contamination = contaminationTask == null
            ? new GpuContaminationResult(true, [], "")
            : await contaminationTask.ConfigureAwait(false);

        string stderr = await stderrTask.ConfigureAwait(false);
        if (cancelled)
            throw new OperationCanceledException(cancellationToken);

        VramValidationSummary summary = MemtestVulkanOutputParser.Parse(
            output + Environment.NewLine + stderr);
        string failure = timedOut
            ? $"VRAM validation exceeded {definition.Timeout.TotalMinutes:0.0} min."
            : summary.Status != VramValidationStatus.Passed
                ? summary.FailureReason
                : contamination.MonitoringSucceeded && contamination.BusyProcesses.Count == 0
                    ? ""
                    : GpuContaminationFormatter.Failure(contamination);
        return new WorkloadExecution(
            new WorkloadResult
            {
                Name = definition.Name,
                Version = Version,
                Kind = WorkloadKind.Vram,
                Score = summary.CheckGiBPerSecond,
                ScoreUnit = "GiB/s checked",
                Duration = timer.Elapsed,
                Completed = summary.Status == VramValidationStatus.Passed
                    && contamination.MonitoringSucceeded
                    && contamination.BusyProcesses.Count == 0,
                FailureReason = failure,
                Iterations = summary.Iterations,
                ReportedWrittenGiB = summary.WrittenGiB,
                ReportedCheckedGiB = summary.CheckedGiB
            },
            process.ExitCode,
            output.ToString(),
            stderr,
            timedOut,
            startedAt,
            endedAt);
    }

    public static void VerifyBinary(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("memtest_vulkan was not found.", path);
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(OfficialSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"memtest_vulkan SHA-256 mismatch: {hash}");
    }

    private static bool IsTerminalLine(string line)
        => line.Contains("Standard 5-minute test PASSed!", StringComparison.Ordinal)
           || line.Contains("Error found.", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Runtime error:", StringComparison.OrdinalIgnoreCase)
           || line.Contains("early exit during init", StringComparison.OrdinalIgnoreCase)
           || line.Contains("testing failed", StringComparison.OrdinalIgnoreCase);
}
