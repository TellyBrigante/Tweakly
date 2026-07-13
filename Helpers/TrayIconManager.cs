using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Icone de Tweakly dans la zone de notification Windows (system tray).
    ///
    /// IMPLEMENTATION : Win32 Shell_NotifyIcon en P/Invoke direct, ATTACHEE au HWND de
    /// MainWindow elle-meme (pas de fenetre supplementaire). Le hook des messages tray
    /// se greffe sur le WindowProc deja existant de MainWindow.
    ///
    /// HISTORIQUE DU DEBUG (3 tentatives ratees) :
    ///   v1 : WinForms NotifyIcon -> ecran noir au demarrage (cohabitation WPF+WinForms).
    ///   v2 : P/Invoke + HwndSource separee message-only -> ecran noir aussi (creer une
    ///        2e fenetre native avant le 1er rendu de MainWindow embrouille le DWM WPF).
    ///   v3 : P/Invoke + reutilisation du HWND de MainWindow (cette implementation) -> OK.
    ///        Zero fenetre supplementaire, zero cohabitation, init dans OnSourceInitialized
    ///        APRES la creation du HWND de MainWindow.
    ///
    /// COMPORTEMENT FIGE :
    ///   - X (fermer) : ferme l'app pour de vrai (inchange, gere par MainWindow).
    ///   - _ (reduire) : reduit dans la barre des taches ; l'icone tray reste presente
    ///                   en parallele (la fenetre n'est PLUS cachee de la barre des taches).
    ///   - Double-clic gauche tray : Ouvrir Tweakly (restore + focus).
    ///   - Clic droit tray : menu { Ouvrir Tweakly, Ouvrir mode mini, Fermer Tweakly }.
    ///   - "Demarrer minimise" (Reglages) : demarre reduit dans la barre des taches (+ tray).
    /// </summary>
    internal sealed class TrayIconManager : IDisposable
    {
        // -- Constantes Win32 -------------------------------------------------

        private const int WM_USER             = 0x0400;
        public  const int WM_TRAYCALLBACK     = WM_USER + 1;
        private const int WM_LBUTTONDBLCLK    = 0x0203;
        private const int WM_RBUTTONUP        = 0x0205;

        private const uint NIF_MESSAGE        = 0x0001;
        private const uint NIF_ICON           = 0x0002;
        private const uint NIF_TIP            = 0x0004;

        private const uint NIM_ADD            = 0x0000;
        private const uint NIM_DELETE         = 0x0002;

        private const uint MF_STRING          = 0x0000;
        private const uint MF_SEPARATOR       = 0x0800;

        private const uint TPM_RIGHTBUTTON    = 0x0002;
        private const uint TPM_RETURNCMD      = 0x0100;

        private const int IDM_OPEN            = 1;
        private const int IDM_MINI            = 2;
        private const int IDM_QUIT            = 3;

        // -- Structures Win32 -------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int    cbSize;
            public IntPtr hWnd;
            public uint   uID;
            public uint   uFlags;
            public uint   uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        // -- P/Invoke ---------------------------------------------------------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA pnid);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, uint nIconIndex);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // -- Etat interne -----------------------------------------------------

        private readonly MainWindow _main;
        private readonly IntPtr _hostHwnd;
        private IntPtr _hIcon = IntPtr.Zero;
        private bool _iconAdded;
        private bool _disposed;

        /// <summary>True si l'icone est enregistree dans le tray.</summary>
        public bool IsAvailable => !_disposed && _iconAdded;

        public TrayIconManager(MainWindow main, IntPtr hostHwnd)
        {
            _main = main;
            _hostHwnd = hostHwnd;

            if (hostHwnd == IntPtr.Zero) { _disposed = true; return; }

            try
            {
                // 1) Charger l'icone de l'exe (ApplicationIcon embarquee dans le PE).
                try
                {
                    var exe = Process.GetCurrentProcess().MainModule!.FileName;
                    _hIcon = ExtractIcon(IntPtr.Zero, exe, 0);
                }
                catch { _hIcon = IntPtr.Zero; }

                // 2) Enregistrer l'icone tray attachee au HWND de MainWindow.
                //    Les messages WM_TRAYCALLBACK seront recus par le WindowProc de
                //    MainWindow, qui delegue a TryHandleMessage() ci-dessous.
                var nid = new NOTIFYICONDATA
                {
                    cbSize           = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd             = hostHwnd,
                    uID              = 1,
                    uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = (uint)WM_TRAYCALLBACK,
                    hIcon            = _hIcon,
                    szTip            = "Tweakly",
                };
                _iconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
                if (!_iconAdded) Dispose();
            }
            catch
            {
                Dispose();
            }
        }

        /// <summary>
        /// Appele depuis le WindowProc de MainWindow. Retourne true si le message a ete
        /// consomme par le tray (clic gauche double, clic droit menu).
        /// </summary>
        public bool TryHandleMessage(int msg, IntPtr lParam)
        {
            if (_disposed || msg != WM_TRAYCALLBACK) return false;
            int evt = lParam.ToInt32() & 0xFFFF;
            switch (evt)
            {
                case WM_LBUTTONDBLCLK:
                    ShowMain();
                    return true;

                case WM_RBUTTONUP:
                    ShowContextMenu();
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reduit MainWindow dans la barre des taches. Avant : _main.Hide() retirait la
        /// fenetre de la barre des taches (seule la tray restait) -> l'utilisateur veut la
        /// voir AUSSI dans la barre des taches. Desormais : reduction classique, l'icone
        /// tray (enregistree au ctor) reste presente en parallele.
        /// </summary>
        public void HideToTray()
        {
            try
            {
                if (!_main.IsVisible) _main.Show();
                _main.WindowState = WindowState.Minimized;
            }
            catch (Exception ex)
            {
                AppLog.Error("Tray : réduction de la fenêtre", ex);
            }
        }

        /// <summary>Restaure MainWindow au premier plan (depuis le tray ou le mode mini).</summary>
        public void ShowMain()
        {
            _main.Dispatcher.Invoke(() =>
            {
                try { _main.RestoreFromUserRequest(); }
                catch (Exception ex) { AppLog.Error("Tray : restauration de la fenêtre", ex); }
            });
        }

        /// <summary>Passe en mode mini depuis le tray.</summary>
        public void ShowMini()
        {
            _main.Dispatcher.Invoke(() =>
            {
                try
                {
                    if (!_main.IsVisible) _main.Show();
                    _main.EnterMiniMode();
                }
                catch (Exception ex) { AppLog.Error("Tray : ouverture du mode mini", ex); }
            });
        }

        /// <summary>Quitter Tweakly depuis le menu tray.</summary>
        public void QuitApp()
        {
            _main.Dispatcher.Invoke(() =>
            {
                try { _main.RequestShutdown(); } catch { }
            });
        }

        // -- Menu contextuel (TrackPopupMenu Win32 natif) --------------------

        private void ShowContextMenu()
        {
            IntPtr hMenu = IntPtr.Zero;
            try
            {
                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return;

                AppendMenu(hMenu, MF_STRING,    (IntPtr)IDM_OPEN, "Ouvrir Tweakly");
                AppendMenu(hMenu, MF_STRING,    (IntPtr)IDM_MINI, "Ouvrir mode mini");
                AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero,     null);
                AppendMenu(hMenu, MF_STRING,    (IntPtr)IDM_QUIT, "Fermer Tweakly");

                if (!GetCursorPos(out POINT pt)) return;

                // IMPORTANT : SetForegroundWindow avant TrackPopupMenu, sinon le menu
                // ne se ferme pas quand on clique ailleurs (bug Win32 connu).
                SetForegroundWindow(_hostHwnd);

                int cmd = TrackPopupMenu(hMenu,
                    TPM_RIGHTBUTTON | TPM_RETURNCMD,
                    pt.x, pt.y, 0, _hostHwnd, IntPtr.Zero);

                switch (cmd)
                {
                    case IDM_OPEN: ShowMain(); break;
                    case IDM_MINI: ShowMini(); break;
                    case IDM_QUIT: QuitApp();  break;
                }
            }
            catch { }
            finally
            {
                if (hMenu != IntPtr.Zero) try { DestroyMenu(hMenu); } catch { }
            }
        }

        // -- Dispose ----------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 1) Retirer l'icone du tray (sinon fantome jusqu'au survol souris).
            if (_iconAdded && _hostHwnd != IntPtr.Zero)
            {
                try
                {
                    var nid = new NOTIFYICONDATA
                    {
                        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                        hWnd   = _hostHwnd,
                        uID    = 1,
                    };
                    Shell_NotifyIcon(NIM_DELETE, ref nid);
                }
                catch { }
                _iconAdded = false;
            }

            // 2) Liberer le HICON.
            if (_hIcon != IntPtr.Zero)
            {
                try { DestroyIcon(_hIcon); } catch { }
                _hIcon = IntPtr.Zero;
            }
        }
    }
}
