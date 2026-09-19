using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VoiceOS.Core.Activation;

/// <summary>
/// Installs a WH_KEYBOARD_LL system hook on a dedicated thread with its own message pump.
/// Fires KeyDown/KeyUp only on clean press/release edges; physical key repeats are suppressed
/// by tracking the logical key-down state (not by lParam repeat-count, which is absent from
/// the low-level hook structure).
/// Injected keystrokes (LLKHF_INJECTED) are ignored so our own SendInput never triggers the hook.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    private readonly int _targetVk;
    private readonly ILogger<GlobalKeyboardHook> _logger;
    private readonly LowLevelKeyboardProc _hookProc;

    private IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _keyIsDown;
    private bool _disposed;

    public event EventHandler? KeyDown;
    public event EventHandler? KeyUp;

    public GlobalKeyboardHook(int targetVk, ILogger<GlobalKeyboardHook> logger)
    {
        _targetVk = targetVk;
        _logger = logger;
        _hookProc = HookCallback; // Keep delegate alive for the lifetime of this object
    }

    public void Install()
    {
        _hookThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "VoiceOS-KeyboardHook"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private void RunMessageLoop()
    {
        _hookThreadId = GetCurrentThreadId();

        using var module = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module.ModuleName), 0);

        if (_hookHandle == IntPtr.Zero)
        {
            _logger.LogError("SetWindowsHookEx failed with error {Error}", Marshal.GetLastWin32Error());
            return;
        }

        _logger.LogDebug("Keyboard hook installed, thread {Id}", _hookThreadId);

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _logger.LogDebug("Keyboard hook removed");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if (data.vkCode == (uint)_targetVk && (data.flags & LLKHF_INJECTED) == 0)
            {
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyDown && !_keyIsDown)
                {
                    _keyIsDown = true;
                    KeyDown?.Invoke(this, EventArgs.Empty);
                }
                else if (isKeyUp && _keyIsDown)
                {
                    _keyIsDown = false;
                    KeyUp?.Invoke(this, EventArgs.Empty);
                }
                // Repeated WM_KEYDOWN while _keyIsDown is already true: silently dropped.
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        _hookThread?.Join(TimeSpan.FromSeconds(2));
    }
}
