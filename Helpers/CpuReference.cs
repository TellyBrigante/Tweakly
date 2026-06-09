using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Reference CPU EXTERNE v2 : compare la mesure brute (Mandelbrot Mpx/s) aux
    /// scores ATTENDUS pour ce modele exact, charges depuis data\cpu_reference.json.
    ///
    /// Format JSON v2 (sup. additif au v1) :
    /// {
    ///   "model": "Intel Core Ultra 7 265K",
    ///   "vendor": "Intel",
    ///   "gen": "Arrow Lake",
    ///   "pcores": 8, "ecores": 12,
    ///   "pboost": 5.5, "eboost": 4.6,
    ///   // ── Nouveau v2 (scores ancres 265K = 100) ──
    ///   "exp_single": 100,    // score attendu single-thread (Mandelbrot)
    ///   "exp_multi":  100,    // score attendu multi-thread
    ///   "exp_mem":    100,    // score attendu CPU memory access
    ///   "tier": "Enthusiast desktop 2024-2026",
    ///   "aliases": [ ... ]
    /// }
    ///
    /// LE CPU PIVOT (Core Ultra 7 265K) = ancre 100 pour les 3 sondes. Les autres
    /// CPUs ont leurs scores attendus calibres proportionnellement, croises avec
    /// Geekbench 6 / PassMark publics pour rester comparable a ces benchmarks.
    /// </summary>
    public sealed class CpuRefEntry
    {
        [JsonPropertyName("model")]      public string Model    { get; set; } = "";
        [JsonPropertyName("vendor")]     public string Vendor   { get; set; } = "";
        [JsonPropertyName("gen")]        public string Gen      { get; set; } = "";
        [JsonPropertyName("pcores")]     public int    PCores   { get; set; }
        [JsonPropertyName("ecores")]     public int    ECores   { get; set; }
        [JsonPropertyName("pboost")]     public double PBoost   { get; set; }
        [JsonPropertyName("eboost")]     public double EBoost   { get; set; }
        [JsonPropertyName("aliases")]    public List<string>? Aliases { get; set; }

        // v2 - scores attendus pour chaque sonde (ancrage 265K = 100)
        [JsonPropertyName("exp_single")] public double ExpSingle { get; set; } = 0;
        [JsonPropertyName("exp_multi")]  public double ExpMulti  { get; set; } = 0;
        [JsonPropertyName("exp_mem")]    public double ExpMem    { get; set; } = 0;
        [JsonPropertyName("tier")]       public string Tier     { get; set; } = "";
    }

    public sealed class CpuRefNominal
    {
        public bool   Found;
        public string MatchedModel = "";
        public double NominalMono;
        public double NominalMulti;
    }

    /// <summary>Retour enrichi pour Benchmark v2 : scores attendus + tier + voisins.</summary>
    public sealed class CpuRefV2
    {
        public bool   Found;
        public string MatchedModel = "";
        public string Tier         = "";
        public double ExpectedSingleMops;   // Mops/s attendus pour ce CPU sur sonde single
        public double ExpectedMultiMops;
        public double ExpectedMemMops;
        public List<(string Name, int Score)> Neighbors = new();
    }

    public static class CpuReference
    {
        // ─── FORMULE THEORIQUE par CPU (corrige le biais de calibration sur 1 user) ──
        // Pour chaque CPU dans la table, on CALCULE son score nominal a partir de ses
        // propres specs (cores, boost, generation) au lieu de tout comparer a un pivot
        // unique. Comme ca un user avec un 7800X3D obtient un score relatif au nominal
        // d'un 7800X3D, pas au nominal d'un 265K.
        //
        // Methodologie :
        //   single_nominal_mpxs = PBoost_GHz * IPC_factor_single(gen)
        //   multi_nominal_mpxs  = min(8, cores_actifs) * boost_eff * IPC_factor * efficiency
        //                         (limite a 8 threads car le bench multi est sur 8 threads max)
        //   mem_nominal_mhops   = base_mem(gen) * factor_cores_count
        //
        // IPC_factor_single : Mpx/s de Mandelbrot par GHz par P-core, calibre
        //   approximativement sur les ratios IPC publics (Geekbench 6, AnandTech).
        //   Reference Arrow Lake = 40 Mpx/s/GHz.
        private static readonly Dictionary<string, double> IpcSingle = new(StringComparer.OrdinalIgnoreCase)
        {
            // AMD
            ["Zen 1"]       = 22,
            ["Zen+"]        = 24,
            ["Zen 2"]       = 28,
            ["Zen 3"]       = 32,
            ["Zen 4"]       = 36,
            ["Zen 5"]       = 42,
            // Intel
            ["Skylake"]     = 22,
            ["Coffee Lake"] = 24,
            ["Comet Lake"]  = 25,
            ["Rocket Lake"] = 30,
            ["Alder Lake"]  = 32,
            ["Raptor Lake"] = 36,
            ["Meteor Lake"] = 37,
            ["Arrow Lake"]  = 40,    // ancre : 265K 5.5 GHz -> 220 Mpx/s = 40 Mpx/s/GHz
            ["Lunar Lake"]  = 39,
        };

        // Efficacite multi-thread sur 8 threads : sur P-cores 100%, sur E-cores ~65%.
        // Le bench multi tourne sur min(8, ProcessorCount) threads. Pour les CPU avec
        // moins de 8 cores, on a moins de threads mais ils saturent les P-cores.
        private const double MtEfficiency = 0.92;        // overhead negligeable a 8 threads
        private const double EcoreFactor  = 0.65;        // un E-core ≈ 65% d'un P-core
        // Bande passante memoire nominale (Mhops/s pointer-chase) selon archi.
        // Limite physiquement par la latence DDR (~70 ns DDR5 -> ~14 Mhops/s plafond).
        private static readonly Dictionary<string, double> MemNominal = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Arrow Lake"]  = 16, ["Lunar Lake"]  = 14,
            ["Meteor Lake"] = 14, ["Raptor Lake"] = 13, ["Alder Lake"]  = 13,
            ["Rocket Lake"] = 11, ["Comet Lake"]  = 10, ["Coffee Lake"] = 9, ["Skylake"] = 9,
            ["Zen 5"]       = 17, ["Zen 4"]       = 14, ["Zen 3"]      = 12,
            ["Zen 2"]       = 10, ["Zen+"]        = 9,  ["Zen 1"]      = 8,
        };

        // Coefficient legacy v1 (formule SHA512). Garde pour compat fallback.
        private static readonly Dictionary<string, (double pCoef, double eCoef)> GenCoef = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Zen 1"]       = (70,  0),
            ["Zen+"]        = (75,  0),
            ["Zen 2"]       = (85,  0),
            ["Zen 3"]       = (105, 0),
            ["Zen 4"]       = (125, 0),
            ["Zen 5"]       = (140, 0),
            ["Skylake"]     = (70,  0),
            ["Coffee Lake"] = (75,  0),
            ["Comet Lake"]  = (80,  0),
            ["Rocket Lake"] = (95,  0),
            ["Alder Lake"]  = (115, 75),
            ["Raptor Lake"] = (130, 85),
            ["Meteor Lake"] = (135, 95),
            ["Arrow Lake"]  = (148, 110),
            ["Lunar Lake"]  = (140, 105),
        };

        private static List<CpuRefEntry>? _cache;
        private static string DataPath => PathLayout.CpuReference;

        private static List<CpuRefEntry> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (!File.Exists(DataPath)) return _cache = new();
                _cache = JsonSerializer.Deserialize<List<CpuRefEntry>>(File.ReadAllText(DataPath)) ?? new();
            }
            catch { _cache = new(); }
            return _cache!;
        }

        /// <summary>Reset cache (utile si on edite le JSON a chaud).</summary>
        public static void Invalidate() { _cache = null; }

        // FACTEUR DE CONVERSION D'UNITES : "ratio PassMark 100" -> Mpx/s de NOTRE bench.
        // Determine UNE FOIS sur un Core Ultra 7 265K de reference (build RELEASE —
        // jamais Debug, le JIT Release est ~2,9x plus rapide sur Mandelbrot, c'etait
        // le bug de la v1.3.0 : pivots calibres en Debug -> tout le monde a ~270).
        // Ce facteur est FIGE et identique pour tous les users ; la comparaison entre
        // CPUs reste 100% basee sur les ratios PassMark publics du JSON (exp_*).
        // Harness : bench_spike\calib (660/5257/16.7 brut), ajuste ~ -7% pour le
        // contexte in-app (overlay WPF + progress reporting pendant le bench reel).
        public const double BaseSingleMpxsPublic = 620.0;
        public const double BaseMultiMpxsPublic  = 4950.0;

        /// <summary>
        /// Classement type Cinebench : CPUs calibres (exp_multi > 0) tries par
        /// exp_multi decroissant, recentre autour du CPU du user (jusqu'a
        /// 'around' CPUs au-dessus et en-dessous). Retourne (modele, ratio multi,
        /// estCpuDuUser). Les ratios sont sur l'echelle 265K = 100.
        /// </summary>
        public static List<(string Model, double Ratio, bool IsUser)> GetLadder(string matchedModel, int around = 3)
        {
            var all = Load()
                .Where(e => e.ExpMulti > 0)
                .GroupBy(e => e.Model).Select(g => g.First())   // dedoublonne
                .OrderByDescending(e => e.ExpMulti)
                .ToList();
            var result = new List<(string, double, bool)>();
            int idx = all.FindIndex(e => e.Model == matchedModel);
            if (idx < 0)
            {
                // CPU non calibre : top 'around*2' du classement, sans marqueur user
                foreach (var e in all.Take(around * 2))
                    result.Add((e.Model, e.ExpMulti, false));
                return result;
            }
            int from = Math.Max(0, idx - around);
            int to   = Math.Min(all.Count - 1, idx + around);
            for (int i = from; i <= to; i++)
                result.Add((all[i].Model, all[i].ExpMulti, i == idx));
            return result;
        }

        // ═════ API v1 (gardee pour compat) ════════════════════════════════════
        public static CpuRefNominal Lookup(string cpuName)
        {
            var v2 = LookupV2(cpuName, Environment.ProcessorCount);
            return new CpuRefNominal
            {
                Found        = v2.Found,
                MatchedModel = v2.MatchedModel,
                NominalMono  = v2.ExpectedSingleMops,
                NominalMulti = v2.ExpectedMultiMops,
            };
        }

        // ═════ API v2 : scores attendus + tier + voisins ══════════════════════
        public static CpuRefV2 LookupV2(string cpuName, int currentCpuThreads)
        {
            var result = new CpuRefV2();
            if (string.IsNullOrWhiteSpace(cpuName)) return result;

            string norm = Normalize(cpuName);
            CpuRefEntry? match = null;
            foreach (var e in Load())
            {
                if (Normalize(e.Model) == norm) { match = e; break; }
                if (e.Aliases != null)
                    foreach (var a in e.Aliases)
                        if (Normalize(a) == norm) { match = e; break; }
                if (match != null) break;
            }
            if (match == null)
            {
                foreach (var e in Load())
                {
                    string m = Normalize(e.Model);
                    if (norm.Contains(m) || m.Contains(norm)) { match = e; break; }
                }
            }
            if (match == null) return result;

            result.Found        = true;
            result.MatchedModel = match.Model;
            result.Tier         = match.Tier;

            // ─── CALIBRATION GEEKBENCH 6 (donnees publiques objectives) ──────
            // Reference : Intel Core Ultra 7 265K dans la moyenne Geekbench Browser
            //   ~3300 GB6 ST  /  ~22500 GB6 MT
            // Dans notre bench Mandelbrot, ca correspond a :
            //   Single  220 Mpx/s
            //   Multi   1800 Mpx/s
            //   Mem      17 Mhops/s   (~70 ns latence DDR5, peu de variation entre CPU)
            // Ce sont les BASES pour score=100. Chaque CPU a ses exp_single/exp_multi
            // dans cpu_reference.json, calibres = (GB6_du_CPU / GB6_du_265K) * 100.
            // Donc nominal_du_CPU = base_265K * exp_du_CPU / 100.
            const double BaseSingleMpxs = BaseSingleMpxsPublic;
            const double BaseMultiMpxs  = BaseMultiMpxsPublic;
            const double BaseMemMhops   = 16.0;   // pointer-chase memory-bound, peu sensible au JIT

            if (match.ExpSingle > 0)
            {
                // Calibration Geekbench presente : on utilise directement le ratio.
                result.ExpectedSingleMops = BaseSingleMpxs * match.ExpSingle / 100.0;
            }
            else
            {
                // Fallback formule theorique (CPU pas dans la table calibree).
                // JitScale : la table IpcSingle date de l'echelle Debug (Arrow Lake = 40
                // Mpx/s/GHz) ; en Release le JIT donne ~113 Mpx/s/GHz -> x2.82.
                const double JitScale = 2.82;
                double ipc = (IpcSingle.TryGetValue(match.Gen, out var ipcVal) ? ipcVal : 36) * JitScale;
                result.ExpectedSingleMops = match.PBoost * ipc;
            }

            if (match.ExpMulti > 0)
            {
                result.ExpectedMultiMops = BaseMultiMpxs * match.ExpMulti / 100.0;
            }
            else
            {
                const double JitScale = 2.82;
                double ipc = (IpcSingle.TryGetValue(match.Gen, out var ipcVal) ? ipcVal : 36) * JitScale;
                int pUsed = Math.Min(8, match.PCores);
                int eUsed = Math.Min(8 - pUsed, match.ECores);
                double multiOps = pUsed * match.PBoost * ipc
                                + eUsed * match.EBoost * ipc * EcoreFactor;
                result.ExpectedMultiMops = multiOps * MtEfficiency;
            }

            if (match.ExpMem > 0)
            {
                result.ExpectedMemMops = BaseMemMhops * match.ExpMem / 100.0;
            }
            else
            {
                double memNom = MemNominal.TryGetValue(match.Gen, out var mn) ? mn : 12;
                double memMultiplier = 1.0;
                if (match.Model.Contains("X3D", StringComparison.OrdinalIgnoreCase)) memMultiplier = 1.15;
                else if (match.Model.EndsWith("K") || match.Model.EndsWith("KF") || match.Model.EndsWith("KS"))
                    memMultiplier = 1.05;
                result.ExpectedMemMops = memNom * memMultiplier;
            }

            // Voisins du meme tier (jusqu'a 4) pour le comparatif
            if (!string.IsNullOrEmpty(match.Tier))
            {
                result.Neighbors = Load()
                    .Where(e => e.Tier == match.Tier && e.Model != match.Model && e.ExpMulti > 0)
                    .OrderByDescending(e => e.ExpMulti)
                    .Take(4)
                    .Select(e => (e.Model, (int)Math.Round(e.ExpMulti)))
                    .ToList();
            }

            return result;
        }

        private static string Normalize(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            s = s.Replace("(r)", "").Replace("(tm)", "").Replace("(c)", "");
            s = Regex.Replace(s, @"\s*@\s*[\d.]+\s*ghz", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcpu\b", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}
