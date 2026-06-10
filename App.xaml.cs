using System;
using System.Windows;

namespace Optimisation_Tool
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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
        }
    }
}
