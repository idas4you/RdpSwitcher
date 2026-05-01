using Microsoft.Win32;

namespace RdpSwitcher;

internal static class RdcAddInRegistration
{
    public const string PluginName = "RdpSwitcher";
    public const string PluginClsid = "{8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}";

    private const string AddInsRegistryPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns";

    public static string AddInRegistryPath => $@"HKCU\{AddInsRegistryPath}\{PluginName}";

    public static void EnableForCurrentUser()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{AddInsRegistryPath}\{PluginName}", writable: true)
            ?? throw new InvalidOperationException($"Could not create RDC AddIn registry key: {AddInRegistryPath}");

        key.SetValue("Name", PluginClsid, RegistryValueKind.String);
        AppLog.Write($"RDC AddIn enabled. Key={AddInRegistryPath}, Name={PluginClsid}");
    }

    public static void DisableForCurrentUser()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{AddInsRegistryPath}\{PluginName}", throwOnMissingSubKey: false);
            AppLog.Write($"RDC AddIn disabled. Key={AddInRegistryPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Failed to disable RDC AddIn. Key={AddInRegistryPath}, Error={ex.Message}");
        }
    }
}
