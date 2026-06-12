using System;
using System.Threading.Tasks;
using System.Windows;

namespace Optimisation_Tool
{
    /// <summary>
    /// Écran de démarrage (v1.3.5) : affiche le logo + les étapes pendant que l'essentiel
    /// se précharge (préférences déjà lues par App, préchauffage du monitoring : WMI,
    /// LibreHardwareMonitor, nvidia-smi, stockage). Le préchauffage est BORNÉ à 3 s —
    /// s'il n'a pas fini, il continue en arrière-plan et on ouvre l'app quand même.
    ///
    /// ⚠️ ANTI-CASSE MAJ (règle 3 : rien ne doit empêcher le démarrage) : tout est en
    /// try/catch — quel que soit l'échec ici, MainWindow s'ouvre dans le finally.
    /// </summary>
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            try { TxtSplashVersion.Text = "v" + Pages.PageReglages.AppVersion; } catch { }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStep(12, "Initialisation…");
                await Task.Delay(80);   // laisse le splash se peindre

                SetStep(35, "Chargement des préférences…");
                await Task.Delay(120);  // (déjà lues par App — étape visuelle)

                SetStep(60, "Préchauffage du monitoring…");
                // Paie les démarrages à froid (WMI, LHM, nvidia-smi, stockage) ICI plutôt
                // qu'à la première visite du Monitoring. Borné : au-delà de 3 s on ouvre
                // l'app, le préchauffage finit tout seul en arrière-plan.
                var warm = Task.Run(() => { try { Helpers.SystemMonitor.Collect(); } catch { } });
                await Task.WhenAny(warm, Task.Delay(3000));

                SetStep(95, "Lancement de l'interface…");
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Helpers.AppLog.Error("Splash : étape de chargement", ex);
            }
            finally
            {
                OpenMainAndClose();
            }
        }

        private void SetStep(double pct, string label)
        {
            try
            {
                TxtSplashStep.Text = label;
                pct = Math.Max(0, Math.Min(100, pct));
                SplashBar.ColumnDefinitions[0].Width = new GridLength(pct,       GridUnitType.Star);
                SplashBar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            }
            catch { }
        }

        private void OpenMainAndClose()
        {
            try
            {
                var main = new MainWindow();
                Application.Current.MainWindow = main;   // AVANT Close() : l'app ne doit pas s'éteindre
                main.Show();
            }
            catch (Exception ex)
            {
                // MainWindow n'a pas pu se construire : on TRACE (diagnostic) puis on relance
                // l'exception — masquer un crash de la fenêtre principale donnerait une app
                // zombie invisible, pire qu'un crash franc.
                Helpers.AppLog.Error("Splash : ouverture de MainWindow", ex);
                throw;
            }
            finally
            {
                try { Close(); } catch { }
            }
        }
    }
}
