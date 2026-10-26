using System.Text.RegularExpressions;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Policy;

/// <summary>Идентификатор программы из «Программ и компонентов»: arp:{machine|user}:{x64|x86}:&lt;ключ&gt;. Ключ это имя подраздела Uninstall без пути.</summary>
public static class ArpId
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    public const string UninstallPathX86 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";

    public static bool TryParse(string? id, out string scope, out string arch, out string key)
    {
        scope = arch = key = string.Empty;
        if (string.IsNullOrEmpty(id)) return false;
        var p = id.Split(':', 4);
        if (p.Length != 4 || p[0] != "arp") return false;
        if (p[1] is not ("machine" or "user")) return false;
        if (p[2] is not ("x64" or "x86")) return false;
        if (p[3].Length is 0 or > 255 || p[3].Any(c => c < 0x20 || c == '\\' || c == 0x7F)) return false;
        scope = p[1];
        arch = p[2];
        key = p[3];
        return true;
    }

    public static string Make(string arch, string key) => $"arp:machine:{arch}:{key}";

    static readonly Regex ProductCodeRegex =
        new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant);

    /// <summary>Код продукта Windows Installer: GUID в фигурных скобках.</summary>
    public static bool IsProductCode(string? s) => s is not null && ProductCodeRegex.IsMatch(s);

    /// <summary>Путь подраздела в HKLM (Registry64): для x86 это WOW6432Node.</summary>
    public static string KeyPath(string arch, string key) => (arch == "x86" ? UninstallPathX86 : UninstallPath) + key;
}

/// <summary>Маркеры общих библиотек и сред выполнения: их не удаляют вместе с «обычными» программами.</summary>
public static class ProgramMarkers
{
    public static readonly string[] Runtime =
    [
        "Visual C++", "Redistributable", ".NET", " Runtime", "WebView2", "Java(TM)", "Java ", "DirectX", "OpenAL", "Windows SDK",
    ];

    public static bool IsRuntime(string name) => Runtime.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Факты о записи Uninstall, собранные агентом (или App без прав) из реестра. Команды берутся только из HKLM; HKCU сюда не попадает.
/// </summary>
public sealed record PackageFacts(
    string DisplayName, string? Publisher, bool SystemComponent, string? ParentKeyName, string? ReleaseType,
    bool WindowsInstaller, string? UninstallString, string? QuietUninstallString, bool NoRemove = false);

/// <summary>Как именно удалять: msi (по коду продукта) или exe (файл и готовые аргументы, запуск без shell).</summary>
public sealed record UninstallCommand(string Kind, string? ProductCode, string? ExePath, string? Arguments);

/// <summary>
/// Итог классификации программы. Modifiable: можно удалять (Command задан). Forbidden и ReadOnly: никогда; ExpertOnly=true у общих сред
/// выполнения: в протоколе 0.1 это тоже отказ (подтверждения эксперта нет).
/// </summary>
public sealed record PackageClassification(ChangeClass Class, string Reason, string Rule, bool ExpertOnly = false, UninstallCommand? Command = null)
{
    public bool IsModifiable => Class == ChangeClass.Modifiable && Command is not null;
}

/// <summary>
/// Политика удаления программ (docs/C1-DESIGN.md). Чистая: файловые факты приходят через <see cref="IFileSecurity"/>.
/// Порядок правил: область не HKLM → просмотр; системный компонент, зависимая запись, обновление → просмотр;
/// Edge, WebView2, .NET Framework, Магазин, защитные каталоги → запрет; общие среды выполнения → эксперт (= отказ);
/// MSI по коду продукта; иначе разбор команды без shell, файл только под Program Files за ACL-барьером, тихий режим только из
/// QuietUninstallString либо распознанные Inno и NSIS. Всё неопознанное остаётся просмотром.
/// </summary>
public sealed class PackagePolicy
{
    static readonly Regex InnoName = new(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly string[] ForbiddenNameMarkers =
        ["Microsoft Edge", "WebView2", ".NET Framework", "Microsoft Store", "Windows Defender", "Windows Security",
         "Microsoft Update Health Tools", "Intune Management Extension", "Configuration Manager Client", "Microsoft GameInput"];

    static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "mshta.exe", "msiexec.exe",
        "regsvr32.exe", "wmic.exe", "forfiles.exe", "bash.exe", "wsl.exe",
        // интерпретаторы и лаунчеры, которые выполняют произвольный код из аргументов
        "python.exe", "pythonw.exe", "py.exe", "node.exe", "java.exe", "javaw.exe", "perl.exe", "ruby.exe", "git-bash.exe",
        "sh.exe", "dotnet.exe", "conhost.exe", "explorer.exe", "start.exe", "schtasks.exe", "reg.exe", "sc.exe", "net.exe",
        "curl.exe", "certutil.exe", "bitsadmin.exe", "installutil.exe", "regasm.exe", "msbuild.exe",
    };

