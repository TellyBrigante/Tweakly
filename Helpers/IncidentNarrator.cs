using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Narrateur d'incidents (v1.4.3). Pour chaque incident clusterisé temporellement,
    /// LIT les EventData enrichis collectés par <see cref="EventLogDecoder"/>, recoupe les
    /// preuves dans la fenêtre temporelle et produit :
    ///   • une CHRONOLOGIE en clair des events les plus parlants ;
    ///   • un VERDICT factuel désignant le composant physique fautif PAR SON NOM ;
    ///   • une action sûre PRIORITAIRE (1) et un repli (1 ou 2).
    ///
    /// PRINCIPE : on ne fait PAS de matching « Erreur A = Solution B ». On LIT ce que la
    /// machine du client raconte, on RACONTE en français ce qu'on voit, et on désigne LE
    /// COUPABLE désigné par les preuves. Si les preuves sont ambiguës, le narrateur le DIT
    /// (« la séquence est ambiguë, voici les pistes »).
    /// </summary>
    public static class IncidentNarrator
    {
        public sealed class Narration
        {
            /// <summary>
            /// Si true, le cluster n'EST PAS un vrai incident : c'est un faux positif
            /// (arrêt programmé pris pour un BSOD, redémarrage clean, etc.). L'appelant
            /// doit JETER le cluster (ne pas afficher de carte). Sentinel renvoyé par
            /// le narrateur quand il a la preuve formelle que l'événement est bénin.
            /// </summary>
            public bool Suppress;
            public string Title = "";
            public string Icon  = "";
            public string Chain = "";
            public string Advice = "";
            public List<string>                   Steps   = new();
            public List<LogAction> Actions = new();
        }

        /// <summary>
        /// Tente de produire une narration factuelle à partir du cluster d'events.
        /// Renvoie null si aucune preuve suffisamment forte n'a été trouvée (l'appelant
        /// retombe alors sur ses conseils génériques actuels). Le narrateur ne « comble
        /// jamais avec de l'imagination » : pas de preuve → null.
        /// </summary>
        public static Narration? Narrate(List<RawEvent> cluster)
        {
            if (cluster == null || cluster.Count == 0) return null;

            // ─── ANTI-FAUX-POSITIF : un Kernel-Power 41 PEUT être un arrêt programmé ───
            // Avant de tout interpréter comme « BSOD masqué » ou « coupure brutale »,
            // on cherche dans le System log les MARQUEURS D'ARRÊT PROPRE qui ne sont
            // pas dans le cluster (filtrés par Level 1+2) :
            //   - EventID 1074 (User32) = arrêt initié par un programme/utilisateur
            //   - EventID 6006 (EventLog) = service journal arrêté proprement
            // Si l'un des deux est présent dans la fenêtre ±90 s autour du Power 41,
            // l'arrêt n'est PAS brutal : on suppress le cluster. Sinon on continue.
            var p41 = cluster.FirstOrDefault(e =>
                e.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.Id == 41);
            if (p41 != null && ParseBugCheck(p41) == 0)
            {
                // Preuve 1 : marqueurs explicites d'arrêt clean (1074 / 6006) dans ±5 min
                var marker = LookupCleanShutdownMarkers(p41.Time);
                if (marker.clean) return new Narration { Suppress = true };

                // Preuve 2 : il existe une TÂCHE PLANIFIÉE d'arrêt programmée à cette heure
                // (±15 min). Attrape les arrêts via Stop-Computer/wmic/etc. qui ne logguent
                // PAS 1074 ni 6006. C'est le cas typique des extinctions automatiques nocturnes
                // ou matinales : « tous les jours à 9h le PC s'éteint via ma tâche planifiée ».
                if (ScheduledShutdownDetector.MatchAtTime(p41.Time) != null)
                    return new Narration { Suppress = true };
            }

            // On essaie les scénarios par ordre de FORCE de preuve : plus la preuve est
            // directe, plus on essaie tôt. Le premier qui matche gagne.
            return TryDiskInpageBsod(cluster)
                ?? TryWheaUncorrected(cluster)
                ?? TryWheaCorrectedRepeated(cluster)
                ?? TryDiskRepeated(cluster)
                ?? TryTdrGpu(cluster)
                ?? TryAppCrashSystemModule(cluster)
                ?? TryServiceCrashLoop(cluster)
                ?? TryPower41MaskedBsod(cluster)
                ?? TryPower41RealOutage(cluster)
                ?? TryGenericBsod(cluster);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Scénarios — chacun lit DES PREUVES dans le cluster et ne se déclenche
        //  QUE si la preuve est là. Toute déduction se fait à partir de Data, pas
        //  de pattern textuel inventé.
        // ═══════════════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────────────
        // 1. Disk error → NTFS error → BSOD KERNEL_DATA_INPAGE_ERROR (0x7A)
        //    PREUVE FORTE : on voit le disque échouer, puis Windows planter sur
        //    une lecture de page. Le coupable est désigné PAR SON NOM.
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryDiskInpageBsod(List<RawEvent> cluster)
        {
            var bsod = FindBsod(cluster);
            if (bsod == null) return null;
            uint code = bsod.bugcheck;
            if (code != 0x7A && code != 0x4A && code != 0x77) return null;   // pas un I/O page BSOD

            // A-t-on des Disk/NTFS errors avant le BSOD ?
            var diskBefore = cluster
                .Where(e => e.Time <= bsod.ev.Time && IsDiskOrNtfs(e))
                .OrderBy(e => e.Time)
                .ToList();
            if (diskBefore.Count == 0) return null;

            // Identifier LE disque physique cité (Disk 51 a un champ DeviceName)
            string deviceDesc = "";
            int? diskIndex = null;
            foreach (var d in diskBefore)
            {
                if (d.Data.TryGetValue("DeviceName", out var dn) ||
                    d.Data.TryGetValue("DeviceObject", out dn))
                {
                    var m = Regex.Match(dn, @"Harddisk(\d+)");
                    if (m.Success) { diskIndex = int.Parse(m.Groups[1].Value); deviceDesc = HardwareNamer.DescribeDiskDevicePath(dn); break; }
                }
            }
            // NTFS donne souvent le volume au lieu du disque
            if (deviceDesc.Length == 0)
            {
                foreach (var d in diskBefore)
                {
                    if (d.Provider.IndexOf("ntfs", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (d.Data.TryGetValue("DeviceName", out var dn))
                    {
                        deviceDesc = HardwareNamer.DescribeDiskDevicePath(dn);
                        var m = Regex.Match(dn, @"Harddisk(\d+)");
                        if (m.Success) diskIndex = int.Parse(m.Groups[1].Value);
                        break;
                    }
                }
            }
            if (deviceDesc.Length == 0) deviceDesc = "un de tes disques";

            var n = new Narration
            {
                Title  = $"Disque défaillant — masqué par un BSOD ({HardwareNamer.BugCheckName(code)})",
                Icon   = "",
                Chain  = BuildChain(diskBefore, bsod.ev, code),
                Advice = $"Voici ce qui s'est passé chez toi le {bsod.ev.Time:dd/MM à HH:mm:ss} : " +
                         $"ton disque a échoué plusieurs fois à lire des données, et Windows a fini par planter sur une " +
                         $"lecture de page mémoire (BSOD {HardwareNamer.BugCheckName(code)} 0x{code:X}). " +
                         $"Coupable identifié par les logs : {deviceDesc}. " +
                         $"Le code BSOD pointe directement une erreur d'I/O disque — pas Windows, pas un pilote.",
                Steps = new List<string>
                {
                    "Sauvegarde MAINTENANT les fichiers importants de ce disque ailleurs (autre disque, cloud).",
                    "Tweakly > Diagnostic > Bilan de santé : regarde la SMART du disque cité. Si elle est dégradée, le disque est sur la fin.",
                    "Si SMART « OK » mais erreurs répétées : c'est presque toujours le CÂBLE SATA (3 €, à remplacer en premier — vérifié sur d'innombrables PC).",
                    "Si tout le reste va bien et les erreurs continuent : le disque ou son contrôleur est défaillant — remplacement.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Bilan de santé", Tooltip = "SMART du disque cité dans l'incident.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                    new() { Label = "Lancer CHKDSK",       Tooltip = "Vérifie/répare le volume système (reboot requis).",
                            Kind = LogActionKind.Command, Target = "cmd /k chkdsk C: /f", Confirm = true },
                },
            };
            return n;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 2. WHEA non corrigée → composant matériel mort (CPU/RAM/PCIe)
        //    PREUVE FORTE : Windows distingue WHEA « Corrected » et « Uncorrected ».
        //    Une UNCORRECTED = défaillance matérielle réelle (le système n'a pas pu réparer).
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryWheaUncorrected(List<RawEvent> cluster)
        {
            var whea = cluster
                .Where(e => e.Provider.IndexOf("whea", StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(e => SeverityValue(e) == 1 || e.Data.GetValueOrDefault("Severity", "")
                                                       .Equals("Uncorrectable", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (whea.Count == 0) return null;

            var first = whea.First();
            string source = first.Data.GetValueOrDefault("ErrorSource", "")
                          ?? first.Data.GetValueOrDefault("ErrorSourceType", "");
            string component = MapWheaSource(source);
            string addr = first.Data.GetValueOrDefault("PhysicalAddress", "");

            return new Narration
            {
                Title  = $"Erreur matérielle non corrigée — composant {component} à risque",
                Icon   = "",
                Chain  = BuildChainPlain(whea),
                Advice = $"Le matériel a remonté une erreur que Windows n'a PAS pu corriger (WHEA Uncorrected). " +
                         $"Source identifiée : {component}. " +
                         (addr.Length > 0 ? $"Adresse physique mise en cause : {addr}. " : "") +
                         "Concrètement : un composant a une défaillance — ce n'est pas un bug logiciel, pas un pilote.",
                Steps = new List<string>
                {
                    "Si overclock/XMP/EXPO actif : DÉSACTIVE-le immédiatement dans le BIOS et reteste 24 h. C'est la cause #1 d'une WHEA isolée.",
                    component == "RAM"
                        ? "Lance MemTest86 (clé USB bootable, 4 passes minimum). Une seule erreur = barrette à remplacer."
                        : "Surveille les températures du composant cité sous charge (Tweakly > Monitoring).",
                    "Si l'erreur revient en BIOS par défaut et hors overclock : le composant est défaillant, remplacement.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Monitoring", Tooltip = "Températures CPU/GPU/RAM.", Kind = LogActionKind.Navigate, Target = "Monitoring" },
                    new() { Label = "MemTest86",       Tooltip = "Test de RAM bootable (PassMark, gratuit).",
                            Kind = LogActionKind.Url, Target = "https://www.memtest86.com/" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 3. WHEA corrigée RÉPÉTÉE → RAM marginale (XMP trop agressif, barrette qui fatigue)
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryWheaCorrectedRepeated(List<RawEvent> cluster)
        {
            var corrected = cluster
                .Where(e => e.Provider.IndexOf("whea", StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(e => SeverityValue(e) == 2 || e.Data.GetValueOrDefault("Severity", "")
                                                          .Equals("Corrected", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (corrected.Count < 5) return null;   // bruit si trop peu

            return new Narration
            {
                Title  = $"Erreurs RAM corrigées en rafale ({corrected.Count}× dans la fenêtre)",
                Icon   = "",
                Chain  = $"{corrected.Count}× WHEA-Logger Corrected entre {corrected.First().Time:HH:mm:ss} et {corrected.Last().Time:HH:mm:ss}",
                Advice = $"Le contrôleur mémoire a corrigé {corrected.Count} erreurs sur la fenêtre — c'est tolérable PONCTUELLEMENT, mais une rafale comme " +
                         "celle-ci dit que la mémoire est à la limite. Cause la plus fréquente : XMP/EXPO trop tendu pour cette CPU/barrette/carte mère. " +
                         "Risque si on laisse traîner : ça finit en WHEA non corrigée + BSOD.",
                Steps = new List<string>
                {
                    "BIOS : retire le profil XMP/EXPO (RAM aux fréquences JEDEC standard). Reteste 48 h.",
                    "Si l'erreur disparaît : ton XMP est instable — tu peux retenter un profil plus modeste (ex. 3200 au lieu de 3600), ou augmenter VDDIO de 0,05 V.",
                    "Si l'erreur persiste sans XMP : lance MemTest86 — une vraie barrette défectueuse.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "MemTest86", Tooltip = "Test de RAM bootable (PassMark, gratuit).",
                            Kind = LogActionKind.Url, Target = "https://www.memtest86.com/" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 4. Disk errors répétées (sans BSOD) → disque qui souffre, à surveiller
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryDiskRepeated(List<RawEvent> cluster)
        {
            var disk = cluster.Where(IsDiskOrNtfs).ToList();
            if (disk.Count < 3) return null;

            // Identifier le disque cité (le plus fréquent)
            string? topDevice = disk
                .Select(d => d.Data.GetValueOrDefault("DeviceName", "") ?? "")
                .Where(s => s.Length > 0)
                .GroupBy(s => s)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            string deviceDesc = topDevice != null ? HardwareNamer.DescribeDiskDevicePath(topDevice) : "un disque";

            return new Narration
            {
                Title  = $"Disque en difficulté — {disk.Count} erreurs",
                Icon   = "",
                Chain  = BuildChainPlain(disk),
                Advice = $"{disk.Count} erreurs disque dans la fenêtre, principalement sur : {deviceDesc}. " +
                         "Pas de plantage Windows pour l'instant, mais ce disque envoie des signaux — il faut agir avant le BSOD.",
                Steps = new List<string>
                {
                    "Sauvegarde les données importantes de ce disque tant qu'il fonctionne.",
                    "Tweakly > Diagnostic > Bilan de santé : SMART du disque.",
                    "Si disque SATA : remplace le câble SATA d'abord (souvent la vraie cause, 3 €).",
                    "Si SMART dégradé : remplacement du disque.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Bilan de santé", Tooltip = "SMART du disque.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 5. TDR GPU (Display 4101 / nvlddmkm / amdkmdag) + Application Error → pilote GPU
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryTdrGpu(List<RawEvent> cluster)
        {
            var tdr = cluster.Where(IsTdr).ToList();
            if (tdr.Count == 0) return null;

            var appCrash = cluster.Where(e => e.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(e => e.Time).LastOrDefault();
            string apptag = appCrash != null
                ? appCrash.Data.GetValueOrDefault("AppName", "") ?? ""
                : "";
            string vendor = tdr.Any(e => e.Provider.IndexOf("nvlddmkm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          e.RawFull.IndexOf("nvlddmkm", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "NVIDIA"
                : (tdr.Any(e => e.Provider.IndexOf("amdkmdag", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                e.RawFull.IndexOf("amdkmdag", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "AMD" : "GPU");

            return new Narration
            {
                Title  = $"Pilote {vendor} a cessé de répondre (TDR)",
                Icon   = "",
                Chain  = BuildChainPlain(tdr) + (apptag.Length > 0 ? $" → crash {apptag}" : ""),
                Advice = $"Le pilote {vendor} a mis trop de temps à répondre, Windows l'a réinitialisé (TDR, EventID 4101)" +
                         (apptag.Length > 0 ? $" — c'est ce qui a fait crasher {apptag}." : ".") +
                         $" {tdr.Count} TDR dans la fenêtre. Causes mécaniques fréquentes : pilote {vendor} instable, " +
                         "overclock GPU (Afterburner), surchauffe, ou adaptateur d'affichage virtuel (Parsec/Moonlight/Sunshine). " +
                         "Pour un épisode isolé : pas d'artillerie (pas DDU d'emblée).",
                Steps = new List<string>
                {
                    "Vérifie la TEMPÉRATURE GPU sous charge dans Monitoring : au-delà de ~83 °C, ça déclenche un TDR.",
                    "Si MSI Afterburner / undervolt actif : reviens aux paramètres d'usine et reteste — un undervolt « stable » peut devenir limite avec un nouveau pilote.",
                    "Adaptateur d'affichage VIRTUEL installé (Parsec/Moonlight) ? Désactive-le temporairement (Gestionnaire de périphériques) — cause documentée de TDR.",
                    "Réinstalle un pilote stable sans DDU d'abord : NVIDIA App > Pilotes > Installation personnalisée > « Effectuer une installation propre ».",
                    "Seulement si tout le reste échoue : DDU en mode sans échec puis réinstallation. C'est le dernier recours.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Monitoring",        Tooltip = "Températures GPU en temps réel.",                Kind = LogActionKind.Navigate, Target = "Monitoring" },
                    new() { Label = vendor == "AMD" ? "Pilotes AMD" : "Pilotes NVIDIA",
                            Tooltip = "Page officielle de téléchargement.",
                            Kind = LogActionKind.Url,
                            Target = vendor == "AMD" ? "https://www.amd.com/fr/support" : "https://www.nvidia.fr/Download/index.aspx?lang=fr" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 6. Application Error avec FaultingModule = module SYSTÈME / pilote GPU
        //    → le vrai coupable est le module, pas l'application
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryAppCrashSystemModule(List<RawEvent> cluster)
        {
            var crash = cluster
                .Where(e => e.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Time).LastOrDefault();
            if (crash == null) return null;

            string appName = crash.Data.GetValueOrDefault("AppName", "") ?? "";
            string modName = crash.Data.GetValueOrDefault("ModuleName", "") ?? "";
            if (modName.Length == 0) return null;

            string modLow = modName.ToLowerInvariant();
            // Modules système → cause = système (RAM, Windows update, conflit pilote)
            bool isSysModule = Regex.IsMatch(modLow,
                @"^(ntdll|kernelbase|kernel32|combase|ucrtbase|msvcrt|msvcp|win32u|user32|gdi32|wow64|wow64cpu)\.dll$");
            // Modules pilote GPU NVIDIA / AMD / Intel
            bool isGpuModule = Regex.IsMatch(modLow,
                @"^(nvoglv64|nvwgf2umx|nvd3dum|nvldumdx|atioglxx|aticfx64|amdxc64|igdumdim64|igxelpicd)\.dll$");
            // Module pilote audio courant
            bool isAudioModule = Regex.IsMatch(modLow,
                @"^(audiokse|audiodg|rtkvhd64|nahimic)\.dll$");

            if (!isSysModule && !isGpuModule && !isAudioModule) return null;

            string vraiCoupable = isGpuModule
                ? $"le PILOTE GPU (module {modName})"
                : isAudioModule
                    ? $"le PILOTE AUDIO (module {modName})"
                    : $"un module Windows ({modName}) → typiquement RAM instable, fichiers système corrompus, ou conflit pilote";

            var steps = new List<string>();
            if (isGpuModule)
            {
                steps.Add($"Mets à jour le pilote GPU : c'est lui qui a fait planter {appName}, pas l'appli. Pilotes NVIDIA App ou AMD Adrenalin > Installation propre.");
                steps.Add("Si déjà à jour : essaie un pilote N-1 (le plus récent n'est pas toujours le plus stable).");
            }
            else if (isAudioModule)
            {
                steps.Add("Mets à jour le pilote audio (site du constructeur du PC ou de la carte mère).");
                steps.Add("Si la même appli replante : désactive temporairement les améliorations audio (Panneau de configuration > Son > Propriétés du périphérique).");
            }
            else
            {
                steps.Add("Lance SFC /scannow (CMD admin) puis DISM /Online /Cleanup-Image /RestoreHealth si SFC ne suffit pas.");
                steps.Add("Teste la RAM avec MemTest86 — un module système qui plante TRÈS souvent = barrette qui se trompe sur 1 bit de temps en temps.");
                steps.Add("Force les dernières mises à jour Windows.");
            }

            var actions = new List<LogAction>();
            if (isGpuModule)
            {
                actions.Add(new() { Label = "Pilotes NVIDIA", Tooltip = "Téléchargement officiel.",
                                    Kind = LogActionKind.Url, Target = "https://www.nvidia.fr/Download/index.aspx?lang=fr" });
                actions.Add(new() { Label = "Pilotes AMD",    Tooltip = "Téléchargement officiel.",
                                    Kind = LogActionKind.Url, Target = "https://www.amd.com/fr/support" });
            }
            else if (isSysModule)
            {
                actions.Add(new() { Label = "Lancer SFC /scannow", Tooltip = "Vérifie les fichiers système (5-10 min).",
                                    Kind = LogActionKind.Command, Target = "cmd /k sfc /scannow", Confirm = true });
                actions.Add(new() { Label = "MemTest86", Tooltip = "Test de RAM bootable.",
                                    Kind = LogActionKind.Url, Target = "https://www.memtest86.com/" });
            }

            string who = appName.Length > 0 ? appName : "l'application";
            return new Narration
            {
                Title  = $"{who} a planté — mais le coupable n'est PAS l'appli",
                Icon   = "",
                Chain  = $"{crash.Time:HH:mm:ss} : Application Error 1000 sur {who}, module fautif = {modName}",
                Advice = $"L'événement dit que {who} a crashé. Mais en regardant le DÉTAIL : l'exception s'est levée dans {vraiCoupable}. " +
                         $"C'est {vraiCoupable} qui a planté, et {who} était juste l'appli qui l'utilisait à ce moment. Réinstaller l'appli ne servirait à rien.",
                Steps  = steps,
                Actions = actions,
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 7. Service Control Manager : même service qui crash en boucle
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryServiceCrashLoop(List<RawEvent> cluster)
        {
            var scm = cluster
                .Where(e => e.Provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (scm.Count < 3) return null;

            // Le ServiceName n'est pas toujours nommé : positions 0 ou 1 du Data
            var counts = scm
                .Select(e => e.Data.GetValueOrDefault("param1", "") ??
                             e.Data.GetValueOrDefault("#0", "")    ?? "")
                .Where(s => s.Length > 0)
                .GroupBy(s => s)
                .Select(g => new { Name = g.Key, N = g.Count() })
                .OrderByDescending(x => x.N)
                .ToList();
            if (counts.Count == 0 || counts[0].N < 3) return null;

            string svc = counts[0].Name;
            int n = counts[0].N;

            return new Narration
            {
                Title  = $"Service en boucle de crash — {svc}",
                Icon   = "",
                Chain  = $"{n}× Service Control Manager sur « {svc} » entre {scm.First().Time:HH:mm:ss} et {scm.Last().Time:HH:mm:ss}",
                Advice = $"Le service « {svc} » a planté/redémarré {n}× d'affilée. " +
                         "Windows le relance en boucle — chaque crash pollue le journal. " +
                         "Sauf si ce service est essentiel à Windows, l'arrêt + désactivation règle l'incident sans rien casser.",
                Steps = new List<string>
                {
                    $"services.msc → trouve « {svc} » : si c'est un service tiers (jeu, RGB, antivirus, agent constructeur), MAJ ou désactivation directe.",
                    "Si service système Windows : SFC /scannow puis DISM (vérifier l'intégrité avant de toucher au service).",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Ouvrir services.msc",
                            Tooltip = "Console des services Windows.",
                            Kind = LogActionKind.Diag, Target = "services.msc" },
                    new() { Label = "Désactiver ce service",
                            Tooltip = $"sc stop + sc config start=disabled sur « {svc} ». Réversible via services.msc.",
                            Kind = LogActionKind.Command, Confirm = true,
                            Target = $"cmd /k sc stop \"{svc}\" & sc config \"{svc}\" start= disabled" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 8. Kernel-Power 41 AVEC BugCheckCode != 0 → BSOD MASQUÉ
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryPower41MaskedBsod(List<RawEvent> cluster)
        {
            var p41 = cluster.FirstOrDefault(e =>
                e.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.Id == 41);
            if (p41 == null) return null;

            uint bc = ParseBugCheck(p41);
            if (bc == 0) return null;   // pas de BSOD masqué

            string bcName = HardwareNamer.BugCheckName(bc);
            string family = HardwareNamer.BugCheckFamily(bc);

            string verdict = family switch
            {
                "disk" => "Le code BSOD pointe une erreur d'I/O disque — pas une coupure de courant. Voir la SMART du disque cité.",
                "ram-or-driver" => "Le code BSOD pointe la mémoire ou un pilote — pas une coupure. Tester la RAM (MemTest86) et lister les pilotes récemment installés.",
                "gpu" => "Le code BSOD pointe le pilote GPU (TDR profond). Réinstaller le pilote GPU proprement.",
                "cpu" => "Le code BSOD pointe le CPU (watchdog) — souvent overclock CPU ou XMP trop tendu. Retirer toute personnalisation BIOS.",
                "driver" => "Le code BSOD pointe un pilote tiers défaillant. Lister les pilotes mis à jour récemment, vérifier minidumps.",
                _ => "Le BSOD n'est pas classifiable automatiquement — voir minidump.",
            };

            return new Narration
            {
                Title  = $"Arrêt brutal = BSOD masqué (0x{bc:X} {bcName})",
                Icon   = "",
                Chain  = $"Kernel-Power 41 avec BugCheckCode 0x{bc:X} {(bcName.Length > 0 ? $"({bcName})" : "")} le {p41.Time:dd/MM à HH:mm:ss}",
                Advice = $"Tu vois un Kernel-Power 41 (arrêt brutal), MAIS l'event porte un BugCheckCode 0x{bc:X} : ça veut dire que ce n'était " +
                         $"PAS une coupure de courant — Windows a fait un BSOD juste avant l'arrêt. {verdict}",
                Steps = new List<string>
                {
                    @"Ouvre C:\Windows\Minidump : si un .dmp date de cet incident, c'est le BSOD à analyser.",
                    "Outil simple pour lire le minidump : BlueScreenView (NirSoft, gratuit, portable).",
                    family == "disk"     ? "Vérifie la SMART de tes disques (Tweakly > Diagnostic > Bilan de santé)." :
                    family == "ram-or-driver" ? "Désactive XMP/EXPO dans le BIOS, teste 48 h. Si OK = XMP instable." :
                    family == "gpu"      ? "Réinstalle le pilote GPU (installation propre)." :
                    family == "cpu"      ? "BIOS aux valeurs par défaut, retire tout overclock CPU/BCLK." :
                                           "Liste les pilotes mis à jour récemment dans le Gestionnaire de périphériques.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Bilan de santé", Tooltip = "BSOD, SMART, stabilité.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                    new() { Label = "BlueScreenView", Tooltip = "Visualiseur de minidumps (NirSoft).",
                            Kind = LogActionKind.Url, Target = "https://www.nirsoft.net/utils/blue_screen_view.html" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 9. Kernel-Power 41 SEUL (sans BugCheck dans la fenêtre) → vraie coupure
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryPower41RealOutage(List<RawEvent> cluster)
        {
            var p41 = cluster.FirstOrDefault(e =>
                e.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.Id == 41);
            if (p41 == null) return null;
            if (ParseBugCheck(p41) != 0) return null;   // BSOD masqué (autre scénario)

            // Pas d'autre indice → c'est probablement une coupure réelle ou un bouton power
            return new Narration
            {
                Title  = "Arrêt brutal sans BSOD préalable",
                Icon   = "",
                Chain  = $"Kernel-Power 41 isolé (pas de BugCheck) le {p41.Time:dd/MM à HH:mm:ss}",
                Advice = "Cet event est un arrêt brutal SANS BSOD préalable — donc c'est probablement une coupure réelle : " +
                         "panne secteur, multiprise éteinte, bouton power maintenu, ou (sur PC fixe) alimentation qui décroche sur un pic de charge. " +
                         "Si tu te souviens d'une cause physique (orage, reset volontaire) : ignore. Sinon, surveille la répétition.",
                Steps = new List<string>
                {
                    "Si tu te souviens de la cause physique (reset, orage, batterie débranchée) : rien à faire.",
                    "Sinon : surveille — la RÉPÉTITION d'arrêts brutaux signale une PSU fatiguée ou une carte mère.",
                    "PC fixe avec GPU récent + PSU > 5 ans : forte suspicion alim sous-dimensionnée sur les pics.",
                    "PC portable : tester sans la batterie (la batterie peut couper net en fin de vie).",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Bilan de santé", Tooltip = "Historique BSOD/arrêts brutaux.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                },
            };
        }

        // ──────────────────────────────────────────────────────────────────────
        // 10. BSOD générique (pas de scénario plus précis matché)
        //     On exploite quand même le BugCheckCode pour orienter, sans inventer.
        // ──────────────────────────────────────────────────────────────────────
        private static Narration? TryGenericBsod(List<RawEvent> cluster)
        {
            var bsod = FindBsod(cluster);
            if (bsod == null) return null;

            string name = HardwareNamer.BugCheckName(bsod.bugcheck);
            string family = HardwareNamer.BugCheckFamily(bsod.bugcheck);
            string hexFmt = $"0x{bsod.bugcheck:X}" + (name.Length > 0 ? $" ({name})" : "");
            string verdict = family switch
            {
                "disk"          => "Code BSOD = erreur d'I/O disque. Suspecter le disque/contrôleur/câble SATA.",
                "ram-or-driver" => "Code BSOD = mémoire OU pilote corrompu. Tester la RAM (MemTest86) avant tout, puis lister les pilotes récents.",
                "gpu"           => "Code BSOD = TDR/GPU. Réinstaller le pilote GPU proprement.",
                "cpu"           => "Code BSOD = watchdog CPU. Retirer overclock CPU + XMP/EXPO.",
                "driver"        => "Code BSOD = pilote tiers défaillant. Lister les pilotes mis à jour récemment.",
                _ => "Code BSOD non classifiable automatiquement — voir le minidump pour identifier le pilote fautif.",
            };

            return new Narration
            {
                Title  = $"BSOD {hexFmt}",
                Icon   = "",
                Chain  = $"{bsod.ev.Time:HH:mm:ss} : BugCheck {hexFmt}",
                Advice = $"BSOD enregistré le {bsod.ev.Time:dd/MM à HH:mm:ss}. Code : {hexFmt}. {verdict}",
                Steps  = new List<string>
                {
                    @"Ouvre C:\Windows\Minidump : un .dmp daté de l'incident permet d'identifier précisément le pilote fautif.",
                    "Visualiseur recommandé : BlueScreenView (NirSoft, gratuit).",
                    family == "disk"     ? "Tweakly > Diagnostic > Bilan de santé : SMART des disques." :
                    family == "ram-or-driver" ? "Désactive XMP/EXPO, teste 48 h. Si OK = mémoire instable." :
                    family == "gpu"      ? "Réinstalle le pilote GPU (installation propre dans NVIDIA App / AMD Adrenalin)." :
                    family == "cpu"      ? "Reset BIOS aux valeurs par défaut, retire tout OC." :
                                           "Liste les pilotes mis à jour récemment dans le Gestionnaire de périphériques.",
                },
                Actions = new List<LogAction>
                {
                    new() { Label = "Voir Bilan de santé", Tooltip = "BSOD, SMART, stabilité.", Kind = LogActionKind.Navigate, Target = "Diagnostic" },
                    new() { Label = "BlueScreenView",       Tooltip = "Visualiseur de minidumps (NirSoft).",
                            Kind = LogActionKind.Url, Target = "https://www.nirsoft.net/utils/blue_screen_view.html" },
                },
            };
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════════════

        private static bool IsDiskOrNtfs(RawEvent e)
        {
            var p = e.Provider.ToLowerInvariant();
            return p == "disk" || p.Contains("ntfs") || p.Contains("volmgr") || p.Contains("storahci") || p.Contains("stornvme");
        }

        private static bool IsTdr(RawEvent e)
        {
            var p = e.Provider.ToLowerInvariant();
            if (p.Contains("nvlddmkm") || p.Contains("amdkmdag") || p.Contains("amdwddmg")) return true;
            // « Display » Event 4101 = TDR loggé sous une source générique
            if (p == "display" && e.Id == 4101) return true;
            return false;
        }

        private sealed class BsodHit { public RawEvent ev = null!; public uint bugcheck; }

        private static BsodHit? FindBsod(List<RawEvent> cluster)
        {
            // BSOD = WER-SystemErrorReporting ID 1001, ou Kernel-Power 41 avec BugCheckCode, ou BugCheck 1001
            foreach (var e in cluster.OrderBy(x => x.Time))
            {
                uint bc = ParseBugCheck(e);
                if (bc != 0) return new BsodHit { ev = e, bugcheck = bc };
            }
            return null;
        }

        // Récupère un BugCheckCode (hex ou décimal) depuis l'EventData. 0 si absent.
        private static uint ParseBugCheck(RawEvent e)
        {
            // Champ direct (Kernel-Power 41 nomme BugcheckCode ; BugCheck 1001 utilise des positions)
            foreach (var key in new[] { "BugcheckCode", "BugCheckCode", "param1", "#0" })
            {
                if (!e.Data.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) continue;
                v = v.Trim();
                if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (uint.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
                }
                else if (uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                {
                    if (dec != 0) return dec;
                }
            }
            // Fallback : regex sur le texte plein (« BugCheck: 7A » dans le message)
            var m = Regex.Match(e.RawFull ?? "", @"(?:Bug[\s_]?Check|0x)\s*:?\s*([0-9A-Fa-f]{1,8})", RegexOptions.IgnoreCase);
            if (m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hh))
                return hh;
            return 0;
        }

        // WHEA severity : champ numérique (1=Fatal/Uncorrected, 2=Corrected, 3=Warning, 4=Informational)
        private static int SeverityValue(RawEvent e)
        {
            if (e.Data.TryGetValue("Severity", out var s) && int.TryParse(s, out var sv)) return sv;
            return 0;
        }

        // ErrorSource WHEA : entier (1=MachineCheck, 4=NMI, 5=BootError, 7=PCIeError, etc.)
        private static string MapWheaSource(string source)
        {
            if (int.TryParse(source, out int n))
            {
                return n switch
                {
                    1 => "CPU (Machine Check)",
                    2 => "CPU (Corrected Machine Check)",
                    3 => "NMI (parité bus)",
                    4 => "PCIe (lien)",
                    5 => "boot error",
                    7 => "PCIe Express AER",
                    _ => $"source WHEA #{n}",
                };
            }
            return source.Length > 0 ? source : "matérielle";
        }

        private static string BuildChain(List<RawEvent> before, RawEvent bsod, uint code)
        {
            var sb = new StringBuilder();
            foreach (var e in before.Take(4))
                sb.Append($"{e.Time:HH:mm:ss} {e.Provider}/{e.Id}   →   ");
            sb.Append($"{bsod.Time:HH:mm:ss} BSOD 0x{code:X}");
            return sb.ToString();
        }

        private static string BuildChainPlain(IEnumerable<RawEvent> evs)
        {
            var list = evs.OrderBy(e => e.Time).Take(5).ToList();
            return string.Join("   →   ",
                list.Select(e => $"{e.Time:HH:mm:ss} {e.Provider}/{e.Id}"));
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Détection « arrêt clean » : requête XPath ciblée sur les 2 marqueurs
        //  d'arrêt propre Windows (1074 User32 + 6006 EventLog), dans une fenêtre
        //  de ±90 s autour de l'event Kernel-Power 41 candidat.
        //
        //  Ces events sont Level=4 (Information) → exclus du ReadRaw principal qui
        //  filtre Level 1+2. D'où la requête séparée, courte et précise.
        //
        //  RÈGLE : si AU MOINS UN des deux est présent dans la fenêtre → arrêt clean
        //  → on supprime le faux incident. Un crash n'a JAMAIS le temps d'écrire un
        //  6006, et un BSOD ne génère JAMAIS un 1074 (le système crashe avant).
        // ──────────────────────────────────────────────────────────────────────
        private static (bool clean, string initiator, string reason) LookupCleanShutdownMarkers(DateTime around)
        {
            try
            {
                // Fenêtre élargie à ±5 min (avant ±90 s) : sur certains PC un arrêt programmé
                // attend la fermeture d'apps coriaces (jeux, IDE) pendant 2-3 min avant que le
                // service Power loggue son 41 → on rate sinon le 1074 initial.
                DateTime from = around.AddMinutes(-5);
                DateTime to   = around.AddSeconds(30);
                // XPath : System log, EventID 1074 ou 6006, dans la fenêtre.
                // (TimeCreated[@SystemTime>='...' and @SystemTime<='...'] avec format ISO 8601 UTC)
                string fromIso = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string toIso   = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string xpath =
                    $"*[System[(EventID=1074 or EventID=6006) and " +
                    $"TimeCreated[@SystemTime>='{fromIso}' and @SystemTime<='{toIso}']]]";

                var q = new EventLogQuery("System", PathType.LogName, xpath);
                using var reader = new EventLogReader(q);
                EventRecord? rec;
                string initiator = "", reason = "";
                bool found = false;
                int guard = 0;
                while ((rec = reader.ReadEvent()) != null && guard++ < 20)
                {
                    using (rec)
                    {
                        found = true;
                        if (rec.Id == 1074)
                        {
                            // Position 0 = programme/process initiateur, position 4 = raison texte.
                            try
                            {
                                var props = rec.Properties;
                                if (props != null && props.Count > 0)
                                    initiator = props[0]?.Value?.ToString() ?? "";
                                if (props != null && props.Count > 4)
                                    reason    = props[4]?.Value?.ToString() ?? "";
                            }
                            catch { }
                        }
                    }
                }
                return (found, initiator, reason);
            }
            catch { return (false, "", ""); }
        }
    }
}
