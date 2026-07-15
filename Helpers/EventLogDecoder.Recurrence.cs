using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public static partial class EventLogDecoder
    {
        // Strict recurrence filtering keeps scheduled actions out of crash reports.
        private static List<Incident> FilterRecurringScheduled(List<Incident> all)
        {
            if (all.Count < 3) return all;

            string KeyOf(Incident incident)
            {
                int slot = (int)Math.Round(incident.Start.TimeOfDay.TotalMinutes / 15.0);
                string firstWord = (incident.Title ?? "").Split(' ').FirstOrDefault() ?? "";
                return $"{slot}|{firstWord}";
            }

            var groups = all.GroupBy(KeyOf).ToList();
            var toRemove = new HashSet<Incident>();
            foreach (var group in groups)
            {
                int distinctDays = group.Select(i => i.Start.Date).Distinct().Count();
                if (distinctDays < 3) continue;
                foreach (var incident in group) toRemove.Add(incident);
            }
            return all.Where(i => !toRemove.Contains(i)).ToList();
        }

        private static List<Incident> MergeNearbyRepeatedIncidents(List<Incident> all)
        {
            if (all.Count < 2) return all;

            const int mergeWindowSeconds = 10 * 60;
            var merged = new List<Incident>();
            foreach (var incident in all.OrderBy(i => i.Start))
            {
                var previous = merged.LastOrDefault();
                if (previous != null
                    && SameIncidentSignature(previous, incident)
                    && (incident.Start - previous.End).TotalSeconds <= mergeWindowSeconds)
                {
                    MergeInto(previous, incident);
                    continue;
                }

                merged.Add(incident);
            }
            return merged;
        }

        private static List<Incident> MergeRecurringTdrIncidents(List<Incident> all)
        {
            var tdr = all.Where(IsTdrIncident).OrderBy(i => i.Start).ToList();
            if (tdr.Count < 2) return all;

            Incident aggregate = tdr[0];
            foreach (Incident next in tdr.Skip(1)) MergeInto(aggregate, next);

            aggregate.Title = $"Réinitialisations répétées du pilote graphique — {aggregate.Episodes} épisodes";
            aggregate.Conclusion = $"Windows a réinitialisé le pilote graphique à {aggregate.Episodes} reprises. " +
                "Les resets sont confirmés ; leur cause commune doit encore être établie.";

            return all.Where(i => !IsTdrIncident(i) || ReferenceEquals(i, aggregate))
                .ToList();
        }

        private static bool IsTdrIncident(Incident incident)
            => incident.Title.Contains("TDR", StringComparison.OrdinalIgnoreCase)
            || incident.Title.Contains("pilote graphique", StringComparison.OrdinalIgnoreCase)
            || incident.Events.Any(e =>
                e.title.Contains("TDR", StringComparison.OrdinalIgnoreCase)
                || e.title.Contains("LiveKernelEvent 141", StringComparison.OrdinalIgnoreCase));

        private static List<Incident> MergeRecurringApplicationCrashes(List<Incident> all)
        {
            var candidates = all.Where(IsApplicationCrashIncident).ToList();
            var groups = candidates
                .GroupBy(i => EventSignature(i), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0 && group.Count() > 1)
                .ToList();
            if (groups.Count == 0) return all;

            var removed = new HashSet<Incident>();
            foreach (var group in groups)
            {
                var ordered = group.OrderBy(i => i.Start).ToList();
                Incident aggregate = ordered[0];
                foreach (Incident next in ordered.Skip(1))
                {
                    MergeInto(aggregate, next);
                    removed.Add(next);
                }
                string app = ExtractApplicationName(aggregate.Title);
                aggregate.Title = app.Length > 0
                    ? $"Plantages répétés de {app} — {aggregate.Episodes} épisodes"
                    : $"Plantages applicatifs répétés — {aggregate.Episodes} épisodes";
            }
            return all.Where(i => !removed.Contains(i)).ToList();
        }

        private static bool IsApplicationCrashIncident(Incident incident)
            => incident.Events.Any(e => e.title.Contains("Plantage", StringComparison.OrdinalIgnoreCase)
                                     || e.title.Contains("Application Error", StringComparison.OrdinalIgnoreCase));

        private static string ExtractApplicationName(string title)
        {
            var match = Regex.Match(title ?? "", @"(?:de |— )([^—]+?\.exe)(?:\s|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private static bool SameIncidentSignature(Incident a, Incident b)
        {
            if (a.Sev != b.Sev) return false;
            if (!string.Equals(NormalizeKey(a.Title), NormalizeKey(b.Title), StringComparison.OrdinalIgnoreCase))
                return false;
            return string.Equals(EventSignature(a), EventSignature(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string EventSignature(Incident incident)
        {
            return string.Join("|", incident.Events
                .Select(e => NormalizeKey(e.title))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .Take(6));
        }

        private static string NormalizeKey(string value)
            => Regex.Replace(value ?? "", @"\s+", " ").Trim();

        private static void MergeInto(Incident target, Incident next)
        {
            int previousEpisodes = Math.Max(1, target.Episodes);
            target.Episodes = previousEpisodes + Math.Max(1, next.Episodes);
            target.Count += next.Count;
            target.Start = target.Start <= next.Start ? target.Start : next.Start;
            target.End = target.End >= next.End ? target.End : next.End;
            target.Sev = (LogSev)Math.Min((int)target.Sev, (int)next.Sev);

            target.Events.AddRange(next.Events);
            target.Events = target.Events.OrderBy(e => e.time).ToList();

            string prefix = $"Regroupement : {target.Episodes} séquences similaires entre {target.Start:HH:mm:ss} et {target.End:HH:mm:ss}.";
            target.Title = next.Title;
            target.Icon = next.Icon;
            target.Chain = next.Chain.Length > 0 ? next.Chain : target.Chain;
            target.Advice = PrefixOnce(next.Advice.Length > 0 ? next.Advice : target.Advice, prefix);
            target.Steps = next.Steps.Count > 0 ? next.Steps : target.Steps;
            target.Actions = next.Actions.Count > 0 ? next.Actions : target.Actions;
            target.CauseState = next.CauseState;
            target.Conclusion = next.Conclusion.Length > 0 ? next.Conclusion : target.Conclusion;
            target.Evidence = target.Evidence.Concat(next.Evidence)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            target.Repair = next.Repair ?? target.Repair;
            target.Investigation = next.Investigation ?? target.Investigation;
        }

        private static string PrefixOnce(string text, string prefix)
        {
            if (text.StartsWith("Regroupement :", StringComparison.OrdinalIgnoreCase))
            {
                int newline = text.IndexOf('\n');
                return newline >= 0 ? prefix + text.Substring(newline) : prefix;
            }
            return string.IsNullOrWhiteSpace(text) ? prefix : prefix + "\n" + text;
        }
    }
}
