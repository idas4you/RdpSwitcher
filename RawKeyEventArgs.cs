namespace RdpSwitcher;

internal sealed class RawKeyEventArgs : EventArgs
{
    public RawKeyEventArgs(int virtualKeyCode, ushort scanCode, ushort flags)
    {
        VirtualKeyCode = virtualKeyCode;
        ScanCode = scanCode;
        Flags = flags;
    }

    public int VirtualKeyCode { get; }

    public ushort ScanCode { get; }

    public ushort Flags { get; }
}
