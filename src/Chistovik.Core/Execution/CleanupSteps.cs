using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Сериализация payload'ов очистки (журнал и ответ).</summary>
internal static class CleanupJson
{
    public static string Target(CleanupTarget t) => JsonSerializer.Serialize(t, JournalJson.Default.CleanupTarget);

    public static string State(CleanupState s) => JsonSerializer.Serialize(s, JournalJson.Default.CleanupState);

    public static string Desired() => State(new CleanupState(JournalJson.CurrentVersion, true));
}

/// <summary>Счётчики одной категории (или корзины): измерение и итог. Причины пропусков это ключи без префикса «cleanup-».</summary>
internal sealed class CleanupResult
{
    public long Bytes { get; set; }
    public long Files { get; set; }
    public long Deleted { get; set; }
    public long FreedBytes { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, int> Skipped { get; } = [];

    /// <summary>true: проход закончен без прерывания (не таймаут и не отказ корня).</summary>
    public bool Completed { get; set; }

    public void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;

    IReadOnlyDictionary<string, int>? SkippedOrNull => Skipped.Count > 0 ? new Dictionary<string, int>(Skipped) : null;

    public CleanupState Measured() => new(JournalJson.CurrentVersion, false, Bytes, Files, Skipped: SkippedOrNull);

    public CleanupState Outcome(bool done)
        => new(JournalJson.CurrentVersion, done, null, null, Deleted, FreedBytes, SkippedOrNull, Failed);
}

/// <summary>
/// Журнал очистки в памяти с пределом 10 МиБ: строки «D &lt;финальный путь&gt;», «S &lt;причина&gt; &lt;путь&gt;», «F &lt;путь&gt;».
/// По превышении остаётся строка «… truncated». Один буфер на операцию; в файл его пишет OperationHandler после выполнения.
/// </summary>
internal sealed class CleanupLogBuffer
{
    public const int MaxBytes = 10 * 1024 * 1024;
    const string TruncatedLine = "… truncated\n";

    readonly StringBuilder _sb = new();
    long _bytes;

    public bool Truncated { get; private set; }

    public int ByteCount => (int)_bytes;

    public void Line(string line)
    {
        if (Truncated) return;
        var clean = Sanitize(line);
        var n = Encoding.UTF8.GetByteCount(clean) + 1;
        if (_bytes + n > MaxBytes - Encoding.UTF8.GetByteCount(TruncatedLine))
        {
            _sb.Append(TruncatedLine);
            _bytes += Encoding.UTF8.GetByteCount(TruncatedLine);
            Truncated = true;
            return;
        }
        _sb.Append(clean).Append('\n');
        _bytes += n;
    }

    static string Sanitize(string s)
    {
        foreach (var ch in s)
            if (ch < ' ' || ch == '\u007f')
                return string.Create(s.Length, s, static (span, src) =>
                {
                    for (var i = 0; i < src.Length; i++) span[i] = src[i] < ' ' || src[i] == '\u007f' ? '?' : src[i];
                });
        return s;
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_sb.ToString());
}

/// <summary>
/// Очистка одной категории из каталога (C2). Не откатывается. Цель выводится агентом из каталога и SID клиента (корни раскрыты
/// планировщиком), а не берётся из запроса; каждый корень при открытии проверяется заново (<see cref="CleanupPolicy.CheckRoot"/>).
/// RecoveryOnly: шаг из журнала, запись невозможна.
/// </summary>
internal sealed class CleanupStep : PlannedStep
{
    readonly CleanupCategory _category;
    readonly CleanupTarget _target;

    public CleanupStep(CleanupCategory category, CleanupTarget target, CleanupLogBuffer? log)
        : base(StepKind.CleanupCategory, Key(target), CleanupJson.Target(target), CleanupJson.Desired(), category.Restart)
    {
        _category = category;
        _target = target;
        Log = log;
        Revertible = false;
    }

    public static string Key(CleanupTarget t) => $"cleanup:{t.Scope}:{t.Sid ?? "-"}:{t.Category}".ToLowerInvariant();

    public CleanupLogBuffer? Log { get; }

    /// <summary>Итог последнего выполненного прохода (в том числе прерванного): по нему строятся Facts ответа.</summary>
    public CleanupResult? Result { get; internal set; }

