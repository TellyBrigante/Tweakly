using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    internal static class DiskActivitySampler
    {
        private const uint IoctlDiskPerformance = 0x00070020;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        private static readonly object Sync = new();
        private static readonly Dictionary<int, DiskCounters> Previous = new();

        internal static bool TrySample(int deviceNumber, out double usagePercent)
        {
            usagePercent = 0;
            if (deviceNumber < 0 || !TryReadCounters(deviceNumber, out DiskCounters current))
                return false;

            lock (Sync)
            {
                if (!Previous.TryGetValue(deviceNumber, out DiskCounters previous))
                {
                    Previous[deviceNumber] = current;
                    return false;
                }

                Previous[deviceNumber] = current;
                return TryCalculateUsage(previous, current, out usagePercent);
            }
        }

        internal static bool TryCalculateUsage(
            long previousIdleTime,
            long previousQueryTime,
            long currentIdleTime,
            long currentQueryTime,
            out double usagePercent)
            => TryCalculateUsage(
                new DiskCounters(previousIdleTime, previousQueryTime),
                new DiskCounters(currentIdleTime, currentQueryTime),
                out usagePercent);

        private static bool TryCalculateUsage(
            DiskCounters previous,
            DiskCounters current,
            out double usagePercent)
        {
            usagePercent = 0;
            long queryDelta = current.QueryTime - previous.QueryTime;
            long idleDelta = current.IdleTime - previous.IdleTime;
            if (queryDelta <= 0 || idleDelta < 0)
                return false;

            double idlePercent = idleDelta * 100.0 / queryDelta;
            usagePercent = Math.Clamp(100.0 - idlePercent, 0.0, 100.0);
            return double.IsFinite(usagePercent);
        }

        private static bool TryReadCounters(int deviceNumber, out DiskCounters counters)
        {
            counters = default;
            string path = $@"\\.\PhysicalDrive{deviceNumber}";
            using SafeFileHandle handle = CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return false;

            uint outputSize = (uint)Marshal.SizeOf<DiskPerformance>();
            if (!DeviceIoControl(
                    handle,
                    IoctlDiskPerformance,
                    IntPtr.Zero,
                    0,
                    out DiskPerformance performance,
                    outputSize,
                    out uint bytesReturned,
                    IntPtr.Zero) ||
                bytesReturned < outputSize)
            {
                return false;
            }

            counters = new DiskCounters(performance.IdleTime, performance.QueryTime);
            return counters.QueryTime > 0 && counters.IdleTime >= 0;
        }

        private readonly record struct DiskCounters(long IdleTime, long QueryTime);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DiskPerformance
        {
            public long BytesRead;
            public long BytesWritten;
            public long ReadTime;
            public long WriteTime;
            public long IdleTime;
            public uint ReadCount;
            public uint WriteCount;
            public uint QueueDepth;
            public uint SplitCount;
            public long QueryTime;
            public uint StorageDeviceNumber;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
            public string StorageManagerName;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            out DiskPerformance outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
