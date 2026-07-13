using System.Runtime.InteropServices;

namespace RevmRenderHost;

/// <summary>
/// Native renderer ABI version and deterministic result codes. The current
/// assembly is a managed contract shim; the exported C/C++ render host must
/// preserve these numeric values so ReVM can make lifecycle decisions without
/// parsing localized error strings.
/// </summary>
public static class RevmRendererAbi
{
    public const int AbiVersion = 1;
    public const int PreferredFormatBgra8888 = 1;
    public const string PreferredFormatName = "BGRA8888";
    public const string TransportKindShmFrameRing = "shm-frame-ring-v1";
    public const string TransportKindGpuProducer = "revm-gpu-producer-v1";

    public static RevmRendererStatus ValidateCreateConfig(in RevmRendererConfig config)
    {
        if (config.AbiVersion != AbiVersion) return RevmRendererStatus.UnsupportedAbi;
        if (config.ParentHwnd == IntPtr.Zero) return RevmRendererStatus.InvalidParentHwnd;
        if (config.Width <= 0 || config.Height <= 0) return RevmRendererStatus.InvalidSize;
        if (config.PreferredFormat != PreferredFormatBgra8888) return RevmRendererStatus.UnsupportedFormat;
        return RevmRendererStatus.Ok;
    }

    public static RevmRendererStatus ValidateTransportEndpoint(string endpointJson)
    {
        var status = RevmGpuProducerEndpoint.TryParse(endpointJson, out _);
        return status == RevmRendererStatus.UnsupportedTransportKind
            ? RevmFrameRingEndpoint.TryParse(endpointJson, out _)
            : status;
    }
}


/// <summary>
/// Stable C ABI config shape for the future native export:
/// int revm_renderer_create(const RevmRendererConfig* config, RevmRendererHandle* out_handle);
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RevmRendererConfig
{
    public RevmRendererConfig(IntPtr parentHwnd, int width, int height, int preferredFormat = RevmRendererAbi.PreferredFormatBgra8888)
    {
        AbiVersion = RevmRendererAbi.AbiVersion;
        ParentHwnd = parentHwnd;
        Width = width;
        Height = height;
        PreferredFormat = preferredFormat;
    }

    public int AbiVersion { get; }
    public IntPtr ParentHwnd { get; }
    public int Width { get; }
    public int Height { get; }
    public int PreferredFormat { get; }
}

public enum RevmRendererStatus
{
    Ok = 0,
    UnsupportedAbi = 1,
    InvalidParentHwnd = 2,
    InvalidSize = 3,
    UnsupportedFormat = 4,
    InvalidState = 5,
    InvalidTransportEndpoint = 6,
    UnsupportedTransportKind = 7,
    PlatformCreateFailed = 8,
    TransportAttachFailed = 9,
    AlreadyDestroyed = 10,
    InvalidFrameRing = 11
}

public enum RevmRendererLifecycleState
{
    Created = 0,
    TransportAttached = 1,
    Destroyed = 2
}

