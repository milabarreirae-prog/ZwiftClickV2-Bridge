using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Violeta.Interop;

/// <summary>
/// Efectos nativos de Windows 11 para que la ventana se sienta del sistema: modo oscuro en la barra
/// de título, esquinas redondeadas y (opcional) material Mica. Todo vía DWM (dwmapi.dll) con
/// degradación elegante: si el SO es viejo, las llamadas fallan en silencio y la app sigue igual.
/// </summary>
public static class WindowEffects
{
    // Atributos de DwmSetWindowAttribute (Windows 10 2004+ / Windows 11).
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // Preferencias de esquina.
    private const int DWMWCP_ROUND = 2;

    // Tipos de backdrop del sistema.
    public enum Backdrop
    {
        Auto = 0,
        None = 1,
        Mica = 2,
        Acrylic = 3,
        Tabbed = 4
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Aplica el acabado Windows 11 a la ventana: barra oscura + esquinas redondeadas. Llamar en
    /// <c>SourceInitialized</c> (cuando ya hay HWND). No lanza nunca.
    /// </summary>
    public static void ApplyWindows11(Window window, Backdrop backdrop = Backdrop.None)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            if (backdrop != Backdrop.None)
            {
                int type = (int)backdrop;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
            }
        }
        catch
        {
            // SO antiguo o DWM no disponible: la app funciona igual, solo sin el acabado nativo.
        }
    }

    // ── Arreglo de maximizado para ventanas sin marco ────────────────────────
    // Sin esto, una ventana WindowStyle=None tapa la barra de tareas al maximizar y recorta los
    // bordes. Interceptamos WM_GETMINMAXINFO y limitamos el tamaño al área de trabajo del monitor.

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// <summary>Conecta el arreglo de maximizado a la ventana. Llamar en <c>SourceInitialized</c>.</summary>
    public static void EnableMaximizeFix(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(MaximizeHook);
        }
        catch { /* no fatal */ }
    }

    private static IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        try
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return IntPtr.Zero;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            RECT work = mi.rcWork, full = mi.rcMonitor;
            mmi.ptMaxPosition = new POINT { X = work.Left - full.Left, Y = work.Top - full.Top };
            mmi.ptMaxSize = new POINT { X = work.Right - work.Left, Y = work.Bottom - work.Top };
            mmi.ptMaxTrackSize = mmi.ptMaxSize;
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        catch { /* no fatal */ }
        return IntPtr.Zero;
    }
}
