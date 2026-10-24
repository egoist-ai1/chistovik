using System.Text.RegularExpressions;

namespace Chistovik.Core.Policy;

public enum ServiceType { KernelDriver, FileSystemDriver, OwnProcess, ShareProcess, Other }

public enum StartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>Класс службы: Forbidden и ReadOnly не меняются никогда, Modifiable можно менять с откатом.</summary>
public enum ServiceClass { Forbidden, ReadOnly, Modifiable }

/// <summary>Признаки службы, собранные слоем Native. Классификатор не доверяет имени: решают признаки.</summary>
public sealed record ServiceFacts(
    string Name,
    ServiceType Type,
    string? ImagePath,
    bool SignatureValid,
    bool MicrosoftSigned,
    bool ProtectedProcess,
    int DependentCount,
    bool OwnedByInstalledProgram,
    StartMode CurrentStart);

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
    };

    readonly string _windowsDir;
    readonly string[] _systemOwnedRoots;

    public ServicePolicy(PolicyContext ctx)
    {
        _windowsDir = ctx.Norm(ctx.WindowsDir);
        var pf = ctx.Norm(ctx.ProgramFiles);
        var pf86 = ctx.Norm(ctx.ProgramFilesX86);
        var pd = ctx.Norm(ctx.ProgramData);
        _systemOwnedRoots =
        [
            pf + @"\Windows Defender", pf + @"\Windows Defender Advanced Threat Protection", pf + @"\Windows Security",
            pf + @"\WindowsApps", pf + @"\Common Files\Microsoft Shared", pf86 + @"\Common Files\Microsoft Shared",
            pd + @"\Microsoft\Windows Defender", pd + @"\Microsoft\Windows Defender Advanced Threat Protection",
        ];
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-$@ ]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

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

    public (ServiceClass Class, string Reason, string Rule) Classify(ServiceFacts f)
    {
        if (!ValidName().IsMatch(f.Name) || f.Name != f.Name.Trim())
            return (ServiceClass.Forbidden, "Недопустимое имя службы", "bad-name");
        if (IsHardListed(f.Name))
            return (ServiceClass.Forbidden, "Защищённая служба Windows", "service-hardlist");
        if (f.ProtectedProcess)
            return (ServiceClass.Forbidden, "Защищённый процесс (PPL)", "service-ppl");
        if (f.Type is ServiceType.KernelDriver or ServiceType.FileSystemDriver)
            return (ServiceClass.Forbidden, "Драйвер ядра", "service-driver");
        if (f.Type == ServiceType.ShareProcess)
            return (ServiceClass.Forbidden, "Служба в общем процессе svchost", "service-shared");
        if (f.Type != ServiceType.OwnProcess)
            return (ServiceClass.ReadOnly, "Неизвестный тип службы", "service-unknown-type");

        var exe = ResolveImagePath(f.ImagePath);
        if (exe is null)
            return (ServiceClass.ReadOnly, "Не удалось определить файл службы", "service-no-image");

        if (PathNormalizer.IsEqualOrUnder(exe, _windowsDir))
            return (ServiceClass.Forbidden, "Файл службы лежит в каталоге Windows", "service-in-windows");
        foreach (var root in _systemOwnedRoots)
            if (PathNormalizer.IsEqualOrUnder(exe, root))
                return (ServiceClass.Forbidden, "Файл службы в системной зоне", "service-system-zone");

        if (f.DependentCount > 0)
            return (ServiceClass.ReadOnly, "От службы зависят другие службы", "service-has-dependents");
        if (f.MicrosoftSigned && !f.OwnedByInstalledProgram)
            return (ServiceClass.ReadOnly, "Служба Microsoft без установленной программы", "service-microsoft-unowned");
        if (!f.SignatureValid && !f.OwnedByInstalledProgram)
            return (ServiceClass.ReadOnly, "Нет действительной подписи и владельца-программы", "service-unknown");

        return (ServiceClass.Modifiable, "Служба установленной программы", "service-modifiable");
    }

    public PolicyDecision CheckSetStart(ServiceFacts f, StartMode target)
    {
        var (cls, reason, rule) = Classify(f);
        if (cls != ServiceClass.Modifiable)
            return PolicyDecision.Deny(reason, rule);
        if (target is StartMode.Boot or StartMode.System)
            return PolicyDecision.Deny("Загрузочные режимы запуска не назначаем", "service-boot-start");
        return PolicyDecision.Allow(rule);
    }
}
