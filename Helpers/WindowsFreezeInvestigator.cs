using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    public sealed class FreezeInvestigationReport
    {
        public bool IsValid;
        public IncidentCauseState CauseState = IncidentCauseState.Insufficient;
        public string Conclusion = "";
        public List<string> Evidence = new();
        public int EventsLost;
    }

    /// <summary>
    /// Capture ETW circulaire déclenchée uniquement par l'utilisateur. Elle conserve
    /// les dernières secondes précédant un blocage, puis attribue les pics DPC/ISR
    /// aux pilotes chargés et recoupe les attentes disque et défauts de page.
    /// </summary>
    public static class WindowsFreezeInvestigator
    {
        private static readonly object Gate = new();
        private static TraceEventSession? _session;
        private static string _etlPath = "";
        private static DateTime _startedAt;

        public static bool IsCapturing
        {
            get { lock (Gate) return _session != null; }
        }

        public static DateTime StartedAt
        {
            get { lock (Gate) return _startedAt; }
        }

        public static Task StartAsync()
            => Task.Run(() =>
            {
                lock (Gate)
                {
                    if (_session != null)
                        throw new InvalidOperationException("Une capture de blocage est déjà active.");
                    if (TraceEventSession.IsElevated() != true)
                        throw new InvalidOperationException("La capture ETW nécessite les droits administrateur.");

                    string folder = Path.Combine(PathLayout.Config, "diagnostics");
                    Directory.CreateDirectory(folder);
                    _etlPath = Path.Combine(folder, "freeze-capture.etl");
                    TryDelete(_etlPath);

                    string sessionName = $"TweaklyFreeze-{Environment.ProcessId}";
                    var session = new TraceEventSession(sessionName, _etlPath)
                    {
                        StopOnDispose = true,
                        CircularBufferMB = 96,
                    };

                    try
                    {
                        var keywords = KernelTraceEventParser.Keywords.Process
                                     | KernelTraceEventParser.Keywords.Thread
                                     | KernelTraceEventParser.Keywords.ImageLoad
                                     | KernelTraceEventParser.Keywords.ContextSwitch
                                     | KernelTraceEventParser.Keywords.DeferedProcedureCalls
                                     | KernelTraceEventParser.Keywords.Interrupt
                                     | KernelTraceEventParser.Keywords.DiskIO
                                     | KernelTraceEventParser.Keywords.MemoryHardFaults;
                        session.EnableKernelProvider(keywords);
                        session.EnableProvider(
                            "Microsoft-Windows-DxgKrnl",
                            TraceEventLevel.Informational,
                            ulong.MaxValue);
                        _session = session;
                        _startedAt = DateTime.Now;
                    }
                    catch
                    {
                        session.Dispose();
                        TryDelete(_etlPath);
                        throw;
                    }
                }
            });

        public static Task<FreezeInvestigationReport> StopAndAnalyzeAsync()
            => Task.Run(() =>
            {
                string path;
                int eventsLost;
                lock (Gate)
                {
                    if (_session == null)
                        throw new InvalidOperationException("Aucune capture n'est active.");

                    TraceEventSession session = _session;
                    _session = null;
                    path = _etlPath;

                    try
                    {
                        // TraceEvent interroge encore la session ETW pour ce compteur.
                        // Il doit donc être lu avant Stop(), qui supprime son instance.
                        eventsLost = session.EventsLost;
                    }
                    catch (COMException ex)
                    {
                        eventsLost = -1;
                        AppLog.Error("Capture ETW : compteur d'événements perdus indisponible", ex);
                    }

                    try
                    {
                        session.Stop();
                    }
                    finally
                    {
                        session.Dispose();
                    }
                }

                return Analyze(path, eventsLost);
            });

        public static void Cancel()
        {
            lock (Gate)
            {
                if (_session == null) return;
                try { _session.Stop(); } catch { }
                try { _session.Dispose(); } catch { }
                _session = null;
                TryDelete(_etlPath);
            }
        }

        private static FreezeInvestigationReport Analyze(string path, int eventsLost)
        {
            var modules = new List<ModuleRange>();
            var dpcs = new List<RoutineSample>();
            var isrs = new List<RoutineSample>();
            var disks = new List<DiskSample>();
            var faults = new List<FaultSample>();
            int graphicsEvents = 0;
            double endMs = 0;

            using (var source = new ETWTraceEventSource(path))
            {
                var kernel = new KernelTraceEventParser(source);
                kernel.ImageLoad += data =>
                {
                    endMs = Math.Max(endMs, data.TimeStampRelativeMSec);
                    modules.Add(new ModuleRange(
                        data.ImageBase,
                        data.ImageBase + (ulong)Math.Max(0, data.ImageSize),
                        data.FileName ?? ""));
                };
                kernel.PerfInfoDPC += data =>
                {
                    endMs = Math.Max(endMs, data.TimeStampRelativeMSec);
                    dpcs.Add(new RoutineSample(data.TimeStampRelativeMSec, data.ElapsedTimeMSec, data.Routine));
                };
                kernel.PerfInfoISR += data =>
                {
                    endMs = Math.Max(endMs, data.TimeStampRelativeMSec);
                    isrs.Add(new RoutineSample(data.TimeStampRelativeMSec, data.ElapsedTimeMSec, data.Routine));
                };
                kernel.DiskIORead += data => AddDisk(data, "lecture");
                kernel.DiskIOWrite += data => AddDisk(data, "écriture");
                kernel.MemoryHardFault += data =>
                {
                    endMs = Math.Max(endMs, data.TimeStampRelativeMSec);
                    string process = string.IsNullOrWhiteSpace(data.ProcessName)
                        ? $"PID {data.ProcessID}"
                        : data.ProcessName;
                    faults.Add(new FaultSample(data.TimeStampRelativeMSec, process));
                };
                source.Dynamic.All += data =>
                {
                    endMs = Math.Max(endMs, data.TimeStampRelativeMSec);
                    if (data.ProviderName.Contains("DxgKrnl", StringComparison.OrdinalIgnoreCase))
                        graphicsEvents++;
                };
                source.Process();

                void AddDisk(DiskIOTraceData data, string operation)
                {
                    endMs = Math.Max(endMs, data.TimeStampRelativeMSec);
                    disks.Add(new DiskSample(
                        data.TimeStampRelativeMSec,
                        data.DiskServiceTimeMSec,
                        data.ProcessName ?? "",
                        data.FileName ?? "",
                        operation));
                }
            }

            var report = new FreezeInvestigationReport { EventsLost = eventsLost };
            if (eventsLost > 0)
            {
                report.Conclusion = $"Capture invalide : {eventsLost} événements ETW ont été perdus.";
                report.Evidence.Add($"Événements perdus : {eventsLost}");
                return report;
            }

            if (eventsLost < 0)
                report.Evidence.Add("Compteur d'événements ETW perdus indisponible.");

            double windowStart = Math.Max(0, endMs - 20_000);
            var recentDpcs = dpcs.Where(item => item.AtMs >= windowStart)
                .OrderByDescending(item => item.DurationMs).ToList();
            var recentIsrs = isrs.Where(item => item.AtMs >= windowStart)
                .OrderByDescending(item => item.DurationMs).ToList();
            var recentDisks = disks.Where(item => item.AtMs >= windowStart)
                .OrderByDescending(item => item.DurationMs).ToList();
            var recentFaults = faults.Where(item => item.AtMs >= windowStart)
                .GroupBy(item => item.Process, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Process = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count).ToList();

            RoutineSample? dpc = recentDpcs.FirstOrDefault(item => item.DurationMs >= 10);
            RoutineSample? isr = recentIsrs.FirstOrDefault(item => item.DurationMs >= 5);
            DiskSample? disk = recentDisks.FirstOrDefault(item => item.DurationMs >= 250);
            var fault = recentFaults.FirstOrDefault(item => item.Count >= 50);

            if (dpc != null)
            {
                string driver = ResolveModule(modules, dpc.Routine);
                report.CauseState = driver.StartsWith("0x", StringComparison.Ordinal)
                    ? IncidentCauseState.Probable
                    : IncidentCauseState.Established;
                report.Conclusion = driver.StartsWith("0x", StringComparison.Ordinal)
                    ? $"Un DPC a bloqué un cœur pendant {dpc.DurationMs:F1} ms, mais son pilote n'a pas pu être résolu."
                    : $"Le pilote {Path.GetFileName(driver)} a exécuté un DPC de {dpc.DurationMs:F1} ms dans les 20 s précédant l'arrêt de la capture.";
                report.Evidence.Add($"DPC maximal : {dpc.DurationMs:F1} ms — {driver}");
            }
            else if (isr != null)
            {
                string driver = ResolveModule(modules, isr.Routine);
                report.CauseState = driver.StartsWith("0x", StringComparison.Ordinal)
                    ? IncidentCauseState.Probable
                    : IncidentCauseState.Established;
                report.Conclusion = driver.StartsWith("0x", StringComparison.Ordinal)
                    ? $"Une interruption matérielle a retenu un cœur pendant {isr.DurationMs:F1} ms, sans pilote résolu."
                    : $"Le pilote {Path.GetFileName(driver)} a retenu une interruption pendant {isr.DurationMs:F1} ms dans la fenêtre du blocage.";
                report.Evidence.Add($"ISR maximale : {isr.DurationMs:F1} ms — {driver}");
            }
            else if (disk != null)
            {
                report.CauseState = IncidentCauseState.Probable;
                report.Conclusion = $"Une {disk.Operation} disque a attendu {disk.DurationMs:F0} ms dans la fenêtre du blocage.";
                report.Evidence.Add($"Disque : {disk.DurationMs:F0} ms — {Display(disk.Process)} — {Display(disk.File)}");
            }
            else if (fault != null)
            {
                report.CauseState = IncidentCauseState.Probable;
                report.Conclusion = $"{fault.Process} a provoqué {fault.Count} défauts de page matériels dans les 20 s précédant l'arrêt de la capture.";
                report.Evidence.Add($"Défauts de page : {fault.Count} — {fault.Process}");
            }
            else
            {
                report.CauseState = IncidentCauseState.Insufficient;
                report.Conclusion = "Aucun blocage DPC/ISR, disque ou mémoire suffisamment long n'est présent dans les 20 dernières secondes de la capture.";
            }

            if (recentDisks.Count > 0)
            {
                DiskSample top = recentDisks[0];
                report.Evidence.Add($"Attente disque maximale : {top.DurationMs:F0} ms — {Display(top.Process)}");
            }
            if (recentFaults.Count > 0)
                report.Evidence.Add($"Défauts de page dominants : {recentFaults[0].Count} — {recentFaults[0].Process}");
            report.Evidence.Add($"Événements graphiques capturés : {graphicsEvents}");
            report.IsValid = true;
            return report;
        }

        private static string ResolveModule(IEnumerable<ModuleRange> modules, ulong routine)
        {
            ModuleRange? module = modules
                .Where(item => routine >= item.Start && routine < item.End)
                .OrderBy(item => item.End - item.Start)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(module?.FileName) ? $"0x{routine:X}" : module.FileName;
        }

        private static string Display(string value)
            => string.IsNullOrWhiteSpace(value) ? "non exposé" : value;

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed record ModuleRange(ulong Start, ulong End, string FileName);
        private sealed record RoutineSample(double AtMs, double DurationMs, ulong Routine);
        private sealed record DiskSample(double AtMs, double DurationMs, string Process, string File, string Operation);
        private sealed record FaultSample(double AtMs, string Process);
    }
}
