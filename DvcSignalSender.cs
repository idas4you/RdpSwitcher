using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RdpSwitcher;

internal static class DvcSignalSender
{
    private const int ErrorGenFailure = 31;
    private const int MaxSendAttempts = 3;
    private const int AckTimeoutMilliseconds = 4000;
    private const int AckBufferBytes = 4096;

    private static readonly object SyncRoot = new();
    private static IntPtr _channel;

    public static bool TrySend(out string? error)
    {
        error = null;
        var payload = CreatePayload();
        var errors = new List<string>();

        lock (SyncRoot)
        {
            for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
            {
                if (!EnsureChannelOpen(out error))
                {
                    errors.Add($"Attempt={attempt}: {error}");
                    CloseCurrentChannel();
                    continue;
                }

                NativeMethods.WTSVirtualChannelPurgeInput(_channel);
                if (!TryWriteCurrentChannel(payload.Bytes, out error))
                {
                    errors.Add($"Attempt={attempt}: {error}");
                    CloseCurrentChannel();
                    continue;
                }

                if (TryReadForwardedAck(payload.Nonce, out var ackStatus, out error))
                {
                    AppLog.Write($"DVC host ACK received. Attempt={attempt}, Nonce={payload.Nonce}, Status={ackStatus}");
                    return true;
                }

                errors.Add($"Attempt={attempt}: {error}");
                CloseCurrentChannel();
            }

            error = string.Join("; ", errors);
            return false;
        }
    }

    public static void Close()
    {
        lock (SyncRoot)
        {
            CloseCurrentChannel();
        }
    }

    private static bool EnsureChannelOpen(out string? error)
    {
        error = null;
        if (_channel != IntPtr.Zero)
        {
            return true;
        }

        _channel = NativeMethods.WTSVirtualChannelOpenEx(
            NativeMethods.WTS_CURRENT_SESSION,
            DvcChannel.Name,
            NativeMethods.WTS_CHANNEL_OPTION_DYNAMIC);

        if (_channel != IntPtr.Zero)
        {
            return true;
        }

        error = FormatLastWin32Error("Could not open DVC channel");
        return false;
    }

    private static bool TryWriteCurrentChannel(byte[] payload, out string? error)
    {
        error = null;
        if (!NativeMethods.WTSVirtualChannelWrite(_channel, payload, (uint)payload.Length, out var bytesWritten))
        {
            error = FormatLastWin32Error("Could not write DVC payload");
            return false;
        }

        if (bytesWritten == payload.Length)
        {
            return true;
        }

        error = $"DVC payload write was incomplete. Written={bytesWritten}, Expected={payload.Length}";
        return false;
    }

    private static bool TryReadForwardedAck(string expectedNonce, out string? ackStatus, out string? error)
    {
        ackStatus = null;
        error = null;
        var deadline = Environment.TickCount64 + AckTimeoutMilliseconds;

        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                error = $"Timed out waiting for host ACK. Nonce={expectedNonce}, TimeoutMs={AckTimeoutMilliseconds}";
                return false;
            }

            var buffer = new byte[AckBufferBytes];
            if (!NativeMethods.WTSVirtualChannelRead(_channel, (uint)remaining, buffer, (uint)buffer.Length, out var bytesRead))
            {
                error = FormatLastWin32Error($"Could not read DVC host ACK. Nonce={expectedNonce}");
                return false;
            }

            if (bytesRead == 0)
            {
                error = $"Timed out waiting for host ACK. Nonce={expectedNonce}, TimeoutMs={AckTimeoutMilliseconds}";
                return false;
            }

            var ack = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
            var nonce = GetPayloadValue(ack, "nonce");
            var status = GetPayloadValue(ack, "status");
            if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            {
                AppLog.Write($"Ignored stale DVC ACK. ExpectedNonce={expectedNonce}, ReceivedNonce={nonce ?? "(missing)"}, Payload={ack.ReplaceLineEndings(" | ")}");
                continue;
            }

            if (string.Equals(status, "forwarded", StringComparison.Ordinal))
            {
                ackStatus = status;
                return true;
            }

            error = $"Host plug-in returned non-forwarded ACK. Nonce={expectedNonce}, Status={status ?? "(missing)"}, Payload={ack.ReplaceLineEndings(" | ")}";
            return false;
        }
    }

    private static void CloseCurrentChannel()
    {
        if (_channel == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.WTSVirtualChannelClose(_channel);
        _channel = IntPtr.Zero;
    }

    private static DvcPayload CreatePayload()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var text = string.Join(
            "\n",
            "event=pause-double-press",
            $"utc={DateTimeOffset.UtcNow:O}",
            $"machine={Environment.MachineName}",
            $"user={Environment.UserName}",
            $"session={SessionContext.SessionName}",
            $"process={Environment.ProcessId}",
            $"nonce={nonce}");

        return new DvcPayload(nonce, Encoding.UTF8.GetBytes(text));
    }

    private static string? GetPayloadValue(string payload, string key)
    {
        var prefix = $"{key}=";
        foreach (var line in payload.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return null;
    }

    private static string FormatLastWin32Error(string message)
    {
        var errorCode = Marshal.GetLastWin32Error();
        var formatted = $"{message}. Win32Error={errorCode}, Message={new Win32Exception(errorCode).Message}";
        if (errorCode == ErrorGenFailure && string.Equals(message, "Could not open DVC channel", StringComparison.Ordinal))
        {
            formatted += "; LikelyCause=The host mstsc.exe plug-in did not create the RDPSWCH listener. Start RdpSwitcher on the host before opening RDP, confirm the RDC AddIn is enabled, then close and reopen the RDP window.";
        }

        return formatted;
    }

    private sealed record DvcPayload(string Nonce, byte[] Bytes);
}
