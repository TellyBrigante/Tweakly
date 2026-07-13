using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.D3DCompiler.Compiler;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

const int NvidiaVendorId = 0x10DE;
const uint ThreadsPerGroup = 256;

try
{
    WorkloadOptions options = WorkloadOptions.Parse(args);
    WorkloadConfiguration configuration = WorkloadConfiguration.For(options.Mode);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
    Console.WriteLine("Stage: adapter discovery");
    using IDXGIAdapter1 adapter = FindNvidiaAdapter(factory);
    AdapterDescription1 description = adapter.Description1;

    FeatureLevel[] featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
    Console.WriteLine("Stage: Direct3D 11 device creation");
    Result deviceResult = D3D11CreateDevice(
        adapter,
        DriverType.Unknown,
        DeviceCreationFlags.None,
        featureLevels,
        out ID3D11Device device,
        out FeatureLevel featureLevel,
        out ID3D11DeviceContext context);
    deviceResult.CheckError();
    using (device)
    using (context)
    {
        Console.WriteLine($"GPU: {description.Description.Trim()}");
        Console.WriteLine($"Vendor ID: 0x{description.VendorId:X4}");
        Console.WriteLine($"Dedicated VRAM: {description.DedicatedVideoMemory / 1_048_576.0:0} MiB");
        Console.WriteLine($"Direct3D feature level: {featureLevel}");
        Console.WriteLine($"Duration: {options.DurationSeconds} s");
        Console.WriteLine($"Mode: {options.Mode.ToString().ToLowerInvariant()}");

        WorkloadMeasurement measurement = options.Mode == WorkloadMode.Graphics
            ? RunGraphicsWorkload(device, context, options.DurationSeconds, cancellation.Token)
            : RunComputeWorkload(device, context, options, configuration, cancellation.Token);
        Console.WriteLine($"Completed operations: {measurement.OperationCount}");
        Console.WriteLine($"Measured duration: {measurement.Duration.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s");
        Console.WriteLine($"Final score: {measurement.Score.ToString("0.000", CultureInfo.InvariantCulture)} {measurement.ScoreUnit}");
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Workload cancelled.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}

static WorkloadMeasurement RunComputeWorkload(
    ID3D11Device device,
    ID3D11DeviceContext context,
    WorkloadOptions options,
    WorkloadConfiguration configuration,
    CancellationToken cancellationToken)
{
    Console.WriteLine("Stage: workload resource creation");
    using ID3D11Buffer buffer = device.CreateBuffer(
        configuration.ElementCount * configuration.ElementStride,
        BindFlags.UnorderedAccess,
        ResourceUsage.Default,
        CpuAccessFlags.None,
        ResourceOptionFlags.BufferStructured,
        configuration.ElementStride);
    using ID3D11UnorderedAccessView uav = device.CreateUnorderedAccessView(
        buffer,
        new UnorderedAccessViewDescription(
            buffer,
            Format.Unknown,
            0,
            configuration.ElementCount,
            BufferUnorderedAccessViewFlags.None));
    Console.WriteLine("Stage: compute shader compilation");
    using ID3D11ComputeShader shader = CompileShader(device, configuration);
    using ID3D11Query completion = device.CreateQuery(new QueryDescription(QueryType.Event));

    context.CSSetShader(shader, null, 0);
    SetComputeUav(context, uav);
    Console.WriteLine("Stage: synchronized warm-up");
    DispatchBatch(context, completion, configuration, cancellationToken);

    long dispatchCount = 0;
    var timer = Stopwatch.StartNew();
    TimeSpan target = TimeSpan.FromSeconds(options.DurationSeconds);
    while (timer.Elapsed < target)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode == WorkloadMode.Transient)
        {
            TimeSpan activeUntil = timer.Elapsed + TimeSpan.FromMilliseconds(configuration.ActiveMilliseconds);
            while (timer.Elapsed < target && timer.Elapsed < activeUntil)
            {
                DispatchBatch(context, completion, configuration, cancellationToken);
                dispatchCount += configuration.DispatchesPerBatch;
            }
            int remainingMs = (int)Math.Max(0, (target - timer.Elapsed).TotalMilliseconds);
            if (remainingMs > 0)
                Thread.Sleep(Math.Min(configuration.IdleMilliseconds, remainingMs));
        }
        else
        {
            DispatchBatch(context, completion, configuration, cancellationToken);
            dispatchCount += configuration.DispatchesPerBatch;
        }
    }
    timer.Stop();

    SetComputeUav(context, null);
    context.CSSetShader(null, null, 0);
    context.Flush();
    return new WorkloadMeasurement(
        dispatchCount,
        timer.Elapsed,
        configuration.CalculateScore(dispatchCount, timer.Elapsed),
        configuration.ScoreUnit);
}

