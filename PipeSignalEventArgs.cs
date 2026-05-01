namespace RdpSwitcher;

internal sealed class PipeSignalEventArgs(string payload) : EventArgs
{
    public string Payload { get; } = payload;
}
