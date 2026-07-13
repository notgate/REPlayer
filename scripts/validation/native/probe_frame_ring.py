#!/usr/bin/env python3
"""Probe revm-render-host's renderer-side shm-frame-ring-v1 consumer.

This builds a temporary .NET console app outside the repo, references
runtime-src/RevmRenderHost, creates a deterministic BGRA frame ring, attaches it
through RenderHostSession, and verifies the renderer shim sees a ready frame.

This is renderer transport validation only: no scrcpy, no ADB display, no SDL
reparenting, and no encoded video path.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-render-host-frame-ring-probe-tmp")


def win_path(path: Path) -> str:
    try:
        return subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()
    except (FileNotFoundError, subprocess.CalledProcessError):
        resolved = path.resolve()
        if str(resolved).startswith("/mnt/"):
            drive = str(resolved)[5]
            rest = str(resolved)[7:].replace("/", "\\")
            return f"{drive.upper()}:\\{rest}"
        raise


def run(cmd: list[str], cwd: Path | None = None) -> None:
    print("+", " ".join(cmd))
    subprocess.run(cmd, cwd=str(cwd) if cwd else None, check=True)


def write_probe_project(work: Path) -> None:
    render_host_project = ROOT / "runtime-src" / "RevmRenderHost" / "RevmRenderHost.csproj"
    (work / "RenderHostFrameRingProbe.csproj").write_text(
        f"""<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=\"{win_path(render_host_project)}\" />
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )

    (work / "Program.cs").write_text(
        r'''using System.Runtime.InteropServices;
using System.Text.Json;
using RevmRenderHost;

const uint FrameMagic = 0x4D465652u;
const uint AbiVersion = 1;
const uint FormatBgra8888 = 1;
const int HeaderSize = 48;
const int SlotSize = 28;
const int WS_POPUP = unchecked((int)0x80000000);
const int WS_VISIBLE = 0x10000000;

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string? lpWindowName,
    int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
    IntPtr hInstance, IntPtr lpParam);

[DllImport("user32.dll", SetLastError = true)]
static extern bool DestroyWindow(IntPtr hWnd);

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"ok {message}");
}

static uint Checksum(ReadOnlySpan<byte> bytes)
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

static string WriteRing(string dir, string vmId, int width, int height, int slots, int frames)
{
    var stride = width * 4;
    var frameBytes = stride * height;
    var firstDataOffset = HeaderSize + SlotSize * slots;
    var readyIndex = frames - 1;
    var path = Path.Combine(dir, vmId + ".frame-ring.bin");

    using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
    using var writer = new BinaryWriter(stream);

    writer.Write(FrameMagic);
    writer.Write(AbiVersion);
    writer.Write((uint)width);
    writer.Write((uint)height);
    writer.Write((uint)stride);
    writer.Write(FormatBgra8888);
    writer.Write((ulong)slots);
    writer.Write(0UL);
    writer.Write((uint)readyIndex);
    writer.Write(0U);

    for (var slot = 0; slot < slots; slot++)
    {
        var ready = slot < frames;
        writer.Write((uint)(ready ? 2 : 0));
        writer.Write((ulong)(ready ? slot + 1 : 0));
        writer.Write(0UL);
        writer.Write((uint)(firstDataOffset + slot * frameBytes));
        writer.Write((uint)(ready ? frameBytes : 0));
    }

    for (var slot = 0; slot < slots; slot++)
    {
        var frameId = slot + 1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                writer.Write((byte)((x + frameId * 17) & 0xFF));
                writer.Write((byte)((y + frameId * 29) & 0xFF));
                writer.Write((byte)(((x ^ y) + frameId * 43) & 0xFF));
                writer.Write((byte)0xFF);
            }
        }
    }

    return path;
}

static string Endpoint(string vmId, string ringPath, int width = 4, int height = 3, int slots = 3) => JsonSerializer.Serialize(new
{
    kind = "shm-frame-ring-v1",
    abiVersion = 1,
    vmId,
    mappingName = "Local\\RevmFrameRing-" + vmId,
    readyEventName = "Local\\RevmFrameReady-" + vmId,
    controlPipeName = "\\\\.\\pipe\\revm-control-" + vmId,
    width,
    height,
    stride = width * 4,
    format = "BGRA8888",
    formatCode = 1,
    slots,
    ringImagePath = ringPath
});

var work = Path.Combine(Path.GetTempPath(), "revm-frame-ring-consumer-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    var vmId = "frame-ring-probe";
    var width = 4;
    var height = 3;
    var slots = 3;
    var frameBytes = width * height * 4;
    var ring = WriteRing(work, vmId, width, height, slots, frames: 3);
    var endpoint = Endpoint(vmId, ring, width, height, slots);

    Require(RevmRendererAbi.ValidateTransportEndpoint(endpoint) == RevmRendererStatus.Ok, "endpoint validates through ABI helper");

    var openStatus = RevmFrameRingConsumer.TryOpen(endpoint, out var consumer);
    Require(openStatus == RevmRendererStatus.Ok, "consumer opens valid frame ring");
    using (consumer)
    {
        Require(consumer is not null, "consumer instance is non-null");
        Require(consumer!.Header.Magic == FrameMagic, "header magic RVFM accepted");
        Require(consumer.Header.AbiVersion == AbiVersion, "header ABI v1 accepted");
        Require(consumer.Header.Width == width && consumer.Header.Height == height, "header dimensions match endpoint");
        Require(consumer.Header.Stride == width * 4, "header stride matches endpoint");
        Require(consumer.Slots.Count == slots, "slot table count accepted");
        Require(consumer.HasReadyFrame, "consumer sees at least one ready frame");
        Require(consumer.LatestReadySlot is not null, "latest ready slot is non-null");
        var latest = consumer.LatestReadySlot!.Value;
        Require(latest.Index == 2, "latest ready slot is highest ready frame");
        Require(latest.DataSize == frameBytes, "ready slot data size equals stride*height");
        Require(latest.DataOffset >= HeaderSize + SlotSize * slots, "ready slot data offset is after slot table");
        var source = new RevmFrameRingFrameSource(consumer);
        Require(source.TryReadLatestFrame(out var copiedFrame) == RevmRendererStatus.Ok, "frame source copies latest BGRA frame");
        Require(copiedFrame is not null, "copied frame is non-null");
        Require(copiedFrame!.Info.FrameId == 3 && copiedFrame.Info.SlotIndex == 2, "copied frame metadata identifies latest slot");
        Require(copiedFrame.Info.Width == width && copiedFrame.Info.Height == height, "copied frame metadata carries dimensions");
        Require(copiedFrame.Pixels.Length == frameBytes, "copied frame byte count equals stride*height");
        Require(copiedFrame.Pixels[0] == 51 && copiedFrame.Pixels[1] == 87 && copiedFrame.Pixels[2] == 129 && copiedFrame.Pixels[3] == 255, "copied first pixel matches latest frame pattern");
        using var presenter = new RevmBgraFramePresenter(new RevmRendererConfig(new IntPtr(0x1234), width, height));
        Require(presenter.Capabilities.ParentHwnd == new IntPtr(0x1234), "presenter capabilities retain target parent HWND");
        Require(presenter.Capabilities.TargetWidth == width && presenter.Capabilities.TargetHeight == height, "presenter capabilities retain target dimensions");
        Require(presenter.Capabilities.PreferredFormat == RevmRendererAbi.PreferredFormatBgra8888, "presenter capabilities report BGRA8888 format");
        Require(!presenter.Capabilities.SupportsChildHwndSwapchain, "presenter capabilities declare swapchain not yet wired");
        Require(!presenter.Capabilities.SupportsGpuUpload, "presenter capabilities declare GPU upload not yet wired");
        Require(presenter.Capabilities.BackendName == "bgra-cpu-validation-presenter-v1", "presenter capabilities name deterministic validation backend");
        Require(presenter.D3D11Probe.Status != RevmD3D11ProbeStatus.NotAttempted, "presenter runs D3D11 device capability probe");
        Require(presenter.D3D11Probe.BgraSupportRequested || presenter.D3D11Probe.Status != RevmD3D11ProbeStatus.Available, "D3D11 probe requests BGRA support when available");
        if (presenter.D3D11Probe.DeviceCreated)
        {
            Require(!string.IsNullOrWhiteSpace(presenter.D3D11Probe.FeatureLevel), "D3D11 probe records selected feature level");
            Require(presenter.D3D11Probe.HResult == 0, "D3D11 probe reports successful HRESULT");
        }
        Require(presenter.Present(copiedFrame) == RevmRendererStatus.Ok, "presenter accepts copied BGRA frame");
        Require(!presenter.LastD3D11Present.SwapchainAvailable, "validation presenter reports no D3D11 present attempt");
        Require(presenter.LastD3D11Present.Status == RevmRendererStatus.InvalidState, "validation presenter keeps deterministic D3D11 not-attempted status");
        Require(presenter.LastPresentedFrameId == 3, "presenter records latest frame id");
        Require(presenter.LastUpload.FrameId == 3, "presenter records upload frame id");
        Require(presenter.LastUpload.Width == width && presenter.LastUpload.Height == height, "presenter records uploaded dimensions");
        Require(presenter.LastUpload.Stride == width * 4, "presenter records uploaded stride");
        Require(presenter.LastUpload.BytesCopied == frameBytes, "presenter records uploaded byte count");
        Require(presenter.LastUpload.ContentChecksum == Checksum(copiedFrame.Pixels), "presenter records deterministic upload checksum");
        Require(presenter.PresentCount == 1, "presenter increments present count");
        Require(presenter.Resize(width + 1, height) == RevmRendererStatus.Ok, "presenter resizes target surface contract");
        Require(presenter.Capabilities.TargetWidth == width + 1 && presenter.Capabilities.TargetHeight == height, "presenter capabilities update after resize");
        Require(presenter.Present(copiedFrame) == RevmRendererStatus.InvalidSize, "presenter rejects dimensions outside target surface");
        Require(presenter.Resize(width, height) == RevmRendererStatus.Ok, "presenter resizes target surface contract back to frame dimensions");
        Require(presenter.Present(copiedFrame) == RevmRendererStatus.Ok, "presenter accepts resized target dimensions");
        var badFormatFrame = copiedFrame with { Info = copiedFrame.Info with { FormatCode = 99 } };
        Require(presenter.Present(badFormatFrame) == RevmRendererStatus.UnsupportedFormat, "presenter rejects unsupported format");
        var badSizeFrame = copiedFrame with { Pixels = copiedFrame.Pixels[..^1] };
        Require(presenter.Present(badSizeFrame) == RevmRendererStatus.InvalidSize, "presenter rejects undersized pixel buffer");

        var parentProbe = CreateWindowExW(0, "STATIC", "REPlayerPresenterProbeParent", WS_POPUP | WS_VISIBLE, 0, 0, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (parentProbe != IntPtr.Zero)
        {
            try
            {
                using var childPresenter = new RevmBgraFramePresenter(parentProbe, width, height);
                Require(childPresenter.Capabilities.ParentHwnd == parentProbe, "child-HWND presenter retains real parent HWND");
                Require(childPresenter.Capabilities.OwnsNativeChildHwnd, "child-HWND presenter reports owned native child surface");
                Require(childPresenter.Capabilities.RenderHwnd != IntPtr.Zero, "child-HWND presenter creates render HWND");
                Require(childPresenter.Capabilities.BackendName is "bgra-native-child-hwnd-gdi-blit-presenter-v1" or "bgra-native-child-hwnd-d3d11-upload-stub-v1" or "bgra-native-child-hwnd-d3d11-upload-present-v1", "child-HWND presenter reports deterministic native backend name");
                if (childPresenter.Capabilities.SupportsChildHwndSwapchain)
                {
                    Require(childPresenter.D3D11SwapchainState.SwapchainCreated, "D3D11 swapchain scaffold creates a child-HWND swapchain when advertised");
                    Require(childPresenter.D3D11SwapchainState.Width == width && childPresenter.D3D11SwapchainState.Height == height, "D3D11 swapchain scaffold matches target dimensions");
                }
                else
                {
                    Require(!childPresenter.D3D11SwapchainState.SwapchainCreated, "child-HWND presenter exposes deterministic swapchain fallback state");
                }
                if (childPresenter.Capabilities.SupportsGpuUpload)
                {
                    Require(childPresenter.D3D11Probe.DeviceCreated, "D3D11 probe reports device creation when GPU upload is advertised");
                    Require(childPresenter.D3D11Probe.BgraSupportRequested, "D3D11 probe requested BGRA support");
                    Require(!string.IsNullOrWhiteSpace(childPresenter.D3D11Probe.FeatureLevel), "D3D11 probe records selected feature level when available");
                    Require(childPresenter.D3D11UploadState.DeviceCreated, "D3D11 upload stub creates a D3D11 device when advertised");
                    Require(childPresenter.D3D11UploadState.UploadTextureCreated, "D3D11 upload stub creates a BGRA texture when advertised");
                    Require(childPresenter.D3D11UploadState.TextureWidth == width && childPresenter.D3D11UploadState.TextureHeight == height, "D3D11 upload texture matches target dimensions");
                    if (childPresenter.Capabilities.SupportsChildHwndSwapchain)
                    {
                        Require(childPresenter.D3D11SwapchainState.Width == width && childPresenter.D3D11SwapchainState.Height == height, "DXGI swapchain scaffold matches target dimensions");
                        Require(childPresenter.D3D11SwapchainState.CreateHResult == 0, "DXGI swapchain scaffold records successful create HRESULT");
                    }
                    Require(childPresenter.Resize(width + 1, height + 1) == RevmRendererStatus.Ok, "D3D11 upload stub recreates texture on resize");
                    Require(childPresenter.D3D11UploadState.TextureWidth == width + 1 && childPresenter.D3D11UploadState.TextureHeight == height + 1, "D3D11 upload texture tracks resize dimensions");
                    if (childPresenter.Capabilities.SupportsChildHwndSwapchain)
                        Require(childPresenter.D3D11SwapchainState.Width == width + 1 && childPresenter.D3D11SwapchainState.Height == height + 1, "DXGI swapchain scaffold tracks resize dimensions");
                    Require(childPresenter.Resize(width, height) == RevmRendererStatus.Ok, "D3D11 upload stub resizes back to frame dimensions");
                }
                Require(childPresenter.Present(copiedFrame) == RevmRendererStatus.Ok, "child-HWND presenter accepts and presents copied BGRA frame");
                if (childPresenter.Capabilities.SupportsChildHwndSwapchain)
                {
                    Require(childPresenter.D3D11SwapchainState.TextureUploadAttempted, "D3D11 path uploads BGRA frame bytes into the upload texture");
                    Require(childPresenter.D3D11SwapchainState.BackbufferCopyAttempted, "D3D11 path copies the upload texture into the swapchain backbuffer");
                    Require(childPresenter.D3D11SwapchainState.LastBackbufferHResult == 0, "D3D11 backbuffer acquisition succeeds before Present");
                    Require(childPresenter.D3D11SwapchainState.PresentAttempted, "D3D11 path records a Present attempt");
                    Require(childPresenter.LastD3D11Present.FrameId == 3, "D3D11 present result records frame id");
                    Require(childPresenter.LastD3D11Present.SwapchainAvailable, "D3D11 present result reports swapchain availability");
                    Require(childPresenter.LastD3D11Present.Stage is "presented" or "present-failed", "D3D11 present result records terminal GPU stage");
                    Require(childPresenter.LastD3D11Present.SourceRectangleValid, "D3D11 present result validates source rectangle before upload");
                    Require(childPresenter.LastD3D11Present.SourceWidth == width && childPresenter.LastD3D11Present.SourceHeight == height, "D3D11 present result records source rectangle dimensions");
                    Require(childPresenter.LastD3D11Present.TargetWidth == width && childPresenter.LastD3D11Present.TargetHeight == height, "D3D11 present result records target rectangle dimensions");
                    Require(childPresenter.LastD3D11Present.TextureUploadAttempted, "D3D11 present result records upload attempt");
                    Require(childPresenter.LastD3D11Present.BackbufferCopyAttempted, "D3D11 present result records backbuffer copy");
                    Require(childPresenter.LastD3D11Present.BackbufferHResult == childPresenter.D3D11SwapchainState.LastBackbufferHResult, "D3D11 present result mirrors backbuffer HRESULT");
                    Require(childPresenter.LastD3D11Present.PresentAttempted, "D3D11 present result records Present attempt");
                    Require(childPresenter.LastD3D11Present.PresentHResult == childPresenter.D3D11SwapchainState.LastPresentHResult, "D3D11 present result mirrors Present HRESULT");
                    Require(childPresenter.LastD3D11Present.PresentStartTimestamp > 0 && childPresenter.LastD3D11Present.PresentEndTimestamp >= childPresenter.LastD3D11Present.PresentStartTimestamp, "D3D11 present result records pacing timestamps");
                    Require(childPresenter.LastD3D11Present.PresentDurationTicks >= 0, "D3D11 present result records non-negative present duration");
                    Require(childPresenter.LastD3D11Present.FrameIntervalTicks >= 0, "D3D11 present result records non-negative frame interval");
                }
                else
                {
                    Require(!childPresenter.LastD3D11Present.SwapchainAvailable, "fallback child-HWND presenter reports no D3D11 present attempt");
                }
                Require(childPresenter.LastUpload.ContentChecksum == Checksum(copiedFrame.Pixels), "child-HWND presenter records uploaded frame checksum");
            }
            finally
            {
                DestroyWindow(parentProbe);
            }
        }
    }

    var session = new RenderHostSession(new IntPtr(0x1234), width, height);
    Require(session.AttachVmTransport(endpoint) == RevmRendererStatus.Ok, "RenderHostSession attaches valid frame ring");
    Require(session.State == RevmRendererLifecycleState.TransportAttached, "session state is TransportAttached");
    Require(session.HasReadyFrame, "session exposes HasReadyFrame");
    Require(session.LatestReadyFrame is not null, "session exposes latest ready frame");
    Require(session.LatestReadyFrame!.Value.DataSize == frameBytes, "session latest frame size is valid");
    Require(session.FrameSource is not null, "session exposes renderer frame source");
    Require(session.TryReadLatestFrame(out var sessionFrame) == RevmRendererStatus.Ok, "session copies latest frame through frame source");
    Require(sessionFrame is not null && sessionFrame.Info.FrameId == 3, "session copied frame is latest frame");
    Require(sessionFrame!.Pixels[0] == 51 && sessionFrame.Pixels[3] == 255, "session copied frame carries BGRA bytes");
    Require(session.Presenter.PresentCount == 0, "session presenter starts with zero presents");
    Require(session.Presenter.Capabilities.ParentHwnd == new IntPtr(0x1234), "session presenter carries child HWND target");
    Require(session.Presenter.Capabilities.TargetWidth == width && session.Presenter.Capabilities.TargetHeight == height, "session presenter starts with create dimensions");
    Require(!session.Presenter.Capabilities.SupportsChildHwndSwapchain, "session presenter reports swapchain pending");
    Require(session.PresentLatestFrame() == RevmRendererStatus.Ok, "session presents latest copied frame through presenter seam");
    Require(!session.Presenter.LastD3D11Present.SwapchainAvailable, "session validation presenter reports no D3D11 present attempt without real parent HWND");
    Require(session.Presenter.LastPresentedFrameId == 3, "session presenter records latest frame id");
    Require(session.Presenter.LastUpload.FrameId == 3, "session presenter records uploaded latest frame id");
    Require(session.Presenter.LastUpload.BytesCopied == frameBytes, "session presenter records uploaded latest frame bytes");
    Require(session.Presenter.LastUpload.ContentChecksum == Checksum(sessionFrame.Pixels), "session presenter records deterministic uploaded latest frame checksum");
    Require(session.Presenter.PresentCount == 1, "session presenter records one present");
    Require(session.Resize(width + 1, height + 1) == RevmRendererStatus.Ok, "session resize updates presenter target surface contract");
    Require(session.Presenter.Capabilities.TargetWidth == width + 1 && session.Presenter.Capabilities.TargetHeight == height + 1, "session presenter capabilities track resize");
    Require(session.PresentLatestFrame() == RevmRendererStatus.InvalidSize, "session presenter rejects stale frame after target resize");
    Require(!session.Presenter.LastD3D11Present.SourceRectangleValid, "session presenter records invalid stale-frame rectangle diagnostics");
    Require(session.Presenter.LastD3D11Present.Stage == "source-target-width-mismatch", "session presenter records stale-frame width mismatch stage");
    Require(session.Destroy() == RevmRendererStatus.Ok, "session destroys and disposes consumer");
    Require(session.Presenter.LastStatus == RevmRendererStatus.AlreadyDestroyed, "session destroy disposes presenter");

    var badMagicRing = WriteRing(work, "bad-magic", width, height, slots, frames: 3);
    await using (var stream = new FileStream(badMagicRing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
    await using (var writer = new BinaryWriter(stream))
    {
        writer.Write(0xDEADBEEFu);
    }
    Require(RevmFrameRingConsumer.TryOpen(Endpoint("bad-magic", badMagicRing, width, height, slots), out _) == RevmRendererStatus.InvalidFrameRing, "consumer rejects bad magic");

    var missingEndpoint = Endpoint("missing", Path.Combine(work, "missing.frame-ring.bin"), width, height, slots);
    Require(RevmFrameRingConsumer.TryOpen(missingEndpoint, out _) == RevmRendererStatus.TransportAttachFailed, "consumer rejects missing ring image");

    Console.WriteLine("renderer frame-ring consumer probe passed");
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { }
}
''',
        encoding="utf-8",
    )


def main() -> int:
    if not DOTNET.exists():
        print(f"error: dotnet not found at {DOTNET}", file=sys.stderr)
        return 2

    if TMP_ROOT.exists():
        shutil.rmtree(TMP_ROOT)
    TMP_ROOT.mkdir(parents=True)

    try:
        write_probe_project(TMP_ROOT)
        run([str(DOTNET), "run", "--project", win_path(TMP_ROOT / "RenderHostFrameRingProbe.csproj"), "-c", "Release"])
    finally:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
