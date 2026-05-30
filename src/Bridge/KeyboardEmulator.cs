using System.Runtime.InteropServices;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Emula pulsaciones de teclas usando la API SendInput de Windows (user32.dll).
/// Traduce eventos del Click V2 (izquierda/derecha) a teclas para MyWoosh.
/// </summary>
public class KeyboardEmulator
{
    // Virtual Key Codes
    public const byte VK_LEFT = 0x25;
    public const byte VK_RIGHT = 0x27;
    public const byte VK_UP = 0x26;
    public const byte VK_DOWN = 0x28;
    public const byte VK_SPACE = 0x20;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_TAB = 0x09;

    // Letras útiles para apps de ciclismo indoor (cambio de marcha virtual, dirección…).
    public const byte VK_I = 0x49;  // marcha arriba (MyWoosh: Shift Up)
    public const byte VK_K = 0x4B;  // marcha abajo  (MyWoosh: Shift Down)
    public const byte VK_A = 0x41;  // dirección izquierda
    public const byte VK_D = 0x44;  // dirección derecha
    public const byte VK_U = 0x55;  // alternar UI mínima

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 2;

    /// <summary>
    /// Envía una pulsación de tecla (down + up).
    /// </summary>
    public void SendKeyPress(byte virtualKeyCode)
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = virtualKeyCode, dwFlags = 0 }
        };

        // Key up
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = virtualKeyCode, dwFlags = KEYEVENTF_KEYUP }
        };

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Emula click izquierdo (flecha izquierda).
    /// </summary>
    public void SendLeftClick() => SendKeyPress(VK_LEFT);

    /// <summary>
    /// Emula click derecho (flecha derecha).
    /// </summary>
    public void SendRightClick() => SendKeyPress(VK_RIGHT);
}