    public bool RecoveryOnly { get; init; }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        if (RecoveryOnly) return OpenOutcome.Refuse(StepCodes.PolicyDenied, "cleanup-recovery-only");
        var policy = env.Gate.Cleanup;

        var running = _category.SkipIfRunning is not null && env.Processes.IsRunning(_category.SkipIfRunning, _target.Sid ?? string.Empty);
        var nr = policy.CheckNotRunning(_category, running);
        if (nr.IsDenied) return OpenOutcome.Refuse(new Refusal(StepCodes.SkippedByRule, nr.Rule, SkipNotFound: true));

        var roots = new List<CleanupRootInfo>();
        foreach (var requested in _target.Roots)
        {
            var d = policy.CheckRoot(requested, env.Cleanup.Inspect(requested));
            if (d.IsAllowed) roots.Add(new CleanupRootInfo(requested, d.Target));
            else if (d.Rule != "cleanup-root-missing") return OpenOutcome.Refuse(StepCodes.PolicyDenied, d.Rule);
        }
        return OpenOutcome.Ok(new CleanupSession(env, _category, roots, forWrite, this));
    }
}

internal sealed record CleanupRootInfo(string Requested, string Final);

/// <summary>
/// Сессия очистки категории. ReadStateJson до записи это измерение (сколько файлов и байт подлежит удалению), после записи итог
/// done=true. Write обходит корни в глубину: файл → OpenForDelete → CheckOpened → Delete; каталог: дети, затем он сам (пустой).
/// Ссылки не входим и не удаляем; корень не удаляем; занято и отказ доступа это пропуск с причиной.
/// </summary>
internal sealed class CleanupSession(ExecEnv env, CleanupCategory category, IReadOnlyList<CleanupRootInfo> roots, bool writable, CleanupStep owner)
    : IStepSession
{
    public const int MaxEntries = 500_000;
    public const int MaxDepth = 64;
    public const int RootRecheckEvery = 1000;

    /// <summary>Сколько записей каталога держится в памяти за раз.</summary>
    public const int BatchSize = 1000;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);

    /// <summary>HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY): так порт сообщает «каталог не пуст».</summary>
    public const int HResultDirNotEmpty = unchecked((int)0x80070091);

    bool _written;

    public string? DoneCode { get; private set; }

    public string ReadStateJson()
    {
        if (_written) return CleanupJson.State(owner.Result!.Outcome(true));
        var r = new Walker(env, category, roots, dry: true, log: null).Run();
        return CleanupJson.State(r.Measured());
    }

    public void Write()
    {
        if (!writable) throw new InvalidOperationException("Сессия открыта без права записи");
        var w = new Walker(env, category, roots, dry: false, log: owner.Log);
        owner.Result = w.Result;
        w.Run();
        w.Result.Completed = true;
        _written = true;
        if (w.Result.Skipped.GetValueOrDefault("in-use") > 0) DoneCode = StepCodes.InUse;
    }

    public void Dispose()
    {
    }

    sealed class Walker(ExecEnv env, CleanupCategory cat, IReadOnlyList<CleanupRootInfo> roots, bool dry, CleanupLogBuffer? log)
    {
        readonly CleanupPolicy _policy = env.Gate.Cleanup;
        DateTimeOffset _start;
        int _entries;
        CleanupRootInfo _root = null!;

        public CleanupResult Result { get; } = new();

        public CleanupResult Run()
        {
            _start = env.Clock.UtcNow;
            log?.Line("# " + cat.Id);
            foreach (var root in roots)
            {
                _root = root;
                Walk(root.Requested, 0);
            }
            return Result;
        }

        void Tick()
        {
            if (++_entries > MaxEntries) throw new StepFailureException(StepCodes.Timeout);
            if (env.Clock.UtcNow - _start > MaxDuration) throw new StepFailureException(StepCodes.Timeout);
            if (_entries % RootRecheckEvery == 0) RecheckRoot();
        }

        // Корень мог быть подменён ссылкой посреди обхода: останавливаем категорию.
        void RecheckRoot()
        {
            var d = _policy.CheckRoot(_root.Requested, env.Cleanup.Inspect(_root.Requested));
            if (!d.IsAllowed || !string.Equals(d.Target, _root.Final, StringComparison.OrdinalIgnoreCase))
                throw new StepFailureException(StepCodes.PolicyDenied);
        }

        static string Reason(string rule) => rule.StartsWith("cleanup-", StringComparison.Ordinal) ? rule["cleanup-".Length..] : rule;

        void Skip(string reason, string path)
        {
            if (reason == "pattern") return;      // не подошло под шаблон: это не пропуск, а чужие файлы каталога
            Result.Skip(reason);
            log?.Line($"S {reason} {path}");
        }

        void Walk(string dir, int depth)
        {
            // Каталог читается лениво блоками по BatchSize: на миллионах записей полный список не удерживается в памяти (OOM).
            // Блок обрабатывается (с удалением по хэндлам) до следующего MoveNext; удаление уже отданных записей во время
            // перечисления допустимо на NTFS и не сбивает позицию, а во весь список после этого ничего не возвращается.
            // Оговорка: на FAT/exFAT удаление во время перечисления может сдвинуть позицию, и часть записей перечисление пропустит.
            // Последствие одностороннее: недоудаление (файл останется до следующей очистки), а не удаление лишнего: каждая запись
            // всё равно проходит CheckEntry и открытие по хэндлу, а пропущенная до них не доходит.
            IEnumerator<DirEntryFacts> it;
            try { it = env.Cleanup.Enumerate(dir).GetEnumerator(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Skip("access-denied", dir);
                return;
            }

            using (it)
            {
                var batch = new List<DirEntryFacts>(BatchSize);
                for (var more = true; more;)
                {
                    batch.Clear();
                    try
                    {
                        while (batch.Count < BatchSize)
                        {
                            if (!it.MoveNext())
                            {
                                more = false;
                                break;
                            }
                            batch.Add(it.Current);
                        }
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                    {
                        Skip("access-denied", dir);
                        more = false;
                    }

                    foreach (var e in batch)
                    {
                        Tick();
                        var d = _policy.CheckEntry(_root.Final, e, _start, cat);
                        var path = dir + "\\" + e.Name;
                        if (d.IsDenied)
                        {
                            Skip(Reason(d.Rule), path);
                            continue;
                        }
                        if (e.IsDirectory) Directory(path, depth);
                        else File(path, e);
                    }
                }
            }
        }

        void File(string path, DirEntryFacts e)
        {
            if (dry)
            {
                Result.Files++;
                Result.Bytes += e.Length;
                return;
            }
            try
            {
                using var h = env.Cleanup.OpenForDelete(path, false);
                if (h is null) return;
                var o = _policy.CheckOpened(_root.Final, h.FinalPath, h.IsReparse);
                if (o.IsDenied)
                {
                    Skip(Reason(o.Rule), path);
                    return;
                }
                h.Delete();
                Result.Deleted++;
                Result.FreedBytes += e.Length;
                log?.Line("D " + h.FinalPath);
            }
            catch (Exception ex) when (Classify(ex, path)) { }
        }

        void Directory(string path, int depth)
        {
            if (dry)
            {
                if (depth + 1 >= MaxDepth) return;
                Result.Files++;
                Walk(path, depth + 1);
                return;
            }
            try
            {
                using var h = env.Cleanup.OpenForDelete(path, true);
                if (h is null) return;
                var o = _policy.CheckOpened(_root.Final, h.FinalPath, h.IsReparse);
                if (o.IsDenied)
                {
                    Skip(Reason(o.Rule), path);
                    return;
                }
                if (depth + 1 >= MaxDepth)
                {
                    Skip("too-deep", path);
                    return;
                }
                Walk(path, depth + 1);
                try
                {
                    h.Delete();
                    Result.Deleted++;
                    log?.Line("D " + h.FinalPath);
                }
                catch (IOException io) when (io is not SharingViolationException && io.HResult == HResultDirNotEmpty)
                {
                    Skip("not-empty", path);
                }
            }
            catch (Exception ex) when (Classify(ex, path)) { }
        }

        // true: исключение разобрано как пропуск или сбой одного элемента; прочее (таймаут, обрыв процесса) идёт выше.
        bool Classify(Exception ex, string path)
        {
            switch (ex)
            {
                case SharingViolationException:
                    Skip("in-use", path);
                    return true;
                case UnauthorizedAccessException:
                    Skip("access-denied", path);
                    return true;
                case IOException or Win32Exception:
                    Result.Failed++;
                    log?.Line("F " + path);
                    return true;
                default:
                    return false;
            }
        }
    }
}

/// <summary>Очистка корзины токеном клиента (C2). Корзину чужого пользователя не трогаем: SID токена агента должен совпасть с SID клиента.</summary>
internal sealed class RecycleBinStep : PlannedStep
{
    readonly string _sid;

    public RecycleBinStep(CleanupCategory category, string sid)
        : base(StepKind.RecycleBinEmpty, $"recyclebin:{sid}".ToLowerInvariant(),
            CleanupJson.Target(new CleanupTarget(JournalJson.CurrentVersion, category.Id, "user", sid, [], 0)), CleanupJson.Desired())
    {
        _sid = sid;
        Revertible = false;
    }

    public CleanupResult? Result { get; internal set; }

    public bool RecoveryOnly { get; init; }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        if (RecoveryOnly) return OpenOutcome.Refuse(StepCodes.PolicyDenied, "cleanup-recovery-only");
        var d = env.Gate.Cleanup.CheckRecycleBin(env.RecycleBin.CurrentTokenSid(), _sid);
        if (d.IsDenied) return OpenOutcome.Refuse(StepCodes.PolicyDenied, d.Rule);
        return OpenOutcome.Ok(new RecycleBinSession(env, forWrite, this));
    }

    sealed class RecycleBinSession(ExecEnv env, bool writable, RecycleBinStep owner) : IStepSession
    {
        bool _written;
        long _before;

        public string ReadStateJson()
        {
            var bytes = env.RecycleBin.QueryBytes();
            var done = _written && bytes == 0;
            return CleanupJson.State(new CleanupState(JournalJson.CurrentVersion, done, bytes));
        }

        public void Write()
        {
            if (!writable) throw new InvalidOperationException("Сессия открыта без права записи");
            _before = env.RecycleBin.QueryBytes();
            var r = new CleanupResult { FreedBytes = _before };
            owner.Result = r;
            env.RecycleBin.Empty();
            r.Completed = true;
            _written = true;
        }

        public void Dispose()
        {
        }
    }
}

