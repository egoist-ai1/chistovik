using System.Runtime.InteropServices;
using System.Text;
using Chistovik.Core.Execution;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Registry;

/// <summary>Открытый ключ реестра. Освобождает хэндл в Dispose. Поведение значений строго по типу, без преобразований.</summary>
public sealed class WindowsRegKeyHandle : IRegKeyHandle
{
    private const int KeyNameInformation = 3;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    private readonly SafeRegistryHandle _key;
    private readonly RegLocation _location;
    private readonly bool _writable;
    private string? _resolved;

    internal WindowsRegKeyHandle(SafeRegistryHandle key, RegLocation location, bool writable)
    {
        _key = key;
        _location = location;
        _writable = writable;
        IsSymbolicLink = DetectLink(key);
    }

    /// <summary>Фактический путь (\REGISTRY\MACHINE\... или \REGISTRY\USER\&lt;sid&gt;\...), получен на открытом хэндле.</summary>
    public string ResolvedPath => _resolved ??= QueryName();

    /// <summary>
    /// Ключ открыт с REG_OPTION_OPEN_LINK, поэтому ссылка не разыменована. Признак ссылки: значение SymbolicLinkValue типа REG_LINK
    /// (его создаёт сама система у ключей-ссылок; у обычного ключа такого значения нет). Ссылки в промежуточных компонентах пути
    /// ловятся сравнением ResolvedPath в Core.
    /// </summary>
    public bool IsSymbolicLink { get; }

    public RegValueState Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint size = 0;
        var rc = Win32.RegQueryValueEx(_key, name, IntPtr.Zero, out var type, null, ref size);
        if (rc == Win32.ErrorFileNotFound) return new RegValueState(false, RegType.None, null);
        if (rc != Win32.ErrorSuccess && rc != Win32.ErrorMoreData) throw Win32.Fail(rc, $"RegQueryValueEx '{name}' в {Where()}");

