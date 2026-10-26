using Chistovik.Core.Execution;
using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Подставной менеджер служб. Query живой: тест может менять службу между предпроверкой и записью (QueryHook).</summary>
public sealed class FakeServiceControl : IServiceControl
{
    readonly Dictionary<string, ServiceRaw> _services = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _denyWrite = new(StringComparer.OrdinalIgnoreCase);
    int _failAt;
    Exception? _failWith;
    bool _failApplies;

    public int WriteCount { get; private set; }
    public int OpenHandles { get; private set; }
    public List<(string Name, StartMode Start)> Writes { get; } = [];

    /// <summary>Записи молча ничего не меняют.</summary>
    public bool DropWrites { get; set; }

    /// <summary>Вызывается перед каждым Query: (имя службы, номер вызова для неё с 1).</summary>
    public Action<string, int>? QueryHook { get; set; }

    readonly Dictionary<string, int> _queries = new(StringComparer.OrdinalIgnoreCase);

    public void Add(ServiceRaw s) => _services[s.Name] = s;

    public ServiceRaw Get(string name) => _services[name];

    public bool Has(string name) => _services.ContainsKey(name);

    public void Mutate(string name, Func<ServiceRaw, ServiceRaw> f) => _services[name] = f(_services[name]);

    public void DenyWrite(string name) => _denyWrite.Add(name);

    public void FailOnWrite(int n, Exception? e = null, bool applyFirst = false)
    {
        _failAt = n;
        _failWith = e ?? new UnauthorizedAccessException("Подставной отказ записи");
        _failApplies = applyFirst;
    }

    public IReadOnlyList<string> ListNames() => _services.Keys.ToList();

    public IServiceHandle? Open(string name, bool write)
    {
        if (!_services.ContainsKey(name)) return null;
        OpenHandles++;
        return new Handle(this, _services[name].Name, write);
    }

    void Apply(string name, StartMode start)
    {
        var s = _services[name];
        _services[name] = start switch
        {
            StartMode.Automatic => s with { StartType = 2, Delayed = false },
            StartMode.AutomaticDelayed => s with { StartType = 2, Delayed = true },
            StartMode.Manual => s with { StartType = 3, Delayed = false },
            _ => s with { StartType = 4, Delayed = false },
        };
    }

    sealed class Handle(FakeServiceControl owner, string name, bool canWrite) : IServiceHandle
    {
        bool _disposed;

        public ServiceRaw Query()
        {
            var n = owner._queries[name] = owner._queries.GetValueOrDefault(name) + 1;
            owner.QueryHook?.Invoke(name, n);
            return owner._services[name];
        }

        public void SetStart(ApprovedServiceChange c)
        {
            if (!canWrite) throw new UnauthorizedAccessException("Служба открыта только на чтение");
            owner.WriteCount++;
            if (owner._denyWrite.Contains(name)) throw new UnauthorizedAccessException("Нет права изменить службу");
            if (owner._failAt == owner.WriteCount)
            {
                if (owner._failApplies && !owner.DropWrites) owner.Apply(name, c.Start);
                throw owner._failWith!;
            }
            if (owner.DropWrites) return;
            owner.Apply(name, c.Start);
            owner.Writes.Add((name, c.Start));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.OpenHandles--;
        }
    }
}

/// <summary>Подставной планировщик задач.</summary>
public sealed class FakeTaskScheduler : ITaskScheduler
{
    readonly Dictionary<string, TaskRaw> _tasks = new(StringComparer.OrdinalIgnoreCase);
    int _failAt;
    Exception? _failWith;
    bool _failApplies;

    public int WriteCount { get; private set; }
    public int OpenHandles { get; private set; }
    public List<(string Path, bool Enabled)> Writes { get; } = [];
    public bool DropWrites { get; set; }
    public Action<string, int>? QueryHook { get; set; }

    readonly Dictionary<string, int> _queries = new(StringComparer.OrdinalIgnoreCase);

    public void Add(TaskRaw t) => _tasks[t.Path] = t;

    public void Remove(string path) => _tasks.Remove(path);

