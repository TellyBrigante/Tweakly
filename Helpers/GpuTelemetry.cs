using System;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.GPU;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Télémétrie GPU NVIDIA EN PROCESS via NvAPI (pas de spawn nvidia-smi → ~2,5 ms par
    /// lecture, validé 2026-06-14, contre ~14 ms pour Process.GetProcesses qui faisait
    /// laguer). Handle GPU persistant (init une fois). Utilisé par le sampler 1 Hz du
    /// suivi en jeu pour corréler temp/usage/clocks/VRAM/power aux drops — SANS process spawn.
    /// Tout est best-effort : si NvAPI échoue (pas de carte Nvidia, pilote), renvoie des NaN.
    /// </summary>
    public sealed class GpuTelemetry : IDisposable
    {
        private PhysicalGPU? _gpu;
        private bool _ok;
        private static bool _initialized;

        public bool Available => _ok;

        public GpuTelemetry()
        {
            try
            {
                if (!_initialized) { NVIDIA.Initialize(); _initialized = true; }
                var gpus = PhysicalGPU.GetPhysicalGPUs();
                if (gpus.Length > 0) { _gpu = gpus[0]; _ok = true; }
            }
            catch { _ok = false; }
        }

        public GpuReading Read()
        {
            var r = new GpuReading
            {
                TempC = double.NaN, UsagePct = double.NaN, CoreMhz = double.NaN,
                MemMhz = double.NaN, VramUsedMB = double.NaN, PowerW = double.NaN,
            };
            if (!_ok || _gpu == null) return r;
            try { foreach (var s in _gpu.ThermalInformation.ThermalSensors) { r.TempC = s.CurrentTemperature; break; } } catch { }
            try { r.UsagePct = _gpu.UsageInformation.GPU.Percentage; } catch { }
            try
            {
                var c = _gpu.CurrentClockFrequencies;
                r.CoreMhz = c.GraphicsClock.Frequency / 1000.0;
                r.MemMhz = c.MemoryClock.Frequency / 1000.0;
            }
            catch { }
            try
            {
                var mi = _gpu.MemoryInformation;
                double totalKb = mi.DedicatedVideoMemoryInkB;
                double freeKb = mi.CurrentAvailableDedicatedVideoMemoryInkB;
                r.VramUsedMB = (totalKb - freeKb) / 1024.0;
                r.VramTotalMB = totalKb / 1024.0;
            }
            catch { }
            return r;
        }

        public void Dispose() { _gpu = null; _ok = false; }
    }

    public sealed class GpuReading
    {
        public double TempC;
        public double UsagePct;
        public double CoreMhz;
        public double MemMhz;
        public double VramUsedMB;
        public double VramTotalMB = double.NaN;
        public double PowerW;
    }
}
