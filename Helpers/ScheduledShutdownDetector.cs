using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Détecte les tâches planifiées Windows qui font un ARRÊT / REDÉMARRAGE / DÉCONNEXION
    /// (shutdown.exe, logoff.exe, Stop-Computer, Restart-Computer, psshutdown, etc.).
    /// Sert au narrateur d'incidents à filtrer les faux positifs : un Kernel-Power 41 ou un
    /// BSOD apparent qui survient EXACTEMENT à l'heure programmée d'une telle tâche est en
    /// fait un arrêt programmé, pas une panne — on ne doit pas l'afficher comme un incident.
    ///
    /// Lecture via `schtasks /query /xml ONE` (XML complet de TOUTES les tâches). Cache pour la
    /// session : les tâches changent rarement, et le spawn schtasks coûte ~150 ms.
    /// </summary>
    public static class ScheduledShutdownDetector
    {
        public sealed class Task
        {
            public string Name = "";
            public TimeSpan TimeOfDay;   // heure du jour du déclencheur (heure locale)
            public string Action = "";   // ex. "shutdown.exe /s /t 0"
            public bool Daily;           // déclencheur quotidien (DailyTrigger ou interval daily)
        }

        private static List<Task>? _cache;
        private static readonly object _lock = new();

        /// <summary>
        /// Renvoie la liste des tâches planifiées dont une action est un arrêt/redémarrage/déconnexion.
        /// Vide si aucune trouvée ou si schtasks a échoué.
        /// </summary>
        public static List<Task> List()
        {
            if (_cache != null) return _cache;
            lock (_lock)
            {
                if (_cache != null) return _cache;
                _cache = Detect();
                return _cache;
            }
        }

        /// <summary>
        /// Trouve une tâche d'arrêt programmé compatible avec l'heure donnée (±toleranceMinutes
        /// par défaut 15 min). Renvoie null si aucune correspondance.
        /// </summary>
        public static Task? MatchAtTime(DateTime when, int toleranceMinutes = 15)
        {
            var todIncident = when.TimeOfDay;
            foreach (var t in List())
            {
                if (!t.Daily) continue;   // on ne fait le match horaire que pour les déclencheurs quotidiens
                double diff = Math.Abs((t.TimeOfDay - todIncident).TotalMinutes);
                // Gérer le wrap-around minuit (00:05 ≈ 23:55)
                if (diff > 12 * 60) diff = 24 * 60 - diff;
                if (diff <= toleranceMinutes) return t;
            }
            return null;
        }

        // ── Parsing schtasks /query /xml ONE ────────────────────────────────────
        // « ONE » concatène toutes les tâches en un seul XML root par tâche.
        // Sortie : <Task xmlns="..."> ... </Task><Task ...> ... </Task>
        // On entoure manuellement d'un root unique pour que XDocument parse l'ensemble.
        private static List<Task> Detect()
        {
            var list = new List<Task>();
            try
            {
                ProcessCommandResult query = ProcessCommand.Run("schtasks", "/query /xml ONE", 10_000);
                if (!query.Success)
                {
                    AppLog.WriteOnce("scheduled-shutdown-query",
                        "Arrêts planifiés : lecture des tâches impossible : " + query.FailureDescription);
                    return list;
                }
                string output = query.Output;
                if (string.IsNullOrWhiteSpace(output)) return list;

                // schtasks /xml ONE produit DEJA un document unique avec un root <Tasks>,
                // mais selon les versions il peut produire plusieurs <Task> à la suite —
                // on entoure défensivement, ça ne casse rien si le wrapper est déjà là.
                string wrapped = "<Tasks>" + output + "</Tasks>";
                XDocument doc;
                try { doc = XDocument.Parse(wrapped); }
                catch
                {
                    // Si l'enveloppage a cassé un root existant, on essaie sans
                    doc = XDocument.Parse(output);
                }

                // Le namespace par défaut des Task XML schtasks
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                foreach (var taskEl in doc.Descendants(ns + "Task"))
                {
                    try
                    {
                        // Nom de la tâche : l'attribut URI dans <RegistrationInfo><URI> (ex. "\Tweakly\AutoStart")
                        var uri = taskEl.Descendants(ns + "URI").FirstOrDefault()?.Value?.Trim() ?? "";

                        // Action(s) : on cherche un <Exec><Command> qui contient un shutdown
                        bool isShutdown = false;
                        string actionStr = "";
                        foreach (var exec in taskEl.Descendants(ns + "Exec"))
                        {
                            var cmd  = exec.Element(ns + "Command")?.Value?.Trim() ?? "";
                            var args = exec.Element(ns + "Arguments")?.Value?.Trim() ?? "";
                            string full = (cmd + " " + args).Trim();
                            if (LooksLikeShutdown(cmd, args))
                            {
                                isShutdown = true;
                                actionStr  = full;
                                break;
                            }
                        }
                        if (!isShutdown) continue;

                        // Déclencheur(s) quotidien(s)
                        foreach (var trig in taskEl.Descendants(ns + "CalendarTrigger"))
                        {
                            var startBoundary = trig.Element(ns + "StartBoundary")?.Value?.Trim() ?? "";
                            bool daily = trig.Element(ns + "ScheduleByDay") != null;
                            if (!daily) continue;
                            if (!DateTime.TryParse(startBoundary, out var dt)) continue;
                            list.Add(new Task
                            {
                                Name = uri.Length > 0 ? uri : "(tâche planifiée)",
                                TimeOfDay = dt.TimeOfDay,
                                Action = actionStr,
                                Daily = true,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.ErrorOnce("scheduled-shutdown-task", "Arrêts planifiés : tâche illisible", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("scheduled-shutdown-detect", "Arrêts planifiés : détection impossible", ex);
            }
            return list;
        }

        // Reconnaissance d'une commande d'arrêt/redémarrage/déconnexion typique
        private static bool LooksLikeShutdown(string cmd, string args)
        {
            string c = (cmd ?? "").ToLowerInvariant();
            string a = (args ?? "").ToLowerInvariant();
            string both = c + " " + a;

            // Exes directs d'arrêt
            if (Regex.IsMatch(c, @"\bshutdown\.exe\b") || c.EndsWith("\\shutdown.exe") || c.EndsWith("/shutdown.exe") || c == "shutdown") return true;
            if (Regex.IsMatch(c, @"\blogoff\.exe\b")   || c.EndsWith("\\logoff.exe")   || c == "logoff")   return true;
            if (Regex.IsMatch(c, @"\bpsshutdown") || Regex.IsMatch(c, @"\bpsshutdown64")) return true;

            // PowerShell / cmd avec args
            bool isShell = Regex.IsMatch(c, @"\b(powershell|pwsh|cmd)\b") || c.EndsWith("\\powershell.exe") || c.EndsWith("\\cmd.exe");
            if (isShell)
            {
                if (a.Contains("stop-computer"))    return true;
                if (a.Contains("restart-computer")) return true;
                if (a.Contains("shutdown /s") || a.Contains("shutdown -s")) return true;
                if (a.Contains("shutdown /r") || a.Contains("shutdown -r")) return true;
                if (a.Contains("shutdown /l") || a.Contains("shutdown -l")) return true;
                if (a.Contains("shutdown /p") || a.Contains("shutdown -p")) return true;
                if (a.Contains("wmic os") && a.Contains("shutdown"))         return true;
            }
            // WMIC direct
            if (c.Contains("wmic") && a.Contains("os") && a.Contains("shutdown")) return true;

            return false;
        }
    }
}
