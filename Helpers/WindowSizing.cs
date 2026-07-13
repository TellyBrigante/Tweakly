using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Optimisation_Tool.Helpers;

internal static class WindowSizing
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void FitToCurrentWorkArea(
        Window window,
        double desiredWidth,
        double desiredHeight,
        double standardMinWidth,
        double standardMinHeight,
        double widthRatio,
        double heightRatio,
        double margin)
    {
        if (window.WindowState != WindowState.Normal) return;

        var work = GetWorkArea(window);
        if (work.Width <= 0 || work.Height <= 0) return;

        double availableWidth = Math.Max(1, work.Width - margin * 2);
        double availableHeight = Math.Max(1, work.Height - margin * 2);
        double minWidth = Math.Min(standardMinWidth, availableWidth);
        double minHeight = Math.Min(standardMinHeight, availableHeight);

        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.Width = Math.Min(
            desiredWidth,
            Math.Min(availableWidth, Math.Max(minWidth, work.Width * widthRatio)));
        window.Height = Math.Min(
            desiredHeight,
            Math.Min(availableHeight, Math.Max(minHeight, work.Height * heightRatio)));
        window.Left = work.Left + Math.Max(0, (work.Width - window.Width) / 2);
        window.Top = work.Top + Math.Max(0, (work.Height - window.Height) / 2);
    }

    private static Rect GetWorkArea(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    var source = PresentationSource.FromVisual(window);
                    double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                    return new Rect(
                        info.Work.Left / scaleX,
                        info.Work.Top / scaleY,
                        (info.Work.Right - info.Work.Left) / scaleX,
                        (info.Work.Bottom - info.Work.Top) / scaleY);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Fenetre : lecture de la zone de travail", ex);
        }

        return SystemParameters.WorkArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
