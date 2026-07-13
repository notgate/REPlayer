using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.NativeRendering;

/// <summary>
/// Deterministic renderer-controller scaffold. Today it creates the owned child HWND
/// that the native render host will present into; the controller boundary is already
/// the production boundary used for later revm-render-host/gfxstream attachment.
/// </summary>
public sealed class RenderHostController : IRenderHostController
{
    private const int AbiVersion = 1;
    private const string TransportKindShmFrameRing = "shm-frame-ring-v1";
    private const string TransportKindGpuProducer = "revm-gpu-producer-v1";
    private const string PreferredFormatName = "BGRA8888";
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SS_BLACKRECT = 0x00000004;

    private readonly RenderHostOptions _options;
    private NativeRenderHostAbiBinding? _nativeAbi;
    private RevmRenderHost.RenderHostSession? _managedRenderSession;
    private bool _disposed;

    public RenderHostController(RenderHostOptions options)
    {
        _options = options;
        TransportEndpoint = JsonSerializer.Serialize(new
        {
            kind = "renderer-controller-pending-v1",
            vmId = options.VmId,
            rendererPath = options.RendererPath,
            preferredFormat = "BGRA8888"
        });
    }

    public IntPtr ChildHwnd { get; private set; }
    public string TransportEndpoint { get; private set; }
    public RenderHostControllerStatus LastStatus { get; private set; } = RenderHostControllerStatus.Ok;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    public Task StartAsync(IntPtr parentHwnd, int width, int height, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var createStatus = ValidateCreateConfig(parentHwnd, width, height);
        if (createStatus != RenderHostControllerStatus.Ok)
        {
            LastStatus = createStatus;
            throw new InvalidOperationException($"Invalid render host create config: {createStatus}");
        }

        if (ChildHwnd != IntPtr.Zero)
        {
            Resize(width, height);
            return Task.CompletedTask;
        }

        ChildHwnd = CreateWindowExW(0, "STATIC", null,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | SS_BLACKRECT,
            0, 0, Math.Max(width, 1), Math.Max(height, 1), parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (ChildHwnd == IntPtr.Zero)
        {
            LastStatus = RenderHostControllerStatus.PlatformCreateFailed;
            throw new InvalidOperationException($"CreateWindowExW render surface failed: {Marshal.GetLastWin32Error()}");
        }

        TransportEndpoint = ShmFrameRingEndpointFactory.CreateLiveScanoutGpuProducerEndpointJson(
            _options.VmId,
            Math.Max(width, 1),
            Math.Max(height, 1),
            _options.TransportDirectory,
            _options.RendererApi);

        if (NativeRenderHostAbiBinding.TryLoad(_options.RendererPath, out var nativeAbi) && nativeAbi is not null)
        {
            var nativeStatus = nativeAbi.Create(ChildHwnd, Math.Max(width, 1), Math.Max(height, 1), _options.PreferredFormat);
            if (nativeStatus == RenderHostControllerStatus.Ok)
            {
                _nativeAbi = nativeAbi;
            }
            else
            {
                nativeAbi.Dispose();
                LastStatus = nativeStatus;
                throw new InvalidOperationException($"Native render host ABI create failed: {nativeStatus}");
            }
        }
        else
        {
            // The checked-in revm-render-host artifact is currently a managed
            // scaffold DLL, not a NativeAOT/exported DLL. Use the same managed
            // renderer implementation in-process as the fallback so the owned
            // child HWND still receives shm-frame-ring-v1 BGRA frames. This keeps
            // display native/local and avoids scrcpy/ADB display while the final
            // exported D3D/gfxstream DLL is being brought up.
            _managedRenderSession = new RevmRenderHost.RenderHostSession(ChildHwnd, Math.Max(width, 1), Math.Max(height, 1));
        }

        LastStatus = RenderHostControllerStatus.Ok;

        return Task.CompletedTask;
    }

    public Task AttachTransportAsync(string endpoint, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            LastStatus = RenderHostControllerStatus.InvalidTransportEndpoint;
            throw new ArgumentException("Renderer transport endpoint must not be empty.", nameof(endpoint));
        }

        var attachStatus = ValidateTransportEndpoint(endpoint);
        if (attachStatus != RenderHostControllerStatus.Ok)
        {
            LastStatus = attachStatus;
            throw new InvalidOperationException($"Invalid renderer transport endpoint: {attachStatus}");
        }

        if (_nativeAbi is { HasRendererHandle: true })
        {
            var nativeStatus = _nativeAbi.AttachTransport(endpoint);
            if (nativeStatus != RenderHostControllerStatus.Ok)
            {
                LastStatus = nativeStatus;
                throw new InvalidOperationException($"Native render host transport attach failed: {nativeStatus}");
            }
        }
        else if (_managedRenderSession is not null)
        {
            var managedStatus = MapManagedStatus(_managedRenderSession.AttachVmTransport(endpoint));
            if (managedStatus != RenderHostControllerStatus.Ok)
            {
                LastStatus = managedStatus;
                throw new InvalidOperationException($"Managed render host transport attach failed: {managedStatus}");
            }
        }

        TransportEndpoint = endpoint;
        LastStatus = RenderHostControllerStatus.Ok;
        return Task.CompletedTask;
    }

