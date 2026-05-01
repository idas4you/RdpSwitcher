using Microsoft.Win32;

namespace RdpSwitcher;

internal enum ComPluginRegistrationResult
{
    Registered,
    AlreadyRegistered,
    PluginDllMissing,
    UntrustedInstallLocation
}

internal static class ComPluginRegistration
{
    public const string PluginFileName = "RdpSwitcher.Plugin.dll";
    public const string ThreadingModel = "Both";

    // When true, COM registration is allowed only from Program Files locations
    // to reduce the risk of mstsc.exe loading a DLL from a user-writable folder.
    // Keep false until Authenticode signing or another DLL trust policy is ready.
    private const bool RequireTrustedInstallLocation = false;

    private const string ClassesClsidRegistryPath = @"Software\Classes\CLSID";

    public static string PluginPath => Path.Combine(AppContext.BaseDirectory, PluginFileName);

    public static string ComRegistryPath => $@"HKCU\{ClassesClsidRegistryPath}\{RdcAddInRegistration.PluginClsid}";

    public static ComPluginRegistrationResult EnsureRegisteredForCurrentUser()
    {
        if (!File.Exists(PluginPath))
        {
            AppLog.Write($"COM plug-in DLL missing. Path={PluginPath}");
            return ComPluginRegistrationResult.PluginDllMissing;
        }

        if (RequireTrustedInstallLocation && !IsTrustedInstallLocation(AppContext.BaseDirectory))
        {
            AppLog.Write($"COM plug-in registration skipped because the install location is not trusted. BaseDirectory={AppContext.BaseDirectory}, Path={PluginPath}");
            return ComPluginRegistrationResult.UntrustedInstallLocation;
        }

        if (IsRegisteredForCurrentUser())
        {
            AppLog.Write($"COM plug-in already registered. Key={ComRegistryPath}, Path={PluginPath}");
            return ComPluginRegistrationResult.AlreadyRegistered;
        }

        RegisterForCurrentUser();
        return ComPluginRegistrationResult.Registered;
    }

    private static bool IsRegisteredForCurrentUser()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesClsidRegistryPath}\{RdcAddInRegistration.PluginClsid}\InprocServer32");
        var registeredPath = key?.GetValue(null) as string;
        var threadingModel = key?.GetValue("ThreadingModel") as string;

        return string.Equals(registeredPath, PluginPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(threadingModel, ThreadingModel, StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterForCurrentUser()
    {
        using var clsidKey = Registry.CurrentUser.CreateSubKey(
            $@"{ClassesClsidRegistryPath}\{RdcAddInRegistration.PluginClsid}",
            writable: true)
            ?? throw new InvalidOperationException($"Could not create COM CLSID key: {ComRegistryPath}");

        clsidKey.SetValue(null, "RdpSwitcher Dynamic Virtual Channel Plugin", RegistryValueKind.String);

        using var inprocKey = clsidKey.CreateSubKey("InprocServer32", writable: true)
            ?? throw new InvalidOperationException($"Could not create COM InprocServer32 key: {ComRegistryPath}\\InprocServer32");

        inprocKey.SetValue(null, PluginPath, RegistryValueKind.String);
        inprocKey.SetValue("ThreadingModel", ThreadingModel, RegistryValueKind.String);

        AppLog.Write($"COM plug-in registered. Key={ComRegistryPath}, Path={PluginPath}, ThreadingModel={ThreadingModel}");
    }

    private static bool IsTrustedInstallLocation(string baseDirectory)
    {
        var fullBaseDirectory = EnsureTrailingSeparator(Path.GetFullPath(baseDirectory));
        return IsUnderSpecialFolder(fullBaseDirectory, Environment.SpecialFolder.ProgramFiles)
            || IsUnderSpecialFolder(fullBaseDirectory, Environment.SpecialFolder.ProgramFilesX86);
    }

    private static bool IsUnderSpecialFolder(string fullBaseDirectory, Environment.SpecialFolder specialFolder)
    {
        var root = Environment.GetFolderPath(specialFolder);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return fullBaseDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : $"{path}{Path.DirectorySeparatorChar}";
    }
}
