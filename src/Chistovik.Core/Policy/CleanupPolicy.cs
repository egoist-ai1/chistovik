using System.Text.RegularExpressions;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Policy;

/// <summary>
/// Политика очистки диска (C2), отдельно от <see cref="PathPolicy"/> (карантин). Все решения это чистые функции от фактов:
/// ввода-вывода здесь нет, факты (FileFacts, DirEntryFacts, финальные пути хэндлов) приносит исполнитель.
/// Корень берётся из встроенного каталога и SID клиента, никогда из запроса; корень сам не удаляется; ссылки не следуем.
/// </summary>
public sealed partial class CleanupPolicy(PolicyContext ctx)
{
    public static readonly string[] AllowedBases = { "windir", "programdata", "systemdrive", "userlocal", "userroaming" };

    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    const string AdministratorsSid = "S-1-5-32-544";
    const string SystemSid = "S-1-5-18";

    static readonly string[] MachineBases = { "windir", "programdata", "systemdrive" };
    static readonly string[] UserBases = { "userlocal", "userroaming" };

    // Закрытые списки корней машинных баз (Sub, компоненты через «\»). Всё прочее под windir/programdata/systemdrive отвергается.
    static readonly string[] WindirAllowed = { @"Temp", @"Logs\CBS", @"Minidump" };
    static readonly string[] ProgramDataAllowed =
    {
        @"Microsoft\Windows\WER\ReportQueue", @"Microsoft\Windows\WER\ReportArchive", @"Microsoft\Windows\WER\Temp",
    };

    // Под userlocal/userroaming: в Microsoft\Windows разрешены только WER и Explorer; прочие системные ветки закрыты.
    static readonly string[] UserMicrosoftWindowsAllowed = { @"WER\ReportQueue", @"WER\ReportArchive", @"Explorer" };
    static readonly HashSet<string> UserMicrosoftDeny = new(Cmp)
        { "Credentials", "Crypto", "Protect", "Vault", "SystemCertificates", "Windows Defender", "WindowsApps" };