    static readonly string[] LauncherSchemes = ["steam://", "com.epicgames.launcher://", "uplay://", "goggalaxy://", "origin2://", "battlenet://"];

    const string InnoArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
    const string NsisArguments = "/S";

    readonly CommandResolver _resolver;
    readonly IFileSecurity _files;
    readonly string _windowsDir;
    readonly string[] _roots;
    readonly string[] _systemOwnedRoots;

    public PackagePolicy(PolicyContext ctx, IFileSecurity files)
    {
        _resolver = new CommandResolver(ctx);
        _files = files;
        _windowsDir = ctx.Norm(ctx.WindowsDir);
        var pf = ctx.Norm(ctx.ProgramFiles);
        var pf86 = ctx.Norm(ctx.ProgramFilesX86);
        _roots = [pf, pf86];
        _systemOwnedRoots = SystemRoots.Build(pf, pf86, ctx.Norm(ctx.ProgramData));
    }

    static PackageClassification View(string reason, string rule) => new(ChangeClass.ReadOnly, reason, rule);
    static PackageClassification Forbid(string reason, string rule) => new(ChangeClass.Forbidden, reason, rule);

    public PackageClassification Classify(string id, PackageFacts f)
    {
        if (!ArpId.TryParse(id, out var scope, out _, out var key))
            return View("Некорректный идентификатор программы", "arp-id-invalid");
        if (scope != "machine")
            return View("Программа установлена для текущего пользователя: удаляется её собственным деинсталлятором", "hkcu-scope");

        if (f.SystemComponent)
            return View("Системный компонент", "system-component");
        if (f.NoRemove)
            return View("Производитель запретил удаление этой программы", "no-remove");
        if (!string.IsNullOrWhiteSpace(f.ParentKeyName))
            return View("Часть другой программы", "has-parent");
        if (f.ReleaseType is { } rt && (rt.Equals("Update", StringComparison.OrdinalIgnoreCase) || rt.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)
            || rt.Equals("Security Update", StringComparison.OrdinalIgnoreCase) || rt.Equals("Service Pack", StringComparison.OrdinalIgnoreCase)))
            return View("Обновление Windows или программы", "is-update");

        foreach (var m in ForbiddenNameMarkers)
            if (f.DisplayName.Contains(m, StringComparison.OrdinalIgnoreCase))
                return Forbid("Компонент Windows или защиты", "protected-component");

        if (ProgramMarkers.IsRuntime(f.DisplayName))
            return new PackageClassification(ChangeClass.ReadOnly, "Общая среда выполнения: от неё зависят другие программы", "runtime-shared", ExpertOnly: true);

        if (f.WindowsInstaller && ArpId.IsProductCode(key))
            return new PackageClassification(ChangeClass.Modifiable, "Пакет Windows Installer", "msi",
                Command: new UninstallCommand("msi", key, null, null));

        return ClassifyExe(f);
    }

