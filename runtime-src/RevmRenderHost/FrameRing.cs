using System.Text.Json;

namespace RevmRenderHost;

/// <summary>
/// Renderer-side model for the clean-room shm-frame-ring-v1 transport.
/// This is intentionally a raw BGRA frame ring contract, not scrcpy, not ADB
/// display streaming, not SDL, and not encoded video. The current managed
/// implementation reads the synthetic ring image emitted by REPlayer while the
/// future native renderer will map the same layout from shared memory.
/// </summary>
public sealed class RevmFrameRingConsumer : IDisposable
{
    public const uint FrameMagic = 0x4D465652u; // "RVFM"
    public const uint AbiVersion = 1;
    public const uint FormatBgra8888 = 1;
    public const int HeaderSize = 48;
    public const int SlotSize = 28;
    public const int MaxSlots = 3;

    private readonly FileStream? _ringStream;

    private RevmFrameRingConsumer(RevmFrameRingEndpoint endpoint, FileStream? ringStream, RevmFrameRingHeader header, IReadOnlyList<RevmFrameRingSlot> slots)
    {
        Endpoint = endpoint;
        _ringStream = ringStream;
        Header = header;
        Slots = slots;
        LatestReadySlot = slots
            .Where(static slot => slot.State == RevmFrameSlotState.Ready)
            .OrderByDescending(static slot => slot.FrameId)
            .FirstOrDefault();
    }

    public RevmFrameRingEndpoint Endpoint { get; }
    public RevmFrameRingHeader Header { get; }
    public IReadOnlyList<RevmFrameRingSlot> Slots { get; }
    public RevmFrameRingSlot? LatestReadySlot { get; }
    public bool HasReadyFrame => LatestReadySlot is not null;

    public RevmRendererStatus TryCopyLatestFrame(Span<byte> destination, out RevmBgraFrameInfo frame)
    {
        frame = default;
        if (LatestReadySlot is not { } slot) return RevmRendererStatus.InvalidState;
        if (_ringStream is null) return RevmRendererStatus.TransportAttachFailed;
        if (destination.Length < slot.DataSize) return RevmRendererStatus.InvalidSize;

        _ringStream.Position = slot.DataOffset;
        var target = destination[..checked((int)slot.DataSize)];
        var totalRead = 0;
        while (totalRead < target.Length)
        {
            var read = _ringStream.Read(target[totalRead..]);
            if (read == 0) return RevmRendererStatus.InvalidFrameRing;
            totalRead += read;
        }

        frame = new RevmBgraFrameInfo(
            Header.Width,
            Header.Height,
            Header.Stride,
            Header.FormatCode,
            slot.FrameId,
            slot.QpcProduced,
            slot.Index,
            slot.DataSize);
        return RevmRendererStatus.Ok;
    }

