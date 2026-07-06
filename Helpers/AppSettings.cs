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

        [JsonPropertyName("startMinimized")]
        public bool StartMinimized { get; set; } = false;

        // Température CPU (opt-in) : nécessite l'enregistrement du pilote PawnIO. OFF par défaut.
        [JsonPropertyName("cpuTempEnabled")]
        public bool CpuTempEnabled { get; set; } = false;

        [JsonPropertyName("navigationMode")]
        public string NavigationMode { get; set; } = "Easy";

        [JsonPropertyName("cpuStabilityDefaultsRevision")]
        public int CpuStabilityDefaultsRevision { get; set; } = 0;

        // ── Chemin du fichier : config\tweakly-settings.json (depuis v1.2.8) ──
        public static string FilePath => PathLayout.SettingsFile;

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

        // ── Sauvegarde (écriture ATOMIQUE : tmp + Move) ────────────────────────
        // Un crash entre l'open et le write laissait l'ancienne version intacte avec WriteAllText
        // direct mais pouvait quand même tronquer le fichier si le process était tué à mi-flush
        // → l'app repartait sur les défauts à la prochaine session. tmp + Move règle ça (le
        // remplacement de fichier est atomique côté NTFS).
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? "");
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                var tmp  = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
