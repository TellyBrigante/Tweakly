using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Optimisation_Tool
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // ── INSTANCE UNIQUE ───────────────────────────────────────────────────
        // Mutex nommé global à la session : si on en crée déjà un, c'est qu'une instance
        // Tweakly tourne. On envoie alors un message Win32 ENREGISTRÉ (= identifiant unique
        // au système, partagé par toutes les apps qui ont fait le même RegisterWindowMessage)
        // en broadcast HWND_BROADCAST → seule la 1re instance Tweakly réagit (cf. MainWindow
        // WindowProc), elle se met au 1er plan, et nous on quitte sans rien afficher.
        // GUID stable dans le nom = impossible de collisionner avec une autre app.
        private const string SingleInstanceMutexName = "Tweakly_SingleInstance_F7B3A19C-2D4E-4A8B-B6F0-A2C1D9E5B742";
        public static readonly uint WM_TWEAKLY_SHOW = RegisterWindowMessage("Tweakly.Show.B742D9C1");
        private static Mutex? _singleInstanceMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

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

            // Mode interne sans interface : le processus fils surveille exclusivement
            // le controle logiciel des ventilateurs. Il doit passer avant le mutex,
            // le splash et toute initialisation de l'application principale.
            try
            {
                if (Helpers.FanSafetyWatchdogClient.IsSmokeInvocation(e.Args))
                {
                    Shutdown(Helpers.FanSafetyWatchdogClient.RunSmokeTest());
                    return;
                }
                if (Helpers.FanSafetyWatchdogClient.IsInvocation(e.Args))
                {
                    Shutdown(Helpers.FanSafetyWatchdogClient.RunWatchdog(e.Args));
                    return;
                }
            }
            catch
            {
                // Fail-closed pour ce mode interne : ne jamais ouvrir une deuxieme
                // instance Tweakly si le watchdog est mal forme.
                Shutdown(15);
                return;
            }

            // ── INSTANCE UNIQUE : la 2e instance NE FAIT RIEN d'autre que réveiller la 1re.
            // PLACÉ AVANT toute autre action (log, thème, splash…) pour ne pas écrire dans le
            // journal, allumer un splash ou voler le verrou de fichier de la 1re. Toléré
            // d'exception : si le mutex échoue (cas pathologique), on laisse l'app démarrer
            // normalement plutôt que de la bloquer (RÈGLE 3 anti-casse).
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
                if (!createdNew)
                {
                    try { PostMessage(HWND_BROADCAST, WM_TWEAKLY_SHOW, IntPtr.Zero, IntPtr.Zero); } catch { }
                    Shutdown(0);
                    return;
                }
            }
            catch { /* fail-open : si la sécurité plante, on n'empêche pas Tweakly de tourner */ }

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

        // Libère le mutex à la fermeture : sinon, sur certaines fins brutales, le système
        // attend l'expiration du handle avant de permettre à la prochaine instance de démarrer.
        protected override void OnExit(ExitEventArgs e)
        {
            try { Helpers.FanRuntimeController.StopAndRestore(); } catch { }
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }
}