static WorkloadMeasurement RunGraphicsWorkload(
    ID3D11Device device,
    ID3D11DeviceContext context,
    int durationSeconds,
    CancellationToken cancellationToken)
{
    const uint width = 1920;
    const uint height = 1080;
    const int drawsPerBatch = 2;
    Console.WriteLine("Stage: graphics target creation");
    using ID3D11Texture2D target = device.CreateTexture2D(new Texture2DDescription(
        Format.R16G16B16A16_Float,
        width,
        height,
        1,
        1,
        BindFlags.RenderTarget,
        ResourceUsage.Default,
        CpuAccessFlags.None,
        1,
        0,
        ResourceOptionFlags.None));
    using ID3D11RenderTargetView renderTarget = device.CreateRenderTargetView(target, null);
    using ID3D11Texture2D readback = device.CreateTexture2D(new Texture2DDescription(
        Format.R16G16B16A16_Float,
        width,
        height,
        1,
        1,
        BindFlags.None,
        ResourceUsage.Staging,
        CpuAccessFlags.Read,
        1,
        0,
        ResourceOptionFlags.None));
    Console.WriteLine("Stage: graphics shader compilation");
    (ID3D11VertexShader vertexShader, ID3D11PixelShader pixelShader) = CompileGraphicsShaders(device);
    using (vertexShader)
    using (pixelShader)
    using (ID3D11Query completion = device.CreateQuery(new QueryDescription(QueryType.Event)))
    {
        context.OMSetRenderTargets(renderTarget);
        context.RSSetViewport(0, 0, width, height);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertexShader, null, 0);
        context.PSSetShader(pixelShader, null, 0);
        var clearColor = new Color4(0.97f, 0.03f, 0.91f, 1.0f);
        context.ClearRenderTargetView(renderTarget, clearColor);
        Console.WriteLine("Stage: synchronized warm-up");
        DrawBatch(context, completion, drawsPerBatch, cancellationToken);

        long draws = 0;
        var timer = Stopwatch.StartNew();
        TimeSpan targetDuration = TimeSpan.FromSeconds(durationSeconds);
        while (timer.Elapsed < targetDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawBatch(context, completion, drawsPerBatch, cancellationToken);
            draws += drawsPerBatch;
        }
        timer.Stop();

        context.PSSetShader(null, null, 0);
        context.VSSetShader(null, null, 0);
        context.UnsetRenderTargets();
        context.CopyResource(readback, target);
        context.End(completion);
        WaitForGpu(context, completion, cancellationToken);
        VerifyGraphicsOutput(context, readback, width, height, clearColor);
        context.Flush();
        double megapixelsPerSecond = draws * (double)width * height / timer.Elapsed.TotalSeconds / 1_000_000.0;
        return new WorkloadMeasurement(draws, timer.Elapsed, megapixelsPerSecond, "Mpx/s");
    }
}

static void VerifyGraphicsOutput(
    ID3D11DeviceContext context,
    ID3D11Texture2D readback,
    uint width,
    uint height,
    Color4 clearColor)
{
    Result result = context.Map(
        readback,
        0,
        MapMode.Read,
        Vortice.Direct3D11.MapFlags.None,
        out MappedSubresource mapped);
    result.CheckError();
    try
    {
        const int bytesPerPixel = 8;
        IntPtr pixel = mapped.DataPointer
            + (int)(height / 2 * mapped.RowPitch)
            + (int)(width / 2 * bytesPerPixel);
        ushort[] actual =
        [
            unchecked((ushort)Marshal.ReadInt16(pixel, 0)),
            unchecked((ushort)Marshal.ReadInt16(pixel, 2)),
            unchecked((ushort)Marshal.ReadInt16(pixel, 4)),
            unchecked((ushort)Marshal.ReadInt16(pixel, 6))
        ];
        ushort[] marker =
        [
            unchecked((ushort)BitConverter.HalfToInt16Bits((Half)clearColor.R)),
            unchecked((ushort)BitConverter.HalfToInt16Bits((Half)clearColor.G)),
            unchecked((ushort)BitConverter.HalfToInt16Bits((Half)clearColor.B)),
            unchecked((ushort)BitConverter.HalfToInt16Bits((Half)clearColor.A))
        ];
        if (actual.SequenceEqual(marker))
            throw new InvalidOperationException("Graphics verification failed: the render target still contains the clear marker.");
    }
    finally
    {
        context.Unmap(readback, 0);
    }
}

