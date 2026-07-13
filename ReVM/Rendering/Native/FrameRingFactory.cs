using System;
using System.IO;
using System.Text.Json;

namespace ReVM.NativeRendering;

/// <summary>
/// Deterministic bring-up producer for the REPlayer-owned native display transport.
/// It emits the same clean-room shm-frame-ring-v1 endpoint/schema used by revm-vmm
/// validation, without using scrcpy, ADB display, SDL, or encoded video.
/// </summary>
public static class ShmFrameRingEndpointFactory
{
    private const int QmpStdVgaWidth = 1024;
    private const int QmpStdVgaHeight = 768;
    private const uint FrameMagic = 0x4D465652u; // "RVFM"
    private const uint AbiVersion = 1;
    private const uint FormatBgra8888 = 1;
    private const int DefaultSlots = 3;
    private const int SlotFree = 0;
    private const int SlotReady = 2;
    private const int HeaderSize = 48;
    private const int SlotSize = 28;
    private const long MaxSyntheticBytes = 64L * 1024L * 1024L;

    public static string CreateSyntheticEndpointJson(string vmId, int width, int height, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(vmId)) throw new ArgumentException("VM id is required.", nameof(vmId));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        var stride = checked(width * 4);
        var frameBytes = checked((long)stride * height);
        var totalBytes = checked((long)HeaderSize + SlotSize * DefaultSlots + frameBytes * DefaultSlots);
        if (totalBytes > MaxSyntheticBytes)
            throw new InvalidOperationException($"Synthetic shm-frame-ring-v1 image would be {totalBytes} bytes, above the {MaxSyntheticBytes} byte safety cap.");

        Directory.CreateDirectory(outputDirectory);
        var safeVmId = SanitizeVmId(vmId);
        var ringPath = Path.GetFullPath(Path.Combine(outputDirectory, safeVmId + ".frame-ring.bin"));
        WriteRingImage(ringPath, width, height, stride, frameBytes);

        var endpoint = new
        {
            kind = "shm-frame-ring-v1",
            abiVersion = AbiVersion,
            vmId,
            mappingName = $"Global\\RevmFrameRing-{vmId}",
            readyEventName = $"Global\\RevmFrameReady-{vmId}",
            controlPipeName = $"\\\\.\\pipe\\revm-control-{vmId}",
            width,
            height,
            stride,
            format = "BGRA8888",
            formatCode = FormatBgra8888,
            slots = DefaultSlots,
            ringImagePath = ringPath
        };

