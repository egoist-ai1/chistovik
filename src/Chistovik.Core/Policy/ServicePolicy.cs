using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Chistovik.Core.Policy;

public enum ServiceType { KernelDriver, FileSystemDriver, OwnProcess, ShareProcess, Other }

public enum StartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>Класс службы: Forbidden и ReadOnly не меняются никогда, Modifiable можно менять с откатом.</summary>
public enum ServiceClass { Forbidden, ReadOnly, Modifiable }

/// <summary>
/// Признаки службы, собранные слоем Native. Классификатор не доверяет имени: решают признаки.
/// ImagePath — сырая строка из реестра; NativeImagePath — путь бинарника, уже раскрытый слоем Native
/// (по конечному пути хэндла); если он задан, проверяется наравне с сырым и ничего не ослабляет.
/// LaunchProtected — ServiceLaunchProtected из QueryServiceConfig2 (PPL); RegisteredSecurityProduct — служба
/// зарегистрирована как антивирус, брандмауэр или антишпион в Центре безопасности.
/// InstallLocation — каталог установки из записи Uninstall; без него владельцем-программой служба не считается.
/// </summary>
public sealed record ServiceFacts(
    string Name,
    ServiceType Type,
    string? ImagePath,
    bool SignatureValid,
    bool MicrosoftSigned,
    bool LaunchProtected,
    int DependentCount,
    bool OwnedByInstalledProgram,
    StartMode CurrentStart,
    bool RegisteredSecurityProduct = false,
    string? InstallLocation = null,
    string? NativeImagePath = null,
    int TriggerCount = 0);