internal static class CleanupPlanner
{
    const int MaxExpandedRoots = 64;

    /// <summary>План очистки: по шагу на категорию, продолжение после отказа. Неизвестная категория или категория без удаления это PlanException.</summary>
    public static OperationPlan Plan(ExecEnv env, CleanCategoriesRequest r, string clientSid)
    {
        var log = new CleanupLogBuffer();
        var steps = new List<PlannedStep>();
        var profile = new ProfileCache(env, clientSid);
        foreach (var id in r.Categories)
            steps.Add(BuildStep(env, Find(id), clientSid, profile, log));
        return new OperationPlan(OperationKind.Cleanup, string.Join(",", r.Categories), steps) { ContinueOnFailure = true };
    }

    /// <summary>Измерение без записи в журнал: по записи FactEntry на категорию (Key = ключ цели, Json = CleanupState: bytes, files, skipped).</summary>
    public static IReadOnlyList<FactEntry> Measure(ExecEnv env, CleanCategoriesRequest r, string clientSid)
    {
        var facts = new List<FactEntry>();
        var profile = new ProfileCache(env, clientSid);
        foreach (var id in r.Categories)
        {
            var c = Find(id);
            if (c.Mechanism == "handler")
            {
                facts.Add(new FactEntry($"cleanup:{c.Scope}:-:{c.Id}".ToLowerInvariant(),
                    CleanupJson.State(new CleanupState(JournalJson.CurrentVersion, false, Skipped: new Dictionary<string, int> { ["handler"] = 1 }))));
                continue;
            }
            var step = BuildStep(env, c, clientSid, profile, null);
            facts.Add(new FactEntry(step.TargetKey, MeasureStep(env, step)));
        }
        return facts;
    }

