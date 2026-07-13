using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;

namespace GpuTuningLab.Core;

public sealed class NvApiTelemetryEnricher : ITelemetryEnricher
{
    private PhysicalGPU? _gpu;

    public string Name => "NVAPI";
    public bool Available => _gpu != null;

    public NvApiTelemetryEnricher()
    {
        try
        {
            NVIDIA.Initialize();
            _gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
        }
        catch
        {
            _gpu = null;
        }
    }

    public GpuTelemetrySample Enrich(GpuTelemetrySample sample)
    {
        if (_gpu == null) return sample;
        double? voltage = null;
        try
        {
            uint currentMicroVolt = GPUApi.GetCurrentVoltage(_gpu.Handle).ValueInMicroVolt;
            if (currentMicroVolt > 0)
                voltage = currentMicroVolt / 1_000_000.0;
        }
        catch
        {
            // Fall back to the current performance-state voltage below.
        }

        try
        {
            if (voltage.HasValue) return sample with { VoltageV = voltage };
            var voltages = _gpu.PerformanceStatesInfo.CurrentPerformanceState.Voltages;
            var core = voltages.FirstOrDefault(item =>
                item.VoltageDomain.ToString().Contains("Core", StringComparison.OrdinalIgnoreCase));
            core ??= voltages.FirstOrDefault(item => item.CurrentVoltageInMicroVolt > 0);
            if (core?.CurrentVoltageInMicroVolt > 0)
                voltage = core.CurrentVoltageInMicroVolt / 1_000_000.0;
        }
        catch
        {
            // Unsupported by this GPU/driver. Missing data remains null.
        }

        return sample with { VoltageV = voltage };
    }

    public void Dispose()
    {
        _gpu = null;
    }
}
