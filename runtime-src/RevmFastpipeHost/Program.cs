using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RevmFastpipeAbi;

namespace RevmFastpipeHost;

internal static class Program
{
    internal const uint Magic = RevmFastpipeProtocol.Magic;
    internal const int AbiVersion = RevmFastpipeProtocol.AbiVersion;
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly SemaphoreSlim LogLock = new(1, 1);

    private static async Task<int> Main(string[] args)
    {
        var options = FastpipeOptions.Parse(args);
        Directory.CreateDirectory(options.LogDir);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown.Cancel(); };

        if (options.SelfTest)
            return await RunSelfTestAsync(options);

        using var log = CreateLog(options.LogDir);
        var endpoint = FastpipeEndpoint.Create(options);
        WriteEndpoint(options.EndpointOut, endpoint);
        await EmitAsync(log, new FastpipeEvent("fastpipe.host.start", 5,
            $"REPlayer fastpipe host starting vmId={endpoint.VmId}; rendererApi={endpoint.RendererApi}; qmpFallback=false"));
        await EmitAsync(log, new FastpipeEvent("fastpipe.endpoint.ready", 20, JsonSerializer.Serialize(endpoint)));

        using var shared = FastpipeSharedMemory.Create(endpoint, log);
        await EmitAsync(log, new FastpipeEvent("fastpipe.shared_memory.ready", 30,
            $"mapping={endpoint.SharedMemory.MappingName}; bytes={endpoint.SharedMemory.Bytes}; queues={endpoint.Queues.Count}"));

        using var frameTarget = FastpipeFrameTarget.TryCreate(options.RendererEndpoint, log, out var frameTargetMessage);
        if (frameTarget is not null)
            await EmitAsync(log, new FastpipeEvent("fastpipe.frame_target.ready", 48, frameTargetMessage));
        else
            await EmitAsync(log, new FastpipeEvent("fastpipe.frame_target.pending", 48, frameTargetMessage));

        var tasks = new List<Task>
        {
            RunControlPipeAsync(endpoint, shared, log, Shutdown.Token),
            RunDataPipeAsync(endpoint, shared, frameTarget, log, Shutdown.Token)
        };
        if (endpoint.VirtioSerial.ControlTcpPort > 0)
            tasks.Add(RunControlTcpAsync(endpoint, shared, log, Shutdown.Token));
        if (endpoint.VirtioSerial.DataTcpPort > 0)
            tasks.Add(RunDataTcpAsync(endpoint, shared, frameTarget, log, Shutdown.Token));
        await EmitAsync(log, new FastpipeEvent("fastpipe.host.ready", 40,
            $"Host fastpipe device service is live; waiting for guest virtio/fastpipe device connection; virtioSerial={endpoint.VirtioSerial.ControlName}:{endpoint.VirtioSerial.ControlTcpPort},{endpoint.VirtioSerial.DataName}:{endpoint.VirtioSerial.DataTcpPort}"));

