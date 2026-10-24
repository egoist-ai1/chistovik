using System.Text.RegularExpressions;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 3. Питание: выбор встроенной схемы и откат, параметр схемы (min) и откат, схема Ultimate Performance
/// (копия шаблона с заданным GUID через IIdGenerator, активация, откат с удалением своей схемы). Независимая проверка
/// идёт через powercfg.exe. Всё, что создал стенд, удаляется; исходная активная схема возвращается.
/// </summary>
static class S3Power
{
    const string Sc = "S3 питание";
    static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);

    static Guid? Active()
    {
        var (_, o) = Proc.Run("powercfg.exe", ["/getactivescheme"]);
        var m = GuidRx.Match(o);
        return m.Success ? Guid.Parse(m.Value) : null;
    }

    static Dictionary<Guid, string> Schemes()
    {
        var (_, o) = Proc.Run("powercfg.exe", ["/list"]);
        var d = new Dictionary<Guid, string>();
        foreach (var line in o.Split('\n'))
        {
            var m = GuidRx.Match(line);
            if (!m.Success) continue;
            var open = line.IndexOf('(', m.Index);
            var close = line.LastIndexOf(')');
            d[Guid.Parse(m.Value)] = open >= 0 && close > open ? line[(open + 1)..close] : "";
        }
        return d;
    }

    /// <summary>Текущие значения параметра (AC, DC) из powercfg /query: последние два шестнадцатеричных числа вывода.</summary>
    static (uint Ac, uint Dc)? Query(Guid scheme, Guid sub, Guid setting)
    {
        var (rc, o) = Proc.Run("powercfg.exe", ["/query", scheme.ToString("D"), sub.ToString("D"), setting.ToString("D")]);
        if (rc != 0) return null;
        var hex = Regex.Matches(o, "0x[0-9a-fA-F]{8}");
        if (hex.Count < 2) return null;
        uint P(Match m) => Convert.ToUInt32(m.Value[2..], 16);
        return (P(hex[^2]), P(hex[^1]));
    }

    public static async Task Run(LiveContext c)
    {
        var original = Active();
        if (original is null)
        {
            c.Report.Skip(Sc, "питание", "powercfg не вернул активную схему");
            return;
        }

        var createdIds = new List<Guid>();
        var settingEntry = PowerCatalog.Shared.FindSetting("min")!;
        var before = Query(original.Value, settingEntry.Subgroup, settingEntry.Setting);
        try
        {
            await BuiltIn(c, original.Value);
            await Setting(c, settingEntry);
            await Ultimate(c, original.Value, createdIds);
        }
        finally
        {
            Proc.Run("powercfg.exe", ["/setactive", original.Value.ToString("D")]);
            if (before is { } b)
            {
                Proc.Run("powercfg.exe", ["/setacvalueindex", original.Value.ToString("D"), settingEntry.Subgroup.ToString("D"), settingEntry.Setting.ToString("D"), b.Ac.ToString()]);
                Proc.Run("powercfg.exe", ["/setdcvalueindex", original.Value.ToString("D"), settingEntry.Subgroup.ToString("D"), settingEntry.Setting.ToString("D"), b.Dc.ToString()]);
            }
            // схемы, оставшиеся от стенда или от Чистовика (имя «Chistovik …»): убираем
            foreach (var (g, n) in Schemes())
                if (n.StartsWith("Chistovik ", StringComparison.Ordinal) || createdIds.Contains(g))
                    if (g != original.Value) Proc.Run("powercfg.exe", ["/delete", g.ToString("D")]);
        }
    }

    static async Task BuiltIn(LiveContext c, Guid original)
    {
        using var s = Session.Open(c, "s3a");
        var installed = Schemes();
        var pick = PowerCatalog.Shared.Schemes.FirstOrDefault(x => x.BuiltIn && x.Guid != original && installed.ContainsKey(x.Guid));
        if (pick is null)
        {
            c.Report.Skip(Sc, "встроенная схема", "нет другой встроенной схемы из каталога среди установленных");
            return;
        }

        var plan = pick.Id switch { "saver" => PowerPlan.Saver, "bal" => PowerPlan.Bal, "high" => PowerPlan.High, _ => PowerPlan.Bal };
        var r = await s.Send(new SetPowerSchemeRequest { Id = Guid.NewGuid(), Plan = plan });
        var now = Active();
        c.Report.Check(Sc, $"схема {pick.Id}: активация", r.Ok && now == pick.Guid, $"активна {now}", $"ответ: {s.Describe(r)}; активна {now}, ждали {pick.Guid}");
        if (r.OperationId is { } op)
        {
            var u = await s.Undo(op);
            var back = Active();
            c.Report.Check(Sc, $"схема {pick.Id}: откат", u.Ok && back == original, $"снова активна {back}", $"ответ: {s.Describe(u)}; активна {back}, ждали {original}");
        }
    }

    static async Task Setting(LiveContext c, PowerSettingEntry e)
    {
        using var s = Session.Open(c, "s3b");
        var active = Active()!.Value;
        var cur = Query(active, e.Subgroup, e.Setting);
        if (cur is null)
        {
            c.Report.Skip(Sc, "параметр min", "powercfg /query не вернул значения параметра");
            return;
        }

        var newAc = cur.Value.Ac == 50 ? 60u : 50u;
        var r = await s.Send(new SetPowerSettingRequest { Id = Guid.NewGuid(), SettingId = e.Id, Ac = newAc });
        var after = Query(active, e.Subgroup, e.Setting);
        c.Report.Check(Sc, "параметр min: запись (AC)", r.Ok && after?.Ac == newAc && after?.Dc == cur.Value.Dc,
            $"AC {cur.Value.Ac} -> {after?.Ac}, DC без изменений {after?.Dc}",
            $"ответ: {s.Describe(r)}; AC {after?.Ac} (ждали {newAc}), DC {after?.Dc} (было {cur.Value.Dc})");
        if (r.OperationId is { } op)
        {
            var u = await s.Undo(op);
            var back = Query(active, e.Subgroup, e.Setting);
            c.Report.Check(Sc, "параметр min: откат", u.Ok && back == cur, $"возврат к {back?.Ac}/{back?.Dc}",
                $"ответ: {s.Describe(u)}; сейчас {back?.Ac}/{back?.Dc}, было {cur.Value.Ac}/{cur.Value.Dc}");
        }
    }

    static async Task Ultimate(LiveContext c, Guid original, List<Guid> created)
    {
        var template = PowerCatalog.Shared.FindScheme("ult")!;
        var opId = Guid.Parse("c0de0001-0000-4000-8000-000000000001");
        var schemeId = Guid.Parse("c0de0002-0000-4000-8000-000000000002");
        var ids = new ScriptedIds(opId, schemeId);
        using var s = Session.Open(c, "s3c", p => p with { Ids = ids });
        created.Add(schemeId);

        var r = await s.Send(new SetPowerSchemeRequest { Id = Guid.NewGuid(), Plan = PowerPlan.Ult });
        var list = Schemes();
        if (!r.Ok && r.Steps?.Any(x => x.Code == "not-found") == true)
        {
            c.Report.Skip(Sc, "Ultimate Performance", "в этом образе Windows нет шаблона схемы (powercfg не умеет её дублировать): " + s.Describe(r));
            return;
        }

        var ours = list.TryGetValue(schemeId, out var name);
        c.Report.Check(Sc, "Ultimate: копия шаблона с заданным GUID", r.Ok && ours,
            $"создана {schemeId} «{name}» (GUID выдал IIdGenerator)", $"ответ: {s.Describe(r)}; схемы: {string.Join(", ", list.Keys)}");
        c.Report.Check(Sc, "Ultimate: имя своей схемы", ours && name!.StartsWith("Chistovik ", StringComparison.Ordinal), $"имя «{name}»", $"имя «{name}», ждали префикс «Chistovik »");
        c.Report.Check(Sc, "Ultimate: схема активна", Active() == schemeId, "активна своя копия", $"активна {Active()}");
        if (!list.ContainsKey(template.Guid)) c.Report.Pass(Sc, "Ultimate: шаблон не тронут", "шаблона в списке нет и он не трогался");

        if (r.OperationId is { } op)
        {
            var u = await s.Undo(op);
            var after = Schemes();
            c.Report.Check(Sc, "Ultimate: откат возвращает схему", u.Ok && Active() == original, $"активна {Active()}", $"ответ: {s.Describe(u)}; активна {Active()}, ждали {original}");
            c.Report.Check(Sc, "Ultimate: откат удаляет только свою схему", !after.ContainsKey(schemeId), "своей копии в списке нет",
                $"своя копия осталась в списке; ответ отката: {s.Describe(u)}");
        }
    }
}
