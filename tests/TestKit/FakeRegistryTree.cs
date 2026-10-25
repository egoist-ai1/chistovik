using System.Text;
using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>
/// Подставное дерево реестра поверх FakeRegistry: те же ключи «HKLM\…» и «HKU\&lt;sid&gt;\…», промежуточные узлы выводятся из путей.
/// Ссылки раскрываются на промежуточных уровнях, а конечная ссылка открывается как есть (IsSymbolicLink), как REG_OPTION_OPEN_LINK.
/// Значения хранятся в FakeRegistry; сырые байты нестандартной длины запоминаются отдельно, чтобы round-trip был точным.
/// </summary>
public sealed class FakeRegistryTree : IRegistryTree
{
    readonly FakeRegistry _reg;
    readonly Dictionary<string, (RegType Type, byte[] Raw)> _raw = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _failDelete = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistryTree() : this(new FakeRegistry())
    {
    }

    public FakeRegistryTree(FakeRegistry reg) => _reg = reg;

    public int OpenHandles { get; private set; }
    public int Created { get; private set; }
    public int Deleted { get; private set; }
    public int SetRawCount { get; private set; }

    /// <summary>SetRaw молча ничего не пишет внутри этого ключа (копия получается неполной).</summary>
    public string? DropSetRawUnder { get; set; }

    /// <summary>Сколько байт значений реально отдано через RawValues (оверсайз-значение, отвергнутое по размеру, сюда не попадает).</summary>
    public long RawBytesMaterialized { get; private set; }

    /// <summary>Вызывается в CreateSub перед созданием/открытием подключа (полный путь без раскрытия ссылки): окно гонки для подкладывания ссылки.</summary>
    public Action<string>? OnCreate { get; set; }

    static long SizeOf(RegValueState s) => s.Type == RegType.Binary ? ((byte[]?)s.Data)?.Length ?? 0 : Encode(s).Length;

    /// <summary>Вызывается при каждом открытии ключа дерева с разрешённым путём.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>DeleteSelf этого ключа бросает исключение (удаление прервалось).</summary>
    public void FailDeleteAt(string path) => _failDelete.Add(path);

    readonly HashSet<string> _denyDelete = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Открытие этого ключа на запись бросает UnauthorizedAccessException (нет права DELETE на подключ).</summary>
    public void DenyDeleteAt(string path) => _denyDelete.Add(path);

    // ---------- коды значений ----------

    public static byte[] Encode(RegValueState v)
    {
        switch (v.Type)
        {
            case RegType.String or RegType.ExpandString:
                return Encoding.Unicode.GetBytes(((string?)v.Data ?? string.Empty) + "\0");
            case RegType.DWord:
                return BitConverter.GetBytes(v.Data is uint u ? u : 0u);
            case RegType.QWord:
                return BitConverter.GetBytes(v.Data is ulong q ? q : 0ul);
            case RegType.Binary:
                return (byte[])((byte[]?)v.Data ?? []).Clone();
            case RegType.MultiString:
            {
                var sb = new StringBuilder();
                foreach (var s in (string[]?)v.Data ?? []) sb.Append(s).Append('\0');
                sb.Append('\0');
                return Encoding.Unicode.GetBytes(sb.ToString());
            }
            default:
                return [];
        }
    }

    public static RegValueState Decode(RegType type, byte[] raw)
    {
        switch (type)
        {
            case RegType.String or RegType.ExpandString:
                return new RegValueState(true, type, Encoding.Unicode.GetString(raw, 0, raw.Length & ~1).TrimEnd('\0'));
            case RegType.DWord when raw.Length == 4:
                return new RegValueState(true, type, BitConverter.ToUInt32(raw, 0));
            case RegType.QWord when raw.Length == 8:
                return new RegValueState(true, type, BitConverter.ToUInt64(raw, 0));
            case RegType.MultiString:
            {
                var parts = Encoding.Unicode.GetString(raw, 0, raw.Length & ~1).Split('\0').ToList();
                while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
                return new RegValueState(true, type, parts.ToArray());
            }
            case RegType.None:
                return new RegValueState(true, type, null);
            default:
                return new RegValueState(true, RegType.Binary, (byte[])raw.Clone());
        }
    }

