using System.Runtime.InteropServices;

namespace RevmRenderHost;

/// <summary>
/// Minimal Direct3D 11 device capability probe. This is intentionally only the
/// first D3D layer: it proves whether this process can create a D3D11 device
/// with BGRA support before the renderer grows a DXGI swapchain, texture upload,
/// and GPU present path.
/// </summary>
public static unsafe class RevmD3D11CapabilityProbe
{
    private const int D3D11SdkVersion = 7;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;

    private static readonly int[] FeatureLevels =
    [
        0xB000, // D3D_FEATURE_LEVEL_11_0
        0xA100, // D3D_FEATURE_LEVEL_10_1
        0xA000, // D3D_FEATURE_LEVEL_10_0
        0x9300, // D3D_FEATURE_LEVEL_9_3
        0x9200, // D3D_FEATURE_LEVEL_9_2
        0x9100  // D3D_FEATURE_LEVEL_9_1
    ];

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int* featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        IntPtr* device,
        int* featureLevel,
        IntPtr* immediateContext);

    public static RevmD3D11ProbeResult Probe()
    {
        try
        {
            var hardware = TryCreateDevice(D3DDriverTypeHardware, "hardware");
            if (hardware.Status == RevmD3D11ProbeStatus.Available) return hardware;

            var warp = TryCreateDevice(D3DDriverTypeWarp, "warp");
            if (warp.Status == RevmD3D11ProbeStatus.Available) return warp;

            return hardware with
            {
                FallbackDriverType = warp.AttemptedDriverType,
                FallbackHResult = warp.HResult,
                FailureDetail = $"hardware={hardware.HResultHex}; warp={warp.HResultHex}"
            };
        }
        catch (DllNotFoundException ex)
        {
            return RevmD3D11ProbeResult.Unavailable(RevmD3D11ProbeStatus.DllMissing, "d3d11.dll", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return RevmD3D11ProbeResult.Unavailable(RevmD3D11ProbeStatus.EntryPointMissing, "D3D11CreateDevice", ex.Message);
        }
        catch (PlatformNotSupportedException ex)
        {
            return RevmD3D11ProbeResult.Unavailable(RevmD3D11ProbeStatus.UnsupportedPlatform, Environment.OSVersion.Platform.ToString(), ex.Message);
        }
    }

    private static RevmD3D11ProbeResult TryCreateDevice(int driverType, string driverTypeName)
    {
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        var selectedFeatureLevel = 0;

        fixed (int* levels = FeatureLevels)
        {
            var hr = D3D11CreateDevice(
                IntPtr.Zero,
                driverType,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                levels,
                (uint)FeatureLevels.Length,
                D3D11SdkVersion,
                &device,
                &selectedFeatureLevel,
                &context);

            try
            {
                if (hr < 0)
                {
                    return new RevmD3D11ProbeResult(
                        RevmD3D11ProbeStatus.CreateDeviceFailed,
                        driverTypeName,
                        HResult: hr,
                        FeatureLevel: string.Empty,
                        BgraSupportRequested: true,
                        FallbackDriverType: string.Empty,
                        FallbackHResult: 0,
                        FailureDetail: "D3D11CreateDevice failed");
                }

                return new RevmD3D11ProbeResult(
                    RevmD3D11ProbeStatus.Available,
                    driverTypeName,
                    HResult: hr,
                    FeatureLevel: FormatFeatureLevel(selectedFeatureLevel),
                    BgraSupportRequested: true,
                    FallbackDriverType: string.Empty,
                    FallbackHResult: 0,
                    FailureDetail: string.Empty);
            }
            finally
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }
    }

    private static string FormatFeatureLevel(int featureLevel) => featureLevel switch
    {
        0xB100 => "11_1",
        0xB000 => "11_0",
        0xA100 => "10_1",
        0xA000 => "10_0",
        0x9300 => "9_3",
        0x9200 => "9_2",
        0x9100 => "9_1",
        _ => $"0x{featureLevel:X}"
    };
}

public enum RevmD3D11ProbeStatus
{
    NotAttempted = 0,
    Available = 1,
    DllMissing = 2,
    EntryPointMissing = 3,
    CreateDeviceFailed = 4,
    UnsupportedPlatform = 5
}

public readonly record struct RevmD3D11ProbeResult(
    RevmD3D11ProbeStatus Status,
    string AttemptedDriverType,
    int HResult,
    string FeatureLevel,
    bool BgraSupportRequested,
    string FallbackDriverType,
    int FallbackHResult,
    string FailureDetail)
{
    public bool DeviceCreated => Status == RevmD3D11ProbeStatus.Available;
    public string HResultHex => $"0x{unchecked((uint)HResult):X8}";
    public string FallbackHResultHex => $"0x{unchecked((uint)FallbackHResult):X8}";

    public static RevmD3D11ProbeResult NotAttempted { get; } = new(
        RevmD3D11ProbeStatus.NotAttempted,
        string.Empty,
        0,
        string.Empty,
        BgraSupportRequested: false,
        FallbackDriverType: string.Empty,
        FallbackHResult: 0,
        FailureDetail: string.Empty);

    public static RevmD3D11ProbeResult Unavailable(RevmD3D11ProbeStatus status, string attempted, string detail) => new(
        status,
        attempted,
        HResult: 0,
        FeatureLevel: string.Empty,
        BgraSupportRequested: false,
        FallbackDriverType: string.Empty,
        FallbackHResult: 0,
        FailureDetail: detail);
}
