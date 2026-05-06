using System.Runtime.InteropServices;

namespace Spikit.Native;

internal static class User32
{
    private const string Dll = "user32.dll";

    // Registra un hotkey global. fsModifiers usa la enum HotkeyModifiers.
    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    // Libera un hotkey previamente registrado por RegisterHotKey.
    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Inyecta eventos sintéticos de teclado/mouse en la cola del thread foreground.
    [DllImport(Dll, SetLastError = true)]
    public static extern uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    // Devuelve el HWND de la ventana actualmente con foco.
    [DllImport(Dll)]
    public static extern IntPtr GetForegroundWindow();

    // Trae una ventana al frente y le da foco. Sujeto a las restricciones SetForegroundWindow.
    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // Devuelve el thread ID dueño del HWND y, opcional, el process ID en lpdwProcessId.
    [DllImport(Dll, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Estado actual de una virtual key. El bit alto (0x8000) indica que está apretada.
    // Lo usamos para detectar release en push-to-talk (RegisterHotKey solo emite al press).
    [DllImport(Dll)]
    public static extern short GetAsyncKeyState(int vKey);
}

// Subset de Win32 virtual-key codes que necesitamos en V1. Agregar a demanda.
public static class VirtualKeys
{
    public const uint Shift = 0x10;
    public const uint Control = 0x11;
    public const uint Alt = 0x12;       // VK_MENU
    public const uint LWin = 0x5B;
    public const uint RWin = 0x5C;
    public const uint Space = 0x20;
    public const uint Escape = 0x1B;
    public const uint Enter = 0x0D;
    public const uint Tab = 0x09;
    public const uint Back = 0x08;
    public const uint M = 0x4D;

    public static string GetName(uint vk) => vk switch
    {
        Space => "Space",
        Escape => "Esc",
        Enter => "Enter",
        Tab => "Tab",
        Back => "Backspace",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),                   // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),                   // A-Z
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",                         // F1-F24
        _ => $"VK_{vk:X2}",
    };
}

// Mensaje destinado a una message-only window (HWND_MESSAGE).
internal static class SpecialWindowHandles
{
    public static readonly IntPtr HWND_MESSAGE = new(-3);
}

// Tipo de evento sintético para SendInput.
internal enum InputType : uint
{
    Mouse = 0,
    Keyboard = 1,
    Hardware = 2,
}

// Flags para KEYBDINPUT.dwFlags.
[Flags]
internal enum KeyEventF : uint
{
    KeyDown = 0x0000,
    ExtendedKey = 0x0001,
    KeyUp = 0x0002,
    Unicode = 0x0004,
    ScanCode = 0x0008,
}

// Flags para MOUSEINPUT.dwFlags.
[Flags]
internal enum MouseEventF : uint
{
    Move = 0x0001,
    LeftDown = 0x0002,
    LeftUp = 0x0004,
    RightDown = 0x0008,
    RightUp = 0x0010,
    MiddleDown = 0x0020,
    MiddleUp = 0x0040,
    Wheel = 0x0800,
    Absolute = 0x8000,
}

// Modificadores aceptados por RegisterHotKey.fsModifiers.
// Public porque HotkeyDefinition (Models/) lo expone — son flags estables, sin leak de P/Invoke.
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

// Mensaje WM enviado a la ventana cuando dispara el hotkey registrado.
internal static class WindowMessages
{
    public const int WM_HOTKEY = 0x0312;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public InputType type;
    public InputUnion U;
    public static int Size => Marshal.SizeOf<INPUT>();
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public KeyEventF dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public MouseEventF dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}
