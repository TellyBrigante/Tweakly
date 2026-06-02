using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Lecture du log SMART/Health NVMe (page 0x02) via IOCTL_STORAGE_QUERY_PROPERTY.
    /// Méthode native Windows, pas de dépendance. Nécessite l'élévation admin.
    /// </summary>
    internal static class NvmeSmart
    {
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;
        private const uint GENERIC_READ  = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_RW = 0x00000003;
        private const uint OPEN_EXISTING = 3;

        private const int  StorageDeviceProtocolSpecificProperty = 50;
        private const int  PropertyStandardQuery = 0;
        private const int  ProtocolTypeNvme      = 3;
        private const uint NVMeDataTypeLogPage   = 2;
        private const uint NVME_LOG_HEALTH_INFO  = 0x02;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        /// <summary>Retourne les 512 octets du log SMART/Health NVMe, ou null si indisponible.</summary>
        private static byte[]? ReadHealthLog(int diskNumber)
        {
            try
            {
                using var h = CreateFileW($@"\\.\PhysicalDrive{diskNumber}",
                    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;

                const int queryHeader = 8;
                const int protoData   = 40;
                const int logSize     = 512;
                int size = queryHeader + protoData + logSize;
                var b = new byte[size];

                BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(b, 0);
                BitConverter.GetBytes(PropertyStandardQuery).CopyTo(b, 4);
                BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(b, 8);
                BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(b, 12);
                BitConverter.GetBytes(NVME_LOG_HEALTH_INFO).CopyTo(b, 16);
                BitConverter.GetBytes((uint)0).CopyTo(b, 20);
                BitConverter.GetBytes((uint)protoData).CopyTo(b, 24);
                BitConverter.GetBytes((uint)logSize).CopyTo(b, 28);

                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, b, size, b, size, out _, IntPtr.Zero))
                    return null;

                uint off = BitConverter.ToUInt32(b, 8 + 16);   // ProtocolDataOffset renvoyé
                int logStart = 8 + (int)off;
                if (logStart + logSize > b.Length) logStart = 8 + protoData;   // garde-fou → 48
                if (logStart + logSize > b.Length) return null;

                var log = new byte[logSize];
                Array.Copy(b, logStart, log, 0, logSize);
                return log;
            }
            catch { return null; }
        }

        /// <summary>Usure NVMe « Percentage Used » (octet 5), 0-255 %. Null si indisponible.</summary>
        public static int? GetPercentageUsed(int diskNumber)
        {
            var log = ReadHealthLog(diskNumber);
            if (log == null) return null;
            int pu = log[5];
            return (pu >= 0 && pu <= 255) ? pu : (int?)null;
        }

        /// <summary>« Media and Data Integrity Errors » (offset 160, 64 bits LE). Null si indisponible.</summary>
        public static ulong? GetMediaErrors(int diskNumber)
        {
            var log = ReadHealthLog(diskNumber);
            if (log == null || log.Length < 168) return null;
            return BitConverter.ToUInt64(log, 160);
        }
    }
}
