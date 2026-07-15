using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Historique LOCAL des bench (JSON à côté de tweakly-settings.json). Rien ne sort de la machine.
    /// </summary>
    public static class BenchmarkStore
    {
        public static string FilePath => PathLayout.BenchmarksFile;

        // IMPORTANT : BenchmarkResult expose ses données en CHAMPS publics. System.Text.Json
        // ignore les champs par défaut → il faut explicitement IncludeFields=true, sinon tout
        // est sérialisé/désérialisé à zéro (= « score 0, 01/01 00:00 » dans l'historique).
        private static readonly JsonSerializerOptions _opt = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            // Durcissement : si une mesure devenait NaN/Infinity (ex. division par un
            // temps quasi nul), System.Text.Json jetterait à l'écriture → perte
            // silencieuse de tout l'historique. On l'autorise (même fix que SessionStore).
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static List<BenchmarkResult> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<BenchmarkResult>>(File.ReadAllText(FilePath), _opt) ?? new();
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("benchmark-store-load", "Historique benchmark : lecture impossible", ex);
                return new();
            }
        }

        // Écriture ATOMIQUE (v1.4.3) : on écrit d'abord dans <fichier>.tmp puis on remplace
        // l'original via File.Move (atomique côté NTFS). Crash/kill pendant l'écriture =
        // l'ancien historique reste intact, jamais d'état à moitié écrit.
        public static void Save(List<BenchmarkResult> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? "");
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, _opt));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Historique benchmark : enregistrement impossible", ex);
            }
        }

        public static void Append(BenchmarkResult r)
        {
            var list = Load();
            list.Add(r);
            // Garde les 50 dernières entrées (largement suffisant, évite gonflement)
            if (list.Count > 50) list = list.OrderByDescending(x => x.Timestamp).Take(50).ToList();
            Save(list);
        }

        public static void Remove(DateTime timestamp)
        {
            var list = Load();
            list.RemoveAll(x => x.Timestamp == timestamp);
            Save(list);
        }

        /// <summary>Vide complètement l'historique (best-effort, ne lève jamais).</summary>
        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex)
            {
                AppLog.Error("Historique benchmark : suppression impossible", ex);
                Save(new());
            }
        }

        /// <summary>Comparaison « a → b » (b plus récent) — produit un delta % et un verdict.</summary>
        public sealed class Comparison
        {
            public double TotalDelta, CpuDelta, SysDelta, NetDelta;
            public string Verdict = "";
        }
        public static Comparison Compare(BenchmarkResult a, BenchmarkResult b)
        {
            double d(double x, double y) => x == 0 ? 0 : (y - x) * 100.0 / x;
            var c = new Comparison
            {
                TotalDelta = d(a.TotalScore, b.TotalScore),
                CpuDelta   = d(a.CpuScore,   b.CpuScore),
                SysDelta   = d(a.SysScore,   b.SysScore),
                NetDelta   = d(a.NetScore,   b.NetScore),
            };
            double abs = Math.Abs(c.TotalDelta);
            c.Verdict =
                abs < 2  ? "Identique (dans la marge d'erreur)" :
                abs < 5  ? (c.TotalDelta > 0 ? "Léger gain"  : "Léger recul") :
                abs < 15 ? (c.TotalDelta > 0 ? "Gain net"    : "Recul net")   :
                           (c.TotalDelta > 0 ? "Gros gain"   : "Gros recul");
            return c;
        }
    }
}
