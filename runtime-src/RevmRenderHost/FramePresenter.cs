using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RevmRenderHost;

/// <summary>
/// Deterministic renderer-side upload/present seam. This is the smallest
/// contract between copied BGRA frames and the future native child-HWND D3D
/// swapchain: callers hand over a raw BGRA8888 frame and receive stable status
/// codes. The current implementation validates/copies BGRA pixels, creates an
/// owned native child HWND when a real parent HWND is supplied, and can blit the
/// copied frame into that child surface. When D3D11 is available it uploads the
/// BGRA bytes into an ID3D11Texture2D, copies that texture into the child-HWND
/// swapchain backbuffer, and calls Present(); otherwise it falls back to a
/// top-down BGRA GDI DIB blit. It does not
/// add scrcpy, ADB display streaming, SDL reparenting, encoded video, or
/// proprietary renderer assets.
/// </summary>
public interface IRevmFramePresenter : IDisposable
{
    RevmPresenterCapabilities Capabilities { get; }
    RevmD3D11UploadState D3D11UploadState { get; }
    RevmD3D11SwapchainState D3D11SwapchainState { get; }
    RevmD3D11PresentResult LastD3D11Present { get; }
    RevmD3D11ProbeResult D3D11Probe { get; }
    RevmRendererStatus LastStatus { get; }
    RevmBgraUploadSnapshot LastUpload { get; }
    ulong LastPresentedFrameId { get; }
    int PresentCount { get; }
    RevmRendererStatus Resize(int width, int height);
    RevmRendererStatus Present(in RevmBgraFrame frame);
}

