using System.Text.RegularExpressions;

namespace Chistovik.Core.Policy;

/// <summary>
/// Что можно переносить в карантин и что нельзя. Проверяет текстовый путь после нормализации.
/// Это первый из двух барьеров: второй (по хэндлу, после раскрытия ссылок и точек соединения) стоит в слое Native.
/// Список закрытых зон зашит в сборку и не читается из файлов.
/// </summary>
public sealed partial class PathPolicy
{
    public const string QuarantineDirName = "Chistovik.Quarantine";

    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    // На любом томе: всё поддерево закрыто.
    static readonly HashSet<string> RootSubtreeDeny = new(Cmp)
    {
        "Windows", "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI",
        "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset", "Config.Msi",
        "Documents and Settings", "WindowsApps", "WpSystem",
    };

    // На любом томе: сам объект закрыт, содержимое решают другие правила.
    static readonly HashSet<string> RootExactDeny = new(Cmp)
    {
        "Users", "Program Files", "Program Files (x86)", "ProgramData", "PerfLogs",
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK",
    };

    // Прямые потомки Program Files / Program Files (x86), которые принадлежат системе.
    static readonly HashSet<string> ProgramFilesProtected = new(Cmp)
    {
        "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Security", "Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Sidebar", "Windows Multimedia Platform", "WindowsPowerShell",
        "Internet Explorer", "Microsoft Update Health Tools", "Reference Assemblies", "dotnet", "MSBuild",
        "Microsoft.NET", "Uninstall Information", "Microsoft Edge", "Microsoft EdgeWebView",
        "Hyper-V", "RUXIM", "PackageManagement",
    };

    static readonly HashSet<string> CommonFilesProtected = new(Cmp)
    {
        "Microsoft Shared", "System", "Services", "SpeechEngines",
    };

    static readonly HashSet<string> UserDataFolders = new(Cmp)
    {
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive",
        "Favorites", "Contacts", "Links", "Saved Games", "Searches",
    };

    // Точки соединения в корне профиля (старые имена папок): всё поддерево закрыто.
    static readonly HashSet<string> UserJunctions = new(Cmp)
    {
        "Application Data", "Local Settings", "My Documents", "Start Menu", "Templates",
        "NetHood", "PrintHood", "SendTo", "Recent", "Cookies",
    };

    static readonly HashSet<string> UserTemplateProfiles = new(Cmp)
    {
        "Default", "Default User", "All Users", "defaultuser0",
    };

    static readonly HashSet<string> AppDataMicrosoftProtected = new(Cmp)
    {
        "Windows", "WindowsApps", "Credentials", "Crypto", "Protect", "SystemCertificates", "Vault", "Windows Defender",
    };

    // Системные папки внутри Start Menu\Programs: ярлыки остатков программ там не лежат.
    static readonly HashSet<string> StartMenuSystemFolders = new(Cmp)
    {
        "Startup", "Windows PowerShell", "Windows Accessories", "Windows System", "Windows Tools",
        "Windows Administrative Tools", "Administrative Tools", "Accessories", "System Tools",
        "Maintenance", "Accessibility", "Windows Ease of Access",
    };

    static readonly string[] StartMenuProgramsTail = ["Microsoft", "Windows", "Start Menu", "Programs"];
    static readonly string[] UserStartMenuProgramsTail = ["AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"];

    readonly record struct Zone(string Path, string Rule, string Reason, bool ExpertOnly = false);

