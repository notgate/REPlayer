namespace RevmFastpipeAbi;

public static class RevmFastpipeProtocol
{
    public const uint Magic = 0x31504652; // RFP1
    public const int AbiVersion = 1;
    public const int HeaderBytes = 4096;
    public const string EndpointKind = "revm-fastpipe-device-v1";
    public const string RendererStack = "gfxstream-fastpipe";
    public const string DefaultControlPort = "revm.fastpipe.control";
    public const string DefaultDataPort = "revm.fastpipe.data";
}

public sealed record RevmFastpipeQueue(string Name, int Id, long Bytes);
public sealed record RevmFastpipeSharedMemoryEndpoint(string MappingName, long Bytes);
public sealed record RevmFastpipePipeEndpoint(string Name);
public sealed record RevmFastpipeVirtioSerialEndpoint(string ControlName, string DataName, int ControlTcpPort, int DataTcpPort);

public sealed record RevmFastpipeEndpoint(
    string Kind,
    int AbiVersion,
    string VmId,
    string RendererStack,
    string RendererApi,
    bool QmpFallbackAllowed,
    int HeaderBytes,
    RevmFastpipeSharedMemoryEndpoint SharedMemory,
    RevmFastpipePipeEndpoint ControlPipe,
    RevmFastpipePipeEndpoint DataPipe,
    RevmFastpipeVirtioSerialEndpoint VirtioSerial,
    IReadOnlyList<RevmFastpipeQueue> Queues);
