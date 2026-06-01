using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Lecture de l'usure réelle d'un SSD NVMe via IOCTL_STORAGE_QUERY_PROPERTY
    /// (log SMART/Health NVMe, page 0x02, octet 5 = « Percentage Used », 0-255 %).
    /// Méthode native Windows — pas de dépendance externe. Nécessite l'élévation admin.
    /// Retourne null si indisponible (disque non NVMe, accès refusé, etc.).
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

        public static int? GetPercentageUsed(int diskNumber)
        {
            try
            {
                using var h = CreateFileW($@"\\.\PhysicalDrive{diskNumber}",
                    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;

                const int queryHeader = 8;    // STORAGE_PROPERTY_QUERY : PropertyId + QueryType
                const int protoData   = 40;   // STORAGE_PROTOCOL_SPECIFIC_DATA
                const int logSize     = 512;  // NVMe SMART/Health log
                int size = queryHeader + protoData + logSize;
                var b = new byte[size];

                // STORAGE_PROPERTY_QUERY
                BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(b, 0);
                BitConverter.GetBytes(PropertyStandardQuery).CopyTo(b, 4);
                // STORAGE_PROTOCOL_SPECIFIC_DATA (offset 8)
                BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(b, 8);          // ProtocolType
                BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(b, 12);      // DataType
                BitConverter.GetBytes(NVME_LOG_HEALTH_INFO).CopyTo(b, 16);     // ProtocolDataRequestValue (log 0x02)
                BitConverter.GetBytes((uint)0).CopyTo(b, 20);                  // ProtocolDataRequestSubValue
                BitConverter.GetBytes((uint)protoData).CopyTo(b, 24);         // ProtocolDataOffset
                BitConverter.GetBytes((uint)logSize).CopyTo(b, 28);           // ProtocolDataLength

                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, b, size, b, size, out _, IntPtr.Zero))
                    return null;

                // Sortie : STORAGE_PROTOCOL_DATA_DESCRIPTOR (Version+Size = 8) + ProtocolSpecificData (40) + log
                // ProtocolDataOffset (renvoyé) est relatif au début de ProtocolSpecificData (offset 8).
                uint off = BitConverter.ToUInt32(b, 8 + 16);   // champ ProtocolDataOffset dans la sortie
                int logStart = 8 + (int)off;
                if (logStart + 5 >= b.Length) logStart = 8 + protoData;   // garde-fou → 48
                if (logStart + 5 >= b.Length) return null;

                int pu = b[logStart + 5];        // octet 5 du log = Percentage Used
                return (pu >= 0 && pu <= 255) ? pu : (int?)null;
            }
            catch { return null; }
        }
    }
}
