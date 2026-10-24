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

    [GeneratedRegex(@"^ControlSet\d{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
        ["CurrentControlSet", "Hardware Profiles"],
        ["Select"], ["Setup"], ["MountedDevices"], ["DriverDatabase"], ["HardwareConfig"], ["RNG"], ["WPA"],
    ];

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
        ["Microsoft", "Windows", "CurrentVersion", "App Paths"], ["Microsoft", "Windows", "CurrentVersion", "Explorer"],
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
    {
        var s = k.Segments;
        if (s.Length == 0) return PolicyDecision.Deny("Корень куста", "reg-hive-root");

        switch (k.Hive)
        {
            case RegHive.CurrentConfig:
                return PolicyDecision.Deny("Текущая конфигурация оборудования не изменяется", "reg-hkcc");

            case RegHive.Users:
                if (s.Length == 1) return PolicyDecision.Deny("Корень профиля пользователя в реестре", "reg-hive-root");
                if (s.Length == 2) return PolicyDecision.Deny("Структурный узел профиля", "reg-structure");
                break;

            case RegHive.ClassesRoot:
                return CheckClassesDelete(s);

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
            foreach (var d in SoftwareDenyUnder)
                if (StartsWith(rel, 0, d)) return PolicyDecision.Deny("Системный узел реестра", "reg-protected-node");
            if (StartsWith(rel, 0, SoftwareDenyPolicies))
                return PolicyDecision.Deny("Политики защитника", "reg-protected-node");
            foreach (var d in SoftwareDenyEqual)
                if (Equal(rel, 0, d)) return PolicyDecision.Deny("Структурный узел реестра", "reg-structure");

            // Classes внутри Software: тот же смысл, что и HKCR.
            if (StartsWith(rel, 0, "Classes"))
                return CheckClassesDelete(rel.Skip(1).ToArray());

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

    static PolicyDecision CheckClassesDelete(string[] s)
    {
        if (s.Length == 0) return PolicyDecision.Deny("Корень классов", "reg-hive-root");
        if (s.Length == 1 && ClassesStructural.Contains(s[0]))
            return PolicyDecision.Deny("Структурный узел классов", "reg-structure");
        if (s.Length == 1 && s[0].StartsWith('.'))
            return PolicyDecision.Allow("reg-assoc");
        if (s.Length >= 1 && s[0].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase))
            return s.Length == 2 && ClassesStructural.Contains(s[1])
                ? PolicyDecision.Deny("Структурный узел классов", "reg-structure")
                : PolicyDecision.Allow("reg-classes-entry");
        return PolicyDecision.Allow("reg-classes-entry");
    }

    // ---------- проверка записи значения ----------

    static readonly HashSet<string> UacValues = new(Cmp)
    {
        "EnableLUA", "ConsentPromptBehaviorAdmin", "ConsentPromptBehaviorUser", "PromptOnSecureDesktop",
        "FilterAdministratorToken", "LocalAccountTokenFilterPolicy", "EnableInstallerDetection",
        "EnableSecureUIAPaths", "EnableUIADesktopToggle", "ValidateAdminCodeSignatures", "EnableVirtualization",
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

        switch (k.Hive)
        {
            case RegHive.CurrentConfig:
                return PolicyDecision.Deny("Текущая конфигурация оборудования не изменяется", "reg-hkcc");
            case RegHive.LocalMachine:
                if (!s[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                    && !s[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                    return PolicyDecision.Deny("Системный куст (SAM, SECURITY, BCD, HARDWARE)", "reg-system-hive");
                if (s[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = s.Skip(1).ToArray();
                    foreach (var d in SystemDenyUnder)
                        if (StartsWith(rest, 0, d)) return PolicyDecision.Deny("Защищённая ветка SYSTEM", "reg-protected-node");
                }
                break;
        }

        var rel = SoftwareRel(k);
        if (rel is not null)
        {
            foreach (var d in SoftwareDenyUnder)
                if (StartsWith(rel, 0, d)) return PolicyDecision.Deny("Системный узел реестра", "reg-protected-node");
            if (StartsWith(rel, 0, SoftwareDenyPolicies))
                return PolicyDecision.Deny("Политики защитника", "reg-protected-node");
        }

        return PolicyDecision.Allow();
    }
}
