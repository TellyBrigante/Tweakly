using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Lecture des attributs SMART d'un disque SATA via IOCTL SMART_RCV_DRIVE_DATA.
    /// - Vie restante : attribut normalisé 231 / 202 / 177 / 233 (dépend du fabricant).
    /// - Secteurs réalloués : attribut 5 (raw value) — indicateur de dégradation.
    /// Nécessite l'élévation admin. Retourne null si indisponible.
    /// </summary>
    internal static class AtaSmart
    {
        private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
        private const uint GENERIC_READ  = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_RW = 0x00000003;
        private const uint OPEN_EXISTING = 3;

        private const byte SMART_CMD       = 0xB0;
        private const byte READ_ATTRIBUTES = 0xD0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        private static readonly int[] LifeAttrs = { 231, 202, 177, 233 };

        /// <summary>Renvoie le buffer de sortie SMART (en-tête 16 o + 512 o de données), ou null.</summary>
        private static byte[]? ReadSmartData(int diskNumber)
        {
            try
            {
                using var h = CreateFileW($@"\\.\PhysicalDrive{diskNumber}",
                    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;

                var inBuf = new byte[32];
                BitConverter.GetBytes((uint)512).CopyTo(inBuf, 0);   // cBufferSize
                inBuf[4]  = READ_ATTRIBUTES;   // Features = 0xD0
                inBuf[5]  = 1;                 // SectorCount
                inBuf[6]  = 1;                 // SectorNumber
                inBuf[7]  = 0x4F;              // CylLow
                inBuf[8]  = 0xC2;              // CylHigh
                inBuf[9]  = 0xA0;              // DriveHead
                inBuf[10] = SMART_CMD;         // Command = 0xB0
                inBuf[12] = (byte)diskNumber;  // bDriveNumber

                var outBuf = new byte[16 + 512];
                if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
                    return null;
                return outBuf;
            }
            catch { return null; }
        }

        // Itère les 30 attributs SMART. Données à partir de l'offset 16 ; attributs à l'octet 2 de ces données.
        // Chaque attribut = 12 octets : [0]=ID, [3]=valeur normalisée, [5..10]=raw (6 o LE).
        private const int AttrBase = 16 + 2;

        public static int? GetLifeRemaining(int diskNumber)
        {
            var d = ReadSmartData(diskNumber);
            if (d == null) return null;
            for (int i = 0; i < 30; i++)
            {
                int pos = AttrBase + i * 12;
                if (pos + 3 >= d.Length) break;
                int id = d[pos];
                if (id == 0) continue;
                if (Array.IndexOf(LifeAttrs, id) >= 0)
                {
                    int value = d[pos + 3];                 // valeur normalisée = % de vie restante
                    if (value > 0 && value <= 100) return value;
                }
            }
            return null;
        }

        public static long? GetReallocatedSectors(int diskNumber)
        {
            var d = ReadSmartData(diskNumber);
            if (d == null) return null;
            for (int i = 0; i < 30; i++)
            {
                int pos = AttrBase + i * 12;
                if (pos + 10 >= d.Length) break;
                int id = d[pos];
                if (id == 5)   // Reallocated Sectors Count
                {
                    // raw value sur 4 octets LE (suffisant pour un compteur de secteurs)
                    long raw = d[pos + 5] | ((long)d[pos + 6] << 8) | ((long)d[pos + 7] << 16) | ((long)d[pos + 8] << 24);
                    return raw;
                }
            }
            return null;
        }
    }
}