/// <summary>
/// C ABI entry points for the future native/AOT render-host boundary. They use
/// opaque GCHandle-backed session handles in the current managed shim so callers
/// can exercise deterministic create/attach/resize/rotation/destroy status codes
/// before the D3D/gfxstream implementation is swapped in.
/// </summary>
public static unsafe class RevmRendererNativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "revm_renderer_create")]
    public static int Create(RevmRendererConfig* config, IntPtr* outHandle)
    {
        if (outHandle == null) return (int)RevmRendererStatus.InvalidState;
        *outHandle = IntPtr.Zero;
        if (config == null) return (int)RevmRendererStatus.InvalidState;

        var status = RevmRendererAbi.ValidateCreateConfig(in *config);
        if (status != RevmRendererStatus.Ok) return (int)status;

        try
        {
            var session = new RenderHostSession(*config);
            *outHandle = GCHandle.ToIntPtr(GCHandle.Alloc(session));
            return (int)RevmRendererStatus.Ok;
        }
        catch
        {
            return (int)RevmRendererStatus.PlatformCreateFailed;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "revm_renderer_attach_transport")]
    public static int AttachTransport(IntPtr handle, IntPtr endpointJson)
    {
        if (!TryGetSession(handle, out var session)) return (int)RevmRendererStatus.InvalidState;
        var endpoint = Marshal.PtrToStringUTF8(endpointJson);
        if (endpoint is null) return (int)RevmRendererStatus.InvalidTransportEndpoint;
        return (int)session.AttachVmTransport(endpoint);
    }

    [UnmanagedCallersOnly(EntryPoint = "revm_renderer_resize")]
    public static int Resize(IntPtr handle, int width, int height)
    {
        if (!TryGetSession(handle, out var session)) return (int)RevmRendererStatus.InvalidState;
        return (int)session.Resize(width, height);
    }

    [UnmanagedCallersOnly(EntryPoint = "revm_renderer_set_rotation")]
    public static int SetRotation(IntPtr handle, int rotation)
    {
        if (!TryGetSession(handle, out var session)) return (int)RevmRendererStatus.InvalidState;
        return (int)session.SetRotation(rotation);
    }

    [UnmanagedCallersOnly(EntryPoint = "revm_renderer_present_latest_frame")]
    public static int PresentLatestFrame(IntPtr handle)
    {
        if (!TryGetSession(handle, out var session)) return (int)RevmRendererStatus.InvalidState;
        return (int)session.PresentLatestFrame();
    }

    [UnmanagedCallersOnly(EntryPoint = "revm_renderer_destroy")]
    public static int Destroy(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return (int)RevmRendererStatus.InvalidState;

        var gcHandle = GCHandle.FromIntPtr(handle);
        if (gcHandle.Target is not RenderHostSession session) return (int)RevmRendererStatus.InvalidState;

        var status = session.Destroy();
        gcHandle.Free();
        return (int)status;
    }

    private static bool TryGetSession(IntPtr handle, out RenderHostSession session)
    {
        session = null!;
        if (handle == IntPtr.Zero) return false;

        var gcHandle = GCHandle.FromIntPtr(handle);
        if (gcHandle.Target is not RenderHostSession target) return false;

        session = target;
        return true;
    }
}

/// <summary>
/// Deterministic managed lifecycle model for renderer bring-up tests. It mirrors
/// the C ABI that the native D3D/gfxstream implementation will expose, while
/// intentionally doing no display streaming and no ADB/scrcpy/SDL work.
/// </summary>
public sealed class RenderHostSession
{
    public RenderHostSession(RevmRendererConfig config)
    {
        var status = RevmRendererAbi.ValidateCreateConfig(config);
        if (status != RevmRendererStatus.Ok)
            throw new ArgumentException($"Invalid renderer config: {status}", nameof(config));

        ParentHwnd = config.ParentHwnd;
        Width = config.Width;
        Height = config.Height;
        PreferredFormat = config.PreferredFormat;
        Presenter = new RevmBgraFramePresenter(config);
    }

    public RenderHostSession(IntPtr parentHwnd, int width, int height)
        : this(new RevmRendererConfig(parentHwnd, width, height))
    {
    }

