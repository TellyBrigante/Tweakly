using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Lecture de la vie restante d'un SSD SATA via les attributs SMART ATA
    /// (IOCTL SMART_RCV_DRIVE_DATA, commande SMART READ ATTRIBUTES).
    /// On cherche, dans l'ordre, un attribut « vie restante » normalisé (0-100) :
    /// 231 (SSD Life Left), 202 (Percent Lifetime Remaining), 177 (Wear Leveling), 233.
    /// La valeur exacte dépend du fabricant → retourne null si rien d'exploitable.
    /// Nécessite l'élévation admin.
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

        // Attributs « vie restante » par ordre de préférence
        private static readonly int[] LifeAttrs = { 231, 202, 177, 233 };

        public static int? GetLifeRemaining(int diskNumber)
        {
            try
            {
                using var h = CreateFileW($@"\\.\PhysicalDrive{diskNumber}",
                    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;

                // SENDCMDINPARAMS (en-tête 32 octets, on n'a pas besoin de bBuffer en entrée)
                var inBuf = new byte[32];
                BitConverter.GetBytes((uint)512).CopyTo(inBuf, 0);   // cBufferSize
                // IDEREGS à l'offset 4
                inBuf[4]  = READ_ATTRIBUTES;   // Features = 0xD0
                inBuf[5]  = 1;                 // SectorCount
                inBuf[6]  = 1;                 // SectorNumber
                inBuf[7]  = 0x4F;              // CylLow
                inBuf[8]  = 0xC2;              // CylHigh
                inBuf[9]  = 0xA0;              // DriveHead
                inBuf[10] = SMART_CMD;         // Command = 0xB0
                inBuf[12] = (byte)diskNumber;  // bDriveNumber

                // SENDCMDOUTPARAMS : en-tête 16 octets + 512 octets de données SMART
                var outBuf = new byte[16 + 512];

                if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero))
                    return null;

                // Données SMART à partir de l'offset 16 ; attributs à partir de l'octet 2 de ces données.
                // Chaque attribut = 12 octets : [0]=ID, [3]=valeur normalisée (current), …
                const int dataStart = 16 + 2;
                for (int i = 0; i < 30; i++)
                {
                    int pos = dataStart + i * 12;
                    if (pos + 3 >= outBuf.Length) break;
                    int id = outBuf[pos];
                    if (id == 0) continue;
                    if (Array.IndexOf(LifeAttrs, id) >= 0)
                    {
                        int value = outBuf[pos + 3];        // valeur normalisée = % de vie restante
                        if (value > 0 && value <= 100) return value;
                    }
                }
                return null;
            }
            catch { return null; }
        }
    }
}