    public static RevmRendererStatus TryOpen(string endpointJson, out RevmFrameRingConsumer? consumer)
    {
        consumer = null;
        var endpointStatus = RevmFrameRingEndpoint.TryParse(endpointJson, out var endpoint);
        if (endpointStatus != RevmRendererStatus.Ok || endpoint is null) return endpointStatus;

        // The future native implementation can map Endpoint.MappingName. The
        // current deterministic shim opens the synthetic ring image when present.
        if (string.IsNullOrWhiteSpace(endpoint.RingImagePath))
        {
            consumer = new RevmFrameRingConsumer(endpoint, null, RevmFrameRingHeader.FromEndpoint(endpoint), Array.Empty<RevmFrameRingSlot>());
            return RevmRendererStatus.Ok;
        }

        try
        {
            var stream = new FileStream(endpoint.RingImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var status = TryRead(stream, endpoint, out var header, out var slots);
            if (status != RevmRendererStatus.Ok)
            {
                stream.Dispose();
                return status;
            }

            consumer = new RevmFrameRingConsumer(endpoint, stream, header, slots);
            return RevmRendererStatus.Ok;
        }
        catch (IOException)
        {
            return RevmRendererStatus.TransportAttachFailed;
        }
        catch (UnauthorizedAccessException)
        {
            return RevmRendererStatus.TransportAttachFailed;
        }
    }

    private static RevmRendererStatus TryRead(Stream stream, RevmFrameRingEndpoint endpoint, out RevmFrameRingHeader header, out IReadOnlyList<RevmFrameRingSlot> slots)
    {
        header = default;
        slots = Array.Empty<RevmFrameRingSlot>();

        if (stream.Length < HeaderSize + SlotSize)
            return RevmRendererStatus.InvalidFrameRing;

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        header = new RevmFrameRingHeader(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadUInt32(),
            reader.ReadUInt32());

        if (header.Magic != FrameMagic || header.AbiVersion != AbiVersion)
            return RevmRendererStatus.InvalidFrameRing;
        if (header.Width != (uint)endpoint.Width || header.Height != (uint)endpoint.Height || header.Stride != (uint)endpoint.Stride)
            return RevmRendererStatus.InvalidFrameRing;
        if (header.FormatCode != FormatBgra8888 || endpoint.FormatCode != FormatBgra8888)
            return RevmRendererStatus.UnsupportedFormat;
        if (header.Slots == 0 || header.Slots > MaxSlots || header.Slots != (uint)endpoint.Slots)
            return RevmRendererStatus.InvalidFrameRing;

        var frameBytes = checked((ulong)header.Stride * header.Height);
        var minimumBytes = checked((ulong)HeaderSize + header.Slots * SlotSize + frameBytes * header.Slots);
        if ((ulong)stream.Length < minimumBytes)
            return RevmRendererStatus.InvalidFrameRing;

        var parsedSlots = new List<RevmFrameRingSlot>((int)header.Slots);
        for (var i = 0; i < (int)header.Slots; i++)
        {
            var slot = new RevmFrameRingSlot(
                i,
                (RevmFrameSlotState)reader.ReadUInt32(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt32(),
                reader.ReadUInt32());

            if (slot.DataOffset < HeaderSize + SlotSize * header.Slots)
                return RevmRendererStatus.InvalidFrameRing;
            if (slot.DataSize != frameBytes)
                return RevmRendererStatus.InvalidFrameRing;
            if ((ulong)slot.DataOffset + slot.DataSize > (ulong)stream.Length)
                return RevmRendererStatus.InvalidFrameRing;

            parsedSlots.Add(slot);
        }

        slots = parsedSlots;
        return RevmRendererStatus.Ok;
    }

    public void Dispose() => _ringStream?.Dispose();
}

public sealed record RevmFrameRingEndpoint(
    string VmId,
    string MappingName,
    string ReadyEventName,
    string ControlPipeName,
    int Width,
    int Height,
    int Stride,
    string Format,
    uint FormatCode,
    int Slots,
    string? RingImagePath)
{
    public static RevmRendererStatus TryParse(string endpointJson, out RevmFrameRingEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(endpointJson)) return RevmRendererStatus.InvalidTransportEndpoint;

        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            var kind = StringProp(root, "kind");
            if (!string.Equals(kind, RevmRendererAbi.TransportKindShmFrameRing, StringComparison.OrdinalIgnoreCase))
                return RevmRendererStatus.UnsupportedTransportKind;

            var format = StringProp(root, "format");
            var formatCode = UIntProp(root, "formatCode", RevmFrameRingConsumer.FormatBgra8888);
            var width = IntProp(root, "width");
            var height = IntProp(root, "height");
            var stride = IntProp(root, "stride");
            var slots = IntProp(root, "slots");

            if (!string.Equals(format, RevmRendererAbi.PreferredFormatName, StringComparison.OrdinalIgnoreCase))
                return RevmRendererStatus.UnsupportedFormat;
            if (formatCode != RevmFrameRingConsumer.FormatBgra8888)
                return RevmRendererStatus.UnsupportedFormat;
            if (width <= 0 || height <= 0 || stride < checked(width * 4))
                return RevmRendererStatus.InvalidSize;
            if (slots is < 1 or > RevmFrameRingConsumer.MaxSlots)
                return RevmRendererStatus.InvalidTransportEndpoint;

            endpoint = new RevmFrameRingEndpoint(
                StringProp(root, "vmId"),
                StringProp(root, "mappingName"),
                StringProp(root, "readyEventName"),
                StringProp(root, "controlPipeName"),
                width,
                height,
                stride,
                format,
                formatCode,
                slots,
                OptionalStringProp(root, "ringImagePath"));
            return RevmRendererStatus.Ok;
        }
        catch (JsonException)
        {
            return RevmRendererStatus.InvalidTransportEndpoint;
        }
        catch (OverflowException)
        {
            return RevmRendererStatus.InvalidSize;
        }
    }

    private static string StringProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;

    private static string? OptionalStringProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) ? prop.GetString() : null;

    private static int IntProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) ? value : 0;

    private static uint UIntProp(JsonElement root, string name, uint fallback) =>
        root.TryGetProperty(name, out var prop) && prop.TryGetUInt32(out var value) ? value : fallback;
}

