using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace GpuTuningLab.Core;

public interface IStabilityEvidenceCollector
{
    Task<IReadOnlyList<StabilityEvent>> CollectAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? processName = null,
        GpuIdentity? gpu = null,
        CancellationToken cancellationToken = default);
}

public sealed class WevtutilEvidenceCollector : IStabilityEvidenceCollector
{
    private static readonly string[] Logs = ["System", "Application"];

    public async Task<IReadOnlyList<StabilityEvent>> CollectAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? processName = null,
        GpuIdentity? gpu = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from) throw new ArgumentOutOfRangeException(nameof(to));
        var events = new List<StabilityEvent>();
        foreach (string log in Logs)
        {
            string xml = await QueryAsync(log, from, to, cancellationToken).ConfigureAwait(false);
            events.AddRange(WindowsEventEvidenceParser.Parse(xml, processName, gpu?.DeviceId));
        }
        return events.OrderBy(static item => item.Timestamp).ToArray();
    }

    private static async Task<string> QueryAsync(
        string log,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        string start = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string end = to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string query = $"*[System[TimeCreated[@SystemTime>='{start}' and @SystemTime<='{end}']]]";
        var info = new ProcessStartInfo
        {
            FileName = "wevtutil.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("qe");
        info.ArgumentList.Add(log);
        info.ArgumentList.Add($"/q:{query}");
        info.ArgumentList.Add("/f:xml");
        info.ArgumentList.Add("/rd:false");

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ProcessSupport.WaitForExitAfterStopAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException($"Windows event query timed out for {log}.");
        }

        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"wevtutil failed for {log}: {error.Trim()}");
        return output;
    }
}

public static class WindowsEventEvidenceParser
{
    private static readonly XNamespace EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static IReadOnlyList<StabilityEvent> Parse(
        string xml,
        string? processName = null,
        string? gpuDeviceId = null)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        XDocument document;
        try { document = XDocument.Parse(xml, LoadOptions.None); }
        catch (System.Xml.XmlException) { return []; }

        var results = new List<StabilityEvent>();
        foreach (XElement element in document.Descendants(EventNs + "Event"))
        {
            XElement? system = element.Element(EventNs + "System");
            string provider = system?.Element(EventNs + "Provider")?.Attribute("Name")?.Value ?? "";
            int.TryParse(system?.Element(EventNs + "EventID")?.Value, out int id);
            DateTimeOffset.TryParse(
                system?.Element(EventNs + "TimeCreated")?.Attribute("SystemTime")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var timestamp);
            string payload = string.Join(" | ", element.Descendants(EventNs + "Data")
                .Select(static data => data.Value.Trim())
                .Where(static value => value.Length > 0));

            if (!TryClassify(provider, id, payload, processName, gpuDeviceId, out var kind)) continue;
            string evidence = string.IsNullOrWhiteSpace(payload)
                ? $"{provider}/{id}"
                : $"{provider}/{id}: {Limit(payload, 240)}";
            results.Add(new StabilityEvent
            {
                Timestamp = timestamp,
                Kind = kind,
                Evidence = evidence
            });
        }
        return results;
    }

    private static bool TryClassify(
        string provider,
        int id,
        string payload,
        string? processName,
        string? gpuDeviceId,
        out StabilityEventKind kind)
    {
        kind = default;
        if (provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && id == 4101)
        {
            kind = StabilityEventKind.Tdr;
            return true;
        }
        if (provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)
            && id is 0 or 13 or 14 or 153)
        {
            kind = StabilityEventKind.DriverReset;
            return true;
        }
        if (provider.Contains("WHEA-Logger", StringComparison.OrdinalIgnoreCase)
            && id is 17 or 18 or 19)
        {
            string normalizedDeviceId = (gpuDeviceId ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (!payload.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                && !payload.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)
                && (normalizedDeviceId.Length < 4
                    || !payload.Contains(normalizedDeviceId[..4], StringComparison.OrdinalIgnoreCase)))
                return false;
            kind = StabilityEventKind.Whea;
            return true;
        }
        if (provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && id == 1000)
        {
            if (!string.IsNullOrWhiteSpace(processName)
                && !payload.Contains(processName, StringComparison.OrdinalIgnoreCase)) return false;
            kind = StabilityEventKind.ApplicationCrash;
            return true;
        }
        if (provider.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) && id == 1001)
        {
            if (!string.IsNullOrWhiteSpace(processName)
                && !payload.Contains(processName, StringComparison.OrdinalIgnoreCase)) return false;
            if (payload.Contains("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)
                && (payload.Contains("141", StringComparison.OrdinalIgnoreCase)
                    || payload.Contains("117", StringComparison.OrdinalIgnoreCase)
                    || payload.Contains("142", StringComparison.OrdinalIgnoreCase)))
            {
                kind = StabilityEventKind.DriverReset;
                return true;
            }
            if (!string.IsNullOrWhiteSpace(processName))
            {
                kind = StabilityEventKind.ApplicationCrash;
                return true;
            }
        }
        return false;
    }

    private static string Limit(string value, int length)
        => value.Length <= length ? value : value[..length] + "...";
}
