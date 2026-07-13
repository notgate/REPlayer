#!/usr/bin/env python3
"""Exercise WPF's optional NativeRenderHostAbiBinding against a real NativeAOT export.

This probe publishes the clean-room revm-render-host shim as a temporary Windows
NativeAOT shared library outside the repository, then builds a tiny temporary
C# harness from the WPF native-rendering binding source. The harness calls the
actual NativeRenderHostAbiBinding lifecycle path: load exports, create, attach a
shm-frame-ring-v1 endpoint, resize, rotate, destroy, and verify destroyed-handle
behavior. It stays at the REPlayer-owned native renderer boundary: no scrcpy, no
ADB display streaming, no SDL reparenting, no encoded video path, and no
proprietary renderer assets.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOTNET = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
TMP_ROOT = Path("/mnt/d/Downloads/revm-wpf-render-host-abi-binding-probe-tmp")
NATIVE_OUT = TMP_ROOT / "native"
HARNESS = TMP_ROOT / "harness"
RENDER_HOST_PROJECT = ROOT / "runtime-src" / "RevmRenderHost" / "RevmRenderHost.csproj"
OUTPUT_DLL = NATIVE_OUT / "revm-render-host.dll"


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


def run(cmd: list[str]) -> None:
    print("+", " ".join(cmd))
    subprocess.run(cmd, check=True)


def publish_native_renderer() -> None:
    run(
        [
            str(DOTNET),
            "publish",
            win_path(RENDER_HOST_PROJECT),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishAot=true",
            "-p:NativeLib=Shared",
            "-p:OutputType=Library",
            "-o",
            win_path(NATIVE_OUT),
        ]
    )
    if not OUTPUT_DLL.exists():
        raise FileNotFoundError(f"expected NativeAOT renderer DLL was not produced: {OUTPUT_DLL}")


def write_harness() -> None:
    sources = [
        ROOT / "ReVM" / "Rendering" / "Native" / "AbiBinding.cs",
        ROOT / "ReVM" / "Rendering" / "Native" / "Status.cs",
        ROOT / "ReVM" / "Rendering" / "Native" / "FrameRingFactory.cs",
    ]
    for source in sources:
        if not source.exists():
            raise FileNotFoundError(f"required WPF native-rendering source missing: {source}")

    compile_items = "\n".join(
        f'    <Compile Include="{win_path(source)}" Link="{source.name}" />' for source in sources
    )
    (HARNESS / "WpfNativeRenderHostAbiBindingProbe.csproj").write_text(
        f"""<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
{compile_items}
    <Compile Include=\"Program.cs\" />
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )

    renderer_dll = win_path(OUTPUT_DLL).replace("\\", "\\\\")
    transport_dir = win_path(TMP_ROOT / "transport").replace("\\", "\\\\")
    (HARNESS / "Program.cs").write_text(
        rf'''using ReVM.NativeRendering;

static void Expect(string name, RenderHostControllerStatus actual, RenderHostControllerStatus expected)
{{
    if (actual != expected)
    {{
        throw new InvalidOperationException($"{{name}}: expected {{expected}} ({{(int)expected}}), got {{actual}} ({{(int)actual}})");
    }}

    Console.WriteLine($"ok {{name}}: {{actual}} ({{(int)actual}})");
}}

var rendererPath = @"{renderer_dll}";
var transportDir = @"{transport_dir}";

if (!NativeRenderHostAbiBinding.TryLoad(rendererPath, out var binding) || binding is null)
{{
    throw new InvalidOperationException($"NativeRenderHostAbiBinding failed to load expected renderer exports from {{rendererPath}}");
}}

using (binding)
{{
    Expect("create via NativeRenderHostAbiBinding", binding.Create(new IntPtr(0x1234), 64, 48, 1), RenderHostControllerStatus.Ok);
    if (!binding.HasRendererHandle) throw new InvalidOperationException("binding did not retain a renderer handle after create");

    var endpoint = ShmFrameRingEndpointFactory.CreateSyntheticEndpointJson("wpf-abi-probe", 64, 48, transportDir);
    if (endpoint.Contains("scrcpy", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Contains("h264", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Contains("webrtc", StringComparison.OrdinalIgnoreCase))
    {{
        throw new InvalidOperationException($"endpoint contains a banned display transport token: {{endpoint}}");
    }}

    Expect("attach shm-frame-ring-v1 via NativeRenderHostAbiBinding", binding.AttachTransport(endpoint), RenderHostControllerStatus.Ok);
    Expect("present latest BGRA frame via NativeRenderHostAbiBinding", binding.PresentLatestFrame(), RenderHostControllerStatus.Ok);
    Expect("resize via NativeRenderHostAbiBinding", binding.Resize(80, 60), RenderHostControllerStatus.Ok);
    Expect("rotate via NativeRenderHostAbiBinding", binding.SetRotation(90), RenderHostControllerStatus.Ok);
    Expect("reject invalid rotation via NativeRenderHostAbiBinding", binding.SetRotation(45), RenderHostControllerStatus.InvalidState);
    Expect("destroy via NativeRenderHostAbiBinding", binding.DestroyRenderer(), RenderHostControllerStatus.Ok);
    if (binding.HasRendererHandle) throw new InvalidOperationException("binding retained a renderer handle after destroy");
    Expect("present after destroy is invalid state", binding.PresentLatestFrame(), RenderHostControllerStatus.InvalidState);
    Expect("resize after destroy is invalid state", binding.Resize(80, 60), RenderHostControllerStatus.InvalidState);
}}

Console.WriteLine("WPF NativeRenderHostAbiBinding probe passed");
''',
        encoding="utf-8",
    )


def main() -> int:
    if not DOTNET.exists():
        print(f"error: dotnet not found at {DOTNET}", file=sys.stderr)
        return 2
    if not RENDER_HOST_PROJECT.exists():
        print(f"error: render-host project not found at {RENDER_HOST_PROJECT}", file=sys.stderr)
        return 2

    if TMP_ROOT.exists():
        shutil.rmtree(TMP_ROOT)
    NATIVE_OUT.mkdir(parents=True)
    HARNESS.mkdir(parents=True)

    try:
        publish_native_renderer()
        write_harness()
        run([str(DOTNET), "run", "--project", win_path(HARNESS / "WpfNativeRenderHostAbiBindingProbe.csproj"), "-c", "Release"])
    finally:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
