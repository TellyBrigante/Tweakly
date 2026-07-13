using System.Diagnostics;
using System.Globalization;

namespace GpuTuningLab.Core;

public interface IGpuTelemetrySource
{
    Task<TelemetryCapture> CaptureAsync(TimeSpan duration, int intervalMs, CancellationToken cancellationToken = default);
}

public interface ITelemetryEnricher : IDisposable
{
    string Name { get; }
    bool Available { get; }
    GpuTelemetrySample Enrich(GpuTelemetrySample sample);
}

public sealed class NvidiaSmiTelemetrySource : IGpuTelemetrySource
{
    private readonly ITelemetryEnricher? _enricher;

    public NvidiaSmiTelemetrySource(ITelemetryEnricher? enricher = null)
    {
        _enricher = enricher;
    }

    internal const string Query =
        "timestamp,name,uuid,pci.bus_id,pci.device_id,pci.sub_device_id,driver_version,vbios_version," +
        "pstate,temperature.gpu,power.draw.average,power.draw.instant,power.limit,enforced.power.limit," +
        "power.default_limit,power.min_limit,power.max_limit,clocks.current.graphics,clocks.current.memory," +
        "clocks.max.graphics,clocks.max.memory,utilization.gpu,utilization.memory,memory.used,memory.total," +
        "fan.speed,clocks_event_reasons.active";

    public async Task<TelemetryCapture> CaptureAsync(
        TimeSpan duration,
        int intervalMs,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (intervalMs is < 200 or > 5000) throw new ArgumentOutOfRangeException(nameof(intervalMs));

        var startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = $"--query-gpu={Query} --format=csv,noheader,nounits --loop-ms={intervalMs}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("nvidia-smi could not be started.");

        var warnings = new List<string>();
        var samples = new List<GpuTelemetrySample>();
        GpuIdentity? identity = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (!NvidiaSmiCsv.TryParse(line, out var parsed, out var error))
                {
                    warnings.Add(error);
                    continue;
                }

                if (identity == null)
                {
                    identity = parsed.Identity;
                }
                else if (!identity.Uuid.Equals(parsed.Identity.Uuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Multiple NVIDIA GPUs were returned. Explicit GPU selection is required before testing this system.");
                }

                samples.Add(_enricher?.Available == true
                    ? _enricher.Enrich(parsed.Sample)
                    : parsed.Sample);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                ProcessSupport.TryKillTree(process);
            }

            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }

        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr)) warnings.Add(stderr.Trim());
        if (identity == null)
            throw new InvalidOperationException("No NVIDIA telemetry sample was returned. " + string.Join(" | ", warnings));

        return new TelemetryCapture(identity, samples, warnings);
    }
}

public sealed record ParsedNvidiaSmiLine(GpuIdentity Identity, GpuTelemetrySample Sample);

public static class NvidiaSmiCsv
{
    private const int FieldCount = 27;

    public static bool TryParse(string line, out ParsedNvidiaSmiLine parsed, out string error)
    {
        parsed = null!;
        error = "";
        string[] fields = line.Split(',').Select(static value => value.Trim()).ToArray();
        if (fields.Length != FieldCount)
        {
            error = $"nvidia-smi returned {fields.Length} fields instead of {FieldCount}.";
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                fields[0],
                "yyyy/MM/dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var timestamp))
        {
            error = $"Invalid nvidia-smi timestamp: {fields[0]}";
            return false;
        }

        var identity = new GpuIdentity(
            fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7]);

        var sample = new GpuTelemetrySample
        {
            Timestamp = timestamp,
            PerformanceState = fields[8],
            TemperatureC = Number(fields[9]),
            PowerAverageW = Number(fields[10]),
            PowerInstantW = Number(fields[11]),
            RequestedPowerLimitW = Number(fields[12]),
            EnforcedPowerLimitW = Number(fields[13]),
            DefaultPowerLimitW = Number(fields[14]),
            MinPowerLimitW = Number(fields[15]),
            MaxPowerLimitW = Number(fields[16]),
            CoreClockMhz = Number(fields[17]),
            MemoryClockMhz = Number(fields[18]),
            MaxCoreClockMhz = Number(fields[19]),
            MaxMemoryClockMhz = Number(fields[20]),
            GpuUtilizationPercent = Number(fields[21]),
            MemoryUtilizationPercent = Number(fields[22]),
            VramUsedMiB = Number(fields[23]),
            VramTotalMiB = Number(fields[24]),
            FanPercent = Number(fields[25]),
            ClockEventReasons = ParseReasons(fields[26])
        };

        parsed = new ParsedNvidiaSmiLine(identity, sample);
        return true;
    }

    private static double? Number(string value)
        => value is "N/A" or "[Not Supported]"
            ? null
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
                ? result
                : null;

    private static NvidiaClockEventReasons ParseReasons(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong mask))
            return (NvidiaClockEventReasons)mask;
        return NvidiaClockEventReasons.None;
    }
}
