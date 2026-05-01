using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RdpSwitcher;

internal sealed class PipeSignalServer : IDisposable
{
    private const int MaxPayloadBytes = 8192;
    private const int ListenerCount = 4;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
    private readonly List<Task> _listenTasks = [];
    private bool _disposed;

    public event EventHandler<PipeSignalEventArgs>? SignalReceived;

    public void Start()
    {
        if (_listenTasks.Count > 0)
        {
            return;
        }

        AppLog.Write($"Named pipe server starting. Pipe={IpcEndpoint.PipeName}, Listeners={ListenerCount}");
        for (var listenerIndex = 1; listenerIndex <= ListenerCount; listenerIndex++)
        {
            var capturedIndex = listenerIndex;
            _listenTasks.Add(Task.Run(() => ListenAsync(capturedIndex, _cancellation.Token)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();
        WaitForListenersToStop();
        _cancellation.Dispose();
        _disposed = true;
    }

    private async Task ListenAsync(int listenerIndex, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServerStream();
                await pipe.WaitForConnectionAsync(cancellationToken);
                pipe.ReadMode = PipeTransmissionMode.Message;
                AppLog.Write($"Named pipe client connected. Pipe={IpcEndpoint.PipeName}, Listener={listenerIndex}");

                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var payload = await ReadPayloadAsync(pipe, cancellationToken);
                    if (payload is null)
                    {
                        break;
                    }

                    if (!IsValidPayload(payload))
                    {
                        AppLog.Write($"Rejected named pipe payload. Pipe={IpcEndpoint.PipeName}, Payload={payload.ReplaceLineEndings(" | ")}");
                        continue;
                    }

                    AppLog.Write($"Named pipe payload accepted. Pipe={IpcEndpoint.PipeName}, Listener={listenerIndex}, Bytes={Encoding.UTF8.GetByteCount(payload)}");
                    PostSignal(payload);
                }

                AppLog.Write($"Named pipe client disconnected. Pipe={IpcEndpoint.PipeName}, Listener={listenerIndex}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Write($"Named pipe server error. Pipe={IpcEndpoint.PipeName}, Listener={listenerIndex}, Error={ex}");
                await DelayAfterErrorAsync(cancellationToken);
            }
        }

        AppLog.Write($"Named pipe listener stopped. Pipe={IpcEndpoint.PipeName}, Listener={listenerIndex}");
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        return NamedPipeServerStreamAcl.Create(
            IpcEndpoint.PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: ListenerCount,
            PipeTransmissionMode.Message,
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

    private static async Task<string?> ReadPayloadAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var memory = new MemoryStream();

        while (memory.Length <= MaxPayloadBytes)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return memory.Length == 0 ? null : Encoding.UTF8.GetString(memory.ToArray());
            }

            memory.Write(buffer, 0, bytesRead);
            if (stream.IsMessageComplete)
            {
                break;
            }
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

    private void WaitForListenersToStop()
    {
        if (_listenTasks.Count == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(_listenTasks.ToArray(), TimeSpan.FromSeconds(1));
        }
        catch
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