    // Зоны ProgramData (относительно каталога): действуют на ProgramData любого тома.
    static readonly Zone[] ProgramDataRel =
    [
        new(@"\Microsoft\Windows", "zone-pd-windows", "Данные компонентов Windows"),
        new(@"\Microsoft\Crypto", "zone-pd-crypto", "Ключи и сертификаты системы"),
        new(@"\Microsoft\Vault", "zone-pd-vault", "Хранилище учётных данных"),
        new(@"\Microsoft\Network", "zone-pd-network", "Сетевые данные системы"),
        new(@"\Microsoft\Windows Defender", "zone-pd-defender", "Защитник Windows"),
        new(@"\Microsoft\Windows Defender Advanced Threat Protection", "zone-pd-defender", "Защитник Windows"),
        new(@"\Microsoft\Windows Security Health", "zone-pd-defender", "Центр безопасности Windows"),
        new(@"\Microsoft\DeviceSync", "zone-pd-windows", "Данные компонентов Windows"),
        new(@"\Microsoft\User Account Pictures", "zone-pd-windows", "Данные компонентов Windows"),
        new(@"\Chistovik", "zone-self", "Данные самого Чистовика"),
        new(@"\Package Cache", "zone-package-cache", "Кэш установщиков (нужен для ремонта и удаления программ)", ExpertOnly: true),
        new(@"\Application Data", "zone-pd-junction", "Системная точка соединения"),
        new(@"\Desktop", "zone-pd-junction", "Системная точка соединения"),
        new(@"\Documents", "zone-pd-junction", "Системная точка соединения"),
        new(@"\Start Menu", "zone-pd-junction", "Системная точка соединения"),
        new(@"\Templates", "zone-pd-junction", "Системная точка соединения"),
        new(@"\Favorites", "zone-pd-junction", "Системная точка соединения"),
    ];

    readonly PolicyContext _ctx;
    readonly List<Zone> _zones;
    readonly string _usersDir;
    readonly string _pd;

    public PathPolicy(PolicyContext ctx)
    {
        _ctx = ctx;
        _usersDir = ctx.Norm(ctx.UsersDir);
        var win = ctx.Norm(ctx.WindowsDir);
        var pd = ctx.Norm(ctx.ProgramData);
        var pf = ctx.Norm(ctx.ProgramFiles);
        _pd = pd;
        _zones =
        [
            new(win + @"\WinSxS", "zone-winsxs", "Хранилище компонентов Windows"),
            new(win + @"\System32\DriverStore", "zone-driverstore", "Хранилище драйверов"),
            new(win + @"\Installer", "zone-installer", "Кэш установщика Windows (нужен для удаления и ремонта программ)"),
            new(pf + @"\WindowsApps", "zone-windowsapps", "Приложения Microsoft Store"),
            new(win, "zone-windows", "Каталог Windows"),
            .. ProgramDataRel.Select(z => z with { Path = pd + z.Path }),
        ];
    }

