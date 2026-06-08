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
    /// Référence CPU EXTERNE : compare la mesure brute (SHA512 Mio/s) au score nominal attendu
    /// pour le CPU détecté, à partir d'une base packagée dans data\cpu_reference.json.
    ///
    /// Formule : score_mono  = boost_P × coef_génération(P)
    ///           score_multi = (cœurs_P × boost_P × coef_P) + (cœurs_E × boost_E × coef_E)
    /// avec un coefficient « Mio/s par GHz par cœur » par génération, calibré empiriquement
    /// (ancrage : Core Ultra 7 265K mesuré ~12200 Mio/s multi → définit Arrow Lake).
    ///
    /// Précision attendue : ±15-20 % par CPU. Suffisant pour « tu es dans la norme / tu sous-
    /// performes / tu surperformes », pas pour un benchmark de précision. Si CPU absent de la base
    /// → fallback sur la référence personnelle (1er bench = 100). Ne lève jamais.
    /// </summary>
    public sealed class CpuRefEntry
    {
        [JsonPropertyName("model")]    public string Model    { get; set; } = "";
        [JsonPropertyName("vendor")]   public string Vendor   { get; set; } = "";   // Intel / AMD
        [JsonPropertyName("gen")]      public string Gen      { get; set; } = "";   // Arrow Lake, Zen 4...
        [JsonPropertyName("pcores")]   public int    PCores   { get; set; }
        [JsonPropertyName("ecores")]   public int    ECores   { get; set; }
        [JsonPropertyName("pboost")]   public double PBoost   { get; set; }   // GHz
        [JsonPropertyName("eboost")]   public double EBoost   { get; set; }   // GHz (0 si pas de E-core)
        [JsonPropertyName("aliases")]  public List<string>? Aliases { get; set; }   // variantes de nom WMI
    }

    public sealed class CpuRefNominal
    {
        public bool   Found;             // true si un CPU de la base a matché
        public string MatchedModel = ""; // libellé canonique trouvé
        public double NominalMono;       // Mio/s attendus en mono-thread
        public double NominalMulti;      // Mio/s attendus en multi-thread
    }

    public static class CpuReference
    {
        // Coefficient « Mio/s SHA512 par GHz par cœur » selon la génération.
        // Calibré empiriquement, ancré sur la mesure Arrow Lake (Core Ultra 7 265K, 20 cœurs, ~12200).
        // Mono-thread P-core : on suppose ~148 Mio/s/GHz pour Arrow Lake → les autres générations
        // suivent les ratios IPC connus (ARK/AnandTech grand public, pas de données propriétaires).
        // Pas parfait, mais cohérent à ±15 % au sein d'une même génération.
        private static readonly Dictionary<string, (double pCoef, double eCoef)> GenCoef = new(StringComparer.OrdinalIgnoreCase)
        {
            // AMD
            ["Zen 1"]       = (70,  0),
            ["Zen+"]        = (75,  0),
            ["Zen 2"]       = (85,  0),
            ["Zen 3"]       = (105, 0),
            ["Zen 4"]       = (125, 0),
            ["Zen 5"]       = (140, 0),
            // Intel desktop / laptop H
            ["Skylake"]     = (70,  0),
            ["Coffee Lake"] = (75,  0),
            ["Comet Lake"]  = (80,  0),
            ["Rocket Lake"] = (95,  0),
            ["Alder Lake"]  = (115, 75),    // E-core Gracemont ≈ 65 % d'un P-core
            ["Raptor Lake"] = (130, 85),    // E-core Gracemont amélioré
            ["Meteor Lake"] = (135, 95),    // Core Ultra 100
            ["Arrow Lake"]  = (148, 110),   // Core Ultra 200, E-core Skymont (~75 % d'un P-core)
            ["Lunar Lake"]  = (140, 105),   // Core Ultra 200V (mobile)
        };

        private static List<CpuRefEntry>? _cache;

        private static string DataPath =>
            Path.Combine(AppContext.BaseDirectory, "data", "cpu_reference.json");

        private static List<CpuRefEntry> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (!File.Exists(DataPath)) return _cache = new();
                _cache = JsonSerializer.Deserialize<List<CpuRefEntry>>(File.ReadAllText(DataPath)) ?? new();
            }
            catch { _cache = new(); }
            return _cache;
        }

        /// <summary>Tente de matcher le nom WMI du CPU avec une entrée de la base.</summary>
        public static CpuRefNominal Lookup(string cpuName)
        {
            var result = new CpuRefNominal();
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
            // Fallback : matching tolérant (le nom WMI contient le modèle, ou inverse)
            if (match == null)
            {
                foreach (var e in Load())
                {
                    string m = Normalize(e.Model);
                    if (norm.Contains(m) || m.Contains(norm)) { match = e; break; }
                }
            }
            if (match == null) return result;

            if (!GenCoef.TryGetValue(match.Gen, out var coef))
                return result;   // génération non calibrée → on s'abstient (silencieux)

            result.Found        = true;
            result.MatchedModel = match.Model;
            result.NominalMono  = match.PBoost * coef.pCoef;
            result.NominalMulti = match.PCores * match.PBoost * coef.pCoef
                                + match.ECores * match.EBoost * coef.eCoef;
            return result;
        }

        // Normalisation : minuscules, supprime (R)/(TM), espaces multiples, "CPU @ x.xGHz"
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
