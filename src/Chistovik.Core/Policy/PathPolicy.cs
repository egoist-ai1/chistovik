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
    };

    // На любом томе: сам объект закрыт, содержимое решают другие правила.
    static readonly HashSet<string> RootExactDeny = new(Cmp)
    {
        "Users", "Program Files", "Program Files (x86)", "ProgramData", "Documents and Settings", "PerfLogs",
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

    static readonly HashSet<string> UserTemplateProfiles = new(Cmp)
    {
        "Default", "Default User", "All Users", "defaultuser0",
    };

    static readonly HashSet<string> AppDataMicrosoftProtected = new(Cmp)
    {
        "Windows", "WindowsApps", "Credentials", "Crypto", "Protect", "SystemCertificates", "Vault", "Windows Defender",
    };

    readonly record struct Zone(string Path, string Rule, string Reason, bool ExpertOnly = false);

    readonly PolicyContext _ctx;
    readonly List<Zone> _zones;
    readonly string _usersDir;

    public PathPolicy(PolicyContext ctx)
    {
        _ctx = ctx;
        _usersDir = ctx.Norm(ctx.UsersDir);
        var win = ctx.Norm(ctx.WindowsDir);
        var pd = ctx.Norm(ctx.ProgramData);
        var pf = ctx.Norm(ctx.ProgramFiles);
        _zones =
        [
            new(win + @"\WinSxS", "zone-winsxs", "Хранилище компонентов Windows"),
            new(win + @"\System32\DriverStore", "zone-driverstore", "Хранилище драйверов"),
            new(win + @"\Installer", "zone-installer", "Кэш установщика Windows (нужен для удаления и ремонта программ)"),
            new(pf + @"\WindowsApps", "zone-windowsapps", "Приложения Microsoft Store"),
            new(win, "zone-windows", "Каталог Windows"),
            new(pd + @"\Microsoft\Windows", "zone-pd-windows", "Данные компонентов Windows"),
            new(pd + @"\Microsoft\Crypto", "zone-pd-crypto", "Ключи и сертификаты системы"),
            new(pd + @"\Microsoft\Vault", "zone-pd-vault", "Хранилище учётных данных"),
            new(pd + @"\Microsoft\Network", "zone-pd-network", "Сетевые данные системы"),
            new(pd + @"\Microsoft\Windows Defender", "zone-pd-defender", "Защитник Windows"),
            new(pd + @"\Microsoft\Windows Defender Advanced Threat Protection", "zone-pd-defender", "Защитник Windows"),
            new(pd + @"\Microsoft\Windows Security Health", "zone-pd-defender", "Центр безопасности Windows"),
            new(pd + @"\Microsoft\DeviceSync", "zone-pd-windows", "Данные компонентов Windows"),
            new(pd + @"\Microsoft\User Account Pictures", "zone-pd-windows", "Данные компонентов Windows"),
            new(pd + @"\Chistovik", "zone-self", "Данные самого Чистовика"),
            new(pd + @"\Package Cache", "zone-package-cache", "Кэш установщиков (нужен для ремонта и удаления программ)", ExpertOnly: true),
        ];
    }

    /// <summary>Можно ли перенести объект в карантин.</summary>
    public PolicyDecision CheckQuarantineMove(string? path)
    {
        if (!PathNormalizer.TryNormalize(path, out var p, out var err))
            return PolicyDecision.Deny("Путь не удалось привести к безопасному виду: " + err, "bad-path");

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
            if (c[1].Equals("Common Files", StringComparison.OrdinalIgnoreCase))
            {
                if (c.Length == 2) return PolicyDecision.Deny("Общая папка Common Files", "program-files-common");
                if (CommonFilesProtected.Contains(c[2]))
                    return PolicyDecision.Deny("Общие компоненты Windows", "program-files-common");
            }
        }

        // Закрытые зоны машины: сам объект и всё внутри.
        foreach (var z in _zones)
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
        foreach (var z in _zones)
        {
            if (z.ExpertOnly) continue;
            if (PathNormalizer.IsEqualOrAncestor(p, z.Path))
                return PolicyDecision.Deny("Внутри этого каталога есть закрытая зона", "ancestor-of-" + z.Rule);
        }

        var userDecision = CheckUserProfile(p);
        if (userDecision is { } ud) return ud;

        if (c.Length == 1)
            return PolicyDecision.Expert("Объект в корне тома: только по подтверждению эксперта", "root-level-item");

        return PolicyDecision.Allow();
    }

    PolicyDecision? CheckUserProfile(string p)
    {
        if (!PathNormalizer.IsEqualOrUnder(p, _usersDir)) return null;

        var rel = p.Length > _usersDir.Length ? p[(_usersDir.Length + 1)..].Split('\\') : [];
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
            return PolicyDecision.Deny("Стирать можно только внутри карантина", "purge-outside-quarantine");
        if (!OpId().IsMatch(c[1]))
            return PolicyDecision.Deny("Имя операции в карантине неверно", "purge-bad-opid");
        return PolicyDecision.Allow("purge-in-quarantine");
    }

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpId();
}
