using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpSwitcher;

internal sealed class RawKeyboardInputWindow : NativeWindow, IDisposable
{
    private const string DisplayName = "Pause";
    private bool _disposed;

    public RawKeyboardInputWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "RdpSwitcherRawInputWindow"
        });

        RegisterKeyboardInput();
    }

    public event EventHandler<RawKeyEventArgs>? PausePressed;

    public static string KeyDisplayName => DisplayName;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DestroyHandle();
        _disposed = true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_INPUT)
        {
            HandleRawInput(m.LParam);
        }

        base.WndProc(ref m);
    }

    private void RegisterKeyboardInput()
    {
        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = Handle
            }
        };

        var size = (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>();
        if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length, size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register raw keyboard input.");
        }
    }

    private void HandleRawInput(IntPtr rawInputHandle)
    {
        var dataSize = 0u;
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>();
        var result = NativeMethods.GetRawInputData(
            rawInputHandle,
            NativeMethods.RID_INPUT,
            IntPtr.Zero,
            ref dataSize,
            headerSize);

        if (result == uint.MaxValue || dataSize == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)dataSize);
        try
        {
            result = NativeMethods.GetRawInputData(
                rawInputHandle,
                NativeMethods.RID_INPUT,
                buffer,
                ref dataSize,
                headerSize);

            if (result == uint.MaxValue || result != dataSize)
            {
                return;
            }

            var input = Marshal.PtrToStructure<NativeMethods.RawInput>(buffer);
            if (input.header.dwType != NativeMethods.RIM_TYPEKEYBOARD)
            {
                return;
            }

            if (!IsKeyDownMessage(input.keyboard.Message))
            {
                return;
            }

            if (!IsPauseKey(input.keyboard.VKey))
            {
                return;
            }

            PausePressed?.Invoke(
                this,
                new RawKeyEventArgs(input.keyboard.VKey, input.keyboard.MakeCode, input.keyboard.Flags));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsKeyDownMessage(uint message)
    {
        return message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
    }

    private static bool IsPauseKey(int virtualKeyCode)
    {
        return virtualKeyCode is NativeMethods.VK_PAUSE or NativeMethods.VK_CANCEL;
    }
}
