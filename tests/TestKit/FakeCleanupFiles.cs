using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>
/// Подставная файловая система очистки: дерево узлов в памяти (диск C:), ссылки, занятые файлы, отказ доступа и подмена
/// финального пути у открытого хэндла. Удаление только через хэндл; непустой каталог удалить нельзя (как в Windows).
/// Живую систему не трогает.
/// </summary>
public sealed class FakeCleanupFiles : ICleanupFiles
{
    /// <summary>Том, в который «превращается» C: во всех финальных путях.</summary>
    public const string Volume = @"\\?\Volume{11111111-2222-3333-4444-555555555555}";

    /// <summary>Совпадает с FakeClock: возраст файла считается от этого момента.</summary>
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    sealed class Node
    {
        public required string Path { get; init; }
        public bool IsDir { get; init; }
        public bool IsReparse { get; set; }
        public long Length { get; set; }
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset LastWrite { get; set; }
        public string Owner { get; set; } = TestEnv.Sid;
        public bool Locked { get; set; }
        public bool AccessDenied { get; set; }
        public string? FinalOverride { get; set; }
    }

    readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пути, удалённые через хэндл (в порядке удаления).</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Каталоги, которые перечислялись.</summary>
    public List<string> Enumerated { get; } = [];

    /// <summary>Пути, открытые через OpenForDelete.</summary>
    public List<string> Opened { get; } = [];

    /// <summary>Вызывается в начале каждого Enumerate (сдвинуть часы, подменить корень посреди обхода).</summary>
    public Action<string>? OnEnumerate { get; set; }

    /// <summary>Вызывается при каждом Inspect.</summary>
    public Action<string>? OnInspect { get; set; }

    public static string Final(string path) => Volume + path[2..];

    static string Trim(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    static string Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i <= 2 ? path[..3] : path[..i];
    }

    static string NameOf(string path) => path[(path.LastIndexOf('\\') + 1)..];

    public bool Exists(string path) => _nodes.ContainsKey(Trim(path));

    public IEnumerable<string> AllPaths => _nodes.Values.Select(n => n.Path).ToList();

    // ---------- построение дерева ----------

    void EnsureParents(string path)
    {
        var p = Parent(path);
        if (p.Length <= 3 || _nodes.ContainsKey(p)) return;
        EnsureParents(p);
        _nodes[p] = new Node { Path = p, IsDir = true, Created = BaseTime.AddHours(-5000), LastWrite = BaseTime.AddHours(-5000) };
    }

    public void AddDir(string path, double ageHours = 5000)
    {
        path = Trim(path);
        EnsureParents(path);
        _nodes[path] = new Node { Path = path, IsDir = true, Created = BaseTime.AddHours(-ageHours), LastWrite = BaseTime.AddHours(-ageHours) };
    }

    public void AddFile(string path, double ageHours = 5000, long length = 1000)
    {
        path = Trim(path);
        EnsureParents(path);
        _nodes[path] = new Node { Path = path, Length = length, Created = BaseTime.AddHours(-ageHours), LastWrite = BaseTime.AddHours(-ageHours) };
    }

    /// <summary>Каталог-ссылка (junction). Дети, добавленные внутрь, видны только если в них зайти, чего агент делать не должен.</summary>
    public void AddReparseDir(string path, double ageHours = 5000)
    {
        AddDir(path, ageHours);
        Get(path).IsReparse = true;
    }

    public void AddReparseFile(string path, double ageHours = 5000)
    {
        AddFile(path, ageHours);
        Get(path).IsReparse = true;
    }

    public void Lock(string path) => Get(path).Locked = true;

    public void Deny(string path) => Get(path).AccessDenied = true;

    public void SetOwner(string path, string sid) => Get(path).Owner = sid;

    /// <summary>Хэндл этого пути будет сообщать указанный финальный путь (подмена ссылкой между перечислением и открытием).</summary>
    public void SetFinal(string path, string final) => Get(path).FinalOverride = final;

    public void MakeReparse(string path) => Get(path).IsReparse = true;

    public void Remove(string path)
    {
        path = Trim(path);
        foreach (var k in _nodes.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)
                                                 || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            _nodes.Remove(k);
    }

    Node Get(string path) => _nodes.TryGetValue(Trim(path), out var n) ? n : throw new ArgumentException("Нет узла " + path);

    // ---------- порт ----------

    public FileFacts Inspect(string path)
    {
        OnInspect?.Invoke(path);
        path = Trim(path);
        if (!_nodes.TryGetValue(path, out var n)) return new FileFacts(false, false, null, null, []);
        return new FileFacts(true, n.IsReparse, n.FinalOverride ?? Final(n.Path), n.Owner, [], RequestedVolumeOverride ?? Volume);
    }

    /// <summary>Том, который порт сообщает для запрошенной буквы диска (по умолчанию тот же, что в финальных путях).</summary>
    public string? RequestedVolumeOverride { get; set; }

    /// <summary>Сколько записей перечисление уже отдало потребителю (ленивое чтение, как у настоящего порта).</summary>
    public int Pulled { get; private set; }

    /// <summary>Вызывается в OpenForDelete после учёта пути в Opened.</summary>
    public Action<string>? OnOpenForDelete { get; set; }

    IEnumerable<DirEntryFacts> Lazy(List<DirEntryFacts> snapshot)
    {
        foreach (var e in snapshot)
        {
            Pulled++;
            yield return e;
        }
    }

    public IEnumerable<DirEntryFacts> Enumerate(string directory)
    {
        directory = Trim(directory);
        Enumerated.Add(directory);
        OnEnumerate?.Invoke(directory);
        if (!_nodes.TryGetValue(directory, out var d) || !d.IsDir) throw new DirectoryNotFoundException(directory);
        if (d.AccessDenied) throw new UnauthorizedAccessException(directory);
        return Lazy(_nodes.Values
            .Where(n => !ReferenceEquals(n, d) && Parent(n.Path).Equals(directory, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .Select(n => new DirEntryFacts(NameOf(n.Path), n.IsDir, n.IsReparse, n.Length, n.Created, n.LastWrite))
            .ToList());
    }

    public IDeletableHandle? OpenForDelete(string path, bool isDirectory)
    {
        path = Trim(path);
        Opened.Add(path);
        OnOpenForDelete?.Invoke(path);
        if (!_nodes.TryGetValue(path, out var n)) return null;
        if (n.Locked) throw new SharingViolationException("sharing violation: " + path);
        if (n.AccessDenied) throw new UnauthorizedAccessException(path);
        return new Handle(this, n);
    }

    sealed class Handle(FakeCleanupFiles fs, Node node) : IDeletableHandle
    {
        public string FinalPath => node.FinalOverride ?? Final(node.Path);
        public bool IsReparse => node.IsReparse;

        public void Delete()
        {
            if (node.IsDir && fs._nodes.Values.Any(c => !ReferenceEquals(c, node) && Parent(c.Path).Equals(node.Path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Каталог не пуст", unchecked((int)0x80070091));
            fs._nodes.Remove(node.Path);
            fs.Deleted.Add(node.Path);
        }

        public void Dispose()
        {
        }
    }
}
