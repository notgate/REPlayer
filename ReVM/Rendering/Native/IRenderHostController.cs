using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReVM.NativeRendering;

/// <summary>
/// Owns the REPlayer native renderer surface embedded below NativeChildHost.
/// Display transport must be a REPlayer-owned native path such as gfxstream or
/// shm-frame-ring; ADB/scrcpy/SDL/video streams are intentionally not modeled here.
/// </summary>
public interface IRenderHostController : IDisposable
{
    IntPtr ChildHwnd { get; }
    string TransportEndpoint { get; }
    RenderHostControllerStatus LastStatus { get; }

    Task StartAsync(IntPtr parentHwnd, int width, int height, CancellationToken ct);
    Task AttachTransportAsync(string endpoint, CancellationToken ct);
    RenderHostControllerStatus PresentLatestFrame();
    void Resize(int width, int height);
    void SetRotation(int rotation);
    Task StopAsync();
}
