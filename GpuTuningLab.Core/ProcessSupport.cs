using System.ComponentModel;
using System.Diagnostics;

namespace GpuTuningLab.Core;

internal static class ProcessSupport
{
    public static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    public static async Task WaitForExitAfterStopAsync(
        Process process,
        TimeSpan? timeout = null)
    {
        TryKillTree(process);
        using var stopTimeout = new CancellationTokenSource(
            timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(stopTimeout.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            throw new TimeoutException(
                $"Process {SafeProcessName(process)} did not stop within "
                + $"{(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds:0} s.");
        }
    }

    private static string SafeProcessName(Process process)
    {
        try { return $"'{process.ProcessName}' (PID {process.Id})"; }
        catch (InvalidOperationException) { return "(not started)"; }
    }
}
