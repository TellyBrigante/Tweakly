using System;
using System.Windows;

namespace Optimisation_Tool
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// True quand l'app a été relancée par le script de MAJ (update.bat passe l'argument
        /// « --after-update »). Dans ce cas on FORCE l'affichage au premier plan même si
        /// « Démarrer minimisé » est coché : l'utilisateur vient d'updater, il veut VOIR la
        /// nouvelle version revenir, pas la retrouver muette dans la barre des tâches.
        /// ⚠️ MAJ-SENSIBLE : l'argument est ajouté par le batch de la version EN COURS → ce
        /// confort ne profite qu'aux MAJ PARTANT de cette version (les installs plus
        /// anciennes relancent sans l'argument, comportement inchangé chez elles).
        /// </summary>
        public static bool LaunchedAfterUpdate { get; private set; }

        /// <summary>
        /// True quand l'app a été lancée par la tâche planifiée « démarrer avec Windows »
        /// (qui passe l'argument « --startup »). C'est le SEUL cas où « Démarrer minimisé »
        /// s'applique : un lancement MANUEL par l'utilisateur affiche toujours le splash +
        /// l'app au premier plan, quelle que soit la valeur du réglage.
        /// ⚠️ MAJ-SENSIBLE : les tâches créées par d'anciennes versions n'ont pas l'argument
        /// → StartupManager.EnsureStartupArg() les répare au démarrage (re-création silencieuse).
        /// </summary>
        public static bool LaunchedAtStartup { get; private set; }

        /// <summary>
        /// Faut-il VRAIMENT démarrer minimisé ? Uniquement si le réglage est coché ET que
        /// Windows nous a lancés au boot (--startup) ET qu'on ne revient pas d'une MAJ.
        /// Un lancement manuel ou une relance post-MAJ = splash + premier plan.
        /// </summary>
        public static bool ShouldStartMinimized(bool settingStartMinimized)
            => settingStartMinimized && LaunchedAtStartup && !LaunchedAfterUpdate;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            LaunchedAfterUpdate = Array.Exists(e.Args,
                a => string.Equals(a, "--after-update", StringComparison.OrdinalIgnoreCase));
            LaunchedAtStartup = Array.Exists(e.Args,
                a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));

            // ── Capture des exceptions NON GÉRÉES (v1.3.3) ──────────────────────
            // Avant : un crash (surtout au démarrage) ne laissait AUCUNE trace → les
            // utilisateurs disaient juste « ça ne s'ouvre plus » et on déboguait à
            // l'aveugle (cf. l'épisode Cowork des releases 1.1.x qui ne démarraient
            // pas). Maintenant : toute exception fatale est écrite dans
            // config\tweakly-log.txt AVANT que l'app ne tombe.
            //
            // ⚠️ On ne CHANGE PAS le comportement (pas de e.Handled = true global —
            // masquer les exceptions rendrait l'app zombie) : on TRACE, c'est tout.
            DispatcherUnhandledException += (_, ex) =>
            {
                Helpers.AppLog.Error("Exception non gérée (thread UI)", ex.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            {
                if (ex.ExceptionObject is Exception exc)
                    Helpers.AppLog.Error("Exception non gérée (AppDomain)", exc);
                else
                    Helpers.AppLog.Write("ERREUR · Exception non gérée (AppDomain) — objet non-Exception : " + ex.ExceptionObject);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
            {
                Helpers.AppLog.Error("Exception non observée (Task)", ex.Exception);
                ex.SetObserved();   // une task oubliée ne doit pas tuer le process
            };

            Helpers.AppLog.Write($"— Démarrage Tweakly v{Pages.PageReglages.AppVersion} —");

            // ── Écran de démarrage (v1.3.5) ─────────────────────────────────────
            // StartupUri a été retiré : on choisit ici. Splash brandé qui précharge
            // l'essentiel, SAUF si « démarrer minimisé » (pas de flash d'écran au
            // boot de Windows — l'utilisateur a demandé la discrétion).
            // ⚠️ ANTI-CASSE MAJ : si le splash échoue à se construire, on retombe
            // DIRECTEMENT sur MainWindow — le démarrage ne dépend jamais du splash.
            try
            {
                var settings = Helpers.AppSettings.Load();   // lecture best-effort (défauts si absent)
                // Appliquer le thème ENREGISTRÉ avant d'afficher quoi que ce soit : le splash
                // s'affiche AVANT MainWindow.Loaded (où le thème était appliqué jusqu'ici), donc
                // sans ça il restait coincé sur les valeurs sombres par défaut même en mode clair.
                Helpers.ThemeManager.Apply(settings.Theme == "Light"
                    ? Helpers.ThemeManager.Mode.Light
                    : Helpers.ThemeManager.Mode.Dark);
                // Splash sauf si on doit réellement démarrer minimisé (réglage coché ET
                // lancé au boot par Windows). Lancement manuel ou relance post-MAJ = splash.
                if (!ShouldStartMinimized(settings.StartMinimized))
                {
                    new SplashWindow().Show();
                    return;
                }
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Error("Démarrage : splash indisponible — ouverture directe", ex);
            }
            new MainWindow().Show();
        }
    }
}