    public TaskRaw Get(string path) => _tasks[path];

    public void Mutate(string path, Func<TaskRaw, TaskRaw> f) => _tasks[path] = f(_tasks[path]);

    public void FailOnWrite(int n, Exception? e = null, bool applyFirst = false)
    {
        _failAt = n;
        _failWith = e ?? new UnauthorizedAccessException("Подставной отказ записи");
        _failApplies = applyFirst;
    }

    public IReadOnlyList<string> ListPaths() => _tasks.Keys.ToList();

    public ITaskHandle? Open(string path)
    {
        if (!_tasks.TryGetValue(path, out var t)) return null;
        OpenHandles++;
        return new Handle(this, t.Path);
    }

    sealed class Handle(FakeTaskScheduler owner, string path) : ITaskHandle
    {
        bool _disposed;

        public TaskRaw Query()
        {
            var n = owner._queries[path] = owner._queries.GetValueOrDefault(path) + 1;
            owner.QueryHook?.Invoke(path, n);
            return owner._tasks[path];
        }

        public void SetEnabled(ApprovedTaskChange c)
        {
            owner.WriteCount++;
            if (owner._failAt == owner.WriteCount)
            {
                if (owner._failApplies && !owner.DropWrites) owner._tasks[path] = owner._tasks[path] with { Enabled = c.Enabled };
                throw owner._failWith!;
            }
            if (owner.DropWrites) return;
            owner._tasks[path] = owner._tasks[path] with { Enabled = c.Enabled };
            owner.Writes.Add((path, c.Enabled));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.OpenHandles--;
        }
    }
}

/// <summary>Подставные схемы питания: список, активная, значения AC/DC, копирование шаблона и удаление.</summary>
public sealed class FakePowerControl : IPowerControl
{
    readonly List<Guid> _schemes = [];
    readonly Dictionary<(Guid, Guid, Guid), (uint Ac, uint Dc)> _values = [];
    int _failAt;
    Exception? _failWith;
    bool _failApplies;

    public Guid Active { get; set; }
    public Dictionary<Guid, string> Names { get; } = [];

    /// <summary>Параметры (подгруппа, параметр), которых «нет в схеме»: Read бросает PowerSettingNotFoundException, как Native при ERROR_FILE_NOT_FOUND.</summary>
    public HashSet<(Guid Subgroup, Guid Setting)> MissingSettings { get; } = [];

    /// <summary>Счётчик любых изменяющих вызовов (SetActive, Write, Duplicate, Delete).</summary>
    public int WriteCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int DeleteCount { get; private set; }
    public bool DropWrites { get; set; }

    public void AddScheme(Guid g, string? name = null)
    {
        if (!_schemes.Contains(g)) _schemes.Add(g);
        if (name is not null) Names[g] = name;
    }

    public void RemoveScheme(Guid g)
    {
        _schemes.Remove(g);
        Names.Remove(g);
    }

    public void SetValue(Guid scheme, Guid sub, Guid setting, uint ac, uint dc) => _values[(scheme, sub, setting)] = (ac, dc);

    public (uint Ac, uint Dc) Value(Guid scheme, Guid sub, Guid setting) => _values.GetValueOrDefault((scheme, sub, setting));

    public void FailOnWrite(int n, Exception? e = null, bool applyFirst = false)
    {
        _failAt = n;
        _failWith = e ?? new UnauthorizedAccessException("Подставной отказ записи");
        _failApplies = applyFirst;
    }

    public Guid GetActive() => Active;

    /// <summary>Устройство с Modern Standby (S0): IsModernStandby возвращает true.</summary>
    public bool ModernStandby { get; set; }

    public bool IsModernStandby() => ModernStandby;

    /// <summary>Если задано, GetName бросает это исключение (проверка непредвиденных отказов порта).</summary>
    public Exception? GetNameFailure { get; set; }

    public string? GetName(Guid scheme) => GetNameFailure is { } f ? throw f : Names.GetValueOrDefault(scheme);

