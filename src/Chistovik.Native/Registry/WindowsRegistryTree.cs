using System.Runtime.InteropServices;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Registry;

/// <summary>
/// Обход и удаление поддеревьев реестра по хэндлам. Каждый ключ открывается с REG_OPTION_OPEN_LINK (ссылка не разыменовывается),
/// всегда Registry64. RegCopyTree/RegDeleteTree/RegDeleteKey не используются: они идут по путям и ссылкам.
/// </summary>
public sealed class WindowsRegistryTree : IRegistryTree
{
    public IRegTreeHandle? OpenTree(RegLocation loc, bool forWrite)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var (root, subKey) = WindowsRegistry.Resolve(loc);
        var sam = WindowsRegTreeHandle.Sam(forWrite);

        var rc = Win32.RegOpenKeyEx(root, subKey, Win32.RegOptionOpenLink, sam, out var handle);
        var where = WindowsRegistry.Describe(loc);
        return WindowsRegTreeHandle.FromResult(rc, handle, where, forWrite, "RegOpenKeyEx");
    }
}

/// <summary>Открытый ключ для обхода дерева. Не следует по ссылкам; значения отдаёт сырыми байтами.</summary>
internal sealed class WindowsRegTreeHandle : IRegTreeHandle
{
    private const uint RegCreatedNewKey = 1; // REG_OPENED_EXISTING_KEY = 2
    private const int KeyNameInformation = 3;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    private readonly SafeRegistryHandle _key;
    private readonly string _where;
    private readonly bool _writable;
    private string? _resolved;

    private WindowsRegTreeHandle(SafeRegistryHandle key, string where, bool writable)
    {
        _key = key;
        _where = where;
        _writable = writable;
        IsSymbolicLink = DetectLink(key);
    }

    internal static uint Sam(bool write) =>
        Win32Cleanup.KeyRead | Win32.KeyWow64_64Key | (write ? Win32Cleanup.KeyWrite | Win32Cleanup.Delete : 0);

