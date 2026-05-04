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
    private int _nextConnectionId;
    private bool _disposed;

    public event EventHandler<PipeSignalEventArgs>? SignalReceived;

    public void Start()
    {
        if (_listenTask is not null)
        {
            return;
        }

        _listenTask = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();
        WaitForListenerToStop();
        _cancellation.Dispose();
        _disposed = true;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        AppLog.Write($"Named pipe server starting. Pipe={IpcEndpoint.PipeName}");

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateServerStream();
                await pipe.WaitForConnectionAsync(cancellationToken);
                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                AppLog.Write($"Named pipe client connected. Pipe={IpcEndpoint.PipeName}, Connection={connectionId}");

                _ = Task.Run(() => HandleClientAsync(pipe, connectionId, cancellationToken), CancellationToken.None);
                pipe = null;
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
            finally
            {
                pipe?.Dispose();
            }
        }

        AppLog.Write($"Named pipe server stopped. Pipe={IpcEndpoint.PipeName}");
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        int connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe)
            {
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var payload = await ReadPayloadAsync(pipe, cancellationToken);
                    if (payload is null)
                    {
                        break;
                    }

                    if (!IsValidPayload(payload))
                    {
                        AppLog.Write($"Rejected named pipe payload. Pipe={IpcEndpoint.PipeName}, Connection={connectionId}, Payload={payload.ReplaceLineEndings(" | ")}");
                        continue;
                    }

                    AppLog.Write($"Named pipe payload accepted. Pipe={IpcEndpoint.PipeName}, Connection={connectionId}, Bytes={Encoding.UTF8.GetByteCount(payload)}");
                    PostSignal(payload);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write($"Named pipe client error. Pipe={IpcEndpoint.PipeName}, Connection={connectionId}, Error={ex}");
        }
        finally
        {
            AppLog.Write($"Named pipe client disconnected. Pipe={IpcEndpoint.PipeName}, Connection={connectionId}");
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        return NamedPipeServerStreamAcl.Create(
            IpcEndpoint.PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
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

    private static async Task<string?> ReadPayloadAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        if (!await ReadExactAsync(stream, lengthBuffer, cancellationToken))
        {
            return null;
        }

        var payloadLength = BitConverter.ToInt32(lengthBuffer, 0);
        if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Named pipe payload length is invalid. Length={payloadLength}, Max={MaxPayloadBytes}.");
        }

        var payloadBuffer = new byte[payloadLength];
        if (!await ReadExactAsync(stream, payloadBuffer, cancellationToken))
        {
            return null;
        }

        return Encoding.UTF8.GetString(payloadBuffer);
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (bytesRead == 0)
            {
                return false;
            }

            offset += bytesRead;
        }

        return true;
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

    private void WaitForListenerToStop()
    {
        if (_listenTask is null)
        {
            return;
        }

        try
        {
            _listenTask.Wait(TimeSpan.FromSeconds(1));
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
