using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Core.Launcher;

internal static partial class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    public static void Apply(Window window) => DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, 1, sizeof(int));
}