    internal static WindowsRegTreeHandle? FromResult(int rc, SafeRegistryHandle handle, string where, bool writable, string api)
    {
        if (rc == Win32.ErrorFileNotFound || rc == Win32.ErrorPathNotFound)
        {
            handle.Dispose();
            return null;
        }
        if (rc == Win32.ErrorAccessDenied)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException($"Отказано в доступе к ключу реестра {where} (ERROR_ACCESS_DENIED)");
        }
        if (rc != Win32.ErrorSuccess)
        {
            handle.Dispose();
            throw Win32.Fail(rc, $"{api} {where}");
        }
        return new WindowsRegTreeHandle(handle, where, writable);
    }

    public string ResolvedPath => _resolved ??= QueryName();

    /// <summary>
    /// Ключ открыт с OPEN_LINK, поэтому ссылка не разыменована. Признак ссылки: значение SymbolicLinkValue типа REG_LINK
    /// (его создаёт система у ключей-ссылок; у обычного ключа его нет). Ссылки в промежуточных компонентах пути ловит сверка
    /// ResolvedPath в Core.
    /// </summary>
    public bool IsSymbolicLink { get; }

    public RegKeyInfo Info()
    {
        var rc = Win32Cleanup.RegQueryInfoKey(_key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var subKeys, IntPtr.Zero, IntPtr.Zero,
            out var values, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (rc != Win32.ErrorSuccess) throw Win32.Fail(rc, $"RegQueryInfoKey {_where}");
        return new RegKeyInfo((int)subKeys, (int)values);
    }

    public IReadOnlyList<string> SubKeyNames()
    {
        var names = new List<string>();
        var buf = new char[256]; // максимум имени подключа 255 символов
        for (uint i = 0; ; i++)
        {
            var len = (uint)buf.Length;
            var rc = Win32Cleanup.RegEnumKeyEx(_key, i, buf, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (rc == Win32.ErrorNoMoreItems) break;
            if (rc != Win32.ErrorSuccess) throw Win32.Fail(rc, $"RegEnumKeyEx {_where}");
            names.Add(new string(buf, 0, (int)len));
        }
        return names;
    }

    public IRegTreeHandle? OpenSub(string name, bool forWrite)
    {
        var single = SingleComponent(name);
        if (forWrite && !_writable) throw new InvalidOperationException($"Ключ {_where} открыт без права записи.");
        if (forWrite) RequireNotLink();
        // forWrite у подключа значит «удалить»: нужны чтение (перечисление детей) и DELETE. KEY_WRITE здесь не требуется, а его отсутствие у
        // подключа дало бы ложный отказ reg-tree-not-deletable там, где ключ удалить можно.
        var sam = forWrite ? Win32Cleanup.KeyRead | Win32.KeyWow64_64Key | Win32Cleanup.Delete : Sam(false);
        var rc = Win32Cleanup.RegOpenKeyExSub(_key, single, Win32.RegOptionOpenLink, sam, out var h);
        return FromResult(rc, h, _where + "\\" + single, forWrite, "RegOpenKeyEx");
    }

    public IReadOnlyList<(string Name, RegType Type, byte[] Raw)> RawValues() => RawValues(long.MaxValue);

    public IReadOnlyList<(string Name, RegType Type, byte[] Raw)> RawValues(long maxTotalBytes)
    {
        var result = new List<(string, RegType, byte[])>();
        var budget = maxTotalBytes;
        foreach (var name in ValueNames())
        {
            // Значение могло исчезнуть между перечислением и чтением: пропускаем его.
            if (!TryReadRaw(name, budget, out var type, out var raw)) continue;
            budget -= raw.Length;
            result.Add((name, MapType(type), raw));
        }
        return result;
    }

    public IRegTreeHandle CreateSub(string name, out bool created)
    {
        created = false;
        var single = SingleComponent(name);
        RequireWritable();
        RequireNotLink();
        // REG_OPTION_OPEN_LINK: если на месте имени уже стоит ссылка, открывается она сама (disposition = существующий ключ), а не её цель.
        var rc = Win32Cleanup.RegCreateKeyExSub(_key, single, 0, null, Win32.RegOptionOpenLink, Sam(true), IntPtr.Zero, out var h, out var disposition);
        if (rc == Win32.ErrorAccessDenied)
        {
            h.Dispose();
            throw new UnauthorizedAccessException($"Отказано в доступе к ключу реестра {_where} (ERROR_ACCESS_DENIED)");
        }
        if (rc != Win32.ErrorSuccess)
        {
            h.Dispose();
            throw Win32.Fail(rc, $"RegCreateKeyEx {_where}\\{single}");
        }
        created = disposition == RegCreatedNewKey;
        return new WindowsRegTreeHandle(h, _where + "\\" + single, true);
    }

    public void SetRaw(string name, RegType type, byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(raw);
        RequireWritable();
        RequireNotLink();
        var rc = Win32.RegSetValueEx(_key, name, 0, ToRegType(type), raw, (uint)raw.Length);
        if (rc != Win32.ErrorSuccess) throw Win32.Fail(rc, $"RegSetValueEx '{name}' в {_where}");
    }

    public void DeleteSelf()
    {
        RequireWritable();
        if (IsSymbolicLink) throw new InvalidOperationException($"Ключ {_where} является символической ссылкой, удаление запрещено.");
        var status = Win32Cleanup.NtDeleteKey(_key);
        if (status != 0)
            throw new IOException($"NtDeleteKey для {_where} вернул NTSTATUS 0x{status:X8}.", status);
    }

    public void Dispose() => _key.Dispose();

    // ---------- вспомогательное ----------

    private void RequireWritable()
    {
        if (!_writable) throw new InvalidOperationException($"Ключ {_where} открыт без права записи.");
    }

    // Запись, создание подключей и открытие подключа на удаление через ключ-ссылку запрещены: относительный путь от хэндла ссылки
    // Windows разрешает по цели ссылки, то есть операция ушла бы в чужое дерево.
    private void RequireNotLink()
    {
        if (IsSymbolicLink) throw new InvalidOperationException($"Ключ {_where} является символической ссылкой, операция запрещена.");
    }

    // Имя подключа это один компонент: путь с «\» мог бы пройти через неучтённую ссылку.
    private static string SingleComponent(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\\') || name.Contains('\0')) throw new ArgumentException("Имя подключа не может содержать '\\'.", nameof(name));
        return name;
    }

    private List<string> ValueNames()
    {
        var names = new List<string>();
        var buf = new char[16384]; // максимум имени значения 16383 символа
        for (uint i = 0; ; i++)
        {
            var len = (uint)buf.Length;
            var rc = Win32.RegEnumValue(_key, i, buf, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (rc == Win32.ErrorNoMoreItems) break;
            if (rc != Win32.ErrorSuccess) throw Win32.Fail(rc, $"RegEnumValue {_where}");
            names.Add(new string(buf, 0, (int)len));
        }
        return names;
    }

    // limit: сколько байт ещё можно прочитать. Первый вызов (без буфера) даёт только размер: если он больше остатка,
    // данные не читаются и не выделяются (RegValueTooLargeException).
    private bool TryReadRaw(string name, long limit, out uint type, out byte[] data)
    {
        uint size = 0;
        data = [];
        var rc = Win32.RegQueryValueEx(_key, name, IntPtr.Zero, out type, null, ref size);
        if (rc == Win32.ErrorFileNotFound) return false;
        if (rc != Win32.ErrorSuccess && rc != Win32.ErrorMoreData) throw Win32.Fail(rc, $"RegQueryValueEx '{name}' в {_where}");
        for (var attempt = 0; ; attempt++)
        {
            // Значение могло вырасти между вызовами: размер проверяется перед каждым выделением буфера.
            if (size > limit) throw new RegValueTooLargeException();
            data = new byte[size];
            rc = Win32.RegQueryValueEx(_key, name, IntPtr.Zero, out type, data, ref size);
            if (rc == Win32.ErrorSuccess) break;
            if (rc == Win32.ErrorFileNotFound) return false;
            if (rc != Win32.ErrorMoreData || attempt >= 4) throw Win32.Fail(rc, $"RegQueryValueEx '{name}' в {_where}");
        }
        if (data.Length != size) Array.Resize(ref data, (int)size);
        return true;
    }

    // Типы вне RegType (REG_LINK, REG_RESOURCE_LIST, ...) отдаются как Other: Core отказывается от такого дерева (reg-tree-bad-type),
    // а не записывает его как Binary и не портит тип при восстановлении.
    private static RegType MapType(uint t) => t switch
    {
        Win32.RegBinary => RegType.Binary,
        Win32.RegNone => RegType.None,
        Win32.RegSz => RegType.String,
        Win32.RegExpandSz => RegType.ExpandString,
        Win32.RegDword => RegType.DWord,
        Win32.RegQword => RegType.QWord,
        Win32.RegMultiSz => RegType.MultiString,
        _ => RegType.Other,
    };

    private static uint ToRegType(RegType t) => t switch
    {
        RegType.None => Win32.RegNone,
        RegType.String => Win32.RegSz,
        RegType.ExpandString => Win32.RegExpandSz,
        RegType.Binary => Win32.RegBinary,
        RegType.DWord => Win32.RegDword,
        RegType.QWord => Win32.RegQword,
        RegType.MultiString => Win32.RegMultiSz,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Неизвестный тип значения."),
    };

    private static bool DetectLink(SafeRegistryHandle key)
    {
        uint size = 0;
        var rc = Win32.RegQueryValueEx(key, "SymbolicLinkValue", IntPtr.Zero, out var type, null, ref size);
        return (rc == Win32.ErrorSuccess || rc == Win32.ErrorMoreData) && type == Win32.RegLink;
    }

    private string QueryName()
    {
        uint capacity = 1024;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var mem = Marshal.AllocHGlobal((int)capacity);
            try
            {
                var status = Win32.NtQueryKey(_key, KeyNameInformation, mem, capacity, out var needed);
                if (status == 0)
                {
                    var bytes = Marshal.ReadInt32(mem); // KEY_NAME_INFORMATION.NameLength в байтах
                    return Marshal.PtrToStringUni(mem + 4, bytes / 2);
                }
                if (status != StatusBufferOverflow && status != StatusBufferTooSmall)
                    throw new InvalidOperationException($"NtQueryKey для {_where} вернул NTSTATUS 0x{status:X8}.");
                capacity = Math.Max(needed, capacity * 2);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
        throw new InvalidOperationException($"NtQueryKey для {_where}: не удалось подобрать размер буфера.");
    }
}