    PackageClassification ClassifyExe(PackageFacts f)
    {
        var quiet = !string.IsNullOrWhiteSpace(f.QuietUninstallString);
        var command = quiet ? f.QuietUninstallString : f.UninstallString;
        if (string.IsNullOrWhiteSpace(command))
            return View("У программы нет команды удаления", "exe-no-quiet");

        var trimmed = command.TrimStart('"', ' ');
        foreach (var scheme in LauncherSchemes)
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return View("Программу удаляет её лаунчер", "managed-by-launcher");

        if (!_resolver.TryResolve(command, out var exe, out var args, out _))
            return View("Не удалось однозначно разобрать команду удаления", "uninstall-unparsed");

        var name = exe[(exe.LastIndexOf('\\') + 1)..];
        if (Interpreters.Contains(name))
            return View("Удаление запускается через интерпретатор команд", "managed-by-launcher");

        if (PathNormalizer.IsEqualOrUnder(exe, _windowsDir))
            return Forbid("Деинсталлятор лежит в каталоге Windows", "protected-component");
        foreach (var root in _systemOwnedRoots)
            if (PathNormalizer.IsEqualOrUnder(exe, root))
                return Forbid("Деинсталлятор в системной зоне", "protected-component");
        if (!_roots.Any(r => PathNormalizer.IsEqualOrUnder(exe, r)))
            return View("Деинсталлятор вне Program Files", "exe-writable-dir");

        var file = _files.Inspect(exe);
        if (!file.Exists) return View("Файла деинсталлятора нет", "exe-missing");
        if (file.FinalPath is null || !PathNormalizer.TryNormalize(file.FinalPath, out var final, out _))
            return View("Не удалось определить реальный путь деинсталлятора", "exe-writable-dir");
        if (!_roots.Any(r => PathNormalizer.IsEqualOrUnder(final, r)))
            return View("Деинсталлятор реально лежит вне Program Files", "exe-writable-dir");
        foreach (var root in _systemOwnedRoots)
            if (PathNormalizer.IsEqualOrUnder(final, root))
                return Forbid("Деинсталлятор в системной зоне", "protected-component");
        if (!Trusted(file)) return View("Файл деинсталлятора доступен на запись обычным пользователям", "exe-writable-dir");

        // Каждый каталог от файла вверх до корня Program Files: иначе пользователь, владеющий верхним каталогом, подменит
        // вложенный (переименует его или подставит ссылку) между проверкой и запуском.
        var pfRoot = _roots.First(r => PathNormalizer.IsEqualOrUnder(final, r)).TrimEnd('\\');
        var dirPath = final[..final.LastIndexOf('\\')];
        for (var depth = 0; ; depth++)
        {
            if (depth > 64) return View("Слишком глубокий путь деинсталлятора", "exe-writable-dir");
            var dir = _files.Inspect(dirPath);
            if (!dir.Exists || !Trusted(dir)) return View("Каталог деинсталлятора доступен на запись обычным пользователям", "exe-writable-dir");
            var up = dirPath.LastIndexOf('\\');
            if (up < 0) break;
            dirPath = dirPath[..up];
            if (dirPath.Length <= pfRoot.Length || !PathNormalizer.IsEqualOrUnder(dirPath, pfRoot)) break; // сам корень Program Files не проверяется
        }

        string finalName = final[(final.LastIndexOf('\\') + 1)..];
        if (Interpreters.Contains(finalName))
            return View("Удаление запускается через интерпретатор команд", "managed-by-launcher");

        if (quiet)
            return Run(final, args, "Деинсталлятор с тихим режимом");
        // Тихий ключ подставляем только к команде без своих аргументов: «setup.exe /uninstall» не должен превратиться в запуск установщика.
        if (!string.IsNullOrWhiteSpace(args))
            return View("У деинсталлятора нет известного тихого режима", "exe-no-quiet");
        if (InnoName.IsMatch(finalName) && _files.Inspect(final[..^4] + ".dat").Exists)
            return Run(final, InnoArguments, "Деинсталлятор Inno Setup");
        if (_files.HasNsisMarker(final))
            return Run(final, NsisArguments, "Деинсталлятор NSIS");
        return View("У деинсталлятора нет известного тихого режима", "exe-no-quiet");
    }

    static PackageClassification Run(string exe, string args, string reason)
        => new(ChangeClass.Modifiable, reason, "exe", Command: new UninstallCommand("exe", null, exe, args));

    // Владелец и права файла или каталога проверяются теми же правилами, что каталог журнала: запись только у администраторов, SYSTEM, TrustedInstaller.
    static bool Trusted(FileFacts f)
        => JournalAclPolicy.Evaluate(new DirFacts(f.Exists, f.IsReparse, f.OwnerSid, f.Aces)).IsTrusted;

    /// <summary>
    /// Решение для шлюза. Allow только для Modifiable с командой; общая среда выполнения это ExpertOnly (шлюз трактует как отказ);
    /// остальное Deny. Target это идентификатор в нижнем регистре.
    /// </summary>
    public PolicyDecision CheckUninstall(string id, PackageFacts f)
    {
        var c = Classify(id, f);
        var target = id.ToLowerInvariant();
        if (c.IsModifiable) return PolicyDecision.Allow(c.Rule).WithTarget(target);
        if (c.ExpertOnly) return PolicyDecision.Expert(c.Reason, c.Rule).WithTarget(target);
        return PolicyDecision.Deny(c.Reason, c.Rule).WithTarget(target);
    }
}
