using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Core.Launcher;

/// <summary>Dark title bar, and on Windows 11 a caption and border in the window's own colors.</summary>
internal static partial class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, 1, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ColorRef("BackgroundColor"), sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ColorRef("TextColor"), sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ColorRef("BorderColor"), sizeof(int));
        }
        catch
        {
            // Ignore on environments like Wine where DWM attributes are unavailable.
        }
    }

    // COLORREF is 0x00BBGGRR.
    private static int ColorRef(string key) => Application.Current.Resources[key] is Color color ? color.R | color.G << 8 | color.B << 16 : 0;
}
