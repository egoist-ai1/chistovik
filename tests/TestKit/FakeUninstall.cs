using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>
/// Подставные факты о файлах: по умолчанию путь «не существует». Trust* добавляют файл или каталог с владельцем TrustedInstaller
/// и записью только у доверенных субъектов (как в Program Files), Add — любые факты для негативных тестов.
/// </summary>
public sealed class FakeFileSecurity : IFileSecurity
{
    public const string Administrators = "S-1-5-32-544";
    public const string SystemSid = "S-1-5-18";
    public const string Users = "S-1-5-32-545";

    readonly Dictionary<string, FileFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _nsis = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Inspected { get; } = [];

    public void Add(string path, FileFacts facts) => _facts[path] = facts;

    /// <summary>Файл или каталог в Program Files: владелец Administrators, запись только у Administrators и SYSTEM.</summary>
    public FakeFileSecurity Trust(string path, string? finalPath = null)
    {
        _facts[path] = new FileFacts(true, false, finalPath ?? path, Administrators,
            [new DirAce(Administrators, true, true), new DirAce(SystemSid, true, true), new DirAce(Users, true, false)]);
        return this;
    }

    /// <summary>Файл и его каталог под доверенными правами (каталог берётся из пути).</summary>
    public FakeFileSecurity TrustFileAndDir(string exe)
    {
        Trust(exe);
        Trust(exe[..exe.LastIndexOf('\\')]);
        return this;
    }

    /// <summary>Обычные пользователи могут писать в путь (ReadOnly exe-writable-dir).</summary>
    public FakeFileSecurity UsersWritable(string path)
    {
        var f = _facts[path];
        _facts[path] = f with { Aces = [.. f.Aces, new DirAce(Users, true, true)] };
        return this;
    }

    public FakeFileSecurity Nsis(string path)
    {
        _nsis.Add(path);
        return this;
    }

    public FileFacts Inspect(string path)
    {
        Inspected.Add(path);
        return _facts.TryGetValue(path, out var f) ? f : new FileFacts(false, false, null, null, []);
    }

    public bool HasNsisMarker(string finalPath) => _nsis.Contains(finalPath);
}

/// <summary>
/// Подставной деинсталлятор. Состояние MSI хранится по коду продукта; запуск пишется в Calls. Effect моделирует действие
/// установщика (например, удалить ключ ARP) и вызывается при коде 0/3010 (или всегда, если EffectOnFailure).
/// CrashAfterRun бросает SimulatedCrash сразу после действия, до ответа агенту (обрыв процесса между записью и Complete).
/// </summary>
public sealed class FakeUninstaller : IUninstaller
{
    public Dictionary<string, int> MsiStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public UninstallOutcome Outcome { get; set; } = new(0, false);
    public Action<string>? Effect { get; set; }
    public bool EffectOnFailure { get; set; }
    public bool CrashAfterRun { get; set; }
    public TimeSpan? LastTimeout { get; private set; }

    public int QueryMsiState(string productCode) => MsiStates.TryGetValue(productCode, out var s) ? s : -1;

    public UninstallOutcome RunMsi(string productCode, TimeSpan timeout)
    {
        Calls.Add("msi:" + productCode);
        LastTimeout = timeout;
        return Finish(productCode, () => MsiStates[productCode] = 2);
    }

    public UninstallOutcome RunExe(string exePath, string arguments, TimeSpan timeout)
    {
        Calls.Add("exe:" + exePath + " " + arguments);
        LastTimeout = timeout;
        return Finish(exePath, () => { });
    }

    UninstallOutcome Finish(string what, Action markGone)
    {
        var ok = !Outcome.TimedOut && Outcome.ExitCode is 0 or 3010;
        if (ok || EffectOnFailure)
        {
            markGone();
            Effect?.Invoke(what);
        }
        if (CrashAfterRun) throw new SimulatedCrash("uninstall-after-run");
        return Outcome;
    }
}
