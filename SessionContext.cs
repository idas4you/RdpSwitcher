using System.Runtime.InteropServices;

namespace RdpSwitcher;

internal static class SessionContext
{
    private const short WtsProtocolConsole = 0;

    private static readonly Lazy<SessionState> Current = new(CreateSessionState);

    public static string SessionName => Current.Value.SessionName;

    public static bool IsRemoteSession => Current.Value.IsRemoteSession;

    public static string RoleName => IsRemoteSession ? "Remote sender" : "Host listener";

    public static string Diagnostics => Current.Value.Diagnostics;

    private static SessionState CreateSessionState()
    {
        var sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? string.Empty;
        var terminalServerSession = SystemInformation.TerminalServerSession;
        var sessionId = GetCurrentSessionId(out var sessionIdError);
        string? protocolError = null;
        var clientProtocolType = sessionId.HasValue
            ? GetClientProtocolType(sessionId.Value, out protocolError)
            : null;

        var isRemoteSession = clientProtocolType.HasValue
            ? clientProtocolType.Value != WtsProtocolConsole
            : sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase)
                || terminalServerSession;

        return new SessionState(
            sessionName,
            terminalServerSession,
            sessionId,
            sessionIdError,
            clientProtocolType,
            protocolError,
            isRemoteSession);
    }

    private static int? GetCurrentSessionId(out string? error)
    {
        error = null;
        if (NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId))
        {
            return (int)sessionId;
        }

        error = $"ProcessIdToSessionId failed. Win32Error={Marshal.GetLastWin32Error()}";
        return null;
    }

    private static short? GetClientProtocolType(int sessionId, out string? error)
    {
        error = null;
        if (!NativeMethods.WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                NativeMethods.WTS_CLIENT_PROTOCOL_TYPE,
                out var buffer,
                out var bytesReturned))
        {
            error = $"WTSQuerySessionInformation(WTSClientProtocolType) failed. Win32Error={Marshal.GetLastWin32Error()}";
            return null;
        }

        try
        {
            if (buffer == IntPtr.Zero || bytesReturned < sizeof(short))
            {
                error = $"WTSClientProtocolType returned invalid data. Bytes={bytesReturned}";
                return null;
            }

            return Marshal.ReadInt16(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
    }

    private sealed record SessionState(
        string SessionName,
        bool TerminalServerSession,
        int? SessionId,
        string? SessionIdError,
        short? ClientProtocolType,
        string? ClientProtocolTypeError,
        bool IsRemoteSession)
    {
        public string Diagnostics =>
            $"Role={(IsRemoteSession ? "Remote sender" : "Host listener")}, " +
            $"SessionName={SessionName}, " +
            $"TerminalServerSession={TerminalServerSession}, " +
            $"SessionId={FormatNullable(SessionId)}, " +
            $"WtsClientProtocolType={FormatNullable(ClientProtocolType)}, " +
            $"SessionIdError={SessionIdError ?? "none"}, " +
            $"WtsClientProtocolTypeError={ClientProtocolTypeError ?? "none"}";

        private static string FormatNullable<T>(T? value)
            where T : struct
        {
            return value.HasValue ? value.Value.ToString() ?? string.Empty : "unknown";
        }
    }
}