    public RenderHostControllerStatus PresentLatestFrame()
    {
        ThrowIfDisposed();
        if (_nativeAbi is not { HasRendererHandle: true })
        {
            LastStatus = _managedRenderSession is null
                ? RenderHostControllerStatus.InvalidState
                : MapManagedStatus(_managedRenderSession.PresentLatestFrame());
            return LastStatus;
        }

        LastStatus = _nativeAbi.PresentLatestFrame();
        return LastStatus;
    }

    public void Resize(int width, int height)
    {
        ThrowIfDisposed();
        if (width <= 0 || height <= 0)
        {
            LastStatus = RenderHostControllerStatus.InvalidSize;
            throw new ArgumentOutOfRangeException(nameof(width), $"Renderer size must be positive: {width}x{height}.");
        }
        if (ChildHwnd != IntPtr.Zero)
            MoveWindow(ChildHwnd, 0, 0, Math.Max(width, 1), Math.Max(height, 1), true);
        if (_nativeAbi is { HasRendererHandle: true })
        {
            var nativeStatus = _nativeAbi.Resize(width, height);
            if (nativeStatus != RenderHostControllerStatus.Ok)
            {
                LastStatus = nativeStatus;
                throw new InvalidOperationException($"Native render host resize failed: {nativeStatus}");
            }
        }
        else if (_managedRenderSession is not null)
        {
            var managedStatus = MapManagedStatus(_managedRenderSession.Resize(width, height));
            if (managedStatus != RenderHostControllerStatus.Ok)
            {
                LastStatus = managedStatus;
                throw new InvalidOperationException($"Managed render host resize failed: {managedStatus}");
            }
        }
        LastStatus = RenderHostControllerStatus.Ok;
    }

    public void SetRotation(int rotation)
    {
        ThrowIfDisposed();
        if (rotation is not (0 or 90 or 180 or 270))
        {
            LastStatus = RenderHostControllerStatus.InvalidState;
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Rotation must be 0, 90, 180, or 270 degrees.");
        }
        if (_nativeAbi is { HasRendererHandle: true })
        {
            var nativeStatus = _nativeAbi.SetRotation(rotation);
            if (nativeStatus != RenderHostControllerStatus.Ok)
            {
                LastStatus = nativeStatus;
                throw new InvalidOperationException($"Native render host rotation failed: {nativeStatus}");
            }
        }
        else if (_managedRenderSession is not null)
        {
            var managedStatus = MapManagedStatus(_managedRenderSession.SetRotation(rotation));
            if (managedStatus != RenderHostControllerStatus.Ok)
            {
                LastStatus = managedStatus;
                throw new InvalidOperationException($"Managed render host rotation failed: {managedStatus}");
            }
        }
        LastStatus = RenderHostControllerStatus.Ok;
    }

    public Task StopAsync()
    {
        _nativeAbi?.Dispose();
        _nativeAbi = null;
        _managedRenderSession?.Destroy();
        _managedRenderSession = null;

        if (ChildHwnd != IntPtr.Zero)
        {
            DestroyWindow(ChildHwnd);
            ChildHwnd = IntPtr.Zero;
        }

        LastStatus = RenderHostControllerStatus.Ok;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (!_disposed) return;
        LastStatus = RenderHostControllerStatus.Disposed;
        throw new ObjectDisposedException(nameof(RenderHostController));
    }

    private static RenderHostControllerStatus MapManagedStatus(RevmRenderHost.RevmRendererStatus status)
        => Enum.IsDefined(typeof(RenderHostControllerStatus), (int)status)
            ? (RenderHostControllerStatus)(int)status
            : RenderHostControllerStatus.InvalidState;