public sealed record RevmGpuProducerEndpoint(
    string VmId,
    string ProducerKind,
    string DisplayMode,
    int Width,
    int Height,
    RevmGpuProducerCapabilities Capabilities,
    string FrameEndpointJson,
    RevmFrameRingEndpoint? FrameEndpoint)
{
    private static readonly string[] BannedTransportTokens =
    {
        "scrcpy", "adb-display", "adb_display", "sdl", "h264", "h.264", "video", "webrtc", "encoded", "decoder", "encoder"
    };

    public static RevmRendererStatus TryParse(string endpointJson, out RevmGpuProducerEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(endpointJson)) return RevmRendererStatus.InvalidTransportEndpoint;

        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            var root = doc.RootElement;
            var kind = StringProp(root, "kind");
            if (!string.Equals(kind, RevmRendererAbi.TransportKindGpuProducer, StringComparison.OrdinalIgnoreCase))
                return RevmRendererStatus.UnsupportedTransportKind;
            if (IntProp(root, "abiVersion") != RevmRendererAbi.AbiVersion)
                return RevmRendererStatus.UnsupportedAbi;

            var producerKind = StringProp(root, "producerKind");
            var displayMode = StringProp(root, "displayMode");
            if (ContainsBannedTransportToken(producerKind) || ContainsBannedTransportToken(displayMode))
                return RevmRendererStatus.UnsupportedTransportKind;

            var width = IntProp(root, "width");
            var height = IntProp(root, "height");
            if (width <= 0 || height <= 0) return RevmRendererStatus.InvalidSize;

            var capabilities = RevmGpuProducerCapabilities.FromJson(root.TryGetProperty("capabilities", out var caps) ? caps : default);
            if (capabilities.RequiresAdb || capabilities.UsesEncodedVideo)
                return RevmRendererStatus.UnsupportedTransportKind;

            if (string.Equals(producerKind, "virtio-gpu-gfxstream", StringComparison.OrdinalIgnoreCase))
            {
                if (!capabilities.ProducesGpuCommands || !capabilities.SupportsVirtioGpu || !capabilities.SupportsGfxstream || capabilities.ProducesBgraFrames)
                    return RevmRendererStatus.UnsupportedTransportKind;
                if (!root.TryGetProperty("commandEndpoint", out var commandEndpointElement) || commandEndpointElement.ValueKind != JsonValueKind.Object)
                    return RevmRendererStatus.InvalidTransportEndpoint;
                if (!string.Equals(StringProp(commandEndpointElement, "kind"), "gfxstream-command-stream-v1", StringComparison.OrdinalIgnoreCase))
                    return RevmRendererStatus.UnsupportedTransportKind;

                endpoint = new RevmGpuProducerEndpoint(
                    StringProp(root, "vmId"),
                    producerKind,
                    displayMode,
                    width,
                    height,
                    capabilities,
                    string.Empty,
                    null);
                return RevmRendererStatus.Ok;
            }

            if (string.Equals(producerKind, "virtio-gpu-gfxstream-scanout-import", StringComparison.OrdinalIgnoreCase))
            {
                if (!capabilities.ProducesBgraFrames || !capabilities.ProducesGpuCommands || !capabilities.SupportsVirtioGpu || !capabilities.SupportsGfxstream)
                    return RevmRendererStatus.UnsupportedTransportKind;
                if (!root.TryGetProperty("commandEndpoint", out var commandEndpointElement) || commandEndpointElement.ValueKind != JsonValueKind.Object)
                    return RevmRendererStatus.InvalidTransportEndpoint;
                if (!string.Equals(StringProp(commandEndpointElement, "kind"), "gfxstream-command-stream-v1", StringComparison.OrdinalIgnoreCase))
                    return RevmRendererStatus.UnsupportedTransportKind;
                if (!root.TryGetProperty("frameEndpoint", out var scanoutFrameEndpointElement) || scanoutFrameEndpointElement.ValueKind != JsonValueKind.Object)
                    return RevmRendererStatus.InvalidTransportEndpoint;

                var scanoutFrameEndpointJson = scanoutFrameEndpointElement.GetRawText();
                var scanoutFrameStatus = RevmFrameRingEndpoint.TryParse(scanoutFrameEndpointJson, out var scanoutFrameEndpoint);
                if (scanoutFrameStatus != RevmRendererStatus.Ok || scanoutFrameEndpoint is null) return scanoutFrameStatus;
                if (scanoutFrameEndpoint.Width != width || scanoutFrameEndpoint.Height != height)
                    return RevmRendererStatus.InvalidSize;

                endpoint = new RevmGpuProducerEndpoint(
                    StringProp(root, "vmId"),
                    producerKind,
                    displayMode,
                    width,
                    height,
                    capabilities,
                    scanoutFrameEndpointJson,
                    scanoutFrameEndpoint);
                return RevmRendererStatus.Ok;
            }

            if (!root.TryGetProperty("frameEndpoint", out var frameEndpointElement) || frameEndpointElement.ValueKind != JsonValueKind.Object)
                return RevmRendererStatus.InvalidTransportEndpoint;

            var frameEndpointJson = frameEndpointElement.GetRawText();
            var frameStatus = RevmFrameRingEndpoint.TryParse(frameEndpointJson, out var frameEndpoint);
            if (frameStatus != RevmRendererStatus.Ok || frameEndpoint is null) return frameStatus;
            if (!string.Equals(producerKind, "shm-bgra-frame-ring", StringComparison.OrdinalIgnoreCase))
                return RevmRendererStatus.UnsupportedTransportKind;
            if (!capabilities.ProducesBgraFrames || capabilities.ProducesGpuCommands)
                return RevmRendererStatus.UnsupportedTransportKind;
            if (frameEndpoint.Width != width || frameEndpoint.Height != height)
                return RevmRendererStatus.InvalidSize;

            endpoint = new RevmGpuProducerEndpoint(
                StringProp(root, "vmId"),
                producerKind,
                displayMode,
                width,
                height,
                capabilities,
                frameEndpointJson,
                frameEndpoint);
            return RevmRendererStatus.Ok;
        }
        catch (JsonException)
        {
            return RevmRendererStatus.InvalidTransportEndpoint;
        }
    }

    private static bool ContainsBannedTransportToken(string value) =>
        BannedTransportTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string StringProp(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;

    private static int IntProp(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) ? value : 0;
}