    /// <summary>Если задано, ListSchemes бросает это исключение (чтение схем упало).</summary>
    public Exception? ListFailure { get; set; }

    public IReadOnlyList<Guid> ListSchemes() => ListFailure is { } f ? throw f : _schemes.ToList();

    public (uint Ac, uint Dc) Read(Guid scheme, Guid sub, Guid setting)
    {
        if (!_schemes.Contains(scheme)) throw new InvalidOperationException("Схемы нет");
        if (MissingSettings.Contains((sub, setting))) throw new PowerSettingNotFoundException("Параметра нет в схеме");
        return _values.GetValueOrDefault((scheme, sub, setting));
    }

    void Mutating(Action apply)
    {
        WriteCount++;
        if (_failAt == WriteCount)
        {
            if (_failApplies && !DropWrites) apply();
            throw _failWith!;
        }
        if (DropWrites) return;
        apply();
    }

    public void Write(ApprovedPowerSetting c) => Mutating(() => _values[(c.Scheme, c.Subgroup, c.Setting)] = (c.Ac, c.Dc));

    public void SetActive(ApprovedPowerScheme c) => Mutating(() => Active = c.Scheme);

    public void Duplicate(ApprovedSchemeCreate c) => Mutating(() =>
    {
        if (_schemes.Contains(c.NewScheme)) throw new InvalidOperationException("Схема с таким GUID уже есть");
        DuplicateCount++;
        _schemes.Add(c.NewScheme);
        Names[c.NewScheme] = c.Name;
        foreach (var kv in _values.Where(k => k.Key.Item1 == c.Template).ToList())
            _values[(c.NewScheme, kv.Key.Item2, kv.Key.Item3)] = kv.Value;
    });

    public void Delete(ApprovedSchemeDelete c) => Mutating(() =>
    {
        if (c.Scheme == Active) throw new InvalidOperationException("Нельзя удалить активную схему");
        DeleteCount++;
        _schemes.Remove(c.Scheme);
        Names.Remove(c.Scheme);
    });
}

/// <summary>Подписи файлов. Для неизвестного пути отдаёт Default (по умолчанию: неподписан).</summary>
public sealed class FakeFileTrust : IFileTrust
{
    readonly Dictionary<string, SignatureFacts> _facts = new(StringComparer.OrdinalIgnoreCase);

    public SignatureFacts Default { get; set; } = new(false, false);

    public void Set(string path, SignatureFacts facts) => _facts[path] = facts;

    public void SetSigned(string path, bool microsoft = false) => _facts[path] = new SignatureFacts(true, microsoft);

    public SignatureFacts GetSignature(string path) => _facts.GetValueOrDefault(path, Default);
}

public sealed class FakeUserFolders : IUserFolders
{
    readonly Dictionary<string, string> _user = new(StringComparer.OrdinalIgnoreCase);

    public string? Common { get; set; }

    public void SetUser(string sid, string folder) => _user[sid] = folder;

    public string? StartupFolder(string userSid) => _user.GetValueOrDefault(userSid);

    public string? CommonStartupFolder() => Common;

    readonly Dictionary<string, string> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public void SetProfile(string sid, string dir) => _profiles[sid] = dir;

    public string? ProfileDir(string userSid) => _profiles.GetValueOrDefault(userSid);
}

public sealed class FakeFileSystem : IFileSystem
{
    readonly List<string> _files = [];

    public void Add(string fullPath) => _files.Add(fullPath);

    public IReadOnlyList<string> ListFiles(string dir, string pattern)
    {
        var d = dir.TrimEnd('\\');
        return _files.Where(f =>
        {
            var cut = f.LastIndexOf('\\');
            return cut > 0 && f[..cut].Equals(d, StringComparison.OrdinalIgnoreCase)
                   && (pattern == "*" || f[(cut + 1)..].EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan d) => UtcNow += d;
}

/// <summary>Предсказуемые GUID: 00000001-0000-…, 00000002-0000-… (первый выданный = 1).</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    int _n;

    public static Guid Make(int n) => new(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    public int Issued => _n;

    public Guid NewGuid() => Make(++_n);
}
