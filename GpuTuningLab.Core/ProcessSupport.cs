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

    public static async Task WaitForExitAfterStopAsync(Process process)
    {
        TryKillTree(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
