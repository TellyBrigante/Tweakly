using System.Threading;

namespace Optimisation_Tool.Helpers;

/// <summary>
/// Serializes LibreHardwareMonitor access. The driver can stall when multiple
/// Computer instances are opened or updated concurrently in the same process.
/// </summary>
internal static class HardwareMonitorAccess
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable Enter(CancellationToken cancellationToken = default)
    {
        Gate.Wait(cancellationToken);
        return new Lease();
    }

    public static bool TryEnter(out IDisposable? lease)
    {
        if (!Gate.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new Lease();
        return true;
    }

    private sealed class Lease : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Gate.Release();
        }
    }
}
