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
    public const byte VK_I = 0x49;  // marcha arriba (MyWhoosh: Shift Up)
    public const byte VK_K = 0x4B;  // marcha abajo  (MyWhoosh: Shift Down)
    public const byte VK_A = 0x41;  // dirección izquierda
    public const byte VK_D = 0x44;  // dirección derecha
    public const byte VK_U = 0x55;  // alternar UI mínima
    public const byte VK_H = 0x48;  // ocultar controles (solo versión HD)

    // Fila numérica (NO teclado numérico): emotes y reacciones de MyWhoosh (1–7).
    public const byte VK_1 = 0x31;  // emote: paz
    public const byte VK_2 = 0x32;  // emote: saludo
    public const byte VK_3 = 0x33;  // emote: choque de puños
    public const byte VK_4 = 0x34;  // emote: dab
    public const byte VK_5 = 0x35;  // emote: codo
    public const byte VK_6 = 0x36;  // emote: brindis
    public const byte VK_7 = 0x37;  // emote: pulgar arriba

    // ⚠️ El tamaño de INPUT en x64 DEBE ser 40 bytes: la unión se dimensiona a MOUSEINPUT (la más
    // grande). Con una struct simplificada de 32 bytes (solo type+KEYBDINPUT), SendInput falla con
    // ERROR_INVALID_PARAMETER (87) y NO envía NADA — verificado en hardware. Por eso las teclas no
    // llegaban a MyWhoosh aunque el teclado físico sí funcionara.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
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

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 2;
    private const uint KEYEVENTF_EXTENDEDKEY = 1;
    private const uint KEYEVENTF_SCANCODE = 8;
    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>
    /// Envía una pulsación (down + breve mantener + up) por SCANCODE de hardware, no solo por
    /// virtual-key. Los juegos como MyWhoosh (Unity / DirectInput / raw-input) leen el scancode;
    /// un SendInput con solo <c>wVk</c> NO les llega aunque el teclado físico sí funcione. Las flechas
    /// se marcan como teclas «extendidas». El breve mantener asegura que el bucle del juego lo lea.
    /// </summary>
    public void SendKeyPress(byte virtualKeyCode)
    {
        ushort scan = (ushort)MapVirtualKey(virtualKeyCode, MAPVK_VK_TO_VSC);
        uint flags = KEYEVENTF_SCANCODE | (IsExtendedKey(virtualKeyCode) ? KEYEVENTF_EXTENDEDKEY : 0);

        SendScan(scan, flags);                    // key down
        Thread.Sleep(40);                         // mantener ~1 fotograma para que el juego lo registre
        SendScan(scan, flags | KEYEVENTF_KEYUP);  // key up
    }

    private static void SendScan(ushort scan, uint flags)
    {
        var inputs = new INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags } }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Teclas «extendidas» (necesitan el flag): flechas del cursor.</summary>
    private static bool IsExtendedKey(byte vk) => vk is VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN;

    /// <summary>
    /// Emula click izquierdo (flecha izquierda).
    /// </summary>
    public void SendLeftClick() => SendKeyPress(VK_LEFT);

    /// <summary>
    /// Emula click derecho (flecha derecha).
    /// </summary>
    public void SendRightClick() => SendKeyPress(VK_RIGHT);
}