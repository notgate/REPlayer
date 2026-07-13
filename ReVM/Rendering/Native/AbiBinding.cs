using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ReVM.NativeRendering;

/// <summary>
/// Optional binding to the native/AOT revm-render-host C ABI. If the configured
/// renderer artifact is still the managed scaffold or lacks exports, callers keep
/// using the deterministic WPF-side lifecycle mirror.
/// </summary>
internal sealed class NativeRenderHostAbiBinding : IDisposable
{
    private const string CreateExport = "revm_renderer_create";
    private const string AttachTransportExport = "revm_renderer_attach_transport";
    private const string ResizeExport = "revm_renderer_resize";
    private const string SetRotationExport = "revm_renderer_set_rotation";
    private const string PresentLatestFrameExport = "revm_renderer_present_latest_frame";
    private const string DestroyExport = "revm_renderer_destroy";

    private readonly IntPtr _libraryHandle;
    private readonly CreateDelegate _create;
    private readonly AttachTransportDelegate _attachTransport;
    private readonly ResizeDelegate _resize;
    private readonly SetRotationDelegate _setRotation;
    private readonly PresentLatestFrameDelegate _presentLatestFrame;
    private readonly DestroyDelegate _destroy;
    private IntPtr _rendererHandle;
    private bool _disposed;

    private NativeRenderHostAbiBinding(
        IntPtr libraryHandle,
        CreateDelegate create,
        AttachTransportDelegate attachTransport,
        ResizeDelegate resize,
        SetRotationDelegate setRotation,
        PresentLatestFrameDelegate presentLatestFrame,
        DestroyDelegate destroy)
    {
        _libraryHandle = libraryHandle;
        _create = create;
        _attachTransport = attachTransport;
        _resize = resize;
        _setRotation = setRotation;
        _presentLatestFrame = presentLatestFrame;
        _destroy = destroy;
    }

    public bool HasRendererHandle => _rendererHandle != IntPtr.Zero;

    public static bool TryLoad(string rendererPath, out NativeRenderHostAbiBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(rendererPath) || !File.Exists(rendererPath)) return false;
        if (!NativeLibrary.TryLoad(rendererPath, out var libraryHandle)) return false;

        try
        {
            if (!TryGetDelegate(libraryHandle, CreateExport, out CreateDelegate? create) ||
                !TryGetDelegate(libraryHandle, AttachTransportExport, out AttachTransportDelegate? attachTransport) ||
                !TryGetDelegate(libraryHandle, ResizeExport, out ResizeDelegate? resize) ||
                !TryGetDelegate(libraryHandle, SetRotationExport, out SetRotationDelegate? setRotation) ||
                !TryGetDelegate(libraryHandle, PresentLatestFrameExport, out PresentLatestFrameDelegate? presentLatestFrame) ||
                !TryGetDelegate(libraryHandle, DestroyExport, out DestroyDelegate? destroy))
            {
                NativeLibrary.Free(libraryHandle);
                return false;
            }

            binding = new NativeRenderHostAbiBinding(libraryHandle, create!, attachTransport!, resize!, setRotation!, presentLatestFrame!, destroy!);
            return true;
        }
        catch
        {
            NativeLibrary.Free(libraryHandle);
            binding = null;
            return false;
        }
    }

    public RenderHostControllerStatus Create(IntPtr parentHwnd, int width, int height, int preferredFormat)
    {
        ThrowIfDisposed();
        if (_rendererHandle != IntPtr.Zero) return RenderHostControllerStatus.Ok;

        var config = new NativeRendererConfig(parentHwnd, width, height, preferredFormat);
        var configPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRendererConfig>());
        try
        {
            Marshal.StructureToPtr(config, configPtr, false);
            var status = MapStatus(_create(configPtr, out _rendererHandle));
            if (status != RenderHostControllerStatus.Ok) _rendererHandle = IntPtr.Zero;
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(configPtr);
        }
    }

    public RenderHostControllerStatus AttachTransport(string endpointJson)
    {
        ThrowIfDisposed();
        if (_rendererHandle == IntPtr.Zero) return RenderHostControllerStatus.InvalidState;

        var endpointBytes = Encoding.UTF8.GetBytes(endpointJson + "\0");
        var endpointPtr = Marshal.AllocHGlobal(endpointBytes.Length);
        try
        {
            Marshal.Copy(endpointBytes, 0, endpointPtr, endpointBytes.Length);
            return MapStatus(_attachTransport(_rendererHandle, endpointPtr));
        }
        finally
        {
            Marshal.FreeHGlobal(endpointPtr);
        }
    }

    public RenderHostControllerStatus Resize(int width, int height)
    {
        ThrowIfDisposed();
        return _rendererHandle == IntPtr.Zero
            ? RenderHostControllerStatus.InvalidState
            : MapStatus(_resize(_rendererHandle, width, height));
    }

    public RenderHostControllerStatus SetRotation(int rotation)
    {
        ThrowIfDisposed();
        return _rendererHandle == IntPtr.Zero
            ? RenderHostControllerStatus.InvalidState
            : MapStatus(_setRotation(_rendererHandle, rotation));
    }

    public RenderHostControllerStatus PresentLatestFrame()
    {
        ThrowIfDisposed();
        return _rendererHandle == IntPtr.Zero
            ? RenderHostControllerStatus.InvalidState
            : MapStatus(_presentLatestFrame(_rendererHandle));
    }

    public RenderHostControllerStatus DestroyRenderer()
    {
        if (_rendererHandle == IntPtr.Zero) return RenderHostControllerStatus.Ok;
        var handle = _rendererHandle;
        _rendererHandle = IntPtr.Zero;
        return MapStatus(_destroy(handle));
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { DestroyRenderer(); } catch { }
        NativeLibrary.Free(_libraryHandle);
        _disposed = true;
    }

    private static bool TryGetDelegate<TDelegate>(IntPtr libraryHandle, string exportName, out TDelegate? del)
        where TDelegate : Delegate
    {
        del = null;
        if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out var exportPtr)) return false;
        del = Marshal.GetDelegateForFunctionPointer<TDelegate>(exportPtr);
        return true;
    }

    private static RenderHostControllerStatus MapStatus(int status)
        => Enum.IsDefined(typeof(RenderHostControllerStatus), status)
            ? (RenderHostControllerStatus)status
            : RenderHostControllerStatus.InvalidState;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeRenderHostAbiBinding));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRendererConfig
    {
        public NativeRendererConfig(IntPtr parentHwnd, int width, int height, int preferredFormat)
        {
            AbiVersion = 1;
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateDelegate(IntPtr config, out IntPtr outHandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AttachTransportDelegate(IntPtr handle, IntPtr endpointJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ResizeDelegate(IntPtr handle, int width, int height);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetRotationDelegate(IntPtr handle, int rotation);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PresentLatestFrameDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyDelegate(IntPtr handle);
}