    // Зоны для данного пути: зоны машины и зоны ProgramData на томе самого пути.
    IEnumerable<Zone> ZonesFor(string p)
    {
        foreach (var z in _zones) yield return z;
        var anyPd = p[..2] + @"\ProgramData";
        if (anyPd.Equals(_pd, StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var z in ProgramDataRel) yield return z with { Path = anyPd + z.Path };
    }

    // Компоненты пути ниже корня: корень из контекста либо одноимённая папка в корне любого тома.
    static string[]? RelativeTo(string p, string[] c, string ctxRoot, string name)
    {
        if (PathNormalizer.IsEqualOrUnder(p, ctxRoot))
            return c[PathNormalizer.Components(ctxRoot).Length..];
        if (c.Length >= 1 && c[0].Equals(name, StringComparison.OrdinalIgnoreCase)) return c[1..];
        return null;
    }

    static bool TailStartsWith(string[] tail, string[] prefix)
    {
        if (tail.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (!tail[i].Equals(prefix[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Можно ли перенести объект в карантин.</summary>
    public PolicyDecision CheckQuarantineMove(string? path)
    {
        if (!PathNormalizer.TryNormalize(path, out var p, out var err))
            return PolicyDecision.Deny("Путь не удалось привести к безопасному виду: " + err, "bad-path");
        return CheckNormalized(p).WithTarget(p);
    }

    PolicyDecision CheckNormalized(string p)
    {
        var c = PathNormalizer.Components(p);
        if (c.Length == 0) return PolicyDecision.Deny("Корень тома нельзя трогать", "drive-root");
        if (c[0].Equals(QuarantineDirName, StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Карантин нельзя помещать в карантин", "quarantine-self");

        if (RootSubtreeDeny.Contains(c[0]))
            return PolicyDecision.Deny("Системная область тома", "root-system-subtree");
        if (c.Length == 1 && RootExactDeny.Contains(c[0]))
            return PolicyDecision.Deny("Системный объект в корне тома", "root-system-item");

        // Program Files на любом томе.
        if (c.Length >= 2 && (c[0].Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                              || c[0].Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)))
        {
            if (ProgramFilesProtected.Contains(c[1]))
                return PolicyDecision.Deny("Компонент Windows в Program Files", "program-files-system");
            if (c[1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                && (c.Length == 2 || c[2].StartsWith("Edge", StringComparison.OrdinalIgnoreCase)))
                return PolicyDecision.Deny("Microsoft Edge и его компоненты", "program-files-edge");
            if (c[1].Equals("Common Files", StringComparison.OrdinalIgnoreCase))
            {
                if (c.Length == 2) return PolicyDecision.Deny("Общая папка Common Files", "program-files-common");
                if (CommonFilesProtected.Contains(c[2]))
                    return PolicyDecision.Deny("Общие компоненты Windows", "program-files-common");
            }
        }

        // Ярлыки остатков в меню «Пуск»: явное исключение до общих запретов (сама Programs и Startup закрыты).
        if (CheckStartMenuPrograms(p, c) is { } sm) return sm;

        // Закрытые зоны машины: сам объект и всё внутри.
        var zones = ZonesFor(p).ToList();
        foreach (var z in zones)
        {
            if (!PathNormalizer.IsEqualOrUnder(p, z.Path)) continue;
            if (z.ExpertOnly)
            {
                if (p.Equals(z.Path, StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny(z.Reason, z.Rule);
                return PolicyDecision.Expert(z.Reason, z.Rule);
            }
            return PolicyDecision.Deny(z.Reason, z.Rule);
        }

        // Предки закрытых зон (C:\ProgramData\Microsoft и подобные): удалить предка значит удалить зону.
        foreach (var z in zones)
        {
            if (z.ExpertOnly) continue;
            if (PathNormalizer.IsEqualOrAncestor(p, z.Path))
                return PolicyDecision.Deny("Внутри этого каталога есть закрытая зона", "ancestor-of-" + z.Rule);
        }

        // Профили пользователей на любом томе (D:\Users\x тоже профиль).
        if (RelativeTo(p, c, _usersDir, "Users") is { } rel && CheckUserProfile(rel) is { } ud) return ud;

        if (c.Length == 1)
            return PolicyDecision.Expert("Объект в корне тома: только по подтверждению эксперта", "root-level-item");

        return PolicyDecision.Allow();
    }

    // Объект ниже ...\Start Menu\Programs в ProgramData или в Roaming пользователя.
    PolicyDecision? CheckStartMenuPrograms(string p, string[] c)
    {
        string[]? tail = null;
        if (RelativeTo(p, c, _pd, "ProgramData") is { } pdRel && TailStartsWith(pdRel, StartMenuProgramsTail))
            tail = pdRel[StartMenuProgramsTail.Length..];
        else if (RelativeTo(p, c, _usersDir, "Users") is { Length: > 0 } ur
                 && !UserTemplateProfiles.Contains(ur[0])
                 && TailStartsWith(ur[1..], UserStartMenuProgramsTail))
            tail = ur[(1 + UserStartMenuProgramsTail.Length)..];

        if (tail is null || tail.Length == 0) return null;      // сама Programs остаётся под общими запретами
        if (StartMenuSystemFolders.Contains(tail[0]))
            return PolicyDecision.Deny("Системная папка меню «Пуск» (в том числе автозагрузка)", "start-menu-system");
        return PolicyDecision.Allow("start-menu-shortcut");
    }

    // rel: компоненты ниже каталога профилей (rel[0] = имя профиля).
    static PolicyDecision? CheckUserProfile(string[] rel)
    {
        if (rel.Length == 0) return PolicyDecision.Deny("Каталог профилей", "users-root");

        var user = rel[0];
        if (UserTemplateProfiles.Contains(user))
            return PolicyDecision.Deny("Шаблонный профиль Windows", "user-template");
        if (rel.Length == 1)
            return PolicyDecision.Deny("Корень профиля пользователя", "user-profile-root");

        var top = rel[1];
        if (top.StartsWith("NTUSER.DAT", StringComparison.OrdinalIgnoreCase)
            || top.StartsWith("ntuser.", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Куст реестра пользователя", "user-hive");

        if (UserJunctions.Contains(top))
            return PolicyDecision.Deny("Системная точка соединения в профиле", "user-junction");

        if (top.Equals("AppData", StringComparison.OrdinalIgnoreCase))
        {
            if (rel.Length <= 3) return PolicyDecision.Deny("Структура AppData", "appdata-structure");
            var area = rel[2];
            if (!area.Equals("Local", StringComparison.OrdinalIgnoreCase)
                && !area.Equals("Roaming", StringComparison.OrdinalIgnoreCase)
                && !area.Equals("LocalLow", StringComparison.OrdinalIgnoreCase))
                return PolicyDecision.Deny("Неизвестный раздел AppData", "appdata-structure");

            if (rel[3].Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                if (rel.Length == 4) return PolicyDecision.Deny("Папка Microsoft в AppData содержит системные данные", "appdata-microsoft");
                if (AppDataMicrosoftProtected.Contains(rel[4]))
                    return PolicyDecision.Deny("Системные данные Windows в AppData", "appdata-microsoft");
            }
            if (rel[3].Equals("Packages", StringComparison.OrdinalIgnoreCase))
                return rel.Length == 4
                    ? PolicyDecision.Deny("Данные пакетов Store", "appdata-packages")
                    : PolicyDecision.Expert("Данные пакета Store: удаляется вместе с пакетом", "appdata-packages");
            if (rel[3].Equals("Temp", StringComparison.OrdinalIgnoreCase) && rel.Length == 4)
                return PolicyDecision.Deny("Саму папку Temp не удаляем", "appdata-temp");
            if (rel[3].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
                || rel[3].Equals("Programs", StringComparison.OrdinalIgnoreCase) && rel.Length == 4)
                return PolicyDecision.Deny("Системная папка пользователя", "appdata-structure");
            return null;
        }

        if (UserDataFolders.Contains(top))
            return rel.Length == 2
                ? PolicyDecision.Deny("Личная папка пользователя", "user-data-folder")
                : PolicyDecision.Expert("Личные файлы пользователя", "user-data");

        return null;
    }

    /// <summary>Можно ли окончательно стереть объект: только внутри папки операции в карантине.</summary>
    public PolicyDecision CheckPurgeTarget(string? path)
    {
        if (!PathNormalizer.TryNormalize(path, out var p, out var err))
            return PolicyDecision.Deny("Путь не удалось привести к безопасному виду: " + err, "bad-path");

        var c = PathNormalizer.Components(p);
        if (c.Length < 2 || !c[0].Equals(QuarantineDirName, StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Стирать можно только внутри карантина", "purge-outside-quarantine").WithTarget(p);
        if (!OpId().IsMatch(c[1]))
            return PolicyDecision.Deny("Имя операции в карантине неверно", "purge-bad-opid").WithTarget(p);
        return PolicyDecision.Allow("purge-in-quarantine").WithTarget(p);
    }

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z\-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OpId();
}
