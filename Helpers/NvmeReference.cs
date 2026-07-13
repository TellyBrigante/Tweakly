using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    internal sealed class NvmeReferenceEntry
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("pcie_gen")] public int PcieGen { get; set; }
        [JsonPropertyName("lanes")] public int Lanes { get; set; }
        [JsonPropertyName("patterns")] public List<string> Patterns { get; set; } = new();
    }

    internal readonly record struct NvmeReferenceMatch(int PcieGen, int Lanes);

    internal static class NvmeReference
    {
        private static readonly object Sync = new();
        private static List<NvmeReferenceEntry>? _entries;

        public static NvmeReferenceMatch? Match(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;

            foreach (var entry in Load())
            {
                if (entry.PcieGen <= 0 || entry.Lanes <= 0) continue;
                foreach (string pattern in entry.Patterns)
                {
                    try
                    {
                        if (Regex.IsMatch(model, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                            return new NvmeReferenceMatch(entry.PcieGen, entry.Lanes);
                    }
                    catch (ArgumentException ex)
                    {
                        AppLog.Error($"Référence NVMe : motif invalide pour {entry.Model}", ex);
                    }
                }
            }

            return null;
        }

        private static IReadOnlyList<NvmeReferenceEntry> Load()
        {
            lock (Sync)
            {
                if (_entries != null) return _entries;

                try
                {
                    if (!File.Exists(PathLayout.NvmeReference))
                    {
                        AppLog.Write($"Référence NVMe absente : {PathLayout.NvmeReference}");
                        return _entries = new List<NvmeReferenceEntry>();
                    }

                    _entries = JsonSerializer.Deserialize<List<NvmeReferenceEntry>>(
                        File.ReadAllText(PathLayout.NvmeReference)) ?? new List<NvmeReferenceEntry>();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Chargement de la référence NVMe", ex);
                    _entries = new List<NvmeReferenceEntry>();
                }

                return _entries;
            }
        }
    }
}