    static string MeasureStep(ExecEnv env, PlannedStep step)
    {
        static string Zero(string reason) => CleanupJson.State(new CleanupState(JournalJson.CurrentVersion, false, 0, 0,
            Skipped: new Dictionary<string, int> { [reason] = 1 }));
        try
        {
            var o = step.Open(env, null, false);
            if (o.Refusal is { } refusal) return Zero(refusal.Rule is { } rule ? (rule.StartsWith("cleanup-", StringComparison.Ordinal) ? rule[8..] : rule) : refusal.Code);
            using var session = o.Session!;
            return session.ReadStateJson();
        }
        catch (StepFailureException e)
        {
            return Zero(e.Code);
        }
    }

    static CleanupCategory Find(string id)
    {
        var c = CleanupCatalog.Shared.Find(id);
        if (c is null) throw new PlanException(StepCodes.NotFound, "Категория очистки не найдена: " + id);
        return c;
    }

    // Профиль клиента проверяется один раз на запрос.
    sealed class ProfileCache(ExecEnv env, string sid)
    {
        bool _done;
        string? _dir;
        Refusal? _refusal;

        public (string? Dir, Refusal? Refusal) Get()
        {
            if (_done) return (_dir, _refusal);
            _done = true;
            var dir = env.Folders.ProfileDir(sid);
            if (string.IsNullOrWhiteSpace(dir))
            {
                _refusal = new Refusal(StepCodes.NotFound, "cleanup-root-missing", SkipNotFound: true);
                return (null, _refusal);
            }
            var f = env.Cleanup.Inspect(dir);
            var d = env.Gate.Cleanup.CheckProfileDir(sid, dir, new DirFacts(f.Exists, f.IsReparse, f.OwnerSid, f.Aces));
            if (d.IsDenied)
            {
                _refusal = d.Rule == "cleanup-root-missing"
                    ? new Refusal(StepCodes.NotFound, d.Rule, SkipNotFound: true)
                    : new Refusal(StepCodes.PolicyDenied, d.Rule);
                return (null, _refusal);
            }
            _dir = dir;
            return (_dir, null);
        }
    }