    // ---------- узлы ----------

    bool Exists(string path)
    {
        if (_reg.KeyExists(path)) return true;
        var prefix = path + "\\";
        return _reg.KeyPaths.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    List<string> Children(string path)
    {
        var prefix = path + "\\";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _reg.KeyPaths)
        {
            if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = k[prefix.Length..];
            var cut = rest.IndexOf('\\');
            var name = cut < 0 ? rest : rest[..cut];
            names.TryAdd(name, name);
        }
        return names.Keys.ToList();
    }

    string ResolveOpenLink(string path, out bool isLink)
    {
        isLink = false;
        for (var round = 0; round < 8; round++)
        {
            var segs = path.Split('\\');
            var replaced = false;
            for (var i = 1; i <= segs.Length; i++)
            {
                var prefix = string.Join('\\', segs.Take(i));
                if (!_reg.TryGetLink(prefix, out var target)) continue;
                if (i == segs.Length)
                {
                    isLink = true;
                    return path;
                }
                path = target + "\\" + string.Join('\\', segs.Skip(i));
                replaced = true;
                break;
            }
            if (!replaced) return path;
        }
        throw new InvalidOperationException("Цикл символических ссылок");
    }

    public IRegTreeHandle? OpenTree(RegLocation loc, bool forWrite)
    {
        var path = ResolveOpenLink(FakeRegistry.Path(loc), out var isLink);
        OnOpen?.Invoke(path);
        if (!Exists(path)) return null;
        if (forWrite && _denyDelete.Contains(path)) throw new UnauthorizedAccessException("Нет права DELETE (подставной отказ)");
        OpenHandles++;
        return new Handle(this, path, isLink, forWrite);
    }

    sealed class Handle(FakeRegistryTree t, string path, bool isLink, bool canWrite) : IRegTreeHandle
    {
        bool _disposed;

        public string ResolvedPath => FakeRegistry.ToNt(path);
        public bool IsSymbolicLink => isLink;

        public RegKeyInfo Info() => new(t.Children(path).Count, t._reg.ValuesOf(path).Count);

        public IReadOnlyList<string> SubKeyNames() => t.Children(path);

        public IRegTreeHandle? OpenSub(string name, bool forWrite)
        {
            if (forWrite && !canWrite) throw new UnauthorizedAccessException("Ключ открыт только на чтение");
            var real = t.Children(path).FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (real is null) return null;
            var child = path + "\\" + real;
            var link = t._reg.TryGetLink(child, out _);
            if (!t.Exists(child)) return null;
            if (forWrite && t._denyDelete.Contains(child)) throw new UnauthorizedAccessException("Нет права DELETE (подставной отказ)");
            t.OpenHandles++;
            return new Handle(t, child, link, forWrite);
        }

        public IReadOnlyList<(string Name, RegType Type, byte[] Raw)> RawValues() => RawValues(long.MaxValue);

        public IReadOnlyList<(string Name, RegType Type, byte[] Raw)> RawValues(long maxTotalBytes)
        {
            var list = new List<(string, RegType, byte[])>();
            var budget = maxTotalBytes;
            foreach (var (name, state) in t._reg.ValuesOf(path))
            {
                // Как порт: размер известен до чтения данных, оверсайз-значение не материализуется.
                var size = t._raw.TryGetValue(path + "|" + name, out var o) ? o.Raw.Length : SizeOf(state);
                if (size > budget) throw new RegValueTooLargeException();
                budget -= size;
                t.RawBytesMaterialized += size;
                if (o.Raw is not null) list.Add((name, o.Type, (byte[])o.Raw.Clone()));
                else list.Add((name, state.Type, Encode(state)));
            }
            return list;
        }

