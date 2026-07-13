using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RevmVmm;

internal static class Program
{
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly SemaphoreSlim EmitLock = new(1, 1);
    private static Process? _qemu;
    private static Process? _fastpipeHost;

    private static async Task<int> Main(string[] args)
    {
        var options = RuntimeOptions.Parse(args);
        Directory.CreateDirectory(options.LogDir);
        var logPath = Path.Combine(options.LogDir, "revm-vmm.log");

        using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { StopQemu(); log.WriteLine($"[{Stamp()}] process-exit"); } catch { }
        };

        await EmitAsync(log, new RuntimeEvent("runtime.start", 1, "ReVM VMM bootstrap starting"));
        await EmitAsync(log, new RuntimeEvent("runtime.config", 5, JsonSerializer.Serialize(options)));

        if (options.SyntheticProducerFrames > 0)
        {
            await EmitAsync(log, new RuntimeEvent("producer.create", 6, "Starting bounded host-owned shm-frame-ring-v1 synthetic producer"));
            var producerExit = await RunSyntheticFrameProducerAsync(options, log);
            if (producerExit != 0 || !options.ValidateAfterSyntheticProducer)
                return producerExit;

            await EmitAsync(log, new RuntimeEvent("renderer.probe.start", 96, "Validating native renderer transport endpoint after synthetic frame production"));
            var producerProbe = await ProbeRendererTransportAsync(options.RendererEndpoint, log);
            await EmitAsync(log, producerProbe.RendererReady && producerProbe.FirstFrameReady
                ? new RuntimeEvent("renderer.probe.ready", 100, "Renderer endpoint validation passed after synthetic frame production")
                : new RuntimeEvent("renderer.probe.invalid", 100, "Renderer endpoint validation failed after synthetic frame production"));
            return producerProbe.RendererReady && producerProbe.FirstFrameReady ? 0 : 2;
        }

        if (options.ProbeQmpScanoutOnce)
        {
            await EmitAsync(log, new RuntimeEvent("producer.create", 6, "Starting one-shot QMP guest scanout producer probe"));
            return await RunQmpScanoutProbeOnceAsync(options, log);
        }

        if (options.ValidateRendererEndpointOnly)
        {
            await EmitAsync(log, new RuntimeEvent("renderer.probe.start", 5, "Validating native renderer transport endpoint only"));
            var probe = await ProbeRendererTransportAsync(options.RendererEndpoint, log);
            await EmitAsync(log, probe.RendererReady && probe.FirstFrameReady
                ? new RuntimeEvent("renderer.probe.ready", 100, "Renderer endpoint validation passed")
                : new RuntimeEvent("renderer.probe.invalid", 100, "Renderer endpoint validation failed or remained pending"));
            return probe.RendererReady && probe.FirstFrameReady ? 0 : 2;
        }

        var manifestLoad = await ValidateManifestFromOptionsAsync(options, log);
        if (manifestLoad.ExitCode is not null)
            return manifestLoad.ExitCode.Value;

        var manifest = manifestLoad.Manifest!;
        var engineRoot = manifestLoad.EngineRoot!;
        var runtimeRoot = manifestLoad.RuntimeRoot!;

        if (options.ValidateManifestOnly)
        {
            await EmitAsync(log, new RuntimeEvent("runtime.manifest.ready", 100,
                "Runtime manifest/package validation passed; VM launch intentionally skipped"));
            return 0;
        }

        _ = Task.Run(() => RunPipeServerAsync(options.PipeName, log, Shutdown.Token));

        var boot = ResolveBootAssets(runtimeRoot, engineRoot);
        foreach (var msg in boot.Messages)
            await EmitAsync(log, new RuntimeEvent("runtime.validate", boot.CanAttemptBoot ? 18 : 100, msg));

        if (!boot.CanAttemptBoot)
        {
            await EmitAsync(log, new RuntimeEvent("android.blocked", 100, boot.BlockReason));
            await KeepAliveAsync(log, "VMM bootstrap alive; Android guest not started");
            return 0;
        }

        if (string.Equals(manifest.DisplayMode, "native-hwnd-gfxstream-host", StringComparison.OrdinalIgnoreCase))
        {
            var fastpipeStarted = await StartReplayerFastpipeDeviceRuntimeAsync(manifest, boot, engineRoot, options, log);
            await KeepFastpipeHostAliveAsync(log, fastpipeStarted);
            return 0;
        }

        await EmitAsync(log, new RuntimeEvent("android.boot.prepare", 22, "Preparing Android x86_64 guest boot"));
        var qemuStart = await StartQemuAsync(boot, options, log);
        if (!qemuStart.Started)
        {
            await EmitAsync(log, new RuntimeEvent("android.boot.failed", 100, "QEMU/WHPX Android boot process failed to start"));
            await KeepAliveAsync(log, "VMM bootstrap alive; boot failed");
            return 3;
        }

        await EmitAsync(log, new RuntimeEvent("android.boot.kernel", 35, $"Android VM process started with graphics mode {qemuStart.GraphicsMode}; waiting for userspace/ADB"));
        await EmitRendererReadinessPendingAsync(log);
        var scanoutProducer = StartQmpScanoutProducerAsync(options, log, Shutdown.Token);
        _ = scanoutProducer.ContinueWith(t =>
        {
            if (t.Exception is not null)
                File.AppendAllText(Path.Combine(options.LogDir, "revm-vmm.log"), $"[{Stamp()}] qmp scanout producer fault: {t.Exception}\r\n");
        }, TaskContinuationOptions.OnlyOnFaulted);

        var adbReady = await WaitForAdbAsync(boot.AdbExe, options.AdbSerial, log, TimeSpan.FromSeconds(options.AdbTimeoutSeconds));
        if (adbReady)
        {
            await EmitAsync(log, new RuntimeEvent("adb.ready", 72, $"ADB online at {options.AdbSerial}"));
            await TryInstallAgentAsync(boot.AdbExe, options.AdbSerial, boot.AgentApk, boot.AgentScript, log);
        }
        else
        {
            await EmitAsync(log, new RuntimeEvent("adb.timeout", 72, $"ADB not online after {options.AdbTimeoutSeconds}s at {options.AdbSerial}"));
            await EmitAsync(log, new RuntimeEvent("android.ui.pending", 73,
                "Guest scanout is live but Android userspace is not ready: ADB is not online, so renderer frames may still be GRUB/initramfs/boot console rather than Android UI"));
        }

        var rendererStatus = await ProbeRendererTransportAsync(options.RendererEndpoint, log);
        var rendererReady = rendererStatus.RendererReady;
        var firstFrameReady = rendererStatus.FirstFrameReady;

        await EmitAsync(log, adbReady && rendererReady && firstFrameReady
            ? new RuntimeEvent("android.ready", 100, "Android guest booted: ADB online, renderer ready, and first native frame received")
            : new RuntimeEvent("android.partial", 94, "Android boot attempt running; waiting for missing readiness signals"));

        while (!Shutdown.IsCancellationRequested)
        {
            await EmitAsync(log, new RuntimeEvent("runtime.heartbeat", 100,
                _qemu is { HasExited: false } ? "Android VM process alive" : "Android VM process exited"));
            try { await Task.Delay(TimeSpan.FromSeconds(5), Shutdown.Token); }
            catch (OperationCanceledException) { break; }
        }

