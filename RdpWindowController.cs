using System.Diagnostics;

namespace RdpSwitcher;

internal sealed class RdpWindowController
{
    private const string RdpProcessName = "mstsc";

    private readonly List<IntPtr> _cycleOrder = [];

    public RdpToggleResult Toggle()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var rdpWindows = FindRdpWindows();
        var foregroundRdpWindow = ResolveForegroundRdpWindow(foregroundWindow, rdpWindows);
        RefreshCycleOrder(rdpWindows, foregroundRdpWindow);

        if (foregroundRdpWindow != IntPtr.Zero)
        {
            var currentIndex = _cycleOrder.IndexOf(foregroundRdpWindow);
            var nextRdpWindow = GetNextRdpWindow(currentIndex);

            MinimizeWindow(foregroundRdpWindow);

            if (nextRdpWindow != IntPtr.Zero)
            {
                RestoreAndActivate(nextRdpWindow);
                return new RdpToggleResult(RdpToggleAction.Restored, nextRdpWindow);
            }

            return new RdpToggleResult(RdpToggleAction.Minimized, foregroundRdpWindow);
        }

        var targetWindow = ResolveFirstRdpWindow(rdpWindows);
        if (targetWindow == IntPtr.Zero)
        {
            return new RdpToggleResult(RdpToggleAction.NoRdpWindow, IntPtr.Zero);
        }

        RestoreAndActivate(targetWindow);
        return new RdpToggleResult(RdpToggleAction.Restored, targetWindow);
    }

    public void RememberForegroundRdpWindow()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var rdpWindows = FindRdpWindows();
        var foregroundRdpWindow = ResolveForegroundRdpWindow(foregroundWindow, rdpWindows);
        if (foregroundRdpWindow != IntPtr.Zero)
        {
            RefreshCycleOrder(rdpWindows, foregroundRdpWindow);
        }
    }

    private void RefreshCycleOrder(IReadOnlyCollection<IntPtr> rdpWindows, IntPtr foregroundWindow)
    {
        _cycleOrder.RemoveAll(window => !rdpWindows.Contains(window));

        if (_cycleOrder.Count == 0 && rdpWindows.Contains(foregroundWindow))
        {
            _cycleOrder.Add(foregroundWindow);
        }

        foreach (var window in rdpWindows)
        {
            if (!_cycleOrder.Contains(window))
            {
                _cycleOrder.Add(window);
            }
        }
    }

    private IntPtr GetNextRdpWindow(int currentIndex)
    {
        if (currentIndex < 0 || currentIndex >= _cycleOrder.Count - 1)
        {
            return IntPtr.Zero;
        }

        return _cycleOrder[currentIndex + 1];
    }

    private IntPtr ResolveFirstRdpWindow(IReadOnlyCollection<IntPtr> rdpWindows)
    {
        _cycleOrder.RemoveAll(window => !rdpWindows.Contains(window));
        if (_cycleOrder.Count == 0)
        {
            _cycleOrder.AddRange(rdpWindows);
        }

        return _cycleOrder.Count > 0 ? _cycleOrder[0] : IntPtr.Zero;
    }

    private static void RestoreAndActivate(IntPtr hWnd)
    {
        NativeMethods.ShowWindowAsync(hWnd, NativeMethods.SW_RESTORE);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hWnd);
    }

    private static void MinimizeWindow(IntPtr hWnd)
    {
        NativeMethods.ShowWindowAsync(hWnd, NativeMethods.SW_MINIMIZE);
        NativeMethods.PostMessage(
            hWnd,
            NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MINIMIZE),
            IntPtr.Zero);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_FORCEMINIMIZE);
    }

    private static IntPtr ResolveForegroundRdpWindow(IntPtr foregroundWindow, IReadOnlyCollection<IntPtr> rdpWindows)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (rdpWindows.Contains(foregroundWindow))
        {
            return foregroundWindow;
        }

        var rootWindow = NativeMethods.GetAncestor(foregroundWindow, NativeMethods.GA_ROOT);
        if (rdpWindows.Contains(rootWindow))
        {
            return rootWindow;
        }

        var rootOwnerWindow = NativeMethods.GetAncestor(foregroundWindow, NativeMethods.GA_ROOTOWNER);
        if (rdpWindows.Contains(rootOwnerWindow))
        {
            return rootOwnerWindow;
        }

        if (IsRdpWindow(foregroundWindow))
        {
            return foregroundWindow;
        }

        if (IsRdpWindow(rootWindow))
        {
            return rootWindow;
        }

        return IsRdpWindow(rootOwnerWindow) ? rootOwnerWindow : IntPtr.Zero;
    }

    private static List<IntPtr> FindRdpWindows()
    {
        var windows = new List<IntPtr>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!IsCandidateTopLevelWindow(hWnd))
            {
                return true;
            }

            if (!IsRdpWindow(hWnd))
            {
                return true;
            }

            windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsCandidateTopLevelWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return false;
        }

        return NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd);
    }

    private static bool IsRdpWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, RdpProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
