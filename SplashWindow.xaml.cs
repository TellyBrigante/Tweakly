using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace Optimisation_Tool
{
    /// <summary>
    /// Écran de démarrage (v1.3.5) : affiche le logo + les étapes pendant que l'essentiel
    /// se précharge (préférences déjà lues par App, préchauffage léger du monitoring :
    /// WMI CPU, LibreHardwareMonitor, NvAPI). Le préchauffage est BORNÉ à 3 s —
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

        /// <summary>
        /// Place le splash sur le moniteur où se trouve la SOURIS (celui que l'utilisateur
        /// utilise), pas le primaire. MainWindow se centrera ensuite sur le splash (cf.
        /// OpenMainAndClose) → splash + app sur le même écran, celui de la souris.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var src = System.Windows.PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(mon, ref mi))
                    {
                        double waLeft = mi.rcWork.left / sx, waTop = mi.rcWork.top / sy;
                        double waW = (mi.rcWork.right - mi.rcWork.left) / sx;
                        double waH = (mi.rcWork.bottom - mi.rcWork.top) / sy;
                        Left = waLeft + (waW - Width) / 2.0;
                        Top  = waTop + (waH - Height) / 2.0;
                        return;
                    }
                }
            }
            catch { }
            // Repli : centré sur l'écran primaire (l'ancien comportement).
            try
            {
                Left = (SystemParameters.PrimaryScreenWidth - Width) / 2.0;
                Top  = (SystemParameters.PrimaryScreenHeight - Height) / 2.0;
            }
            catch { }
        }

        /// <summary>
        /// 1re fenêtre post-UAC : Topmost ne suffit PAS à la sortir de la barre des tâches
        /// (bug vécu — le splash restait caché tant qu'on ne cliquait pas). Dès qu'il est
        /// RÉELLEMENT peint, on force le premier plan (AttachThreadInput…), avec 2 rappels
        /// dont le dernier autorise le filet minimize/restore si Windows s'entête.
        /// </summary>
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Helpers.Foreground.Force(hwnd, allowMinimizeRestore: false);
                int n = 0;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                t.Tick += (_, _) =>
                {
                    n++;
                    Helpers.Foreground.Force(hwnd, allowMinimizeRestore: n >= 2);
                    if (n >= 2) t.Stop();
                };
                t.Start();
            }
            catch { }
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
                // Paie les démarrages à froid utiles à l'accueil (WMI CPU, LHM, NvAPI) ICI plutôt
                // qu'à la première visite. Borné : au-delà de 3 s on ouvre
                // l'app, le préchauffage finit tout seul en arrière-plan.
                var warm = WarmMonitoringAsync();
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

        private static async Task WarmMonitoringAsync()
        {
            try { await Helpers.SystemMonitor.CollectAsync(Helpers.MonCollectParts.Light); }
            catch { }
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

                // MÊME ÉCRAN que le splash : MainWindow n'a pas de position définie → Windows
                // l'ouvrait à sa position par défaut, souvent sur un AUTRE moniteur que le splash
                // (centré primaire). On la centre sur le centre du splash → elle suit le splash,
                // quel que soit l'écran. (Mêmes coords DIP, même moniteur → pas de souci de DPI.)
                try
                {
                    double cx = Left + Width / 2.0, cy = Top + Height / 2.0;
                    if (!double.IsNaN(cx) && !double.IsNaN(cy) && main.Width > 0 && main.Height > 0)
                    {
                        main.WindowStartupLocation = WindowStartupLocation.Manual;
                        main.Left = cx - main.Width / 2.0;
                        main.Top  = cy - main.Height / 2.0;
                    }
                }
                catch { }

                Application.Current.MainWindow = main;   // AVANT Close() : l'app ne doit pas s'éteindre
                main.Show();

                // PASSATION DU PREMIER PLAN (régression vécue : à la fermeture du splash,
                // Windows rendait le foreground à explorer → app coincée dans la barre des
                // tâches). Deux rappels différés : juste après la fermeture du splash, puis
                // un filet à 600 ms si une autre app a repris le focus entre-temps.
                main.Dispatcher.BeginInvoke(new Action(main.EnsureForeground),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                var t = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(600) };
                t.Tick += (_, _) => { t.Stop(); main.EnsureForeground(); };
                t.Start();
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

        // ── Moniteur sous le curseur ──
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    }
}