        StopQemu();
        await EmitAsync(log, new RuntimeEvent("runtime.stop", 100, "ReVM VMM bootstrap stopped"));
        return 0;
    }

    private static BootAssets ResolveBootAssets(string runtimeRoot, string engineRoot)
    {
        var qemu = Path.Combine(runtimeRoot, "qemu-system-x86_64.exe");
        var disk = Path.Combine(runtimeRoot, "android-base.qcow2");
        var kernel = Path.Combine(engineRoot, "boot", "kernel");
        var ramdisk = Path.Combine(engineRoot, "boot", "revm-ramdisk.img");
        var adb = Path.Combine(engineRoot, "bin", "adb.exe");
        var agent = Path.Combine(engineRoot, "guest", "revm-agent.apk");
        var agentScript = Path.Combine(engineRoot, "guest", "revm-agent.sh");
        var fastpipeGuest = Path.Combine(engineRoot, "guest", "fastpipe", "revm-fastpipe-guest");
        var fastpipeGuestRc = Path.Combine(engineRoot, "guest", "fastpipe", "revm_fastpipe_guest.rc");
        var messages = new List<string>
        {
            Component("qemu", qemu),
            Component("android disk", disk),
            Component("kernel", kernel),
            Component("ramdisk", ramdisk),
            Component("adb", adb),
            Component("agent apk", agent),
            Component("agent script", agentScript),
            Component("fastpipe guest bridge", fastpipeGuest),
            Component("fastpipe guest init rc", fastpipeGuestRc)
        };

        var missing = new[] { qemu, disk, kernel, ramdisk, adb }.Where(p => !File.Exists(p)).ToList();
        return new BootAssets(qemu, disk, kernel, ramdisk, adb, agent, agentScript, missing.Count == 0,
            missing.Count == 0 ? "" : "Missing boot asset(s): " + string.Join(", ", missing), messages);
    }

    private static async Task EmitGfxstreamFastpipeHostProbeAsync(string runtimeRoot, RuntimeOptions options, StreamWriter log)
    {
        var sdkEmulator = Path.Combine(runtimeRoot, "google-emulator", "sdk", "emulator");
        var artifacts = new[]
        {
            Path.Combine(sdkEmulator, "lib64", "libgfxstream_backend.dll"),
            Path.Combine(sdkEmulator, "lib64", "vulkan", "vulkan-1.dll"),
            Path.Combine(sdkEmulator, "lib64", "gles_angle", "libEGL.dll"),
            Path.Combine(sdkEmulator, "lib64", "gles_angle", "libGLESv2.dll")
        };

        await EmitAsync(log, new RuntimeEvent("gfxstream.fastpipe.start", 24,
            "Starting gfxstream/fastpipe product renderer path; QMP fallback is disabled"));

        foreach (var artifact in artifacts)
        {
            var info = new FileInfo(artifact);
            if (!info.Exists)
            {
                await EmitAsync(log, new RuntimeEvent("gfxstream.fastpipe.invalid", 100,
                    $"Missing gfxstream renderer artifact: {artifact}"));
                return;
            }

            await EmitAsync(log, new RuntimeEvent("gfxstream.artifact.ready", 30,
                $"{Path.GetFileName(artifact)} present ({info.Length} bytes)"));
        }

        var rendererStatus = await ProbeRendererTransportAsync(options.RendererEndpoint, log);
        await EmitAsync(log, rendererStatus.RendererReady && rendererStatus.FirstFrameReady
            ? new RuntimeEvent("gfxstream.fastpipe.ready", 96, "Gfxstream/fastpipe renderer endpoint produced first frame")
            : new RuntimeEvent("gfxstream.fastpipe.pending", 60,
                "Gfxstream artifacts are present and command endpoint is configured; guest fastpipe device/command transport is not integrated yet"));
    }

    private static async Task<bool> StartReplayerFastpipeDeviceRuntimeAsync(RuntimeManifest manifest, BootAssets boot, string engineRoot, RuntimeOptions options, StreamWriter log)
    {
        var hostExe = Path.GetFullPath(Path.Combine(engineRoot, manifest.FastpipeHostPath));
        var endpointPath = Path.Combine(options.LogDir, "revm-fastpipe-endpoint.json");
        var rendererApi = string.Equals(options.RendererApi, "opengl", StringComparison.OrdinalIgnoreCase) ? "opengl" : "vulkan";
        var controlTcpPort = GetAvailableTcpPort();
        var dataTcpPort = GetAvailableTcpPort();

        await EmitAsync(log, new RuntimeEvent("fastpipe.device.start", 22,
            $"Starting REPlayer-owned fastpipe device host rendererApi={rendererApi}; QMP fallback disabled; emulator wrapper disabled"));

        if (!File.Exists(hostExe))
        {
            await EmitAsync(log, new RuntimeEvent("fastpipe.device.invalid", 100,
                $"Missing REPlayer fastpipe host binary: {hostExe}"));
            return false;
        }

        var psi = new ProcessStartInfo(hostExe)
        {
            WorkingDirectory = Path.GetDirectoryName(hostExe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "--vm-id", options.PipeName,
            "--renderer-api", rendererApi,
            "--log-dir", options.LogDir,
            "--endpoint-out", endpointPath,
            "--shared-memory-bytes", "67108864",
            "--control-tcp-port", controlTcpPort.ToString(),
            "--data-tcp-port", dataTcpPort.ToString(),
            "--renderer-endpoint", options.RendererEndpoint ?? string.Empty
        })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            _fastpipeHost = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _fastpipeHost.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ForwardFastpipeHostEvent(e.Data, log); };
            _fastpipeHost.ErrorDataReceived += async (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) await EmitAsync(log, new RuntimeEvent("fastpipe.host.stderr", 50, e.Data)); };
            _fastpipeHost.Exited += async (_, _) => await EmitAsync(log, new RuntimeEvent("fastpipe.host.exited", 100, $"revm-fastpipe-host exited code={SafeExitCode(_fastpipeHost)}"));

            if (!_fastpipeHost.Start())
            {
                await EmitAsync(log, new RuntimeEvent("fastpipe.device.failed", 100, "Failed to start revm-fastpipe-host"));
                return false;
            }
            _fastpipeHost.BeginOutputReadLine();
            _fastpipeHost.BeginErrorReadLine();
            await EmitAsync(log, new RuntimeEvent("fastpipe.host.started", 30,
                $"revm-fastpipe-host started pid={_fastpipeHost.Id}; endpoint={endpointPath}; controlTcp=127.0.0.1:{controlTcpPort}; dataTcp=127.0.0.1:{dataTcpPort}"));

            var endpointReady = await WaitForFileContainingAsync(endpointPath, "revm-fastpipe-device-v1", TimeSpan.FromSeconds(10));
            if (!endpointReady)
            {
                await EmitAsync(log, new RuntimeEvent("fastpipe.device.failed", 100, "Fastpipe endpoint was not produced by host service"));
                return false;
            }

            await EmitAsync(log, new RuntimeEvent("fastpipe.device.ready", 45,
                "REPlayer-owned fastpipe host/device endpoint is ready"));
            await EmitAsync(log, new RuntimeEvent("fastpipe.guest.ports", 46,
                $"Exposing /dev/virtio-ports/revm.fastpipe.control -> 127.0.0.1:{controlTcpPort} and /dev/virtio-ports/revm.fastpipe.data -> 127.0.0.1:{dataTcpPort}"));

            var guestStarted = await StartFinalFastpipeAndroidGuestAsync(boot, options, controlTcpPort, dataTcpPort, log);
            if (!guestStarted)
                return true;

            await EmitAsync(log, new RuntimeEvent("display.primary", 61,
                "Primary display source is revm.fastpipe.data -> scanout-import frame ring; QMP screendump bridge is retired for product display"));

            var androidReady = await WaitForAndroidFrameworkReadyAsync(boot.AdbExe, options.AdbSerial, log, TimeSpan.FromSeconds(options.AdbTimeoutSeconds));
            if (androidReady.FrameworkReady)
            {
                await EmitAsync(log, new RuntimeEvent("adb.ready", 72, $"ADB online at {options.AdbSerial}"));
                await EmitAsync(log, new RuntimeEvent("android.ready", 100,
                    "Android framework is ready on the REPlayer-owned native runtime; display is fastpipe scanout-import, ADB is control/readiness only"));
            }
            else
            {
                await EmitAsync(log, new RuntimeEvent(androidReady.AdbOnline ? "android.framework.timeout" : "adb.timeout", 72,
                    $"Android framework readiness did not complete within {options.AdbTimeoutSeconds}s at {options.AdbSerial}; {androidReady.Summary}"));
                await EmitAsync(log, new RuntimeEvent("android.partial", 88,
                    "Android VM launched with guest-visible fastpipe virtio ports, but framework readiness is still pending"));
            }
            return true;
        }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("fastpipe.device.failed", 100, ex.Message));
            return false;
        }
    }

    private static async Task<bool> StartFinalFastpipeAndroidGuestAsync(BootAssets boot, RuntimeOptions options, int controlTcpPort, int dataTcpPort, StreamWriter log)
    {
        var serialLog = Path.Combine(options.LogDir, "android-fastpipe-serial.log");
        var qemuLog = Path.Combine(options.LogDir, "qemu-fastpipe-android.log");
        var args = BuildFinalFastpipeQemuArgs(boot, options, serialLog, controlTcpPort, dataTcpPort);
        await EmitAsync(log, new RuntimeEvent("android.boot.prepare", 50,
            "Launching Android VM with guest-visible virtio fastpipe ports; QMP display fallback disabled; emulator wrapper disabled"));
        await WriteLogLineAsync(log, "final fastpipe qemu args: " + args);

        try
        {
            _qemu = new Process
            {
                StartInfo = new ProcessStartInfo(boot.QemuExe, args)
                {
                    WorkingDirectory = Path.GetDirectoryName(boot.QemuExe)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            _qemu.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) File.AppendAllText(qemuLog, $"OUT {e.Data}\r\n"); };
            _qemu.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) File.AppendAllText(qemuLog, $"ERR {e.Data}\r\n"); };
            _qemu.Exited += async (_, _) => await EmitAsync(log, new RuntimeEvent("android.boot.exited", 100,
                $"QEMU final fastpipe Android process exited code={SafeExitCode(_qemu)}"));
            if (!_qemu.Start())
            {
                await EmitAsync(log, new RuntimeEvent("android.boot.failed", 100, "Failed to start QEMU Android process"));
                return false;
            }
            _qemu.BeginOutputReadLine();
            _qemu.BeginErrorReadLine();
            await Task.Delay(TimeSpan.FromSeconds(5), Shutdown.Token);
            if (_qemu.HasExited)
            {
                await EmitAsync(log, new RuntimeEvent("android.boot.failed", 100,
                    $"QEMU exited during final fastpipe startup code={SafeExitCode(_qemu)}; see {qemuLog}"));
                return false;
            }
            await EmitAsync(log, new RuntimeEvent("qemu.started", 58,
                $"QEMU PID {_qemu.Id}; finalFastpipeVirtioSerial=true; controlPort=/dev/virtio-ports/revm.fastpipe.control; dataPort=/dev/virtio-ports/revm.fastpipe.data"));
            await EmitAsync(log, new RuntimeEvent("android.boot.kernel", 60,
                "Android VM process is running with REPlayer fastpipe virtio-serial channels attached"));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("android.boot.failed", 100, "QEMU final fastpipe launch failed: " + ex.Message));
            return false;
        }
    }

    private static string BuildFinalFastpipeQemuArgs(BootAssets boot, RuntimeOptions options, string serialLog, int controlTcpPort, int dataTcpPort)
    {
        var adbHostPort = TryParseLoopbackPort(options.AdbSerial, 5555);
        return string.Join(' ', new[]
        {
            "-accel whpx,kernel-irqchip=off",
            "-machine q35",
            "-cpu qemu64",
            $"-smp {options.CpuCount}",
            $"-m {options.RamMb}",
            $"-drive file=\"{boot.AndroidDisk}\",if=virtio,format=qcow2,cache=writeback",
            "-snapshot",
            // Android-x86 9 framework readiness currently fails with SurfaceFlinger
            // tombstones in EGL/GLES init under headless virtio-vga. Use the
            // conservative std VGA device for framework boot; REPlayer display
            // remains the native fastpipe/gfxstream path, not QEMU's window.
            "-vga std",
            "-display none",
            "-monitor none",
            $"-qmp tcp:127.0.0.1:{options.QmpPort},server,nowait",
            $"-serial file:\"{serialLog}\"",
            BuildAdbUserNetdevArg(adbHostPort),
            // Final fastpipe Android-x86 ramdisk probes/configures wifi_eth for the
            // control network. Expose the forwarded ADB NAT on virtio-net instead
            // of e1000 so the guest gets the carrier model its bootstrap expects.
            // disable-modern keeps the device compatible with older Android-x86 kernels.
            "-device virtio-net-pci,netdev=net0,disable-modern=on,mac=52:54:00:52:56:4D",
            // Entropy starvation can delay Android userspace/adbd during early boot;
            // virtio-rng is a low-risk headless QEMU device supported by Android-x86.
            "-device virtio-rng-pci",
            "-device virtio-serial-pci,id=revmfastpipe,disable-modern=on",
            $"-chardev socket,id=revmfpctrl,host=127.0.0.1,port={controlTcpPort}",
            "-device virtserialport,bus=revmfastpipe.0,chardev=revmfpctrl,name=revm.fastpipe.control",
            $"-chardev socket,id=revmfpdata,host=127.0.0.1,port={dataTcpPort}",
            "-device virtserialport,bus=revmfastpipe.0,chardev=revmfpdata,name=revm.fastpipe.data"
        });
    }

    private static string BuildAdbUserNetdevArg(int adbHostPort) =>
        $"-netdev user,id=net0,hostname=revm-android,hostfwd=tcp:127.0.0.1:{adbHostPort}-:5555";

    private static int TryParseLoopbackPort(string serial, int fallback)
    {
        if (string.IsNullOrWhiteSpace(serial)) return fallback;
        var colon = serial.LastIndexOf(':');
        return colon >= 0 && int.TryParse(serial[(colon + 1)..], out var port) && port > 0 ? port : fallback;
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForFileContainingAsync(string path, string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !Shutdown.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var content = await reader.ReadToEndAsync();
                    if (content.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            try { await Task.Delay(200, Shutdown.Token); } catch { break; }
        }
        return false;
    }

    private static void ForwardFastpipeHostEvent(string json, StreamWriter log)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = StringProp(doc.RootElement, "Type");
            var progress = IntProp(doc.RootElement, "Progress");
            var message = StringProp(doc.RootElement, "Message");
            _ = EmitAsync(log, new RuntimeEvent(type.Length == 0 ? "fastpipe.host.event" : type, progress, message));
        }
        catch
        {
            _ = WriteLogLineAsync(log, "fastpipe host: " + json);
        }
    }

    private static async Task<bool> StartGfxstreamFastpipeRuntimeAsync(string runtimeRoot, RuntimeOptions options, StreamWriter log)
    {
        var sdkRoot = Path.Combine(runtimeRoot, "google-emulator", "sdk");
        var emulatorExe = Path.Combine(sdkRoot, "emulator", "emulator.exe");
        var adbExe = Path.Combine(sdkRoot, "platform-tools", "adb.exe");
        var avdHome = Path.Combine(runtimeRoot, "google-emulator", "avd-home");

        await EmitGfxstreamFastpipeHostProbeAsync(runtimeRoot, options, log);

        var missing = new[] { emulatorExe, adbExe, avdHome }
            .Where(path => !(File.Exists(path) || Directory.Exists(path)))
            .ToList();
        if (missing.Count > 0)
        {
            await EmitAsync(log, new RuntimeEvent("gfxstream.fastpipe.invalid", 100,
                "Missing Google emulator gfxstream runtime asset(s): " + string.Join(", ", missing)));
            return false;
        }

        var rendererApi = string.Equals(options.RendererApi, "opengl", StringComparison.OrdinalIgnoreCase) ? "opengl" : "vulkan";
        const string serial = "emulator-5554";
        var stdoutLog = Path.Combine(options.LogDir, "gfxstream-emulator.stdout.log");
        var stderrLog = Path.Combine(options.LogDir, "gfxstream-emulator.stderr.log");

        await EmitAsync(log, new RuntimeEvent("android.boot.prepare", 32,
            $"Launching hardware-accelerated gfxstream Android runtime rendererApi={rendererApi}; QMP fallback disabled"));

        try
        {
            await RunProcessAsync(adbExe, $"-s {serial} emu kill", log, TimeSpan.FromSeconds(5));
            await RunProcessAsync(adbExe, "kill-server", log, TimeSpan.FromSeconds(5));
            try { await Task.Delay(1500, Shutdown.Token); } catch { }
            var psi = new ProcessStartInfo(emulatorExe)
            {
                WorkingDirectory = Path.GetDirectoryName(emulatorExe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["ANDROID_SDK_ROOT"] = sdkRoot;
            psi.Environment["ANDROID_HOME"] = sdkRoot;
            psi.Environment["ANDROID_AVD_HOME"] = avdHome;
            psi.Environment["QT_QPA_PLATFORM"] = "offscreen";

            psi.ArgumentList.Add("-feature");
            psi.ArgumentList.Add(rendererApi == "vulkan" ? "Vulkan" : "-Vulkan");
            foreach (var arg in new[]
            {
                "-avd", "ReVM",
                "-no-snapshot-save",
                "-no-boot-anim",
                "-gpu", "host",
                "-accel", "on",
                "-port", "5554",
                "-writable-system",
                "-no-audio",
                "-no-window",
                "-no-metrics",
                "-verbose"
            })
            {
                psi.ArgumentList.Add(arg);
            }

            _qemu = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _qemu.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) File.AppendAllText(stdoutLog, e.Data + Environment.NewLine); };
            _qemu.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) File.AppendAllText(stderrLog, e.Data + Environment.NewLine); };
            _qemu.Exited += async (_, _) =>
            {
                await EmitAsync(log, new RuntimeEvent("android.boot.exited", 100,
                    $"gfxstream emulator process exited code={SafeExitCode(_qemu)}"));
            };

            if (!_qemu.Start())
            {
                await EmitAsync(log, new RuntimeEvent("android.boot.failed", 100, "Failed to start gfxstream emulator process"));
                return false;
            }

            _qemu.BeginOutputReadLine();
            _qemu.BeginErrorReadLine();
            await EmitAsync(log, new RuntimeEvent("qemu.started", 38,
                $"gfxstream emulator started pid={_qemu.Id}; rendererApi={rendererApi}; gpu=host; serial={serial}"));

            var ready = await WaitForEmulatorBootCompletedAsync(adbExe, serial, log, TimeSpan.FromSeconds(options.AdbTimeoutSeconds));
            if (!ready)
            {
                await EmitAsync(log, new RuntimeEvent("adb.timeout", 72,
                    $"ADB/device boot readiness did not complete within {options.AdbTimeoutSeconds}s for {serial}"));
                return true;
            }

            await EmitAsync(log, new RuntimeEvent("adb.ready", 72, $"ADB online at {serial}"));
            await EmitAsync(log, new RuntimeEvent("android.ready", 100,
                $"Android booted with gfxstream-fastpipe reference runtime; rendererApi={rendererApi}; sys.boot_completed=1"));
            return true;
        }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("android.boot.failed", 100,
                "gfxstream emulator launch failed: " + ex.Message));
            return false;
        }
    }

    private static async Task<bool> WaitForEmulatorBootCompletedAsync(string adbExe, string serial, StreamWriter log, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        await RunProcessAsync(adbExe, "start-server", log, TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline && !Shutdown.IsCancellationRequested)
        {
            if (_qemu is { HasExited: true }) return false;
            var devices = await RunProcessAsync(adbExe, "devices", log, TimeSpan.FromSeconds(10));
            if (IsAdbDeviceOnline(devices.Output, serial))
            {
                var boot = await RunProcessAsync(adbExe, $"-s {serial} shell getprop sys.boot_completed", log, TimeSpan.FromSeconds(10));
                var bootValue = boot.Output.Trim();
                await EmitAsync(log, new RuntimeEvent("adb.wait", 65, $"{serial} online; sys.boot_completed={bootValue}"));
                if (bootValue == "1") return true;
            }
            else
            {
                await EmitAsync(log, new RuntimeEvent("adb.wait", 55, $"Waiting for ADB at {serial}"));
            }

            try { await Task.Delay(2000, Shutdown.Token); } catch { break; }
        }

        return false;
    }

    private static string Component(string name, string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{name}: {path} ({info.Length} bytes)" : $"{name}: missing {path}";
    }

    private static async Task<QemuStartResult> StartQemuAsync(BootAssets boot, RuntimeOptions options, StreamWriter log)
    {
        var serialLog = Path.Combine(options.LogDir, "android-serial.log");
        var qemuLog = Path.Combine(options.LogDir, "qemu-android.log");

        foreach (var plan in BuildQemuGraphicsPlans(options))
        {
            var args = BuildQemuArgs(boot, options, serialLog, plan);
            await EmitAsync(log, new RuntimeEvent("qemu.start", 28, $"Launching Android VM with {plan.Mode}: {boot.AndroidDisk}"));
            await WriteLogLineAsync(log, $"qemu graphics plan: {plan.Mode}; {plan.Description}");
            await WriteLogLineAsync(log, $"qemu args: {args}");

            try
            {
                _qemu = new Process
                {
                    StartInfo = new ProcessStartInfo(boot.QemuExe, args)
                    {
                        WorkingDirectory = Path.GetDirectoryName(boot.QemuExe)!,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                _qemu.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) File.AppendAllText(qemuLog, $"OUT {e.Data}\r\n"); };
                _qemu.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) File.AppendAllText(qemuLog, $"ERR {e.Data}\r\n"); };
                _qemu.Exited += (_, _) => File.AppendAllText(qemuLog, $"EXIT {SafeExitCode(_qemu)}\r\n");
                if (!_qemu.Start()) continue;
                _qemu.BeginOutputReadLine();
                _qemu.BeginErrorReadLine();

                var qmpReady = await WaitForQmpReadyAsync(options.QmpPort, _qemu, TimeSpan.FromMilliseconds(plan.StartupGraceMs));
                if (!qmpReady)
                {
                    StopQemu();
                    await EmitAsync(log, new RuntimeEvent("qemu.graphics.failed", 30,
                        $"QEMU graphics mode {plan.Mode} did not expose QMP during startup; exitCode={SafeExitCode(_qemu)}; trying next supported mode"));
                    continue;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(plan.PostQmpStableMs));
                if (_qemu.HasExited)
                {
                    await EmitAsync(log, new RuntimeEvent("qemu.graphics.failed", 30,
                        $"QEMU graphics mode {plan.Mode} exited after QMP startup with code {SafeExitCode(_qemu)}; trying next supported mode"));
                    continue;
                }

                if (plan.IsGuestGl)
                {
                    await EmitAsync(log, new RuntimeEvent("gfxstream.bridge.unavailable", 31,
                        "Installed QEMU exposes virtio-gpu-gl/virgl, not gfxstream; using the GL-capable virtio-gpu scanout path until a gfxstream-enabled VMM is integrated"));
                    await EmitAsync(log, new RuntimeEvent("virtio_gpu.scanout.configured", 32, plan.Description));
                }

                await EmitAsync(log, new RuntimeEvent("qemu.started", 32, $"QEMU PID {_qemu.Id}; graphics={plan.Mode}"));
                return new QemuStartResult(true, plan.Mode);
            }
            catch (Exception ex)
            {
                await EmitAsync(log, new RuntimeEvent("qemu.error", 100, $"{plan.Mode}: {ex.Message}"));
            }
        }

        return new QemuStartResult(false, "none");
    }

    private static string BuildQemuArgs(BootAssets boot, RuntimeOptions options, string serialLog, QemuGraphicsPlan plan) =>
        string.Join(' ', new[]
        {
            "-accel whpx,kernel-irqchip=off",
            "-machine q35",
            "-cpu qemu64",
            $"-smp {options.CpuCount}",
            $"-m {options.RamMb}",
            // Use the installed GRUB boot path. The patched direct -kernel/-initrd path
            // currently reboots/exits under WHPX on this Android-x86 image.
            $"-drive file=\"{boot.AndroidDisk}\",if=virtio,format=qcow2,cache=writeback",
            plan.DeviceArg,
            plan.DisplayArg,
            "-monitor none",
            $"-qmp tcp:127.0.0.1:{options.QmpPort},server,nowait",
            $"-serial file:\"{serialLog}\"",
            BuildAdbUserNetdevArg(TryParseLoopbackPort(options.AdbSerial, 5555)),
            // Keep e1000 for the legacy QMP scanout boot path's WHPX stability.
            "-device e1000,netdev=net0"
        });

    private static IEnumerable<QemuGraphicsPlan> BuildQemuGraphicsPlans(RuntimeOptions options)
    {
        var mode = (options.QemuGraphicsMode ?? "auto").Trim().ToLowerInvariant();
        if (mode is "egl-headless-gl" or "virtio-gpu-gl")
        {
            yield return new QemuGraphicsPlan(
                "egl-headless-gl",
                "-device virtio-vga-gl,blob=on,hostmem=256M",
                "-display egl-headless,gl=on",
                IsGuestGl: true,
                StartupGraceMs: 5000,
                PostQmpStableMs: 1500,
                "Headless virtio-gpu GL scanout via QEMU egl-headless");
        }

        if (mode is "gtk-gl")
        {
            yield return new QemuGraphicsPlan(
                "gtk-gl",
                "-device virtio-vga-gl,blob=on,hostmem=256M",
                "-display gtk,gl=on",
                IsGuestGl: true,
                StartupGraceMs: 5000,
                PostQmpStableMs: 1500,
                "Windowed diagnostic virtio-gpu GL scanout via QEMU GTK; not used by the WPF display path");
        }

        if (mode is "auto" or "virtio-gpu-2d" or "qmp-scanout")
        {
            yield return new QemuGraphicsPlan(
                "virtio-gpu-2d-qmp-scanout",
                "-device virtio-vga",
                "-display none",
                IsGuestGl: false,
                StartupGraceMs: 5000,
                PostQmpStableMs: 500,
                "Headless virtio-gpu 2D scanout copied through QMP screendump into the REPlayer frame ring");
        }
    }

    private static async Task<bool> WaitForQmpReadyAsync(int qmpPort, Process qemu, TimeSpan timeout)
    {
        var stopAt = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stopAt)
        {
            if (qemu.HasExited) return false;
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync("127.0.0.1", qmpPort, cts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(100);
            }
        }

        return !qemu.HasExited;
    }

    private static async Task EmitRendererReadinessPendingAsync(StreamWriter log)
    {
        await EmitAsync(log, new RuntimeEvent("renderer.pending", 36, "Native render host launch is pending"));
        await EmitAsync(log, new RuntimeEvent("transport.pending", 37, "Native GPU/frame transport endpoint is pending"));
        await EmitAsync(log, new RuntimeEvent("first_frame.pending", 38, "First native renderer frame is pending"));
    }

    private static async Task StartQmpScanoutProducerAsync(RuntimeOptions options, StreamWriter log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.RendererEndpoint))
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 39, "QMP scanout producer requires a renderer endpoint"));
            return;
        }

        string endpointText;
        try { endpointText = await ResolveEndpointTextAsync(options.RendererEndpoint); }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 39, $"Renderer endpoint could not be read for QMP scanout: {ex.Message}"));
            return;
        }

        if (!TryExtractFrameEndpoint(endpointText, out var frameEndpoint, out var error))
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 39, $"QMP scanout producer needs a shm-frame-ring payload endpoint: {error}"));
            return;
        }

        var ringImagePath = StringProp(frameEndpoint, "ringImagePath");
        var width = IntProp(frameEndpoint, "width");
        var height = IntProp(frameEndpoint, "height");
        var stride = IntProp(frameEndpoint, "stride");
        var slots = IntProp(frameEndpoint, "slots");
        if (string.IsNullOrWhiteSpace(ringImagePath) || width <= 0 || height <= 0 || stride < width * 4 || slots is < 1 or > 3)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 39, "QMP scanout producer received an invalid frame-ring endpoint"));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ringImagePath)) ?? ".");
        var dumpRoot = Path.Combine(options.LogDir, "qmp-scanout");
        Directory.CreateDirectory(dumpRoot);
        await EmitAsync(log, new RuntimeEvent("producer.created", 40,
            $"QMP guest scanout producer attached to QEMU port {options.QmpPort}; writing {width}x{height} frames to shm-frame-ring-v1"));

        var frameId = 0;
        var lastObservation = string.Empty;
        byte[]? lastVisibleFrame = null;
        ScanoutObservation? lastVisibleObservation = null;
        while (!ct.IsCancellationRequested && _qemu is { HasExited: false })
        {
            try
            {
                var dumpPath = Path.Combine(dumpRoot, $"scanout-{Environment.ProcessId}-{frameId + 1:D8}.ppm");
                var dumped = await TryQmpScreendumpAsync(options.QmpPort, dumpPath, log, TimeSpan.FromSeconds(3), ct);
                var ppmError = string.Empty;
                if (dumped && TryReadP6PpmAsBgraWithRetry(dumpPath, width, height, stride, out var bgra, out ppmError))
                {
                    try { File.Delete(dumpPath); } catch { }
                    var observation = AnalyzeBgraFrame(bgra, width, height, stride);
                    // Only promote real Android-looking content as the preserved frame.
                    // Low-colour boot/console and transient blank/gray QMP scanouts are
                    // diagnostic signals, not user-visible frames. If we preserve or
                    // present them after the launcher has appeared, the REPlayer view
                    // constantly flashes gray/console and then comes back.
                    if (string.Equals(observation.State, "display-changing", StringComparison.OrdinalIgnoreCase))
                    {
                        lastVisibleFrame = bgra;
                        lastVisibleObservation = observation;
                    }
                    else if (lastVisibleFrame is not null && lastVisibleObservation is not null)
                    {
                        bgra = lastVisibleFrame;
                        observation = new ScanoutObservation(
                            lastVisibleObservation.State,
                            lastVisibleObservation.EventType,
                            lastVisibleObservation.Progress,
                            $"Guest scanout produced {observation.State}; preserving last Android-looking frame instead of overwriting display ({lastVisibleObservation.Summary})",
                            lastVisibleObservation.Summary,
                            true);
                    }

                    frameId++;
                    var readyIndex = (frameId - 1) % slots;
                    WriteBgraRingImage(ringImagePath, width, height, stride, slots, frameId, readyIndex, bgra);
                    if (frameId <= 3 || frameId % 30 == 0)
                    {
                        await EmitAsync(log, new RuntimeEvent("producer.frame_written", Math.Min(95, 40 + frameId % 55),
                            $"QMP guest scanout frame {frameId} written to slot {readyIndex}; {observation.Summary}"));
                    }

                    if (!string.Equals(lastObservation, observation.State, StringComparison.OrdinalIgnoreCase) || frameId == 1 || frameId % 120 == 0)
                    {
                        lastObservation = observation.State;
                        await EmitAsync(log, new RuntimeEvent(observation.EventType, observation.Progress, observation.Message));
                    }
                }
                else if (dumped)
                {
                    try { File.Delete(dumpPath); } catch { }
                    await EmitAsync(log, new RuntimeEvent("scanout.pending", 42, $"QMP screendump not usable yet: {ppmError}"));
                }
            }
            catch (Exception ex)
            {
                await EmitAsync(log, new RuntimeEvent("scanout.pending", 42, $"Waiting for QMP guest scanout: {ex.Message}"));
            }

            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }

        await EmitAsync(log, new RuntimeEvent("producer.stopped", 100, "QMP guest scanout producer stopped"));
    }

    private static async Task<int> RunQmpScanoutProbeOnceAsync(RuntimeOptions options, StreamWriter log)
    {
        if (string.IsNullOrWhiteSpace(options.RendererEndpoint))
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 100, "QMP scanout probe requires --renderer-endpoint with a shm-frame-ring-v1 payload endpoint"));
            return 2;
        }

        string endpointText;
        try { endpointText = await ResolveEndpointTextAsync(options.RendererEndpoint); }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 100, $"Renderer endpoint could not be read for QMP scanout probe: {ex.Message}"));
            return 2;
        }

        if (!TryExtractFrameEndpoint(endpointText, out var frameEndpoint, out var error))
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 100, $"QMP scanout probe needs a shm-frame-ring payload endpoint: {error}"));
            return 2;
        }

        var ringImagePath = StringProp(frameEndpoint, "ringImagePath");
        var width = IntProp(frameEndpoint, "width");
        var height = IntProp(frameEndpoint, "height");
        var stride = IntProp(frameEndpoint, "stride");
        var slots = IntProp(frameEndpoint, "slots");
        if (string.IsNullOrWhiteSpace(ringImagePath) || width <= 0 || height <= 0 || stride < width * 4 || slots is < 1 or > 3)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.invalid", 100, "QMP scanout probe received an invalid frame-ring endpoint"));
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ringImagePath)) ?? ".");
        var dumpPath = Path.Combine(options.LogDir, "qmp-scanout-probe.ppm");
        await EmitAsync(log, new RuntimeEvent("producer.created", 12,
            $"One-shot QMP guest scanout producer attached to port {options.QmpPort}; target={width}x{height}, stride={stride}, slots={slots}"));

        bool dumped;
        try
        {
            dumped = await TryQmpScreendumpAsync(options.QmpPort, dumpPath, log, TimeSpan.FromSeconds(3), Shutdown.Token);
        }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.pending", 100, $"QMP screendump probe failed: {ex.Message}"));
            return 2;
        }

        if (!dumped)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.pending", 100, "QMP screendump did not produce a usable PPM file"));
            return 2;
        }

        string ppmError = string.Empty;
        byte[] bgra = Array.Empty<byte>();
        var read = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            read = TryReadP6PpmAsBgra(dumpPath, width, height, stride, out bgra, out ppmError);
            if (read) break;
            if (!ppmError.Contains("truncated", StringComparison.OrdinalIgnoreCase)) break;
            try { await Task.Delay(50, Shutdown.Token); } catch (OperationCanceledException) { break; }
        }

        if (!read)
        {
            await EmitAsync(log, new RuntimeEvent("scanout.pending", 100, $"QMP screendump PPM was not usable: {ppmError}"));
            return 2;
        }

        WriteBgraRingImage(ringImagePath, width, height, stride, slots, frameId: 1, readyIndex: 0, bgra);
        await EmitAsync(log, new RuntimeEvent("producer.frame_written", 80, "QMP guest scanout probe frame 1 written to slot 0"));
        await EmitAsync(log, new RuntimeEvent("producer.stopped", 90, "One-shot QMP guest scanout producer probe stopped"));

        await EmitAsync(log, new RuntimeEvent("renderer.probe.start", 92, "Validating native renderer transport endpoint after QMP scanout probe"));
        var probe = await ProbeRendererTransportAsync(options.RendererEndpoint, log);
        await EmitAsync(log, probe.RendererReady && probe.FirstFrameReady
            ? new RuntimeEvent("renderer.probe.ready", 100, "Renderer endpoint validation passed after QMP scanout probe")
            : new RuntimeEvent("renderer.probe.invalid", 100, "Renderer endpoint validation failed after QMP scanout probe"));
        return probe.RendererReady && probe.FirstFrameReady ? 0 : 2;
    }

    private static async Task<bool> TryQmpScreendumpAsync(int qmpPort, string outputPath, StreamWriter log, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", qmpPort, timeoutCts.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

        _ = await reader.ReadLineAsync(timeoutCts.Token);
        await writer.WriteLineAsync("{\"execute\":\"qmp_capabilities\"}");
        _ = await reader.ReadLineAsync(timeoutCts.Token);
        var escaped = outputPath.Replace("\\", "\\\\");
        await writer.WriteLineAsync($"{{\"execute\":\"human-monitor-command\",\"arguments\":{{\"command-line\":\"screendump {escaped}\"}}}}");
        var reply = await reader.ReadLineAsync(timeoutCts.Token) ?? string.Empty;
        await WriteLogLineAsync(log, $"qmp screendump reply: {reply}");
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 16;
    }

    private static bool TryReadP6PpmAsBgraWithRetry(string path, int targetWidth, int targetHeight, int targetStride, out byte[] bgra, out string error)
    {
        bgra = Array.Empty<byte>();
        error = string.Empty;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (TryReadP6PpmAsBgra(path, targetWidth, targetHeight, targetStride, out bgra, out error))
                return true;

            if (!error.Contains("being used", StringComparison.OrdinalIgnoreCase) &&
                !error.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static bool TryReadP6PpmAsBgra(string path, int targetWidth, int targetHeight, int targetStride, out byte[] bgra, out string error)
    {
        bgra = Array.Empty<byte>();
        error = string.Empty;
        try
        {
            var data = File.ReadAllBytes(path);
            var index = 0;
            string NextToken()
            {
                while (index < data.Length)
                {
                    var c = (char)data[index];
                    if (char.IsWhiteSpace(c)) { index++; continue; }
                    if (c == '#') { while (index < data.Length && data[index] != '\n') index++; continue; }
                    break;
                }
                var start = index;
                while (index < data.Length && !char.IsWhiteSpace((char)data[index])) index++;
                return Encoding.ASCII.GetString(data, start, index - start);
            }

            var magic = NextToken();
            var width = int.Parse(NextToken());
            var height = int.Parse(NextToken());
            var max = int.Parse(NextToken());
            if (index < data.Length && char.IsWhiteSpace((char)data[index])) index++;
            if (magic != "P6" || max != 255) { error = $"unsupported ppm header {magic} max={max}"; return false; }
            if (width <= 0 || height <= 0) { error = "invalid ppm dimensions"; return false; }

            var rgbBytes = width * height * 3;
            if (data.Length - index < rgbBytes) { error = "ppm payload is truncated"; return false; }
            bgra = new byte[targetStride * targetHeight];
            for (var y = 0; y < targetHeight; y++)
            {
                var srcY = Math.Min(height - 1, y * height / targetHeight);
                for (var x = 0; x < targetWidth; x++)
                {
                    var srcX = Math.Min(width - 1, x * width / targetWidth);
                    var src = index + (srcY * width + srcX) * 3;
                    var dst = y * targetStride + x * 4;
                    bgra[dst + 0] = data[src + 2];
                    bgra[dst + 1] = data[src + 1];
                    bgra[dst + 2] = data[src + 0];
                    bgra[dst + 3] = 0xFF;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ScanoutObservation AnalyzeBgraFrame(byte[] bgra, int width, int height, int stride)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0 || stride < width * 4)
        {
            return new ScanoutObservation(
                "invalid",
                "scanout.pending",
                42,
                "QMP scanout frame is structurally invalid; Android UI is not ready",
                "invalid scanout frame");
        }

        var sampleStepX = Math.Max(1, width / 160);
        var sampleStepY = Math.Max(1, height / 90);
        var samples = 0;
        var unique = new HashSet<int>();
        long luminanceTotal = 0;
        var bright = 0;
        var dark = 0;
        var colorish = 0;

        for (var y = 0; y < height; y += sampleStepY)
        {
            for (var x = 0; x < width; x += sampleStepX)
            {
                var offset = y * stride + x * 4;
                if (offset + 2 >= bgra.Length) continue;
                var b = bgra[offset + 0];
                var g = bgra[offset + 1];
                var r = bgra[offset + 2];
                var lum = (r * 299 + g * 587 + b * 114) / 1000;
                unique.Add((r >> 4) << 8 | (g >> 4) << 4 | (b >> 4));
                luminanceTotal += lum;
                samples++;
                if (lum < 20) dark++;
                if (lum > 160) bright++;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) colorish++;
            }
        }

        if (samples == 0)
        {
            return new ScanoutObservation("invalid", "scanout.pending", 42, "QMP scanout frame has no readable samples", "no readable scanout samples");
        }

        var mean = luminanceTotal / samples;
        var darkRatio = dark / (double)samples;
        var brightRatio = bright / (double)samples;
        var colorRatio = colorish / (double)samples;
        var summary = $"scanout samples={samples}, colors={unique.Count}, meanLuma={mean}, dark={darkRatio:P0}, bright={brightRatio:P0}, color={colorRatio:P0}";
        var isVisible = bright > 8 || unique.Count > 4 || darkRatio < 0.98;

        if (!isVisible)
        {
            return new ScanoutObservation(
                "blank-or-gray",
                "android.ui.pending",
                43,
                $"Live scanout is currently blank/gray; Android UI is not ready yet ({summary})",
                summary,
                false);
        }

        if (unique.Count <= 4 && darkRatio > 0.85)
        {
            return new ScanoutObservation(
                "boot-console",
                "android.ui.pending",
                43,
                $"Live scanout is still low-color boot/console content; Android UI is not ready yet ({summary})",
                summary,
                true);
        }

        if (unique.Count < 16 && colorRatio < 0.05)
        {
            return new ScanoutObservation(
                "boot-console",
                "android.ui.pending",
                43,
                $"Live scanout looks like bootloader/initramfs console rather than Android UI; waiting for Android userspace/ADB ({summary})",
                summary,
                true);
        }

        return new ScanoutObservation(
            "display-changing",
            "scanout.frame_observed",
            44,
            $"Live scanout has non-trivial visual content; waiting for Android userspace readiness/ADB before declaring Android ready ({summary})",
            summary,
            true);
    }

    private static async Task<RendererProbeResult> ProbeRendererTransportAsync(string? endpointArg, StreamWriter log)
    {
        if (string.IsNullOrWhiteSpace(endpointArg))
        {
            await EmitAsync(log, new RuntimeEvent("renderer.pending", 88, "Native render host controller not attached yet"));
            await EmitAsync(log, new RuntimeEvent("transport.pending", 90, "GPU/frame transport endpoint not attached yet"));
            await EmitAsync(log, new RuntimeEvent("first_frame.pending", 92, "First frame pending until native renderer transport produces a frame"));
            return RendererProbeResult.Pending;
        }

        string endpointText;
        try
        {
            endpointText = await ResolveEndpointTextAsync(endpointArg);
        }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("transport.invalid", 90, $"Renderer endpoint could not be read: {ex.Message}"));
            await EmitAsync(log, new RuntimeEvent("renderer.pending", 91, "Native renderer is waiting for a readable shm-frame-ring-v1 endpoint"));
            await EmitAsync(log, new RuntimeEvent("first_frame.pending", 92, "First frame pending until a readable native transport is attached"));
            return RendererProbeResult.Pending;
        }

        var producerEndpoint = IsGpuProducerEndpoint(endpointText);
        await EmitAsync(log, producerEndpoint
            ? new RuntimeEvent("gpu_producer.pending", 86, "GPU producer endpoint wrapper detected; validating producer contract")
            : new RuntimeEvent("gpu_producer.pending", 86, "Legacy shm-frame-ring producer endpoint detected"));

        var validation = ValidateRendererEndpoint(endpointText);
        if (!validation.Valid)
        {
            await EmitAsync(log, new RuntimeEvent("gpu_producer.invalid", 89, validation.Message));
            await EmitAsync(log, new RuntimeEvent("transport.invalid", 90, validation.Message));
            await EmitAsync(log, new RuntimeEvent("renderer.pending", 91, "Native renderer is waiting for a valid shm-frame-ring-v1 endpoint"));
            await EmitAsync(log, new RuntimeEvent("first_frame.pending", 92, "First frame pending until a valid native transport is attached"));
            return RendererProbeResult.Pending;
        }

        if (validation.Message.Contains("virtio-gpu-gfxstream-scanout-import", StringComparison.OrdinalIgnoreCase))
        {
            await EmitAsync(log, new RuntimeEvent("gpu_producer.configured", 89, "Virtio-gpu/gfxstream scanout-import producer accepted"));
            await EmitAsync(log, new RuntimeEvent("gfxstream.bridge.configured", 90, validation.Message));
            await EmitAsync(log, new RuntimeEvent("transport.attached", 91, "Live virtio-gpu/gfxstream scanout-import frame transport attached"));
            await EmitAsync(log, new RuntimeEvent("renderer.ready", 93, "Native renderer accepted live virtio-gpu/gfxstream scanout-import transport"));
            await EmitAsync(log, new RuntimeEvent("first_frame.ready", 96, "First live virtio-gpu/gfxstream scanout-import frame is available to the native renderer"));
            return RendererProbeResult.Ready;
        }

        if (validation.Message.Contains("virtio-gpu-gfxstream", StringComparison.OrdinalIgnoreCase))
        {
            await EmitAsync(log, new RuntimeEvent("gpu_producer.configured", 89, "Virtio-gpu/gfxstream producer bridge accepted"));
            await EmitAsync(log, new RuntimeEvent("gfxstream.bridge.configured", 90, validation.Message));
            await EmitAsync(log, new RuntimeEvent("transport.attached", 91, "Gfxstream command transport bridge configured; waiting for guest scanout frames"));
            await EmitAsync(log, new RuntimeEvent("renderer.pending", 92, "Native renderer is waiting for gfxstream scanout import support"));
            await EmitAsync(log, new RuntimeEvent("first_frame.pending", 93, "First frame pending until guest virtio-gpu/gfxstream produces a scanout"));
            return RendererProbeResult.Pending;
        }

        await EmitAsync(log, new RuntimeEvent("gpu_producer.configured", 89,
            producerEndpoint ? "GPU producer wrapper accepted with shm-bgra-frame-ring payload" : "Legacy shm-frame-ring producer accepted"));
        await EmitAsync(log, new RuntimeEvent("transport.attached", 90, validation.Message));
        await EmitAsync(log, new RuntimeEvent("renderer.ready", 93, "Native renderer accepted BGRA frame-ring transport"));
        await EmitAsync(log, new RuntimeEvent("first_frame.ready", 96, "First BGRA frame-ring scanout frame is available to the native renderer"));
        return RendererProbeResult.Ready;
    }

    private static async Task<int> RunSyntheticFrameProducerAsync(RuntimeOptions options, StreamWriter log)
    {
        if (string.IsNullOrWhiteSpace(options.RendererEndpoint))
        {
            await EmitAsync(log, new RuntimeEvent("producer.invalid", 100, "Synthetic producer requires --renderer-endpoint with a shm-frame-ring-v1 or revm-gpu-producer-v1 endpoint"));
            return 2;
        }

        string endpointText;
        try
        {
            endpointText = await ResolveEndpointTextAsync(options.RendererEndpoint);
        }
        catch (Exception ex)
        {
            await EmitAsync(log, new RuntimeEvent("producer.invalid", 100, $"Synthetic producer endpoint could not be read: {ex.Message}"));
            return 2;
        }

        var validation = ValidateRendererEndpoint(endpointText);
        if (!validation.Valid && !validation.Message.StartsWith("Renderer endpoint ring image missing:", StringComparison.OrdinalIgnoreCase))
        {
            await EmitAsync(log, new RuntimeEvent("producer.invalid", 100, validation.Message));
            return 2;
        }

        if (!TryExtractFrameEndpoint(endpointText, out var frameEndpoint, out var error))
        {
            await EmitAsync(log, new RuntimeEvent("producer.invalid", 100, error));
            return 2;
        }

        var ringImagePath = StringProp(frameEndpoint, "ringImagePath");
        if (string.IsNullOrWhiteSpace(ringImagePath))
        {
            await EmitAsync(log, new RuntimeEvent("producer.invalid", 100, "Synthetic producer requires frameEndpoint.ringImagePath for the deterministic file-backed ring"));
            return 2;
        }

        var width = IntProp(frameEndpoint, "width");
        var height = IntProp(frameEndpoint, "height");
        var stride = IntProp(frameEndpoint, "stride");
        var slots = IntProp(frameEndpoint, "slots");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ringImagePath)) ?? ".");

        await EmitAsync(log, new RuntimeEvent("producer.created", 12,
            $"Synthetic shm-frame-ring-v1 producer attached: {width}x{height}, stride={stride}, slots={slots}"));

        for (var frameId = 1; frameId <= options.SyntheticProducerFrames && !Shutdown.IsCancellationRequested; frameId++)
        {
            var readyIndex = (frameId - 1) % slots;
            WriteBgraRingImage(ringImagePath, width, height, stride, slots, frameId, readyIndex,
                CreateSyntheticBgraPayload(width, height, stride, frameId));
            await EmitAsync(log, new RuntimeEvent("producer.frame_written", Math.Min(95, 12 + frameId * 80 / Math.Max(1, options.SyntheticProducerFrames)),
                $"Synthetic BGRA frame {frameId} written to slot {readyIndex}"));
            if (options.SyntheticProducerIntervalMs > 0 && frameId < options.SyntheticProducerFrames)
            {
                try { await Task.Delay(options.SyntheticProducerIntervalMs, Shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        await EmitAsync(log, new RuntimeEvent("producer.stopped", 100, "Bounded host-owned synthetic frame producer stopped"));
        return 0;
    }

    private static bool TryExtractFrameEndpoint(string endpointText, out JsonElement frameEndpoint, out string error)
    {
        frameEndpoint = default;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(endpointText);
            var root = doc.RootElement;
            var candidate = root;
            if (StringProp(root, "kind").Equals("revm-gpu-producer-v1", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("frameEndpoint", out candidate) || candidate.ValueKind != JsonValueKind.Object)
                {
                    error = "GPU producer endpoint must wrap frameEndpoint for the synthetic producer.";
                    return false;
                }
            }

            if (!StringProp(candidate, "kind").Equals("shm-frame-ring-v1", StringComparison.OrdinalIgnoreCase))
            {
                error = "Synthetic producer only writes shm-frame-ring-v1 frame endpoints.";
                return false;
            }

            frameEndpoint = candidate.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Synthetic producer endpoint JSON is invalid: {ex.Message}";
            return false;
        }
    }

    private static void WriteBgraRingImage(string ringImagePath, int width, int height, int stride, int slots, int frameId, int readyIndex, byte[] bgraPayload)
    {
        const uint frameMagic = 0x4D465652u;
        const uint abiVersion = 1;
        const uint formatBgra8888 = 1;
        const int headerSize = 48;
        const int slotSize = 28;
        var frameBytes = checked(stride * height);
        if (bgraPayload.Length != frameBytes)
            throw new InvalidDataException($"BGRA payload length {bgraPayload.Length} does not match frame bytes {frameBytes}.");
        var payloadStart = checked(headerSize + slots * slotSize);
        var totalBytes = checked(payloadStart + frameBytes * slots);
        var qpcProduced = Stopwatch.GetTimestamp();

        using var stream = new FileStream(ringImagePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(totalBytes);
        stream.Position = 0;
        using var writer = new BinaryWriter(stream);
        writer.Write(frameMagic);
        writer.Write(abiVersion);
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write((uint)stride);
        writer.Write(formatBgra8888);
        writer.Write((ulong)slots);
        writer.Write((ulong)frameId);
        writer.Write((uint)readyIndex);
        writer.Write(0u);

        for (var slot = 0; slot < slots; slot++)
        {
            writer.Write(slot == readyIndex ? 2u : 0u);
            writer.Write((ulong)(slot == readyIndex ? frameId : 0));
            writer.Write((ulong)(slot == readyIndex ? qpcProduced : 0));
            writer.Write((uint)(payloadStart + slot * frameBytes));
            writer.Write((uint)frameBytes);
        }

        for (var slot = 0; slot < slots; slot++)
            writer.Write(bgraPayload);
    }

    private static byte[] CreateSyntheticBgraPayload(int width, int height, int stride, int frameId)
    {
        var payload = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                payload[offset + 0] = (byte)((x + frameId * 17) & 0xFF);
                payload[offset + 1] = (byte)((y + frameId * 29) & 0xFF);
                payload[offset + 2] = (byte)(((x ^ y) + frameId * 43) & 0xFF);
                payload[offset + 3] = 0xFF;
            }
        }
        return payload;
    }

    private static async Task<string> ResolveEndpointTextAsync(string endpointArg)
    {
        var trimmed = endpointArg.Trim();
        if (trimmed.StartsWith('{')) return trimmed;

        var path = Path.GetFullPath(trimmed);
        return await File.ReadAllTextAsync(path);
    }

    private static EndpointValidationResult ValidateRendererEndpoint(string endpointJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            var kind = StringProp(root, "kind");
            return kind.Equals("revm-gpu-producer-v1", StringComparison.OrdinalIgnoreCase)
                ? ValidateGpuProducerEndpoint(root)
                : ValidateShmFrameRingEndpoint(endpointJson);
        }
        catch (Exception ex)
        {
            return EndpointValidationResult.Invalid($"Renderer endpoint JSON is invalid: {ex.Message}");
        }
    }

    private static EndpointValidationResult ValidateGpuProducerEndpoint(JsonElement root)
    {
        var abi = IntProp(root, "abiVersion");
        if (abi != 1) return EndpointValidationResult.Invalid($"GPU producer ABI {abi} is unsupported.");

        var producerKind = StringProp(root, "producerKind");
        var displayMode = StringProp(root, "displayMode");
        if (ContainsBannedDisplayToken(producerKind) || ContainsBannedDisplayToken(displayMode))
            return EndpointValidationResult.Invalid("GPU producer endpoint uses a banned display transport token.");

        if (!root.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
            return EndpointValidationResult.Invalid("GPU producer endpoint must declare capabilities.");

        if (producerKind.Equals("virtio-gpu-gfxstream", StringComparison.OrdinalIgnoreCase))
        {
            if (!BoolProp(caps, "producesGpuCommands") || !BoolProp(caps, "supportsVirtioGpu") || !BoolProp(caps, "supportsGfxstream") ||
                BoolProp(caps, "producesBgraFrames") || BoolProp(caps, "requiresAdb") || BoolProp(caps, "usesEncodedVideo"))
                return EndpointValidationResult.Invalid("Gfxstream producer capabilities must be command-only virtio-gpu/gfxstream without ADB or encoded video.");
            if (!root.TryGetProperty("commandEndpoint", out var commandEndpoint) || commandEndpoint.ValueKind != JsonValueKind.Object)
                return EndpointValidationResult.Invalid("Gfxstream producer endpoint must declare commandEndpoint.");
            if (!StringProp(commandEndpoint, "kind").Equals("gfxstream-command-stream-v1", StringComparison.OrdinalIgnoreCase))
                return EndpointValidationResult.Invalid("Gfxstream command endpoint kind must be gfxstream-command-stream-v1.");

            var backendPath = StringProp(commandEndpoint, "rendererBackendPath");
            if (!string.IsNullOrWhiteSpace(backendPath) && !File.Exists(Path.GetFullPath(backendPath)))
                return EndpointValidationResult.Invalid($"Gfxstream backend library is missing: {Path.GetFullPath(backendPath)}");

            var vmId = StringProp(root, "vmId");
            return EndpointValidationResult.ValidResult($"revm-gpu-producer-v1 attached via virtio-gpu-gfxstream bridge for vmId={vmId}; commandEndpoint=gfxstream-command-stream-v1");
        }

        if (producerKind.Equals("virtio-gpu-gfxstream-scanout-import", StringComparison.OrdinalIgnoreCase))
        {
            if (!BoolProp(caps, "producesBgraFrames") || !BoolProp(caps, "producesGpuCommands") || !BoolProp(caps, "supportsVirtioGpu") || !BoolProp(caps, "supportsGfxstream") ||
                BoolProp(caps, "requiresAdb") || BoolProp(caps, "usesEncodedVideo"))
                return EndpointValidationResult.Invalid("Gfxstream scanout-import capabilities must expose virtio-gpu/gfxstream plus BGRA import without ADB or encoded video.");
            if (!root.TryGetProperty("commandEndpoint", out var commandEndpoint) || commandEndpoint.ValueKind != JsonValueKind.Object)
                return EndpointValidationResult.Invalid("Gfxstream scanout-import producer endpoint must declare commandEndpoint.");
            if (!StringProp(commandEndpoint, "kind").Equals("gfxstream-command-stream-v1", StringComparison.OrdinalIgnoreCase))
                return EndpointValidationResult.Invalid("Gfxstream command endpoint kind must be gfxstream-command-stream-v1.");
            if (!root.TryGetProperty("frameEndpoint", out var scanoutFrameEndpoint) || scanoutFrameEndpoint.ValueKind != JsonValueKind.Object)
                return EndpointValidationResult.Invalid("Gfxstream scanout-import producer endpoint must wrap a frameEndpoint object.");

            var scanoutFrameValidation = ValidateShmFrameRingEndpoint(scanoutFrameEndpoint.GetRawText());
            var vmId = StringProp(root, "vmId");
            return scanoutFrameValidation.Valid
                ? EndpointValidationResult.ValidResult($"revm-gpu-producer-v1 attached via virtio-gpu-gfxstream-scanout-import for vmId={vmId}; commandEndpoint=gfxstream-command-stream-v1; " + scanoutFrameValidation.Message)
                : scanoutFrameValidation;
        }

        if (!producerKind.Equals("shm-bgra-frame-ring", StringComparison.OrdinalIgnoreCase))
            return EndpointValidationResult.Invalid($"GPU producer kind '{producerKind}' is not implemented yet; expected shm-bgra-frame-ring, virtio-gpu-gfxstream, or virtio-gpu-gfxstream-scanout-import.");

        if (!BoolProp(caps, "producesBgraFrames") || BoolProp(caps, "producesGpuCommands") || BoolProp(caps, "requiresAdb") || BoolProp(caps, "usesEncodedVideo"))
            return EndpointValidationResult.Invalid("GPU producer capabilities are incompatible with the current BGRA frame-ring producer contract.");

        if (!root.TryGetProperty("frameEndpoint", out var frameEndpoint) || frameEndpoint.ValueKind != JsonValueKind.Object)
            return EndpointValidationResult.Invalid("GPU producer endpoint must wrap a frameEndpoint object for shm-bgra-frame-ring.");
        var validation = ValidateShmFrameRingEndpoint(frameEndpoint.GetRawText());
        return validation.Valid
            ? EndpointValidationResult.ValidResult("revm-gpu-producer-v1 attached via shm-bgra-frame-ring; " + validation.Message)
            : validation;
    }

    private static EndpointValidationResult ValidateShmFrameRingEndpoint(string endpointJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            if (!StringProp(root, "kind").Equals("shm-frame-ring-v1", StringComparison.OrdinalIgnoreCase))
                return EndpointValidationResult.Invalid("Renderer endpoint kind must be shm-frame-ring-v1.");
            if (!StringProp(root, "format").Equals("BGRA8888", StringComparison.OrdinalIgnoreCase))
                return EndpointValidationResult.Invalid("Renderer endpoint format must be BGRA8888.");

            var width = IntProp(root, "width");
            var height = IntProp(root, "height");
            var stride = IntProp(root, "stride");
            var slots = IntProp(root, "slots");
            if (width <= 0 || height <= 0 || stride < width * 4)
                return EndpointValidationResult.Invalid("Renderer endpoint dimensions/stride are invalid.");
            if (slots is < 1 or > 3)
                return EndpointValidationResult.Invalid("Renderer endpoint slots must be in the shm-frame-ring-v1 range 1..3.");

            var ringImagePath = StringProp(root, "ringImagePath");
            if (!string.IsNullOrWhiteSpace(ringImagePath))
            {
                var ring = ValidateRingImage(ringImagePath, width, height, stride, slots);
                if (!ring.Valid) return ring;
            }

            var vmId = StringProp(root, "vmId");
            return EndpointValidationResult.ValidResult($"shm-frame-ring-v1 attached for vmId={vmId}; {width}x{height}; slots={slots}");
        }
        catch (Exception ex)
        {
            return EndpointValidationResult.Invalid($"Renderer endpoint JSON is invalid: {ex.Message}");
        }
    }

    private static EndpointValidationResult ValidateRingImage(string ringImagePath, int width, int height, int stride, int slots)
    {
        var path = Path.GetFullPath(ringImagePath);
        if (!File.Exists(path))
            return EndpointValidationResult.Invalid($"Renderer endpoint ring image missing: {path}");

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        const int headerSize = 48;
        const int slotSize = 28;
        if (stream.Length < headerSize + slots * slotSize)
            return EndpointValidationResult.Invalid("Renderer endpoint ring image is too small for header and slot table.");

        var magic = reader.ReadUInt32();
        var abi = reader.ReadUInt32();
        var ringWidth = reader.ReadUInt32();
        var ringHeight = reader.ReadUInt32();
        var ringStride = reader.ReadUInt32();
        var format = reader.ReadUInt32();
        var ringSlots = reader.ReadUInt64();
        var lastFrameId = reader.ReadUInt64();
        var readyIndex = reader.ReadUInt32();
        _ = reader.ReadUInt32();

        if (magic != 0x4D465652u) return EndpointValidationResult.Invalid("Renderer endpoint ring image magic is not RVFM.");
        if (abi != 1) return EndpointValidationResult.Invalid($"Renderer endpoint ring ABI {abi} is unsupported.");
        if (format != 1) return EndpointValidationResult.Invalid($"Renderer endpoint ring format {format} is not BGRA8888.");
        if (ringWidth != width || ringHeight != height || ringStride != stride)
            return EndpointValidationResult.Invalid("Renderer endpoint ring header does not match endpoint dimensions.");
        if (ringSlots != (ulong)slots)
            return EndpointValidationResult.Invalid("Renderer endpoint ring header slot count does not match endpoint slots.");
        if (readyIndex >= slots) return EndpointValidationResult.Invalid("Renderer endpoint ring ready_index is outside the slot table.");

        var frameBytes = checked((long)stride * height);
        var payloadStart = checked(headerSize + slots * slotSize);
        for (var slot = 0; slot < slots; slot++)
        {
            var state = reader.ReadUInt32();
            var slotFrameId = reader.ReadUInt64();
            _ = reader.ReadUInt64();
            var dataOffset = reader.ReadUInt32();
            var dataSize = reader.ReadUInt32();

            if (slot != readyIndex) continue;

            if (state != 2) return EndpointValidationResult.Invalid("Renderer endpoint ready slot is not marked ready.");
            if (slotFrameId == 0)
                return EndpointValidationResult.Invalid("Renderer endpoint ready slot has no produced frame_id.");
            if (lastFrameId != 0 && slotFrameId != lastFrameId)
                return EndpointValidationResult.Invalid("Renderer endpoint ready slot frame_id does not match header last_frame_id.");
            if (dataSize != frameBytes)
                return EndpointValidationResult.Invalid("Renderer endpoint ready slot size does not match endpoint dimensions.");
            if ((long)dataOffset < payloadStart || (long)dataOffset + dataSize > stream.Length)
                return EndpointValidationResult.Invalid("Renderer endpoint ready slot payload is outside the ring image.");

            return EndpointValidationResult.ValidResult("");
        }

        return EndpointValidationResult.Invalid("Renderer endpoint ready slot was not found in the slot table.");
    }

    private static string StringProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int IntProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool BoolProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsGpuProducerEndpoint(string endpointJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            return StringProp(doc.RootElement, "kind").Equals("revm-gpu-producer-v1", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool ContainsBannedDisplayToken(string value)
    {
        var banned = new[] { "scrcpy", "adb-display", "adb_display", "sdl", "h264", "h.264", "video", "webrtc", "encoded", "decoder", "encoder" };
        return banned.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> WaitForAdbAsync(string adbExe, string serial, StreamWriter log, TimeSpan timeout)
    {
        var readiness = await WaitForAndroidFrameworkReadyAsync(adbExe, serial, log, timeout, requireFrameworkReady: false);
        return readiness.AdbOnline;
    }

    private static Task<AndroidReadinessResult> WaitForAndroidFrameworkReadyAsync(string adbExe, string serial, StreamWriter log, TimeSpan timeout) =>
        WaitForAndroidFrameworkReadyAsync(adbExe, serial, log, timeout, requireFrameworkReady: true);

    private static async Task<AndroidReadinessResult> WaitForAndroidFrameworkReadyAsync(
        string adbExe,
        string serial,
        StreamWriter log,
        TimeSpan timeout,
        bool requireFrameworkReady)
    {
        var deadline = DateTime.UtcNow + timeout;
        var adbHostPort = TryParseLoopbackPort(serial, 5555);
        await EmitAsync(log, new RuntimeEvent("adb.connect", 52,
            $"ADB control/readiness probe using {serial}; forwarded host port=127.0.0.1:{adbHostPort}; display transport remains native fastpipe"));
        await RunProcessAsync(adbExe, "start-server", log, TimeSpan.FromSeconds(10));
        var connect = await RunProcessAsync(adbExe, $"connect {serial}", log, TimeSpan.FromSeconds(10));
        await EmitAsync(log, new RuntimeEvent("adb.connect", 53, CompactOneLine(connect.Output)));

        var last = AndroidReadinessResult.Missing("missing");
        var stableFrameworkReadySamples = 0;
        while (DateTime.UtcNow < deadline && !Shutdown.IsCancellationRequested)
        {
            var devices = await RunProcessAsync(adbExe, "devices -l", log, TimeSpan.FromSeconds(10));
            var stateLine = TryGetAdbDeviceState(devices.Output, serial, out var matchedStateLine) ? matchedStateLine : "missing";
            if (IsAdbDeviceLineOnline(stateLine))
            {
                var state = await ProbeAndroidFrameworkStateAsync(adbExe, serial, log);
                last = state;
                await EmitAsync(log, new RuntimeEvent("adb.online", 65, state.Summary));

                if (!requireFrameworkReady)
                    return state;

                if (state.FrameworkReady)
                {
                    stableFrameworkReadySamples++;
                    await EmitAsync(log, new RuntimeEvent("android.framework.ready.sample", 86,
                        $"Framework-ready sample {stableFrameworkReadySamples}/2; {state.Summary}"));
                    if (stableFrameworkReadySamples >= 2)
                        return state;
                }
                else
                {
                    stableFrameworkReadySamples = 0;
                    await EmitAsync(log, new RuntimeEvent("android.framework.wait", 74,
                        $"Waiting for sys.boot_completed=1 and running zygote/surfaceflinger; {state.Summary}"));
                }
            }
            else
            {
                stableFrameworkReadySamples = 0;
                var tcpReachable = await ProbeTcpLoopbackAsync(adbHostPort, TimeSpan.FromMilliseconds(750));
                last = AndroidReadinessResult.Missing($"adb_state={stateLine}; host_port_open={tcpReachable}");
                await EmitAsync(log, new RuntimeEvent("adb.wait", 55,
                    $"Waiting for ADB at {serial}; {last.Summary}"));
                connect = await RunProcessAsync(adbExe, $"connect {serial}", log, TimeSpan.FromSeconds(5));
                if (!string.IsNullOrWhiteSpace(connect.Output))
                    await EmitAsync(log, new RuntimeEvent("adb.connect", 56, CompactOneLine(connect.Output)));
            }

            try { await Task.Delay(3000, Shutdown.Token); } catch { break; }
        }

        await EmitAsync(log, new RuntimeEvent("adb.diagnostics", 71,
            $"Android readiness timeout diagnostics for {serial}: {last.Summary}; host_port_open={await ProbeTcpLoopbackAsync(adbHostPort, TimeSpan.FromMilliseconds(750))}"));
        return last;
    }

    private static async Task<AndroidReadinessResult> ProbeAndroidFrameworkStateAsync(string adbExe, string serial, StreamWriter log)
    {
        var boot = await RunProcessAsync(adbExe, $"-s {serial} shell getprop sys.boot_completed", log, TimeSpan.FromSeconds(5));
        var adbd = await RunProcessAsync(adbExe, $"-s {serial} shell getprop init.svc.adbd", log, TimeSpan.FromSeconds(5));
        var zygote = await RunProcessAsync(adbExe, $"-s {serial} shell getprop init.svc.zygote", log, TimeSpan.FromSeconds(5));
        var surfaceFlinger = await RunProcessAsync(adbExe, $"-s {serial} shell getprop init.svc.surfaceflinger", log, TimeSpan.FromSeconds(5));
        var bootValue = CompactAdbValue(boot);
        var adbdValue = CompactAdbValue(adbd);
        var zygoteValue = CompactAdbValue(zygote);
        var surfaceFlingerValue = CompactAdbValue(surfaceFlinger);
        return AndroidReadinessResult.FromProps(serial, bootValue, adbdValue, zygoteValue, surfaceFlingerValue);
    }

    private static string CompactAdbValue(ProcessResult result) =>
        result.ExitCode == -999 ? "<timeout>" : CompactOneLine(result.Output);

    private static bool IsAdbDeviceOnline(string adbDevicesOutput, string serial) =>
        TryGetAdbDeviceState(adbDevicesOutput, serial, out var stateLine) &&
        IsAdbDeviceLineOnline(stateLine);

    private static bool IsAdbDeviceLineOnline(string stateLine)
    {
        var parts = stateLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAdbDeviceState(string adbDevicesOutput, string serial, out string stateLine)
    {
        using var reader = new StringReader(adbDevicesOutput);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(serial, StringComparison.OrdinalIgnoreCase))
            {
                stateLine = trimmed;
                return true;
            }
        }

        stateLine = "";
        return false;
    }

    private static async Task<bool> ProbeTcpLoopbackAsync(int port, TimeSpan timeout)
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(IPAddress.Loopback, port);
            return await Task.WhenAny(connect, Task.Delay(timeout)) == connect && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string CompactOneLine(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static async Task TryInstallAgentAsync(string adbExe, string serial, string agentApk, string agentScript, StreamWriter log)
    {
        var info = new FileInfo(agentApk);
        if (!info.Exists || info.Length < 1024)
        {
            var scriptInfo = new FileInfo(agentScript);
            if (!scriptInfo.Exists || scriptInfo.Length < 64)
            {
                await EmitAsync(log, new RuntimeEvent("agent.pending", 78, "ReVM guest agent APK/script is placeholder/missing; install skipped"));
                return;
            }

            await EmitAsync(log, new RuntimeEvent("agent.install", 78, "Installing ReVM bootstrap guest agent script"));
            var push = await RunProcessAsync(adbExe, $"-s {serial} push \"{agentScript}\" /data/local/tmp/revm-agent.sh", log, TimeSpan.FromSeconds(30));
            if (push.ExitCode != 0)
            {
                await EmitAsync(log, new RuntimeEvent("agent.failed", 82, "ReVM guest agent push failed: " + push.Output));
                return;
            }
            await RunProcessAsync(adbExe, $"-s {serial} shell chmod 755 /data/local/tmp/revm-agent.sh", log, TimeSpan.FromSeconds(10));
            var start = await RunProcessAsync(adbExe, $"-s {serial} shell nohup /data/local/tmp/revm-agent.sh >/data/local/tmp/revm-agent.log 2>&1 &", log, TimeSpan.FromSeconds(10));
            var probe = await RunProcessAsync(adbExe, $"-s {serial} shell cat /data/local/tmp/revm-agent/status.txt", log, TimeSpan.FromSeconds(10));
            await EmitAsync(log, probe.Output.Contains("started")
                ? new RuntimeEvent("agent.ready", 82, "ReVM bootstrap guest agent running")
                : new RuntimeEvent("agent.pending", 82, "ReVM guest agent start issued; heartbeat pending"));
            return;
        }

        var result = await RunProcessAsync(adbExe, $"-s {serial} install -r \"{agentApk}\"", log, TimeSpan.FromSeconds(60));
        await EmitAsync(log, result.ExitCode == 0
            ? new RuntimeEvent("agent.ready", 82, "ReVM guest agent installed")
            : new RuntimeEvent("agent.failed", 82, "ReVM guest agent install failed: " + result.Output));
    }

    private static async Task<ProcessResult> RunProcessAsync(string exe, string args, StreamWriter log, TimeSpan timeout)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();
            var exited = await Task.WhenAny(waitTask, Task.Delay(timeout)) == waitTask;
            if (!exited) { try { proc.Kill(true); } catch { } }
            var output = (await outputTask) + (await errorTask);
            var exitCode = exited ? proc.ExitCode : -999;
            var compactOutput = output.Replace("\r", " ").Replace("\n", " ");
            await WriteLogLineAsync(log, $"process {Path.GetFileName(exe)} {args} => {exitCode} {compactOutput}");
            return new ProcessResult(exitCode, output);
        }
        catch (Exception ex)
        {
            await WriteLogLineAsync(log, $"process error {exe} {args}: {ex}");
            return new ProcessResult(-998, ex.Message);
        }
    }

    private static async Task KeepFastpipeHostAliveAsync(StreamWriter log, bool started)
    {
        while (!Shutdown.IsCancellationRequested)
        {
            string heartbeat;
            if (!started)
                heartbeat = "VMM bootstrap alive; REPlayer fastpipe host failed before guest device readiness";
            else if (_fastpipeHost is not { HasExited: false })
                heartbeat = $"REPlayer fastpipe host exited code={SafeExitCode(_fastpipeHost)}";
            else if (_qemu is { HasExited: false })
                heartbeat = "REPlayer fastpipe host alive; Android VM alive with guest virtio-fastpipe ports attached";
            else
                heartbeat = "REPlayer fastpipe host alive; Android VM is not running";

            await EmitAsync(log, new RuntimeEvent("runtime.heartbeat", 100, heartbeat));
            try { await Task.Delay(TimeSpan.FromSeconds(5), Shutdown.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task KeepAliveAsync(StreamWriter log, string heartbeat)
    {
        while (!Shutdown.IsCancellationRequested)
        {
            await EmitAsync(log, new RuntimeEvent("runtime.heartbeat", 100, heartbeat));
            try { await Task.Delay(TimeSpan.FromSeconds(5), Shutdown.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task RunPipeServerAsync(string pipeName, StreamWriter log, CancellationToken ct)
    {
        await EmitAsync(log, new RuntimeEvent("ipc.start", 8, $"Named pipe listening: {pipeName}"));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var command = await reader.ReadLineAsync(ct) ?? string.Empty;
                await EmitAsync(log, new RuntimeEvent("ipc.command", 10, command));
                if (command.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("ok:shutdown");
                    Shutdown.Cancel();
                }
                else if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync(_qemu is { HasExited: false } ? "ok:qemu-alive" : "ok:qemu-not-running");
                }
                else await writer.WriteLineAsync("error:unknown-command");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await EmitAsync(log, new RuntimeEvent("ipc.error", 10, ex.Message));
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private static async Task<ManifestLoadResult> ValidateManifestFromOptionsAsync(RuntimeOptions options, StreamWriter log)
    {
        if (!File.Exists(options.ManifestPath))
        {
            await EmitAsync(log, new RuntimeEvent("runtime.error", 100, $"Manifest not found: {options.ManifestPath}"));
            return ManifestLoadResult.Failed(2);
        }

        var manifest = await LoadManifestAsync(options.ManifestPath);
        var engineRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(options.ManifestPath)!, ".."));
        var runtimeRoot = Path.GetFullPath(Path.Combine(engineRoot, ".."));
        var manifestValidation = ValidateManifest(manifest, engineRoot);
        if (!manifestValidation.Valid)
        {
            await EmitAsync(log, new RuntimeEvent("runtime.manifest.invalid", 100, manifestValidation.Error));
            return ManifestLoadResult.Failed(2);
        }

        await EmitAsync(log, new RuntimeEvent("runtime.manifest", 10, $"engine={manifest.EngineKind}; arch={manifest.AndroidArch}; display={manifest.DisplayMode}"));
        return ManifestLoadResult.Valid(manifest, engineRoot, runtimeRoot);
    }

    private static async Task<RuntimeManifest> LoadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RuntimeManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RuntimeManifest();
    }

    private static ManifestValidationResult ValidateManifest(RuntimeManifest manifest, string engineRoot)
    {
        var allowedEngines = new[] { "revm-managed-bootstrap", "revm-dev-scaffold", "revm-gfxstream" };
        if (string.IsNullOrWhiteSpace(manifest.EngineKind) ||
            !allowedEngines.Contains(manifest.EngineKind.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return ManifestValidationResult.Invalid(
                $"Runtime manifest engineKind '{manifest.EngineKind}' is not one of: {string.Join(", ", allowedEngines)}.");
        }

        if (!string.Equals(manifest.AndroidArch, "x86_64", StringComparison.OrdinalIgnoreCase))
            return ManifestValidationResult.Invalid("Runtime manifest androidArch must be x86_64 for the native renderer backend.");

        var required = new Dictionary<string, string?>
        {
            ["vmmPath"] = manifest.VmmPath,
            ["rendererPath"] = manifest.RendererPath,
            ["systemImage"] = manifest.SystemImage,
            ["dataImageTemplate"] = manifest.DataImageTemplate,
            ["guestAgentApk"] = manifest.GuestAgentApk
        };

        foreach (var (name, rel) in required)
        {
            if (string.IsNullOrWhiteSpace(rel))
                return ManifestValidationResult.Invalid($"Runtime manifest must declare {name}.");

            var full = Path.GetFullPath(Path.Combine(engineRoot, rel));
            if (!full.StartsWith(engineRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full, engineRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ManifestValidationResult.Invalid($"Runtime manifest {name} escapes the runtime engine root: {rel}");
            }

            if (!File.Exists(full))
                return ManifestValidationResult.Invalid($"Runtime manifest component missing for {name}: {rel}");
        }

        if (!ValidateDisplayContract(manifest.DisplayMode, out var displayError))
            return ManifestValidationResult.Invalid(displayError);

        if (!string.IsNullOrWhiteSpace(manifest.AdbMode) &&
            !new[] { "tcp", "disabled" }.Contains(manifest.AdbMode.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return ManifestValidationResult.Invalid("Runtime manifest adbMode must be 'tcp' or 'disabled'; ADB is control/readiness only, never display transport.");
        }

        return ManifestValidationResult.ValidResult;
    }

    private static bool ValidateDisplayContract(string? displayMode, out string error)
    {
        error = "";
        var mode = (displayMode ?? "").Trim();
        if (mode.Length == 0)
        {
            error = "Runtime manifest must declare displayMode.";
            return false;
        }

        var bannedTokens = new[] { "scrcpy", "adb", "sdl", "video", "h264", "h.264", "encoder", "encoded", "webrtc", "decoder" };
        foreach (var token in bannedTokens)
        {
            if (mode.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Runtime manifest displayMode '{mode}' is rejected: display must not depend on {token}.";
                return false;
            }
        }

        var allowedModes = new[]
        {
            "native-hwnd-gfxstream",
            "native-hwnd-gfxstream-dev",
            "native-hwnd-gfxstream-host",
            "native-hwnd-qmp-scanout",
            "native-hwnd-shm-ring",
            "native-hwnd-virtio-gpu-gfxstream"
        };

        if (!allowedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Runtime manifest displayMode '{mode}' is not one of: {string.Join(", ", allowedModes)}.";
            return false;
        }

        return true;
    }

    private static void StopQemu()
    {
        try { if (_qemu is { HasExited: false }) _qemu.Kill(entireProcessTree: true); } catch { }
        try { if (_fastpipeHost is { HasExited: false }) _fastpipeHost.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task EmitAsync(StreamWriter log, RuntimeEvent ev)
    {
        var json = JsonSerializer.Serialize(ev);
        await EmitLock.WaitAsync();
        try
        {
            Console.WriteLine(json);
            await log.WriteLineAsync($"[{Stamp()}] {json}");
        }
        finally
        {
            EmitLock.Release();
        }
    }

    private static async Task WriteLogLineAsync(StreamWriter log, string message)
    {
        await EmitLock.WaitAsync();
        try
        {
            await log.WriteLineAsync($"[{Stamp()}] {message}");
        }
        finally
        {
            EmitLock.Release();
        }
    }

    private static int SafeExitCode(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -999; } }
    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
}

internal sealed record RuntimeEvent(string Type, int Progress, string Message);
internal sealed record BootAssets(string QemuExe, string AndroidDisk, string Kernel, string Ramdisk, string AdbExe, string AgentApk, string AgentScript,
    bool CanAttemptBoot, string BlockReason, List<string> Messages);
internal sealed record ProcessResult(int ExitCode, string Output);
internal sealed record AndroidReadinessResult(
    bool AdbOnline,
    bool FrameworkReady,
    string SysBootCompleted,
    string Adbd,
    string Zygote,
    string SurfaceFlinger,
    string Summary)
{
    public static AndroidReadinessResult Missing(string summary) =>
        new(false, false, "<unknown>", "<unknown>", "<unknown>", "<unknown>", summary);

    public static AndroidReadinessResult FromProps(string serial, string bootCompleted, string adbd, string zygote, string surfaceFlinger)
    {
        static bool Running(string value) => value.Equals("running", StringComparison.OrdinalIgnoreCase);
        var frameworkReady = bootCompleted == "1" && Running(zygote) && Running(surfaceFlinger);
        return new AndroidReadinessResult(
            true,
            frameworkReady,
            bootCompleted,
            adbd,
            zygote,
            surfaceFlinger,
            $"{serial} state=device; sys.boot_completed={bootCompleted}; init.svc.adbd={adbd}; init.svc.zygote={zygote}; init.svc.surfaceflinger={surfaceFlinger}");
    }
}
internal sealed record ScanoutObservation(string State, string EventType, int Progress, string Message, string Summary, bool IsVisible = true);
internal sealed record RendererProbeResult(bool RendererReady, bool FirstFrameReady)
{
    public static RendererProbeResult Pending { get; } = new(false, false);
    public static RendererProbeResult Ready { get; } = new(true, true);
}

internal sealed record QemuStartResult(bool Started, string GraphicsMode);

internal sealed record QemuGraphicsPlan(
    string Mode,
    string DeviceArg,
    string DisplayArg,
    bool IsGuestGl,
    int StartupGraceMs,
    int PostQmpStableMs,
    string Description);

internal sealed record EndpointValidationResult(bool Valid, string Message)
{
    public static EndpointValidationResult ValidResult(string message) => new(true, message);
    public static EndpointValidationResult Invalid(string error) => new(false, error);
}

internal sealed record ManifestLoadResult(RuntimeManifest? Manifest, string? EngineRoot, string? RuntimeRoot, int? ExitCode)
{
    public static ManifestLoadResult Valid(RuntimeManifest manifest, string engineRoot, string runtimeRoot) =>
        new(manifest, engineRoot, runtimeRoot, null);

    public static ManifestLoadResult Failed(int exitCode) => new(null, null, null, exitCode);
}

internal sealed record ManifestValidationResult(bool Valid, string Error)
{
    public static ManifestValidationResult ValidResult { get; } = new(true, "");
    public static ManifestValidationResult Invalid(string error) => new(false, error);
}

internal sealed class RuntimeManifest
{
    public string RuntimeVersion { get; set; } = "";
    public string AndroidArch { get; set; } = "x86_64";
    public string EngineKind { get; set; } = "revm-gfxstream";
    public string VmmPath { get; set; } = "bin/revm-vmm.exe";
    public string FastpipeHostPath { get; set; } = "bin/revm-fastpipe-host.exe";
    public string RendererPath { get; set; } = "bin/revm-render-host.dll";
    public string SystemImage { get; set; } = "images/android-x86_64-system.img";
    public string DataImageTemplate { get; set; } = "images/android-x86_64-data-template.img";
    public string GuestAgentApk { get; set; } = "guest/revm-agent.apk";
    public string AdbMode { get; set; } = "tcp";
    public string DisplayMode { get; set; } = "native-hwnd-gfxstream";
}

internal sealed class RuntimeOptions
{
    public string ManifestPath { get; init; } = "runtime.json";
    public string LogDir { get; init; } = "logs";
    public string PipeName { get; init; } = $"revm-vmm-{Environment.ProcessId}";
    public string AdbSerial { get; init; } = "127.0.0.1:5555";
    public int AdbTimeoutSeconds { get; init; } = 120;
    public int CpuCount { get; init; } = 4;
    public int RamMb { get; init; } = 4096;
    public int QmpPort { get; init; } = 4444;
    public string? RendererEndpoint { get; init; }
    public bool ValidateRendererEndpointOnly { get; init; }
    public bool ValidateManifestOnly { get; init; }
    public bool ValidateAfterSyntheticProducer { get; init; }
    public bool ProbeQmpScanoutOnce { get; init; }
    public int SyntheticProducerFrames { get; init; }
    public int SyntheticProducerIntervalMs { get; init; }
    public string QemuGraphicsMode { get; init; } = "auto";
    public string RendererApi { get; init; } = "vulkan";

    public static RuntimeOptions Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }
        bool Has(string name) => args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
        int GetInt(string name, int fallback) => int.TryParse(Get(name, fallback.ToString()), out var v) ? v : fallback;

        return new RuntimeOptions
        {
            ManifestPath = Path.GetFullPath(Get("--manifest", "runtime.json")),
            LogDir = Path.GetFullPath(Get("--log-dir", "logs")),
            PipeName = Get("--pipe", $"revm-vmm-{Environment.ProcessId}"),
            AdbSerial = Get("--adb-serial", "127.0.0.1:5555"),
            AdbTimeoutSeconds = GetInt("--adb-timeout", 120),
            CpuCount = GetInt("--cpu", 4),
            RamMb = GetInt("--ram", 4096),
            QmpPort = GetInt("--qmp-port", 4444),
            RendererEndpoint = Get("--renderer-endpoint", ""),
            ValidateRendererEndpointOnly = Has("--validate-renderer-endpoint"),
            ValidateManifestOnly = Has("--validate-manifest-only"),
            ValidateAfterSyntheticProducer = Has("--validate-after-synthetic-producer"),
            ProbeQmpScanoutOnce = Has("--probe-qmp-scanout-once"),
            SyntheticProducerFrames = Math.Max(0, GetInt("--synthetic-producer-frames", 0)),
            SyntheticProducerIntervalMs = Math.Max(0, GetInt("--synthetic-producer-interval-ms", 0)),
            QemuGraphicsMode = Get("--qemu-graphics", "auto"),
            RendererApi = Get("--renderer-api", "vulkan")
        };
    }
}
