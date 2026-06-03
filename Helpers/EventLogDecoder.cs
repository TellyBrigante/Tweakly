using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public enum LogSev { Serious, Warning, Benign }

    /// <summary>Une famille d'erreurs (Provider + EventID) décodée en clair (vue « par source »).</summary>
    public class LogEntry
    {
        public string  Provider = "";
        public int     Id;
        public int     Count;
        public DateTime Last;
        public LogSev  Sev   = LogSev.Warning;
        public string  Title = "";
        public string  What  = "";
        public string  Cause = "";
        public string  Fix   = "";
        public string  Raw   = "";
        public bool    Known;
    }

    /// <summary>Un événement individuel (pour la corrélation temporelle).</summary>
    public class RawEvent
    {
        public DateTime Time;
        public string   Provider = "";
        public int      Id;
        public string   Raw = "";
    }

    /// <summary>Un incident = plusieurs événements survenus ensemble, avec cause racine + conseil.</summary>
    public class Incident
    {
        public DateTime Start, End;
        public int      Count;
        public LogSev   Sev = LogSev.Warning;
        public string   Title = "";     // cause racine en clair
        public string   Chain = "";     // enchaînement des events
        public string   Advice = "";    // recommandation raisonnée (multi-lignes)
        public List<(DateTime time, string title, LogSev sev)> Events = new();
    }

    /// <summary>
    /// Lit System + Application (erreurs/critiques) en LECTURE SEULE et :
    ///  • vue « par source » : regroupe par Provider+EventID avec explication/cause/fix ;
    ///  • vue « par incident » : corrèle les events proches dans le temps, déduit la cause racine
    ///    et produit un conseil de correction raisonné. Aucune écriture, aucune dépendance.
    /// </summary>
    public static class EventLogDecoder
    {
        // Lecture brute des events (partagée par les deux vues)
        private static List<RawEvent> ReadRaw(int days)
        {
            long ms = (long)days * 24 * 60 * 60 * 1000;
            string xpath = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            var list = new List<RawEvent>();

            foreach (var log in new[] { "System", "Application" })
            {
                try
                {
                    var query = new EventLogQuery(log, PathType.LogName, xpath);
                    using var reader = new EventLogReader(query);
                    EventRecord? rec; int guard = 0;
                    while ((rec = reader.ReadEvent()) != null && guard++ < 5000)
                    {
                        using (rec)
                        {
                            string raw = "";
                            try
                            {
                                var full = rec.FormatDescription();
                                if (!string.IsNullOrWhiteSpace(full)) raw = full.Split('\n')[0].Trim();
                            }
                            catch { }
                            list.Add(new RawEvent
                            {
                                Time = rec.TimeCreated ?? DateTime.Now,
                                Provider = rec.ProviderName ?? "?",
                                Id = rec.Id,
                                Raw = raw,
                            });
                        }
                    }
                }
                catch { }
            }
            return list.OrderBy(e => e.Time).ToList();
        }

        /// <summary>Vue « par source » : regroupe par Provider+EventID.</summary>
        public static List<LogEntry> Scan(int days)
        {
            var groups = new Dictionary<string, LogEntry>();
            foreach (var ev in ReadRaw(days))
            {
                string key = ev.Provider + "|" + ev.Id;
                if (!groups.TryGetValue(key, out var e))
                {
                    e = Decode(ev.Provider, ev.Id, ev.Raw);
                    e.Last = ev.Time;
                    groups[key] = e;
                }
                e.Count++;
                if (ev.Time > e.Last) e.Last = ev.Time;
            }
            return groups.Values.OrderBy(e => (int)e.Sev).ThenByDescending(e => e.Count).ToList();
        }

        /// <summary>Vue « par incident » : corrèle les events proches dans le temps.</summary>
        public static List<Incident> ScanIncidents(int days, int gapSeconds = 90)
        {
            var all = ReadRaw(days);
            var incidents = new List<Incident>();

            var cluster = new List<RawEvent>();
            DateTime? prev = null;
            foreach (var ev in all)
            {
                if (prev != null && (ev.Time - prev.Value).TotalSeconds > gapSeconds)
                {
                    var inc = Analyze(cluster);
                    if (inc != null) incidents.Add(inc);
                    cluster = new List<RawEvent>();
                }
                cluster.Add(ev);
                prev = ev.Time;
            }
            var last = Analyze(cluster);
            if (last != null) incidents.Add(last);

            return incidents.OrderByDescending(i => i.Start).ToList();
        }

        // ── Analyse d'un cluster → incident (cause racine + conseil) ───────────
        private static Incident? Analyze(List<RawEvent> cluster)
        {
            if (cluster.Count == 0) return null;

            var decoded = cluster
                .Select(e => (e.Time, e.Raw, Info: Decode(e.Provider, e.Id, e.Raw), Kind: KindOf(e.Provider, e.Id)))
                .ToList();

            // On ne retient un incident que s'il y a un vrai signal : >=2 events OU au moins un sérieux
            bool hasSerious = decoded.Any(d => d.Info.Sev == LogSev.Serious);
            if (cluster.Count < 2 && !hasSerious) return null;
            // Cluster purement bénin → pas un incident à mettre en avant
            if (decoded.All(d => d.Info.Sev == LogSev.Benign)) return null;

            // Cause racine = plus haut rang, le plus tôt
            var root = decoded.OrderByDescending(d => Rank(d.Kind)).ThenBy(d => d.Time).First();
            int rootCount = decoded.Count(d => d.Kind == root.Kind);
            int span = (int)Math.Round((decoded.Max(d => d.Time) - decoded.Min(d => d.Time)).TotalSeconds);
            string app = decoded.Select(d => Extract(d.Raw, @"(?:application défaillante|application name)\s*:?\s*([^\s,]+)"))
                                 .FirstOrDefault(s => s.Length > 0) ?? "";

            // Enchaînement (par type, dans l'ordre d'apparition)
            var chainParts = decoded
                .GroupBy(d => d.Info.Title)
                .Select(g => new { Title = g.Key, First = g.Min(x => x.Time), N = g.Count() })
                .OrderBy(x => x.First)
                .Select(x => x.N > 1 ? $"{x.Title} ×{x.N}" : x.Title);
            var inc = new Incident
            {
                Start = decoded.Min(d => d.Time),
                End   = decoded.Max(d => d.Time),
                Count = cluster.Count,
                Sev   = (LogSev)decoded.Min(d => (int)d.Info.Sev),
                Chain = string.Join("   →   ", chainParts),
                Events = decoded.OrderBy(d => d.Time)
                                .Select(d => (d.Time, d.Info.Title, d.Info.Sev)).ToList(),
            };
            (inc.Title, inc.Advice) = Recommend(root.Kind, root.Info, rootCount, span, app, decoded.Count);
            return inc;
        }

        private enum Kind { Gpu, Whea, Power, Disk, AppCrash, Service, Benign, Other }

        private static Kind KindOf(string prov, int id)
        {
            var p = prov.ToLowerInvariant();
            if (p.Contains("nvlddmkm"))               return Kind.Gpu;
            if (p.Contains("whea"))                   return Kind.Whea;
            if (p.Contains("kernel-power"))           return id == 41 ? Kind.Power : Kind.Other;
            if (p == "disk" || p.Contains("ntfs"))    return Kind.Disk;
            if (p == "application error" || p == ".net runtime") return Kind.AppCrash;
            if (p == "service control manager")       return Kind.Service;
            if (p.Contains("distributedcom") || p.Contains("perflib") || p.Contains("user profiles")) return Kind.Benign;
            return Kind.Other;
        }

        // Plus le rang est élevé, plus l'event est une CAUSE (vs un effet)
        private static int Rank(Kind k) => k switch
        {
            Kind.Whea => 5, Kind.Disk => 5, Kind.Power => 4, Kind.Gpu => 4,
            Kind.Service => 2, Kind.AppCrash => 2, Kind.Other => 1, _ => 0,
        };

        // Recommandation raisonnée selon la cause racine + les preuves
        private static (string title, string advice) Recommend(
            Kind k, LogEntry root, int rootCount, int span, string app, int total)
        {
            string rafale = rootCount >= 3 ? $" {rootCount}× en {Math.Max(span,1)} s — ce n'est pas un hoquet isolé, ça a vraiment lâché"
                                           : (rootCount > 1 ? $" {rootCount}×" : "");
            string appPart = app.Length > 0 ? $", et ça a entraîné le crash de {app}" : "";

            switch (k)
            {
                case Kind.Gpu:
                    return ("Effondrement du pilote graphique" + (rootCount >= 3 ? " (en rafale)" : ""),
                        $"Le pilote GPU a cessé de répondre{rafale}{appPart}.\n" +
                        "Plan conseillé, du plus probable au moins probable :\n" +
                        "1) Réinstaller le pilote NVIDIA PROPREMENT : DDU en mode sans échec, puis une version STABLE (pas forcément la dernière — plusieurs récentes ont causé des TDR).\n" +
                        "2) Surveiller la température GPU en charge (onglet Monitoring) ; au-delà de ~83 °C, revoir le flux d'air / la pâte thermique.\n" +
                        "3) Retirer tout overclock GPU (Afterburner) ; un léger UNDERVOLT stabilise souvent les TDR.\n" +
                        "4) Si ça persiste : suspecter l'alimentation (PSU faible/âgé → TDR sur les pics de charge) et tester le GPU sur un autre PC.");

                case Kind.Whea:
                    return ("Erreur matérielle (WHEA)",
                        "Le matériel a signalé une erreur (CPU / RAM / lien PCIe). À prendre au sérieux.\n" +
                        "1) Tester la RAM avec MemTest86 (plusieurs passes).\n" +
                        "2) Désactiver XMP/EXPO ou tout overclock (CPU/BCLK) : si les erreurs cessent, c'était le profil mémoire / l'OC.\n" +
                        "3) Vérifier températures et alimentation.\n" +
                        "4) Si l'erreur est « non corrigée » et récurrente : composant probablement défaillant (RAM/CPU/carte mère).");

                case Kind.Power:
                    return ("Arrêt brutal du PC",
                        "Le PC s'est éteint/redémarré sans arrêt propre.\n" +
                        "1) Croiser avec les BSOD (onglet Diagnostic) : s'il y a un BSOD au même moment → c'est un plantage logiciel/pilote, PAS une coupure de courant.\n" +
                        "2) Vérifier les températures CPU/GPU sous charge.\n" +
                        "3) Tester une autre prise ; suspecter le PSU s'il est vieux ou sous-dimensionné.\n" +
                        "4) Retirer tout overclock.");

                case Kind.Disk:
                    return ("Problème disque / système de fichiers",
                        "Windows a rencontré une erreur disque — risque pour tes données.\n" +
                        "1) Vérifier la santé SMART du disque (onglet Diagnostic) ET sauvegarder l'important MAINTENANT.\n" +
                        "2) chkdsk /f sur le volume concerné.\n" +
                        "3) Remplacer le câble SATA (cause fréquente et bénigne) ; si le SMART est dégradé → remplacer le disque.");

                case Kind.AppCrash:
                    string who = app.Length > 0 ? $" « {app} »" : "";
                    if (rootCount >= 3)
                        return ($"Application en crash-boucle{(app.Length > 0 ? " — " + app : "")}",
                            $"L'application{who} a planté {rootCount}× d'affilée (elle crashe, redémarre, recrashe).\n" +
                            "1) La mettre à jour, ou la réinstaller proprement.\n" +
                            (app.Length > 0 ? $"2) Si elle n'est pas essentielle, la désinstaller ({app}).\n" : "2) Si elle n'est pas essentielle, la désinstaller.\n") +
                            "3) Si le module fautif est un composant système (ntdll, kernelbase…), vérifier la RAM (MemTest) + Windows Update.");
                    return ($"Plantage d'application{(app.Length > 0 ? " — " + app : "")}",
                        $"Une application{who} a planté.\n" +
                        "1) La mettre à jour / réinstaller.\n" +
                        "2) Surveiller : si ça se répète souvent, la désinstaller si elle n'est pas indispensable.");

                case Kind.Service:
                    return ("Service en échec (effet d'un crash)",
                        "Un service a été signalé en échec, juste autour d'un crash d'application — c'est en général la CONSÉQUENCE, pas la cause.\n" +
                        "Concentre-toi sur l'appli/service concerné (voir le détail) : le mettre à jour, ou le désactiver s'il est inutile (agent constructeur, télémétrie tierce…).");

                default:
                    return (root.Title,
                        root.Fix.Length > 0 ? root.Fix : "Voir le détail des événements ci-dessous et rechercher le contexte sur le web.");
            }
        }

        // ── Base de connaissances « par source » (clé = Provider + EventID) ────
        private static LogEntry Decode(string prov, int id, string raw)
        {
            var e = new LogEntry { Provider = prov, Id = id, Raw = raw, Known = true };
            string p = prov.ToLowerInvariant();

            if (p.Contains("nvlddmkm"))
            {
                e.Sev = LogSev.Serious;
                e.Title = "Pilote graphique NVIDIA — perte de réponse (TDR)";
                e.What  = "Le GPU a cessé de répondre puis a été réinitialisé par Windows (Timeout Detection & Recovery).";
                e.Cause = "Pilote instable ou version buguée · overclock GPU · surchauffe · alimentation insuffisante · parfois un jeu/appli précis.";
                e.Fix   = "Réinstaller proprement le pilote NVIDIA (DDU puis version stable), vérifier les températures GPU, retirer l'overclock, tester une autre version.";
                return e;
            }

            switch (p)
            {
                case "application error":
                    e.Sev = LogSev.Warning;
                    var app = Extract(raw, @"(?:application défaillante|application name)\s*:?\s*([^\s,]+)");
                    e.Title = app.Length > 0 ? $"Plantage d'application — {app}" : "Plantage d'application";
                    e.What  = "Une application s'est arrêtée à cause d'une erreur (crash).";
                    e.Cause = "Bug de l'application ou d'un de ses modules. Si le module fautif est système (ntdll, kernelbase…), penser RAM / mises à jour.";
                    e.Fix   = app.Length > 0 ? $"Mettre à jour ou réinstaller « {app} » ; la désinstaller si inutile et récurrente." : "Mettre à jour / réinstaller l'application concernée.";
                    return e;

                case ".net runtime":
                    e.Sev = LogSev.Warning;
                    e.Title = "Plantage d'une application .NET";
                    e.What  = "Une application .NET a levé une exception non gérée et s'est fermée.";
                    e.Cause = "Bug de l'application .NET concernée.";
                    e.Fix   = "Mettre à jour l'application ; vérifier que .NET est à jour (Windows Update).";
                    return e;

                case "service control manager":
                    var svc = Extract(raw, @"(?:Le service|The)\s+(.+?)\s+(?:s['’]est|service)");
                    e.Title = svc.Length > 0 ? $"Service en échec — {svc}" : "Service Windows en échec";
                    e.Sev   = LogSev.Warning;
                    e.What  = id switch
                    {
                        7031 => "Un service s'est arrêté de façon inattendue (il a planté).",
                        7034 => "Un service s'est arrêté de façon inattendue.",
                        7000 => "Un service n'a pas pu démarrer.",
                        7009 => "Délai dépassé en attendant le démarrage d'un service.",
                        7011 => "Délai de réponse dépassé pour un service.",
                        _    => "Problème de service signalé par le gestionnaire de services.",
                    };
                    e.Cause = "Le service tiers ou Windows associé a planté ou mis trop de temps à répondre.";
                    e.Fix   = "Souvent bénin si c'est un service tiers ; mettre à jour le logiciel associé, ou le désactiver si inutile. Si service Windows critique : sfc /scannow.";
                    return e;

                case "volsnap":
                    e.Sev = LogSev.Warning;
                    e.Title = "Clichés instantanés (restauration) supprimés";
                    e.What  = "Windows a supprimé des points de restauration / clichés VSS faute de place.";
                    e.Cause = "Espace réservé aux clichés trop petit, ou disque lent/saturé.";
                    e.Fix   = "Augmenter l'espace (Propriétés système → Protection du système → Configurer), libérer de la place, ou vérifier la santé du disque.";
                    return e;

                case "microsoft-windows-whea-logger":
                    e.Sev = LogSev.Serious;
                    e.Title = "Erreur matérielle (WHEA)";
                    e.What  = "Le matériel a signalé une erreur (CPU, RAM, cache, lien PCIe…).";
                    e.Cause = "RAM instable · overclock · surchauffe · alimentation · périphérique PCIe.";
                    e.Fix   = "Tester la RAM (MemTest86), retirer overclock/XMP douteux, vérifier températures et alimentation.";
                    return e;

                case "microsoft-windows-kernel-power":
                    e.Sev = id == 41 ? LogSev.Serious : LogSev.Warning;
                    e.Title = id == 41 ? "Arrêt/redémarrage inattendu (Kernel-Power 41)" : "Événement alimentation noyau";
                    e.What  = "Le PC s'est éteint/redémarré sans arrêt propre.";
                    e.Cause = "Coupure d'alimentation · surchauffe · PSU défaillante · BSOD non enregistré · overclock instable.";
                    e.Fix   = "Vérifier alimentation/températures, retirer l'overclock, croiser avec les BSOD (Diagnostic).";
                    return e;

                case "disk":
                case "ntfs":
                case "microsoft-windows-ntfs":
                    e.Sev = LogSev.Serious;
                    e.Title = "Erreur disque / système de fichiers";
                    e.What  = "Windows a rencontré une erreur d'entrée-sortie ou de cohérence sur un disque.";
                    e.Cause = "Câble SATA · disque vieillissant / secteurs défectueux · coupure pendant écriture.";
                    e.Fix   = "Vérifier la santé SMART (Diagnostic), chkdsk, sauvegarder, remplacer câble ou disque si récurrent.";
                    return e;

                case "microsoft-windows-distributedcom":
                    e.Sev = LogSev.Benign;
                    e.Title = "DCOM — délai/permission (bruit)";
                    e.What  = "Un composant COM ne s'est pas enregistré à temps, ou une permission manquait.";
                    e.Cause = "Course au démarrage de Windows, service lent. Aucun impact perceptible.";
                    e.Fix   = "Généralement inoffensif — aucune action nécessaire.";
                    return e;

                case "microsoft-windows-perflib":
                    e.Sev = LogSev.Benign;
                    e.Title = "Compteur de performance (bruit)";
                    e.What  = "Un compteur de performance d'un service n'a pas pu être chargé/déchargé.";
                    e.Cause = "Service tiers mal désinstallé ou en cours d'initialisation.";
                    e.Fix   = "Généralement inoffensif.";
                    return e;

                case "microsoft-windows-user profiles service":
                    e.Sev = LogSev.Benign;
                    e.Title = "Service de profils utilisateur (mineur)";
                    e.What  = "Avertissement lors du chargement/déchargement du profil.";
                    e.Cause = "Fichier de profil verrouillé à la fermeture de session.";
                    e.Fix   = "Mineur.";
                    return e;
            }

            e.Known = false;
            e.Sev   = LogSev.Warning;
            e.Title = prov;
            e.What  = $"Erreur signalée par « {prov} » (EventID {id}).";
            e.Cause = "Source non répertoriée par Tweakly — voir le détail brut.";
            e.Fix   = "Rechercher « " + prov + " " + id + " » sur le web.";
            return e;
        }

        private static string Extract(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups.Count > 1) return m.Groups[1].Value.Trim().Trim(',', '.', ';');
            }
            catch { }
            return "";
        }
    }
}