public readonly record struct RevmGpuProducerCapabilities(
    bool ProducesBgraFrames,
    bool ProducesGpuCommands,
    bool SupportsHostGpu,
    bool SupportsVirtioGpu,
    bool SupportsGfxstream,
    bool RequiresAdb,
    bool UsesEncodedVideo)
{
    public static RevmGpuProducerCapabilities FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new RevmGpuProducerCapabilities(false, false, false, false, false, true, true);

        return new RevmGpuProducerCapabilities(
            BoolProp(root, "producesBgraFrames"),
            BoolProp(root, "producesGpuCommands"),
            BoolProp(root, "supportsHostGpu"),
            BoolProp(root, "supportsVirtioGpu"),
            BoolProp(root, "supportsGfxstream"),
            BoolProp(root, "requiresAdb"),
            BoolProp(root, "usesEncodedVideo"));
    }

    private static bool BoolProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;
}

public readonly record struct RevmFrameRingHeader(
    uint Magic,
    uint AbiVersion,
    uint Width,
    uint Height,
    uint Stride,
    uint FormatCode,
    ulong Slots,
    ulong LastFrameId,
    uint ReadyIndex,
    uint Reserved)
{
    public static RevmFrameRingHeader FromEndpoint(RevmFrameRingEndpoint endpoint) => new(
        RevmFrameRingConsumer.FrameMagic,
        RevmFrameRingConsumer.AbiVersion,
        (uint)endpoint.Width,
        (uint)endpoint.Height,
        (uint)endpoint.Stride,
        endpoint.FormatCode,
        (ulong)endpoint.Slots,
        0,
        0,
        0);
}

public readonly record struct RevmFrameRingSlot(
    int Index,
    RevmFrameSlotState State,
    ulong FrameId,
    ulong QpcProduced,
    uint DataOffset,
    uint DataSize);

public readonly record struct RevmBgraFrameInfo(
    uint Width,
    uint Height,
    uint Stride,
    uint FormatCode,
    ulong FrameId,
    ulong QpcProduced,
    int SlotIndex,
    uint DataSize);

public sealed record RevmBgraFrame(RevmBgraFrameInfo Info, byte[] Pixels);

/// <summary>
/// Renderer-side BGRA frame source seam. The current implementation copies the
/// latest ready slot out of the clean-room frame ring; the native renderer can
/// replace the final consumer with a D3D/Vulkan upload while preserving this
/// transport contract.
/// </summary>
public sealed class RevmFrameRingFrameSource
{
    private readonly RevmFrameRingConsumer _consumer;

    public RevmFrameRingFrameSource(RevmFrameRingConsumer consumer)
    {
        _consumer = consumer;
    }

    public RevmRendererStatus TryReadLatestFrame(out RevmBgraFrame? frame)
    {
        frame = null;
        var slot = _consumer.LatestReadySlot;
        if (slot is null) return RevmRendererStatus.InvalidState;

        var pixels = new byte[slot.Value.DataSize];
        var status = _consumer.TryCopyLatestFrame(pixels, out var info);
        if (status != RevmRendererStatus.Ok) return status;

        frame = new RevmBgraFrame(info, pixels);
        return RevmRendererStatus.Ok;
    }
}

public enum RevmFrameSlotState : uint
{
    Free = 0,
    Writing = 1,
    Ready = 2,
    Reading = 3
}
