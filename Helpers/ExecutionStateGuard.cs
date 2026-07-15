using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Maintient Windows eveille pendant une operation longue. L'acquisition et la
    /// liberation sont idempotentes et chaque appel natif est verifie.
    /// </summary>
    internal sealed class ExecutionStateGuard
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;
        private const uint EsDisplayRequired = 0x00000002;

        public bool IsActive { get; private set; }

        public bool TryAcquire(bool keepDisplayAwake, out string error)
        {
            if (IsActive)
            {
                error = "";
                return true;
            }

            uint flags = EsContinuous | EsSystemRequired;
            if (keepDisplayAwake)
                flags |= EsDisplayRequired;

            if (SetThreadExecutionState(flags) == 0)
            {
                error = NativeError("Windows a refusé le blocage de la veille");
                return false;
            }

            IsActive = true;
            error = "";
            return true;
        }

        public bool TryRelease(out string error)
        {
            if (!IsActive)
            {
                error = "";
                return true;
            }

            if (SetThreadExecutionState(EsContinuous) == 0)
            {
                error = NativeError("Windows a refusé de restaurer la gestion de veille");
                return false;
            }

            IsActive = false;
            error = "";
            return true;
        }

        private static string NativeError(string context)
        {
            int code = Marshal.GetLastWin32Error();
            return code == 0
                ? context
                : $"{context} ({new Win32Exception(code).Message}, code {code})";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
