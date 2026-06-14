using System;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Force une fenêtre au PREMIER PLAN, bypass de la protection foreground-lock de
    /// Windows 10/11. Extrait de MainWindow.ForceForeground (technique éprouvée :
    /// AttachThreadInput + Alt fantôme + filet minimize/restore) pour être RÉUTILISABLE.
    ///
    /// ⚠️ POURQUOI (bug vécu) : la PREMIÈRE fenêtre d'un process fraîchement ÉLEVÉ (après
    /// le consentement UAC) ne reçoit PAS le foreground — elle clignote dans la barre des
    /// tâches et n'apparaît que si l'utilisateur clique dessus. Topmost seul NE suffit PAS.
    /// Le SplashWindow (1re fenêtre depuis v1.3.5) en était victime → on l'appelle ici.
    /// </summary>
    public static class Foreground
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int SW_SHOW = 5, SW_MINIMIZE = 6, SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// Amène hwnd au premier plan. Si allowMinimizeRestore et que Windows refuse
        /// encore, applique le filet minimize→restore (une fenêtre restaurée depuis l'état
        /// minimisé reçoit TOUJOURS le foreground — seule méthode 100 % fiable).
        /// </summary>
        public static void Force(IntPtr hwnd, bool allowMinimizeRestore = true)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                var fore = GetForegroundWindow();
                if (fore == hwnd) return;

                uint foreThread = GetWindowThreadProcessId(fore, out _);
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                if (foreThread != 0 && foreThread != myThread)
                    attached = AttachThreadInput(foreThread, myThread, true);
                try
                {
                    // Alt fantôme (KB Q97925) : légitime notre process pour SetForegroundWindow.
                    try { keybd_event(VK_MENU, 0, 0, UIntPtr.Zero); keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); } catch { }
                    ShowWindow(hwnd, SW_SHOW);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally { if (attached) AttachThreadInput(foreThread, myThread, false); }

                if (allowMinimizeRestore && GetForegroundWindow() != hwnd)
                {
                    ShowWindow(hwnd, SW_MINIMIZE);
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
            }
            catch { }
        }
    }
}
