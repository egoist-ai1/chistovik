using System.Security.Principal;
using System.Text.Json;
using Chistovik.Agent;
using Chistovik.Core.Catalog;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.Native.Ipc;
using Microsoft.Win32;

namespace Sandbox.Live;

/// <summary>Аргументы командной строки вида «--имя значение»; флаг без значения даёт "1".</summary>
sealed class Options
{
    readonly Dictionary<string, string> _v = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            o._v[key] = hasValue ? args[i + 1] : "1";
            if (hasValue) i++;
        }
        return o;
    }

    public string? Get(string name) => _v.GetValueOrDefault(name);
    public bool Has(string name) => _v.ContainsKey(name);
}

/// <summary>Общие данные запуска.</summary>
sealed class LiveContext
{
    public required Report Report { get; init; }
    public required string Sid { get; init; }
    public required string WorkDir { get; init; }
    public required string Token { get; init; }
    public required string SelfExe { get; init; }
    public string? AgentPath { get; init; }
    public string? SvcExe { get; init; }
    /// <summary>Почему нет подписанного тестового файла (пишет guest-run.ps1); попадает в ПРОПУСК S4.</summary>
    public string? SvcSkipReason { get; init; }
    public bool SkipIdle { get; init; }
    /// <summary>Папка с установщиками для S9 (*.msi, *.exe); хост монтирует её на чтение.</summary>
    public string? InstallersDir { get; init; }
}

/// <summary>Заранее заданные GUID (идентификаторы операций и новых схем питания), дальше обычные случайные.</summary>
sealed class ScriptedIds(params Guid[] scripted) : IIdGenerator
{
    readonly Queue<Guid> _queue = new(scripted);
    readonly object _gate = new();

    public List<Guid> Issued { get; } = [];

    public Guid NewGuid()
    {
        lock (_gate)
        {
            var g = _queue.Count > 0 ? _queue.Dequeue() : Guid.NewGuid();
            Issued.Add(g);
            return g;
        }
    }
}

/// <summary>
/// Настоящий граф агента (AgentComposition.Build с AgentPorts.Windows()) и настоящий OperationHandler с журналом SQLite
/// в отдельном каталоге внутри Sandbox. Запросы идут тем же путём, что и из канала, без самого канала.
/// </summary>
sealed class Session : IDisposable
{
    readonly LiveContext _c;
    public AgentComposition Comp { get; }
    public string Dir { get; }
    public string DbPath => Path.Combine(Dir, AgentComposition.DatabaseFileName);

    Session(LiveContext c, string dir, AgentComposition comp)
    {
        _c = c;
        Dir = dir;
        Comp = comp;
    }

    public static Session Open(LiveContext c, string name, Func<AgentPorts, AgentPorts>? tune = null, string? existingDir = null)
    {
        var dir = existingDir ?? Path.Combine(c.WorkDir, name, "journal");
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var ports = AgentPorts.Windows();
        if (tune is not null) ports = tune(ports);
        var comp = AgentComposition.Build(ports, dir, PolicyContext.FromEnvironment(), m => Console.WriteLine("  [агент] " + m));
        if (comp.Mode != JournalMode.Ready)
        {
            comp.Dispose();
            throw new InvalidOperationException($"Журнал не открылся: {comp.Mode} {comp.Reason}");
        }
        return new Session(c, dir, comp);
    }

    public Task<AgentResponse> Send(AgentRequest request)
        => Comp.Handler.HandleAsync(request, new ClientContext(_c.Sid, Environment.ProcessId, ClientProbe.CurrentSession()), CancellationToken.None);

    public Task<AgentResponse> Undo(Guid operationId) => Send(new UndoRequest { Id = Guid.NewGuid(), OperationId = operationId });

    /// <summary>Правила Policy из шагов операции (для понятного FAIL).</summary>
    public string Rules(AgentResponse r)
    {
        if (r.OperationId is not { } op) return "";
        try
        {
            using var reader = new SqliteJournalReader(DbPath);
            var rules = reader.Get(op)?.Steps.Select(s => s.PolicyRule).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray() ?? [];
            return rules.Length == 0 ? "" : "; правила: " + string.Join(",", rules);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or JournalException)
        {
            return "";
        }
    }

    public string Describe(AgentResponse r)
        => r.Code + (string.IsNullOrEmpty(r.Message) ? "" : " (" + r.Message + ")")
           + (r.Steps is { Count: > 0 } ? "; шаги: " + string.Join(", ", r.Steps.Select(x => $"{x.Kind}:{x.State}" + (x.Code is null ? "" : "/" + x.Code))) : "")
           + Rules(r);

    public void Dispose() => Comp.Dispose();
}

/// <summary>Вспомогательное для твиков каталога.</summary>
static class Tweaks
{
    public sealed record Expectation(bool Exists, RegistryValueKind Kind, string Text)
    {
        public bool Matches(RegSnap s) => Exists == s.Exists && (!Exists || (Kind == s.Kind && Text == s.Text));
        public override string ToString() => Exists ? $"{Kind}:{Text}" : "<нет значения>";
    }

    /// <summary>Ожидаемое значение для состояния твика (как в TweakPlanner.Desired, но независимо от него).</summary>
    public static Expectation Expect(TweakEntry t, bool enabled)
    {
        JsonElement? v = enabled ? t.OnValue : t.OffValue;
        if (v is null || v.Value.ValueKind == JsonValueKind.Null) return new Expectation(false, RegistryValueKind.None, "");
        return t.ValueType == "DWord"
            ? new Expectation(true, RegistryValueKind.DWord, v.Value.GetUInt32().ToString())
            : new Expectation(true, RegistryValueKind.String, v.Value.GetString() ?? "");
    }

    public static bool IsUser(TweakEntry t) => t.Hive == "HKCU";

    public static RegSnap Read(LiveContext c, TweakEntry t) => Reg.Read(IsUser(t), c.Sid, t.Key, t.Value);

    /// <summary>Записывает состояние напрямую в реестр (подготовка сценария).</summary>
    public static void Force(LiveContext c, TweakEntry t, Expectation e)
    {
        var user = IsUser(t);
        if (!e.Exists) { Reg.Delete(user, c.Sid, t.Key, t.Value); return; }
        if (e.Kind == RegistryValueKind.DWord) Reg.SetRaw(user, c.Sid, t.Key, t.Value, unchecked((int)uint.Parse(e.Text)), RegistryValueKind.DWord);
        else Reg.SetRaw(user, c.Sid, t.Key, t.Value, e.Text, RegistryValueKind.String);
    }

    /// <summary>Простой пользовательский DWord-твик с двумя значениями: для сценариев журнала и канала.</summary>
    public static TweakEntry? SimpleUserDword()
        => TweakCatalog.Shared.Tweaks.FirstOrDefault(t => IsUser(t) && t.ValueType == "DWord"
            && t.OffValue is { ValueKind: JsonValueKind.Number });

    public static ApplyTweaksRequest Request(string id, bool enabled)
        => new() { Id = Guid.NewGuid(), Changes = [new TweakChange { TweakId = id, Enabled = enabled }] };
}

static class Sids
{
    public static string Current() => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Нет SID пользователя");

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
