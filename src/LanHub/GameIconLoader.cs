using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LanHub;

internal static class GameIconLoader
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExe, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? TryLoadFromExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        var hIcon = IntPtr.Zero;
        try
        {
            hIcon = ExtractIconW(IntPtr.Zero, exePath, 0);
            if (hIcon == IntPtr.Zero || hIcon == new IntPtr(1))
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero && hIcon != new IntPtr(1))
                DestroyIcon(hIcon);
        }
    }
}
