using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RdpSwitcher;

internal static class DvcSignalSender
{
    public static bool TrySend(out string? error)
    {
        error = null;
        var channel = NativeMethods.WTSVirtualChannelOpenEx(
            NativeMethods.WTS_CURRENT_SESSION,
            DvcChannel.Name,
            NativeMethods.WTS_CHANNEL_OPTION_DYNAMIC);

        if (channel == IntPtr.Zero)
        {
            error = FormatLastWin32Error("Could not open DVC channel");
            return false;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(CreatePayload());
            if (!NativeMethods.WTSVirtualChannelWrite(channel, payload, (uint)payload.Length, out var bytesWritten))
            {
                error = FormatLastWin32Error("Could not write DVC payload");
                return false;
            }

            if (bytesWritten != payload.Length)
            {
                error = $"DVC payload write was incomplete. Written={bytesWritten}, Expected={payload.Length}";
                return false;
            }

            return true;
        }
        finally
        {
            NativeMethods.WTSVirtualChannelClose(channel);
        }
    }

    private static string CreatePayload()
    {
        return string.Join(
            "\n",
            "event=pause-double-press",
            $"utc={DateTimeOffset.UtcNow:O}",
            $"machine={Environment.MachineName}",
            $"user={Environment.UserName}",
            $"session={SessionContext.SessionName}",
            $"process={Environment.ProcessId}",
            $"nonce={Guid.NewGuid():N}");
    }

    private static string FormatLastWin32Error(string message)
    {
        var errorCode = Marshal.GetLastWin32Error();
        return $"{message}. Win32Error={errorCode}, Message={new Win32Exception(errorCode).Message}";
    }
}
