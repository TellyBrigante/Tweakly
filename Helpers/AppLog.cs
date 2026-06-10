using System;
using System.IO;
using System.Text;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Journal technique LOCAL de Tweakly (v1.3.3).
    ///
    /// POURQUOI : avant, les erreurs internes etaient avalees en silence (catch vides)
    /// → impossible de diagnostiquer un probleme chez un utilisateur autrement que par
    /// captures d'ecran. Ce logger ecrit les messages du journal d'activite ET les
    /// erreurs techniques dans un fichier que l'utilisateur peut consulter/partager
    /// S'IL le decide.
    ///
    /// VIE PRIVEE (exigence du proprietaire du projet) : AUCUNE donnee personnelle,
    /// AUCUN envoi reseau, jamais. Uniquement des messages techniques de l'app
    /// (« echec lecture WMI : acces refuse »), stockes en LOCAL dans config\
    /// (dossier exclu du ZIP de mise a jour, donc jamais ecrase ni redistribue).
    ///
    /// Rotation : au-dela de 1 Mo, le fichier devient .old (1 seul backup) → l'espace
    /// disque est borne a ~2 Mo quoi qu'il arrive.
    /// </summary>
    internal static class AppLog
    {
        private static readonly object _lock = new();
        private const long MaxBytes = 1_000_000;   // ~1 Mo avant rotation

        public static string LogFile => Path.Combine(PathLayout.Config, "tweakly-log.txt");

        /// <summary>Écrit une ligne horodatée. Ne lève JAMAIS (le log ne doit pas casser l'app).</summary>
        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(PathLayout.Config);
                    RotateIfNeeded();
                    File.AppendAllText(LogFile,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch { /* jamais bloquant */ }
        }

        /// <summary>Écrit une erreur avec contexte + exception complète (type, message, stack).</summary>
        public static void Error(string context, Exception ex)
            => Write($"ERREUR · {context} — {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(LogFile);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                var old = LogFile + ".old";
                File.Delete(old);
                File.Move(LogFile, old);
            }
            catch { }
        }
    }
}
