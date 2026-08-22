using System;
using System.IO;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Source UNIQUE de tous les chemins de l'application. Aucun helper / aucune page ne doit
    /// composer un chemin de fichier de configuration ou de data « à la main » — passer par ici,
    /// ça garantit qu'un futur déplacement ne casse rien.
    ///
    /// Layout (depuis v1.2.8) :
    ///   <exe-dir>\
    ///     Tweakly.exe
    ///     config\                            ← données UTILISATEUR (jamais dans le ZIP MAJ)
    ///       tweakly-settings.json
    ///       tweakly-benchmarks.json
    ///       tweakly-bench-ref.json
    ///     data\                              ← données fournies par Tweakly (dans le ZIP MAJ)
    ///       nip\      Counter_Strike_2.nip, Rocket League.nip
    ///       drivers\  PawnIO_setup.exe, PawnIOLib.dll
    ///       tools\    nvidiaProfileInspector.exe
    ///       refs\     cpu_reference.json
    ///
    /// La migration depuis l'ancien layout (tout à la racine + data\ à plat) se fait au démarrage
    /// via <see cref="MigrateIfNeeded"/>, idempotent et entièrement blindé.
    /// </summary>
    public static class PathLayout
    {
        public static string Root      => AppDomain.CurrentDomain.BaseDirectory;
        public static string Config    => Path.Combine(Root, "config");
        public static string Data      => Path.Combine(Root, "data");
        public static string DataNip   => Path.Combine(Data, "nip");
        public static string DataDrv   => Path.Combine(Data, "drivers");
        public static string DataTools => Path.Combine(Data, "tools");
        public static string DataRefs  => Path.Combine(Data, "refs");

        // Fichiers user (config\)
        public static string SettingsFile   => Path.Combine(Config, "tweakly-settings.json");
        public static string BenchmarksFile => Path.Combine(Config, "tweakly-benchmarks.json");
        public static string BenchRefFile   => Path.Combine(Config, "tweakly-bench-ref.json");
        public static string FanProfilesFile => Path.Combine(Config, "tweakly-fans.json");
        public static string FanWatchdogLog => Path.Combine(Config, "tweakly-fan-watchdog.log");
        public static string NetworkUndoFile => Path.Combine(Config, "tweakly-network-undo.json");

        // Fichiers fournis (data\<sous-dossier>\)
        public static string PawnIoSetup     => Path.Combine(DataDrv,   "PawnIO_setup.exe");
        public static string PawnIoLib       => Path.Combine(DataDrv,   "PawnIOLib.dll");   // user-mode DLL
        public static string NvidiaInspector => Path.Combine(DataTools, "nvidiaProfileInspector.exe");
        public static string PresentMon      => Path.Combine(DataTools, "PresentMon.exe");
        public static string SessionsFile    => Path.Combine(Config,    "tweakly-sessions.json");
        public static string CpuReference    => Path.Combine(DataRefs,  "cpu_reference.json");
        public static string NvmeReference   => Path.Combine(DataRefs,  "nvme_reference.json");
        public static string FanHeaderCatalog => Path.Combine(DataRefs, "fan_header_catalog.json");
        public static string GpuTuningData   => Path.Combine(Data,      "gpu-tuning");
        public static string GpuTuningTools  => Path.Combine(DataTools, "gpu-tuning");
        public static string GpuTuningSession => Path.Combine(Config,   "tweakly-gpu-tuning.json");
        public static string GpuTuningPolicy => Path.Combine(GpuTuningData, "evaluation_policy.json");
        public static string GpuTuningEvidence => Path.Combine(GpuTuningData, "published_tuning_evidence.json");

        // Dossier où chercher les profils .nip (page Nvidia, onglet « par application »)
        public static string NipFolder       => DataNip;

        /// <summary>
        /// Migration depuis l'ancien layout (≤ v1.2.7). Idempotente, silencieuse, blindée.
        /// À appeler UNE FOIS au démarrage, avant tout helper qui touche aux fichiers.
        /// </summary>
        public static void MigrateIfNeeded()
        {
            try
            {
                Directory.CreateDirectory(Config);
                Directory.CreateDirectory(DataNip);
                Directory.CreateDirectory(DataDrv);
                Directory.CreateDirectory(DataTools);
                Directory.CreateDirectory(DataRefs);
            }
            catch { }

            // 1. JSON user : racine → config\
            SafeMove(Path.Combine(Root, "tweakly-settings.json"),   SettingsFile);
            SafeMove(Path.Combine(Root, "tweakly-benchmarks.json"), BenchmarksFile);
            SafeMove(Path.Combine(Root, "tweakly-bench-ref.json"),  BenchRefFile);

            // 2. Anciens fichiers data\ à plat (≤ v1.2.7) → on les RETIRE après que le ZIP MAJ ait
            //    posé les nouveaux dans les sous-dossiers. Sinon ils restent orphelins ad vitam.
            //    Le robocopy /E de la MAJ a déjà créé data\nip\, data\drivers\, etc. ; il n'a PAS
            //    supprimé les fichiers anciens (robocopy /E est additif, pas miroir). On nettoie.
            SafeDeleteIfDuplicate(Path.Combine(Data, "Counter_Strike_2.nip"),       Path.Combine(DataNip,   "Counter_Strike_2.nip"));
            SafeDeleteIfDuplicate(Path.Combine(Data, "Rocket League.nip"),          Path.Combine(DataNip,   "Rocket League.nip"));
            SafeDeleteIfDuplicate(Path.Combine(Data, "PawnIO_setup.exe"),           Path.Combine(DataDrv,   "PawnIO_setup.exe"));
            SafeDeleteIfDuplicate(Path.Combine(Data, "PawnIOLib.dll"),              Path.Combine(DataDrv,   "PawnIOLib.dll"));
            SafeDeleteIfDuplicate(Path.Combine(Data, "nvidiaProfileInspector.exe"), Path.Combine(DataTools, "nvidiaProfileInspector.exe"));
            SafeDeleteIfDuplicate(Path.Combine(Data, "cpu_reference.json"),         Path.Combine(DataRefs,  "cpu_reference.json"));
        }

        /// <summary>Déplace src → dst si src existe et dst est absent. Idempotent.</summary>
        private static void SafeMove(string src, string dst)
        {
            try
            {
                if (!File.Exists(src) || File.Exists(dst)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? "");
                File.Move(src, dst);
            }
            catch { /* best-effort */ }
        }

        /// <summary>Supprime <paramref name="oldPath"/> si <paramref name="newPath"/> existe (le ZIP MAJ a fait son boulot).</summary>
        private static void SafeDeleteIfDuplicate(string oldPath, string newPath)
        {
            try
            {
                if (File.Exists(oldPath) && File.Exists(newPath)) File.Delete(oldPath);
            }
            catch { }
        }
    }
}
