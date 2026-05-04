namespace RdpSwitcher;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly RdpWindowController _rdpWindowController;
    private readonly PauseDoublePressDetector _pauseDoublePressDetector;
    private readonly PipeSignalServer? _pipeSignalServer;
    private readonly RawKeyboardInputWindow? _rawKeyboardInputWindow;
    private readonly Icon? _ownedIcon;
    private readonly bool _isRemoteSession;

    public TrayApplicationContext()
    {
        _isRemoteSession = SessionContext.IsRemoteSession;
        _ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _notifyIcon = CreateNotifyIcon(ExitThread, _ownedIcon ?? SystemIcons.Application, _isRemoteSession);
        _rdpWindowController = new RdpWindowController();
        _pauseDoublePressDetector = new PauseDoublePressDetector();

        try
        {
            _rawKeyboardInputWindow = new RawKeyboardInputWindow();
            _rawKeyboardInputWindow.PausePressed += OnPausePressed;

            if (!_isRemoteSession)
            {
                AppLog.Write($"Host registration diagnostics before registration. {ComPluginRegistration.GetDiagnostics()}");
                AppLog.Write($"Host plug-in load diagnostics before registration. {ComPluginRegistration.GetLoadDiagnostics()}");
                AppLog.Write($"RDC AddIn diagnostics before registration. {RdcAddInRegistration.GetDiagnostics()}");

                var registrationResult = ComPluginRegistration.EnsureRegisteredForCurrentUser();
                AppLog.Write($"COM plug-in registration check. Result={registrationResult}, PluginPath={ComPluginRegistration.PluginPath}, Key={ComPluginRegistration.ComRegistryPath}");
                if (registrationResult == ComPluginRegistrationResult.PluginDllMissing)
                {
                    RdcAddInRegistration.DisableForCurrentUser();
                    _notifyIcon.ShowBalloonTip(
                        2000,
                        "RdpSwitcher",
                        $"{ComPluginRegistration.PluginFileName} was not found. RDC plug-in registration was skipped.",
                        ToolTipIcon.Warning);
                }
                else if (registrationResult == ComPluginRegistrationResult.UntrustedInstallLocation)
                {
                    RdcAddInRegistration.DisableForCurrentUser();
                    _notifyIcon.ShowBalloonTip(
                        3000,
                        "RdpSwitcher",
                        "RDC plug-in registration was skipped because RdpSwitcher is not installed under Program Files.",
                        ToolTipIcon.Warning);
                }
                else
                {
                    RdcAddInRegistration.EnableForCurrentUser();
                }

                AppLog.Write($"Host registration diagnostics after registration. {ComPluginRegistration.GetDiagnostics()}");
                AppLog.Write($"Host plug-in load diagnostics after registration. {ComPluginRegistration.GetLoadDiagnostics()}");
                AppLog.Write($"RDC AddIn diagnostics after registration. {RdcAddInRegistration.GetDiagnostics()}");

                _pipeSignalServer = new PipeSignalServer();
                _pipeSignalServer.SignalReceived += OnPipeSignalReceived;
                _pipeSignalServer.Start();
            }
            else
            {
                AppLog.Write($"COM plug-in registration skipped because this process is running in a remote session. {SessionContext.Diagnostics}");
            }

            _notifyIcon.Visible = true;
            AppLog.Write($"Started. {SessionContext.Diagnostics}, RawInputKey={RawKeyboardInputWindow.KeyDisplayName}, DvcChannel={DvcChannel.Name}.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Startup failed. {ex}");
            MessageBox.Show(
                ex.Message,
                "RdpSwitcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            ExitThread();
        }
    }

    protected override void ExitThreadCore()
    {
        if (_rawKeyboardInputWindow is not null)
        {
            _rawKeyboardInputWindow.PausePressed -= OnPausePressed;
        }

        if (_pipeSignalServer is not null)
        {
            _pipeSignalServer.SignalReceived -= OnPipeSignalReceived;
        }

        _pipeSignalServer?.Dispose();
        _rawKeyboardInputWindow?.Dispose();
        DvcSignalSender.Close();

        if (!_isRemoteSession)
        {
            RdcAddInRegistration.DisableForCurrentUser();
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
        base.ExitThreadCore();
    }

    private void OnPausePressed(object? sender, RawKeyEventArgs e)
    {
        AppLog.Write($"Detected raw input. VK=0x{e.VirtualKeyCode:X2}, ScanCode=0x{e.ScanCode:X2}, Flags=0x{e.Flags:X2}.");
        if (!_pauseDoublePressDetector.RegisterPress())
        {
            _rdpWindowController.RememberForegroundRdpWindow();
            return;
        }

        if (_isRemoteSession)
        {
            SendDvcSignal();
            return;
        }

        ToggleRdp("local Pause double press");
    }

    private void OnPipeSignalReceived(object? sender, PipeSignalEventArgs e)
    {
        AppLog.Write($"Received DVC signal from plug-in. Payload={e.Payload.ReplaceLineEndings(" | ")}");
        ToggleRdp("DVC plug-in signal");
    }

    private void SendDvcSignal()
    {
        if (DvcSignalSender.TrySend(out var error))
        {
            AppLog.Write($"Sent DVC signal and received host ACK. Channel={DvcChannel.Name}");
            return;
        }

        AppLog.Write($"Failed to send DVC signal. Channel={DvcChannel.Name}, Error={error}");
        _notifyIcon.ShowBalloonTip(
            3000,
            "RdpSwitcher",
            GetDvcFailureBalloonMessage(error),
            ToolTipIcon.Warning);
    }

    private static string GetDvcFailureBalloonMessage(string? error)
    {
        if (error?.Contains("Win32Error=31", StringComparison.Ordinal) == true)
        {
            return "Host RDP plug-in is not ready. Start RdpSwitcher on the host, then close and reopen the RDP window.";
        }

        return "Could not send DVC signal to host. Check the RDC plug-in and the log.";
    }

    private void ToggleRdp(string reason)
    {
        var result = _rdpWindowController.Toggle();
        AppLog.Write($"Toggle result. Reason={reason}, Action={result.Action}, Window=0x{result.WindowHandle.ToInt64():X}.");
        ShowResult(result);
    }

    private void ShowResult(RdpToggleResult result)
    {
        if (result.Action != RdpToggleAction.NoRdpWindow)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(
            1500,
            "RdpSwitcher",
            "No running RDP window was found.",
            ToolTipIcon.Warning);
    }

    private static NotifyIcon CreateNotifyIcon(Action exit, Icon icon, bool isRemoteSession)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(SessionContext.RoleName).Enabled = false;
        menu.Items.Add($"{RawKeyboardInputWindow.KeyDisplayName} x2 cycles RDP/Host").Enabled = false;
        if (!isRemoteSession)
        {
            menu.Items.Add($"DVC Channel: {DvcChannel.Name}").Enabled = false;
            menu.Items.Add($"IPC Pipe: {IpcEndpoint.PipeName}").Enabled = false;
            menu.Items.Add($"RDC AddIn: {RdcAddInRegistration.PluginName}").Enabled = false;
            menu.Items.Add($"COM CLSID: {RdcAddInRegistration.PluginClsid}").Enabled = false;
        }

        menu.Items.Add("Open Log Folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        return new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = icon,
            Text = "RdpSwitcher",
            Visible = false
        };
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppLog.LogDirectory,
            UseShellExecute = true
        });
    }
}
