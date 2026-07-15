using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public enum IncidentCauseState
    {
        Established,
        Probable,
        Insufficient,
    }

    public enum IncidentRepairKind
    {
        VssWriters,
        NtfsVolume,
        WindowsComponents,
        StorePackagesInUse,
    }

    public enum IncidentRepairPhase
    {
        NeedsDiagnosis,
        Ready,
        Running,
        Corrected,
        NotPresent,
        Blocked,
    }

    public sealed class IncidentRepairPlan
    {
        public IncidentRepairKind Kind;
        public IncidentRepairPhase Phase = IncidentRepairPhase.NeedsDiagnosis;
        public string Target = "";
        public string Title = "";
        public string Detail = "";
        public string Status = "";
        public List<string> VerifiedTargets = new();
    }

    public enum IncidentInvestigationKind
    {
        FreezeTrace,
    }

    public enum IncidentInvestigationPhase
    {
        Ready,
        Capturing,
        Analyzing,
        Completed,
        Failed,
    }

    public sealed class IncidentInvestigationPlan
    {
        public IncidentInvestigationKind Kind;
        public IncidentInvestigationPhase Phase = IncidentInvestigationPhase.Ready;
        public string Target = "";
        public string Title = "";
        public string Detail = "";
        public string Status = "";
    }

    public static class IncidentDiagnosticEngine
    {
        private static readonly string[] EvidenceKeys =
        {
            "BugcheckCode", "BugCheckCode", "DeviceName", "DeviceObject",
            "DriveName", "VolumeName", "FaultingApplicationName", "FaultingModuleName",
            "ExceptionCode", "ErrorSource", "ErrorSourceType", "param1",
        };

        public static void Enrich(Incident incident, IReadOnlyList<RawEvent> events)
        {
            incident.Evidence = BuildEvidence(events);
            incident.CauseState = IncidentCauseState.Insufficient;
            incident.Conclusion = "L'incident est confirmé, mais ces événements ne suffisent pas à établir sa cause.";
            incident.Repair = null;
            incident.Investigation = null;

            if (TryVss(incident, events)) return;
            if (TryNtfs(incident, events)) return;
            if (TryWhea(incident, events)) return;
            if (TryGpu(incident, events)) return;
            if (TryService(incident, events)) return;
            if (TryBugCheck(incident, events)) return;
            if (TryDotNetCrash(incident, events)) return;
            if (TryApplicationCrash(incident, events)) return;
            if (TryApplicationHang(incident, events)) return;
            if (TryWindowsUpdate(incident, events)) return;

            incident.Conclusion = "Windows a enregistré cette séquence, mais aucun lien causal suffisamment précis n'est présent dans les données collectées.";
        }

        private static bool TryVss(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var vss = events.Where(e =>
                e.Provider.Equals("VSS", StringComparison.OrdinalIgnoreCase)
                && e.Id is not 8224 and not 8231).ToList();
            if (vss.Count == 0) return false;

            incident.CauseState = IncidentCauseState.Probable;
            incident.Conclusion = "VSS a échoué pendant une opération de cliché. L'état réel de chaque writer doit être contrôlé avant toute correction.";
            incident.Repair = new IncidentRepairPlan
            {
                Kind = IncidentRepairKind.VssWriters,
                Title = "Contrôler et réparer VSS",
                Detail = "Tweakly vérifiera chaque writer VSS. Une correction ne sera proposée que si un writer est encore défaillant et que son service est identifié.",
                Status = "Diagnostic VSS requis.",
            };
            return true;
        }

        private static bool TryNtfs(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var ntfs = events.Where(e =>
                e.Provider.Contains("ntfs", StringComparison.OrdinalIgnoreCase)
                && e.Id is 55 or 98).ToList();
            if (ntfs.Count == 0) return false;

            string volume = FindDriveLetter(ntfs);
            incident.CauseState = volume.Length > 0
                ? IncidentCauseState.Established
                : IncidentCauseState.Probable;
            incident.Conclusion = volume.Length > 0
                ? $"Windows a signalé une corruption du système de fichiers sur le volume {volume}."
                : "Windows a signalé une corruption NTFS, mais le volume concerné n'est pas exposé dans l'événement.";

            if (volume.Length > 0)
            {
                incident.Repair = new IncidentRepairPlan
                {
                    Kind = IncidentRepairKind.NtfsVolume,
                    Target = volume,
                    Title = $"Analyser et réparer {volume}",
                    Detail = $"Tweakly lancera d'abord un contrôle en ligne de {volume}. La réparation ne sera engagée que si Windows confirme une corruption.",
                    Status = $"Contrôle du volume {volume} requis.",
                };
            }
            return true;
        }

        private static bool TryWhea(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var whea = events.Where(e => e.Provider.Contains("whea", StringComparison.OrdinalIgnoreCase)).ToList();
            if (whea.Count == 0) return false;

            string source = whea.SelectMany(e => e.Data)
                .Where(pair => pair.Key.Equals("ErrorSource", StringComparison.OrdinalIgnoreCase)
                            || pair.Key.Equals("ErrorSourceType", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            incident.CauseState = source.Length > 0
                ? IncidentCauseState.Established
                : IncidentCauseState.Probable;
            incident.Conclusion = source.Length > 0
                ? $"Le matériel a signalé une erreur WHEA. Source remontée par Windows : {source}."
                : "Le matériel a signalé une erreur WHEA, mais l'événement ne désigne pas précisément le composant.";
            return true;
        }

        private static bool TryGpu(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var gpu = events.Where(IsGpuReset).ToList();
            if (gpu.Count == 0) return false;

            string vendor = gpu.Any(e => e.Provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)
                                      || e.RawFull.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase))
                ? "NVIDIA"
                : gpu.Any(e => e.Provider.Contains("amd", StringComparison.OrdinalIgnoreCase)) ? "AMD" : "graphique";
            incident.CauseState = IncidentCauseState.Insufficient;
            incident.Conclusion = $"Windows a réinitialisé le pilote {vendor}. Le reset est confirmé ; sa cause n'est pas établie par les événements disponibles.";
            incident.Investigation = CreateFreezeInvestigation("le prochain blocage graphique");
            return true;
        }

        private static bool TryService(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var serviceEvents = events.Where(e =>
                e.Provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase)).ToList();
            if (serviceEvents.Count == 0) return false;

            string service = serviceEvents
                .Select(e => FirstValue(e, "param1", "ServiceName", "#0"))
                .FirstOrDefault(value => value.Length > 0) ?? "";
            incident.CauseState = service.Length > 0
                ? IncidentCauseState.Probable
                : IncidentCauseState.Insufficient;
            incident.Conclusion = service.Length > 0
                ? $"Le service « {service} » échoue de façon répétée. Le journal confirme le service touché, pas encore la cause de ses arrêts."
                : "Un service Windows échoue, mais son identité n'est pas présente dans les données collectées.";
            return true;
        }

        private static bool TryBugCheck(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var bugCheckEvents = events.Where(e =>
                (e.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.Id == 41)
                || e.Provider.Contains("BugCheck", StringComparison.OrdinalIgnoreCase)
                || e.Provider.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase))
                .ToList();
            string code = bugCheckEvents.Select(e => FirstValue(e, "BugcheckCode", "BugCheckCode", "param1", "#0"))
                .FirstOrDefault(value => value.Length > 0 && value != "0") ?? "";
            bool hasBugCheck = code.Length > 0 || bugCheckEvents.Count > 0;
            if (!hasBugCheck) return false;

            incident.CauseState = IncidentCauseState.Insufficient;
            incident.Conclusion = code.Length > 0
                ? $"Le BSOD est confirmé avec le code {code}. Ce code classe l'incident mais ne désigne pas à lui seul le pilote ou le composant responsable."
                : "Le BSOD est confirmé, mais aucun code exploitable n'a été extrait de cet événement.";
            return true;
        }

        private static bool TryApplicationCrash(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var crash = events.FirstOrDefault(e =>
                e.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase));
            if (crash == null) return false;

            string app = FirstValue(crash, "FaultingApplicationName", "AppName", "param1", "#0");
            string module = FirstValue(crash, "FaultingModuleName", "ModuleName", "param3", "#2");
            incident.CauseState = module.Length > 0
                ? IncidentCauseState.Probable
                : IncidentCauseState.Insufficient;
            incident.Conclusion = module.Length > 0
                ? $"{(app.Length > 0 ? app : "L'application")} a planté dans {module}. Le module est localisé, mais cela ne prouve pas encore pourquoi il a échoué."
                : "Le crash applicatif est confirmé, mais le module fautif n'est pas exposé dans l'événement.";
            incident.Investigation = CreateFreezeInvestigation(
                app.Length > 0 ? $"le prochain crash de {app}" : "le prochain crash applicatif");
            return true;
        }

        private static bool TryDotNetCrash(Incident incident, IReadOnlyList<RawEvent> events)
        {
            RawEvent? runtime = events.FirstOrDefault(e =>
                e.Provider.Equals(".NET Runtime", StringComparison.OrdinalIgnoreCase)
                && e.Id == 1026);
            if (runtime == null) return false;

            string app = Match(runtime.RawFull, @"(?im)^Application:\s*(.+)$");
            string exception = Match(runtime.RawFull, @"(?im)^Exception Info:\s*([^\r\n]+)");
            string firstFrame = Match(runtime.RawFull, @"(?im)^\s*at\s+([^\r\n]+)");
            string exceptionType = Match(exception, @"^([A-Za-z0-9_.+]+Exception)");

            incident.Title = app.Length > 0
                ? $"{app} a planté — {(exceptionType.Length > 0 ? exceptionType : "exception .NET")}"
                : "Application .NET arrêtée par une exception";
            incident.CauseState = exception.Length > 0
                ? IncidentCauseState.Established
                : IncidentCauseState.Probable;
            incident.Conclusion = exception.Length > 0
                ? $"Le processus a été arrêté par une exception non gérée : {exception}."
                : "Le runtime .NET a arrêté le processus après une exception non gérée.";
            AddEvidence(incident, exception.Length > 0 ? $"Exception : {exception}" : "");
            AddEvidence(incident, firstFrame.Length > 0 ? $"Première frame : {firstFrame}" : "");
            return true;
        }

        private static bool TryApplicationHang(Incident incident, IReadOnlyList<RawEvent> events)
        {
            RawEvent? hang = events.FirstOrDefault(e =>
                e.Provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase)
                || (e.Provider.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase)
                    && (FirstValue(e, "EventName").Contains("AppHang", StringComparison.OrdinalIgnoreCase)
                        || e.RawFull.Contains("AppHang", StringComparison.OrdinalIgnoreCase))));
            if (hang == null) return false;

            string app = FirstValue(hang, "AppName", "P1", "param1", "#0");
            if (app.Length == 0)
                app = Match(hang.RawFull, @"(?im)^(?:Nom de l.application|Application Name|P1)\s*:\s*([^\r\n]+)");

            incident.Title = app.Length > 0 ? $"{app} ne répondait plus" : "Application bloquée";
            incident.CauseState = IncidentCauseState.Insufficient;
            incident.Conclusion = app.Length > 0
                ? $"Windows confirme que {app} ne répondait plus. Le journal ne contient pas la durée exacte du blocage ni le composant qui retenait son thread principal."
                : "Windows confirme un blocage applicatif, sans exposer le composant qui retenait le thread principal.";
            incident.Investigation = CreateFreezeInvestigation(
                app.Length > 0 ? $"le prochain blocage de {app}" : "le prochain blocage applicatif");
            return true;
        }

        private static bool TryWindowsUpdate(Incident incident, IReadOnlyList<RawEvent> events)
        {
            var failures = events.Where(e =>
                e.Provider.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase)
                && e.Id == 20).ToList();
            if (failures.Count == 0) return false;

            string code = failures.Select(e => FirstValue(e, "errorCode"))
                .FirstOrDefault(value => value.Length > 0) ?? "";
            var titles = failures.Select(e => FirstValue(e, "updateTitle"))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string target = titles.Count > 0 ? string.Join(", ", titles) : "la mise à jour";

            incident.Title = "Échec d'installation Windows Update";
            incident.CauseState = code.Equals("0x80073d02", StringComparison.OrdinalIgnoreCase)
                ? IncidentCauseState.Established
                : IncidentCauseState.Probable;
            incident.Conclusion = code.Equals("0x80073d02", StringComparison.OrdinalIgnoreCase)
                ? $"Windows a refusé d'installer {target} parce que des fichiers du package étaient encore utilisés (0x80073D02)."
                : $"Windows Update a échoué pour {target}{(code.Length > 0 ? $" avec le code {code}" : "")}.";
            AddEvidence(incident, code.Length > 0 ? $"Code Windows Update : {code}" : "");
            foreach (string title in titles) AddEvidence(incident, $"Mise à jour : {title}");
            if (code.Equals("0x80073d02", StringComparison.OrdinalIgnoreCase) && titles.Count > 0)
            {
                var targets = titles.Select(title =>
                {
                    int separator = title.IndexOf('-');
                    string storeId = separator > 0 ? title[..separator] : title;
                    string package = separator > 0 && separator + 1 < title.Length ? title[(separator + 1)..] : "";
                    return $"{storeId}|{package}";
                });
                incident.Repair = new IncidentRepairPlan
                {
                    Kind = IncidentRepairKind.StorePackagesInUse,
                    Phase = IncidentRepairPhase.Ready,
                    Target = string.Join(";", targets),
                    Title = "Relancer les mises à jour bloquées",
                    Detail = "Tweakly fermera uniquement les processus des packages concernés, relancera leurs mises à jour Microsoft Store, puis vérifiera le code de sortie de chaque installation.",
                    Status = $"{titles.Count} mise(s) à jour bloquée(s) prête(s) à être relancée(s).",
                };
            }
            return true;
        }

        private static IncidentInvestigationPlan CreateFreezeInvestigation(string target)
            => new()
            {
                Kind = IncidentInvestigationKind.FreezeTrace,
                Target = target,
                Title = "Capturer la prochaine occurrence",
                Detail = "Tweakly enregistrera en boucle les pilotes, blocages disque, défauts mémoire et événements graphiques. Après le problème, arrête la capture pour analyser les dernières secondes.",
                Status = "Prêt à surveiller.",
            };

        private static void AddEvidence(Incident incident, string value)
        {
            if (value.Length > 0 && !incident.Evidence.Contains(value, StringComparer.OrdinalIgnoreCase))
                incident.Evidence.Add(value);
        }

        private static string Match(string value, string pattern)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            Match match = Regex.Match(value, pattern, RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private static List<string> BuildEvidence(IReadOnlyList<RawEvent> events)
        {
            var evidence = events
                .GroupBy(e => $"{e.Provider}/{e.Id}", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Min(e => e.Time))
                .Take(4)
                .Select(group =>
                {
                    DateTime first = group.Min(e => e.Time);
                    DateTime last = group.Max(e => e.Time);
                    return group.Count() == 1
                        ? $"{first:HH:mm:ss} · {group.Key}"
                        : $"{group.Count()} événements {group.Key} entre {first:HH:mm:ss} et {last:HH:mm:ss}";
                })
                .ToList();

            foreach (var ev in events)
            {
                foreach (string key in EvidenceKeys)
                {
                    if (!ev.Data.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)) continue;
                    string line = $"{key} : {Shorten(value, 140)}";
                    if (!evidence.Contains(line, StringComparer.OrdinalIgnoreCase)) evidence.Add(line);
                    if (evidence.Count >= 7) return evidence;
                }
            }
            return evidence;
        }

        private static string FindDriveLetter(IEnumerable<RawEvent> events)
        {
            foreach (var ev in events)
            {
                foreach (string value in ev.Data.Values.Append(ev.RawFull))
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var match = Regex.Match(value, @"(?<![A-Za-z])([A-Za-z]):(?:\\|\b)");
                    if (match.Success) return char.ToUpperInvariant(match.Groups[1].Value[0]) + ":";
                }
            }
            return "";
        }

        private static string FirstValue(RawEvent ev, params string[] keys)
        {
            foreach (string key in keys)
                if (ev.Data.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            return "";
        }

        private static bool IsGpuReset(RawEvent ev)
            => ev.Provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)
            || ev.Provider.Contains("amdkmdag", StringComparison.OrdinalIgnoreCase)
            || ev.Provider.Contains("amdwddmg", StringComparison.OrdinalIgnoreCase)
            || (ev.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && ev.Id == 4101)
            || (ev.Provider.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase)
                && ev.Id == 1001
                && ev.RawFull.Contains("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(ev.RawFull, @"P1\s*:\s*141", RegexOptions.IgnoreCase));

        private static string Shorten(string value, int max)
        {
            string clean = Regex.Replace(value, @"\s+", " ").Trim();
            return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
        }
    }
}
