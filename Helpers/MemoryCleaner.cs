using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Libère la RAM RÉELLEMENT récupérable, à la manière du « Boost » de Microsoft PC Manager.
    /// Deux mécanismes, tous deux légitimes (PAS un placebo cosmétique) :
    ///   1. PURGE DE LA STANDBY LIST — reprend le cache de fichiers que Windows garde « au cas où »
    ///      (NtSetSystemInformation / MemoryPurgeStandbyList). C'est le vrai gain anti-saccade :
    ///      cette mémoire est déjà comptée comme disponible, mais une standby list gonflée peut
    ///      provoquer des micro-saccades en jeu. Nécessite SeProfileSingleProcessPrivilege.
    ///   2. TRIM DES WORKING SETS — réduit l'empreinte des process qui n'utilisent plus activement
    ///      leur RAM (EmptyWorkingSet). Pour une appli en arrière-plan / idle, le gain est durable ;
    ///      seul le cas d'une appli active la rechargerait aussitôt (ça, PC Manager le fait aussi).
    ///
    /// Tout est best-effort et entièrement blindé : aucune exception ne remonte (l'app tourne en
    /// admin via le manifest, mais on ne prend aucun risque de crasher le sampler du monitoring).
    /// </summary>
    public static class MemoryCleaner
    {
        // ── Trim des working sets ─────────────────────────────────────────────
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        // PROCESS_QUERY_LIMITED_INFORMATION évite l'orage d'exceptions sur les process protégés
        // (leçon §5 succession). PROCESS_SET_QUOTA est requis par EmptyWorkingSet.
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA                  = 0x0100;

        // ── Purge de la standby list (NtSetSystemInformation) ─────────────────
        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        private const int SystemMemoryListInformation = 0x50; // 80
        private const int MemoryPurgeStandbyList      = 4;

        // ── Activation du privilège SeProfileSingleProcessPrivilege ───────────
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? host, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, int len, IntPtr prev, IntPtr prevLen);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

        private const int  SE_PRIVILEGE_ENABLED    = 0x2;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const uint TOKEN_QUERY             = 0x8;

        // ── Mesure du delta réellement repris (GlobalMemoryStatusEx) ──────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX b);

        private static ulong AvailBytes()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys : 0UL;
        }

        /// <summary>
        /// Exécute le nettoyage (trim des working sets + purge de la standby list) et renvoie les
        /// Go de RAM réellement rendus disponibles (disponible après − avant, borné à 0 : jamais de
        /// chiffre gonflé ni négatif). Best-effort — ne lève jamais. À appeler hors thread UI.
        /// </summary>
        public static double FreeMemory()
        {
            ulong before = AvailBytes();
            try { TrimAllWorkingSets(); }                          catch { }
            try { EnableProfilePrivilege(); PurgeStandbyList(); }  catch { }
            ulong after = AvailBytes();

            return after > before ? (after - before) / (1024.0 * 1024.0 * 1024.0) : 0.0;
        }

        private static void TrimAllWorkingSets()
        {
            foreach (var p in Process.GetProcesses())
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                    if (h != IntPtr.Zero) EmptyWorkingSet(h);
                }
                catch { }
                finally
                {
                    if (h != IntPtr.Zero) CloseHandle(h);
                    p.Dispose();
                }
            }
        }

        private static void PurgeStandbyList()
        {
            int cmd = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
        }

        private static void EnableProfilePrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                return;
            try
            {
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid)) return;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<TOKEN_PRIVILEGES>(),
                                      IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
    }
}
