namespace ReVM.NativeRendering;

public sealed record RenderHostOptions(
    string VmId,
    string RendererPath,
    string TransportDirectory,
    string RendererApi = "vulkan",
    int PreferredFormat = RenderHostOptions.Bgra8888)
{
    public const int Bgra8888 = 1;
}
