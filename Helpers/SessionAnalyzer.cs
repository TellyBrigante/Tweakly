using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Moteur d'analyse d'une capture de session de jeu.
    ///
    /// ⚠ RÈGLE ABSOLUE (apprise dans la douleur, 2026-06-13) : ne JAMAIS énoncer une
    /// affirmation non prouvée par la donnée. Les versions précédentes inventaient
    /// des « patterns périodiques » sur 17 points, devinaient un « Garbage Collector »
    /// par famille de moteur, affirmaient un « throttle » sans mesure. Tout ça a été
    /// SUPPRIMÉ. Cette version ne dit QUE ce que les frametimes prouvent :
    ///   • statistiques réelles (fps moyen, 1 % / 0,1 % low, frametimes) ;
    ///   • mode de présentation (réel, actionnable) ;
    ///   • drops détectés + leur amplitude + classification CPU/GPU par frame ;
    ///   • coupable applicatif SEULEMENT si un autre process qui présente des frames
    ///     spike de façon corrélée (mécanisme prouvé : Brave → drops RL, 4/4) ;
    ///   • quand aucune cause n'est prouvée : on le DIT, sans en inventer une.
    /// </summary>
    public static class SessionAnalyzer
    {
        // ── Seuils de détection ──
        // Drop par-frame : frametime > 2× la médiane ET ≥ seuil absolu. Le double
        // critère évite les faux positifs sur très haut framerate et la cécité sur
        // les jeux cappés. Calibré sur captures RL réelles (319 fps p50 3,1 ms).
        public const double DropMultiplier = 2.0;
        public const double DropMinMs = 5.0;

        // Trim des bordures : les 1res/dernières secondes = clic + alt-tab + menu,
        // pas du jeu. On les exclut de TOUTE analyse.
        public const double TrimHeadMs = 12000;
        public const double TrimTailMs = 8000;

        public enum DropCause { CpuBound, GpuBound, DisplaySync, ShaderCompile, Mixed }
        public enum RecoSeverity { Info, Warn, Crit }

        public sealed class Drop
        {
            public double TimeMs;
            public double FrameTimeMs;
            public double CpuBusyMs;
            public double CpuWaitMs;            // le thread du jeu ATTENDAIT (bloqué) pendant la frame
            public double GpuBusyMs;
            public DropCause Cause;
            public string? CulpritProcess;      // (legacy, non utilisé — plus d'accusation)
            public double CulpritFrametimeMs;
            /// <summary>true si le jeu CALCULAIT (CpuBusy domine) → moteur. false s'il ATTENDAIT.</summary>
            public bool GameWasComputing;
        }


        public sealed class Recommendation
        {
            public string Title = "";
            public string Explanation = "";
            public string? ActionLabel;
            public string? ActionTarget;
            public RecoSeverity Severity;
        }

        public sealed class ChartPoint { public double T; public double Ft; }

        public sealed class Report
        {
            public DateTime CapturedAtUtc;
            public string GameExe = "";
            public string GameDisplay = "";
            public bool GameKnown;
            public int FrameCount;
            public double DurationSec;
            public double FpsAvg;
            public double FpsP50;
            public double FpsOnePctLow;
            public double FpsZeroOnePctLow;
            public double FrametimeP50Ms;
            public double FrametimeP99Ms;
            public double FrametimeMaxMs;
            public double FrametimeStdMs;        // écart-type du frametime
            public double FrametimeCvPct;        // coefficient de variation (SD/moyenne) — la RÉGULARITÉ
            public double PerceivedFpsLow;       // 1000/(median+std) : le bas de la bande perçue
            public double PerceivedFpsHigh;      // 1000/(median-std)
            public double InputLatencyAvgMs;
            public string PresentMode = "";
            public bool PresentModeOptimal;
            public double CpuGlobalAtDropsPct;   // % CPU global médian au moment des drops (NaN si pas de sample)
            public List<Drop> Drops = new();
            public List<Recommendation> Recommendations = new();
            public List<ChartPoint> Chart = new();
            public string Verdict = "";
            public int Score;
            // Contexte machine (one-shot, vrai) — pour éliminer les fausses pistes
            public string GamePath = "";
            public string GameDiskKind = "Unknown";
            public double TotalRamGb;
            public int MonitorHz;
            public bool VbsRunning;              // hyperviseur/VBS réellement actif (taxe virtu)
            public bool HvciEnabled;             // Intégrité de la mémoire (HVCI) ON ? (≠ VBS running)
            public int CompetitiveFps = 144;     // seuil « bon » pour ce jeu
        }

        // ───────────────────────── analyse ─────────────────────────
        public static Report Analyze(SessionCapture cap)
        {
            var r = new Report { CapturedAtUtc = cap.StartedUtc };
            var pf = cap.MainGame();
            if (pf == null || pf.Frames.Count < 60)
            {
                r.Verdict = "Pas assez de frames capturées pour analyser (lance la capture pendant que tu joues, ≥ 30 s).";
                return r;
            }

            r.GameExe = pf.Exe;
            var game = GameDatabase.Lookup(pf.Exe);
            r.GameKnown = game != null;
            r.GameDisplay = game?.Display ?? pf.Exe;
            r.CompetitiveFps = game?.CompetitiveFps ?? 144;
            r.PresentMode = pf.PresentMode ?? "";
            r.PresentModeOptimal = r.PresentMode.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase);

            // Contexte machine (vrai, one-shot)
            if (cap.SystemContext != null)
            {
                r.TotalRamGb = cap.SystemContext.TotalRamGb;
                r.MonitorHz = cap.SystemContext.MonitorRefreshRate;
                if (cap.SystemContext.ExePaths.TryGetValue(pf.Exe, out string? gp) && !string.IsNullOrEmpty(gp))
                {
                    r.GamePath = gp;
                    r.GameDiskKind = cap.SystemContext.StorageOf(gp).ToString();
                }
                r.VbsRunning = cap.SystemContext.VbsRunning;
                r.HvciEnabled = cap.SystemContext.HvciEnabled;
            }

            // Trim bordures. Validé en réel 2026-06-13 : les freezes catastrophiques
            // (402 ms, 1059 ms) étaient l'alt-tab d'entrée + l'arrêt du record, jamais
            // du jeu. Deux niveaux :
            //  (a) trim temporel large si la session est assez longue ;
            //  (b) GARDE ANTI-BORDURE inconditionnel : toute frame géante (> 100 ms) dans
            //      les 5 premières / 5 dernières secondes = alt-tab/menu, jamais un drop
            //      réel — exclue quelle que soit la durée de session.
            var all = pf.Frames;
            double tMin = all.Min(f => f.TimeMs), tMax = all.Max(f => f.TimeMs);
            List<FrameRecord> frames = (tMax - tMin > TrimHeadMs + TrimTailMs + 30000)
                ? all.Where(f => f.TimeMs >= tMin + TrimHeadMs && f.TimeMs <= tMax - TrimTailMs).ToList()
                : all;
            frames = frames.Where(f => !(
                        f.FrameTimeMs > 100 &&
                        (f.TimeMs <= tMin + 5000 || f.TimeMs >= tMax - 5000))).ToList();
            r.FrameCount = frames.Count;
            r.DurationSec = frames.Count > 0 ? (frames.Max(f => f.TimeMs) - frames.Min(f => f.TimeMs)) / 1000.0 : 0;

            // Stats
            var ft = frames.Select(f => f.FrameTimeMs).Where(v => v > 0 && !double.IsNaN(v)).OrderBy(v => v).ToArray();
            if (ft.Length == 0) { r.Verdict = "Frametimes illisibles."; return r; }
            r.FrametimeP50Ms = Pct(ft, 0.50);
            r.FrametimeP99Ms = Pct(ft, 0.99);
            r.FrametimeMaxMs = ft[^1];
            double mean = ft.Average();
            r.FpsAvg = 1000.0 / mean;
            r.FpsP50 = 1000.0 / r.FrametimeP50Ms;
            r.FpsOnePctLow = 1000.0 / r.FrametimeP99Ms;
            r.FpsZeroOnePctLow = 1000.0 / Pct(ft, 0.999);

            // RÉGULARITÉ (le métrique clé pour le haut framerate / jeu de précision) :
            // écart-type du frametime → coefficient de variation. Mesure la CONSTANCE,
            // pas la moyenne. Validé : RL @ 319 fps médian a un CV de 16 % = jitter
            // perçu sur 320 Hz (compteur qui oscille 274-380 au lieu d'un 318 stable).
            r.FrametimeStdMs = Math.Sqrt(ft.Select(v => (v - mean) * (v - mean)).Average());
            r.FrametimeCvPct = mean > 0 ? 100.0 * r.FrametimeStdMs / mean : 0;
            r.PerceivedFpsLow = 1000.0 / (r.FrametimeP50Ms + r.FrametimeStdMs);
            r.PerceivedFpsHigh = 1000.0 / Math.Max(0.3, r.FrametimeP50Ms - r.FrametimeStdMs);

            var inp = frames.Where(f => !double.IsNaN(f.InputLatencyMs) && f.InputLatencyMs > 0).ToArray();
            r.InputLatencyAvgMs = inp.Length >= 30 ? inp.Average(f => f.InputLatencyMs) : double.NaN;

            // Drops : par-frame + creux soutenus (les 2 sont prouvés par la donnée)
            double dropThr = Math.Max(r.FrametimeP50Ms * DropMultiplier, DropMinMs);
            var byFrame = frames.Where(f => f.FrameTimeMs > dropThr).Select(Classify).ToList();
            var sustained = DetectSustained(frames, r.FrametimeP50Ms)
                .Where(s => !byFrame.Any(d => Math.Abs(d.TimeMs - s.TimeMs) < 1000)).ToList();
            r.Drops = byFrame.Concat(sustained).OrderBy(d => d.TimeMs).ToList();

            // Shader-comp : UNIQUEMENT si décroissance d'amplitude prouvée en début de session
            MarkShaderCompile(r.Drops);

            var runtimeDrops = r.Drops.Where(d => d.Cause != DropCause.ShaderCompile).ToList();

            // CPU global au moment des drops (contexte, pas une cause)
            r.CpuGlobalAtDropsPct = CpuGlobalAtDrops(runtimeDrops, cap.Samples);

            // Pas de corrélateur de « coupable » tiers : structurellement incapable de
            // distinguer cause et co-victime (cf. BuildRecommendations). r.Culprits reste vide.

            // Graphe
            r.Chart = Downsample(frames, 1000);

            // Score + verdict + recos — uniquement du prouvé
            r.Score = ScoreFor(r);
            r.Recommendations = BuildRecommendations(r, game, runtimeDrops);
            r.Verdict = BuildVerdict(r, runtimeDrops);
            return r;
        }

        // ───────────────────────── classification ─────────────────────────
        private static Drop Classify(FrameRecord f)
        {
            var d = new Drop
            {
                TimeMs = f.TimeMs,
                FrameTimeMs = f.FrameTimeMs,
                CpuBusyMs = double.IsNaN(f.CpuBusyMs) ? 0 : f.CpuBusyMs,
                CpuWaitMs = double.IsNaN(f.CpuWaitMs) ? 0 : f.CpuWaitMs,
                GpuBusyMs = double.IsNaN(f.GpuBusyMs) ? 0 : f.GpuBusyMs,
            };
            double cpu = d.FrameTimeMs > 0 ? d.CpuBusyMs / d.FrameTimeMs : 0;
            double gpu = d.FrameTimeMs > 0 ? d.GpuBusyMs / d.FrameTimeMs : 0;
            d.Cause = cpu >= 0.70 ? DropCause.CpuBound
                    : gpu >= 0.70 ? DropCause.GpuBound
                    : (cpu < 0.30 && gpu < 0.30 ? DropCause.DisplaySync : DropCause.Mixed);
            // Le jeu CALCULAIT-il, ou ATTENDAIT-il ? CpuBusy > CpuWait → calcul (moteur).
            // CpuWait domine sur une frame longue → le thread était bloqué : c'est LÀ,
            // et seulement là, qu'une cause externe (autre process, I/O, pilote) est plausible.
            d.GameWasComputing = d.CpuBusyMs >= d.CpuWaitMs;
            return d;
        }

        /// <summary>Creux soutenu : fenêtre 1 s dont le fps moyen tombe < 70 % du fps de référence.</summary>
        private static List<Drop> DetectSustained(List<FrameRecord> frames, double p50Ms)
        {
            var res = new List<Drop>();
            if (frames.Count < 30 || p50Ms <= 0) return res;
            double minFps = (1000.0 / p50Ms) * 0.70;
            int i = 0;
            while (i < frames.Count)
            {
                double t0 = frames[i].TimeMs, t1 = t0 + 1000;
                int j = i; while (j < frames.Count && frames[j].TimeMs <= t1) j++;
                int cnt = j - i;
                if (cnt >= 10)
                {
                    double wMs = frames[j - 1].TimeMs - t0;
                    double fps = wMs > 100 ? 1000.0 * cnt / wMs : 999;
                    if (fps < minFps)
                    {
                        var win = frames.GetRange(i, cnt);
                        double avgFt = win.Average(f => f.FrameTimeMs);
                        double cpuAvg = win.Where(f => !double.IsNaN(f.CpuBusyMs)).Select(f => f.CpuBusyMs).DefaultIfEmpty(0).Average();
                        double gpuAvg = win.Where(f => !double.IsNaN(f.GpuBusyMs)).Select(f => f.GpuBusyMs).DefaultIfEmpty(0).Average();
                        var d = new Drop { TimeMs = (t0 + frames[j - 1].TimeMs) / 2, FrameTimeMs = avgFt, CpuBusyMs = cpuAvg, GpuBusyMs = gpuAvg };
                        double c = avgFt > 0 ? cpuAvg / avgFt : 0, g = avgFt > 0 ? gpuAvg / avgFt : 0;
                        d.Cause = c >= 0.70 ? DropCause.CpuBound : g >= 0.70 ? DropCause.GpuBound : DropCause.Mixed;
                        res.Add(d);
                        i = j; continue;
                    }
                }
                i = j > i ? j : i + 1;
            }
            return res;
        }

        /// <summary>
        /// Marque les drops shader-compile UNIQUEMENT si la signature est prouvée :
        /// concentrés dans les 30 premières s ET amplitude décroissante (2e moitié des
        /// drops précoces < 70 % de la 1re). Sans décroissance = pas du shader.
        /// </summary>
        private static void MarkShaderCompile(List<Drop> drops)
        {
            var early = drops.Where(d => d.TimeMs < 30000).OrderBy(d => d.TimeMs).ToList();
            if (early.Count < 3) return;
            int half = early.Count / 2;
            double a1 = early.Take(half).Average(d => d.FrameTimeMs);
            double a2 = early.Skip(half).Average(d => d.FrameTimeMs);
            if (a2 < a1 * 0.7)
                foreach (var d in early) d.Cause = DropCause.ShaderCompile;
        }

        private static double CpuGlobalAtDrops(List<Drop> drops, List<SysSample> samples)
        {
            if (drops.Count == 0 || samples.Count == 0) return double.NaN;
            var vals = new List<double>();
            foreach (var d in drops)
            {
                var near = samples.Where(s => Math.Abs(s.ElapsedMs - d.TimeMs) <= 1500)
                                  .Select(s => s.CpuLoadPct).Where(v => !double.IsNaN(v));
                if (near.Any()) vals.Add(near.Max());
            }
            if (vals.Count == 0) return double.NaN;
            vals.Sort();
            return vals[vals.Count / 2];
        }

        // ───────────────────────── recommandations (que du prouvé) ─────────────────────────
        private static List<Recommendation> BuildRecommendations(Report r, GameDatabase.Game? game, List<Drop> runtime)
        {
            var list = new List<Recommendation>();

            // ⚠ PAS d'accusation de process tiers. Un corrélateur basé sur la seule
            // présentation de frames est STRUCTURELLEMENT incapable de distinguer une
            // cause d'une co-victime : lors d'un hoquet système, tout ce qui dessine
            // (jeu, navigateur, overlay) spike ensemble. Pointer l'app de fond ouverte
            // = faux positif systématique (vécu : Brave puis webview2…). Supprimé.

            // (1.5) RÉGULARITÉ insuffisante — le cœur du ressenti en haut framerate.
            // S'affiche quand le frametime n'est pas assez constant (CV > 12 %), avec
            // les leviers RÉELS pour le resserrer (pas pour gagner des fps moyens).
            if (r.FrametimeCvPct > 12)
            {
                var levers = new List<string>();
                if (r.VbsRunning) levers.Add(VbsLever(r));
                levers.Add("Plafonner les FPS à une valeur stable (cap moteur du jeu + G-Sync/VRR) : empêche le frametime de courir après le max et le resserre nettement.");
                levers.Add("Fermer une à une les apps de fond et recapturer : la comparaison d'historique te dira si l'une élargit vraiment ton frametime (sans deviner).");
                levers.Add("Plan d'alim « Performances ultimes » : évite les micro-baisses de fréquence qui font varier le temps de calcul des frames.");
                string severity = r.FrametimeCvPct > 20 ? "instable" : "irrégulier";
                list.Add(new Recommendation
                {
                    Title = $"Frametime {severity} (CV {r.FrametimeCvPct:0} %) — c'est ce que tu ressens sur {(r.MonitorHz > 0 ? r.MonitorHz + " Hz" : "ton écran")}",
                    Explanation =
                        $"Ton fps médian est {r.FpsP50:0} mais le frametime oscille (±{r.FrametimeStdMs:0.##} ms), donc le compteur bouge entre {r.PerceivedFpsLow:0} et {r.PerceivedFpsHigh:0} fps au lieu de rester stable. Sur un jeu de précision en haut framerate, c'est cette inconstance qui se sent, pas la moyenne. Cible : CV sous ~8 % pour un ressenti « collé ».\n\n" +
                        "Leviers pour resserrer le frametime (teste-les un par un, recapture après chacun) :\n" +
                        string.Join("\n", levers.Select(l => "• " + l)),
                    ActionLabel = r.VbsRunning ? null : null,
                    Severity = r.FrametimeCvPct > 20 ? RecoSeverity.Crit : RecoSeverity.Warn,
                });
            }

            // (2) Mode de présentation non optimal (réel, actionnable)
            if (!r.PresentModeOptimal && !string.IsNullOrEmpty(r.PresentMode))
                list.Add(new Recommendation
                {
                    Title = "Le jeu rend en mode composé",
                    Explanation = $"Présentation détectée : {r.PresentMode}. Le compositeur Windows (DWM) est dans le chemin de rendu — il ajoute de la latence et peut introduire du stutter. Passe le jeu en plein écran exclusif (ou borderless si le jeu supporte le flip optimisé).",
                    Severity = RecoSeverity.Warn,
                });

            // (3) Shader compile (rassure, pas de coupable)
            int shaderN = r.Drops.Count(d => d.Cause == DropCause.ShaderCompile);
            if (shaderN >= 3)
                list.Add(new Recommendation
                {
                    Title = "Saccades en début de partie : compilation de shaders",
                    Explanation = $"{shaderN} saccades dans les 30 premières secondes, amplitude décroissante = signature de la compilation de shaders. Normal sur la 1re partie d'une map ; ne se reproduira pas tant que les pilotes GPU ne changent pas.",
                    Severity = RecoSeverity.Info,
                });

            int runtimeCpu = runtime.Count(d => d.Cause == DropCause.CpuBound);
            int runtimeGpu = runtime.Count(d => d.Cause == DropCause.GpuBound);

            // (4) GPU-bound massif → fait carte saturée, vérité directe
            if (runtime.Count >= 5 && runtimeGpu >= runtime.Count * 0.6)
                list.Add(new Recommendation
                {
                    Title = "Tes drops sont GPU-bound",
                    Explanation = $"{runtimeGpu}/{runtime.Count} drops viennent de la carte graphique saturée (le GPU n'a pas fini sa frame à temps). Baisse les réglages graphiques lourds (ombres, textures, résolution interne) ou active DLSS/FSR/XeSS si dispo. Aucun réglage Windows ne libérera de FPS dans ce cas.",
                    Severity = RecoSeverity.Info,
                });

            // ── ÉTAT : session top / drops sans cause / 0 drop ──
            // Tout ce qui suit ne s'affiche que si on n'est pas déjà en pur profil GPU.
            bool alreadyExplained = runtime.Count >= 5 && runtimeGpu >= runtime.Count * 0.6;

            if (!alreadyExplained)
            {
                double compFt = 1000.0 / Math.Max(1, r.CompetitiveFps);
                if (runtime.Count == 0)
                {
                    list.Add(new Recommendation
                    {
                        Title = "Session propre, rien à corriger",
                        Explanation = $"0 drop en jeu sur {r.DurationSec:0} s à {r.FpsAvg:0} fps de moyenne (0,1 % low {r.FpsZeroOnePctLow:0} fps). Refais une mesure après un changement pour comparer.",
                        Severity = RecoSeverity.Info,
                    });
                }
                else
                {
                    var below = runtime.Where(d => d.FrameTimeMs > compFt).ToList();
                    double worstFt = runtime.Max(d => d.FrameTimeMs);
                    int worstFps = (int)Math.Round(1000.0 / worstFt);
                    if (below.Count == 0 && r.FpsZeroOnePctLow >= r.CompetitiveFps)
                    {
                        list.Add(new Recommendation
                        {
                            Title = "Session TOP — aucune action requise",
                            Explanation = $"Tes {runtime.Count} drops restent tous au-dessus du seuil compétitif de {r.CompetitiveFps} fps (pire : {worstFps} fps inst.). 0,1 % low à {r.FpsZeroOnePctLow:0} fps. Setup stable pour ce jeu.",
                            Severity = RecoSeverity.Info,
                        });
                    }
                    else
                    {
                        list.AddRange(BuildNoThirdPartyRecos(r, runtime, below, worstFt, worstFps, runtimeCpu));
                    }
                }
            }

            // Levier VBS/hyperviseur : seulement si VBS tourne, drops CPU-bound, ET que
            // la reco « régularité » ne l'a pas déjà mentionné (CV ≤ 12).
            if (r.VbsRunning && r.FrametimeCvPct <= 12
                && runtime.Count(d => d.Cause == DropCause.CpuBound) >= 5)
            {
                list.Add(new Recommendation
                {
                    Title = "Virtualisation (VBS) active — taxe de scheduling sur le thread du jeu",
                    Explanation = "Le noyau Windows tourne au-dessus d'un hyperviseur, ce qui ajoute du jitter de scheduling à chaque frame — sur un jeu mono-thread sensible, ça gonfle les drops CPU-bound.\n" + VbsLever(r),
                    Severity = RecoSeverity.Warn,
                });
            }

            return list;
        }

        /// <summary>
        /// Levier VBS ADAPTÉ à l'état RÉEL : on ne dit « désactive l'Intégrité de la
        /// mémoire » QUE si elle est ON. Si HVCI est déjà OFF mais que l'hyperviseur tourne
        /// quand même, la cause est ailleurs (Plateforme de machine virtuelle / Hyper-V /
        /// Sandbox / Credential Guard) → on pointe le bon levier (bug de pertinence vécu).
        /// </summary>
        private static string VbsLever(Report r)
        {
            if (r.HvciEnabled)
                return "Désactive l'Intégrité de la mémoire (Sécurité Windows > Sécurité des appareils > Isolation du noyau), reboot, recapture. C'est le levier le plus lourd de la taxe virtualisation.";
            return "⚠ Ton Intégrité de la mémoire est DÉJÀ désactivée, mais l'hyperviseur tourne quand même — la cause vient d'une autre fonctionnalité (Plateforme de machine virtuelle, Hyper-V, Bac à sable Windows, ou Credential Guard). Si tu n'utilises AUCUN de ces outils : désactive « Plateforme de machine virtuelle » et « Hyper-V » dans Fonctionnalités Windows (ou, en admin : bcdedit /set hypervisorlaunchtype off), reboot, recapture. ⚠ ça casse WSL / Sandbox / Hyper-V si tu t'en sers.";
        }

        /// <summary>
        /// Drops réels. La distinction décisive vient de la donnée PresentMon elle-même :
        /// le thread du jeu CALCULAIT-il (CpuBusy) ou ATTENDAIT-il (CpuWait) pendant la frame ?
        ///  (A) CpuBusy domine → le moteur produisait une frame lourde. Cause = le jeu.
        ///      Un process tiers ne peut PAS être en cause (le jeu tournait tout du long).
        ///  (B) CpuWait domine → le thread était BLOQUÉ. C'est le seul cas où une cause
        ///      externe (autre process, I/O, pilote, attente GPU) est physiquement plausible.
        /// </summary>
        private static List<Recommendation> BuildNoThirdPartyRecos(Report r, List<Drop> runtime, List<Drop> below, double worstFt, int worstFps, int runtimeCpu)
        {
            var res = new List<Recommendation>();

            // Combien de drops « jeu calculait » vs « jeu bloqué », preuve CpuBusy vs CpuWait.
            int computing = runtime.Count(d => d.GameWasComputing);
            int blocked = runtime.Count - computing;
            var cpuDrops = runtime.Where(d => d.FrameTimeMs > 0).ToList();
            double gameCpuShare = cpuDrops.Count > 0
                ? cpuDrops.Select(d => d.CpuBusyMs / d.FrameTimeMs).OrderBy(x => x).ElementAt(cpuDrops.Count / 2)
                : 0;

            var facts = new List<string>();
            if (!double.IsNaN(r.CpuGlobalAtDropsPct))
                facts.Add(r.CpuGlobalAtDropsPct >= 85
                    ? $"charge CPU globale {r.CpuGlobalAtDropsPct:0} %"
                    : $"charge CPU globale {r.CpuGlobalAtDropsPct:0} % (un seul thread en cause, pas le CPU entier)");
            if (r.GameDiskKind == "NVMe") facts.Add("jeu sur NVMe (accès disque écarté)");
            else if (r.GameDiskKind == "HDD") facts.Add("jeu sur HDD (un accès disque peut expliquer un freeze long)");
            if (r.TotalRamGb >= 16) facts.Add($"{r.TotalRamGb:0} Go RAM (pagination écartée)");
            string factLine = facts.Count > 0 ? " Contexte : " + string.Join(", ", facts) + "." : "";

            if (computing >= runtime.Count * 0.6)
            {
                // PREUVE forte : le jeu calculait pendant la quasi-totalité de ces frames.
                res.Add(new Recommendation
                {
                    Title = $"{below.Count} drops dus au moteur du jeu — prouvé (pire {worstFps} fps)",
                    Explanation =
                        $"Sur {computing}/{runtime.Count} de ces drops, le thread du jeu CALCULAIT pendant ~{gameCpuShare * 100:0} % de la frame (mesuré : MsCPUBusy, pas MsCPUWait). " +
                        "C'est la preuve directe que c'est le moteur du jeu qui produit ces frames lourdes — et que ce n'est PAS un programme tiers : si une autre app volait le CPU, le thread du jeu serait en ATTENTE pendant la frame, or il calculait." + factLine + "\n\n" +
                        "Leviers réels : baisser les réglages qui chargent le thread principal (effets/particules, distance d'affichage, ombres) ; cap FPS stable. Aucun process à fermer, aucun nettoyage Windows ne change ce type de drop.",
                    Severity = RecoSeverity.Info,
                });
            }
            else
            {
                // Le thread du jeu ATTENDAIT sur ces frames → cause externe plausible.
                res.Add(new Recommendation
                {
                    Title = $"{blocked} drops où le jeu ÉTAIT BLOQUÉ (pire {worstFps} fps) — cause externe possible",
                    Explanation =
                        $"Sur {blocked}/{runtime.Count} de ces drops, le thread du jeu n'a PAS calculé : il ATTENDAIT (MsCPUWait domine). C'est le seul profil où une cause extérieure au jeu est physiquement plausible — attente du GPU, d'un accès disque, d'un verrou, ou préemption par un autre thread." +
                        factLine + "\n\n" +
                        "Pour isoler sans deviner : ferme une app de fond à la fois et recapture (l'historique compare). Si un blocage disparaît, c'était lié. Tweakly ne nomme PAS de coupable au hasard sur une simple coïncidence.",
                    Severity = RecoSeverity.Warn,
                });
            }
            return res;
        }

        // ───────────────────────── verdict + score ─────────────────────────
        private static string BuildVerdict(Report r, List<Drop> runtime)
        {
            // Mène avec la RÉGULARITÉ (le ressenti dominant en haut framerate), exprimée
            // par rapport à la propre médiane de l'utilisateur, pas un chiffre absolu.
            string reg = r.FrametimeCvPct <= 8 ? "frametime très régulier"
                       : r.FrametimeCvPct <= 14 ? "frametime un peu irrégulier"
                       : r.FrametimeCvPct <= 22 ? "frametime irrégulier (jitter perceptible)"
                       : "frametime instable";
            string band = $"{r.FpsP50:0} fps médian, oscille entre {r.PerceivedFpsLow:0} et {r.PerceivedFpsHigh:0} fps (CV {r.FrametimeCvPct:0} %)";

            int deep = runtime.Count(d => d.FrameTimeMs > 0 && 1000.0 / d.FrameTimeMs < r.FpsP50 * 0.70);
            string deepTail = deep > 0 ? $" {deep} creux marqués (sous {r.FpsP50 * 0.70:0} fps)." : "";

            if (r.FrametimeCvPct <= 8 && deep == 0)
                return $"Session TOP — {reg}. {band}.{deepTail}";
            return $"{reg} — {band}.{deepTail}";
        }

        private static int ScoreFor(Report r)
        {
            // Le score juge ce que l'utilisateur RESSENT sur SON matériel, pas un chiffre
            // absolu. Deux composantes, référence = sa propre médiane + son écran :
            //  1. NIVEAU : la médiane atteint-elle le refresh du moniteur (ou le plafond
            //     compétitif si pas de moniteur connu) ? Au-delà du refresh = inutile.
            //  2. RÉGULARITÉ (dominante en haut framerate) : le frametime est-il constant ?
            //     CV ≤ 8 % = lisse (100), 8-25 % dégressif, ≥ 25 % = instable (0).
            int target = r.MonitorHz > 0 ? r.MonitorHz : Math.Max(1, r.CompetitiveFps);
            double levelScore = Math.Clamp(100.0 * r.FpsP50 / target, 0, 100);
            // Régularité : CV ≤ 6 % = parfait, −4 pts par % au-delà. CV 16 % → 60.
            double consistencyScore = Math.Clamp(100.0 - (r.FrametimeCvPct - 6.0) * 4.0, 0, 100);
            // Le CV capture déjà le jitter ; on n'ajoute qu'une PETITE pénalité pour les
            // creux VRAIMENT profonds (sous 40 % de la médiane = chute brutale), par minute.
            double deepFps = r.FpsP50 * 0.40;
            int deep = r.Drops.Count(d => d.Cause != DropCause.ShaderCompile
                                        && d.FrameTimeMs > 0 && 1000.0 / d.FrameTimeMs < deepFps);
            double minutes = Math.Max(0.5, r.DurationSec / 60.0);
            double deepPenalty = Math.Min(12, (deep / minutes) * 0.5);
            // Régularité 60 % du ressenti en haut framerate, niveau 40 %.
            return Math.Clamp((int)Math.Round(0.4 * levelScore + 0.6 * consistencyScore - deepPenalty), 0, 100);
        }

        // ───────────────────────── utilitaires ─────────────────────────
        private static List<ChartPoint> Downsample(List<FrameRecord> frames, int target)
        {
            var res = new List<ChartPoint>();
            if (frames.Count == 0) return res;
            double t0 = frames[0].TimeMs;
            if (frames.Count <= target)
            {
                foreach (var f in frames) res.Add(new ChartPoint { T = (f.TimeMs - t0) / 1000.0, Ft = f.FrameTimeMs });
                return res;
            }
            double tEnd = frames[^1].TimeMs;
            double bucket = (tEnd - t0) / target;
            int idx = 0;
            for (int b = 0; b < target; b++)
            {
                double bEnd = t0 + (b + 1) * bucket;
                double maxFt = 0, tAt = t0 + b * bucket;
                while (idx < frames.Count && frames[idx].TimeMs <= bEnd)
                {
                    if (frames[idx].FrameTimeMs > maxFt) { maxFt = frames[idx].FrameTimeMs; tAt = frames[idx].TimeMs; }
                    idx++;
                }
                if (maxFt > 0) res.Add(new ChartPoint { T = (tAt - t0) / 1000.0, Ft = maxFt });
            }
            return res;
        }

        private static double Pct(double[] sortedAsc, double q)
        {
            if (sortedAsc.Length == 0) return 0;
            return sortedAsc[Math.Clamp((int)Math.Floor((sortedAsc.Length - 1) * q), 0, sortedAsc.Length - 1)];
        }
    }
}