        // Хэндл-ссылка (OPEN_LINK) в Windows: относительный путь от него разрешается по цели ссылки. Фейк моделирует это буквально:
        // создание подключа и запись значений через такой хэндл попадают в целевое дерево. Только проверка IsSymbolicLink в Core это останавливает.
        string Base => isLink && t._reg.TryGetLink(path, out var target) ? target : path;

        public IRegTreeHandle CreateSub(string name, out bool created)
        {
            if (!canWrite) throw new UnauthorizedAccessException("Ключ открыт только на чтение");
            var basePath = Base;
            var real = t.Children(basePath).FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            var child = basePath + "\\" + real;
            t.OnCreate?.Invoke(child);
            // created=false (REG_OPENED_EXISTING_KEY): подключ уже был, в том числе как промежуточный узел чужих путей.
            created = !t.Exists(child);
            if (created) t.Created++;
            if (!t._reg.KeyExists(child)) t._reg.EnsureKey(child);
            t.OpenHandles++;
            return new Handle(t, child, t._reg.TryGetLink(child, out _), true);
        }

        public void SetRaw(string name, RegType type, byte[] raw)
        {
            if (!canWrite) throw new UnauthorizedAccessException("Ключ открыт только на чтение");
            var path = Base;
            if (t.DropSetRawUnder is { } d && path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return;
            t.SetRawCount++;
            var state = Decode(type, raw);
            t._reg.Set(path, name, state);
            var key = path + "|" + name;
            if (state.Type == type && Encode(state).AsSpan().SequenceEqual(raw)) t._raw.Remove(key);
            else t._raw[key] = (type, (byte[])raw.Clone());
        }

        public void DeleteSelf()
        {
            if (!canWrite) throw new UnauthorizedAccessException("Нет права DELETE");
            if (t._failDelete.Contains(path)) throw new IOException("Подставной сбой удаления");
            if (t.Children(path).Count > 0) throw new IOException("Ключ с подключами не удаляется");
            t._reg.RemoveKey(path);

            // Удаление ключа не удаляет родителя: узел, который существовал только как промежуточный, становится явным.
            var cut = path.LastIndexOf('\\');
            if (cut > 0 && !t._reg.KeyExists(path[..cut])) t._reg.EnsureKey(path[..cut]);
            foreach (var k in t._raw.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList()) t._raw.Remove(k);
            t.Deleted++;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            t.OpenHandles--;
        }
    }
}

/// <summary>Подставное хранилище .reg-копий (C3).</summary>
public sealed class FakeQuarantineStore : IQuarantineStore
{
    readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool FailWrites { get; set; }
    public int WriteCount { get; private set; }

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public void WriteFile(string relativePath, byte[] content)
    {
        WriteCount++;
        if (FailWrites) throw new IOException("Подставной сбой записи копии");
        _files[relativePath] = (byte[])content.Clone();
    }

    public bool Exists(string relativePath) => _files.ContainsKey(relativePath);

    public void Delete(string relativePath) => _files.Remove(relativePath);

    /// <summary>DeleteOperation бросает исключение (каталог занят).</summary>
    public bool FailDeletes { get; set; }

    public int DeleteOperationCount { get; private set; }

    public IReadOnlyList<Guid> ListOperations()
    {
        var ids = new HashSet<Guid>();
        foreach (var f in _files.Keys)
        {
            var cut = f.IndexOf('\\');
            if (cut == 32 && Guid.TryParseExact(f[..cut], "N", out var id)) ids.Add(id);
        }
        return ids.ToList();
    }

    public void DeleteOperation(Guid operationId)
    {
        DeleteOperationCount++;
        if (FailDeletes) throw new IOException("Подставной сбой удаления каталога копий");
        var prefix = operationId.ToString("N") + "\\";
        foreach (var k in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) _files.Remove(k);
    }
}
