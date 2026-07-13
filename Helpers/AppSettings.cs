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

        // ── Chemin du fichier : config\tweakly-settings.json (depuis v1.2.8) ──
        public static string FilePath => PathLayout.SettingsFile;
        private static string BackupFilePath => FilePath + ".bak";
        private static string TempFilePath => FilePath + ".tmp";

        // ── Chargement ─────────────────────────────────────────────────────────
        public static AppSettings Load()
        {
            var candidates = new[] { FilePath, TempFilePath, BackupFilePath };
            bool foundCandidate = false;

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                foundCandidate = true;
                try
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json)
                        ?? throw new InvalidDataException("Le JSON ne contient aucun réglage.");

                    if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                        AppLog.Write($"Réglages : récupération depuis {Path.GetFileName(path)}.");
                    return settings;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Réglages : lecture impossible ({Path.GetFileName(path)})", ex);
                }
            }

            if (foundCandidate)
                AppLog.Write("ERREUR · Réglages : aucun fichier valide, valeurs par défaut utilisées.");
            return new AppSettings();
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
                File.WriteAllText(TempFilePath, json);

                if (File.Exists(FilePath))
                {
                    try
                    {
                        File.Replace(TempFilePath, FilePath, BackupFilePath, ignoreMetadataErrors: true);
                    }
                    catch (IOException)
                    {
                        // Fallback pour les supports portables qui ne gèrent pas File.Replace.
                        File.Copy(FilePath, BackupFilePath, overwrite: true);
                        File.Move(TempFilePath, FilePath, overwrite: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(FilePath, BackupFilePath, overwrite: true);
                        File.Move(TempFilePath, FilePath, overwrite: true);
                    }
                }
                else
                    File.Move(TempFilePath, FilePath);
            }
            catch (Exception ex)
            {
                AppLog.Error("Réglages : sauvegarde impossible", ex);
            }
        }
    }
}
