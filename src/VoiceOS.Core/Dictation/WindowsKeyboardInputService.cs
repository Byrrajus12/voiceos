using System.Runtime.InteropServices;

namespace VoiceOS.Core.Dictation;

/// <summary>
/// Trusted global text-input implementation. All foreground text injection goes
/// through this bounded SendInput wrapper; target validation remains in backends.
/// </summary>
public sealed class WindowsKeyboardInputService : IKeyboardInputService
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const ushort Control = 0x11;
    private const ushort Shift = 0x10;
    private const ushort Alt = 0x12;
    private const ushort LeftWindows = 0x5B;
    private const ushort RightWindows = 0x5C;
    private const ushort V = 0x56;
    private const ushort Return = 0x0D;

    public bool HasActiveModifiers()
        => IsDown(Control) || IsDown(Shift) || IsDown(Alt) || IsDown(LeftWindows) || IsDown(RightWindows);

    public KeyboardDispatchResult SendPasteShortcut() => Dispatch([
        VirtualKey(Control), VirtualKey(V), VirtualKey(V, KeyUp), VirtualKey(Control, KeyUp)
    ]);

    public KeyboardDispatchResult SendUnicodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return default;

        var inputs = new List<Input>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character is '\r' or '\n')
            {
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                inputs.Add(VirtualKey(Return));
                inputs.Add(VirtualKey(Return, KeyUp));
            }
            else
            {
                inputs.Add(UnicodeInput(character));
                inputs.Add(UnicodeInput(character, KeyUp));
            }
        }
        return Dispatch([.. inputs]);
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static KeyboardDispatchResult Dispatch(Input[] inputs)
    {
        Marshal.SetLastPInvokeError(0);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return new(inputs.Length, checked((int)sent), Marshal.GetLastPInvokeError());
    }

    private static Input VirtualKey(ushort key, uint flags = 0) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } }
    };

    private static Input UnicodeInput(char value, uint flags = 0) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { ScanCode = value, Flags = Unicode | flags } }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput { public uint Message; public ushort Low; public ushort High; }
}