public readonly record struct RevmBgraUploadSnapshot(
    ulong FrameId,
    int Width,
    int Height,
    int Stride,
    int BytesCopied,
    uint ContentChecksum)
{
    public static RevmBgraUploadSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public readonly record struct RevmD3D11UploadState(
    bool DeviceCreated,
    bool UploadTextureCreated,
    int TextureWidth,
    int TextureHeight,
    int LastHResult)
{
    public static RevmD3D11UploadState Unavailable { get; } = new(false, false, 0, 0, 0);
}

public readonly record struct RevmD3D11SwapchainState(
    bool SwapchainCreated,
    int Width,
    int Height,
    int CreateHResult,
    bool TextureUploadAttempted,
    bool BackbufferCopyAttempted,
    int LastBackbufferHResult,
    bool PresentAttempted,
    int LastPresentHResult)
{
    public static RevmD3D11SwapchainState Unavailable { get; } = new(false, 0, 0, 0, false, false, 0, false, 0);
}

public readonly record struct RevmD3D11PresentResult(
    ulong FrameId,
    bool SwapchainAvailable,
    string Stage,
    bool SourceRectangleValid,
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight,
    bool TextureUploadAttempted,
    bool BackbufferCopyAttempted,
    int BackbufferHResult,
    bool PresentAttempted,
    int PresentHResult,
    int DeviceRemovedHResult,
    long PresentStartTimestamp,
    long PresentEndTimestamp,
    long PresentDurationTicks,
    long FrameIntervalTicks,
    RevmRendererStatus Status)
{
    public static RevmD3D11PresentResult NotAttempted { get; } = new(
        0,
        SwapchainAvailable: false,
        Stage: "not-attempted",
        SourceRectangleValid: false,
        SourceWidth: 0,
        SourceHeight: 0,
        TargetWidth: 0,
        TargetHeight: 0,
        TextureUploadAttempted: false,
        BackbufferCopyAttempted: false,
        BackbufferHResult: 0,
        PresentAttempted: false,
        PresentHResult: 0,
        DeviceRemovedHResult: 0,
        PresentStartTimestamp: 0,
        PresentEndTimestamp: 0,
        PresentDurationTicks: 0,
        FrameIntervalTicks: 0,
        RevmRendererStatus.InvalidState);
}

public readonly record struct RevmPresenterCapabilities(
    IntPtr ParentHwnd,
    IntPtr RenderHwnd,
    int TargetWidth,
    int TargetHeight,
    int PreferredFormat,
    bool OwnsNativeChildHwnd,
    bool SupportsChildHwndSwapchain,
    bool SupportsGpuUpload,
    string BackendName)
{
    public static RevmPresenterCapabilities CpuValidationOnly(IntPtr parentHwnd, int targetWidth, int targetHeight) =>
        new(
            parentHwnd,
            RenderHwnd: IntPtr.Zero,
            targetWidth,
            targetHeight,
            RevmRendererAbi.PreferredFormatBgra8888,
            OwnsNativeChildHwnd: false,
            SupportsChildHwndSwapchain: false,
            SupportsGpuUpload: false,
            BackendName: "bgra-cpu-validation-presenter-v1");

    public static RevmPresenterCapabilities NativeChildHwndGdi(IntPtr parentHwnd, IntPtr renderHwnd, int targetWidth, int targetHeight) =>
        new(
            parentHwnd,
            renderHwnd,
            targetWidth,
            targetHeight,
            RevmRendererAbi.PreferredFormatBgra8888,
            OwnsNativeChildHwnd: true,
            SupportsChildHwndSwapchain: false,
            SupportsGpuUpload: false,
            BackendName: "bgra-native-child-hwnd-gdi-blit-presenter-v1");

    public static RevmPresenterCapabilities NativeChildHwndD3D11UploadStub(IntPtr parentHwnd, IntPtr renderHwnd, int targetWidth, int targetHeight) =>
        new(
            parentHwnd,
            renderHwnd,
            targetWidth,
            targetHeight,
            RevmRendererAbi.PreferredFormatBgra8888,
            OwnsNativeChildHwnd: true,
            SupportsChildHwndSwapchain: false,
            SupportsGpuUpload: true,
            BackendName: "bgra-native-child-hwnd-d3d11-upload-stub-v1");

    public static RevmPresenterCapabilities NativeChildHwndD3D11UploadPresent(IntPtr parentHwnd, IntPtr renderHwnd, int targetWidth, int targetHeight) =>
        new(
            parentHwnd,
            renderHwnd,
            targetWidth,
            targetHeight,
            RevmRendererAbi.PreferredFormatBgra8888,
            OwnsNativeChildHwnd: true,
            SupportsChildHwndSwapchain: true,
            SupportsGpuUpload: true,
            BackendName: "bgra-native-child-hwnd-d3d11-upload-present-v1");
}

public sealed class RevmBgraFramePresenter : IRevmFramePresenter
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SS_BLACKRECT = 0x00000004;
    private const int BI_RGB = 0;
    private const int DIB_RGB_COLORS = 0;
    private const int SRCCOPY = 0x00CC0020;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;

    private bool _disposed;
    private byte[] _uploadBuffer = Array.Empty<byte>();
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private IntPtr _uploadTexture;
    private IntPtr _dxgiSwapchain;
    private long _lastD3D11PresentEndTimestamp;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern unsafe int StretchDIBits(
        IntPtr hdc,
        int xDest,
        int yDest,
        int destWidth,
        int destHeight,
        int xSrc,
        int ySrc,
        int srcWidth,
        int srcHeight,
        byte* bits,
        ref BitmapInfo bitmapInfo,
        int usage,
        int rop);

    [DllImport("d3d11.dll", SetLastError = false)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out uint pFeatureLevel,
        out IntPtr ppImmediateContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDelegate(IntPtr self, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetParentDelegate(IntPtr self, ref Guid riid, out IntPtr parent);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSwapChainDelegate(IntPtr self, IntPtr device, ref SwapChainDesc desc, out IntPtr swapchain);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr self, uint syncInterval, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr self, uint buffer, ref Guid riid, out IntPtr surface);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public SampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeDesc
    {
        public uint Width;
        public uint Height;
        public Rational RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDesc
    {
        public ModeDesc BufferDesc;
        public SampleDesc SampleDesc;
        public uint BufferUsage;
        public uint BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public uint SwapEffect;
        public uint Flags;
    }

    public RevmBgraFramePresenter()
        : this(IntPtr.Zero, 0, 0)
    {
    }

    public RevmBgraFramePresenter(RevmRendererConfig config)
        : this(config.ParentHwnd, config.Width, config.Height)
    {
    }

    public RevmBgraFramePresenter(IntPtr parentHwnd, int targetWidth, int targetHeight)
    {
        Capabilities = RevmPresenterCapabilities.CpuValidationOnly(parentHwnd, targetWidth, targetHeight);
        D3D11Probe = RevmD3D11CapabilityProbe.Probe();
        var renderHwnd = TryCreateChildHwnd(parentHwnd, targetWidth, targetHeight);
        if (renderHwnd == IntPtr.Zero)
            return;

        D3D11UploadState = TryCreateD3D11UploadResources(targetWidth, targetHeight, out var d3dStatus)
            ? d3dStatus
            : d3dStatus;
        if (D3D11UploadState.DeviceCreated && D3D11UploadState.UploadTextureCreated)
            TryCreateSwapchain(renderHwnd, targetWidth, targetHeight);

        Capabilities = D3D11SwapchainState.SwapchainCreated
            ? RevmPresenterCapabilities.NativeChildHwndD3D11UploadPresent(parentHwnd, renderHwnd, targetWidth, targetHeight)
            : D3D11UploadState.DeviceCreated && D3D11UploadState.UploadTextureCreated
                ? RevmPresenterCapabilities.NativeChildHwndD3D11UploadStub(parentHwnd, renderHwnd, targetWidth, targetHeight)
                : RevmPresenterCapabilities.NativeChildHwndGdi(parentHwnd, renderHwnd, targetWidth, targetHeight);
    }

    public RevmPresenterCapabilities Capabilities { get; private set; }
    public RevmD3D11UploadState D3D11UploadState { get; private set; } = RevmD3D11UploadState.Unavailable;
    public RevmD3D11SwapchainState D3D11SwapchainState { get; private set; } = RevmD3D11SwapchainState.Unavailable;
    public RevmD3D11PresentResult LastD3D11Present { get; private set; } = RevmD3D11PresentResult.NotAttempted;
    public RevmD3D11ProbeResult D3D11Probe { get; private set; } = RevmD3D11ProbeResult.NotAttempted;
    public RevmRendererStatus LastStatus { get; private set; } = RevmRendererStatus.InvalidState;
    public RevmBgraUploadSnapshot LastUpload { get; private set; } = RevmBgraUploadSnapshot.Empty;
    public ulong LastPresentedFrameId { get; private set; }
    public int PresentCount { get; private set; }

    public RevmRendererStatus Resize(int width, int height)
    {
        if (_disposed) return SetStatus(RevmRendererStatus.AlreadyDestroyed);
        if (width <= 0 || height <= 0) return SetStatus(RevmRendererStatus.InvalidSize);

        if (Capabilities.RenderHwnd != IntPtr.Zero)
            MoveWindow(Capabilities.RenderHwnd, 0, 0, width, height, true);
        if (_d3dDevice != IntPtr.Zero)
        {
            RecreateUploadTexture(width, height);
            if (Capabilities.RenderHwnd != IntPtr.Zero)
                TryCreateSwapchain(Capabilities.RenderHwnd, width, height);
        }
        Capabilities = Capabilities with { TargetWidth = width, TargetHeight = height };
        return SetStatus(RevmRendererStatus.Ok);
    }

    public RevmRendererStatus Present(in RevmBgraFrame frame)
    {
        if (_disposed) return SetStatus(RevmRendererStatus.AlreadyDestroyed);
        if (frame is null) return SetStatus(RevmRendererStatus.InvalidState);

        var info = frame.Info;
        if (info.FormatCode != RevmFrameRingConsumer.FormatBgra8888)
            return SetStatus(RevmRendererStatus.UnsupportedFormat);
        if (info.Width == 0 || info.Height == 0 || info.Stride < checked(info.Width * 4))
            return RejectInvalidD3D11Rectangle(info, "invalid-source-rectangle");
        if (Capabilities.TargetWidth > 0 && info.Width != Capabilities.TargetWidth)
            return RejectInvalidD3D11Rectangle(info, "source-target-width-mismatch");
        if (Capabilities.TargetHeight > 0 && info.Height != Capabilities.TargetHeight)
            return RejectInvalidD3D11Rectangle(info, "source-target-height-mismatch");
        if (info.DataSize != checked(info.Stride * info.Height))
            return RejectInvalidD3D11Rectangle(info, "source-byte-count-mismatch");
        if (frame.Pixels.Length < info.DataSize)
            return RejectInvalidD3D11Rectangle(info, "source-buffer-too-small");

        var dataSize = checked((int)(info.Stride * info.Height));
        if (_uploadBuffer.Length != dataSize)
            _uploadBuffer = new byte[dataSize];
        new ReadOnlySpan<byte>(frame.Pixels, 0, dataSize).CopyTo(new Span<byte>(_uploadBuffer, 0, dataSize));
        LastUpload = new RevmBgraUploadSnapshot(
            info.FrameId,
            checked((int)info.Width),
            checked((int)info.Height),
            checked((int)info.Stride),
            dataSize,
            ComputeDeterministicChecksum(new ReadOnlySpan<byte>(_uploadBuffer, 0, dataSize)));

        if (D3D11SwapchainState.SwapchainCreated)
        {
            var backbufferResult = TryUploadBgraToSwapchain(info, dataSize);
            var start = Stopwatch.GetTimestamp();
            var presentResult = TryPresentSwapchain();
            var end = Stopwatch.GetTimestamp();
            var previousEnd = _lastD3D11PresentEndTimestamp;
            _lastD3D11PresentEndTimestamp = end;
            LastD3D11Present = new RevmD3D11PresentResult(
                info.FrameId,
                SwapchainAvailable: true,
                Stage: backbufferResult && presentResult ? "presented" : backbufferResult ? "present-failed" : "backbuffer-copy-failed",
                SourceRectangleValid: true,
                SourceWidth: checked((int)info.Width),
                SourceHeight: checked((int)info.Height),
                TargetWidth: Capabilities.TargetWidth,
                TargetHeight: Capabilities.TargetHeight,
                TextureUploadAttempted: D3D11SwapchainState.TextureUploadAttempted,
                BackbufferCopyAttempted: backbufferResult,
                BackbufferHResult: D3D11SwapchainState.LastBackbufferHResult,
                PresentAttempted: D3D11SwapchainState.PresentAttempted,
                PresentHResult: D3D11SwapchainState.LastPresentHResult,
                DeviceRemovedHResult: GetDeviceRemovedReason(),
                PresentStartTimestamp: start,
                PresentEndTimestamp: end,
                PresentDurationTicks: end - start,
                FrameIntervalTicks: previousEnd == 0 ? 0 : start - previousEnd,
                backbufferResult && presentResult ? RevmRendererStatus.Ok : RevmRendererStatus.PlatformCreateFailed);
        }
        else
        {
            LastD3D11Present = RevmD3D11PresentResult.NotAttempted;
            if (Capabilities.RenderHwnd != IntPtr.Zero && !TryBlitBgraToChildHwnd(info, dataSize))
                return SetStatus(RevmRendererStatus.PlatformCreateFailed);
        }

        if (Capabilities.RenderHwnd != IntPtr.Zero)
            InvalidateRect(Capabilities.RenderHwnd, IntPtr.Zero, false);

        LastPresentedFrameId = info.FrameId;
        PresentCount++;
        return SetStatus(RevmRendererStatus.Ok);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Capabilities.RenderHwnd != IntPtr.Zero && Capabilities.OwnsNativeChildHwnd)
            DestroyWindow(Capabilities.RenderHwnd);
        ReleaseComObject(ref _dxgiSwapchain);
        ReleaseComObject(ref _uploadTexture);
        ReleaseComObject(ref _d3dContext);
        ReleaseComObject(ref _d3dDevice);
        D3D11SwapchainState = RevmD3D11SwapchainState.Unavailable;
        D3D11UploadState = RevmD3D11UploadState.Unavailable;
        LastD3D11Present = RevmD3D11PresentResult.NotAttempted;
        LastD3D11Present = LastD3D11Present with { Status = RevmRendererStatus.AlreadyDestroyed };
        _uploadBuffer = Array.Empty<byte>();
        _disposed = true;
        LastStatus = RevmRendererStatus.AlreadyDestroyed;
    }

    private static IntPtr TryCreateChildHwnd(IntPtr parentHwnd, int targetWidth, int targetHeight)
    {
        if (parentHwnd == IntPtr.Zero || targetWidth <= 0 || targetHeight <= 0)
            return IntPtr.Zero;

        try
        {
            return CreateWindowExW(
                0,
                "STATIC",
                "REPlayerNativeRenderSurface",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | SS_BLACKRECT,
                0,
                0,
                targetWidth,
                targetHeight,
                parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    private unsafe bool TryBlitBgraToChildHwnd(RevmBgraFrameInfo info, int dataSize)
    {
        var hdc = IntPtr.Zero;
        try
        {
            hdc = GetDC(Capabilities.RenderHwnd);
            if (hdc == IntPtr.Zero) return false;

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = checked((int)info.Width),
                    Height = -checked((int)info.Height),
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB,
                    SizeImage = checked((uint)dataSize)
                }
            };

            fixed (byte* bits = _uploadBuffer)
            {
                var lines = StretchDIBits(
                    hdc,
                    0,
                    0,
                    Capabilities.TargetWidth,
                    Capabilities.TargetHeight,
                    0,
                    0,
                    checked((int)info.Width),
                    checked((int)info.Height),
                    bits,
                    ref bitmapInfo,
                    DIB_RGB_COLORS,
                    SRCCOPY);
                return lines != 0;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero)
                ReleaseDC(Capabilities.RenderHwnd, hdc);
        }
    }

    private bool TryCreateD3D11UploadResources(int width, int height, out RevmD3D11UploadState state)
    {
        state = RevmD3D11UploadState.Unavailable;
        try
        {
            // 1 == D3D_DRIVER_TYPE_HARDWARE. The BGRA flag is required for the
            // later child-HWND swapchain path; this run only proves deterministic
            // device + BGRA texture allocation and keeps swapchain support false.
            var hr = D3D11CreateDevice(
                IntPtr.Zero,
                1,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out _d3dDevice,
                out _,
                out _d3dContext);
            if (hr < 0 || _d3dDevice == IntPtr.Zero)
            {
                D3D11UploadState = new RevmD3D11UploadState(false, false, 0, 0, hr);
                state = D3D11UploadState;
                return false;
            }

            var createdTexture = RecreateUploadTexture(width, height);
            state = D3D11UploadState;
            return createdTexture;
        }
        catch (DllNotFoundException)
        {
            D3D11UploadState = RevmD3D11UploadState.Unavailable;
            state = D3D11UploadState;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            D3D11UploadState = RevmD3D11UploadState.Unavailable;
            state = D3D11UploadState;
            return false;
        }
    }

    private unsafe bool RecreateUploadTexture(int width, int height)
    {
        ReleaseComObject(ref _uploadTexture);
        if (_d3dDevice == IntPtr.Zero || width <= 0 || height <= 0)
        {
            D3D11UploadState = new RevmD3D11UploadState(_d3dDevice != IntPtr.Zero, false, 0, 0, 0);
            return false;
        }

        var desc = new Texture2DDesc
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = 0,
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
            CpuAccessFlags = 0,
            MiscFlags = 0
        };

        var vtable = *(IntPtr**)_d3dDevice;
        var createTexture2D = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)vtable[5];
        IntPtr texture = IntPtr.Zero;
        var hr = createTexture2D(_d3dDevice, &desc, IntPtr.Zero, &texture);
        _uploadTexture = texture;
        D3D11UploadState = new RevmD3D11UploadState(
            DeviceCreated: true,
            UploadTextureCreated: hr >= 0 && _uploadTexture != IntPtr.Zero,
            TextureWidth: hr >= 0 && _uploadTexture != IntPtr.Zero ? width : 0,
            TextureHeight: hr >= 0 && _uploadTexture != IntPtr.Zero ? height : 0,
            LastHResult: hr);
        return D3D11UploadState.UploadTextureCreated;
    }

    private bool TryCreateSwapchain(IntPtr renderHwnd, int width, int height)
    {
        ReleaseComObject(ref _dxgiSwapchain);
        if (_d3dDevice == IntPtr.Zero || renderHwnd == IntPtr.Zero || width <= 0 || height <= 0)
        {
            D3D11SwapchainState = RevmD3D11SwapchainState.Unavailable;
            return false;
        }

        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr swapchain = IntPtr.Zero;
        var hr = 0;
        try
        {
            var idxgiDeviceGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            var idxgiFactoryGuid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");

            hr = Marshal.QueryInterface(_d3dDevice, in idxgiDeviceGuid, out dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero)
                return SetSwapchainCreateFailed(width, height, hr);

            var getAdapter = GetComDelegate<GetAdapterDelegate>(dxgiDevice, 7);
            hr = getAdapter(dxgiDevice, out adapter);
            if (hr < 0 || adapter == IntPtr.Zero)
                return SetSwapchainCreateFailed(width, height, hr);

            var getParent = GetComDelegate<GetParentDelegate>(adapter, 6);
            hr = getParent(adapter, ref idxgiFactoryGuid, out factory);
            if (hr < 0 || factory == IntPtr.Zero)
                return SetSwapchainCreateFailed(width, height, hr);

            var desc = new SwapChainDesc
            {
                BufferDesc = new ModeDesc
                {
                    Width = checked((uint)width),
                    Height = checked((uint)height),
                    RefreshRate = new Rational { Numerator = 60, Denominator = 1 },
                    Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                    ScanlineOrdering = 0,
                    Scaling = 0
                },
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 1,
                OutputWindow = renderHwnd,
                Windowed = 1,
                SwapEffect = 0,
                Flags = 0
            };

            var createSwapChain = GetComDelegate<CreateSwapChainDelegate>(factory, 10);
            hr = createSwapChain(factory, _d3dDevice, ref desc, out swapchain);
            if (hr < 0 || swapchain == IntPtr.Zero)
                return SetSwapchainCreateFailed(width, height, hr);

            _dxgiSwapchain = swapchain;
            swapchain = IntPtr.Zero;
            D3D11SwapchainState = new RevmD3D11SwapchainState(true, width, height, hr, false, false, 0, false, 0);
            return true;
        }
        finally
        {
            ReleaseComObject(ref swapchain);
            ReleaseComObject(ref factory);
            ReleaseComObject(ref adapter);
            ReleaseComObject(ref dxgiDevice);
        }
    }

    private bool TryPresentSwapchain()
    {
        if (_dxgiSwapchain == IntPtr.Zero || !D3D11SwapchainState.SwapchainCreated)
            return false;
        var present = GetComDelegate<PresentDelegate>(_dxgiSwapchain, 8);
        var hr = present(_dxgiSwapchain, 0, 0);
        D3D11SwapchainState = D3D11SwapchainState with
        {
            PresentAttempted = true,
            LastPresentHResult = hr
        };
        return hr >= 0;
    }

    private unsafe bool TryUploadBgraToSwapchain(RevmBgraFrameInfo info, int dataSize)
    {
        if (_d3dContext == IntPtr.Zero || _uploadTexture == IntPtr.Zero || _dxgiSwapchain == IntPtr.Zero)
            return false;

        IntPtr backbuffer = IntPtr.Zero;
        var hr = 0;
        try
        {
            fixed (byte* source = _uploadBuffer)
            {
                var contextVtable = *(IntPtr**)_d3dContext;
                var updateSubresource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)contextVtable[48];
                updateSubresource(
                    _d3dContext,
                    _uploadTexture,
                    0,
                    IntPtr.Zero,
                    source,
                    checked((uint)info.Stride),
                    checked((uint)dataSize));
            }

            var texture2DGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            var getBuffer = GetComDelegate<GetBufferDelegate>(_dxgiSwapchain, 9);
            hr = getBuffer(_dxgiSwapchain, 0, ref texture2DGuid, out backbuffer);
            if (hr >= 0 && backbuffer != IntPtr.Zero)
            {
                var contextVtable = *(IntPtr**)_d3dContext;
                var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)contextVtable[47];
                copyResource(_d3dContext, backbuffer, _uploadTexture);
            }

            D3D11SwapchainState = D3D11SwapchainState with
            {
                TextureUploadAttempted = true,
                BackbufferCopyAttempted = hr >= 0 && backbuffer != IntPtr.Zero,
                LastBackbufferHResult = hr
            };
            return D3D11SwapchainState.BackbufferCopyAttempted;
        }
        finally
        {
            ReleaseComObject(ref backbuffer);
        }
    }

    private bool SetSwapchainCreateFailed(int width, int height, int hr)
    {
        D3D11SwapchainState = new RevmD3D11SwapchainState(false, width, height, hr, false, false, 0, false, 0);
        return false;
    }

    private RevmRendererStatus RejectInvalidD3D11Rectangle(RevmBgraFrameInfo info, string stage)
    {
        LastD3D11Present = new RevmD3D11PresentResult(
            info.FrameId,
            SwapchainAvailable: D3D11SwapchainState.SwapchainCreated,
            Stage: stage,
            SourceRectangleValid: false,
            SourceWidth: checked((int)info.Width),
            SourceHeight: checked((int)info.Height),
            TargetWidth: Capabilities.TargetWidth,
            TargetHeight: Capabilities.TargetHeight,
            TextureUploadAttempted: false,
            BackbufferCopyAttempted: false,
            BackbufferHResult: 0,
            PresentAttempted: false,
            PresentHResult: 0,
            DeviceRemovedHResult: GetDeviceRemovedReason(),
            PresentStartTimestamp: 0,
            PresentEndTimestamp: 0,
            PresentDurationTicks: 0,
            FrameIntervalTicks: 0,
            RevmRendererStatus.InvalidSize);
        return SetStatus(RevmRendererStatus.InvalidSize);
    }

    private unsafe int GetDeviceRemovedReason()
    {
        if (_d3dDevice == IntPtr.Zero)
            return 0;
        var vtable = *(IntPtr**)_d3dDevice;
        var getDeviceRemovedReason = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[39];
        return getDeviceRemovedReason(_d3dDevice);
    }

    private static TDelegate GetComDelegate<TDelegate>(IntPtr comObject, int vtableIndex)
        where TDelegate : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, vtableIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(function);
    }

    private static unsafe void ReleaseComObject(ref IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        var target = comObject;
        comObject = IntPtr.Zero;
        var vtable = *(IntPtr**)target;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
        release(target);
    }

    private static uint ComputeDeterministicChecksum(ReadOnlySpan<byte> bytes)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        var hash = offsetBasis;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private RevmRendererStatus SetStatus(RevmRendererStatus status)
    {
        LastStatus = status;
        return status;
    }
}
