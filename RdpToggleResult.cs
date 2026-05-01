namespace RdpSwitcher;

internal enum RdpToggleAction
{
    Minimized,
    Restored,
    NoRdpWindow
}

internal sealed class RdpToggleResult
{
    public RdpToggleResult(RdpToggleAction action, IntPtr windowHandle)
    {
        Action = action;
        WindowHandle = windowHandle;
    }

    public RdpToggleAction Action { get; }

    public IntPtr WindowHandle { get; }
}
