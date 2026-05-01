using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RdpSwitcher;

internal sealed class PipeSignalServer : IDisposable
{
    private const int MaxPayloadBytes = 8192;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
    private Task? _listenTask;
    private bool _disposed;

    public event EventHandler<PipeSignalEventArgs>? SignalReceived;

    public void Start()
    {
        _listenTask ??= Task.Run(() => ListenAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();
        _cancellation.Dispose();
        _disposed = true;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        AppLog.Write($"Named pipe server starting. Pipe={IpcEndpoint.PipeName}");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServerStream();
                await pipe.WaitForConnectionAsync(cancellationToken);
                var payload = await ReadPayloadAsync(pipe, cancellationToken);

                if (!IsValidPayload(payload))
                {
                    AppLog.Write($"Rejected named pipe payload. Pipe={IpcEndpoint.PipeName}, Payload={payload.ReplaceLineEndings(" | ")}");
                    continue;
                }

                PostSignal(payload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Write($"Named pipe server error. Pipe={IpcEndpoint.PipeName}, Error={ex}");
                await DelayAfterErrorAsync(cancellationToken);
            }
        }

        AppLog.Write($"Named pipe server stopped. Pipe={IpcEndpoint.PipeName}");
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        return NamedPipeServerStreamAcl.Create(
            IpcEndpoint.PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: MaxPayloadBytes,
            outBufferSize: 0,
            CreatePipeSecurity());
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current user SID is unavailable.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, currentUser);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return security;
    }

    private static void AddFullControl(PipeSecurity security, IdentityReference identity)
    {
        security.AddAccessRule(
            new PipeAccessRule(
                identity,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
    }

    private static async Task<string> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var memory = new MemoryStream();

        while (memory.Length <= MaxPayloadBytes)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            memory.Write(buffer, 0, bytesRead);
        }

        if (memory.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Named pipe payload exceeded {MaxPayloadBytes} bytes.");
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static bool IsValidPayload(string payload)
    {
        return payload
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, "event=pause-double-press", StringComparison.Ordinal));
    }

    private static async Task DelayAfterErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1000, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PostSignal(string payload)
    {
        if (_synchronizationContext is null)
        {
            SignalReceived?.Invoke(this, new PipeSignalEventArgs(payload));
            return;
        }

        _synchronizationContext.Post(
            _ => SignalReceived?.Invoke(this, new PipeSignalEventArgs(payload)),
            null);
    }
}
