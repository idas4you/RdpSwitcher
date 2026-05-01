namespace RdpSwitcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Local\\RdpSwitcher.PauseBreakToggle", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "RdpSwitcher is already running.",
                "RdpSwitcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        AppLog.Write("Application starting.");
        Application.Run(new TrayApplicationContext());
        AppLog.Write("Application stopped.");
    }
}
