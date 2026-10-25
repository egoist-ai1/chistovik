using System.Runtime.InteropServices;
using System.Text;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Setup;

/// <summary>
/// Блок окружения для деинсталлятора: только системные переменные (без HKCU\Environment и без переменных процесса агента),
/// TEMP и TMP заменены на защищённый каталог. Это закрывает __COMPAT_LAYER, COR_PROFILER, подмену PATH и пользовательский TEMP.
/// </summary>
internal static class CleanEnvironment
{
    public static IntPtr Build(string tempDir)
    {
        if (!Win32Setup.CreateEnvironmentBlock(out var system, IntPtr.Zero, false))
            throw Win32.Fail(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock");
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var offset = 0; ; )
            {
                var entry = Marshal.PtrToStringUni(IntPtr.Add(system, offset)) ?? string.Empty;
                if (entry.Length == 0) break;
                offset += (entry.Length + 1) * 2;
                var eq = entry.IndexOf('=', 1); // имена вида "=C:" начинаются со знака равенства
                if (eq > 0) variables[entry[..eq]] = entry[(eq + 1)..];
            }
        }
        finally
        {
            Win32Setup.DestroyEnvironmentBlock(system);
        }

        variables["TEMP"] = tempDir;
        variables["TMP"] = tempDir;

        var text = new StringBuilder();
        foreach (var (name, value) in variables) text.Append(name).Append('=').Append(value).Append('\0');
        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
    }

    public static void Free(IntPtr block)
    {
        if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
    }
}
