using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Chistovik.App.Services;

/// <summary>
/// Значок файла средствами Win32, только чтение: PrivateExtractIcons достаёт значок нужного размера из exe/dll/ico без запуска,
/// SHGetFileInfo даёт значок оболочки (ярлыки, скрипты, файлы без собственных значков). HICON всегда уничтожается.
/// Вызывать из фонового потока: обращение к диску может быть долгим.
/// </summary>
internal static class NativeIcons
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;

    private static readonly HashSet<string> HasResources = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".ico", ".cpl", ".scr", ".ocx",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string fileName, int iconIndex, int cx, int cy,
        [In, Out] IntPtr[] icons, [In, Out] uint[]? ids, uint count, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Значок файла как замороженное изображение или null. Исключения Win32/WPF наружу не выходят.</summary>
    public static ImageSource? Extract(IconRequest request, int pixels)
    {
        var handle = IntPtr.Zero;
        try
        {
            var path = request.Path;
            if (request.Index is not null || HasResources.Contains(Path.GetExtension(path)))
                handle = FromResource(path, request.Index ?? 0, pixels);
            if (handle == IntPtr.Zero) handle = FromShell(path);
            if (handle == IntPtr.Zero) return null;

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or COMException or ExternalException
                                      or DllNotFoundException or EntryPointNotFoundException or NotSupportedException
                                      or IOException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        }
    }

    private static IntPtr FromResource(string path, int index, int pixels)
    {
        var icons = new IntPtr[1];
        var got = PrivateExtractIcons(path, index, pixels, pixels, icons, null, 1, 0);
        return got is 0 or uint.MaxValue ? IntPtr.Zero : icons[0];
    }

    private static IntPtr FromShell(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        return result == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
    }
}