/// <summary>
/// Классификатор служб (раздел 2.1 плана). Единственное правило: «неизвестное» никогда не получает класс Modifiable.
/// </summary>
public sealed partial class ServicePolicy
{
    static readonly HashSet<string> HardList = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "mpssvc", "RpcSs", "RpcEptMapper", "DcomLaunch", "Dhcp", "Dnscache", "EventLog",
        "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc",
        "TrustedInstaller", "Schedule", "LSM", "Winmgmt", "BFE", "CryptSvc", "gpsvc", "ProfSvc", "SamSs",
        "PlugPlay", "Power", "UserManager", "nsi", "netprofm", "NlaSvc", "LanmanWorkstation", "LanmanServer",
        "KeyIso", "VaultSvc", "SystemEventsBroker", "TimeBrokerSvc", "BrokerInfrastructure", "StateRepository",
        "Wcmsvc", "WinHttpAutoProxySvc", "wuauserv", "BITS", "UsoSvc", "WaaSMedicSvc", "AppIDSvc", "SgrmBroker",
        "MsSecFlt", "MsSecCore", "ksecdd", "ksecpkg", "CI", "FltMgr", "Ntfs", "Tcpip", "volmgr", "disk", "partmgr",
        // Шаблоны пользовательских служб (в системе имя вида CDPUserSvc_1a2b3): имя без суффикса и с суффиксом _<4-8 hex> запрещены.
        "CDPUserSvc", "OneSyncSvc", "PimIndexMaintenanceSvc", "UnistoreSvc", "UserDataSvc", "WpnUserService", "cbdhsvc",
        "BcastDVRUserService", "CaptureService", "ConsentUxUserSvc", "DevicesFlowUserSvc", "MessagingService",
        "PrintWorkflowUserSvc", "NPSMSvc", "webthreatdefusersvc",
    };

    /// <summary>
    /// Именованное исключение владельца (26.10.2026): службы каталога Чистовика, которые можно отключать, хотя они
    /// живут в svchost или в каталоге Windows. Точные имена; суффиксы и пробелы не совпадают. Остальные запреты
    /// (hardlist, PPL, продукт безопасности, драйвер) выше и не ослабляются.
    /// </summary>
    public static readonly FrozenSet<string> CarveOut = new[]
    {
        "DiagTrack", "dmwappushservice", "RetailDemo", "WMPNetworkSvc", "RemoteRegistry", "Fax", "WerSvc", "MapsBroker", "lfsvc", "wisvc", "SCardSvr",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    readonly string _windowsDir;
    readonly string[] _systemOwnedRoots;
    readonly string[] _commonRoots;
    readonly string[] _programFilesRoots;

    public ServicePolicy(PolicyContext ctx)
    {
        _windowsDir = ctx.Norm(ctx.WindowsDir);
        var pf = ctx.Norm(ctx.ProgramFiles);
        var pf86 = ctx.Norm(ctx.ProgramFilesX86);
        var pd = ctx.Norm(ctx.ProgramData);
        _commonRoots = [pf, pf86, pd, ctx.Norm(ctx.UsersDir)];
        _programFilesRoots = [pf, pf86];
        _systemOwnedRoots =
        [
            pf + @"\Windows Defender", pf + @"\Windows Defender Advanced Threat Protection", pf + @"\Windows Security",
            pf + @"\WindowsApps", pf + @"\Common Files\Microsoft Shared", pf86 + @"\Common Files\Microsoft Shared",
            pd + @"\Microsoft\Windows Defender", pd + @"\Microsoft\Windows Defender Advanced Threat Protection",
        ];
    }

    // \z, а не $: «$» совпадает и перед хвостовым «\n», и имя «Foo\n» прошло бы проверку.
    [GeneratedRegex(@"^[A-Za-z0-9_.\-$@ ]{1,256}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    // Здесь «$» оставлен намеренно: регэксп только расширяет попадание в запретный список (ложное срабатывание безопасно).
    [GeneratedRegex(@"_[0-9a-fA-F]{4,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PerUserSuffix();

    public static bool IsHardListed(string name)
    {
        if (HardList.Contains(name)) return true;
        // Пользовательские экземпляры шаблонных служб: CDPUserSvc_1a2b3.
        var m = PerUserSuffix().Match(name);
        return m.Success && HardList.Contains(name[..m.Index]);
    }

    /// <summary>Путь к исполняемому файлу из ImagePath службы (с кавычками, аргументами, \SystemRoot\ и т. п.); null, если не разобрать.</summary>
    public string? ResolveImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var s = imagePath.Trim();

        string exe;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 2) return null;
            exe = s[1..end];
        }
        else
        {
            // Без кавычек: берём до первого «.exe»/«.sys» включительно, иначе до первого пробела.
            var m = Regex.Match(s, @"^.*?\.(exe|sys|dll)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            exe = m.Success ? m.Value : s.Split(' ')[0];
        }

        if (exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            exe = _windowsDir + "\\" + exe[12..];
        else if (exe.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase))
            exe = _windowsDir + "\\" + exe;
        else if (exe.StartsWith("%SystemRoot%\\", StringComparison.OrdinalIgnoreCase))
            exe = _windowsDir + "\\" + exe[13..];
        else if (exe.StartsWith("%windir%\\", StringComparison.OrdinalIgnoreCase))
            exe = _windowsDir + "\\" + exe[9..];

        return PathNormalizer.TryNormalize(exe, out var n, out _) ? n : null;
    }

    static bool IsEdgeService(string name)
        => name.StartsWith("edgeupdate", StringComparison.OrdinalIgnoreCase)
           || name.Equals("MicrosoftEdgeElevationService", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Каталог установки подтверждает владельца-программу, только если он лежит как минимум на втором уровне тома
    /// (не корень тома и не «C:\Программа»), не внутри Windows и не является самим корнем Program Files, ProgramData или профилей.
    /// </summary>
    public bool IsTrustedInstallLocation(string? installLocation)
    {
        if (!PathNormalizer.TryNormalize(installLocation, out var p, out _)) return false;
        if (PathNormalizer.Components(p).Length < 2) return false;
        if (PathNormalizer.IsEqualOrUnder(p, _windowsDir)) return false;
        foreach (var root in _commonRoots)
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase) || PathNormalizer.IsEqualOrAncestor(p, root)) return false;
        foreach (var root in _systemOwnedRoots)
            if (PathNormalizer.IsEqualOrUnder(p, root)) return false;
        return true;
    }

    bool IsExpectedCarveOutImage(string name, string exe)
    {
        var sys32 = _windowsDir + @"\System32\";
        if (name.Equals("Fax", StringComparison.OrdinalIgnoreCase))
            return exe.Equals(sys32 + "fxssvc.exe", StringComparison.OrdinalIgnoreCase);
        if (name.Equals("WMPNetworkSvc", StringComparison.OrdinalIgnoreCase))
            return exe.Equals(sys32 + "wmpnetwk.exe", StringComparison.OrdinalIgnoreCase)
                   || _programFilesRoots.Any(r => exe.Equals(r + @"\Windows Media Player\wmpnetwk.exe", StringComparison.OrdinalIgnoreCase));
        return exe.Equals(sys32 + "svchost.exe", StringComparison.OrdinalIgnoreCase);
    }

    public (ServiceClass Class, string Reason, string Rule) Classify(ServiceFacts f)
    {
        if (!ValidName().IsMatch(f.Name) || f.Name != f.Name.Trim())
            return (ServiceClass.Forbidden, "Недопустимое имя службы", "bad-name");
        if (IsHardListed(f.Name))
            return (ServiceClass.Forbidden, "Защищённая служба Windows", "service-hardlist");
        if (f.LaunchProtected)
            return (ServiceClass.Forbidden, "Защищённый процесс (PPL)", "service-ppl");
        if (f.RegisteredSecurityProduct)
            return (ServiceClass.Forbidden, "Служба зарегистрирована как продукт безопасности (антивирус, брандмауэр)", "service-security-product");
        if (f.Type is ServiceType.KernelDriver or ServiceType.FileSystemDriver)
            return (ServiceClass.Forbidden, "Драйвер ядра", "service-driver");
        if (CarveOut.Contains(f.Name))
        {
            if (f.Type is not (ServiceType.OwnProcess or ServiceType.ShareProcess))
                return (ServiceClass.ReadOnly, "Неизвестный тип службы", "service-carveout-unknown-type");
            if (f.DependentCount > 0)
                return (ServiceClass.ReadOnly, "От службы зависят другие службы", "service-carveout-has-dependents");
            if (!f.MicrosoftSigned)
                return (ServiceClass.ReadOnly, "Файл службы не подписан Microsoft", "service-carveout-unsigned");
            // Подпись svchost.exe ничего не доказывает про саму службу: образ должен быть ожидаемым файлом Windows.
            var carveExes = ImageExes(f);
            if (carveExes.Count == 0 || !carveExes.All(e => IsExpectedCarveOutImage(f.Name, e)))
                return (ServiceClass.ReadOnly, "Файл службы не совпадает с ожидаемым файлом Windows", "service-carveout-unexpected-image");
            return (ServiceClass.Modifiable, "Служба Windows из каталога Чистовика", "service-catalog-carveout");
        }
        if (f.Type == ServiceType.ShareProcess)
            return (ServiceClass.Forbidden, "Служба в общем процессе svchost", "service-shared");
        if (f.Type != ServiceType.OwnProcess)
            return (ServiceClass.ReadOnly, "Неизвестный тип службы", "service-unknown-type");

        if (IsEdgeService(f.Name))
            return (ServiceClass.ReadOnly, "Служба Microsoft Edge: обновляется и чинится самим Edge", "service-edge");

        // Сырой ImagePath и путь, раскрытый Native, проверяются оба; нужен хотя бы один, непустой путь не должен не разбираться.
        var exes = new List<string>(2);
        var raw = ResolveImagePath(f.ImagePath);
        if (raw is not null) exes.Add(raw);
        if (f.NativeImagePath is not null)
        {
            if (!PathNormalizer.TryNormalize(f.NativeImagePath, out var nativePath, out _))
                return (ServiceClass.ReadOnly, "Не удалось определить файл службы", "service-no-image");
            exes.Add(nativePath);
        }
        if (exes.Count == 0)
            return (ServiceClass.ReadOnly, "Не удалось определить файл службы", "service-no-image");

        foreach (var exe in exes)
        {
            if (PathNormalizer.IsEqualOrUnder(exe, _windowsDir))
                return (ServiceClass.Forbidden, "Файл службы лежит в каталоге Windows", "service-in-windows");
            foreach (var root in _systemOwnedRoots)
                if (PathNormalizer.IsEqualOrUnder(exe, root))
                    return (ServiceClass.Forbidden, "Файл службы в системной зоне", "service-system-zone");
        }

        var owned = f.OwnedByInstalledProgram && IsTrustedInstallLocation(f.InstallLocation);
        if (f.DependentCount > 0)
            return (ServiceClass.ReadOnly, "От службы зависят другие службы", "service-has-dependents");
        if (f.MicrosoftSigned && !owned)
            return (ServiceClass.ReadOnly, "Служба Microsoft без установленной программы", "service-microsoft-unowned");
        if (!f.SignatureValid && !owned)
            return (ServiceClass.ReadOnly, "Нет действительной подписи и владельца-программы", "service-unknown");

        return (ServiceClass.Modifiable, "Служба установленной программы", "service-modifiable");
    }

    // Ступень запуска: Disabled < Manual < Automatic/AutomaticDelayed < Boot/System. Повышение это переход на ступень выше.
    static int Rank(StartMode m) => m switch
    {
        StartMode.Disabled => 0,
        StartMode.Manual => 1,
        StartMode.Automatic or StartMode.AutomaticDelayed => 2,
        _ => 3,
    };

    // Нормализованные пути файла службы (сырой ImagePath и путь, раскрытый Native), как их видит Classify.
    List<string> ImageExes(ServiceFacts f)
    {
        var exes = new List<string>(2);
        var raw = ResolveImagePath(f.ImagePath);
        if (raw is not null) exes.Add(raw);
        if (f.NativeImagePath is not null && PathNormalizer.TryNormalize(f.NativeImagePath, out var native, out _)) exes.Add(native);
        return exes;
    }

    /// <summary>
    /// Разрешает смену запуска службы. Сигнатура для StepGate:
    /// <c>CheckSetStart(ServiceFacts f, StartMode target, bool isRevertOfOwnStep = false)</c>, <c>f</c> собран по свежему
    /// Query того же хэндла. Deny, если служба не Modifiable (<see cref="Classify"/>) или target это Boot/System.
    /// Понижение и сохранение ступени (Auto→Manual/Disabled, Manual→Disabled, Auto↔Delayed) разрешены по тем же правилам.
    /// ПОВЫШЕНИЕ (Disabled→Manual/Auto/Delayed, Manual→Auto/Delayed) разрешено напрямую, только если все известные пути файла
    /// службы (ImagePath и NativeImagePath) лежат в Program Files или Program Files (x86); иначе Deny, и повышение возможно
    /// только откатом собственного шага (<paramref name="isRevertOfOwnStep"/>=true, флаг выставляет только StepGate;
    /// классификация при этом не ослабляется). Target = «service:&lt;имя&gt;».
    /// </summary>
    public PolicyDecision CheckSetStart(ServiceFacts f, StartMode target, bool isRevertOfOwnStep = false)
    {
        var obj = "service:" + f.Name;
        var (cls, reason, rule) = Classify(f);
        if (cls != ServiceClass.Modifiable)
            return PolicyDecision.Deny(reason, rule).WithTarget(obj);
        if (target is StartMode.Boot or StartMode.System)
            return PolicyDecision.Deny("Загрузочные режимы запуска не назначаем", "service-boot-start").WithTarget(obj);

        var carve = rule == "service-catalog-carveout";
        if (carve && !isRevertOfOwnStep && target is StartMode.Automatic or StartMode.AutomaticDelayed)
            return PolicyDecision.Deny("Службы из каталога Чистовика не включаем в автозапуск, только откатом", "service-carveout-target").WithTarget(obj);

        if (Rank(target) > Rank(f.CurrentStart) && !isRevertOfOwnStep)
        {
            // Службы-исключения лежат в каталоге Windows: повышение запуска напрямую не разрешаем никогда.
            if (carve)
                return PolicyDecision.Deny("Повысить запуск службы из каталога Чистовика можно только откатом собственного шага", "service-elevate-outside-program-files").WithTarget(obj);
            var exes = ImageExes(f);
            var inProgramFiles = exes.Count > 0 && exes.All(e => _programFilesRoots.Any(r => PathNormalizer.IsEqualOrUnder(e, r) && !e.Equals(r, StringComparison.OrdinalIgnoreCase)));
            if (!inProgramFiles)
                return PolicyDecision.Deny("Повысить запуск службы вне Program Files можно только откатом собственного шага", "service-elevate-outside-program-files").WithTarget(obj);
        }
        return PolicyDecision.Allow(rule).WithTarget(obj);
    }
}
