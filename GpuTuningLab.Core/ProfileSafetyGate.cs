namespace GpuTuningLab.Core;

public static class ProfileSafetyGate
{
    public static SafetyGateResult Check(
        GpuIdentity expectedGpu,
        GpuIdentity currentGpu,
        GpuTuningProfile requested,
        GpuTuningProfile? lastStable,
        int? currentVoltageMv,
        GpuReferenceEntry? reference,
        BaselineValidationResult baseline,
        GpuTelemetrySample capability)
    {
        var reasons = new List<string>();
        if (!baseline.Valid) reasons.Add("The stock baseline is not valid.");
        if (!SameHardware(expectedGpu, currentGpu))
            reasons.Add("GPU identity, subsystem or VBIOS changed.");
        if (reference != null && !GpuReferenceMatcher.SameFamily(reference.Model, currentGpu.Name))
            reasons.Add($"Reference model {reference.Model} does not match {currentGpu.Name}.");
        if (reference?.VramMiB is int expectedVram)
        {
            if (capability.VramTotalMiB is not double currentVram)
                reasons.Add("Current VRAM capacity is not available.");
            else if (Math.Abs(currentVram - expectedVram) > 384)
                reasons.Add($"Reference VRAM is {expectedVram} MiB; detected VRAM is {currentVram:0} MiB.");
        }
        if (reference?.DeviceIds.Count > 0
            && !reference.DeviceIds.Contains(currentGpu.DeviceId, StringComparer.OrdinalIgnoreCase))
            reasons.Add($"Device ID {currentGpu.DeviceId} is not covered by the reference.");
        bool trustedReference = reference != null
            && (reference.Confidence.Equals("reviewed", StringComparison.OrdinalIgnoreCase)
                || reference.Confidence.Equals("validated", StringComparison.OrdinalIgnoreCase));
        if (reference?.SearchEnvelope == null || !trustedReference)
            reasons.Add("No reviewed model-specific search envelope is available.");
        if (requested.TargetVoltageMv == null || requested.TargetClockMhz == null)
            reasons.Add("Target voltage and target clock must both be explicit.");

        SearchEnvelope? envelope = reference?.SearchEnvelope;
        if (requested.TargetVoltageMv is int voltage && envelope != null)
        {
            if (envelope.MinimumVoltageMv is int minimum && voltage < minimum)
                reasons.Add($"Requested voltage {voltage} mV is below the {minimum} mV envelope.");
            if (envelope.MaximumVoltageMv is int maximum && voltage > maximum)
                reasons.Add($"Requested voltage {voltage} mV is above the {maximum} mV envelope.");
            int? previous = lastStable?.TargetVoltageMv ?? currentVoltageMv;
            int allowedStep = envelope.VoltageStepMv ?? 25;
            if (previous.HasValue && Math.Abs(voltage - previous.Value) > allowedStep)
                reasons.Add($"Voltage step is {Math.Abs(voltage - previous.Value)} mV; maximum is {allowedStep} mV.");
        }

        if (requested.TargetClockMhz is int clock && envelope != null)
        {
            if (envelope.MinimumClockMhz is int minimum && clock < minimum)
                reasons.Add($"Requested clock {clock} MHz is below the {minimum} MHz envelope.");
            if (envelope.MaximumClockMhz is int maximum && clock > maximum)
                reasons.Add($"Requested clock {clock} MHz is above the {maximum} MHz envelope.");
        }

        if (requested.MemoryOffsetMhz is int memoryOffset
            && envelope?.MaximumMemoryOffsetMhz is int maximumMemory
            && Math.Abs(memoryOffset) > Math.Abs(maximumMemory))
            reasons.Add($"Memory offset {memoryOffset} MHz exceeds the {maximumMemory} MHz envelope.");

        if (requested.PowerLimitPercent is double percent)
        {
            if (capability.DefaultPowerLimitW is not double defaultW
                || capability.MinPowerLimitW is not double minimumW
                || capability.MaxPowerLimitW is not double maximumW)
            {
                reasons.Add("Hardware power-limit bounds are not available.");
            }
            else
            {
                double requestedW = defaultW * percent / 100.0;
                if (requestedW < minimumW || requestedW > maximumW)
                    reasons.Add($"Requested power limit is {requestedW:0.0} W; hardware range is {minimumW:0.0}-{maximumW:0.0} W.");
            }
        }

        return new SafetyGateResult(reasons.Count == 0, reasons);
    }

    private static bool SameHardware(GpuIdentity left, GpuIdentity right)
        => left.Uuid.Equals(right.Uuid, StringComparison.OrdinalIgnoreCase)
           && left.DeviceId.Equals(right.DeviceId, StringComparison.OrdinalIgnoreCase)
           && left.SubsystemId.Equals(right.SubsystemId, StringComparison.OrdinalIgnoreCase)
           && left.VbiosVersion.Equals(right.VbiosVersion, StringComparison.OrdinalIgnoreCase);
}
