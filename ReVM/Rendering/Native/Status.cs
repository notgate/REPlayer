namespace ReVM.NativeRendering;

/// <summary>
/// Deterministic renderer-controller result codes mirrored at the WPF boundary.
/// These values are intentionally display-transport agnostic: they describe the
/// REPlayer-owned native renderer lifecycle only, not ADB, scrcpy, SDL, or video.
/// </summary>
public enum RenderHostControllerStatus
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
    InvalidFrameRing = 11,
    Disposed = 12
}
