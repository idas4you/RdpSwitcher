# RdpSwitcher

[한국어 README](README.ko.md)

RdpSwitcher is a small Windows tray utility for switching between Remote Desktop sessions and the host desktop with a double press of the Pause key.

Install and run the same app on both the host PC and the remote Windows session.

- On the host PC, RdpSwitcher switches RDP windows and enables the RDC Dynamic Virtual Channel add-in for new `mstsc.exe` connections.
- In a remote session, RdpSwitcher detects Pause x2 and writes a signal to the `RDPSWCH` Dynamic Virtual Channel.

RdpSwitcher does not use file-based signaling or RDP drive redirection. The native `RdpSwitcher.Plugin.dll` is loaded by `mstsc.exe`, listens on the `RDPSWCH` Dynamic Virtual Channel, and forwards valid messages to the tray app through the local named pipe `RdpSwitcher.Signal`.

When running on the host PC, RdpSwitcher also enables the future RDC Dynamic Virtual Channel plug-in for new `mstsc.exe` connections by writing:

```text
HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RdpSwitcher
Name = {8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}
```

On normal exit, RdpSwitcher removes only this RDC AddIn key. COM registration for `RdpSwitcher.Plugin.dll` is intentionally left untouched.

On host startup, RdpSwitcher also checks whether `RdpSwitcher.Plugin.dll` next to `RdpSwitcher.exe` is registered as a per-user COM in-process server. If it is missing or points elsewhere, RdpSwitcher writes:

```text
HKCU\Software\Classes\CLSID\{8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}
  (Default) = RdpSwitcher Dynamic Virtual Channel Plugin

HKCU\Software\Classes\CLSID\{8A1E8AC0-827E-42F8-8B65-8D65C7A6AB7D}\InprocServer32
  (Default) = <RdpSwitcher install folder>\RdpSwitcher.Plugin.dll
  ThreadingModel = Both
```

This per-user COM registration does not require administrator rights. RdpSwitcher does not unregister COM on exit; only the RDC AddIn key is removed.

The protected install location check is controlled by `RequireTrustedInstallLocation` in `ComPluginRegistration`. Debug builds set it to `false` so local development can run from non-`Program Files` paths. Release builds set it to `true`, so COM registration is allowed only from `Program Files` or `Program Files (x86)`.

Automatic registration only runs when all of these are true:

- RdpSwitcher is running on the host side, not inside an RDP session.
- `RdpSwitcher.Plugin.dll` exists next to `RdpSwitcher.exe`.
- If `RequireTrustedInstallLocation` is `true`, the install folder is under `Program Files` or `Program Files (x86)`.

The current registration decision is written to `%LOCALAPPDATA%\RdpSwitcher\RdpSwitcher-yyyy-MM-dd.log` at startup.

## Usage

1. Build or install RdpSwitcher so these files are in the same folder:

```text
RdpSwitcher.exe
RdpSwitcher.Plugin.dll
```

2. Start `RdpSwitcher.exe` on the host PC before opening a new RDP connection.

On host startup, the app registers the per-user COM plug-in if needed and enables the RDC AddIn registry key. The AddIn is used by new `mstsc.exe` connections, so close and reopen existing RDP windows after starting RdpSwitcher.

3. Connect to the remote Windows session with Remote Desktop.

4. Start `RdpSwitcher.exe` inside the remote session too.

The host instance runs as `Host listener`. The remote instance runs as `Remote sender`. You can confirm the current role from the tray icon menu.

5. Press `Pause` twice in the remote session.

The remote app sends a DVC message through the `RDPSWCH` channel. The host-side `mstsc.exe` plug-in forwards it to the host tray app through the `RdpSwitcher.Signal` named pipe, and the host tray app switches the active RDP window.

With multiple RDP windows, the switch order is:

```text
RDP 1 -> RDP 2 -> Host desktop -> RDP 1
```

6. Exit the host tray app when you want to disable new RDP plug-in loading.

On normal exit, RdpSwitcher removes only the RDC AddIn key. The COM CLSID registration remains. Already running `mstsc.exe` processes may keep the plug-in loaded until those RDP windows are closed.

Logs are written per day:

```text
%LOCALAPPDATA%\RdpSwitcher\RdpSwitcher-yyyy-MM-dd.log
```

If switching does not work, check the log for:

- `COM plug-in registration check`
- `Named pipe server starting`
- `Sent DVC signal`
- `Received DVC signal from plug-in`

## Build

Build the native RDC plug-in first, then publish the tray app so `RdpSwitcher.Plugin.dll` is copied next to `RdpSwitcher.exe`:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  RdpSwitcher.Plugin\RdpSwitcher.Plugin.vcxproj `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m

dotnet publish RdpSwitcher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  /p:PublishSingleFile=true
```

## Release

GitHub Actions builds and uploads an MSI when a release tag is pushed.

The release workflow uses WiX Toolset v7 and passes `-acceptEula wix7` while building the MSI. Make sure you are allowed to accept the WiX v7 OSMF EULA for your use.

Use a three-part numeric tag:

```powershell
./build/Push-ReleaseTag.ps1
```

The workflow publishes a self-contained `win-x64` build, packages it as `RdpSwitcher-v1.0.0-win-x64.msi`, and attaches it to the GitHub Release for that tag.

You can also pass the tag directly:

```powershell
./build/Push-ReleaseTag.ps1 -Tag v1.0.0
```

To delete the most recent local tag and the matching remote tag:

```powershell
./build/Remove-LatestReleaseTag.ps1
```

Use `-Force` to skip the confirmation prompt.

## License

MIT
