using System.Diagnostics;
using System.Globalization;

namespace GpuTuningLab.Core;

public sealed record FrameTimeSample(
    string Application,
    int ProcessId,
    double TimeSeconds,
    double FrameTimeMs,
    double? GpuTimeMs,
    string PresentMode);

public sealed record FrameTimeSummary
{
    public required int FrameCount { get; init; }
    public required double DurationSeconds { get; init; }
    public required double MedianFps { get; init; }
    public required double OnePercentLowFps { get; init; }
    public required double MedianFrameTimeMs { get; init; }
    public required double P95FrameTimeMs { get; init; }
    public required double P99FrameTimeMs { get; init; }
    public required double MaxFrameTimeMs { get; init; }
    public required double FrameTimeCoefficientOfVariationPercent { get; init; }
}

public static class PresentMonCsv
{
    public static IReadOnlyList<FrameTimeSample> Parse(string path, string? processName = null)
    {
        using var reader = new StreamReader(path);
        string? headerLine = reader.ReadLine();
        if (headerLine == null) return [];
        string[] headers = SplitCsv(headerLine).ToArray();
        var columns = headers.Select((name, index) => (name, index))
            .ToDictionary(static item => item.name, static item => item.index, StringComparer.OrdinalIgnoreCase);
        Require(columns, "Application", "ProcessID", "MsBetweenPresents");
        int timeMs = Index(columns, "TimeInMs");
        int timeSeconds = Index(columns, "TimeInSeconds");
        if (timeMs < 0 && timeSeconds < 0)
            throw new InvalidDataException("PresentMon CSV has neither TimeInMs nor TimeInSeconds.");

        var samples = new List<FrameTimeSample>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] values = SplitCsv(line).ToArray();
            if (values.Length < headers.Length) continue;
            string application = Get(values, columns, "Application");
            if (!string.IsNullOrWhiteSpace(processName)
                && !application.Equals(processName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(Get(values, columns, "ProcessID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) continue;
            if (!Number(Get(values, columns, "MsBetweenPresents"), out double frameMs) || frameMs <= 0) continue;
            double time = 0;
            if (timeMs >= 0 && Number(values[timeMs], out double rawMs)) time = rawMs / 1000.0;
            else if (timeSeconds >= 0) Number(values[timeSeconds], out time);
            double? gpuTime = Number(Get(values, columns, "MsGPUTime"), out double gpu) ? gpu : null;
            samples.Add(new FrameTimeSample(
                application,
                pid,
                time,
                frameMs,
                gpuTime,
                Get(values, columns, "PresentMode")));
        }
        return samples;
    }

    public static FrameTimeSummary Summarize(IReadOnlyList<FrameTimeSample> samples)
    {
        if (samples.Count < 2) throw new InvalidOperationException("At least two displayed frames are required.");
        double[] frameTimes = samples.Select(static sample => sample.FrameTimeMs).Order().ToArray();
        double median = Percentile(frameTimes, 0.50);
        double p95 = Percentile(frameTimes, 0.95);
        double p99 = Percentile(frameTimes, 0.99);
        double mean = frameTimes.Average();
        double variance = frameTimes.Sum(value => Math.Pow(value - mean, 2)) / (frameTimes.Length - 1);
        double duration = samples.Max(static sample => sample.TimeSeconds) - samples.Min(static sample => sample.TimeSeconds);
        return new FrameTimeSummary
        {
            FrameCount = samples.Count,
            DurationSeconds = Math.Max(0, duration),
            MedianFps = 1000.0 / median,
            OnePercentLowFps = 1000.0 / p99,
            MedianFrameTimeMs = median,
            P95FrameTimeMs = p95,
            P99FrameTimeMs = p99,
            MaxFrameTimeMs = frameTimes[^1],
            FrameTimeCoefficientOfVariationPercent = mean <= 0 ? 0 : Math.Sqrt(variance) / mean * 100
        };
    }

    private static IEnumerable<string> SplitCsv(string line)
    {
        var value = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                yield return value.ToString();
                value.Clear();
            }
            else value.Append(c);
        }
        yield return value.ToString();
    }

    private static void Require(IReadOnlyDictionary<string, int> columns, params string[] names)
    {
        string[] missing = names.Where(name => !columns.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("PresentMon CSV is missing: " + string.Join(", ", missing));
    }

    private static int Index(IReadOnlyDictionary<string, int> columns, string name)
        => columns.TryGetValue(name, out int index) ? index : -1;

    private static string Get(string[] values, IReadOnlyDictionary<string, int> columns, string name)
        => columns.TryGetValue(name, out int index) && index < values.Length ? values[index] : "";

    private static bool Number(string raw, out double value)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        double position = (sorted.Count - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

public sealed class PresentMonCapture
{
    public async Task<string> CaptureAsync(
        string presentMonPath,
        int processId,
        TimeSpan duration,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(presentMonPath)) throw new FileNotFoundException("PresentMon not found.", presentMonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string sessionName = "GpuTuningLab_" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo
        {
            FileName = presentMonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "--process_id", processId.ToString(CultureInfo.InvariantCulture),
            "--output_file", Path.GetFullPath(outputPath),
            "--timed", Math.Ceiling(duration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed",
            "--no_console_stats",
            "--v2_metrics",
            "--session_name", sessionName
        }) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("PresentMon could not be started.");
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration + TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ProcessSupport.WaitForExitAfterStopAsync(process).ConfigureAwait(false);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("PresentMon did not stop after the requested capture.");
        }
        string error = await stderr.ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"PresentMon failed: {error.Trim()}");
        if (!File.Exists(outputPath)) throw new InvalidOperationException("PresentMon did not create the CSV output.");
        return outputPath;
    }
}