static IDXGIAdapter1 FindNvidiaAdapter(IDXGIFactory1 factory)
{
    for (uint index = 0; ; index++)
    {
        Result result = factory.EnumAdapters1(index, out IDXGIAdapter1 candidate);
        if (result.Failure)
            break;

        if (candidate.Description1.VendorId == NvidiaVendorId)
            return candidate;

        candidate.Dispose();
    }

    throw new InvalidOperationException("No NVIDIA Direct3D 11 adapter was found.");
}

static unsafe ID3D11ComputeShader CompileShader(
    ID3D11Device device,
    WorkloadConfiguration configuration)
{
    string source = configuration.Mode == WorkloadMode.Vram
        ? $$"""
        RWStructuredBuffer<uint4> Values : register(u0);

        [numthreads(256, 1, 1)]
        void main(uint3 dispatchId : SV_DispatchThreadID)
        {
            uint index = dispatchId.x;
            uint distant = (index + {{configuration.ElementCount / 2}}u) & {{configuration.ElementCount - 1}}u;
            uint4 local = Values[index];
            uint4 remote = Values[distant];
            Values[index] = (remote ^ local.yzwx) + uint4(index, distant, index ^ distant, 0x9e3779b9u);
        }
        """
        : """
        RWStructuredBuffer<float4> Values : register(u0);

        [numthreads(256, 1, 1)]
        void main(uint3 dispatchId : SV_DispatchThreadID)
        {
            uint index = dispatchId.x;
            float seed = (index + 1u) * 0.00000095367431640625f;
            float4 value = Values[index] + float4(seed, seed * 1.37f, seed * 2.11f, seed * 3.17f);

            [loop]
            for (uint iteration = 0; iteration < 64u; iteration++)
            {
                value = mad(value.yzwx, float4(1.00031f, 0.99971f, 1.00013f, 0.99991f), value * 0.6180339f);
                value += rsqrt(abs(value.wxyz) + 0.125f);
                value = frac(value * 0.754877666f + float4(seed, 0.12345f, 0.54321f, 0.31415f));
            }

            Values[index] = value;
        }
        """;

    Result result = Compile(
        source,
        [],
        null!,
        "main",
        "GpuTuningLab.Workload.hlsl",
        "cs_5_0",
        ShaderFlags.OptimizationLevel3,
        EffectFlags.None,
        out Blob bytecode,
        out Blob errors);
    using (bytecode)
    using (errors)
    {
        result.CheckError();
        return device.CreateComputeShader(bytecode.BufferPointer.ToPointer(), bytecode.BufferSize, null);
    }
}

static unsafe (ID3D11VertexShader Vertex, ID3D11PixelShader Pixel) CompileGraphicsShaders(
    ID3D11Device device)
{
    const string source = """
        struct VertexOutput
        {
            float4 Position : SV_POSITION;
            float2 UV : TEXCOORD0;
        };

        VertexOutput VSMain(uint vertexId : SV_VertexID)
        {
            float2 position = vertexId == 0 ? float2(-1.0f, -1.0f)
                : vertexId == 1 ? float2(-1.0f, 3.0f)
                : float2(3.0f, -1.0f);
            VertexOutput output;
            output.Position = float4(position, 0.0f, 1.0f);
            output.UV = position * float2(0.5f, -0.5f) + 0.5f;
            return output;
        }

        float4 PSMain(VertexOutput input) : SV_TARGET
        {
            float3 value = float3(input.UV, input.UV.x + input.UV.y) + 0.001f;
            [loop]
            for (uint iteration = 0; iteration < 96u; iteration++)
            {
                value = frac(sin(value.zxy * 2.173f + float3(0.17f, 0.31f, 0.47f)) * 43758.5453f);
                value = mad(value, value.yzx + 0.125f, 0.03125f);
            }
            return float4(value, 1.0f);
        }
        """;

    Result vertexResult = Compile(
        source, [], null!, "VSMain", "GpuTuningLab.Graphics.hlsl", "vs_5_0",
        ShaderFlags.OptimizationLevel3, EffectFlags.None, out Blob vertexBytecode, out Blob vertexErrors);
    using (vertexBytecode)
    using (vertexErrors)
    {
        vertexResult.CheckError();
        ID3D11VertexShader vertex = device.CreateVertexShader(
            vertexBytecode.BufferPointer.ToPointer(), vertexBytecode.BufferSize, null);
        try
        {
            Result pixelResult = Compile(
                source, [], null!, "PSMain", "GpuTuningLab.Graphics.hlsl", "ps_5_0",
                ShaderFlags.OptimizationLevel3, EffectFlags.None, out Blob pixelBytecode, out Blob pixelErrors);
            using (pixelBytecode)
            using (pixelErrors)
            {
                pixelResult.CheckError();
                ID3D11PixelShader pixel = device.CreatePixelShader(
                    pixelBytecode.BufferPointer.ToPointer(), pixelBytecode.BufferSize, null);
                return (vertex, pixel);
            }
        }
        catch
        {
            vertex.Dispose();
            throw;
        }
    }
}

