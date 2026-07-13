namespace ReVM.NativeRendering;

public sealed record RenderHostSession(
    string VmId,
    IRenderHostController Controller,
    string Status)
{
    public RenderHostSession WithStatus(string status) => this with { Status = status };
}