    [GeneratedRegex(@"^[A-Za-z0-9_.\-*?]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PatternChars();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageName();

    [GeneratedRegex(@"^S-1-[0-9]+(-[0-9]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidFormat();

    // ---------- каталог ----------

    /// <summary>Структурная проверка категории (вызывается и при загрузке каталога).</summary>
    public PolicyDecision CheckCatalog(CleanupCategory c) => ValidateCategory(c);

    /// <summary>Не зависит от контекста машины: используется загрузчиком <see cref="CleanupCatalog"/>.</summary>
    public static PolicyDecision ValidateCategory(CleanupCategory? c)
    {
        if (c is null) return Bad("пустая категория");
        if (c.Id is null || !Rules.CatalogId.IsMatch(c.Id)) return Bad("недопустимый id");
        var w = $"категория «{c.Id}»: ";
        if (c.Scope is not ("machine" or "user")) return Bad(w + "scope вне machine|user");
        if (c.Mechanism is not ("files" or "recyclebin" or "handler")) return Bad(w + "mechanism вне files|recyclebin|handler");
        if (c.Restart is not ("none" or "explorer")) return Bad(w + "restart вне none|explorer");
        if (c.MinAgeHours < 0 || c.MinAgeHours > 24 * 365) return Bad(w + "minAgeHours вне 0..8760");
        if (c.SkipIfRunning is not null && !ImageName().IsMatch(c.SkipIfRunning)) return Bad(w + "skipIfRunning: недопустимое имя образа");
        if (c.Roots is null) return Bad(w + "roots не задан");

        if (c.Mechanism != "files")
        {
            if (c.Roots.Count != 0) return Bad(w + "у recyclebin/handler корней нет");
            if (c.KeepNames is { Count: > 0 } || c.Patterns is { Count: > 0 }) return Bad(w + "keepNames/patterns только для files");
            if (c.Mechanism == "recyclebin" && c.Scope != "user") return Bad(w + "корзина только для user");
            return PolicyDecision.Allow("cleanup-catalog-ok");
        }

        if (c.Roots.Count is 0 or > 8) return Bad(w + "корней должно быть 1..8");
        var seen = new HashSet<string>(Cmp);
        foreach (var r in c.Roots)
        {
            if (r is null) return Bad(w + "пустой корень");
            if (Array.IndexOf(AllowedBases, r.Base) < 0) return Bad(w + $"база «{r.Base}» вне списка");
            var machineBase = Array.IndexOf(MachineBases, r.Base) >= 0;
            if (machineBase != (c.Scope == "machine")) return Bad(w + $"база «{r.Base}» не подходит scope «{c.Scope}»");
            if (CheckSub(r.Base, r.Sub) is { } err) return Bad(w + err);
            if (!seen.Add(r.Base + "|" + r.Sub)) return Bad(w + "повторяющийся корень");
        }
        foreach (var k in c.KeepNames ?? [])
            if (string.IsNullOrWhiteSpace(k) || k.IndexOfAny(['\\', '/', ':', '*', '?']) >= 0 || k is "." or "..")
                return Bad(w + "недопустимое имя в keepNames");
        foreach (var p in c.Patterns ?? [])
            if (p is null || !PatternChars().IsMatch(p) || p.Trim('*', '?').Length == 0)
                return Bad(w + "недопустимый шаблон в patterns");
        return PolicyDecision.Allow("cleanup-catalog-ok");

        static PolicyDecision Bad(string why) => PolicyDecision.Deny("Каталог очистки: " + why, "cleanup-bad-catalog");
    }

    // null, если Sub допустим.
    static string? CheckSub(string @base, string? sub)
    {
        if (string.IsNullOrWhiteSpace(sub)) return "пустой Sub";
        if (sub.Length > 260) return "Sub слишком длинный";
        if (sub.Contains("..", StringComparison.Ordinal)) return "«..» в Sub";
        if (sub.Contains(':')) return "«:» в Sub";
        if (sub.Contains(@"\\", StringComparison.Ordinal) || sub.Contains('/')) return "двойной или прямой слеш в Sub";
        if (sub.StartsWith('\\') || sub.EndsWith('\\')) return "Sub не должен начинаться или кончаться «\\»";
        if (sub.Contains('%')) return "«%» в Sub";
        var comps = sub.Split('\\');
        var stars = 0;
        for (var i = 0; i < comps.Length; i++)
        {
            var comp = comps[i];
            if (comp.Length == 0 || comp == ".") return "пустой компонент в Sub";
            if (comp.EndsWith('.') || comp.EndsWith(' ')) return "компонент оканчивается точкой или пробелом";
            if (comp.IndexOfAny(['<', '>', '"', '|', '?']) >= 0 || comp.Any(ch => ch < 0x20)) return "недопустимые символы в Sub";
            if (comp.Contains('*'))
            {
                // Подстановка: целиком один компонент, один раз, не первый и не последний.
                if (comp != "*" || i == 0 || i == comps.Length - 1) return "«*» только целым средним компонентом";
                stars++;
            }
        }
        if (stars > 1) return "больше одной подстановки";
        if (stars == 1 && @base != "userlocal") return "подстановка допустима только в userlocal";

        switch (@base)
        {
            case "windir": return StartsWithAny(comps, WindirAllowed) ? null : "Sub вне закрытого списка windir";
            case "programdata": return StartsWithAny(comps, ProgramDataAllowed) ? null : "Sub вне закрытого списка programdata";
            case "systemdrive": return "корни на системном диске не допускаются";
            case "userroaming": return "корни в Roaming не допускаются";
            case "userlocal":
                if (comps[0].Equals("Packages", StringComparison.OrdinalIgnoreCase)) return "Packages закрыт";
                if (comps.Length >= 2 && comps[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    if (UserMicrosoftDeny.Contains(comps[1])) return "системная ветка Microsoft закрыта";
                    if (comps[1].Equals("Windows", StringComparison.OrdinalIgnoreCase)
                        && !StartsWithAny(comps[2..], UserMicrosoftWindowsAllowed))
                        return "Microsoft\\Windows: разрешены только WER и Explorer";
                }
                if (comps[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && comps.Length == 1) return "Microsoft целиком закрыт";
                return null;
            default: return "неизвестная база";
        }
    }

    static bool StartsWithAny(string[] comps, string[] allowed)
    {
        foreach (var a in allowed)
        {
            var ac = a.Split('\\');
            if (comps.Length < ac.Length) continue;
            var ok = true;
            for (var i = 0; i < ac.Length && ok; i++) ok = comps[i].Equals(ac[i], StringComparison.OrdinalIgnoreCase);
            if (ok) return true;
        }
        return false;
    }

    // ---------- корни ----------

    /// <summary>
    /// Корни категории из каталога. Для user-баз нужен проверенный (CheckProfileDir) каталог профиля.
    /// Корень с подстановкой «*» возвращается как есть (шаблон): раскрывает его <see cref="TrySplitWildcard"/> +
    /// <see cref="ExpandWildcard"/> по фактам перечисления, и только на один уровень по не-ссылочным каталогам.
    /// </summary>
    public PolicyDecision ResolveRoots(CleanupCategory c, string? profileDir, out IReadOnlyList<string> roots)
    {
        roots = [];
        var cat = CheckCatalog(c);
        if (cat.IsDenied) return cat;
        if (c.Mechanism != "files") return PolicyDecision.Allow("cleanup-no-roots");

        string? profile = null;
        if (c.Scope == "user")
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return PolicyDecision.Deny("Профиль пользователя не найден", "cleanup-root-missing");
            if (!PathNormalizer.TryNormalize(profileDir, out var pn, out var perr))
                return PolicyDecision.Deny("Каталог профиля не приведён к безопасному виду: " + perr, "cleanup-profile-location");
            profile = pn;
        }

        var win = ctx.Norm(ctx.WindowsDir);
        var list = new List<string>();
        foreach (var r in c.Roots)
        {
            var baseDir = r.Base switch
            {
                "windir" => win,
                "programdata" => ctx.Norm(ctx.ProgramData),
                "systemdrive" => win[..3],
                "userlocal" => profile + @"\AppData\Local",
                "userroaming" => profile + @"\AppData\Roaming",
                _ => null,
            };
            if (baseDir is null) return PolicyDecision.Deny("Неизвестная база корня", "cleanup-root-outside");
            var full = baseDir.TrimEnd('\\') + "\\" + r.Sub;
            list.Add(full);
        }
        roots = list;
        return PolicyDecision.Allow("cleanup-roots");
    }

    /// <summary>Делит шаблон «…\Profiles\*\cache2» на родителя («…\Profiles») и хвост («\cache2»); false, если «*» нет или он не один.</summary>
    public static bool TrySplitWildcard(string template, out string parent, out string suffix)
    {
        parent = suffix = "";
        const string mid = @"\*\";
        var i = template.IndexOf(mid, StringComparison.Ordinal);
        if (i <= 0 || template.IndexOf('*', i + mid.Length) >= 0 || template.IndexOf('*') != i + 1) return false;
        parent = template[..i];
        suffix = template[(i + 2)..];     // начинается с «\»
        return suffix.Length > 1;
    }

    /// <summary>
    /// Раскрытие подстановки по фактам перечисления родителя: только каталоги, не ссылки, с обычным именем. Каждый результат
    /// всё равно проходит <see cref="CheckRoot"/> (существование, reparse, финальный путь).
    /// </summary>
    public static IReadOnlyList<string> ExpandWildcard(string parent, string suffix, IEnumerable<DirEntryFacts> entries)
    {
        var res = new List<string>();
        foreach (var e in entries)
        {
            if (!e.IsDirectory || e.IsReparse || !IsPlainName(e.Name)) continue;
            res.Add(parent + "\\" + e.Name + suffix);
        }
        return res;
    }

    static bool IsPlainName(string? n)
        => !string.IsNullOrEmpty(n) && n is not ("." or "..")
           && n.IndexOfAny(['\\', '/', ':', '*', '?', '<', '>', '"', '|', '%']) < 0
           && !n.Any(ch => ch < 0x20) && !n.EndsWith('.') && !n.EndsWith(' ');

    // ---------- профиль ----------

    /// <summary>Каталог профиля из ProfileList: не ссылка; владелец клиент, Administrators или SYSTEM; не внутри системных каталогов.</summary>
    public PolicyDecision CheckProfileDir(string sid, string profileDir, DirFacts facts)
    {
        if (sid is null || !SidFormat().IsMatch(sid))
            return PolicyDecision.Deny("SID клиента имеет неверный формат", "cleanup-profile-owner");
        if (!PathNormalizer.TryNormalize(profileDir, out var p, out var err))
            return PolicyDecision.Deny("Каталог профиля не приведён к безопасному виду: " + err, "cleanup-profile-location");
        if (!facts.Exists) return PolicyDecision.Deny("Каталог профиля не существует", "cleanup-root-missing").WithTarget(p);
        if (facts.IsReparse) return PolicyDecision.Deny("Каталог профиля это ссылка", "cleanup-profile-reparse").WithTarget(p);

        var comps = PathNormalizer.Components(p);
        if (comps.Length == 0) return PolicyDecision.Deny("Корень тома не может быть профилем", "cleanup-profile-location").WithTarget(p);
        foreach (var protectedDir in new[] { ctx.WindowsDir, ctx.ProgramFiles, ctx.ProgramFilesX86, ctx.ProgramData })
        {
            var d = ctx.Norm(protectedDir);
            if (PathNormalizer.IsEqualOrUnder(p, d) || PathNormalizer.IsEqualOrAncestor(p, d))
                return PolicyDecision.Deny("Профиль внутри системного каталога или содержит его", "cleanup-profile-location").WithTarget(p);
        }

        var owner = facts.OwnerSid;
        if (string.IsNullOrEmpty(owner)
            || !(owner.Equals(sid, StringComparison.OrdinalIgnoreCase) || owner.Equals(AdministratorsSid, StringComparison.OrdinalIgnoreCase)
                 || owner.Equals(SystemSid, StringComparison.OrdinalIgnoreCase)))
            return PolicyDecision.Deny("Владелец каталога профиля не клиент, не администраторы и не SYSTEM", "cleanup-profile-owner").WithTarget(p);
        return PolicyDecision.Allow("cleanup-profile-ok").WithTarget(p);
    }

    // ---------- корень ----------

    /// <summary>
    /// Корень (уже раскрытый, без «*») существует, не ссылка, финальный путь совпадает с запрошенным (после тома).
    /// Цель решения это финальный путь, его исполнитель передаёт дальше как rootFinal.
    /// </summary>
    public PolicyDecision CheckRoot(string requestedRoot, FileFacts facts)
    {
        if (!PathNormalizer.TryNormalize(requestedRoot, out var req, out var err))
            return PolicyDecision.Deny("Корень не приведён к безопасному виду: " + err, "cleanup-root-outside");
        if (RootOutside(req) is { } why) return PolicyDecision.Deny(why, "cleanup-root-outside").WithTarget(req);

        if (!facts.Exists) return PolicyDecision.Deny("Корня нет, пропуск", "cleanup-root-missing").WithTarget(req);
        if (facts.IsReparse) return PolicyDecision.Deny("Корень это ссылка", "cleanup-root-reparse").WithTarget(req);
        if (!SplitFinal(facts.FinalPath, out var vol, out var rest))
            return PolicyDecision.Deny("Финальный путь корня не распознан", "cleanup-root-moved").WithTarget(req);
        // Если том указан буквой, она должна совпасть; GUID-том проверяет только хвост (букву сопоставить без ввода-вывода нельзя).
        if (vol.Length == 2 && vol[1] == ':' && !vol.Equals(req[..2], StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Корень оказался на другом томе", "cleanup-root-moved").WithTarget(req);
        // Native сообщает том запрошенной буквы (GetVolumeNameForVolumeMountPoint): GUID-том финального пути обязан совпасть с ним.
        // Так подмена тома точкой монтирования или ссылкой на другой диск не пройдёт, хотя хвост пути совпадает.
        // Том не определился (subst, сетевой или виртуальный диск): fail-closed, сверить нечем.
        if (facts.RequestedVolume is not { } want)
            return PolicyDecision.Deny("Том запрошенного диска не определён", "cleanup-root-volume-unknown").WithTarget(req);
        if (!(vol.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && vol.Equals(want.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return PolicyDecision.Deny("Корень оказался на другом томе", "cleanup-root-moved").WithTarget(req);
        if (!rest.Equals(req[2..], StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Финальный путь корня не совпал с ожидаемым (ссылка выше по пути)", "cleanup-root-moved").WithTarget(req);
        return PolicyDecision.Allow("cleanup-root-ok").WithTarget(facts.FinalPath!);
    }

    // null, если запрошенный корень допустим по расположению (повторная защита от испорченного каталога).
    string? RootOutside(string req)
    {
        var comps = PathNormalizer.Components(req);
        if (comps.Length < 2) return "Корень слишком близко к корню тома";
        var win = ctx.Norm(ctx.WindowsDir);
        var pd = ctx.Norm(ctx.ProgramData);
        foreach (var d in new[] { win, pd, ctx.Norm(ctx.ProgramFiles), ctx.Norm(ctx.ProgramFilesX86), ctx.Norm(ctx.UsersDir) })
            if (PathNormalizer.IsEqualOrAncestor(req, d)) return "Корень совпадает с системным каталогом или содержит его";

        if (PathNormalizer.IsEqualOrUnder(req, win))
            return StartsWithAny(comps[PathNormalizer.Components(win).Length..], WindirAllowed) ? null : "Корень внутри Windows вне закрытого списка";
        if (PathNormalizer.IsEqualOrUnder(req, pd))
            return StartsWithAny(comps[PathNormalizer.Components(pd).Length..], ProgramDataAllowed) ? null : "Корень внутри ProgramData вне закрытого списка";
        if (PathNormalizer.IsEqualOrUnder(req, ctx.Norm(ctx.ProgramFiles)) || PathNormalizer.IsEqualOrUnder(req, ctx.Norm(ctx.ProgramFilesX86)))
            return "Корень внутри Program Files";

        // Остальное это только <профиль>\AppData\Local\<что-то>.
        for (var i = 1; i + 2 < comps.Length; i++)
            if (comps[i].Equals("AppData", StringComparison.OrdinalIgnoreCase) && comps[i + 1].Equals("Local", StringComparison.OrdinalIgnoreCase))
                return null;
        return "Корень вне допустимых расположений";
    }

    // ---------- элементы ----------

    /// <summary>Решение по записи перечисления до открытия: ссылка, KeepNames, шаблон, возраст.</summary>
    public PolicyDecision CheckEntry(string rootFinal, DirEntryFacts e, DateTimeOffset now, CleanupCategory c)
    {
        if (string.IsNullOrEmpty(rootFinal) || e is null || c is null)
            return PolicyDecision.Deny("Нет корня или записи", "cleanup-not-under-root");
        if (!IsPlainName(e.Name))
            return PolicyDecision.Deny("Недопустимое имя записи", "cleanup-not-under-root").WithTarget(e.Name ?? "");
        if (e.IsReparse)
            return PolicyDecision.Deny("Ссылка: не следуем и не удаляем", "cleanup-reparse").WithTarget(e.Name);
        if (c.Mechanism != "files")
            return PolicyDecision.Deny("Категория без файловых корней", "cleanup-bad-catalog").WithTarget(e.Name);

        if (c.KeepNames is { } keep && keep.Any(k => k.Equals(e.Name, StringComparison.OrdinalIgnoreCase)))
            return PolicyDecision.Deny("Файл из списка «оставить»", "cleanup-keep-name").WithTarget(e.Name);
        if (c.Patterns is { Count: > 0 } pats && !pats.Any(p => GlobMatch(p, e.Name)))
            return PolicyDecision.Deny("Имя не подходит под шаблоны категории", "cleanup-pattern").WithTarget(e.Name);

        // Возраст: берём более свежую из двух дат; будущая дата это тоже «свежий».
        var stamp = e.Created > e.LastWrite ? e.Created : e.LastWrite;
        if (now - stamp < TimeSpan.FromHours(c.MinAgeHours))
            return PolicyDecision.Deny($"Моложе {c.MinAgeHours} ч", "cleanup-too-fresh").WithTarget(e.Name);
        return PolicyDecision.Allow("cleanup-entry-ok").WithTarget(e.Name);
    }

    /// <summary>
    /// Решение по открытому хэндлу: конечный путь строго под корнем (сам корень удалять нельзя), не ссылка.
    /// Оба пути в одной форме (VOLUME_NAME_GUID | NORMALIZED), как отдаёт Native.
    /// </summary>
    public PolicyDecision CheckOpened(string rootFinal, string entryFinal, bool isReparse)
    {
        if (!SplitFinal(rootFinal, out var rv, out var rr) || !SplitFinal(entryFinal, out var ev, out var er)
            || rr.Length == 0                                   // корень тома
            || !rv.Equals(ev, StringComparison.OrdinalIgnoreCase)
            || er.Length <= rr.Length
            || !er.StartsWith(rr + "\\", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Открытый объект вне корня очистки", "cleanup-not-under-root").WithTarget(entryFinal ?? "");
        if (isReparse)
            return PolicyDecision.Deny("Открытый объект это ссылка", "cleanup-reparse").WithTarget(entryFinal);
        return PolicyDecision.Allow("cleanup-opened-ok").WithTarget(entryFinal);
    }

    // ---------- процессы и корзина ----------

    /// <summary>Категория с SkipIfRunning при запущенном процессе пропускается целиком.</summary>
    public PolicyDecision CheckNotRunning(CleanupCategory c, bool processRunning)
        => c.SkipIfRunning is not null && processRunning
            ? PolicyDecision.Deny($"Запущен {c.SkipIfRunning}: кэш не трогаем", "cleanup-browser-running")
            : PolicyDecision.Allow("cleanup-not-running");

    /// <summary>Корзину очищаем только токеном самого клиента (иначе очистили бы корзину администратора).</summary>
    public PolicyDecision CheckRecycleBin(string tokenSid, string clientSid)
        => !string.IsNullOrEmpty(tokenSid) && !string.IsNullOrEmpty(clientSid) && tokenSid.Equals(clientSid, StringComparison.OrdinalIgnoreCase)
            ? PolicyDecision.Allow("recyclebin-own-user")
            : PolicyDecision.Deny("SID токена агента не совпадает с SID клиента", "recyclebin-foreign-user");

    // ---------- вспомогательное ----------

    // Путь вида «\\?\X:\a\b», «\\?\Volume{guid}\a\b» или «X:\a\b» → (том, «\a\b» без хвостового «\»). Прочие формы отвергаются.
    static bool SplitFinal(string? path, out string volume, out string rest)
    {
        volume = rest = "";
        if (string.IsNullOrEmpty(path) || path.Length > PathNormalizer.MaxLength) return false;
        string body;
        if (path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            var end = path.IndexOf('}');
            if (end < 0) return false;
            var after = path.IndexOf('\\', end);
            volume = after < 0 ? path : path[..after];
            body = after < 0 ? "" : path[after..];
        }
        else
        {
            var p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            if (p.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (p.Length < 2 || !(p[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') || p[1] != ':') return false;
            if (p.Length > 2 && p[2] != '\\') return false;
            volume = p[..2].ToUpperInvariant();
            body = p[2..];
        }
        body = body.TrimEnd('\\');
        if (body.Length > 0)
            foreach (var comp in body[1..].Split('\\'))
                if (comp.Length == 0 || comp is "." or ".." || comp.Contains(':') || comp.Any(ch => ch < 0x20)) return false;
        rest = body;
        return true;
    }

    static bool GlobMatch(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))
                && pattern[p] != '*') { p++; n++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (star >= 0) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
