using System.Globalization;
using System.Text.RegularExpressions;
using Chistovik.Core.Policy;

namespace Chistovik.Core.Inventory;

/// <summary>
/// Инвентарь программ из веток Uninstall. Порт tools/inventory.ps1 (раздел ARP), только чтение.
/// Расхождения с inventory.ps1:
///  - обновления (ParentKeyName, «Update for», «Security Update», KBnnnnnn) не отбрасываются, а помечаются IsUpdate
///    (скрипт их пропускает); SystemComponent, как и в скрипте, только помечается;
///  - дедупликация x64/x86 по (имя, версия, издатель) — в скрипте её нет, там записи разных ветвей остаются раздельными;
///  - Appx/MSIX здесь не читаются (ProgramSource.Msix зарезервирован); nonRemovable/arch/scope в модель не вынесены.
/// </summary>
public sealed class UninstallInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // «Update for ... (KB123456)», «Security Update ...», «KB123456...», «... (KB123456)» в конце
    private static readonly Regex UpdateName = new(
        @"^(Security )?Update for |Security Update|^KB\d{6,}|\(KB\d{6,}\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> UpdateReleaseTypes =
        new(["Update", "Hotfix", "Security Update", "Service Pack"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex GuidKey = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IRegistryReader _reader;

    public UninstallInventory(IRegistryReader reader) => _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public IReadOnlyList<InstalledProgram> Read()
    {
        var all = new List<InstalledProgram>();
        all.AddRange(ReadRoot(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, UninstallPath, ProgramSource.Uninstall64, "machine", "x64", @"HKLM\" + UninstallPath));
        all.AddRange(ReadRoot(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, UninstallPath32, ProgramSource.Uninstall32, "machine", "x86", @"HKLM\" + UninstallPath32));
        all.AddRange(ReadRoot(RegistryHiveKind.CurrentUser, RegistryViewKind.Registry64, UninstallPath, ProgramSource.UninstallHkcu, "user", "user", @"HKCU\" + UninstallPath));
        return Deduplicate(all);
    }

    private IEnumerable<InstalledProgram> ReadRoot(RegistryHiveKind hive, RegistryViewKind view, string path,
        ProgramSource source, string scope, string arch, string displayRoot)
    {
        foreach (var key in _reader.GetSubKeyNames(hive, view, path))
        {
            var values = _reader.ReadValues(hive, view, path + "\\" + key);
            if (values is null) continue;

            var name = Str(values, "DisplayName");
            if (name.Length == 0) continue;

            var parent = Str(values, "ParentKeyName");
            var releaseType = Str(values, "ReleaseType");
            var msi = Num(values, "WindowsInstaller") == 1;
            var fullPath = displayRoot + "\\" + key;
            var size = Num(values, "EstimatedSize");
            yield return new InstalledProgram
            {
                Id = $"arp:{scope}:{arch}:{key}",
                DisplayName = name,
                DisplayVersion = Str(values, "DisplayVersion"),
                Publisher = Str(values, "Publisher"),
                InstallLocation = Str(values, "InstallLocation"),
                DisplayIcon = Str(values, "DisplayIcon"),
                UninstallString = Str(values, "UninstallString"),
                QuietUninstallString = Str(values, "QuietUninstallString"),
                IsMsi = msi,
                ProductCode = msi && GuidKey.IsMatch(key) ? key : null,
                Source = source,
                IsSystemComponent = values.TryGetValue("SystemComponent", out var sc) && RegFlag.IsSet(sc),
                NoRemove = values.TryGetValue("NoRemove", out var nr) && RegFlag.IsSet(nr),
                IsUpdate = parent.Length > 0 || UpdateName.IsMatch(name) || UpdateReleaseTypes.Contains(releaseType),
                ReleaseType = releaseType,
                ParentKeyName = parent,
                EstimatedSizeKb = size is > 0 ? size : null,
                InstallDate = Date(Str(values, "InstallDate")),
                RegistryKeyPath = fullPath,
                RegistryKeyPaths = new[] { fullPath },
            };
        }
    }

    // Склеиваем только x64+x86 (HKLM); записи HKCU остаются отдельными.
    private static List<InstalledProgram> Deduplicate(List<InstalledProgram> all)
    {
        var result = new List<InstalledProgram>(all.Count);
        var index = new Dictionary<(string, string, string), int>();
        foreach (var p in all)
        {
            if (p.Source is ProgramSource.Uninstall64 or ProgramSource.Uninstall32)
            {
                var k = (p.DisplayName.ToUpperInvariant(), p.DisplayVersion.ToUpperInvariant(), p.Publisher.ToUpperInvariant());
                if (index.TryGetValue(k, out var i))
                {
                    var first = result[i];
                    result[i] = first with { RegistryKeyPaths = first.RegistryKeyPaths.Concat(p.RegistryKeyPaths).ToArray() };
                    continue;
                }
                index[k] = result.Count;
            }
            result.Add(p);
        }
        return result;
    }

    private static string Str(IReadOnlyDictionary<string, object?> v, string name) =>
        v.TryGetValue(name, out var o) ? o switch
        {
            string s => s.Trim(),
            int i => ((uint)i).ToString(CultureInfo.InvariantCulture),
            uint u => u.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => "",
        } : "";

    private static long? Num(IReadOnlyDictionary<string, object?> v, string name) =>
        !v.TryGetValue(name, out var o) ? null : o switch
        {
            int i => (uint)i,
            uint u => u,
            long l => l,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };

    private static DateOnly? Date(string s) =>
        s.Length == 8 && DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