        while (!Shutdown.IsCancellationRequested)
        {
            await EmitAsync(log, new FastpipeEvent("fastpipe.heartbeat", 100, shared.StatusText));
            try { await Task.Delay(TimeSpan.FromSeconds(5), Shutdown.Token); }
            catch (OperationCanceledException) { break; }
        }

        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        await EmitAsync(log, new FastpipeEvent("fastpipe.host.stop", 100, "REPlayer fastpipe host stopped"));
        return 0;
    }

    private static StreamWriter CreateLog(string logDir) => new(new FileStream(
        Path.Combine(logDir, "revm-fastpipe-host.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

    private static void WriteEndpoint(string endpointOut, FastpipeEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpointOut)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(endpointOut))!);
        File.WriteAllText(endpointOut, JsonSerializer.Serialize(endpoint, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunControlPipeAsync(FastpipeEndpoint endpoint, FastpipeSharedMemory shared, StreamWriter log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(endpoint.ControlPipe.Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                await EmitAsync(log, new FastpipeEvent("fastpipe.control.connected", 45, endpoint.ControlPipe.Name));
                await HandleControlStreamAsync(pipe, endpoint, shared, log, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await EmitAsync(log, new FastpipeEvent("fastpipe.control.error", 45, ex.Message));
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    private static async Task RunDataPipeAsync(FastpipeEndpoint endpoint, FastpipeSharedMemory shared, FastpipeFrameTarget? frameTarget, StreamWriter log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(endpoint.DataPipe.Name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                await EmitAsync(log, new FastpipeEvent("fastpipe.data.connected", 45, endpoint.DataPipe.Name));
                await HandleDataStreamAsync(pipe, endpoint, shared, frameTarget, log, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await EmitAsync(log, new FastpipeEvent("fastpipe.data.error", 60, ex.Message));
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    private static async Task RunControlTcpAsync(FastpipeEndpoint endpoint, FastpipeSharedMemory shared, StreamWriter log, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, endpoint.VirtioSerial.ControlTcpPort);
        listener.Start(1);
        try
        {
            await EmitAsync(log, new FastpipeEvent("fastpipe.control.listen", 42,
                $"127.0.0.1:{endpoint.VirtioSerial.ControlTcpPort} -> /dev/virtio-ports/{endpoint.VirtioSerial.ControlName}"));
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                await EmitAsync(log, new FastpipeEvent("fastpipe.control.connected", 50,
                    $"tcp:{endpoint.VirtioSerial.ControlTcpPort}; guestPort=/dev/virtio-ports/{endpoint.VirtioSerial.ControlName}"));
                await HandleControlStreamAsync(stream, endpoint, shared, log, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await EmitAsync(log, new FastpipeEvent("fastpipe.control.error", 45, ex.Message));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RunDataTcpAsync(FastpipeEndpoint endpoint, FastpipeSharedMemory shared, FastpipeFrameTarget? frameTarget, StreamWriter log, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, endpoint.VirtioSerial.DataTcpPort);
        listener.Start(1);
        try
        {
            await EmitAsync(log, new FastpipeEvent("fastpipe.data.listen", 42,
                $"127.0.0.1:{endpoint.VirtioSerial.DataTcpPort} -> /dev/virtio-ports/{endpoint.VirtioSerial.DataName}"));
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                await EmitAsync(log, new FastpipeEvent("fastpipe.data.connected", 50,
                    $"tcp:{endpoint.VirtioSerial.DataTcpPort}; guestPort=/dev/virtio-ports/{endpoint.VirtioSerial.DataName}"));
                await HandleDataStreamAsync(stream, endpoint, shared, frameTarget, log, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await EmitAsync(log, new FastpipeEvent("fastpipe.data.error", 60, ex.Message));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleControlStreamAsync(Stream stream, FastpipeEndpoint endpoint, FastpipeSharedMemory shared, StreamWriter log, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { AutoFlush = true };
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            var command = ParseCommand(line);
            if (line.StartsWith("diag:", StringComparison.OrdinalIgnoreCase))
            {
                shared.RecordControlMessage();
                await EmitAsync(log, new FastpipeEvent("fastpipe.guest.diag", 76, TruncateForLog(line[5..].Trim(), 900)));
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, diag = true }));
            }
            else if (command == "hello")
            {
                shared.RecordControlMessage();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, kind = "revm-fastpipe-host-v1", endpoint.VmId, endpoint.RendererApi, abiVersion = AbiVersion }));
            }
            else if (command == "status")
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, shared.ControlMessages, shared.DataMessages, shared.BytesReceived, shared.LastDoorbell }));
            }
            else if (command == "shutdown")
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, shuttingDown = true }));
                Shutdown.Cancel();
                break;
            }
            else
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = "unknown command", command }));
            }
        }
    }

    private static async Task HandleDataStreamAsync(Stream stream, FastpipeEndpoint endpoint, FastpipeSharedMemory shared, FastpipeFrameTarget? frameTarget, StreamWriter log, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactlyOrDisconnectAsync(stream, lenBuf, ct)) break;
            var length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (length <= 0 || length > endpoint.Queues.Max(q => q.Bytes))
            {
                await EmitAsync(log, new FastpipeEvent("fastpipe.data.invalid", 60, $"invalid packet length={length}"));
                break;
            }

            var payload = new byte[length];
            if (!await ReadExactlyOrDisconnectAsync(stream, payload, ct)) break;
            shared.WriteDataPacket(payload);
            if (frameTarget is not null && frameTarget.TryConsumePacket(payload, out var frameMessage))
            {
                await EmitAsync(log, new FastpipeEvent("fastpipe.frame_written", 82, frameMessage));
            }
            if (shared.DataMessages <= 3 || shared.DataMessages % 64 == 0)
            {
                await EmitAsync(log, new FastpipeEvent("fastpipe.data.packet", 65,
                    $"packet={shared.DataMessages}; bytes={length}; totalBytes={shared.BytesReceived}; doorbell={shared.LastDoorbell}"));
            }
        }
    }

    private static string TruncateForLog(string value, int max)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static async Task<bool> ReadExactlyOrDisconnectAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static string ParseCommand(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{')) return trimmed.ToLowerInvariant();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.TryGetProperty("cmd", out var cmd) ? (cmd.GetString() ?? "").ToLowerInvariant() : "";
        }
        catch { return ""; }
    }

    private static async Task<int> RunSelfTestAsync(FastpipeOptions baseOptions)
    {
        var root = Path.Combine(baseOptions.LogDir, "selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var endpointPath = Path.Combine(root, "endpoint.json");
        var selfTestVmId = "selftest" + Guid.NewGuid().ToString("N")[..8];
        var controlPort = GetFreeTcpPort();
        var dataPort = GetFreeTcpPort();
        var serverArgs = new[]
        {
            "--vm-id", selfTestVmId, "--renderer-api", baseOptions.RendererApi, "--log-dir", root,
            "--endpoint-out", endpointPath, "--shared-memory-bytes", "1048576",
            "--control-tcp-port", controlPort.ToString(), "--data-tcp-port", dataPort.ToString()
        };
        var assemblyPath = typeof(Program).Assembly.Location;
        var startFile = Environment.ProcessPath!;
        var startArgs = startFile.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || startFile.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { assemblyPath }.Concat(serverArgs).ToArray()
            : serverArgs;
        using var proc = Process.Start(new ProcessStartInfo(startFile, startArgs)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("failed to start selftest server");

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!File.Exists(endpointPath) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            if (!File.Exists(endpointPath)) throw new TimeoutException("endpoint was not written");
            var logPath = Path.Combine(root, "revm-fastpipe-host.log");
            while ((!File.Exists(logPath) || !(await ReadSharedTextAsync(logPath)).Contains("fastpipe.host.ready")) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            if (!File.Exists(logPath) || !(await ReadSharedTextAsync(logPath)).Contains("fastpipe.host.ready"))
                throw new TimeoutException("fastpipe host did not reach ready state");
            var endpoint = JsonSerializer.Deserialize<FastpipeEndpoint>(await File.ReadAllTextAsync(endpointPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            using var control = new TcpClient();
            await control.ConnectAsync(IPAddress.Loopback, controlPort);
            await using (var controlStream = control.GetStream())
            {
                var hello = Encoding.UTF8.GetBytes("hello\n");
                await controlStream.WriteAsync(hello);
                var reply = new byte[512];
                var read = await controlStream.ReadAsync(reply);
                var text = Encoding.UTF8.GetString(reply, 0, read);
                if (!text.Contains("revm-fastpipe-host-v1", StringComparison.Ordinal))
                    throw new InvalidDataException("fastpipe TCP control hello failed: " + text);
            }

            using var data = new TcpClient();
            await data.ConnectAsync(IPAddress.Loopback, dataPort);
            await using (var dataStream = data.GetStream())
            {
                var payload = Encoding.ASCII.GetBytes("gfxstream");
                var len = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
                await dataStream.WriteAsync(len);
                await dataStream.WriteAsync(payload);
                await dataStream.FlushAsync();
            }

            await Task.Delay(250);
            using (var mapping = MemoryMappedFile.OpenExisting(endpoint.SharedMemory.MappingName))
            using (var view = mapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read))
            {
                var magic = view.ReadUInt32(0);
                var abi = view.ReadInt32(4);
                var controlMessages = view.ReadInt64(16);
                var dataMessages = view.ReadInt64(24);
                var bytesReceived = view.ReadInt64(32);
                if (magic != Program.Magic || abi != Program.AbiVersion)
                    throw new InvalidDataException($"bad shared memory header magic=0x{magic:X8} abi={abi}");
                if (controlMessages < 1 || dataMessages < 1 || bytesReceived < 9)
                    throw new InvalidDataException($"TCP virtio bridge counters did not advance: control={controlMessages} data={dataMessages} bytes={bytesReceived}");
            }

            Console.WriteLine("revm fastpipe host self-test passed");
            return 0;
        }
        finally
        {
            try { if (!proc.WaitForExit(3000)) proc.Kill(true); } catch { }
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task EmitAsync(StreamWriter log, FastpipeEvent ev)
    {
        var json = JsonSerializer.Serialize(ev);
        await LogLock.WaitAsync();
        try
        {
            Console.WriteLine(json);
            await log.WriteLineAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {json}");
        }
        finally
        {
            LogLock.Release();
        }
    }

    private static async Task<string> ReadSharedTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}


internal sealed class FastpipeFrameTarget : IDisposable
{
    private const uint FrameMagic = 0x4D465652u;
    private const uint AbiVersion = 1;
    private const uint FormatBgra8888 = 1;
    private const int HeaderSize = 48;
    private const int SlotSize = 28;
    private readonly object _lock = new();
    private ulong _frameId;

    private FastpipeFrameTarget(string ringImagePath, int width, int height, int stride, int slots)
    {
        RingImagePath = ringImagePath;
        Width = width;
        Height = height;
        Stride = stride;
        Slots = slots;
    }

    public string RingImagePath { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int Slots { get; }

    public static FastpipeFrameTarget? TryCreate(string rendererEndpoint, StreamWriter log, out string message)
    {
        message = "renderer endpoint missing; fastpipe packets will be counted but not presented";
        if (string.IsNullOrWhiteSpace(rendererEndpoint)) return null;
        try
        {
            var endpointText = ResolveEndpointText(rendererEndpoint);
            if (!TryExtractFrameEndpoint(endpointText, out var frame, out var error))
            {
                message = error;
                return null;
            }
            var ringImagePath = StringProp(frame, "ringImagePath");
            var width = IntProp(frame, "width");
            var height = IntProp(frame, "height");
            var stride = IntProp(frame, "stride");
            var slots = IntProp(frame, "slots");
            if (string.IsNullOrWhiteSpace(ringImagePath) || width <= 0 || height <= 0 || stride < width * 4 || slots is < 1 or > 3)
            {
                message = "renderer frameEndpoint is invalid for fastpipe scanout import";
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ringImagePath)) ?? ".");
            message = $"fastpipe scanout-import target attached: {width}x{height}, stride={stride}, slots={slots}, ring={ringImagePath}";
            return new FastpipeFrameTarget(ringImagePath, width, height, stride, slots);
        }
        catch (Exception ex)
        {
            message = "renderer endpoint could not be opened: " + ex.Message;
            return null;
        }
    }

    public bool TryConsumePacket(byte[] payload, out string message)
    {
        message = string.Empty;
        byte[] bgra;
        string packetType;
        if (TryDecodeAndroidRawScreencap(payload, Width, Height, Stride, out bgra, out var decodeMessage))
        {
            packetType = "android-raw-screencap";
        }
        else if (IsScanoutImportProbe(payload, out packetType))
        {
            bgra = CreateImportedProbeBgraPayload(Width, Height, Stride, _frameId + 1);
            decodeMessage = "fallback probe frame; real screencap payload unavailable";
        }
        else
        {
            return false;
        }

        lock (_lock)
        {
            _frameId++;
            var readyIndex = (int)((_frameId - 1) % (ulong)Slots);
            WriteBgraRingImage(RingImagePath, Width, Height, Stride, Slots, _frameId, readyIndex, bgra);
            message = $"fastpipe scanout-import frame {_frameId} written to slot {readyIndex}; packetType={packetType}; {decodeMessage}; source=revm.fastpipe.data";
            return true;
        }
    }


    private static bool TryDecodeAndroidRawScreencap(byte[] payload, int targetWidth, int targetHeight, int targetStride, out byte[] bgra, out string message)
    {
        bgra = Array.Empty<byte>();
        message = string.Empty;
        if (payload.Length < 12) return false;
        var srcWidth = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var srcHeight = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var format = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        if (srcWidth <= 0 || srcHeight <= 0 || srcWidth > 8192 || srcHeight > 8192) return false;

        var bytesPerPixel = format switch
        {
            1 => 4, // PIXEL_FORMAT_RGBA_8888
            2 => 4, // RGBX_8888
            3 => 3, // RGB_888
            4 => 2, // RGB_565
            5 => 4, // BGRA_8888 on older Android builds
            _ => 0
        };
        if (bytesPerPixel == 0) return false;

        var srcBytes = checked(srcWidth * srcHeight * bytesPerPixel);
        var header = payload.Length >= 16 + srcBytes ? 16 : 12;
        if (payload.Length < header + srcBytes) return false;

        bgra = new byte[checked(targetStride * targetHeight)];
        var pixels = payload.AsSpan(header, srcBytes);
        for (var y = 0; y < targetHeight; y++)
        {
            var sy = Math.Min(srcHeight - 1, y * srcHeight / targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = Math.Min(srcWidth - 1, x * srcWidth / targetWidth);
                var src = (sy * srcWidth + sx) * bytesPerPixel;
                var dst = y * targetStride + x * 4;
                byte r, g, b;
                switch (format)
                {
                    case 1: // RGBA
                    case 2: // RGBX
                        r = pixels[src + 0]; g = pixels[src + 1]; b = pixels[src + 2];
                        break;
                    case 3: // RGB
                        r = pixels[src + 0]; g = pixels[src + 1]; b = pixels[src + 2];
                        break;
                    case 4: // RGB565 little-endian
                        var v = pixels[src + 0] | (pixels[src + 1] << 8);
                        r = (byte)(((v >> 11) & 0x1F) * 255 / 31);
                        g = (byte)(((v >> 5) & 0x3F) * 255 / 63);
                        b = (byte)((v & 0x1F) * 255 / 31);
                        break;
                    case 5: // BGRA
                        b = pixels[src + 0]; g = pixels[src + 1]; r = pixels[src + 2];
                        break;
                    default:
                        return false;
                }
                bgra[dst + 0] = b;
                bgra[dst + 1] = g;
                bgra[dst + 2] = r;
                bgra[dst + 3] = 0xFF;
            }
        }

        message = $"decoded real Android screencap {srcWidth}x{srcHeight} format={format} header={header} bytes={payload.Length}";
        return true;
    }

    private static bool IsScanoutImportProbe(byte[] payload, out string packetType)
    {
        packetType = string.Empty;
        try
        {
            var text = Encoding.UTF8.GetString(payload);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var kind = StringProp(root, "kind");
            packetType = StringProp(root, "packetType");
            return kind.Equals("revm-gfxstream-command-source-v1", StringComparison.OrdinalIgnoreCase)
                && packetType.Equals("scanout-import-probe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ResolveEndpointText(string endpointArg)
    {
        var trimmed = endpointArg.Trim();
        return trimmed.StartsWith('{') ? trimmed : File.ReadAllText(Path.GetFullPath(trimmed));
    }

    private static bool TryExtractFrameEndpoint(string endpointText, out JsonElement frameEndpoint, out string error)
    {
        frameEndpoint = default;
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(endpointText);
            var root = doc.RootElement;
            var candidate = root;
            if (StringProp(root, "kind").Equals("revm-gpu-producer-v1", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("frameEndpoint", out candidate) || candidate.ValueKind != JsonValueKind.Object)
                {
                    error = "fastpipe scanout-import endpoint must wrap frameEndpoint";
                    return false;
                }
            }
            if (!StringProp(candidate, "kind").Equals("shm-frame-ring-v1", StringComparison.OrdinalIgnoreCase))
            {
                error = "fastpipe frame target requires shm-frame-ring-v1 frameEndpoint";
                return false;
            }
            frameEndpoint = candidate.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = "renderer endpoint JSON invalid: " + ex.Message;
            return false;
        }
    }

    private static void WriteBgraRingImage(string ringImagePath, int width, int height, int stride, int slots, ulong frameId, int readyIndex, byte[] bgraPayload)
    {
        var frameBytes = checked(stride * height);
        if (bgraPayload.Length != frameBytes)
            throw new InvalidDataException($"BGRA payload length {bgraPayload.Length} does not match frame bytes {frameBytes}.");
        var payloadStart = checked(HeaderSize + slots * SlotSize);
        var totalBytes = checked(payloadStart + frameBytes * slots);
        var qpcProduced = (ulong)Stopwatch.GetTimestamp();
        using var stream = new FileStream(ringImagePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length != totalBytes)
        {
            stream.SetLength(totalBytes);
            using var initWriter = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            stream.Position = 0;
            initWriter.Write(FrameMagic);
            initWriter.Write(AbiVersion);
            initWriter.Write((uint)width);
            initWriter.Write((uint)height);
            initWriter.Write((uint)stride);
            initWriter.Write(FormatBgra8888);
            initWriter.Write((ulong)slots);
            initWriter.Write(0UL);
            initWriter.Write(0u);
            initWriter.Write(0u);
            for (var slot = 0; slot < slots; slot++)
            {
                initWriter.Write(0u);
                initWriter.Write(0UL);
                initWriter.Write(0UL);
                initWriter.Write((uint)(payloadStart + slot * frameBytes));
                initWriter.Write((uint)frameBytes);
            }
        }

        var slotOffset = payloadStart + readyIndex * frameBytes;
        stream.Position = slotOffset;
        stream.Write(bgraPayload, 0, bgraPayload.Length);
        stream.Flush(flushToDisk: false);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        // Publish slot metadata after pixels are fully written.
        stream.Position = HeaderSize + readyIndex * SlotSize;
        writer.Write(2u);
        writer.Write(frameId);
        writer.Write(qpcProduced);
        writer.Write((uint)slotOffset);
        writer.Write((uint)frameBytes);

        // Publish header last so consumers never see a new frame id before pixels/slot metadata exist.
        stream.Position = 24;
        writer.Write((ulong)slots);
        writer.Write(frameId);
        writer.Write((uint)readyIndex);
        writer.Write(0u);
        stream.Flush(flushToDisk: false);
    }

    private static byte[] CreateImportedProbeBgraPayload(int width, int height, int stride, ulong frameId)
    {
        var payload = new byte[checked(stride * height)];
        var phase = (int)(frameId % 255);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                var grid = ((x / 32) ^ (y / 32)) & 1;
                payload[o + 0] = (byte)(grid == 0 ? 28 : (x + phase) & 0x7F);
                payload[o + 1] = (byte)(grid == 0 ? 120 : (y + phase * 2) & 0xBF);
                payload[o + 2] = (byte)(grid == 0 ? 78 : 20 + phase % 80);
                payload[o + 3] = 0xFF;
            }
        }
        // Moving white scanline makes it obvious frames are coming from fastpipe packets.
        var line = (int)((frameId * 11) % (ulong)Math.Max(1, height));
        for (var y = Math.Max(0, line - 2); y < Math.Min(height, line + 3); y++)
        for (var x = 0; x < width; x++)
        {
            var o = y * stride + x * 4;
            payload[o + 0] = payload[o + 1] = payload[o + 2] = 230;
            payload[o + 3] = 0xFF;
        }
        return payload;
    }

    private static string StringProp(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;

    private static int IntProp(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) ? value : 0;

    public void Dispose() { }
}

internal sealed class FastpipeSharedMemory : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly object _lock = new();
    private long _writeOffset;

    private FastpipeSharedMemory(FastpipeEndpoint endpoint, MemoryMappedFile mapping, MemoryMappedViewAccessor accessor)
    {
        Endpoint = endpoint;
        _mapping = mapping;
        _accessor = accessor;
        _writeOffset = endpoint.HeaderBytes;
        WriteHeader();
    }

    public FastpipeEndpoint Endpoint { get; }
    public long ControlMessages { get; private set; }
    public long DataMessages { get; private set; }
    public long BytesReceived { get; private set; }
    public long LastDoorbell { get; private set; }
    public string StatusText => $"controlMessages={ControlMessages}; dataMessages={DataMessages}; bytesReceived={BytesReceived}; doorbell={LastDoorbell}";

    public static FastpipeSharedMemory Create(FastpipeEndpoint endpoint, StreamWriter log)
    {
        var mapping = MemoryMappedFile.CreateOrOpen(endpoint.SharedMemory.MappingName, endpoint.SharedMemory.Bytes);
        var accessor = mapping.CreateViewAccessor(0, endpoint.SharedMemory.Bytes, MemoryMappedFileAccess.ReadWrite);
        return new FastpipeSharedMemory(endpoint, mapping, accessor);
    }

    public void RecordControlMessage()
    {
        lock (_lock)
        {
            ControlMessages++;
            LastDoorbell++;
            WriteHeader();
        }
    }

    public void WriteDataPacket(byte[] payload)
    {
        lock (_lock)
        {
            if (_writeOffset + 4 + payload.Length > Endpoint.SharedMemory.Bytes)
                _writeOffset = Endpoint.HeaderBytes;
            _accessor.Write((int)_writeOffset, payload.Length);
            _accessor.WriteArray(_writeOffset + 4, payload, 0, payload.Length);
            _writeOffset += 4 + payload.Length;
            DataMessages++;
            BytesReceived += payload.Length;
            LastDoorbell++;
            WriteHeader();
        }
    }

    private void WriteHeader()
    {
        _accessor.Write(0, Program.Magic);
        _accessor.Write(4, Program.AbiVersion);
        _accessor.Write(8, Endpoint.SharedMemory.Bytes);
        _accessor.Write(16, ControlMessages);
        _accessor.Write(24, DataMessages);
        _accessor.Write(32, BytesReceived);
        _accessor.Write(40, LastDoorbell);
        _accessor.Write(48, _writeOffset);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mapping.Dispose();
    }
}

internal sealed record FastpipeEvent(string Type, int Progress, string Message);

internal sealed record FastpipeEndpoint(
    string Kind,
    int AbiVersion,
    string VmId,
    string RendererStack,
    string RendererApi,
    bool QmpFallbackAllowed,
    int HeaderBytes,
    FastpipeSharedMemoryEndpoint SharedMemory,
    FastpipePipeEndpoint ControlPipe,
    FastpipePipeEndpoint DataPipe,
    FastpipeVirtioSerialEndpoint VirtioSerial,
    IReadOnlyList<FastpipeQueueEndpoint> Queues)
{
    public static FastpipeEndpoint Create(FastpipeOptions options)
    {
        var safeVmId = Sanitize(options.VmId);
        var bytes = Math.Max(options.SharedMemoryBytes, 1024 * 1024);
        return new FastpipeEndpoint(
            RevmFastpipeProtocol.EndpointKind,
            Program.AbiVersion,
            options.VmId,
            RevmFastpipeProtocol.RendererStack,
            string.Equals(options.RendererApi, "opengl", StringComparison.OrdinalIgnoreCase) ? "opengl" : "vulkan",
            false,
            RevmFastpipeProtocol.HeaderBytes,
            new FastpipeSharedMemoryEndpoint($"Local\\RevmFastpipe-{safeVmId}", bytes),
            new FastpipePipeEndpoint($"revm-fastpipe-control-{safeVmId}"),
            new FastpipePipeEndpoint($"revm-fastpipe-data-{safeVmId}"),
            new FastpipeVirtioSerialEndpoint(
                RevmFastpipeProtocol.DefaultControlPort,
                RevmFastpipeProtocol.DefaultDataPort,
                options.ControlTcpPort,
                options.DataTcpPort),
            new[]
            {
                new FastpipeQueueEndpoint("controlq", 0, 1024 * 1024),
                new FastpipeQueueEndpoint("gfxstream-commandq", 1, bytes - 4096),
                new FastpipeQueueEndpoint("host-eventq", 2, 1024 * 1024)
            });
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        return new string(chars).Trim('-').ToLowerInvariant() switch { "" => "default", var s => s };
    }
}

internal sealed record FastpipeSharedMemoryEndpoint(string MappingName, long Bytes);
internal sealed record FastpipePipeEndpoint(string Name);
internal sealed record FastpipeVirtioSerialEndpoint(string ControlName, string DataName, int ControlTcpPort, int DataTcpPort);
internal sealed record FastpipeQueueEndpoint(string Name, int Id, long Bytes);

internal sealed class FastpipeOptions
{
    public string VmId { get; init; } = "default";
    public string RendererApi { get; init; } = "vulkan";
    public string LogDir { get; init; } = "logs";
    public string EndpointOut { get; init; } = "";
    public string RendererEndpoint { get; init; } = "";
    public long SharedMemoryBytes { get; init; } = 64L * 1024L * 1024L;
    public int ControlTcpPort { get; init; }
    public int DataTcpPort { get; init; }
    public bool SelfTest { get; init; }

    public static FastpipeOptions Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }
        bool Has(string name) => args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
        long GetLong(string name, long fallback) => long.TryParse(Get(name, fallback.ToString()), out var value) ? value : fallback;
        int GetInt(string name, int fallback) => int.TryParse(Get(name, fallback.ToString()), out var value) ? value : fallback;
        return new FastpipeOptions
        {
            VmId = Get("--vm-id", "default"),
            RendererApi = Get("--renderer-api", "vulkan"),
            LogDir = Path.GetFullPath(Get("--log-dir", "logs")),
            EndpointOut = Get("--endpoint-out", ""),
            RendererEndpoint = Get("--renderer-endpoint", ""),
            SharedMemoryBytes = GetLong("--shared-memory-bytes", 64L * 1024L * 1024L),
            ControlTcpPort = GetInt("--control-tcp-port", 0),
            DataTcpPort = GetInt("--data-tcp-port", 0),
            SelfTest = Has("--self-test")
        };
    }
}
