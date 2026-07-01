using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Optimisation_Tool.Helpers
{
    public sealed class FirmwareSnapshot
    {
        public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class FirmwareSnapshotStore
    {
        public static string FilePath => Path.Combine(PathLayout.Config, "tweakly-firmware.json");

        private static readonly JsonSerializerOptions _opt = new()
        {
            WriteIndented = true,
        };

        public static List<FirmwareSnapshot> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<FirmwareSnapshot>>(File.ReadAllText(FilePath), _opt) ?? new();
            }
            catch { return new(); }
        }

        public static FirmwareSnapshot? Latest()
            => Load().OrderByDescending(x => x.CapturedAtUtc).FirstOrDefault();

        public static void Append(FirmwareSnapshot snapshot)
        {
            try
            {
                var list = Load();
                list.Add(snapshot);
                list = list.OrderByDescending(x => x.CapturedAtUtc).Take(20).ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? "");
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, _opt));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
