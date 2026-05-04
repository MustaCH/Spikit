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
[Flags]
internal enum HotkeyModifiers : uint
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
