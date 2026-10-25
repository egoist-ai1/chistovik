using System.IO;
using System.Security.Cryptography;
using System.Text;
using Chistovik.Core.Findings;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;

namespace Chistovik.App.Services.Scanners;

/// <summary>
/// Чистка реестра без файлов (C3): R1 «Установка и удаление программ», R2 App Paths, R3 Run/RunOnce, R4 SharedDLLs
/// (docs/C2C3-DESIGN.md §1.2). Находку «убрать» получает только запись с подтверждённым свидетельством сироты; сомнение
/// (диск недоступен, команда не разобрана) даёт «мало данных», системная запись «защищено». Всё читается без прав и ничего
/// не меняет: App недоверенный, агент проверит свидетельство заново и сохранит копию записи для отката.
/// Записи, которые не похожи на сироту и не вызывают сомнений, в список не попадают, иначе он превратился бы в весь реестр.
/// </summary>
public sealed class RegistryOrphanScanner(IRegistryReader registry, IFileExists files, IMsiState msi, CommandResolver resolver,
    IRegistryDefaultValue? defaults = null) : IFindingScanner
{
    const string Cv = @"Microsoft\Windows\CurrentVersion\";
    const int MaxSharedDll = 500;
    const int MaxNameLength = 255;

    static readonly string[] ProtectedRelease = ["Update", "Hotfix", "Security Update"];

    readonly IRegistryDefaultValue? _defaults = defaults ?? registry as IRegistryDefaultValue;

    public FindingArea Area => FindingArea.Registry;

    public Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<Finding>();
        foreach (var loc in Locations)
        {
            ct.ThrowIfCancellationRequested();
            ScanArp(loc, list);
            ScanAppPaths(loc, list);
            ScanRun(loc, "Run", list);
            ScanRun(loc, "RunOnce", list);
            if (loc.Scope == "machine") ScanSharedDlls(loc, list, ct);
        }
        return (IReadOnlyList<Finding>)list;
    }, ct);

    sealed record Loc(RegistryHiveKind Hive, RegistryViewKind View, string Scope, string ViewName, string Root)
    {
        public string Path(string leaf) => Root + leaf;
    }

    static readonly Loc[] Locations =
    [
        new(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "machine", "x64", @"SOFTWARE\" + Cv),
        new(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, "machine", "x86", @"SOFTWARE\" + Cv),
        new(RegistryHiveKind.CurrentUser, RegistryViewKind.Registry64, "user", "x64", @"Software\" + Cv),
    ];

    // ---------- R1: Uninstall ----------

    void ScanArp(Loc loc, List<Finding> list)
    {
        var parent = loc.Path("Uninstall");
        foreach (var key in registry.GetSubKeyNames(loc.Hive, loc.View, parent))
        {
            if (!IdPart(key)) continue;
            var v = registry.ReadValues(loc.Hive, loc.View, parent + "\\" + key);
            if (v is null) continue;
            var name = Str(v, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var location = Str(v, "InstallLocation").Trim().Trim('"');
            var uninstall = Str(v, "UninstallString");
            var msiProduct = Dword(v, "WindowsInstaller") == 1;
            var id = $"arp:{loc.Scope}:{loc.ViewName}:{key}";
            var dirMissing = location.Length == 0 || (!files.DirectoryExists(location) && files.DriveExists(location));

            var reasons = new List<Reason>();
            if (msiProduct)
            {
                if (!dirMissing || !IsGuid(key) || msi.QueryProductState(key) != -1) continue;
                reasons.Add(ReasonCodes.Make(ReasonCodes.EvidenceMsiUnknown, key));
            }
            else
            {
                if (!dirMissing || string.IsNullOrWhiteSpace(uninstall)) continue;
                if (IsMsiExec(uninstall)) continue;
                if (!resolver.TryResolve(uninstall, out var exe, out _))
                {
                    list.Add(Unknown(id, name, "Команда удаления не разобрана", ReasonCodes.CommandUnresolved, uninstall));
                    continue;
                }
                if (files.FileExists(exe)) continue;
                if (!files.DriveExists(exe))
                {
                    list.Add(Unknown(id, name, "Диск с программой сейчас недоступен", ReasonCodes.DriveMissing, exe));
                    continue;
                }
                if (location.Length > 0) reasons.Add(ReasonCodes.Make(ReasonCodes.EvidenceInstallDirMissing, location));
                else reasons.Add(ReasonCodes.Make(ReasonCodes.EvidenceInstallDirMissing));
                reasons.Add(ReasonCodes.Make(ReasonCodes.EvidenceUninstallerMissing, exe));
            }

            var detail = $"Запись в списке программ{(loc.Scope == "user" ? " пользователя" : "")}: самой программы на диске нет";
            if (IsProtected(v, location))
                list.Add(Make(id, name, detail, VerdictKind.Protected,
                    [ReasonCodes.Make(ReasonCodes.ClassForbidden)], Confidence.L0, ChangeClass.Forbidden, ArpExplain(name)));
            else
                list.Add(Make(id, name, detail, VerdictKind.Remove, reasons, Confidence.L2, ChangeClass.Modifiable, ArpExplain(name)));
        }
    }

    static bool IsProtected(IReadOnlyDictionary<string, object?> v, string location)
    {
        if (Dword(v, "SystemComponent") == 1) return true;
        if (!string.IsNullOrWhiteSpace(Str(v, "ParentKeyName"))) return true;
        var rel = Str(v, "ReleaseType");
        if (ProtectedRelease.Any(r => r.Equals(rel, StringComparison.OrdinalIgnoreCase))) return true;
        if (location.Length == 0) return false;
        var l = location.Replace('/', '\\');
        return l.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)
               || l.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
               || l.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    static Explanation ArpExplain(string name) => new(
        $"Запись о программе «{name}» в списке установленных. Самой программы и её файла удаления на диске нет.",
        "Запись нужна только для списка «Программы и компоненты»: она осталась после неудачного или ручного удаления.",
        "Строка пропадёт из списка программ. На работу Windows это не влияет, а сама программа уже не работает.",
        "Копия записи сохраняется в журнале Чистовика: раздел «Журнал», кнопка «Отменить».");

    // ---------- R2: App Paths ----------

    void ScanAppPaths(Loc loc, List<Finding> list)
    {
        var parent = loc.Path("App Paths");
        foreach (var exeName in registry.GetSubKeyNames(loc.Hive, loc.View, parent))
        {
            if (exeName.Length is 0 or > MaxNameLength || exeName.IndexOfAny(['\\', '/', ':']) >= 0 || exeName.Any(c => c < ' ' || c == '\u007f')) continue;
            var raw = _defaults?.ReadDefault(loc.Hive, loc.View, parent + "\\" + exeName);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var id = $"apppath:{loc.Scope}:{loc.ViewName}:{exeName}";
            if (!resolver.TryResolve(raw, out var exe, out _))
            {
                list.Add(Unknown(id, exeName, "Путь к программе не разобран", ReasonCodes.CommandUnresolved, raw));
                continue;
            }
            if (files.FileExists(exe)) continue;
            if (!files.DriveExists(exe))
            {
                list.Add(Unknown(id, exeName, "Диск с программой сейчас недоступен", ReasonCodes.DriveMissing, exe));
                continue;
            }
            list.Add(Make(id, exeName, $"Запись быстрого запуска ведёт на {exe}, а файла там нет",
                VerdictKind.Remove, [ReasonCodes.Make(ReasonCodes.EvidenceFileMissing, exe)], Confidence.L2, ChangeClass.Modifiable,
                new Explanation(
                    $"Запись о том, где лежит программа «{exeName}»: по ней Windows находит программу из окна «Выполнить».",
                    "Нужна, только пока файл на месте.",
                    "Имя перестанет находиться по записи. Файла нет, поэтому запустить через неё всё равно нельзя.",
                    "Копия записи сохраняется в журнале Чистовика: раздел «Журнал», кнопка «Отменить».")));
        }
    }

    // ---------- R3: Run / RunOnce ----------

    void ScanRun(Loc loc, string leaf, List<Finding> list)
    {
        var path = loc.Path(leaf);
        var values = registry.ReadValues(loc.Hive, loc.View, path);
        if (values is null) return;
        var where = loc.Scope == "user" ? "hku" : loc.ViewName == "x86" ? "hklm32" : "hklm64";
        foreach (var (valueName, value) in values)
        {
            if (value is not string command || string.IsNullOrWhiteSpace(command)) continue;
            var id = $"{leaf.ToLowerInvariant()}:{where}:{valueName}";
            if (valueName.Length is 0 or > MaxNameLength || valueName.Any(c => c < ' ' || c == '\u007f')) continue;
            if (leaf == "Run" && !StartupEntryId.TryParse(id, out _, out _)) continue;

            if (!resolver.TryResolve(command, out var exe, out _))
            {
                list.Add(Unknown(id, valueName, "Команду запуска не удалось разобрать", ReasonCodes.CommandUnresolved, command));
                continue;
            }
            if (files.FileExists(exe)) continue;
            if (!files.DriveExists(exe))
            {
                list.Add(Unknown(id, valueName, "Диск с программой сейчас недоступен", ReasonCodes.DriveMissing, exe));
                continue;
            }
            list.Add(Make(id, valueName, $"Запускает {exe}, а файла там нет",
                VerdictKind.Remove, [ReasonCodes.Make(ReasonCodes.EvidenceFileMissing, exe)], Confidence.L2, ChangeClass.Modifiable,
                new Explanation(
                    $"Запись «{valueName}» в {(leaf == "Run" ? "автозапуске" : "разовом автозапуске")} реестра: Windows пытается её выполнить при входе.",
                    "Нужна, только пока файл на месте. Сейчас запускать нечего.",
                    "При входе Windows перестанет безуспешно искать файл. Ничего полезного не пропадёт.",
                    "Копия записи сохраняется в журнале Чистовика: раздел «Журнал», кнопка «Отменить».")));
        }
    }

    // ---------- R4: SharedDLLs ----------

    void ScanSharedDlls(Loc loc, List<Finding> list, CancellationToken ct)
    {
        var values = registry.ReadValues(loc.Hive, loc.View, loc.Path("SharedDLLs"));
        if (values is null) return;
        var taken = 0;
        foreach (var valueName in values.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (taken >= MaxSharedDll) break;
            if (string.IsNullOrWhiteSpace(valueName) || valueName.Any(c => c < ' ')) continue;
            var id = $"shareddll:{loc.ViewName}:{Hash(valueName)}";
            var file = Path.GetFileName(valueName.TrimEnd('\\'));
            var title = string.IsNullOrEmpty(file) ? valueName : file;

            if (valueName.StartsWith(@"\\", StringComparison.Ordinal))
            {
                list.Add(Unknown(id, title, "Сетевой путь: проверить файл безопасно нельзя", ReasonCodes.DriveMissing, valueName));
                taken++;
                continue;
            }
            if (valueName.Length < 3 || !char.IsAsciiLetter(valueName[0]) || valueName[1] != ':' || valueName[2] != '\\') continue;
            if (files.FileExists(valueName) || files.DirectoryExists(valueName)) continue;
            if (!files.DriveExists(valueName))
            {
                list.Add(Unknown(id, title, "Диск с этим файлом сейчас недоступен", ReasonCodes.DriveMissing, valueName));
                taken++;
                continue;
            }
            list.Add(Make(id, title, $"Счётчик общего файла {valueName}, а файла нет",
                VerdictKind.Remove, [ReasonCodes.Make(ReasonCodes.EvidenceFileMissing, valueName)], Confidence.L2, ChangeClass.Modifiable,
                new Explanation(
                    $"Запись о том, что файл «{title}» используют несколько программ (счётчик общих файлов).",
                    "Нужна только для установщиков: они по ней решают, можно ли удалить общий файл.",
                    "Счётчик пропадёт. Файла на диске уже нет, на работу программ это не повлияет.",
                    "Копия записи сохраняется в журнале Чистовика: раздел «Журнал», кнопка «Отменить».")));
            taken++;
        }
    }

    // ---------- сборка ----------

    static Finding Unknown(string id, string title, string detail, string code, string evidence)
        => Make(id, title, detail, VerdictKind.Unknown, [ReasonCodes.Make(code, evidence)], Confidence.L4, ChangeClass.ReadOnly,
            new Explanation(
                "Запись реестра, про которую точно сказать нельзя, лишняя она или нет.",
                "Возможно, нужна: файл может быть на отключённом диске или команда записана необычно.",
                "Ничего не меняем, пока нет уверенности.",
                "Менять нечего."));

    static Finding Make(string id, string title, string detail, VerdictKind kind, IReadOnlyList<Reason> reasons,
        Confidence confidence, ChangeClass cls, Explanation explanation)
        => FindingFactory.Make(id, FindingArea.Registry, title, detail, kind, reasons, Risk.Low,
            kind == VerdictKind.Remove ? Reversibility.Undoable : Reversibility.NotAvailable, confidence, explanation, cls);

    static string Str(IReadOnlyDictionary<string, object?> v, string name)
        => v.TryGetValue(name, out var o) && o is string s ? s : "";

    static int Dword(IReadOnlyDictionary<string, object?> v, string name)
        => v.TryGetValue(name, out var o) && o is int i ? i : 0;

    static bool IdPart(string key) => key.Length is > 0 and <= MaxNameLength && !key.Contains('\\') && !key.Any(c => c < ' ' || c == '\u007f');

    static bool IsGuid(string key) => key.Length == 38 && key[0] == '{' && key[^1] == '}' && Guid.TryParse(key, out _);

    static bool IsMsiExec(string command)
    {
        var c = command.TrimStart().TrimStart('"');
        return c.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)
               || c.Contains(@"\msiexec", StringComparison.OrdinalIgnoreCase);
    }

    static string Hash(string valueName)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valueName.ToLowerInvariant()))).ToLowerInvariant();
}
