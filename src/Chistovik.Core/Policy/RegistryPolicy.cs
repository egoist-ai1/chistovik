using System.Text.RegularExpressions;

namespace Chistovik.Core.Policy;

public enum RegHive { LocalMachine, CurrentUser, ClassesRoot, Users, CurrentConfig }

/// <summary>Ключ реестра в нормальном виде: куст и сегменты без пустых элементов.</summary>
public sealed record RegKey(RegHive Hive, string[] Segments)
{
    public override string ToString() => Hive + "\\" + string.Join('\\', Segments);
}

/// <summary>
/// Политика реестра. Для удаления ключей действует строгий список структурных и системных узлов,
/// для записи значений только запретный список (защиты системы); разрешающий список записи ведёт каталог твиков.
/// </summary>
public static partial class RegistryPolicy
{
    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    [GeneratedRegex(@"^ControlSet[0-9]{3}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ControlSetN();

    // ---------- разбор ----------

    public static bool TryParse(string? input, out RegKey key, out string error)
    {
        key = new RegKey(RegHive.LocalMachine, []);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) { error = "Пустой ключ"; return false; }
        if (input.Length > 32767) { error = "Слишком длинный ключ"; return false; }
        foreach (var ch in input)
            if (ch < 0x20) { error = "Управляющие символы в ключе"; return false; }

        var s = input.Replace('/', '\\');
        const string psProvider = @"Microsoft.PowerShell.Core\Registry::";
        if (s.StartsWith(psProvider, StringComparison.OrdinalIgnoreCase)) s = s[psProvider.Length..];
        else if (s.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase)) s = s[10..];

        var seg = s.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (seg.Count == 0) { error = "Нет сегментов"; return false; }
        foreach (var x in seg)
            if (x is "." or "..") { error = "Сегменты '.' и '..' не допускаются"; return false; }

        RegHive hive;
        var first = seg[0].TrimEnd(':');
        if (first.Equals("REGISTRY", StringComparison.OrdinalIgnoreCase))
        {
            if (seg.Count < 2) { error = "Неполный путь \\REGISTRY"; return false; }
            var second = seg[1];
            if (second.Equals("MACHINE", StringComparison.OrdinalIgnoreCase)) hive = RegHive.LocalMachine;
            else if (second.Equals("USER", StringComparison.OrdinalIgnoreCase)) hive = RegHive.Users;
            else { error = "Неизвестный раздел \\REGISTRY"; return false; }
            seg.RemoveRange(0, 2);
        }
        else
        {
            if (first.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || first.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)) hive = RegHive.LocalMachine;
            else if (first.Equals("HKCU", StringComparison.OrdinalIgnoreCase) || first.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)) hive = RegHive.CurrentUser;
            else if (first.Equals("HKCR", StringComparison.OrdinalIgnoreCase) || first.Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase)) hive = RegHive.ClassesRoot;
            else if (first.Equals("HKU", StringComparison.OrdinalIgnoreCase) || first.Equals("HKEY_USERS", StringComparison.OrdinalIgnoreCase)) hive = RegHive.Users;
            else if (first.Equals("HKCC", StringComparison.OrdinalIgnoreCase) || first.Equals("HKEY_CURRENT_CONFIG", StringComparison.OrdinalIgnoreCase)) hive = RegHive.CurrentConfig;
            else { error = "Неизвестный куст"; return false; }
            seg.RemoveAt(0);
        }

        // HKLM\SYSTEM\ControlSet001 это тот же CurrentControlSet.
        if (hive == RegHive.LocalMachine && seg.Count >= 2
            && seg[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) && ControlSetN().IsMatch(seg[1]))
            seg[1] = "CurrentControlSet";

        key = new RegKey(hive, [.. seg]);
        return true;
    }

    // ---------- вспомогательное ----------

    static bool StartsWith(IReadOnlyList<string> segs, int offset, params string[] prefix)
    {
        if (segs.Count - offset < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (!segs[offset + i].Equals(prefix[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static bool Equal(IReadOnlyList<string> segs, int offset, params string[] path)
        => segs.Count - offset == path.Length && StartsWith(segs, offset, path);

    /// <summary>Часть пути после «Software» (с учётом HKLM\SOFTWARE, Wow6432Node, HKCU, HKU\sid), либо null.</summary>
    static string[]? SoftwareRel(RegKey k)
    {
        var s = k.Segments;
        int skip;
        switch (k.Hive)
        {
            case RegHive.LocalMachine: if (!StartsWith(s, 0, "SOFTWARE")) return null; skip = 1; break;
            case RegHive.CurrentUser: if (!StartsWith(s, 0, "Software")) return null; skip = 1; break;
            case RegHive.Users: if (s.Length < 2 || !s[1].Equals("Software", StringComparison.OrdinalIgnoreCase)) return null; skip = 2; break;
            default: return null;
        }
        var rel = s.Skip(skip).ToList();
        if (rel.Count > 0 && rel[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase) && k.Hive == RegHive.LocalMachine)
            rel.RemoveAt(0);
        return [.. rel];
    }

    // ---------- запретные узлы (удаление и запись) ----------

    // Узел и всё под ним: HKLM\SYSTEM\CurrentControlSet\...
    static readonly string[][] SystemDenyUnder =
    [
        ["CurrentControlSet", "Services"],
        ["CurrentControlSet", "Enum"],
        ["CurrentControlSet", "Control", "Lsa"],
        ["CurrentControlSet", "Control", "CI"],
        ["CurrentControlSet", "Control", "DeviceGuard"],
        ["CurrentControlSet", "Control", "SecurityProviders"],
        ["CurrentControlSet", "Control", "Class"],
        ["CurrentControlSet", "Control", "Session Manager", "Kernel"],
        ["CurrentControlSet", "Control", "Session Manager", "Memory Management"],
        ["CurrentControlSet", "Control", "Winlogon"],
        ["CurrentControlSet", "Control", "SafeBoot"],
        ["CurrentControlSet", "Control", "StorageDevicePolicies"],
        ["CurrentControlSet", "Control", "MiniNT"],
        ["CurrentControlSet", "Control", "Terminal Server"],
        ["CurrentControlSet", "Control", "EarlyLaunch"],
        ["CurrentControlSet", "Control", "WMI"],
        ["CurrentControlSet", "Control", "Srp"],
        ["CurrentControlSet", "Control", "ProductOptions"],
        ["CurrentControlSet", "Hardware Profiles"],
        ["Select"], ["Setup"], ["MountedDevices"], ["DriverDatabase"], ["HardwareConfig"], ["RNG"], ["WPA"],
    ];

    // Явный allow-список записи значений в HKLM\SYSTEM (путь относительно SYSTEM). Всё, чего здесь нет, запрещено.
    // Subtree: правило действует на ключ и всё под ним; Values: допустимые имена значений (null: любые).
    // Session Manager целиком закрыт (PendingFileRenameOperations, BootExecute, KnownDLLs...), кроме ветки Power.
    static readonly (string[] Path, bool Subtree, string[]? Values)[] SystemWriteAllow =
    [
        (["CurrentControlSet", "Control"], false, ["WaitToKillServiceTimeout"]),
        (["CurrentControlSet", "Control", "GraphicsDrivers"], false, ["HwSchMode"]),
        (["CurrentControlSet", "Control", "Remote Assistance"], false, ["fAllowToGetHelp"]),
        (["CurrentControlSet", "Control", "Power"], true, null),
        (["CurrentControlSet", "Control", "Session Manager", "Power"], true, null),
    ];

    static bool SystemWriteAllowed(string[] rest, string valueName)
    {
        foreach (var (path, subtree, values) in SystemWriteAllow)
        {
            if (subtree ? !StartsWith(rest, 0, path) : !Equal(rest, 0, path)) continue;
            if (values is null || values.Contains(valueName, Cmp)) return true;
        }
        return false;
    }

    // Узкие исключения из запрета HKLM/HKCU\Software\Policies\Microsoft\*: точный ключ и точное имя значения.
    static readonly (RegHive[] Hives, string[] RelKey, string Value)[] PoliciesMicrosoftWriteAllow =
    [
        ([RegHive.CurrentUser, RegHive.Users], ["Policies", "Microsoft", "Windows", "Explorer"], "DisableSearchBoxSuggestions"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "DataCollection"], "AllowTelemetry"),
        // Твики v2 «Приватность»: по одной строке на каждое значение, без подстановок и целых веток.
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "System"], "EnableActivityFeed"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "System"], "PublishUserActivities"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "System"], "UploadUserActivities"),
        ([RegHive.CurrentUser, RegHive.Users], ["Policies", "Microsoft", "Windows", "WindowsCopilot"], "TurnOffWindowsCopilot"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "WindowsCopilot"], "TurnOffWindowsCopilot"),
        ([RegHive.CurrentUser, RegHive.Users], ["Policies", "Microsoft", "Windows", "WindowsAI"], "DisableAIDataAnalysis"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "WindowsAI"], "DisableAIDataAnalysis"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "CloudContent"], "DisableWindowsConsumerFeatures"),
        ([RegHive.LocalMachine], ["Policies", "Microsoft", "Windows", "Windows Search"], "DisableWebSearch"),
    ];

    static bool IsPoliciesMicrosoft(string[] rel)
        => rel.Length >= 2 && rel[0].Equals("Policies", StringComparison.OrdinalIgnoreCase)
           && rel[1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase);

    // Профиль обычного пользователя: доменный/локальный S-1-5-21-..., учётная запись Майкрософт S-1-12-1-...,
    // с суффиксом _Classes для куста классов. .DEFAULT и сервисные SID (S-1-5-18/19/20) обычными не считаются.
    [GeneratedRegex(@"^(S-1-5-21(-[0-9]+)+|S-1-12-1(-[0-9]+)+)(_Classes)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinaryUserSid();

    static bool IsOrdinaryUser(string sidSegment, out bool classesHive)
    {
        classesHive = sidSegment.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);
        return OrdinaryUserSid().IsMatch(sidSegment);
    }

    // В HKLM\SOFTWARE (относительно): узел и всё под ним.
    static readonly string[][] SoftwareDenyUnder =
    [
        ["Microsoft", "Windows Defender"],
        ["Microsoft", "Windows Defender Security Center"],
        ["Microsoft", "Windows Security Health"],
        ["Microsoft", "Security Center"],
        ["Microsoft", "Windows Advanced Threat Protection"],
        ["Microsoft", "Cryptography"],
        ["Microsoft", "SystemCertificates"],
        ["Microsoft", "EnterpriseCertificates"],
        ["Microsoft", "Windows NT", "CurrentVersion", "Winlogon"],
        ["Microsoft", "Windows NT", "CurrentVersion", "Image File Execution Options"],
        ["Microsoft", "Windows NT", "CurrentVersion", "ProfileList"],
        ["Microsoft", "Windows NT", "CurrentVersion", "SilentProcessExit"],
        ["Microsoft", "Windows NT", "CurrentVersion", "Svchost"],
        ["Microsoft", "Windows", "CurrentVersion", "Policies"],
        ["Microsoft", "Windows", "CurrentVersion", "Authentication"],
        ["Microsoft", "Windows", "CurrentVersion", "Component Based Servicing"],
        ["Microsoft", "Windows", "CurrentVersion", "WindowsUpdate"],
        ["Microsoft", "Windows", "CurrentVersion", "Installer"],
        ["Microsoft", "Windows", "CurrentVersion", "Setup"],
        ["Microsoft", "Windows", "CurrentVersion", "SideBySide"],
        ["Microsoft", "Windows", "CurrentVersion", "Appx"],
        ["Microsoft", "Windows", "CurrentVersion", "AppModel"],
        ["Microsoft", "Windows NT", "CurrentVersion", "Windows"],                 // AppInit_DLLs
        ["Microsoft", "Windows NT", "CurrentVersion", "Schedule", "TaskCache"],
        ["Microsoft", "Windows", "CurrentVersion", "Internet Settings"],          // прокси, в том числе Connections\WinHttpSettings
        ["Microsoft", "WindowsUpdate", "UX", "Settings"],
        ["Microsoft", "Windows", "CurrentVersion", "Group Policy", "Scripts"],
        ["Microsoft", "Rpc"], ["Microsoft", "Ole"], ["Microsoft", "COM3"], ["Microsoft", "WBEM"],
        ["Microsoft", "Driver Signing"], ["Microsoft", "Non-Driver Signing"], ["Microsoft", "PolicyManager"],
        ["Chistovik"],
    ];

    // Узлы, которые целиком удалять нельзя, но их допустимые потомки оцениваются отдельно.
    static readonly string[][] SoftwareDenyEqual =
    [
        ["Microsoft"], ["Microsoft", "Windows"], ["Microsoft", "Windows NT"], ["Microsoft", "Windows NT", "CurrentVersion"],
        ["Microsoft", "Windows", "CurrentVersion"], ["Microsoft", "Windows", "CurrentVersion", "Uninstall"],
        ["Microsoft", "Windows", "CurrentVersion", "Run"], ["Microsoft", "Windows", "CurrentVersion", "RunOnce"],
        ["Microsoft", "Windows", "CurrentVersion", "App Paths"], ["Microsoft", "Windows", "CurrentVersion", "SharedDLLs"],
        ["Microsoft", "Windows", "CurrentVersion", "Explorer"],
        ["Microsoft", "Windows", "CurrentVersion", "Explorer", "Advanced"],
        ["Classes"], ["Clients"], ["Policies"], ["Policies", "Microsoft"], ["Policies", "Microsoft", "Windows"],
        ["RegisteredApplications"], ["ODBC"], ["Wow6432Node"],
    ];

    // Потомки, которые разрешены для удаления целиком (запись программы).
    static readonly string[][] SoftwareDeleteChildAllowed =
    [
        ["Microsoft", "Windows", "CurrentVersion", "Uninstall"],
        ["Microsoft", "Windows", "CurrentVersion", "App Paths"],
    ];

    static readonly string[] SoftwareDenyPolicies = ["Policies", "Microsoft", "Windows Defender"];

    static readonly HashSet<string> SoftwareTopStructural = new(Cmp)
    {
        "Microsoft", "Policies", "Classes", "Clients", "RegisteredApplications", "ODBC", "Wow6432Node", "Chistovik",
    };

    // ---------- проверка удаления ключа ----------

    public static PolicyDecision CheckDeleteKey(string? input)
    {
        if (!TryParse(input, out var k, out var err))
            return PolicyDecision.Deny("Ключ реестра не разобран: " + err, "reg-bad-key");
        return CheckDeleteKey(k);
    }

    public static PolicyDecision CheckDeleteKey(RegKey k)
        => DeleteCore(k).WithTarget(k.ToString());

    static PolicyDecision DeleteCore(RegKey k)
    {
        var s = k.Segments;
        if (s.Length == 0) return PolicyDecision.Deny("Корень куста", "reg-hive-root");

        switch (k.Hive)
        {
            case RegHive.CurrentConfig:
                return PolicyDecision.Deny("Текущая конфигурация оборудования не изменяется", "reg-hkcc");

            case RegHive.Users:
                if (s.Length == 1) return PolicyDecision.Deny("Корень профиля пользователя в реестре", "reg-hive-root");
                if (!IsOrdinaryUser(s[0], out var classesHive))
                    return PolicyDecision.Deny("Профиль не обычного пользователя (.DEFAULT, служебные учётные записи)", "reg-system-user");
                if (classesHive) return CheckClassesDelete(s[1..], k.Hive);   // HKU\<sid>_Classes это HKCU\Software\Classes
                if (s.Length == 2) return PolicyDecision.Deny("Структурный узел профиля", "reg-structure");
                break;

            case RegHive.ClassesRoot:
                return CheckClassesDelete(s, k.Hive);

            case RegHive.LocalMachine:
                if (s.Length == 1) return PolicyDecision.Deny("Корневой раздел куста", "reg-hive-root");
                if (!s[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                    && !s[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny("Системный куст (SAM, SECURITY, BCD, HARDWARE)", "reg-system-hive");
                if (s[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny("Ветка SYSTEM не изменяется напрямую", "reg-system-branch");
                break;

            case RegHive.CurrentUser:
                if (s.Length == 1) return PolicyDecision.Deny("Корневой раздел профиля", "reg-structure");
                break;
        }

        var rel = SoftwareRel(k);
        if (rel is null)
        {
            // HKCU/HKU вне Software: AppEvents, Environment, Control Panel и т. п. по умолчанию только эксперту.
            return PolicyDecision.Expert("Ключ вне ветки Software", "reg-nonsoftware");
        }
        return CheckSoftwareDelete(rel, k.Hive);
    }

    static PolicyDecision CheckSoftwareDelete(string[] rel, RegHive hive)
    {
        if (rel.Length == 0) return PolicyDecision.Deny("Корень Software", "reg-hive-root");

        if (rel.Length == 1 && SoftwareTopStructural.Contains(rel[0]))
            return PolicyDecision.Deny("Структурный узел Software", "reg-structure");

        if (hive == RegHive.LocalMachine || hive == RegHive.CurrentUser || hive == RegHive.Users)
        {
            if (IsPoliciesMicrosoft(rel))
                return PolicyDecision.Deny("Политики Microsoft (Windows, Defender, Edge, обновления) не удаляются", "reg-policies-microsoft");
            foreach (var d in SoftwareDenyUnder)
                if (StartsWith(rel, 0, d)) return PolicyDecision.Deny("Системный узел реестра", "reg-protected-node");
            if (StartsWith(rel, 0, SoftwareDenyPolicies))
                return PolicyDecision.Deny("Политики защитника", "reg-protected-node");
            foreach (var d in SoftwareDenyEqual)
                if (Equal(rel, 0, d)) return PolicyDecision.Deny("Структурный узел реестра", "reg-structure");

            // Classes внутри Software: тот же смысл, что и HKCR.
            if (StartsWith(rel, 0, "Classes"))
                return CheckClassesDelete(rel.Skip(1).ToArray(), hive);

            // Записи программ: Uninstall\<x>, App Paths\<x>.
            foreach (var parent in SoftwareDeleteChildAllowed)
                if (rel.Length == parent.Length + 1 && StartsWith(rel, 0, parent))
                    return PolicyDecision.Allow("reg-program-entry");
            foreach (var parent in SoftwareDeleteChildAllowed)
                if (rel.Length > parent.Length + 1 && StartsWith(rel, 0, parent))
                    return PolicyDecision.Allow("reg-program-entry");

            if (rel.Length >= 1 && rel[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                return PolicyDecision.Expert("Ветка Microsoft: только по подтверждению эксперта", "reg-microsoft");
            if (rel.Length >= 1 && rel[0].Equals("Policies", StringComparison.OrdinalIgnoreCase))
                return PolicyDecision.Expert("Ветка политик: только по подтверждению эксперта", "reg-policies");
        }

        return PolicyDecision.Allow();
    }

    static readonly HashSet<string> ClassesStructural = new(Cmp)
    {
        "CLSID", "Interface", "TypeLib", "AppID", "Wow6432Node", "Installer", "Directory", "Drive", "Folder",
        "*", "AllFilesystemObjects", "Local Settings", "MIME", "Extensions", "PROTOCOLS", "SystemFileAssociations",
        "Applications", "Record",
    };

    // Системные расширения: ключ и всё под ним закрыты.
    static readonly HashSet<string> ClassesProtectedExtensions = new(Cmp)
    {
        ".exe", ".lnk", ".dll", ".sys", ".bat", ".cmd", ".com", ".msi", ".cpl", ".scr", ".inf", ".reg", ".url",
        ".library-ms", ".settingcontent-ms",
    };

    // Системные ProgID и протоколы: ключ и всё под ним закрыты.
    static readonly HashSet<string> ClassesProtectedProgIds = new(Cmp)
    {
        "exefile", "lnkfile", "batfile", "cmdfile", "dllfile", "regfile", "inffile", "scrfile", "Unknown",
        "ms-settings", "http", "https", "file", "mailto", "ActivatableClasses", "PackagedCom",
    };

    // Оболочечные корни: сам узел закрыт (ClassesStructural), записи под ним только эксперту.
    static readonly HashSet<string> ClassesShellRoots = new(Cmp) { "Folder", "Directory", "Drive" };

    static PolicyDecision CheckClassesDelete(string[] s, RegHive hive)
    {
        if (s.Length == 0) return PolicyDecision.Deny("Корень классов", "reg-hive-root");
        if (s[0].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Length == 1) return PolicyDecision.Deny("Структурный узел классов", "reg-structure");
            s = s[1..];
        }
        var top = s[0];
        if (s.Length == 1 && ClassesStructural.Contains(top))
            return PolicyDecision.Deny("Структурный узел классов", "reg-structure");

        if (top.StartsWith('.'))
            return ClassesProtectedExtensions.Contains(top)
                ? PolicyDecision.Deny("Системное сопоставление расширения", "reg-classes-system")
                : PolicyDecision.Expert("Сопоставление расширения: только по подтверждению эксперта", "reg-assoc");

        if (ClassesProtectedProgIds.Contains(top) || top.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Системный ProgID или протокол", "reg-classes-system");

        if (top.Equals("Local Settings", StringComparison.OrdinalIgnoreCase))
        {
            // Local Settings\Software это снимок Software (в нём AppModel\Repository с пакетами Store).
            if (s[1].Equals("Software", StringComparison.OrdinalIgnoreCase)) return CheckSoftwareDelete(s[2..], hive == RegHive.ClassesRoot ? RegHive.CurrentUser : hive);
            if (s[1].Equals("AppModel", StringComparison.OrdinalIgnoreCase))
                return PolicyDecision.Deny("Данные пакетов Store", "reg-protected-node");
            return PolicyDecision.Expert("Local Settings: только по подтверждению эксперта", "reg-classes-local-settings");
        }

        if (ClassesShellRoots.Contains(top))
            return PolicyDecision.Expert("Запись оболочки для папок и дисков: только по подтверждению эксперта", "reg-classes-shell");

        return PolicyDecision.Allow("reg-classes-entry");
    }

    // ---------- проверка записи значения ----------

    static readonly HashSet<string> UacValues = new(Cmp)
    {
        "EnableLUA", "ConsentPromptBehaviorAdmin", "ConsentPromptBehaviorUser", "PromptOnSecureDesktop",
        "FilterAdministratorToken", "LocalAccountTokenFilterPolicy", "EnableInstallerDetection",
        "EnableSecureUIAPaths", "EnableUIADesktopToggle", "ValidateAdminCodeSignatures", "EnableVirtualization",
        "TypeOfAdminApprovalMode",
    };

    static readonly HashSet<string> SmartScreenValues = new(Cmp)
    {
        "EnableSmartScreen", "ShellSmartScreenLevel", "SmartScreenEnabled", "EnableWebContentEvaluation",
        "PreventOverride", "PreventOverrideAppRepUnknown",
    };

    static readonly HashSet<string> DefenderPolicyValues = new(Cmp)
    {
        "DisableAntiSpyware", "DisableAntiVirus", "DisableRealtimeMonitoring", "DisableBehaviorMonitoring",
        "DisableOnAccessProtection", "DisableIOAVProtection", "DisableScanOnRealtimeEnable", "TamperProtection",
    };

    public static PolicyDecision CheckSetValue(string? keyInput, string? valueName)
    {
        if (!TryParse(keyInput, out var k, out var err))
            return PolicyDecision.Deny("Ключ реестра не разобран: " + err, "reg-bad-key");
        if (valueName is null) return PolicyDecision.Deny("Имя значения не задано", "reg-bad-value");
        if (valueName.Length > 16383 || valueName.Any(c => c < 0x20))
            return PolicyDecision.Deny("Недопустимое имя значения", "reg-bad-value");
        return CheckSetValue(k, valueName);
    }

    public static PolicyDecision CheckSetValue(RegKey k, string valueName)
        => SetCore(k, valueName).WithTarget(k + "::" + valueName);

    /// <summary>
    /// Запись значения от имени агента для проверенного клиента. Сигнатура для StepGate:
    /// <c>CheckSetValue(string? keyInput, string? valueName, string clientSid)</c>. Все правила <see cref="CheckSetValue(string?, string?)"/>
    /// плюс правило HKU: ключ в <c>HKU\&lt;sid&gt;</c> или <c>HKU\&lt;sid&gt;_Classes</c> разрешён только при sid == <paramref name="clientSid"/>
    /// (без учёта регистра); <c>HKU\S-1-5-18</c>, <c>.DEFAULT</c>, чужие SID, а также HKCU и HKCR (агент их не открывает) это Deny.
    /// Если сам <paramref name="clientSid"/> не SID обычного пользователя (S-1-5-21-… или S-1-12-1-…, без суффикса _Classes), это Deny.
    /// </summary>
    public static PolicyDecision CheckSetValue(string? keyInput, string? valueName, string clientSid)
    {
        if (!TryParse(keyInput, out var k, out var err))
            return PolicyDecision.Deny("Ключ реестра не разобран: " + err, "reg-bad-key");
        if (valueName is null) return PolicyDecision.Deny("Имя значения не задано", "reg-bad-value");
        if (valueName.Length > 16383 || valueName.Any(c => c < 0x20))
            return PolicyDecision.Deny("Недопустимое имя значения", "reg-bad-value");
        return CheckSetValue(k, valueName, clientSid);
    }

    /// <summary>Версия <see cref="CheckSetValue(string?, string?, string)"/> для уже разобранного ключа.</summary>
    public static PolicyDecision CheckSetValue(RegKey k, string valueName, string clientSid)
    {
        var gate = ClientGate(k, clientSid);
        if (!gate.IsAllowed) return gate.WithTarget(k + "::" + valueName);
        return CheckSetValue(k, valueName);
    }

    /// <summary>
    /// Удаление значения не мягче записи: результат совпадает с <see cref="CheckSetValue(string?, string?)"/> для того же ключа и имени
    /// (включая Target). Сигнатура: <c>CheckDeleteValue(string? keyInput, string? valueName)</c>.
    /// </summary>
    public static PolicyDecision CheckDeleteValue(string? keyInput, string? valueName)
        => CheckSetValue(keyInput, valueName);

    /// <summary>Версия для разобранного ключа.</summary>
    public static PolicyDecision CheckDeleteValue(RegKey k, string valueName)
        => CheckSetValue(k, valueName);

    /// <summary>
    /// Удаление значения от имени агента: как <see cref="CheckSetValue(string?, string?, string)"/> (правило HKU и SID клиента).
    /// Сигнатура для StepGate: <c>CheckDeleteValue(string? keyInput, string? valueName, string clientSid)</c>.
    /// </summary>
    public static PolicyDecision CheckDeleteValue(string? keyInput, string? valueName, string clientSid)
        => CheckSetValue(keyInput, valueName, clientSid);

    /// <summary>Версия для разобранного ключа.</summary>
    public static PolicyDecision CheckDeleteValue(RegKey k, string valueName, string clientSid)
        => CheckSetValue(k, valueName, clientSid);

    /// <summary>
    /// Удаление ключа от имени агента: правила <see cref="CheckDeleteKey(string?)"/> плюс правило HKU/HKCU/HKCR как у
    /// <see cref="CheckSetValue(string?, string?, string)"/>.
    /// </summary>
    public static PolicyDecision CheckDeleteKey(string? input, string clientSid)
    {
        if (!TryParse(input, out var k, out var err))
            return PolicyDecision.Deny("Ключ реестра не разобран: " + err, "reg-bad-key");
        return CheckDeleteKey(k, clientSid);
    }

    /// <summary>Версия для разобранного ключа.</summary>
    public static PolicyDecision CheckDeleteKey(RegKey k, string clientSid)
    {
        var gate = ClientGate(k, clientSid);
        return gate.IsAllowed ? CheckDeleteKey(k) : gate.WithTarget(k.ToString());
    }

    // Правило клиента: пишем только в куст проверенного клиента; HKCU и HKCR агент не открывает (HKCU это HKU\<sid>).
    static PolicyDecision ClientGate(RegKey k, string? clientSid)
    {
        if (string.IsNullOrEmpty(clientSid) || !OrdinaryUserSid().IsMatch(clientSid)
            || clientSid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("SID клиента не SID обычного пользователя", "reg-bad-client-sid");
        switch (k.Hive)
        {
            case RegHive.CurrentUser:
                return PolicyDecision.Deny("Агент не открывает HKCU: пользовательский куст это HKU\\<SID клиента>", "reg-agent-no-hkcu");
            case RegHive.ClassesRoot:
                return PolicyDecision.Deny("Агент не открывает HKCR", "reg-hkcr");
            case RegHive.Users:
                if (k.Segments.Length == 0) return PolicyDecision.Deny("Корень куста", "reg-hive-root");
                var sid = k.Segments[0];
                if (sid.Equals(clientSid, StringComparison.OrdinalIgnoreCase)
                    || sid.Equals(clientSid + "_Classes", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Allow("reg-client-hive");
                return PolicyDecision.Deny("Куст не принадлежит проверенному клиенту (.DEFAULT, служебные и чужие SID)", "reg-foreign-user");
        }
        return PolicyDecision.Allow("reg-client-hive");
    }

    // ---------- закрытые для записи места: запуск кода, подмена команд, автозапуск ----------

    // Сегменты внутри Classes, через которые подменяют запускаемый код (команды оболочки, COM-серверы, обработчики).
    static readonly HashSet<string> ClassesCodeSegments = new(Cmp)
    {
        "shell", "shellex", "command", "InprocServer32", "InprocServer", "LocalServer32", "LocalServer", "InprocHandler32",
        "InprocHandler", "TreatAs", "ScriptletHandler", "DelegateExecute", "AutoConvertTo", "ProxyStubClsid32", "ProxyStubClsid",
        "Local Settings", "ActivatableClasses", "PackagedCom",
    };

    // Узлы Software, не связанные с Classes, которые тоже запускают код при входе или старте проводника.
    static readonly string[][] PersistenceDenyUnder =
    [
        ["Microsoft", "Command Processor"],                                       // AutoRun
        ["Microsoft", "Active Setup"],                                            // StubPath
        ["Microsoft", "Windows", "CurrentVersion", "ShellServiceObjectDelayLoad"],
        ["Microsoft", "Windows", "CurrentVersion", "Explorer", "ShellExecuteHooks"],
        ["Microsoft", "Windows", "CurrentVersion", "Explorer", "Browser Helper Objects"],
        ["Microsoft", "Windows", "CurrentVersion", "Explorer", "ShellIconOverlayIdentifiers"],
        ["Microsoft", "Windows", "CurrentVersion", "Explorer", "Run"],
    ];

    static PolicyDecision CheckClassesWrite(string[] rest)
    {
        if (rest.Length > 0 && rest[0].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase)) rest = rest[1..];
        if (rest.Length == 0) return PolicyDecision.Deny("Значения в корне классов не записываются", "reg-classes-write");
        var top = rest[0];
        if (ClassesProtectedExtensions.Contains(top) || ClassesProtectedProgIds.Contains(top) || top.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Системное сопоставление расширения, ProgID или протокол", "reg-classes-system");
        foreach (var seg in rest)
            if (ClassesCodeSegments.Contains(seg))
                return PolicyDecision.Deny("Запись команды или COM-сервера в Classes подменяет запускаемый код", "reg-classes-code");
        return PolicyDecision.Deny("Запись значений в Classes не входит в разрешённые для записи", "reg-classes-default-deny");
    }

    static PolicyDecision SetCore(RegKey k, string valueName)
    {
        var s = k.Segments;
        if (s.Length == 0) return PolicyDecision.Deny("Корень куста", "reg-hive-root");

        // Значения защит ограничиваем по имени в любом ключе: так не обойти переносом в другой куст или в Wow6432Node.
        if (UacValues.Contains(valueName))
            return PolicyDecision.Deny("Параметр контроля учётных записей (UAC)", "reg-uac");
        if (SmartScreenValues.Contains(valueName))
            return PolicyDecision.Deny("Параметр SmartScreen", "reg-smartscreen");
        if (DefenderPolicyValues.Contains(valueName))
            return PolicyDecision.Deny("Параметр защитника Windows", "reg-defender");
        if (valueName.Equals("UserInitMprLogonScript", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Скрипт входа в систему запускает код при каждом входе", "reg-logon-script");

        switch (k.Hive)
        {
            case RegHive.CurrentConfig:
                return PolicyDecision.Deny("Текущая конфигурация оборудования не изменяется", "reg-hkcc");
            case RegHive.ClassesRoot:
                return PolicyDecision.Deny("Запись значений в HKCR закрыта: там живут команды и COM-серверы", "reg-hkcr");
            case RegHive.CurrentUser:
                if (s[0].Equals("Environment", StringComparison.OrdinalIgnoreCase) || s[0].Equals("Volatile Environment", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny("Переменные среды пользователя и скрипт входа не изменяются", "reg-environment");
                break;
            case RegHive.LocalMachine:
                if (!s[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                    && !s[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny("Системный куст (SAM, SECURITY, BCD, HARDWARE)", "reg-system-hive");
                if (s[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = s.Skip(1).ToArray();
                    foreach (var d in SystemDenyUnder)
                        if (StartsWith(rest, 0, d)) return PolicyDecision.Deny("Защищённая ветка SYSTEM", "reg-protected-node");
                    // Остальное в SYSTEM запрещено, если ключ не назван в явном allow-списке.
                    return SystemWriteAllowed(rest, valueName)
                        ? PolicyDecision.Allow("reg-system-allowlist")
                        : PolicyDecision.Deny("Ветка SYSTEM не входит в список разрешённых для записи", "reg-system-default-deny");
                }
                break;
            case RegHive.Users:
                if (s.Length == 1 || !IsOrdinaryUser(s[0], out var classesHive))
                    return PolicyDecision.Deny("Профиль не обычного пользователя (.DEFAULT, служебные учётные записи)", "reg-system-user");
                if (classesHive) return CheckClassesWrite(s[1..]);   // HKU\<sid>_Classes это HKCU\Software\Classes
                if (s[1].Equals("Environment", StringComparison.OrdinalIgnoreCase) || s[1].Equals("Volatile Environment", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny("Переменные среды пользователя и скрипт входа не изменяются", "reg-environment");
                break;
        }

        var rel = SoftwareRel(k);
        if (rel is not null)
        {
            if (StartsWith(rel, 0, "Classes")) return CheckClassesWrite(rel[1..]);
            // Автозапуск: CurrentVersion\Run, RunOnce, RunOnceEx, RunServices, RunServicesOnce (и Wow6432Node).
            if (StartsWith(rel, 0, "Microsoft", "Windows", "CurrentVersion") && rel.Length > 3
                && rel[3].StartsWith("Run", StringComparison.OrdinalIgnoreCase))
                return PolicyDecision.Deny("Ключи автозапуска Run* не изменяются как значения реестра (автозагрузка идёт через StartupApproved)", "reg-run-keys");
            foreach (var d in PersistenceDenyUnder)
                if (StartsWith(rel, 0, d)) return PolicyDecision.Deny("Узел, запускающий код при входе или старте проводника", "reg-persistence");
            // Согласия приложений на доступ меняются только в профиле пользователя; машинный ConsentStore не трогаем.
            if (k.Hive == RegHive.LocalMachine && StartsWith(rel, 0, "Microsoft", "Windows", "CurrentVersion", "CapabilityAccessManager"))
                return PolicyDecision.Deny("Машинные согласия приложений не изменяются", "reg-consent-machine");
            if (IsPoliciesMicrosoft(rel))
            {
                foreach (var (hives, relKey, value) in PoliciesMicrosoftWriteAllow)
                    if (hives.Contains(k.Hive) && Equal(rel, 0, relKey) && valueName.Equals(value, StringComparison.OrdinalIgnoreCase))
                        return PolicyDecision.Allow("reg-policies-microsoft-exception");
                return PolicyDecision.Deny("Политики Microsoft (Windows, Defender, Edge, обновления) не изменяются", "reg-policies-microsoft");
            }
            foreach (var d in SoftwareDenyUnder)
                if (StartsWith(rel, 0, d)) return PolicyDecision.Deny("Системный узел реестра", "reg-protected-node");
            if (StartsWith(rel, 0, SoftwareDenyPolicies))
                return PolicyDecision.Deny("Политики защитника", "reg-protected-node");
        }

        return PolicyDecision.Allow();
    }
}