static void SetComputeUav(ID3D11DeviceContext context, ID3D11UnorderedAccessView? uav)
{
    context.CSSetUnorderedAccessViews(0, 1, [uav!], [uint.MaxValue]);
}

static void DispatchBatch(
    ID3D11DeviceContext context,
    ID3D11Query completion,
    WorkloadConfiguration configuration,
    CancellationToken cancellationToken)
{
    for (int index = 0; index < configuration.DispatchesPerBatch; index++)
        context.Dispatch(configuration.ElementCount / ThreadsPerGroup, 1, 1);

    context.End(completion);
    WaitForGpu(context, completion, cancellationToken);
}

static void DrawBatch(
    ID3D11DeviceContext context,
    ID3D11Query completion,
    int drawCount,
    CancellationToken cancellationToken)
{
    for (int index = 0; index < drawCount; index++)
        context.Draw(3, 0);
    context.End(completion);
    WaitForGpu(context, completion, cancellationToken);
}

static void WaitForGpu(
    ID3D11DeviceContext context,
    ID3D11Query completion,
    CancellationToken cancellationToken)
{
    while (!context.IsDataAvailable(completion, AsyncGetDataFlags.None))
    {
        cancellationToken.ThrowIfCancellationRequested();
        Thread.Sleep(0);
    }
}

internal enum WorkloadMode
{
    Compute,
    Graphics,
    Vram,
    Transient
}

internal sealed record WorkloadOptions(int DurationSeconds, WorkloadMode Mode)
{
    public static WorkloadOptions Parse(string[] arguments)
    {
        int duration = 10;
        WorkloadMode mode = WorkloadMode.Compute;
        for (int index = 0; index < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], "--seconds", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Length ||
                    !int.TryParse(arguments[index], NumberStyles.None, CultureInfo.InvariantCulture, out duration))
                    throw new ArgumentException("--seconds requires an integer value.");
            }
            else if (string.Equals(arguments[index], "--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Length ||
                    !Enum.TryParse(arguments[index], ignoreCase: true, out mode))
                    throw new ArgumentException("--mode must be compute, graphics, vram or transient.");
            }
            else
            {
                throw new ArgumentException($"Unknown argument: {arguments[index]}");
            }
        }

        if (duration is < 2 or > 120)
            throw new ArgumentException("--seconds must be an integer from 2 to 120.");
        return new WorkloadOptions(duration, mode);
    }
}

internal sealed record WorkloadConfiguration(
    WorkloadMode Mode,
    uint ElementCount,
    uint ElementStride,
    uint ShaderIterations,
    int DispatchesPerBatch,
    int ActiveMilliseconds,
    int IdleMilliseconds,
    string ScoreUnit,
    double BytesPerElement)
{
    public static WorkloadConfiguration For(WorkloadMode mode) => mode switch
    {
        WorkloadMode.Compute => new(mode, 1_048_576, 16, 64, 4, 0, 0, "G element-iterations/s", 0),
        WorkloadMode.Graphics => new(mode, 1, 16, 1, 1, 0, 0, "Mpx/s", 0),
        WorkloadMode.Vram => new(mode, 8_388_608, 16, 1, 1, 0, 0, "GiB/s", 48),
        WorkloadMode.Transient => new(mode, 1_048_576, 16, 64, 4, 300, 300, "G element-iterations/s", 0),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public double CalculateScore(long dispatchCount, TimeSpan elapsed)
    {
        if (Mode == WorkloadMode.Vram)
        {
            double bytes = dispatchCount * (double)ElementCount * BytesPerElement;
            return bytes / elapsed.TotalSeconds / 1_073_741_824.0;
        }

        double elementIterations = dispatchCount * (double)ElementCount * ShaderIterations;
        return elementIterations / elapsed.TotalSeconds / 1_000_000_000.0;
    }
}

internal sealed record WorkloadMeasurement(
    long OperationCount,
    TimeSpan Duration,
    double Score,
    string ScoreUnit);
