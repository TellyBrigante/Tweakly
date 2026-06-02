using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisation_Tool.Helpers
{
    public class AppSettings
    {
        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "Dark";

        [JsonPropertyName("startWithWindows")]
        public bool StartWithWindows { get; set; } = false;

        [JsonPropertyName("autoUpdate")]
        public bool AutoUpdate { get; set; } = true;   // activé par défaut

        [JsonPropertyName("soundsEnabled")]
        public bool SoundsEnabled { get; set; } = true;   // sons d'interface (survol/clic/notifs)

        // ── Chemin du fichier (à côté de l'exe) ───────────────────────────────
        public static string FilePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "tweakly-settings.json");

        // ── Chargement ─────────────────────────────────────────────────────────
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }

        // ── Sauvegarde ─────────────────────────────────────────────────────────
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
