using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public enum LogSev { Serious, Warning, Benign }

    /// <summary>
    /// Type d'action déclenchable depuis une carte EventLog.
    ///   Navigate = ouvrir un onglet interne de Tweakly (target = clé de page).
    ///   Command  = lancer une commande système (target = ligne de commande à exécuter).
    ///   Url      = ouvrir une page web (target = URL).
    ///   Diag     = lancer un utilitaire Windows (Observateur d'événements, Gestionnaire
    ///              de périphériques, msinfo32…). target = ms-settings / exe à lancer.
    /// </summary>
    public enum LogActionKind { Navigate, Command, Url, Diag }

    /// <summary>Bouton d'action affiché sous la carte (Steps).</summary>
    public class LogAction
    {
        public string        Label   = "";   // Texte du bouton (court)
        public string        Tooltip = "";   // Explication au survol (peut détailler la cmd)
        public LogActionKind Kind    = LogActionKind.Navigate;
        public string        Target  = "";   // Tag de page / commande / URL
        public bool          Confirm = false; // Demander confirmation avant exécution (cmd longues / risquées)
    }

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
        public string  Fix   = "";   // Texte libre (fallback si Steps est vide)
        public string  Raw   = "";
        public bool    Known;

        // -- Enrichissements v1.3.0 (clarté visuelle + actions concrètes) -----
        /// <summary>Glyphe Segoe MDL2 affiché à gauche du titre (ex. "" pour GPU).</summary>
        public string  Icon  = "";
        /// <summary>Étapes numérotées concrètes, affichées comme une vraie liste (1. 2. 3.).
        /// Si non vide, remplace l'affichage de Fix en bloc.</summary>
        public List<string>    Steps   = new();
        /// <summary>Boutons d'action affichés en pied de carte (navigation interne, cmd, URL).</summary>
        public List<LogAction> Actions = new();
    }

    /// <summary>Un événement individuel (pour la corrélation temporelle).</summary>
    public class RawEvent
    {
        public DateTime Time;
        public string   Provider = "";
        public int      Id;
        public string   Raw = "";       // 1re ligne du message (affichage compact)
        public string   RawFull = "";   // message COMPLET (analyse : chemin du fautif, etc.)
                                        // ⚠️ piège vécu : n'analyser QUE Raw = rater tout ce qui
                                        // est après la 1re ligne (le chemin de l'exe est ligne ~7)
    }

    /// <summary>Un incident = plusieurs événements survenus ensemble, avec cause racine + conseil.</summary>
    public class Incident
    {
        public DateTime Start, End;
        public int      Count;
        public LogSev   Sev = LogSev.Warning;
        public string   Title = "";     // cause racine en clair
        public string   Chain = "";     // enchaînement des events
        public string   Advice = "";    // recommandation raisonnée (texte libre, fallback)
        public List<(DateTime time, string title, LogSev sev)> Events = new();

        // -- Enrichissements v1.3.0 (clarté visuelle + actions concrètes) -----
        public string          Icon    = "";
        public List<string>    Steps   = new();
        public List<LogAction> Actions = new();
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
                            string raw = "", rawFull = "";
                            try
                            {
                                var full = rec.FormatDescription();
                                if (!string.IsNullOrWhiteSpace(full))
                                {
                                    rawFull = full.Trim();
                                    raw     = rawFull.Split('\n')[0].Trim();
                                }
                            }
                            catch { }
                            list.Add(new RawEvent
                            {
                                Time = rec.TimeCreated ?? DateTime.Now,
                                Provider = rec.ProviderName ?? "?",
                                Id = rec.Id,
                                Raw = raw,
                                RawFull = rawFull,
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

            // ── RÉCURRENCE par type de problème (v1.3.3) ────────────────────────
            // Nb de JOURS DISTINCTS où chaque Kind apparaît sur la période. C'est ce
            // qui pilote l'intensité des conseils : un TDR GPU sur UN seul soir et
            // jamais reproduit ≠ des TDR étalés sur 3 jours. Avant, on conseillait
            // « DDU + check PSU » même pour un épisode ponctuel → générique/inutile
            // pour un utilisateur qui a déjà une config saine.
            var recurrenceDays = all
                .GroupBy(e => KindOf(e.Provider, e.Id))
                .ToDictionary(g => g.Key, g => g.Select(e => e.Time.Date).Distinct().Count());

            var cluster = new List<RawEvent>();
            DateTime? prev = null;
            foreach (var ev in all)
            {
                if (prev != null && (ev.Time - prev.Value).TotalSeconds > gapSeconds)
                {
                    var inc = Analyze(cluster, recurrenceDays);
                    if (inc != null) incidents.Add(inc);
                    cluster = new List<RawEvent>();
                }
                cluster.Add(ev);
                prev = ev.Time;
            }
            var last = Analyze(cluster, recurrenceDays);
            if (last != null) incidents.Add(last);

            return incidents.OrderByDescending(i => i.Start).ToList();
        }

        // ── Analyse d'un cluster → incident (cause racine + conseil) ───────────
        private static Incident? Analyze(List<RawEvent> cluster, Dictionary<Kind, int>? recurrenceDays = null)
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
            // v1.3.7 : CHEMIN COMPLET de l'exe fautif (présent dans l'événement 1000 —
            // « Chemin d'accès de l'application défaillante » / "Faulting application path").
            // C'est lui qui rend le conseil ACTIONNABLE : on sait OÙ est le programme et
            // donc À QUI il appartient (retour utilisateur : « je le trouve pas »).
            // ⚠️ Libellé FR VARIABLE selon les versions de Windows : « Chemin de l'application
            // défaillante » (vérifié en réel sur build 26200) OU « Chemin d'accès de
            // l'application défaillante » (anciennes versions) — les deux sont couverts.
            string appPath = cluster.Select(ev => Extract(ev.RawFull,
                                 @"(?:Chemin (?:d['’]acc[eè]s )?de l['’]application défaillante|Faulting application path)\s*:?\s*([^\r\n]+?\.exe)"))
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
            int recDays  = recurrenceDays != null && recurrenceDays.TryGetValue(root.Kind, out var rd) ? rd : 1;
            double daysAgo = Math.Max(0, (DateTime.Now - inc.End).TotalDays);
            Recommend(root.Kind, root.Info, rootCount, span, app, decoded.Count, inc, recDays, daysAgo, appPath);
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

        // Recommandation raisonnée selon la cause racine + les preuves.
        // v1.3.3 : SENSIBLE À LA RÉCURRENCE (recDays = jours distincts avec ce type de
        // problème sur la période, daysAgo = ancienneté de l'incident) et au CONTEXTE
        // machine (version du driver NVIDIA détectée). Un épisode isolé n'appelle pas
        // les mêmes actions qu'un problème récurrent — fini le « DDU + check ton alim »
        // pour un hoquet ponctuel.
        private static void Recommend(
            Kind k, LogEntry root, int rootCount, int span, string app, int total, Incident inc,
            int recDays = 1, double daysAgo = 0, string appPath = "")
        {
            string rafale = rootCount >= 3 ? $" {rootCount}× en {Math.Max(span,1)} s — ce n'est pas un hoquet isolé, ça a vraiment lâché"
                                           : (rootCount > 1 ? $" {rootCount}×" : "");
            string appPart = app.Length > 0 ? $", et ça a entraîné le crash de {app}" : "";

            switch (k)
            {
                case Kind.Gpu:
                {
                    var drv = GetNvidiaDriverVersion();
                    string drvLine = drv.Length > 0 ? $" Pilote installé : NVIDIA {drv}." : "";

                    if (recDays <= 1)
                    {
                        // ÉPISODE ISOLÉ (un seul jour concerné sur toute la période) → pas
                        // d'action lourde, quel que soit son âge.
                        string since = daysAgo >= 2 ? $"rien depuis {Math.Floor(daysAgo)} j"
                                     : daysAgo >= 1 ? "rien depuis"
                                     : "aucune récidive pour l'instant";
                        inc.Title  = "Plantage ponctuel du pilote graphique";
                        inc.Icon   = "";
                        inc.Advice = $"Le pilote GPU a planté{rafale}{appPart} — mais c'est un épisode ISOLÉ "
                                   + $"(un seul jour concerné sur la période, {since}).{drvLine} "
                                   + "Un pilote peut crasher une fois (un jeu précis, une sortie de veille, un pic ponctuel) "
                                   + "sans que ta config soit en cause. Inutile de sortir l'artillerie.";
                        inc.Steps  = new List<string>
                        {
                            "Rien d'urgent : note ce que tu faisais à ce moment-là (quel jeu / quelle appli) et surveille si ça revient.",
                            "Si ça se reproduit DANS LE MÊME jeu : le coupable est ce jeu ou le pilote pour ce jeu — pas ton matériel.",
                            drv.Length > 0
                                ? $"Si ça se reproduit dans plusieurs jeux : bascule du pilote {drv} vers le Studio Driver équivalent (plus conservateur que le Game Ready)."
                                : "Si ça se reproduit dans plusieurs jeux : teste le Studio Driver (plus conservateur que le Game Ready).",
                            "Relance une analyse ici dans quelques jours : si cette carte ne réapparaît pas, classe l'affaire.",
                        };
                        inc.Actions = new List<LogAction>
                        {
                            new LogAction { Label = "Voir Monitoring", Tooltip = "Températures GPU en temps réel.",          Kind = LogActionKind.Navigate, Target = "Monitoring" },
                            new LogAction { Label = "Pilotes NVIDIA",  Tooltip = "Page officielle de téléchargement.",       Kind = LogActionKind.Url,      Target = "https://www.nvidia.fr/Download/index.aspx?lang=fr" },
                        };
                        return;
                    }

                    // RÉCURRENT (plusieurs jours distincts) → escalade complète.
                    inc.Title  = $"Effondrements répétés du pilote graphique ({recDays} jours concernés)";
                    inc.Icon   = "";
                    inc.Advice = $"Le pilote GPU a cessé de répondre{rafale}{appPart} — et ce n'est pas la première fois : "
                               + $"des TDR sur {recDays} jours différents de la période analysée.{drvLine} "
                               + "Là, c'est un vrai problème à traiter.";
                    inc.Steps  = new List<string>
                    {
                        drv.Length > 0
                            ? $"Désinstaller le pilote {drv} proprement avec DDU en mode sans échec, puis réinstaller une version STABLE (Studio Driver, ou la version précédente)."
                            : "Désinstaller le pilote graphique proprement avec DDU en mode sans échec, puis réinstaller une version STABLE.",
                        "Tweakly > Surveillance > Monitoring système : vérifier la température GPU sous charge. Au-delà de ~83 °C, revoir flux d'air / pâte thermique.",
                        "Retirer tout overclock GPU le temps du diagnostic (même un undervolt « stable » peut devenir limite avec un nouveau pilote — re-valide-le).",
                        "Si ça persiste après tout ça : suspecter l'alimentation (TDR sur les pics de charge) ou un problème matériel GPU — tester sur un autre PC si possible.",
                    };
                    inc.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Monitoring",   Tooltip = "Températures GPU en temps réel.",     Kind = LogActionKind.Navigate, Target = "Monitoring" },
                        new LogAction { Label = "Télécharger DDU",   Tooltip = "Page Wagnardsoft (DDU officiel).",     Kind = LogActionKind.Url,      Target = "https://www.wagnardsoft.com/" },
                        new LogAction { Label = "Pilotes NVIDIA",    Tooltip = "Page officielle de téléchargement.",   Kind = LogActionKind.Url,      Target = "https://www.nvidia.fr/Download/index.aspx?lang=fr" },
                    };
                    return;
                }

                case Kind.Whea:
                    inc.Title  = "Erreur matérielle (WHEA)";
                    inc.Icon   = "";
                    inc.Advice = "Le matériel a signalé une erreur (CPU / RAM / lien PCIe). À prendre au sérieux.";
                    inc.Steps  = new List<string>
                    {
                        "Désactiver XMP/EXPO dans le BIOS et revenir aux fréquences JEDEC par défaut.",
                        "Retirer tout overclock CPU/BCLK.",
                        "Tester la RAM avec MemTest86 (4 passes minimum).",
                        "Tweakly > Surveillance > Monitoring système : vérifier températures CPU/GPU.",
                        "Si l'erreur revient régulièrement et est « non corrigée » : composant probablement défaillant.",
                    };
                    inc.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Monitoring",     Tooltip = "Températures matérielles.",                  Kind = LogActionKind.Navigate, Target = "Monitoring" },
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "SMART disques, BSOD, stabilité système.",   Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "MemTest86",           Tooltip = "Page officielle PassMark MemTest86.",        Kind = LogActionKind.Url,      Target = "https://www.memtest86.com/" },
                    };
                    return;

                case Kind.Power:
                    if (recDays <= 1)
                    {
                        // UN arrêt brutal isolé : souvent un reset volontaire (bouton),
                        // une coupure secteur ponctuelle ou un plantage unique.
                        inc.Title  = "Arrêt brutal isolé";
                        inc.Icon   = "";
                        inc.Advice = "Le PC s'est éteint sans arrêt propre — un seul jour concerné sur la période, "
                                   + "pas de récidive. Si c'était toi (reset, bouton power maintenu, coupure de courant), "
                                   + "c'est simplement la trace de cet événement : rien à faire.";
                        inc.Steps  = new List<string>
                        {
                            "Si tu te souviens de la cause (reset volontaire, orage, multiprise débranchée) : ignore cette carte.",
                            "Sinon : Tweakly > Diagnostic > Bilan de santé pour voir si un BSOD coïncide avec cette heure-là.",
                            "Surveille : c'est la RÉPÉTITION des arrêts brutaux qui est un signal matériel, pas un épisode unique.",
                        };
                        inc.Actions = new List<LogAction>
                        {
                            new LogAction { Label = "Voir Bilan de santé", Tooltip = "BSOD, stabilité système.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        };
                        return;
                    }
                    inc.Title  = $"Arrêts brutaux répétés ({recDays} jours concernés)";
                    inc.Icon   = "";
                    inc.Advice = $"Le PC s'est éteint/redémarré sans arrêt propre sur {recDays} jours différents — ce n'est plus un accident, il faut chercher la cause.";
                    inc.Steps  = new List<string>
                    {
                        "Tweakly > Diagnostic > Bilan de santé : regarder si des BSOD coïncident. Si oui → plantage logiciel/pilote, PAS coupure de courant.",
                        "Tweakly > Surveillance > Monitoring système : températures CPU/GPU sous charge (un arrêt d'urgence thermique est brutal et silencieux).",
                        "Tester une autre prise murale ; PC portable : tester sans la batterie.",
                        "Si tour : suspecter la PSU (vieille >5 ans, ou sous-dimensionnée pour le GPU).",
                        "Retirer overclock CPU/GPU/RAM (XMP off) pour isoler.",
                    };
                    inc.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "BSOD, stabilité système.",                   Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "Voir Monitoring",     Tooltip = "Températures matérielles.",                  Kind = LogActionKind.Navigate, Target = "Monitoring" },
                    };
                    return;

                case Kind.Disk:
                    inc.Title  = "Problème disque / système de fichiers";
                    inc.Icon   = "";
                    inc.Advice = "Windows a rencontré une erreur disque — RISQUE pour tes données.";
                    inc.Steps  = new List<string>
                    {
                        "SAUVEGARDER MAINTENANT les fichiers importants sur un autre disque/cloud.",
                        "Tweakly > Diagnostic > Bilan de santé : vérifier la santé SMART et l'usure SSD.",
                        "Lancer chkdsk /f sur le volume concerné (reboot nécessaire pour le volume système).",
                        "Si disque SATA : remplacer le câble SATA (3 €, cause fréquente et bénigne).",
                        "Si SMART dégradé ou erreurs persistantes : remplacer le disque.",
                    };
                    inc.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "SMART + usure SSD.",                                                            Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "Lancer CHKDSK C:",    Tooltip = "Vérifie et répare le volume C: (reboot nécessaire pour le volume système).",   Kind = LogActionKind.Command,  Target = "cmd /k chkdsk C: /f", Confirm = true },
                    };
                    return;

                case Kind.AppCrash:
                {
                    string who = app.Length > 0 ? $" « {app} »" : "";

                    // ── v1.3.7 : IDENTITÉ et LOCALISATION du fautif (retour utilisateur :
                    // « tu me dis de le désinstaller mais je le trouve pas »). Le chemin vient
                    // de l'événement ; le propriétaire est lu DANS le fichier (FileVersionInfo).
                    string owner = "", locLine = "";
                    bool fileExists = false;
                    // v1.3.7-bis : si l'exe fautif est un SERVICE Windows, Tweakly (élevé) peut
                    // l'arrêter/le désactiver LUI-MÊME — fini le « ouvre services.msc » (retour
                    // utilisateur : « t'as géré le souci pour moi seul, l'app doit le faire »).
                    var (svcName, svcDisplay) = ("", "");
                    if (appPath.Length > 0)
                    {
                        try
                        {
                            fileExists = System.IO.File.Exists(appPath);
                            if (fileExists)
                            {
                                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(appPath);
                                var prod = (fvi.ProductName  ?? "").Trim();
                                var comp = (fvi.CompanyName  ?? "").Trim();
                                if (prod.Length > 0 || comp.Length > 0)
                                    owner = prod.Length > 0 && comp.Length > 0 ? $"{prod} ({comp})"
                                          : prod.Length > 0 ? prod : comp;
                            }
                        }
                        catch { }
                        (svcName, svcDisplay) = FindServiceByExe(appPath);
                        locLine = fileExists
                            ? $" Le fichier est ici : {appPath}" + (owner.Length > 0 ? $" — il appartient à « {owner} »." : ".")
                            : $" Le fichier n'existe plus sur le disque ({appPath}) : le programme a probablement été désinstallé, mais un RÉSIDU essaie encore de le lancer (démarrage automatique ou tâche planifiée).";
                        if (svcName.Length > 0)
                            locLine += $" C'est un SERVICE Windows (« {svcDisplay} ») : Windows le relancera en boucle tant qu'il n'est pas désactivé.";
                    }

                    if (rootCount >= 3)
                    {
                        inc.Title  = $"Application en crash-boucle{(app.Length > 0 ? " — " + app : "")}";
                        inc.Icon   = "";
                        inc.Advice = $"L'application{who} a planté {rootCount}× d'affilée (elle crashe, redémarre, recrashe).{locLine}";
                        inc.Steps  = new List<string>();
                        if (fileExists)
                        {
                            inc.Steps.Add(owner.Length > 0
                                ? $"Le coupable appartient à « {owner} » : mets ce logiciel à jour, ou désinstalle-le s'il ne te sert pas (cherche « {owner} » dans Programmes et fonctionnalités)."
                                : "Clique « Ouvrir l'emplacement » : le nom du dossier t'indique à quel logiciel il appartient — mets-le à jour ou désinstalle-le.");
                            inc.Steps.Add("Si tu le gardes : désinstalle puis réinstalle depuis le site officiel (réparation propre).");
                        }
                        else if (appPath.Length > 0)
                        {
                            inc.Steps.Add("Le fichier a déjà disparu : c'est un résidu qui le relance. Tweakly > Optimisations > Nettoyage > « Résidus de logiciels désinstallés », puis vérifie les programmes au démarrage.");
                        }
                        else
                        {
                            inc.Steps.Add("Tweakly > Boîte à outils > Applications : chercher l'appli concernée et la mettre à jour.");
                        }
                        inc.Steps.Add("Si le module fautif est système (ntdll, kernelbase…) : tester la RAM (MemTest86) et lancer Windows Update.");
                    }
                    else if (recDays <= 1)
                    {
                        // Crash unique, pas reproduit → ne pas en faire un drame.
                        inc.Title  = $"Plantage ponctuel{(app.Length > 0 ? " — " + app : "")}";
                        inc.Icon   = "";
                        inc.Advice = $"Une application{who} a planté — un seul jour concerné sur la période.{locLine} "
                                   + "Les applications plantent parfois, ce n'est pas un signal en soi : aucune action nécessaire si ça ne se répète pas.";
                        inc.Steps  = new List<string>
                        {
                            "Rien à faire pour l'instant — relance une analyse dans quelques jours.",
                            "Si la même appli replante régulièrement : la mettre à jour ou la réinstaller.",
                        };
                    }
                    else
                    {
                        inc.Title  = $"Plantage d'application{(app.Length > 0 ? " — " + app : "")}";
                        inc.Icon   = "";
                        inc.Advice = (recDays >= 3
                            ? $"Une application{who} a planté — et des crashs d'applis reviennent sur {recDays} jours différents de la période. À traiter."
                            : $"Une application{who} a planté.") + locLine;
                        inc.Steps  = new List<string>
                        {
                            owner.Length > 0
                                ? $"Le programme appartient à « {owner} » : mets-le à jour (ou désinstalle-le s'il ne sert pas)."
                                : "Tweakly > Boîte à outils > Applications : mettre à jour l'appli concernée.",
                            !fileExists && appPath.Length > 0
                                ? "Fichier déjà disparu = résidu : Nettoyage > « Résidus de logiciels désinstallés » + vérifier le démarrage automatique."
                                : "Surveiller : si ça se répète souvent, désinstaller si non essentielle.",
                        };
                    }

                    inc.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Applications",            Tooltip = "Liste des applis dans Tweakly.",                       Kind = LogActionKind.Navigate, Target = "Apps" },
                        new LogAction { Label = "Programmes et fonctionnalités", Tooltip = "Ouvre appwiz.cpl pour désinstaller des programmes.", Kind = LogActionKind.Diag,    Target = "appwiz.cpl" },
                    };
                    if (fileExists)
                        inc.Actions.Insert(0, new LogAction
                        {
                            Label = "Ouvrir l'emplacement", Tooltip = appPath,
                            // ⚠️ Kind=Command (file + args splittés), PAS Diag : Diag passe la
                            // chaîne ENTIÈRE comme nom de fichier → échec silencieux (vécu).
                            Kind = LogActionKind.Command, Target = $"explorer /select,\"{appPath}\"",
                        });
                    else if (appPath.Length > 0)
                        inc.Actions.Add(new LogAction
                        {
                            Label = "Nettoyer les résidus", Tooltip = "Tweakly > Nettoyage (résidus de logiciels désinstallés).",
                            Kind = LogActionKind.Navigate, Target = "Nettoyage",
                        });
                    // Service fautif → action DIRECTE (Tweakly est élevé, sc fonctionne) :
                    // arrêt + désactivation, fenêtre cmd visible pour montrer le résultat,
                    // confirmation demandée avant. Le dossier devient supprimable derrière.
                    if (svcName.Length > 0)
                        inc.Actions.Insert(0, new LogAction
                        {
                            Label   = "Arrêter et désactiver le service",
                            Tooltip = $"sc stop + sc config start=disabled sur « {svcDisplay} » ({svcName}). Réversible : services.msc.",
                            Kind    = LogActionKind.Command, Confirm = true,
                            Target  = $"cmd /k sc stop \"{svcName}\" & sc config \"{svcName}\" start= disabled",
                        });
                    return;
                }

                case Kind.Service:
                    inc.Title  = "Service en échec (effet d'un crash)";
                    inc.Icon   = "";
                    inc.Advice = "Un service a été signalé en échec, juste autour d'un crash d'application — c'est en général la CONSÉQUENCE, pas la cause.";
                    inc.Steps  = new List<string>
                    {
                        "Identifier le service dans services.msc (Win+R > services.msc).",
                        "Si service tiers (jeu, antivirus, agent constructeur, RGB software) : mettre à jour ou désactiver.",
                        "Si service Windows critique : lancer SFC /scannow puis DISM /RestoreHealth.",
                    };
                    inc.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Ouvrir services.msc", Tooltip = "Console des services Windows.",          Kind = LogActionKind.Diag,    Target = "services.msc" },
                        new LogAction { Label = "Lancer SFC /scannow", Tooltip = "Vérifie les fichiers système (5-10 min).", Kind = LogActionKind.Command, Target = "cmd /k sfc /scannow", Confirm = true },
                    };
                    return;

                default:
                    inc.Title   = root.Title;
                    inc.Icon    = root.Icon.Length > 0 ? root.Icon : "";
                    inc.Advice  = root.Fix.Length > 0 ? root.Fix : "Voir le détail des événements ci-dessous.";
                    inc.Steps   = root.Steps;
                    inc.Actions = root.Actions;
                    return;
            }
        }

        // ── Base de connaissances « par source » (clé = Provider + EventID) ────
        // Glyphes Segoe MDL2 utilisés (référence visuelle) :
        //    = GPU/écran (Display)         = avertissement triangle
        //    = boucle puissance (power)    = clé à molette (réparation)
        //    = disque dur (Storage)        = info (i)
        //    = puce / matériel              = WiFi
        //    = horloge (Clock)             = bouclier (sécurité TLS)
        //    = Bluetooth-ish               = audio
        //    = enregistrer (sauvegarder)   = corbeille (delete)
        private static LogEntry Decode(string prov, int id, string raw)
        {
            var e = new LogEntry { Provider = prov, Id = id, Raw = raw, Known = true };
            string p = prov.ToLowerInvariant();

            // -- GPU NVIDIA (TDR Timeout Detection & Recovery) --------------
            if (p.Contains("nvlddmkm"))
            {
                e.Sev   = LogSev.Serious;
                e.Icon  = "";
                e.Title = "Pilote graphique NVIDIA — perte de réponse (TDR)";
                e.What  = "Le GPU a cessé de répondre puis a été réinitialisé par Windows (Timeout Detection & Recovery). En jeu, ça se traduit par un freeze de 1-2 s avec écran qui clignote noir, parfois un crash de l'appli.";
                e.Cause = "Pilote instable ou version buguée · overclock GPU (Afterburner) · surchauffe (>83 °C) · alimentation insuffisante sur les pics de charge · parfois un jeu/appli précis qui stresse le shader compiler.";
                e.Steps = new List<string>
                {
                    "Désinstaller le pilote NVIDIA PROPREMENT avec DDU (Display Driver Uninstaller) en mode sans échec, puis réinstaller une version STABLE (Studio Driver ou la Game Ready N-1, plusieurs récentes ont causé des TDR).",
                    "Ouvrir Tweakly > Surveillance > Monitoring système et regarder la température GPU en charge. Au-delà de ~83 °C : revoir le flux d'air boîtier, dépoussiérer, ou refaire la pâte thermique.",
                    "Retirer tout overclock GPU (MSI Afterburner : Core Clock / Memory Clock à 0, Power Limit à 100 %). Un léger UNDERVOLT stabilise souvent les TDR.",
                    "Si ça persiste : suspecter l'alimentation (PSU sous-dimensionnée ou âgée → TDR sur les pics de courant) ; tester le GPU sur un autre PC pour isoler.",
                };
                e.Actions = new List<LogAction>
                {
                    new LogAction { Label = "Voir Monitoring",   Tooltip = "Ouvre la page Monitoring système pour vérifier les températures GPU.", Kind = LogActionKind.Navigate, Target = "Monitoring" },
                    new LogAction { Label = "Télécharger DDU",   Tooltip = "Page officielle de Display Driver Uninstaller (Wagnardsoft).",          Kind = LogActionKind.Url,      Target = "https://www.wagnardsoft.com/" },
                    new LogAction { Label = "Pilotes NVIDIA",    Tooltip = "Page officielle de téléchargement des pilotes NVIDIA.",                 Kind = LogActionKind.Url,      Target = "https://www.nvidia.fr/Download/index.aspx?lang=fr" },
                };
                return e;
            }

            // -- GPU AMD ----------------------------------------------------
            if (p.Contains("amdkmdag") || p.Contains("amdwddmg"))
            {
                e.Sev   = LogSev.Serious;
                e.Icon  = "";
                e.Title = "Pilote graphique AMD — perte de réponse (TDR)";
                e.What  = "Le GPU AMD a cessé de répondre puis a été réinitialisé par Windows (Timeout Detection & Recovery).";
                e.Cause = "Pilote Adrenalin instable · overclock GPU · surchauffe · alimentation insuffisante.";
                e.Steps = new List<string>
                {
                    "Désinstaller proprement le pilote AMD avec DDU en mode sans échec, puis réinstaller la dernière version stable Adrenalin (cleaninstall).",
                    "Ouvrir Tweakly > Surveillance > Monitoring système et vérifier la température GPU en charge.",
                    "Désactiver l'overclock dans Adrenalin (Perf > Tuning > Defaults).",
                };
                e.Actions = new List<LogAction>
                {
                    new LogAction { Label = "Voir Monitoring", Tooltip = "Page Monitoring système.",                                         Kind = LogActionKind.Navigate, Target = "Monitoring" },
                    new LogAction { Label = "Pilotes AMD",     Tooltip = "Page officielle de téléchargement Adrenalin.",                    Kind = LogActionKind.Url,      Target = "https://www.amd.com/fr/support" },
                    new LogAction { Label = "Télécharger DDU", Tooltip = "Page officielle de Display Driver Uninstaller (Wagnardsoft).",   Kind = LogActionKind.Url,      Target = "https://www.wagnardsoft.com/" },
                };
                return e;
            }

            switch (p)
            {
                // -- Crash applicatif -----------------------------------------
                case "application error":
                {
                    e.Sev = LogSev.Warning;
                    e.Icon = "";
                    var app    = Extract(raw, @"(?:application défaillante|application name|faulting application name)\s*:?\s*([^\s,]+)");
                    var modul  = Extract(raw, @"(?:module défaillant|faulting module name)\s*:?\s*([^\s,]+)");
                    e.Title = app.Length > 0 ? $"Plantage d'application — {app}" : "Plantage d'application";
                    e.What  = "Une application s'est arrêtée à cause d'une erreur (crash)." + (modul.Length > 0 ? $" Module fautif identifié : {modul}." : "");
                    bool sysModule = modul.Length > 0 && Regex.IsMatch(modul.ToLowerInvariant(),
                        @"^(ntdll|kernelbase|kernel32|combase|ucrtbase|msvcrt|msvcp|win32u|user32|gdi32)\.dll$");
                    e.Cause = sysModule
                        ? $"Module fautif système ({modul}) : signal RAM instable, fichiers Windows corrompus, ou conflit pilote — RAREMENT un bug de l'appli elle-même."
                        : "Bug de l'application ou d'un de ses modules.";
                    if (sysModule)
                    {
                        e.Steps = new List<string>
                        {
                            "Tester la RAM avec MemTest86 (clé USB, plusieurs passes) — si erreurs : barrette défectueuse ou XMP trop agressif.",
                            "Lancer SFC /scannow dans une CMD admin pour réparer les fichiers système Windows.",
                            "Lancer DISM /Online /Cleanup-Image /RestoreHealth si SFC ne suffit pas.",
                            "Forcer Windows Update à jour (Réglages > Windows Update).",
                        };
                        e.Actions = new List<LogAction>
                        {
                            new LogAction { Label = "Lancer SFC /scannow",  Tooltip = "Vérifie et répare les fichiers système Windows (5-10 min, fenêtre admin).",                Kind = LogActionKind.Command, Target = "cmd /k sfc /scannow",                                    Confirm = true },
                            new LogAction { Label = "Lancer DISM",          Tooltip = "Répare l'image Windows (10-20 min, nécessite Internet).",                                  Kind = LogActionKind.Command, Target = "cmd /k DISM /Online /Cleanup-Image /RestoreHealth",       Confirm = true },
                            new LogAction { Label = "MemTest86",            Tooltip = "Page officielle MemTest86 (PassMark) pour tester la RAM.",                                 Kind = LogActionKind.Url,     Target = "https://www.memtest86.com/" },
                        };
                    }
                    else
                    {
                        e.Steps = new List<string>
                        {
                            app.Length > 0
                                ? $"Ouvrir Tweakly > Boîte à outils > Applications, chercher « {app} » et cliquer Mettre à jour."
                                : "Ouvrir Tweakly > Boîte à outils > Applications et mettre à jour l'appli concernée.",
                            "Si toujours, désinstaller puis réinstaller depuis le site officiel.",
                            app.Length > 0
                                ? $"Si « {app} » n'est pas essentielle et plante souvent, la désinstaller (Win+R > appwiz.cpl)."
                                : "Si non essentielle et récurrente, la désinstaller (Win+R > appwiz.cpl).",
                        };
                        e.Actions = new List<LogAction>
                        {
                            new LogAction { Label = "Voir Applications",       Tooltip = "Liste des applis installées dans Tweakly.",                                  Kind = LogActionKind.Navigate, Target = "Apps" },
                            new LogAction { Label = "Programmes et fonctionnalités", Tooltip = "Ouvre appwiz.cpl pour désinstaller des programmes.",                  Kind = LogActionKind.Diag,    Target = "appwiz.cpl" },
                        };
                    }
                    return e;
                }

                case ".net runtime":
                    e.Sev   = LogSev.Warning;
                    e.Icon  = "";
                    e.Title = "Plantage d'une application .NET";
                    e.What  = "Une application .NET a levé une exception non gérée et s'est fermée.";
                    e.Cause = "Bug de l'application .NET, ou version .NET Framework / .NET Runtime incompatible.";
                    e.Steps = new List<string>
                    {
                        "Mettre à jour l'application concernée (Tweakly > Boîte à outils > Applications).",
                        "Vérifier que .NET Desktop Runtime est à jour : winget upgrade Microsoft.DotNet.DesktopRuntime.8 dans une CMD admin.",
                        "Lancer Windows Update — les mises à jour cumulatives livrent souvent des correctifs .NET Framework.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Applications", Tooltip = "Liste des applis dans Tweakly.",                          Kind = LogActionKind.Navigate, Target = "Apps" },
                        new LogAction { Label = ".NET Microsoft",    Tooltip = "Page officielle de téléchargement .NET.",                Kind = LogActionKind.Url,      Target = "https://dotnet.microsoft.com/fr-fr/download" },
                    };
                    return e;

                // -- Service control manager ---------------------------------
                case "service control manager":
                {
                    var svc = Extract(raw, @"(?:Le service|The)\s+(.+?)\s+(?:s['’]est|service)");
                    e.Icon  = "";
                    e.Title = svc.Length > 0 ? $"Service en échec — {svc}" : "Service Windows en échec";
                    e.Sev   = LogSev.Warning;
                    e.What  = id switch
                    {
                        7031 => "Un service s'est arrêté de façon inattendue (il a planté). Windows va tenter de le redémarrer automatiquement.",
                        7034 => "Un service s'est arrêté de façon inattendue, sans action de récupération configurée.",
                        7000 => "Un service n'a pas pu démarrer du tout (erreur d'initialisation).",
                        7009 => "Délai dépassé en attendant le démarrage d'un service au boot Windows.",
                        7011 => "Délai de réponse dépassé pour un service en cours de fonctionnement.",
                        _    => "Problème de service signalé par le gestionnaire de services.",
                    };
                    e.Cause = "Service tiers (mise à jour Discord/Steam/antivirus mal terminée…) ou service Windows lent au boot.";
                    e.Steps = new List<string>
                    {
                        svc.Length > 0
                            ? $"Identifier le service « {svc} » dans services.msc (Win+R > services.msc) et regarder son éditeur."
                            : "Identifier le service fautif dans services.msc (Win+R > services.msc).",
                        "Si c'est un service tiers (jeu, antivirus, agent constructeur type AsusOptimization, RGB software…) : mettre à jour le logiciel associé, ou le désactiver s'il est inutile.",
                        "Si c'est un service Windows critique : lancer SFC /scannow puis DISM /RestoreHealth pour réparer.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Ouvrir services.msc", Tooltip = "Console des services Windows.",                                          Kind = LogActionKind.Diag,    Target = "services.msc" },
                        new LogAction { Label = "Lancer SFC /scannow", Tooltip = "Vérifie les fichiers système (5-10 min).",                              Kind = LogActionKind.Command, Target = "cmd /k sfc /scannow", Confirm = true },
                    };
                    return e;
                }

                // -- VolSnap (clichés VSS) -----------------------------------
                case "volsnap":
                    e.Sev   = LogSev.Warning;
                    e.Icon  = "";
                    e.Title = "Clichés instantanés (restauration) supprimés";
                    e.What  = "Windows a supprimé des points de restauration / clichés VSS faute de place.";
                    e.Cause = "Espace réservé aux clichés trop petit, ou disque saturé.";
                    e.Steps = new List<string>
                    {
                        "Ouvrir Propriétés système > Protection du système > Configurer et augmenter l'espace alloué (5-10 % typiquement).",
                        "Libérer de la place sur le disque système (Tweakly > Optimisations > Nettoyage).",
                        "Vérifier la santé du disque (Tweakly > Diagnostic > Bilan de santé).",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Protection système", Tooltip = "Ouvre les paramètres de Protection du système.",        Kind = LogActionKind.Command, Target = "cmd /c SystemPropertiesProtection.exe", Confirm = false },
                        new LogAction { Label = "Voir Nettoyage",     Tooltip = "Page Nettoyage de Tweakly.",                            Kind = LogActionKind.Navigate, Target = "Nettoyage" },
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "Page Bilan de santé de Tweakly.",                       Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                    };
                    return e;

                // -- WHEA (erreurs matérielles) ------------------------------
                case "microsoft-windows-whea-logger":
                    e.Sev   = LogSev.Serious;
                    e.Icon  = "";
                    e.Title = id switch
                    {
                        17 => "Erreur matérielle CORRIGÉE (WHEA)",
                        18 => "Erreur matérielle NON CORRIGÉE (WHEA)",
                        19 => "Erreur matérielle CORRIGÉE par le firmware (WHEA)",
                        _  => "Erreur matérielle (WHEA)",
                    };
                    e.What  = id == 18
                        ? "Le matériel a signalé une erreur que Windows N'A PAS pu corriger. C'est sérieux : composant potentiellement défaillant."
                        : "Le matériel a signalé une erreur (CPU, RAM, cache, lien PCIe…). Windows a réussi à la corriger cette fois, mais c'est un signal d'alerte.";
                    e.Cause = "RAM instable (XMP/EXPO trop agressif, barrette défectueuse) · overclock CPU/BCLK · surchauffe · PSU défaillante · périphérique PCIe (GPU, SSD NVMe) en fin de vie.";
                    e.Steps = new List<string>
                    {
                        "Désactiver XMP/EXPO dans le BIOS (revenir aux fréquences JEDEC par défaut). Si les WHEA cessent → ton profil mémoire était instable.",
                        "Retirer tout overclock CPU/BCLK ; refaire les tests à fréquence stock.",
                        "Tester la RAM avec MemTest86 (clé USB, 4 passes minimum).",
                        "Vérifier les températures CPU/GPU sous charge (Tweakly > Monitoring système).",
                        id == 18
                            ? "Si l'erreur 18 (non corrigée) revient : composant probablement défaillant. Tester en croisant les barrettes RAM ; envisager remplacement CPU/CM/PSU."
                            : "Si récurrent, surveiller les composants ; les erreurs corrigées préfigurent souvent une panne sous 1-12 mois.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Monitoring",     Tooltip = "Vérifier températures CPU/GPU.",                                    Kind = LogActionKind.Navigate, Target = "Monitoring" },
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "État SMART disques et autres signaux matériels.",                  Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "MemTest86",           Tooltip = "Page officielle MemTest86 (PassMark) pour tester la RAM.",         Kind = LogActionKind.Url,      Target = "https://www.memtest86.com/" },
                    };
                    return e;

                // -- Kernel-Power (arrêts brutaux) ---------------------------
                case "microsoft-windows-kernel-power":
                    e.Sev   = id == 41 ? LogSev.Serious : LogSev.Warning;
                    e.Icon  = "";
                    e.Title = id == 41 ? "Arrêt/redémarrage inattendu (Kernel-Power 41)" : "Événement alimentation noyau";
                    e.What  = id == 41
                        ? "Le PC s'est éteint/redémarré sans arrêt propre (coupure brutale). C'est l'événement loggué APRÈS un BSOD non visible, une coupure de courant, ou un crash matériel."
                        : "Événement de gestion d'alimentation noyau.";
                    e.Cause = "Coupure d'alimentation murale · surchauffe (arrêt d'urgence CPU/GPU) · PSU défaillante ou sous-dimensionnée · BSOD invisible (écran qui ne reflète pas) · overclock instable · pilote noyau qui plante.";
                    e.Steps = new List<string>
                    {
                        "Croiser avec les BSOD : Tweakly > Diagnostic > Bilan de santé. S'il y a un BSOD au même moment → c'est un plantage logiciel/pilote, PAS une coupure de courant.",
                        "Vérifier les températures CPU/GPU sous charge (Monitoring système).",
                        "Tester une autre prise murale ; si PC portable : tester sans la batterie.",
                        "Si tour : suspecter la PSU si elle est vieille (>5 ans) ou sous-dimensionnée. Le rail 12V faiblit avec l'âge → arrêts sur les pics.",
                        "Retirer tout overclock CPU/GPU/RAM (XMP off) pendant 48h pour isoler.",
                        "Mettre à jour les pilotes (Tweakly > Boîte à outils > Applications) et le BIOS si stable.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "BSOD, stabilité système, SMART.",                Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "Voir Monitoring",     Tooltip = "Températures CPU/GPU/RAM en temps réel.",        Kind = LogActionKind.Navigate, Target = "Monitoring" },
                    };
                    return e;

                // -- Disque / NTFS -------------------------------------------
                case "disk":
                case "ntfs":
                case "microsoft-windows-ntfs":
                    e.Sev   = LogSev.Serious;
                    e.Icon  = "";
                    e.Title = "Erreur disque / système de fichiers";
                    e.What  = "Windows a rencontré une erreur d'entrée-sortie ou de cohérence sur un disque. RISQUE pour tes données.";
                    e.Cause = "Câble SATA défectueux (cause #1, très fréquente) · disque vieillissant / secteurs défectueux · coupure pendant écriture · contrôleur SATA/NVMe instable.";
                    e.Steps = new List<string>
                    {
                        "SAUVEGARDER MAINTENANT les fichiers importants sur un autre disque/cloud — c'est la priorité.",
                        "Vérifier la santé SMART : Tweakly > Diagnostic > Bilan de santé (affiche l'usure SSD et les secteurs réalloués).",
                        "Lancer chkdsk /f sur le volume concerné dans une CMD admin (ex. chkdsk C: /f — nécessite reboot pour le volume système).",
                        "Si c'est un disque SATA : remplacer le câble (3 €, cause fréquente et bénigne).",
                        "Si le SMART est dégradé (PreFail) ou si les erreurs persistent : remplacer le disque.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "État SMART + usure SSD.",                            Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "Lancer CHKDSK C:",    Tooltip = "Vérifie et répare le volume C: (reboot nécessaire). Tape 'O' puis redémarre.", Kind = LogActionKind.Command, Target = "cmd /k chkdsk C: /f", Confirm = true },
                    };
                    return e;

                // -- DCOM (bruit) ---------------------------------------------
                case "microsoft-windows-distributedcom":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "DCOM — délai/permission (bruit)";
                    e.What  = "Un composant COM ne s'est pas enregistré à temps, ou une permission manquait.";
                    e.Cause = "Course au démarrage de Windows, service lent. Aucun impact perceptible.";
                    e.Fix   = "Généralement inoffensif — aucune action nécessaire.";
                    return e;

                // -- Perflib (bruit) ------------------------------------------
                case "microsoft-windows-perflib":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "Compteur de performance (bruit)";
                    e.What  = "Un compteur de performance d'un service n'a pas pu être chargé/déchargé.";
                    e.Cause = "Service tiers mal désinstallé ou en cours d'initialisation.";
                    e.Fix   = "Généralement inoffensif.";
                    return e;

                // -- User Profiles (mineur) -----------------------------------
                case "microsoft-windows-user profiles service":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "Service de profils utilisateur (mineur)";
                    e.What  = "Avertissement lors du chargement/déchargement du profil.";
                    e.Cause = "Fichier de profil verrouillé à la fermeture de session.";
                    e.Fix   = "Mineur.";
                    return e;

                // ── NOUVEAUTÉS v1.3.0 ────────────────────────────────────────

                // -- BSOD (BugCheck) -----------------------------------------
                case "microsoft-windows-windowserror reporting":
                case "bugcheck":
                {
                    e.Sev   = LogSev.Serious;
                    e.Icon  = "";
                    e.Title = "Écran bleu (BSOD)";
                    e.What  = "Windows a planté avec un écran bleu et a généré un minidump pour diagnostic.";
                    e.Cause = "Pilote défaillant (driver_irql_not_less_or_equal, system_service_exception…) · RAM instable · disque défaillant · overclock.";
                    e.Steps = new List<string>
                    {
                        "Repérer le code d'arrêt (ex. DRIVER_IRQL_NOT_LESS_OR_EQUAL, MEMORY_MANAGEMENT) dans le détail brut ci-dessous.",
                        "Identifier le pilote/module fautif via WinDbg ou BlueScreenView (NirSoft) en analysant le minidump dans C:\\Windows\\Minidump\\.",
                        "Si le code est MEMORY_MANAGEMENT ou IRQL : tester la RAM (MemTest86) et désactiver XMP/EXPO.",
                        "Mettre à jour TOUS les pilotes (Tweakly > Boîte à outils > Applications) et le BIOS.",
                        "Lancer SFC /scannow et DISM /RestoreHealth.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "Stabilité système + BSOD comptés.",                    Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                        new LogAction { Label = "BlueScreenView",      Tooltip = "Page NirSoft — outil gratuit pour analyser les minidumps.", Kind = LogActionKind.Url,      Target = "https://www.nirsoft.net/utils/blue_screen_view.html" },
                        new LogAction { Label = "Dossier Minidump",    Tooltip = "Ouvre C:\\Windows\\Minidump (peut nécessiter admin).",  Kind = LogActionKind.Command, Target = "explorer C:\\Windows\\Minidump" },
                    };
                    return e;
                }

                // -- Resource-Exhaustion-Detector (manque RAM) ---------------
                case "microsoft-windows-resource-exhaustion-detector":
                    e.Sev   = LogSev.Warning;
                    e.Icon  = "";
                    e.Title = "Mémoire physique épuisée";
                    e.What  = "Windows a manqué de RAM disponible et a dû terminer des processus pour libérer de la mémoire.";
                    e.Cause = "Fuite mémoire d'une application · trop d'applis ouvertes simultanément · fichier d'échange trop petit ou désactivé · RAM insuffisante pour l'usage.";
                    e.Steps = new List<string>
                    {
                        "Identifier l'appli gourmande : Gestionnaire des tâches > Détails > tri par Mémoire (privée).",
                        "Augmenter le fichier d'échange : SystemPropertiesPerformance > Avancé > Mémoire virtuelle > 1,5× la RAM physique recommandé.",
                        "Tweakly > Surveillance > Monitoring système : utiliser le bouton « Nettoyer » la RAM pour purger la standby list.",
                        "Si récurrent : envisager d'augmenter la RAM physique (16 → 32 Go).",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Monitoring",     Tooltip = "Suivre l'usage RAM en temps réel + bouton Nettoyer.",  Kind = LogActionKind.Navigate, Target = "Monitoring" },
                        new LogAction { Label = "Gestionnaire des tâches", Tooltip = "Ouvre le Gestionnaire des tâches.",                Kind = LogActionKind.Command, Target = "cmd /c taskmgr" },
                        new LogAction { Label = "Mémoire virtuelle",   Tooltip = "Ouvre les paramètres de performance Windows.",         Kind = LogActionKind.Command, Target = "cmd /c SystemPropertiesPerformance.exe" },
                    };
                    return e;

                // -- WLAN-AutoConfig (Wi-Fi) ---------------------------------
                case "microsoft-windows-wlan-autoconfig":
                    e.Sev   = LogSev.Warning;
                    e.Icon  = "";
                    e.Title = "Wi-Fi — perte de connexion / problème d'auth";
                    e.What  = "Le service WLAN-AutoConfig a signalé une perte de connexion Wi-Fi ou un échec d'authentification.";
                    e.Cause = "Pilote Wi-Fi obsolète/instable · économie d'énergie qui éteint la carte · interférences · routeur saturé · canal Wi-Fi encombré.";
                    e.Steps = new List<string>
                    {
                        "Mettre à jour le pilote Wi-Fi via Tweakly > Boîte à outils > Applications, ou directement chez le constructeur de la carte (Intel/Realtek/Broadcom).",
                        "Désactiver la gestion d'énergie de la carte Wi-Fi : Gestionnaire de périphériques > Cartes réseau > [ta carte] > Propriétés > Gestion d'alimentation > décocher « Autoriser cet ordinateur à éteindre ce périphérique pour économiser l'énergie ».",
                        "Tweakly > Surveillance > Monitoring réseau : regarder le jitter et la perte de paquets — s'ils sont élevés en Wi-Fi, le souci est radio/routeur.",
                        "Tester en filaire (Ethernet) pour confirmer que le problème est bien lié au Wi-Fi.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Monitoring réseau", Tooltip = "Latence, jitter, perte de paquets en temps réel.", Kind = LogActionKind.Navigate, Target = "ReseauMon" },
                        new LogAction { Label = "Gestionnaire périph.",   Tooltip = "Ouvre devmgmt.msc (Gestionnaire de périphériques).", Kind = LogActionKind.Diag,    Target = "devmgmt.msc" },
                    };
                    return e;

                // -- DNS-Client ----------------------------------------------
                case "microsoft-windows-dns-client":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "Résolution DNS — délai";
                    e.What  = "Une résolution DNS a échoué ou pris trop de temps.";
                    e.Cause = "DNS du FAI lent ou saturé · perte Wi-Fi brève · routeur en redémarrage. Souvent ponctuel.";
                    e.Steps = new List<string>
                    {
                        "Si ponctuel : ignorer (un DNS qui rate de temps en temps est normal).",
                        "Si récurrent : changer de serveur DNS pour Cloudflare (1.1.1.1) ou Google (8.8.8.8) — Tweakly > Optimisations > Réseau.",
                        "Vider le cache DNS : ipconfig /flushdns dans une CMD admin.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Optimisations Réseau", Tooltip = "Page Réseau de Tweakly (DNS, TCP…).",                Kind = LogActionKind.Navigate, Target = "Reseau" },
                        new LogAction { Label = "Vider cache DNS",     Tooltip = "Lance ipconfig /flushdns.",                          Kind = LogActionKind.Command, Target = "cmd /c ipconfig /flushdns && pause" },
                    };
                    return e;

                // -- Time-Service (horloge) ----------------------------------
                case "microsoft-windows-time-service":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "Synchronisation d'horloge";
                    e.What  = "Windows n'a pas pu se synchroniser avec le serveur de temps NTP.";
                    e.Cause = "Serveur time.windows.com saturé · pare-feu bloquant UDP 123 · pile horloge CMOS faible (PC se croit à une vieille date).";
                    e.Steps = new List<string>
                    {
                        "Forcer une resync : w32tm /resync dans une CMD admin.",
                        "Changer de serveur de temps : Panneau de configuration > Horloge > Heure Internet > Modifier > pool.ntp.org.",
                        "Si l'horloge dérive massivement à chaque démarrage : pile CMOS de la carte mère à remplacer (CR2032, ~2 €).",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Resync horloge",  Tooltip = "Force w32tm /resync.",                                  Kind = LogActionKind.Command, Target = "cmd /k w32tm /resync" },
                        new LogAction { Label = "Date et heure",   Tooltip = "Ouvre les paramètres Date et heure.",                   Kind = LogActionKind.Diag,    Target = "timedate.cpl" },
                    };
                    return e;

                // -- Schannel (TLS) ------------------------------------------
                case "schannel":
                    e.Sev   = LogSev.Warning;
                    e.Icon  = "";
                    e.Title = "Erreur TLS / HTTPS (Schannel)";
                    e.What  = "Windows n'a pas pu établir une connexion sécurisée TLS (HTTPS, SMTP, etc.).";
                    e.Cause = "Certificat expiré ou autosigné · protocole TLS obsolète (TLS 1.0/1.1 désactivé) · horloge système déréglée · chiffrement non négocié.";
                    e.Steps = new List<string>
                    {
                        "Vérifier la date et l'heure système (un décalage > 5 min casse TLS).",
                        "Si erreur en visitant un site précis : c'est le site, pas toi.",
                        "Si erreur dans une appli Windows ancienne : Internet Options > Avancé > activer TLS 1.2 / 1.3.",
                        "Lancer SFC /scannow si récurrent dans plusieurs apps.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Date et heure",        Tooltip = "Vérifier l'heure système.",                Kind = LogActionKind.Diag, Target = "timedate.cpl" },
                        new LogAction { Label = "Options Internet",     Tooltip = "Active TLS 1.2/1.3.",                      Kind = LogActionKind.Diag, Target = "inetcpl.cpl" },
                    };
                    return e;

                // -- Storahci / iaStor (SATA AHCI) ---------------------------
                case "storahci":
                case "iastoravc":
                case "iastora":
                case "iastore":
                    e.Sev   = LogSev.Serious;
                    e.Icon  = "";
                    e.Title = "Erreur contrôleur SATA AHCI";
                    e.What  = "Le contrôleur SATA a réinitialisé une commande (reset to device, port \\Device\\RaidPortX). Signe d'un câble ou d'un disque qui flanche.";
                    e.Cause = "Câble SATA défectueux (#1) · port SATA carte mère défaillant · disque en fin de vie · alimentation instable.";
                    e.Steps = new List<string>
                    {
                        "Sauvegarder les données importantes du disque concerné.",
                        "Remplacer le câble SATA (3 €, cause #1).",
                        "Changer de port SATA sur la carte mère (essayer un autre port).",
                        "Vérifier la santé SMART : Tweakly > Diagnostic > Bilan de santé.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "État SMART + usure SSD.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                    };
                    return e;

                // -- NVMe / stornvme -----------------------------------------
                case "stornvme":
                case "nvme":
                    e.Sev   = LogSev.Serious;
                    e.Icon  = "";
                    e.Title = "Erreur SSD NVMe";
                    e.What  = "Le pilote NVMe a signalé une erreur (commande non complétée, reset du device). Sur SSD NVMe c'est rarement bénin.";
                    e.Cause = "SSD NVMe en surchauffe (>75 °C) · firmware bugué · slot M.2 défaillant · alimentation 3.3 V faible.";
                    e.Steps = new List<string>
                    {
                        "Sauvegarder les données importantes — un NVMe qui jette ses commandes est suspect.",
                        "Tweakly > Surveillance > Monitoring système : surveiller la température du NVMe (>75 °C = problème).",
                        "Mettre à jour le firmware SSD via l'outil du fabricant (Samsung Magician, WD Dashboard, Kingston SSD Manager…).",
                        "Vérifier la santé SMART : Tweakly > Diagnostic > Bilan de santé.",
                        "Si surchauffe : ajouter un dissipateur M.2 (~10 €) ou améliorer le flux d'air.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Voir Monitoring",     Tooltip = "Températures NVMe.",      Kind = LogActionKind.Navigate, Target = "Monitoring" },
                        new LogAction { Label = "Voir Bilan de santé", Tooltip = "SMART + usure SSD.",      Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                    };
                    return e;

                // -- volmgr 5 (boot crash dump) ------------------------------
                case "volmgr":
                    if (id == 5)
                    {
                        e.Sev   = LogSev.Warning;
                        e.Icon  = "";
                        e.Title = "Crash dump impossible (volmgr 5)";
                        e.What  = "Windows n'a pas pu écrire le fichier de dump après un crash (fichier d'échange trop petit ou désactivé).";
                        e.Cause = "Fichier d'échange désactivé ou plus petit que la RAM.";
                        e.Steps = new List<string>
                        {
                            "Activer le fichier d'échange si désactivé : SystemPropertiesPerformance > Avancé > Mémoire virtuelle.",
                            "Le dimensionner à au moins 1× la RAM (1,5× idéal) sur le volume système.",
                        };
                        e.Actions = new List<LogAction>
                        {
                            new LogAction { Label = "Mémoire virtuelle", Tooltip = "Paramètres de performance Windows.", Kind = LogActionKind.Command, Target = "cmd /c SystemPropertiesPerformance.exe" },
                        };
                        return e;
                    }
                    break;

                // -- BITS (transfert WU) -------------------------------------
                case "microsoft-windows-bits-client":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "BITS — transfert annulé/repris";
                    e.What  = "Le service BITS (transferts Windows Update / Store) a annulé ou repris un téléchargement.";
                    e.Cause = "Réseau instable, machine en veille pendant un transfert.";
                    e.Fix   = "Bénin. Si Windows Update échoue régulièrement : Tweakly > Optimisations > Réseau, vérifier la connexion.";
                    return e;

                // -- Wininit (boot/shutdown) ---------------------------------
                case "microsoft-windows-wininit":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "Démarrage/arrêt Windows";
                    e.What  = "Événement d'initialisation/arrêt Windows (normal).";
                    e.Cause = "Évenement informatif lié au cycle de vie de la session.";
                    e.Fix   = "Inoffensif.";
                    return e;

                // -- Power-Troubleshooter (réveil veille) --------------------
                case "microsoft-windows-power-troubleshooter":
                    e.Sev   = LogSev.Benign;
                    e.Icon  = "";
                    e.Title = "Réveil après veille/hibernation";
                    e.What  = "Le PC est sorti de veille (information sur la source du réveil).";
                    e.Cause = "Souris/clavier · réveil planifié (mise à jour) · périphérique réseau Wake-on-LAN.";
                    e.Fix   = "Si réveils nocturnes intempestifs : powercfg /lastwake dans CMD admin pour identifier la source, puis désactiver le wake-up du périphérique en cause dans le Gestionnaire de périphériques.";
                    return e;

                // -- Print Spooler -------------------------------------------
                case "print":
                case "printservice":
                case "microsoft-windows-printservice":
                    e.Sev   = LogSev.Warning;
                    e.Icon  = "";
                    e.Title = "Service Spouleur d'impression";
                    e.What  = "Erreur du service Spouleur d'impression (impression bloquée ou imprimante non trouvée).";
                    e.Cause = "Pilote imprimante corrompu · file d'attente bloquée · service Spouleur planté.";
                    e.Steps = new List<string>
                    {
                        "Redémarrer le service Spouleur : services.msc > Print Spooler > Redémarrer.",
                        "Vider la file d'attente : supprimer les fichiers de C:\\Windows\\System32\\spool\\PRINTERS\\.",
                        "Réinstaller le pilote de l'imprimante depuis le site du fabricant.",
                    };
                    e.Actions = new List<LogAction>
                    {
                        new LogAction { Label = "Ouvrir services.msc", Tooltip = "Console des services Windows.", Kind = LogActionKind.Diag, Target = "services.msc" },
                    };
                    return e;
            }

            // -- Fallback : source inconnue ---------------------------------
            e.Known = false;
            e.Sev   = LogSev.Warning;
            e.Icon  = "";
            e.Title = prov;
            e.What  = $"Erreur signalée par « {prov} » (EventID {id}).";
            e.Cause = "Source non répertoriée par Tweakly — voir le détail brut.";
            e.Fix   = "Rechercher « " + prov + " " + id + " » sur le web.";
            return e;
        }

        /// <summary>Retrouve le service Windows dont le binaire est <paramref name="exePath"/>
        /// (Win32_Service.PathName). Renvoie ("","") si l'exe n'est pas un service.</summary>
        private static (string name, string display) FindServiceByExe(string exePath)
        {
            try
            {
                using var q = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, DisplayName, PathName FROM Win32_Service");
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    var pn = o["PathName"]?.ToString() ?? "";
                    if (pn.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0)
                        return (o["Name"]?.ToString() ?? "", o["DisplayName"]?.ToString() ?? "");
                }
            }
            catch { }
            return ("", "");
        }

        // ── Contexte machine : version du driver NVIDIA installée (v1.3.3) ──────
        // WMI donne « 32.0.15.9649 » ; le format public NVIDIA = les 5 derniers
        // chiffres avec un point avant les 2 derniers → « 596.49 ». Cache statique
        // (une requête WMI par session suffit). Chaîne vide si pas de GPU NVIDIA.
        private static string? _nvDriverCache;

        private static string GetNvidiaDriverVersion()
        {
            if (_nvDriverCache != null) return _nvDriverCache;
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, DriverVersion FROM Win32_VideoController");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    var name = o["Name"]?.ToString() ?? "";
                    var ver  = o["DriverVersion"]?.ToString() ?? "";
                    o.Dispose();
                    if (!name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;

                    var digits = new string(ver.Where(char.IsDigit).ToArray());
                    if (digits.Length >= 5)
                    {
                        var five = digits[^5..];
                        return _nvDriverCache = $"{five[..3]}.{five[3..]}";
                    }
                }
            }
            catch { }
            return _nvDriverCache = "";
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