    public IntPtr ParentHwnd { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int PreferredFormat { get; }
    public int Rotation { get; private set; }
    public string? TransportEndpoint { get; private set; }
    public RevmFrameRingConsumer? FrameRingConsumer { get; private set; }
    public RevmFrameRingFrameSource? FrameSource { get; private set; }
    public RevmGpuProducerEndpoint? GfxstreamCommandEndpoint { get; private set; }
    public IRevmFramePresenter Presenter { get; }
    public RevmFrameRingSlot? LatestReadyFrame => FrameRingConsumer?.LatestReadySlot;
    public bool HasReadyFrame => FrameRingConsumer?.HasReadyFrame == true;
    public RevmRendererLifecycleState State { get; private set; } = RevmRendererLifecycleState.Created;

    public RevmRendererStatus AttachVmTransport(string endpoint)
    {
        if (State == RevmRendererLifecycleState.Destroyed) return RevmRendererStatus.AlreadyDestroyed;

        var attachEndpoint = endpoint;
        var producerStatus = RevmGpuProducerEndpoint.TryParse(endpoint, out var producerEndpoint);
        if (producerStatus == RevmRendererStatus.Ok && producerEndpoint is not null)
        {
            if (producerEndpoint.FrameEndpoint is null)
            {
                if (string.Equals(producerEndpoint.ProducerKind, "virtio-gpu-gfxstream", StringComparison.OrdinalIgnoreCase))
                {
                    FrameRingConsumer?.Dispose();
                    FrameRingConsumer = null;
                    FrameSource = null;
                    GfxstreamCommandEndpoint = producerEndpoint;
                    TransportEndpoint = endpoint;
                    State = RevmRendererLifecycleState.TransportAttached;
                    return RevmRendererStatus.Ok;
                }

                return RevmRendererStatus.TransportAttachFailed;
            }
            attachEndpoint = producerEndpoint.FrameEndpointJson;
        }
        else if (producerStatus != RevmRendererStatus.UnsupportedTransportKind)
            return producerStatus;

        var status = RevmFrameRingConsumer.TryOpen(attachEndpoint, out var consumer);
        if (status != RevmRendererStatus.Ok) return status;

        FrameRingConsumer?.Dispose();
        FrameRingConsumer = consumer;
        FrameSource = consumer is null ? null : new RevmFrameRingFrameSource(consumer);
        GfxstreamCommandEndpoint = null;
        TransportEndpoint = endpoint;
        State = RevmRendererLifecycleState.TransportAttached;
        return RevmRendererStatus.Ok;
    }

    public RevmRendererStatus TryReadLatestFrame(out RevmBgraFrame? frame)
    {
        frame = null;
        if (State == RevmRendererLifecycleState.Destroyed) return RevmRendererStatus.AlreadyDestroyed;
        return FrameSource?.TryReadLatestFrame(out frame) ?? RevmRendererStatus.InvalidState;
    }

    public RevmRendererStatus PresentLatestFrame()
    {
        if (State == RevmRendererLifecycleState.Destroyed) return RevmRendererStatus.AlreadyDestroyed;
        var status = TryReadLatestFrame(out var frame);
        if (status != RevmRendererStatus.Ok || frame is null) return status;
        return Presenter.Present(frame);
    }

    public RevmRendererStatus Resize(int width, int height)
    {
        if (State == RevmRendererLifecycleState.Destroyed) return RevmRendererStatus.AlreadyDestroyed;
        if (width <= 0 || height <= 0) return RevmRendererStatus.InvalidSize;
        var status = Presenter.Resize(width, height);
        if (status != RevmRendererStatus.Ok) return status;

        Width = width;
        Height = height;
        return RevmRendererStatus.Ok;
    }

    public RevmRendererStatus SetRotation(int rotation)
    {
        if (State == RevmRendererLifecycleState.Destroyed) return RevmRendererStatus.AlreadyDestroyed;
        if (rotation is not (0 or 90 or 180 or 270)) return RevmRendererStatus.InvalidState;
        Rotation = rotation;
        return RevmRendererStatus.Ok;
    }

    public RevmRendererStatus Destroy()
    {
        if (State == RevmRendererLifecycleState.Destroyed) return RevmRendererStatus.AlreadyDestroyed;
        State = RevmRendererLifecycleState.Destroyed;
        TransportEndpoint = null;
        FrameSource = null;
        GfxstreamCommandEndpoint = null;
        Presenter.Dispose();
        FrameRingConsumer?.Dispose();
        FrameRingConsumer = null;
        return RevmRendererStatus.Ok;
    }
}