    private RenderHostControllerStatus ValidateCreateConfig(IntPtr parentHwnd, int width, int height)
    {
        if (_options.PreferredFormat != RenderHostOptions.Bgra8888) return RenderHostControllerStatus.UnsupportedFormat;
        if (parentHwnd == IntPtr.Zero) return RenderHostControllerStatus.InvalidParentHwnd;
        if (width <= 0 || height <= 0) return RenderHostControllerStatus.InvalidSize;
        return RenderHostControllerStatus.Ok;
    }

    private static RenderHostControllerStatus ValidateTransportEndpoint(string endpointJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
            var abiVersion = root.TryGetProperty("abiVersion", out var abiProp) && abiProp.TryGetInt32(out var abi) ? abi : 0;
            if (string.Equals(kind, TransportKindGpuProducer, StringComparison.OrdinalIgnoreCase))
                return ValidateGpuProducerEndpoint(root, abiVersion);

            var format = root.TryGetProperty("format", out var formatProp) ? formatProp.GetString() : null;
            var width = root.TryGetProperty("width", out var widthProp) && widthProp.TryGetInt32(out var w) ? w : 0;
            var height = root.TryGetProperty("height", out var heightProp) && heightProp.TryGetInt32(out var h) ? h : 0;
            var stride = root.TryGetProperty("stride", out var strideProp) && strideProp.TryGetInt32(out var s) ? s : 0;
            var slots = root.TryGetProperty("slots", out var slotsProp) && slotsProp.TryGetInt32(out var n) ? n : 0;

            if (abiVersion != AbiVersion) return RenderHostControllerStatus.UnsupportedAbi;
            if (!string.Equals(kind, TransportKindShmFrameRing, StringComparison.OrdinalIgnoreCase))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            if (!string.Equals(format, PreferredFormatName, StringComparison.OrdinalIgnoreCase))
                return RenderHostControllerStatus.UnsupportedFormat;
            if (width <= 0 || height <= 0 || stride < width * 4) return RenderHostControllerStatus.InvalidSize;
            if (slots is < 1 or > 3) return RenderHostControllerStatus.InvalidTransportEndpoint;
            return RenderHostControllerStatus.Ok;
        }
        catch (JsonException)
        {
            return RenderHostControllerStatus.InvalidTransportEndpoint;
        }
    }

    private static RenderHostControllerStatus ValidateGpuProducerEndpoint(JsonElement root, int abiVersion)
    {
        if (abiVersion != AbiVersion) return RenderHostControllerStatus.UnsupportedAbi;
        var producerKind = root.TryGetProperty("producerKind", out var producerProp) ? producerProp.GetString() ?? string.Empty : string.Empty;
        var displayMode = root.TryGetProperty("displayMode", out var displayProp) ? displayProp.GetString() ?? string.Empty : string.Empty;
        if (ContainsBannedDisplayToken(producerKind) || ContainsBannedDisplayToken(displayMode))
            return RenderHostControllerStatus.UnsupportedTransportKind;
        if (string.Equals(producerKind, "virtio-gpu-gfxstream", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("capabilities", out var gfxstreamCaps) || gfxstreamCaps.ValueKind != JsonValueKind.Object)
                return RenderHostControllerStatus.InvalidTransportEndpoint;
            if (BoolProp(gfxstreamCaps, "requiresAdb") || BoolProp(gfxstreamCaps, "usesEncodedVideo") ||
                BoolProp(gfxstreamCaps, "producesBgraFrames") || !BoolProp(gfxstreamCaps, "producesGpuCommands") ||
                !BoolProp(gfxstreamCaps, "supportsHostGpu") || !BoolProp(gfxstreamCaps, "supportsVirtioGpu") ||
                !BoolProp(gfxstreamCaps, "supportsGfxstream"))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            if (!root.TryGetProperty("commandEndpoint", out var commandEndpoint) || commandEndpoint.ValueKind != JsonValueKind.Object)
                return RenderHostControllerStatus.InvalidTransportEndpoint;
            var commandKind = commandEndpoint.TryGetProperty("kind", out var commandKindProp) ? commandKindProp.GetString() : null;
            if (!string.Equals(commandKind, "gfxstream-command-stream-v1", StringComparison.OrdinalIgnoreCase))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            var rendererStack = commandEndpoint.TryGetProperty("rendererStack", out var stackProp) ? stackProp.GetString() : null;
            if (!string.Equals(rendererStack, "gfxstream-fastpipe", StringComparison.OrdinalIgnoreCase))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            var rendererApi = commandEndpoint.TryGetProperty("rendererApi", out var apiProp) ? apiProp.GetString() : null;
            if (!string.Equals(rendererApi, "vulkan", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rendererApi, "opengl", StringComparison.OrdinalIgnoreCase))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            return RenderHostControllerStatus.Ok;
        }

        if (string.Equals(producerKind, "virtio-gpu-gfxstream-scanout-import", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("capabilities", out var scanoutCaps) || scanoutCaps.ValueKind != JsonValueKind.Object)
                return RenderHostControllerStatus.InvalidTransportEndpoint;
            if (BoolProp(scanoutCaps, "requiresAdb") || BoolProp(scanoutCaps, "usesEncodedVideo") ||
                !BoolProp(scanoutCaps, "producesBgraFrames") || !BoolProp(scanoutCaps, "producesGpuCommands") ||
                !BoolProp(scanoutCaps, "supportsVirtioGpu") || !BoolProp(scanoutCaps, "supportsGfxstream"))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            if (!root.TryGetProperty("commandEndpoint", out var commandEndpoint) || commandEndpoint.ValueKind != JsonValueKind.Object)
                return RenderHostControllerStatus.InvalidTransportEndpoint;
            var commandKind = commandEndpoint.TryGetProperty("kind", out var commandKindProp) ? commandKindProp.GetString() : null;
            if (!string.Equals(commandKind, "gfxstream-command-stream-v1", StringComparison.OrdinalIgnoreCase))
                return RenderHostControllerStatus.UnsupportedTransportKind;
            if (!root.TryGetProperty("frameEndpoint", out var scanoutFrameEndpoint) || scanoutFrameEndpoint.ValueKind != JsonValueKind.Object)
                return RenderHostControllerStatus.InvalidTransportEndpoint;
            return ValidateFrameRingEndpoint(scanoutFrameEndpoint);
        }
        if (!string.Equals(producerKind, "shm-bgra-frame-ring", StringComparison.OrdinalIgnoreCase))
            return RenderHostControllerStatus.UnsupportedTransportKind;
        if (!root.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
            return RenderHostControllerStatus.InvalidTransportEndpoint;
        if (BoolProp(caps, "requiresAdb") || BoolProp(caps, "usesEncodedVideo") || BoolProp(caps, "producesGpuCommands") || !BoolProp(caps, "producesBgraFrames"))
            return RenderHostControllerStatus.UnsupportedTransportKind;
        if (!root.TryGetProperty("frameEndpoint", out var frameEndpoint) || frameEndpoint.ValueKind != JsonValueKind.Object)
            return RenderHostControllerStatus.InvalidTransportEndpoint;
        return ValidateFrameRingEndpoint(frameEndpoint);
    }

    private static RenderHostControllerStatus ValidateFrameRingEndpoint(JsonElement root)
    {
        var kind = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
        var abiVersion = root.TryGetProperty("abiVersion", out var abiProp) && abiProp.TryGetInt32(out var abi) ? abi : 0;
        var format = root.TryGetProperty("format", out var formatProp) ? formatProp.GetString() : null;
        var width = root.TryGetProperty("width", out var widthProp) && widthProp.TryGetInt32(out var w) ? w : 0;
        var height = root.TryGetProperty("height", out var heightProp) && heightProp.TryGetInt32(out var h) ? h : 0;
        var stride = root.TryGetProperty("stride", out var strideProp) && strideProp.TryGetInt32(out var s) ? s : 0;
        var slots = root.TryGetProperty("slots", out var slotsProp) && slotsProp.TryGetInt32(out var n) ? n : 0;

        if (abiVersion != AbiVersion) return RenderHostControllerStatus.UnsupportedAbi;
        if (!string.Equals(kind, TransportKindShmFrameRing, StringComparison.OrdinalIgnoreCase))
            return RenderHostControllerStatus.UnsupportedTransportKind;
        if (!string.Equals(format, PreferredFormatName, StringComparison.OrdinalIgnoreCase))
            return RenderHostControllerStatus.UnsupportedFormat;
        if (width <= 0 || height <= 0 || stride < width * 4) return RenderHostControllerStatus.InvalidSize;
        if (slots is < 1 or > 3) return RenderHostControllerStatus.InvalidTransportEndpoint;
        return RenderHostControllerStatus.Ok;
    }

    private static bool BoolProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static bool ContainsBannedDisplayToken(string value)
    {
        var banned = new[] { "scrcpy", "adb-display", "adb_display", "sdl", "h264", "h.264", "video", "webrtc", "encoded", "decoder", "encoder" };
        return banned.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
