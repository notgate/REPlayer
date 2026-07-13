#!/usr/bin/env python3
"""Exercise the clean-room revm-render-host ABI lifecycle contract.

This probe builds a temporary .NET console host outside the repository, references
runtime-src/RevmRenderHost, and verifies deterministic create/config,
transport-attach, resize, rotation, and destroy statuses. It intentionally stays
at the renderer ABI contract boundary: no scrcpy, no ADB display, no SDL
reparenting, and no encoded video display path.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-render-host-abi-probe-tmp")


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
    (work / "RenderHostAbiProbe.csproj").write_text(
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
        r'''using System.Text.Json;
using RevmRenderHost;

static void Expect(string name, RevmRendererStatus actual, RevmRendererStatus expected)
{
    if (actual != expected)
    {
        throw new InvalidOperationException($"{name}: expected {expected} ({(int)expected}), got {actual} ({(int)actual})");
    }

    Console.WriteLine($"ok {name}: {actual} ({(int)actual})");
}

static string Endpoint(int width = 720, int height = 1520, int stride = 2880, int slots = 3) => JsonSerializer.Serialize(new
{
    kind = "shm-frame-ring-v1",
    vmId = "abi-probe",
    mappingName = "Local\\RevmFrameRing-abi-probe",
    readyEventName = "Local\\RevmFrameReady-abi-probe",
    controlPipeName = "\\\\.\\pipe\\revm-control-abi-probe",
    width,
    height,
    stride,
    format = "BGRA8888",
    slots,
    abiVersion = 1
});

Expect("create valid config", RevmRendererAbi.ValidateCreateConfig(new RevmRendererConfig(new IntPtr(0x1234), 720, 1520)), RevmRendererStatus.Ok);
Expect("create rejects null parent hwnd", RevmRendererAbi.ValidateCreateConfig(new RevmRendererConfig(IntPtr.Zero, 720, 1520)), RevmRendererStatus.InvalidParentHwnd);
Expect("create rejects zero width", RevmRendererAbi.ValidateCreateConfig(new RevmRendererConfig(new IntPtr(0x1234), 0, 1520)), RevmRendererStatus.InvalidSize);
Expect("endpoint valid", RevmRendererAbi.ValidateTransportEndpoint(Endpoint()), RevmRendererStatus.Ok);
Expect("endpoint rejects unsupported kind", RevmRendererAbi.ValidateTransportEndpoint(Endpoint().Replace("shm-frame-ring-v1", "scrcpy")), RevmRendererStatus.UnsupportedTransportKind);
Expect("endpoint rejects unsupported format", RevmRendererAbi.ValidateTransportEndpoint(Endpoint().Replace("BGRA8888", "H264")), RevmRendererStatus.UnsupportedFormat);
Expect("endpoint rejects short stride", RevmRendererAbi.ValidateTransportEndpoint(Endpoint(stride: 16)), RevmRendererStatus.InvalidSize);
Expect("endpoint rejects slot overflow", RevmRendererAbi.ValidateTransportEndpoint(Endpoint(slots: 4)), RevmRendererStatus.InvalidTransportEndpoint);
Expect("endpoint rejects malformed json", RevmRendererAbi.ValidateTransportEndpoint("{"), RevmRendererStatus.InvalidTransportEndpoint);

var session = new RenderHostSession(new IntPtr(0x1234), 720, 1520);
Expect("session attach", session.AttachVmTransport(Endpoint()), RevmRendererStatus.Ok);
Expect("session resize", session.Resize(1080, 1920), RevmRendererStatus.Ok);
Expect("session rotation", session.SetRotation(90), RevmRendererStatus.Ok);
Expect("session rejects invalid rotation", session.SetRotation(45), RevmRendererStatus.InvalidState);
Expect("session destroy", session.Destroy(), RevmRendererStatus.Ok);
Expect("destroyed session rejects resize", session.Resize(720, 1520), RevmRendererStatus.AlreadyDestroyed);
Expect("destroyed session rejects attach", session.AttachVmTransport(Endpoint()), RevmRendererStatus.AlreadyDestroyed);

Console.WriteLine("render-host ABI lifecycle probe passed");
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
        run([str(DOTNET), "run", "--project", win_path(TMP_ROOT / "RenderHostAbiProbe.csproj"), "-c", "Release"])
    finally:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
