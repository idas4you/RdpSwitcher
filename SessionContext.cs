namespace RdpSwitcher;

internal static class SessionContext
{
    public static string SessionName => Environment.GetEnvironmentVariable("SESSIONNAME") ?? string.Empty;

    public static bool IsRemoteSession
    {
        get
        {
            if (string.Equals(SessionName, "Console", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return SessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase)
                || SystemInformation.TerminalServerSession;
        }
    }

    public static string RoleName => IsRemoteSession ? "Remote sender" : "Host listener";
}
