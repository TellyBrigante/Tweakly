using System;
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
        private static string SetupPath => PathLayout.PawnIoSetup;

        /// <summary>Le pilote noyau PawnIO est-il enregistré sur la machine ?</summary>
        public static bool IsInstalled()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO");
                return k != null;
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("pawnio-state", "PawnIO : état du pilote indisponible", ex);
                return false;
            }
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

            ProcessCommandResult install = await Task.Run(() =>
                ProcessCommand.Run(SetupPath, "-install -silent", 120_000));

            // Le service peut apparaître avec un léger délai après la fin de l'installeur.
            for (int i = 0; i < 12 && !IsInstalled(); i++) await Task.Delay(300);

            if (IsInstalled())   return (true, "pilote PawnIO installé");
            if (!install.Success)
            {
                AppLog.WriteOnce("pawnio-install", "PawnIO : installation échouée : " + install.FailureDescription);
                return (false, "installation PawnIO impossible");
            }
            return (false, "installeur terminé mais pilote absent");
        }

        /// <summary>
        /// Désinstalle le pilote (best-effort). Un redémarrage peut être nécessaire pour purger
        /// complètement le service. Ne lève jamais.
        /// </summary>
        public static async Task<bool> UninstallAsync()
        {
            if (!IsInstalled()) return true;
            ProcessCommandResult uninstall = await Task.Run(() =>
                ProcessCommand.Run(SetupPath, "-uninstall -silent", 120_000));
            bool removed = !IsInstalled();
            if (!uninstall.Success && !removed)
                AppLog.WriteOnce("pawnio-uninstall", "PawnIO : désinstallation échouée : " + uninstall.FailureDescription);
            return removed;
        }
    }
}
