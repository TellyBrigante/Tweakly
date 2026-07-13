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
