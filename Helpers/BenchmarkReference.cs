using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Référence CPU personnelle (auto-calibration). Le 1er bench établit le composite mono/multi
    /// comme « 100 » pour cette machine. Les bench suivants sont relatifs à cette valeur. Indexée
    /// par nom du CPU (Win32_Processor.Name) pour suivre l'utilisateur s'il change de machine ou
    /// upgrade son CPU (chaque CPU a sa propre référence).
    /// </summary>
    public static class BenchmarkReference
    {
        private sealed class Entry { public string Cpu = ""; public double Composite; public DateTime SetAt; }

        private static string FilePath => PathLayout.BenchRefFile;

        // Entry expose ses données en champs publics → IncludeFields=true obligatoire (même
        // piège que BenchmarkStore : sans ça, la référence est silencieusement re-calibrée à
        // chaque bench, ce qui rend la comparaison « toi vs toi » inutile).
        private static readonly JsonSerializerOptions _opt = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static List<Entry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), _opt) ?? new();
            }
            catch { return new(); }
        }

        private static void Save(List<Entry> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? "");
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, _opt));
            }
            catch { }
        }

        /// <summary>
        /// Renvoie la référence pour ce CPU. Si absente, l'établit avec la valeur courante
        /// (= le 1er bench devient « 100 »). Idempotent thread-safe pour notre usage (sériel).
        /// </summary>
        public static double GetCpu(string cpuName, double currentComposite)
        {
            var list = Load();
            var key  = (cpuName ?? "").Trim();
            var e    = list.Find(x => string.Equals(x.Cpu, key, StringComparison.OrdinalIgnoreCase));
            if (e != null) return e.Composite;

            list.Add(new Entry { Cpu = key, Composite = currentComposite, SetAt = DateTime.Now });
            Save(list);
            return currentComposite;
        }

        /// <summary>Permet à l'utilisateur de remettre à zéro sa référence (changement de CPU, etc.).</summary>
        public static void ResetCpu(string cpuName)
        {
            var list = Load();
            list.RemoveAll(x => string.Equals(x.Cpu, (cpuName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
    }
}