        // Значение могло вырасти между вызовами: повторяем, пока буфер мал.
        byte[] data;
        for (var attempt = 0; ; attempt++)
        {
            data = new byte[size];
            rc = Win32.RegQueryValueEx(_key, name, IntPtr.Zero, out type, data, ref size);
            if (rc == Win32.ErrorSuccess) break;
            if (rc == Win32.ErrorFileNotFound) return new RegValueState(false, RegType.None, null);
            if (rc != Win32.ErrorMoreData || attempt >= 4) throw Win32.Fail(rc, $"RegQueryValueEx '{name}' в {Where()}");
        }
        if (data.Length != size) Array.Resize(ref data, (int)size);
        return Decode(name, type, data);
    }

    public void Write(ApprovedRegistryWrite w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (!_writable) throw new InvalidOperationException($"Ключ {Where()} открыт без права записи.");
        if (IsSymbolicLink) throw new InvalidOperationException($"Ключ {Where()} является символической ссылкой, запись запрещена.");
        var l = w.Location;
        if (l.Root != _location.Root
            || !string.Equals(l.UserSid, _location.UserSid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals((l.KeyPath ?? "").Trim('\\'), (_location.KeyPath ?? "").Trim('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Разрешение выдано для {WindowsRegistry.Describe(l)}, а хэндл открыт для {Where()}.");

        if (!w.Value.Exists)
        {
            var del = Win32.RegDeleteValue(_key, l.ValueName);
            if (del != Win32.ErrorSuccess && del != Win32.ErrorFileNotFound) throw Win32.Fail(del, $"RegDeleteValue '{l.ValueName}' в {Where()}");
            return;
        }

        var (type, bytes) = Encode(w.Value);
        var rc = Win32.RegSetValueEx(_key, l.ValueName, 0, type, bytes, (uint)bytes.Length);
        if (rc != Win32.ErrorSuccess) throw Win32.Fail(rc, $"RegSetValueEx '{l.ValueName}' в {Where()}");
    }

    public IReadOnlyList<string> ValueNames()
    {
        var names = new List<string>();
        var buf = new char[16384]; // максимум имени значения 16383 символа
        for (uint i = 0; ; i++)
        {
            var len = (uint)buf.Length;
            var rc = Win32.RegEnumValue(_key, i, buf, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (rc == Win32.ErrorNoMoreItems) break;
            if (rc != Win32.ErrorSuccess) throw Win32.Fail(rc, $"RegEnumValue в {Where()}");
            names.Add(new string(buf, 0, (int)len));
        }
        return names;
    }

    public void Dispose() => _key.Dispose();

    private string Where() => WindowsRegistry.Describe(_location);

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
                    throw new InvalidOperationException($"NtQueryKey для {Where()} вернул NTSTATUS 0x{status:X8}.");
                capacity = Math.Max(needed, capacity * 2);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
        throw new InvalidOperationException($"NtQueryKey для {Where()}: не удалось подобрать размер буфера.");
    }

    private RegValueState Decode(string name, uint type, byte[] data)
    {
        switch (type)
        {
            case Win32.RegDword:
                if (data.Length != 4) throw Bad(name, "DWORD", data.Length);
                return new RegValueState(true, RegType.DWord, BitConverter.ToUInt32(data, 0));
            case Win32.RegQword:
                if (data.Length != 8) throw Bad(name, "QWORD", data.Length);
                return new RegValueState(true, RegType.QWord, BitConverter.ToUInt64(data, 0));
            case Win32.RegSz:
                return new RegValueState(true, RegType.String, DecodeString(data));
            case Win32.RegExpandSz:
                return new RegValueState(true, RegType.ExpandString, DecodeString(data));
            case Win32.RegMultiSz:
                return new RegValueState(true, RegType.MultiString, DecodeMulti(data));
            case Win32.RegBinary:
                return new RegValueState(true, RegType.Binary, data);
            case Win32.RegNone:
                return new RegValueState(true, RegType.None, data);
            default:
                // Прочие типы (REG_LINK, REG_RESOURCE_LIST и т. д.) отдаём как есть: это сырые байты, Core решит, что с ними делать.
                return new RegValueState(true, RegType.None, data);
        }
    }

    private InvalidDataException Bad(string name, string kind, int len) =>
        new($"Значение '{name}' в {Where()} объявлено как {kind}, но имеет длину {len} байт.");

    private static string DecodeString(byte[] data)
    {
        var s = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        var end = s.IndexOf('\0');
        return end < 0 ? s : s[..end];
    }

    private static string[] DecodeMulti(byte[] data)
    {
        var s = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        var parts = s.Split('\0');
        // Хвостовые пустые элементы — это завершители списка.
        var n = parts.Length;
        while (n > 0 && parts[n - 1].Length == 0) n--;
        return parts[..n];
    }

    private (uint Type, byte[] Bytes) Encode(RegValueState v)
    {
        switch (v.Type)
        {
            case RegType.DWord:
                return (Win32.RegDword, BitConverter.GetBytes(v.Data is uint u ? u : throw Mismatch(v, "uint")));
            case RegType.QWord:
                return (Win32.RegQword, BitConverter.GetBytes(v.Data is ulong q ? q : throw Mismatch(v, "ulong")));
            case RegType.String:
                return (Win32.RegSz, EncodeString(v.Data as string ?? throw Mismatch(v, "string")));
            case RegType.ExpandString:
                return (Win32.RegExpandSz, EncodeString(v.Data as string ?? throw Mismatch(v, "string")));
            case RegType.Binary:
                return (Win32.RegBinary, (byte[])(v.Data as byte[] ?? throw Mismatch(v, "byte[]")).Clone());
            case RegType.MultiString:
                var items = v.Data as string[] ?? throw Mismatch(v, "string[]");
                var sb = new StringBuilder();
                foreach (var item in items)
                {
                    if (item.Contains('\0')) throw new ArgumentException("Элемент MultiString содержит нулевой символ.");
                    sb.Append(item).Append('\0');
                }
                sb.Append('\0');
                if (items.Length == 0) sb.Append('\0');
                return (Win32.RegMultiSz, Encoding.Unicode.GetBytes(sb.ToString()));
            default:
                throw new InvalidOperationException($"Тип {v.Type} нельзя записать.");
        }
    }

    private static byte[] EncodeString(string s)
    {
        if (s.Contains('\0')) throw new ArgumentException("Строка содержит нулевой символ.");
        return Encoding.Unicode.GetBytes(s + "\0");
    }

    private static InvalidOperationException Mismatch(RegValueState v, string expected) =>
        new($"Для типа {v.Type} ожидались данные {expected}, получено {v.Data?.GetType().Name ?? "null"}.");
}
