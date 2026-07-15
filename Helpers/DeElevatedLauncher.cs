using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Lance un processus dans le contexte de l'utilisateur interactif (DÉ-ÉLEVÉ),
    /// même si WMT-GUI tourne en administrateur.
    ///
    /// Indispensable pour les installeurs « user-scope » (Discord, Spotify, Slack…)
    /// qui refusent l'exécution élevée ou s'installeraient dans le mauvais profil.
    ///
    /// Technique : duplication du token de explorer.exe + CreateProcessWithTokenW,
    /// avec le bloc d'environnement de l'utilisateur (%LOCALAPPDATA% correct).
    /// </summary>
    internal static class DeElevatedLauncher
    {
        // ── Lancement + attente ───────────────────────────────────────────────
        /// <summary>
        /// Lance <paramref name="fileName"/> dé-élevé et attend la fin.
        /// Retourne le code de sortie, ou -1 en cas de timeout, ou jette une exception.
        /// </summary>
        public static int StartAndWait(string fileName, string arguments,
                                       int timeoutMs, string? workingDir = null)
        {
            using var explorer = Process.GetProcessesByName("explorer").FirstOrDefault()
                ?? throw new InvalidOperationException("explorer.exe introuvable (session utilisateur absente).");

            IntPtr hExplorer = OpenProcess(PROCESS_QUERY_INFORMATION, false, explorer.Id);
            if (hExplorer == IntPtr.Zero)
                hExplorer = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, explorer.Id);
            if (hExplorer == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(explorer) a échoué.");

            IntPtr hToken = IntPtr.Zero, hDup = IntPtr.Zero, hEnv = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(hExplorer, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out hToken))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken a échoué.");

                if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                        SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                        TOKEN_TYPE.TokenPrimary, out hDup))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx a échoué.");

                // Bloc d'environnement de l'utilisateur (pour %LOCALAPPDATA%, %APPDATA%, etc.)
                uint creationFlags = CREATE_NO_WINDOW;
                if (CreateEnvironmentBlock(out hEnv, hDup, false))
                    creationFlags |= CREATE_UNICODE_ENVIRONMENT;
                else
                    hEnv = IntPtr.Zero;

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = @"winsta0\default";

                // lpCommandLine doit être modifiable → StringBuilder
                var cmd = new StringBuilder($"\"{fileName}\" {arguments}");

                bool ok = CreateProcessWithTokenW(
                    hDup,
                    LOGON_WITH_PROFILE,
                    null,
                    cmd,
                    creationFlags,
                    hEnv,
                    workingDir,
                    ref si,
                    out PROCESS_INFORMATION pi);

                if (!ok)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessWithTokenW a échoué.");

                try
                {
                    uint wait = WaitForSingleObject(pi.hProcess, (uint)timeoutMs);
                    if (wait == WAIT_TIMEOUT)
                    {
                        if (!TerminateProcess(pi.hProcess, 1))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Arrêt du processus expiré impossible.");
                        uint terminatedWait = WaitForSingleObject(pi.hProcess, 2000);
                        if (terminatedWait == WAIT_FAILED)
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Confirmation de l'arrêt impossible.");
                        if (terminatedWait == WAIT_TIMEOUT)
                            throw new TimeoutException("Le processus expiré ne s'est pas arrêté sous 2 000 ms.");
                        return -1;
                    }
                    if (wait == WAIT_FAILED)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Attente du processus impossible.");
                    if (!GetExitCodeProcess(pi.hProcess, out uint exitCode))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Lecture du code de sortie impossible.");
                    return (int)exitCode;
                }
                finally
                {
                    if (pi.hThread  != IntPtr.Zero) CloseHandle(pi.hThread);
                    if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                }
            }
            finally
            {
                if (hEnv      != IntPtr.Zero) DestroyEnvironmentBlock(hEnv);
                if (hDup      != IntPtr.Zero) CloseHandle(hDup);
                if (hToken    != IntPtr.Zero) CloseHandle(hToken);
                if (hExplorer != IntPtr.Zero) CloseHandle(hExplorer);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // P/Invoke
        // ═══════════════════════════════════════════════════════════════════════

        private const uint PROCESS_QUERY_INFORMATION         = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_DUPLICATE                   = 0x0002;
        private const uint TOKEN_QUERY                       = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY              = 0x0001;
        private const uint MAXIMUM_ALLOWED                   = 0x02000000;
        private const uint CREATE_UNICODE_ENVIRONMENT        = 0x00000400;
        private const uint CREATE_NO_WINDOW                  = 0x08000000;
        private const uint LOGON_WITH_PROFILE                = 0x00000001;
        private const uint WAIT_TIMEOUT                      = 0x00000102;
        private const uint WAIT_FAILED                       = 0xFFFFFFFF;

        private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
        private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX; public int dwY; public int dwXSize; public int dwYSize;
            public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute;
            public int dwFlags; public short wShowWindow; public short cbReserved2;
            public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr hProcess, uint access, out IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint access, IntPtr attrs,
            SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr hNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr hToken, uint logonFlags,
            string? appName, StringBuilder cmdLine, uint creationFlags, IntPtr env,
            string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
    }
}
