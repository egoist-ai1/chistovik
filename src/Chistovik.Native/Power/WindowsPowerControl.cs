using System.Runtime.InteropServices;
using System.Text;
using Chistovik.Core.Execution;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Power;

/// <summary>
/// Схемы питания через powrprof.dll. Корень (RootKey) всегда NULL: система сама выбирает пользовательский раздел.
/// Отсутствие параметра в схеме сообщается <see cref="FileNotFoundException"/> (ERROR_FILE_NOT_FOUND), остальные коды — Win32Exception.
/// </summary>
public sealed class WindowsPowerControl : IPowerControl
{
    public Guid GetActive()
    {
        var rc = Win32.PowerGetActiveScheme(IntPtr.Zero, out var p);
        Check(rc, "PowerGetActiveScheme");
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { Win32.LocalFree(p); }
    }

    public IReadOnlyList<Guid> ListSchemes()
    {
        var result = new List<Guid>();
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            for (uint i = 0; ; i++)
            {
                uint size = 16;
                var rc = Win32.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Win32.AccessScheme, i, mem, ref size);
                if (rc == Win32.ErrorNoMoreItems) break;
                Check(rc, "PowerEnumerate");
                result.Add(Marshal.PtrToStructure<Guid>(mem));
            }
        }
        finally { Marshal.FreeHGlobal(mem); }
        return result;
    }

    public (uint Ac, uint Dc) Read(Guid scheme, Guid sub, Guid setting)
    {
        Check(Win32.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac), $"PowerReadACValueIndex {setting}");
        Check(Win32.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc), $"PowerReadDCValueIndex {setting}");
        return (ac, dc);
    }

    public void Write(ApprovedPowerSetting c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var scheme = c.Scheme;
        var sub = c.Subgroup;
        var setting = c.Setting;
        Check(Win32.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, c.Ac), $"PowerWriteACValueIndex {setting}");
        Check(Win32.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, c.Dc), $"PowerWriteDCValueIndex {setting}");
        // Изменение активной схемы вступает в силу только после повторной установки её активной.
        if (GetActive() == scheme)
            Check(Win32.PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme (применение)");
    }

    public void SetActive(ApprovedPowerScheme c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var scheme = c.Scheme;
        Check(Win32.PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme");
    }

    /// <summary>
    /// Копирует шаблон под заданным GUID: в PowerDuplicateScheme передаётся предварительно выделенный буфер с нужным GUID
    /// (так делает и powercfg /duplicatescheme). Если система всё же создала схему под другим GUID, она удаляется и
    /// выбрасывается исключение: порт обещает именно заданный GUID. Затем записывается имя схемы.
    /// </summary>
    public void Duplicate(ApprovedSchemeCreate c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var template = c.Template;
        var wanted = c.NewScheme;
        var mine = Marshal.AllocHGlobal(16);
        var dest = mine;
        try
        {
            Marshal.StructureToPtr(wanted, mine, false);
            Check(Win32.PowerDuplicateScheme(IntPtr.Zero, ref template, ref dest), "PowerDuplicateScheme");

            var actual = Marshal.PtrToStructure<Guid>(dest);
            if (dest != mine) Win32.LocalFree(dest); // система выделила свой буфер
            if (actual != wanted)
            {
                var stray = actual;
                Win32.PowerDeleteScheme(IntPtr.Zero, ref stray);
                throw new InvalidOperationException($"PowerDuplicateScheme создал схему {actual} вместо запрошенной {wanted}; созданная схема удалена.");
            }

            var nameBytes = Encoding.Unicode.GetBytes(c.Name + "\0");
            var scheme = wanted;
            var rc = Win32.PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, nameBytes, (uint)nameBytes.Length);
            if (rc != 0)
            {
                // Схема без имени не оставляется: откатываем собственное создание.
                Win32.PowerDeleteScheme(IntPtr.Zero, ref scheme);
                Check(rc, "PowerWriteFriendlyName");
            }
        }
        finally { Marshal.FreeHGlobal(mine); }
    }

    public void Delete(ApprovedSchemeDelete c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var scheme = c.Scheme;
        Check(Win32.PowerDeleteScheme(IntPtr.Zero, ref scheme), "PowerDeleteScheme");
    }

    private static void Check(uint rc, string what)
    {
        if (rc == 0) return;
        if (rc == Win32.ErrorFileNotFound) throw new FileNotFoundException($"{what}: объект питания не найден (код 2).");
        throw Win32.Fail((int)rc, what);
    }
}
