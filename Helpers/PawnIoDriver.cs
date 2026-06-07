using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Installe / désinstalle le pilote PawnIO (requis pour lire la température CPU) via l'installeur
    /// OFFICIEL signé Microsoft, bundlé dans data\PawnIO_setup.exe. L'application tournant déjà en
    /// administrateur (manifest requireAdministrator), l'installation se fait EN SILENCE et SANS prompt
    /// UAC supplémentaire. Tout est best-effort : aucune exception ne remonte.
    /// </summary>
    public static class PawnIoDriver
    {
        private static string SetupPath =>
            Path.Combine(AppContext.BaseDirectory, "data", "PawnIO_setup.exe");

        /// <summary>Le pilote noyau PawnIO est-il enregistré sur la machine ?</summary>
        public static bool IsInstalled()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO");
                return k != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// S'assure que le pilote est présent. S'il manque, lance l'installeur officiel en silence
        /// (« -install -silent »). Revérifie ensuite l'état RÉEL (l'exit code seul ne suffit pas).
        /// Renvoie (succès, message court). Ne lève jamais. À appeler hors thread UI.
        /// </summary>
        public static async Task<(bool ok, string msg)> EnsureInstalledAsync()
        {
            if (IsInstalled()) return (true, "pilote déjà présent");
            if (!File.Exists(SetupPath)) return (false, "installeur PawnIO introuvable dans data\\");

            bool launched = await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(SetupPath, "-install -silent")
                    { UseShellExecute = false, CreateNoWindow = true });
                    if (p == null) return false;
                    p.WaitForExit(120_000);
                    return true;
                }
                catch { return false; }
            });

            // Le service peut apparaître avec un léger délai après la fin de l'installeur.
            for (int i = 0; i < 12 && !IsInstalled(); i++) await Task.Delay(300);

            if (IsInstalled())   return (true, "pilote PawnIO installé");
            return launched ? (false, "installeur exécuté mais pilote absent")
                            : (false, "impossible de lancer l'installeur");
        }

        /// <summary>
        /// Désinstalle le pilote (best-effort). Un redémarrage peut être nécessaire pour purger
        /// complètement le service. Ne lève jamais.
        /// </summary>
        public static async Task<bool> UninstallAsync()
        {
            if (!IsInstalled()) return true;
            return await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(SetupPath, "-uninstall -silent")
                    { UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(120_000);
                }
                catch { }
                return !IsInstalled();
            });
        }
    }
}
