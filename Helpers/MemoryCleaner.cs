using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    public static class MemoryCleaner
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? host, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TokenPrivileges newState, int length, IntPtr previous, IntPtr previousLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public int Count;
            public long Luid;
            public int Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessSetQuota = 0x0100;
        private const uint TokenAdjustPrivileges = 0x20;
        private const uint TokenQuery = 0x8;
        private const int PrivilegeEnabled = 0x2;
        private const int SystemMemoryListInformation = 0x50;
        private const int MemoryPurgeStandbyList = 4;

        public static double FreeMemory()
        {
            ulong before = AvailableBytes();
            try { TrimAllWorkingSets(); }
            catch (Exception ex) { AppLog.Error("Nettoyage mémoire : working sets", ex); }
            try
            {
                EnableProfilePrivilege();
                PurgeStandbyList();
            }
            catch (Exception ex) { AppLog.Error("Nettoyage mémoire : standby list", ex); }

            ulong after = AvailableBytes();
            return after > before ? (after - before) / (1024.0 * 1024.0 * 1024.0) : 0.0;
        }

        private static ulong AvailableBytes()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical : 0UL;
        }

        private static void TrimAllWorkingSets()
        {
            int currentPid = Environment.ProcessId;
            foreach (Process process in Process.GetProcesses())
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    if (process.Id == currentPid) continue;
                    handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota, false, process.Id);
                    if (handle != IntPtr.Zero) EmptyWorkingSet(handle);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Nettoyage mémoire : processus {process.Id} ignoré — {ex.Message}");
                }
                finally
                {
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                    process.Dispose();
                }
            }
        }

        private static void PurgeStandbyList()
        {
            int command = MemoryPurgeStandbyList;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            if (status < 0)
                throw new InvalidOperationException($"NtSetSystemInformation a échoué avec NTSTATUS 0x{status:X8}");
        }

        private static void EnableProfilePrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
                return;

            try
            {
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid)) return;
                var privileges = new TokenPrivileges
                {
                    Count = 1,
                    Luid = luid,
                    Attributes = PrivilegeEnabled,
                };
                AdjustTokenPrivileges(token, false, ref privileges, Marshal.SizeOf<TokenPrivileges>(),
                    IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