        return JsonSerializer.Serialize(endpoint, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string CreateSyntheticGpuProducerEndpointJson(string vmId, int width, int height, string outputDirectory)
    {
        using var frameDoc = JsonDocument.Parse(CreateSyntheticEndpointJson(vmId, width, height, outputDirectory));
        var producer = new
        {
            kind = "revm-gpu-producer-v1",
            abiVersion = AbiVersion,
            vmId,
            producerKind = "shm-bgra-frame-ring",
            displayMode = "native-hwnd-shm-ring",
            width,
            height,
            format = "BGRA8888",
            capabilities = new
            {
                producesBgraFrames = true,
                producesGpuCommands = false,
                supportsHostGpu = false,
                supportsVirtioGpu = false,
                supportsGfxstream = false,
                requiresAdb = false,
                usesEncodedVideo = false
            },
            frameEndpoint = frameDoc.RootElement.Clone()
        };

        return JsonSerializer.Serialize(producer, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string CreateLiveScanoutGpuProducerEndpointJson(string vmId, int width, int height, string outputDirectory, string rendererApi = "vulkan") =>
        // Product display path: guest command source packets arrive on
        // revm.fastpipe.data, the host imports/decodes them into this BGRA frame
        // ring, and the child-HWND presenter scales the fixed Android scanout.
        // QMP screendump is no longer the primary producer for this endpoint.
        CreateFastpipeScanoutImportGpuProducerEndpointJson(vmId, QmpStdVgaWidth, QmpStdVgaHeight, outputDirectory, rendererApi);

    public static string CreateFastpipeScanoutImportGpuProducerEndpointJson(string vmId, int width, int height, string outputDirectory, string rendererApi = "vulkan")
    {
        using var frameDoc = JsonDocument.Parse(CreateEmptyEndpointJson(vmId, width, height, outputDirectory));
        var api = string.Equals(rendererApi, "opengl", StringComparison.OrdinalIgnoreCase) ? "opengl" : "vulkan";
        var producer = new
        {
            kind = "revm-gpu-producer-v1",
            abiVersion = AbiVersion,
            vmId,
            producerKind = "virtio-gpu-gfxstream-scanout-import",
            displayMode = "native-hwnd-fastpipe-scanout-import",
            width,
            height,
            format = "BGRA8888",
            capabilities = new
            {
                producesBgraFrames = true,
                producesGpuCommands = true,
                supportsHostGpu = true,
                supportsVirtioGpu = true,
                supportsGfxstream = true,
                requiresAdb = false,
                usesEncodedVideo = false
            },
            commandEndpoint = new
            {
                kind = "gfxstream-command-stream-v1",
                rendererStack = "gfxstream-fastpipe",
                rendererApi = api,
                sourceDevice = "/dev/revm-gfxstream-commandq",
                dataPort = "/dev/virtio-ports/revm.fastpipe.data"
            },
            frameEndpoint = frameDoc.RootElement.Clone()
        };
        return JsonSerializer.Serialize(producer, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string CreateGfxstreamFastpipeGpuProducerEndpointJson(string vmId, int width, int height, string rendererApi = "vulkan")
    {
        if (string.IsNullOrWhiteSpace(vmId)) throw new ArgumentException("VM id is required.", nameof(vmId));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        var api = string.Equals(rendererApi, "opengl", StringComparison.OrdinalIgnoreCase) ? "opengl" : "vulkan";
        var producer = new
        {
            kind = "revm-gpu-producer-v1",
            abiVersion = AbiVersion,
            vmId,
            producerKind = "virtio-gpu-gfxstream",
            displayMode = "native-hwnd-gfxstream-host",
            width,
            height,
            format = "GPU_COMMAND_STREAM",
            capabilities = new
            {
                producesBgraFrames = false,
                producesGpuCommands = true,
                supportsHostGpu = true,
                supportsVirtioGpu = true,
                supportsGfxstream = true,
                requiresAdb = false,
                usesEncodedVideo = false
            },
            commandEndpoint = new
            {
                kind = "gfxstream-command-stream-v1",
                rendererStack = "gfxstream-fastpipe",
                rendererApi = api
            }
        };

        return JsonSerializer.Serialize(producer, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string CreateQmpScanoutGpuProducerEndpointJson(string vmId, int width, int height, string outputDirectory)
    {
        using var frameDoc = JsonDocument.Parse(CreateEmptyEndpointJson(vmId, width, height, outputDirectory));
        var producer = new
        {
            kind = "revm-gpu-producer-v1",
            abiVersion = AbiVersion,
            vmId,
            producerKind = "shm-bgra-frame-ring",
            displayMode = "native-hwnd-qmp-scanout",
            width,
            height,
            format = "BGRA8888",
            capabilities = new
            {
                producesBgraFrames = true,
                producesGpuCommands = false,
                supportsHostGpu = false,
                supportsVirtioGpu = true,
                supportsGfxstream = false,
                requiresAdb = false,
                usesEncodedVideo = false
            },
            frameEndpoint = frameDoc.RootElement.Clone()
        };

        return JsonSerializer.Serialize(producer, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string CreateEmptyEndpointJson(string vmId, int width, int height, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(vmId)) throw new ArgumentException("VM id is required.", nameof(vmId));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        var stride = checked(width * 4);
        var frameBytes = checked((long)stride * height);
        var totalBytes = checked((long)HeaderSize + SlotSize * DefaultSlots + frameBytes * DefaultSlots);
        if (totalBytes > MaxSyntheticBytes)
            throw new InvalidOperationException($"Live scanout shm-frame-ring-v1 image would be {totalBytes} bytes, above the {MaxSyntheticBytes} byte safety cap.");

        Directory.CreateDirectory(outputDirectory);
        var safeVmId = SanitizeVmId(vmId);
        var ringPath = Path.GetFullPath(Path.Combine(outputDirectory, safeVmId + ".frame-ring.bin"));
        WriteEmptyRingImage(ringPath, width, height, stride, frameBytes);

        var endpoint = new
        {
            kind = "shm-frame-ring-v1",
            abiVersion = AbiVersion,
            vmId,
            mappingName = $"Global\\RevmFrameRing-{vmId}",
            readyEventName = $"Global\\RevmFrameReady-{vmId}",
            controlPipeName = $"\\\\.\\pipe\\revm-control-{vmId}",
            width,
            height,
            stride,
            format = "BGRA8888",
            formatCode = FormatBgra8888,
            slots = DefaultSlots,
            ringImagePath = ringPath
        };

        return JsonSerializer.Serialize(endpoint, new JsonSerializerOptions { WriteIndented = false });
    }

    private static void WriteRingImage(string path, int width, int height, int stride, long frameBytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);

        writer.Write(FrameMagic);
        writer.Write(AbiVersion);
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write((uint)stride);
        writer.Write(FormatBgra8888);
        writer.Write((ulong)DefaultSlots);
        writer.Write(0UL);
        writer.Write((uint)(DefaultSlots - 1));
        writer.Write(0U);

        var firstDataOffset = HeaderSize + SlotSize * DefaultSlots;
        for (var slot = 0; slot < DefaultSlots; slot++)
        {
            var frameId = (ulong)(slot + 1);
            writer.Write((uint)SlotReady);
            writer.Write(frameId);
            writer.Write(0UL);
            writer.Write((uint)(firstDataOffset + slot * frameBytes));
            writer.Write((uint)frameBytes);
        }

        for (var slot = 0; slot < DefaultSlots; slot++)
            WriteSyntheticBgraFrame(writer, width, height, (byte)(slot + 1));
    }

    private static void WriteEmptyRingImage(string path, int width, int height, int stride, long frameBytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new BinaryWriter(stream);

        writer.Write(FrameMagic);
        writer.Write(AbiVersion);
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write((uint)stride);
        writer.Write(FormatBgra8888);
        writer.Write((ulong)DefaultSlots);
        writer.Write(0UL);
        writer.Write(0U);
        writer.Write(0U);

        var firstDataOffset = HeaderSize + SlotSize * DefaultSlots;
        for (var slot = 0; slot < DefaultSlots; slot++)
        {
            writer.Write((uint)SlotFree);
            writer.Write(0UL);
            writer.Write(0UL);
            writer.Write((uint)(firstDataOffset + slot * frameBytes));
            writer.Write((uint)frameBytes);
        }

        stream.SetLength(firstDataOffset + frameBytes * DefaultSlots);
    }

    private static void WriteSyntheticBgraFrame(BinaryWriter writer, int width, int height, byte frameId)
    {
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

    private static string SanitizeVmId(string vmId)
    {
        var chars = vmId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '.') chars[i] = '-';
        }
        return new string(chars);
    }
}