    static PlannedStep BuildStep(ExecEnv env, CleanupCategory c, string sid, ProfileCache profile, CleanupLogBuffer? log)
    {
        if (c.Mechanism == "handler")
            throw new PlanException(StepCodes.PolicyDenied, "Категория очищается штатной очисткой диска Windows: " + c.Id);
        if (c.Mechanism == "recyclebin") return new RecycleBinStep(c, sid);

        var policy = env.Gate.Cleanup;
        var user = c.Scope == "user";
        var target = new CleanupTarget(JournalJson.CurrentVersion, c.Id, c.Scope, user ? sid : null, [], c.MinAgeHours);
        PlannedStep Refused(Refusal r) => new CleanupStep(c, target, log) { PresetRefusal = r };

        string? profileDir = null;
        if (user)
        {
            var (dir, refusal) = profile.Get();
            if (refusal is not null) return Refused(refusal);
            profileDir = dir;
        }
        var rd = policy.ResolveRoots(c, profileDir, out var resolved);
        if (rd.IsDenied) return Refused(new Refusal(StepCodes.PolicyDenied, rd.Rule));

        var roots = new List<string>();
        foreach (var root in resolved)
        {
            if (!root.Contains('*'))
            {
                roots.Add(root);
                continue;
            }
            if (!CleanupPolicy.TrySplitWildcard(root, out var parent, out var suffix))
                return Refused(new Refusal(StepCodes.PolicyDenied, "cleanup-bad-catalog"));
            IEnumerable<DirEntryFacts> entries;
            try { entries = env.Cleanup.Enumerate(parent).ToList(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { entries = []; }
            roots.AddRange(CleanupPolicy.ExpandWildcard(parent, suffix, entries));
        }
        if (roots.Count > MaxExpandedRoots) return Refused(new Refusal(StepCodes.PolicyDenied, "cleanup-bad-catalog"));
        return new CleanupStep(c, target with { Roots = roots }, log);
    }
}

/// <summary>Итог выполнения очистки для ответа: Facts по шагам и файл журнала очистки.</summary>
internal static class CleanupReports
{
    public static IReadOnlyList<FactEntry>? Collect(ExecEnv env, Guid opId, OperationPlan plan)
    {
        var facts = new List<FactEntry>();
        CleanupLogBuffer? log = null;
        foreach (var step in plan.Steps)
        {
            CleanupResult? r;
            switch (step)
            {
                case CleanupStep s:
                    r = s.Result;
                    log ??= s.Log;
                    break;
                case RecycleBinStep b:
                    r = b.Result;
                    break;
                default:
                    continue;
            }
            if (r is null) continue;
            facts.Add(new FactEntry(step.TargetKey, CleanupJson.State(r.Outcome(r.Completed))));
        }

        if (log is { ByteCount: > 0 } && env.CleanupLog is { } store)
        {
            try { store.Write(opId.ToString("D") + ".log", log.ToBytes()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // журнал очистки вспомогательный: его сбой не меняет итог операции
            }
        }
        return facts.Count > 0 ? facts : null;
    }
}
