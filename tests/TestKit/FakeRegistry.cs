using Chistovik.Core.Execution;
using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Запись, выполненная через хэндл: путь открытого (разрешённого) ключа, имя и новое значение.</summary>
public sealed record RegWrite(string ResolvedKey, string Name, RegValueState Value);

/// <summary>
/// Подставной реестр: деревья ключей «HKLM\…» и «HKU\&lt;sid&gt;\…», символические ссылки (с переходом по ссылке или с открытием
/// самой ссылки), отказ в доступе, отказ на N-й записи и счётчик записей. ResolvedPath отдаётся в виде NT-пути
/// (\REGISTRY\MACHINE, \REGISTRY\USER), как NtQueryKey.
/// </summary>
public sealed class FakeRegistry : IRegistry
{
    readonly Dictionary<string, Dictionary<string, RegValueState>> _keys = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string Target, bool OpenAsLink)> _links = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _denyOpen = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _throwDeniedOpen = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _denyWrite = new(StringComparer.OrdinalIgnoreCase);
    int _failAt;
    Exception? _failWith;
    bool _failApplies;

    public int WriteCount { get; private set; }
    public int OpenCount { get; private set; }

    /// <summary>Сколько хэндлов открыто и не закрыто (утечка хэндла это ошибка).</summary>
    public int OpenHandles { get; private set; }
    public List<RegWrite> Writes { get; } = [];

    /// <summary>Записи молча ничего не меняют (проверка «после записи значение прежнее»).</summary>
    public bool DropWrites { get; set; }

    /// <summary>Вызывается при каждом открытии ключа (после разбора ссылок): повод подменить состояние между предпроверкой и записью.</summary>
    public Action<string>? OnOpen { get; set; }

    public static string Path(RegLocation l) => StepGate.ExpectedPath(l);

    public static string HkuPath(string sid, string key) => "HKU\\" + sid + "\\" + key;

    public void EnsureKey(string path)
    {
        if (!_keys.ContainsKey(path)) _keys[path] = new Dictionary<string, RegValueState>(StringComparer.OrdinalIgnoreCase);
    }

    public bool KeyExists(string path) => _keys.ContainsKey(path);

    public void Set(string keyPath, string name, RegValueState value)
    {
        EnsureKey(keyPath);
        _keys[keyPath][name] = value;
    }

    public void SetDword(string keyPath, string name, uint v) => Set(keyPath, name, new RegValueState(true, RegType.DWord, v));

    public void SetString(string keyPath, string name, string v) => Set(keyPath, name, new RegValueState(true, RegType.String, v));

    public void SetBinary(string keyPath, string name, byte[] v) => Set(keyPath, name, new RegValueState(true, RegType.Binary, v));

    public RegValueState Get(string keyPath, string name)
        => _keys.TryGetValue(keyPath, out var k) && k.TryGetValue(name, out var v) ? v : new RegValueState(false, RegType.None, null);

    public RegValueState Get(RegLocation l) => Get(Path(l), l.ValueName);

    public void Remove(string keyPath, string name)
    {
        if (_keys.TryGetValue(keyPath, out var k)) k.Remove(name);
    }

    /// <summary>Ключ linkPath это ссылка на targetPath. openAsLink=false: открытие идёт по ссылке (ResolvedPath = цель); true: открывается сама ссылка (IsSymbolicLink).</summary>
    public void AddSymlink(string linkPath, string targetPath, bool openAsLink = false)
    {
        _links[linkPath] = (targetPath, openAsLink);
        EnsureKey(linkPath);
    }

    public void DenyOpen(string path) => _denyOpen.Add(path);

    /// <summary>Открытие ключа бросает UnauthorizedAccessException, как настоящий порт при ERROR_ACCESS_DENIED.</summary>
    public void ThrowAccessDeniedOnOpen(string path) => _throwDeniedOpen.Add(path);

    public void DenyWrite(string path) => _denyWrite.Add(path);

    /// <summary>N-я запись (считая с 1, с начала жизни объекта) бросает исключение. applyFirst=true: значение успевает измениться (сбой после записи).</summary>
    public void FailOnWrite(int n, Exception? e = null, bool applyFirst = false)
    {
        _failAt = n;
        _failWith = e ?? new UnauthorizedAccessException("Подставной отказ записи");
        _failApplies = applyFirst;
    }

    string Resolve(string path, out bool isLink)
    {
        isLink = false;
        for (var round = 0; round < 8; round++)
        {
            var segs = path.Split('\\');
            var replaced = false;
            for (var i = 1; i <= segs.Length; i++)
            {
                var prefix = string.Join('\\', segs.Take(i));
                if (!_links.TryGetValue(prefix, out var link)) continue;
                if (i == segs.Length && link.OpenAsLink)
                {
                    isLink = true;
                    return path;
                }
                path = link.Target + (i < segs.Length ? "\\" + string.Join('\\', segs.Skip(i)) : string.Empty);
                replaced = true;
                break;
            }
            if (!replaced) return path;
        }
        throw new InvalidOperationException("Цикл символических ссылок");
    }

    public static string ToNt(string path)
    {
        if (path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)) return "\\REGISTRY\\MACHINE\\" + path[5..];
        if (path.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase)) return "\\REGISTRY\\USER\\" + path[4..];
        return path;
    }

    public IRegKeyHandle? Open(RegLocation loc, bool write, bool createKey)
    {
        OpenCount++;
        var path = Resolve(Path(loc), out var isLink);
        OnOpen?.Invoke(path);
        if (_throwDeniedOpen.Contains(path)) throw new UnauthorizedAccessException("Отказано в доступе к ключу реестра");
        if (_denyOpen.Contains(path)) return null;
        if (!_keys.ContainsKey(path))
        {
            if (!createKey) return null;
            EnsureKey(path);
        }
        OpenHandles++;
        return new Handle(this, path, isLink, write);
    }

    void CountWrite(string key, string name, RegValueState value, Action apply)
    {
        WriteCount++;
        if (_denyWrite.Contains(key)) throw new UnauthorizedAccessException("Нет права записи в ключ");
        if (_failAt == WriteCount)
        {
            if (_failApplies && !DropWrites) apply();
            throw _failWith!;
        }
        if (DropWrites) return;
        apply();
        Writes.Add(new RegWrite(key, name, value));
    }

    sealed class Handle(FakeRegistry owner, string path, bool isLink, bool canWrite) : IRegKeyHandle
    {
        public string ResolvedPath => ToNt(path);
        public bool IsSymbolicLink => isLink;

        public RegValueState Read(string name) => owner.Get(path, name);

        public void Write(ApprovedRegistryWrite w)
        {
            if (!canWrite) throw new UnauthorizedAccessException("Ключ открыт только на чтение");
            var name = w.Location.ValueName;
            owner.CountWrite(path, name, w.Value, () =>
            {
                if (w.Value.Exists) owner._keys[path][name] = w.Value;
                else owner._keys[path].Remove(name);
            });
        }

        public IReadOnlyList<string> ValueNames() => owner._keys[path].Keys.ToList();

        bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.OpenHandles--;
        }
    }
}
