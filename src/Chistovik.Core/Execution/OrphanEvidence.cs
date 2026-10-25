using System.Text;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>
/// Результат повторной проверки «сироты», которую агент выполняет сам (App только предлагает идентификатор).
/// Verified: доказательство подтвердилось, Kind это uninstall-orphan | msi-unknown | file-missing | command-unresolved.
/// Не подтверждено: Rule называет причину (evidence-* или drive-missing), код шага evidence-failed.
/// Protected: запись системная (SystemComponent, обновление, WindowsApps…): код шага policy-denied.
/// </summary>
public sealed record OrphanEvidence(
    bool Verified, string Kind, IReadOnlyList<string> Paths, string? ProductCode, string? Rule = null, bool Protected = false,
    string? Display = null)
{
    public static OrphanEvidence Ok(string kind, IReadOnlyList<string> paths, string? productCode = null, string? display = null)
        => new(true, kind, paths, productCode, null, false, display);

    /// <summary>Доказательство не подтвердилось (или записи нельзя доверять без доказательства): Unknown/evidence-failed.</summary>
    public static OrphanEvidence Failed(string rule, string? display = null, IReadOnlyList<string>? paths = null)
        => new(false, string.Empty, paths ?? [], null, rule, false, display);

    /// <summary>Системная запись, которую не чистим никогда: policy-denied.</summary>
    public static OrphanEvidence Forbidden(string rule, string? display = null)
        => new(false, string.Empty, [], null, rule, true, display);

    public OrphanEvidenceJson ToJson() => new(Kind, Paths, ProductCode);
}

/// <summary>Разбор сырых значений реестра (REG_SZ, REG_DWORD) в текст и числа для проверок.</summary>
internal static class RegRaw
{
    /// <summary>Строка REG_SZ/REG_EXPAND_SZ: UTF-16LE до первого NUL; null для остальных типов.</summary>
    public static string? Text(RegType type, byte[] raw)
    {
        if (type is not (RegType.String or RegType.ExpandString)) return null;
        var s = Encoding.Unicode.GetString(raw, 0, raw.Length & ~1);
        var nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    public static uint? DWord(RegType type, byte[] raw)
        => type == RegType.DWord && raw.Length == 4 ? BitConverter.ToUInt32(raw, 0) : null;

    public static byte[] FromText(string s) => Encoding.Unicode.GetBytes(s + "\0");
}

/// <summary>
/// Перепроверка доказательств R1–R4 по свежему состоянию реестра и диска (docs/C2C3-DESIGN.md §1.2). Ничего не пишет.
/// Отрицательный исход всегда безопасен: запись остаётся на месте.
/// </summary>
internal static class OrphanProbe
{
    static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "explorer.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "regsvr32.exe", "msiexec.exe", "conhost.exe", "start.exe", "schtasks.exe", "reg.exe", "wmic.exe", "forfiles.exe",
        "bash.exe", "wsl.exe", "sh.exe", "python.exe", "pythonw.exe", "py.exe", "node.exe", "java.exe", "javaw.exe", "dotnet.exe",
    };

    static readonly HashSet<string> ReleaseTypes = new(StringComparer.OrdinalIgnoreCase) { "Update", "Hotfix", "Security Update" };

    static CommandResolver Resolver(ExecEnv env) => new(env.Gate.Context);

    static bool Exists(ExecEnv env, string path) => env.Security.Inspect(path).Exists;

    /// <summary>Диск из пути (буква) существует.</summary>
    static bool DriveExists(ExecEnv env, string normalizedPath) => Exists(env, normalizedPath[..3]);

    static Dictionary<string, (RegType Type, byte[] Raw)> Values(IRegTreeHandle key)
    {
        var d = new Dictionary<string, (RegType, byte[])>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (name, type, raw) in key.RawValues(RegistryTreeHasher.MaxBytes)) d[name] = (type, raw);
        }
        catch (RegValueTooLargeException)
        {
            // Ключ с гигантским значением не читаем целиком: доказательства нет (evidence-failed), дерево не трогаем.
            d.Clear();
        }
        return d;
    }

    static string? Text(Dictionary<string, (RegType Type, byte[] Raw)> v, string name)
        => v.TryGetValue(name, out var x) && RegRaw.Text(x.Type, x.Raw) is { Length: > 0 } s ? s : null;

    static bool Flag(Dictionary<string, (RegType Type, byte[] Raw)> v, string name)
        => v.TryGetValue(name, out var x) && RegRaw.DWord(x.Type, x.Raw) is { } u && u != 0;

    static bool UnderWindows(ExecEnv env, string path)
    {
        var win = env.Gate.Context.WindowsDir;
        if (!PathNormalizer.TryNormalize(win, out var w, out _)) return true;
        if (PathNormalizer.IsEqualOrUnder(path, w)) return true;
        return PathNormalizer.Components(path).Any(c => c.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Каталог установки из InstallLocation: кавычки снимаются, известные переменные раскрываются.</summary>
    static bool TryDir(ExecEnv env, string raw, out string path)
        => Resolver(env).TryResolve("\"" + raw.Trim().Trim('"') + "\"", out path, out _);

    // ---------- R1: Uninstall\<key> ----------

    public static OrphanEvidence Uninstall(ExecEnv env, IRegTreeHandle key, OrphanId id)
    {
        var v = Values(key);
        var display = Text(v, "DisplayName");
        if (string.IsNullOrWhiteSpace(display)) return OrphanEvidence.Failed("arp-no-display-name");
        // Имя для людей хранится в журнале: управляющие символы не нужны.
        display = display.Length > 120 ? display[..120] : display;

        if (Flag(v, "SystemComponent")) return OrphanEvidence.Forbidden("arp-system-component", display);
        if (Text(v, "ParentKeyName") is not null) return OrphanEvidence.Forbidden("arp-parent-key", display);
        if (Text(v, "ReleaseType") is { } rt && ReleaseTypes.Contains(rt.Trim())) return OrphanEvidence.Forbidden("arp-update", display);

        var paths = new List<string>();
        if (Text(v, "InstallLocation") is { } loc)
        {
            if (!TryDir(env, loc, out var dir)) return OrphanEvidence.Failed("evidence-location-unresolved", display);
            if (UnderWindows(env, dir)) return OrphanEvidence.Forbidden("arp-windows-location", display);
            if (Exists(env, dir)) return OrphanEvidence.Failed("evidence-install-dir-exists", display, [dir]);
            if (!DriveExists(env, dir)) return OrphanEvidence.Failed("drive-missing", display, [dir]);
            paths.Add(dir);
        }

        // MSI: продукт, который Windows Installer не знает вообще (-1), и установленный продукт (3, 4, 5).
        string? productCode = null;
        if (Flag(v, "WindowsInstaller") && ArpId.IsProductCode(id.Name))
        {
            productCode = id.Name;
            var state = env.Uninstaller.QueryMsiState(productCode);
            if (state is 3 or 4 or 5) return OrphanEvidence.Failed("evidence-msi-installed", display, paths);
            if (state == -1 && id.Scope == "machine") return OrphanEvidence.Ok("msi-unknown", paths, productCode, display);
        }

        var resolver = Resolver(env);
        var any = false;
        foreach (var name in new[] { "UninstallString", "QuietUninstallString" })
        {
            var cmd = Text(v, name);
            if (cmd is null) continue;
            if (!resolver.TryResolve(cmd, out var exe, out _))
            {
                // Единственная команда, которую нельзя разобрать, это не доказательство: msiexec и подобное остаются как есть.
                if (name == "UninstallString") return OrphanEvidence.Failed("evidence-uninstaller-unresolved", display, paths);
                continue;
            }
            if (Exists(env, exe)) return OrphanEvidence.Failed("evidence-uninstaller-exists", display, [exe]);
            if (!DriveExists(env, exe)) return OrphanEvidence.Failed("drive-missing", display, [exe]);
            paths.Add(exe);
            any = true;
        }
        if (!any) return OrphanEvidence.Failed("evidence-no-uninstaller", display, paths);
        return OrphanEvidence.Ok("uninstall-orphan", paths, productCode, display);
    }

    // ---------- R2: App Paths\<exe> ----------

    public static OrphanEvidence AppPath(ExecEnv env, IRegTreeHandle key)
    {
        var v = Values(key);
        var cmd = Text(v, "");
        if (cmd is null) return OrphanEvidence.Failed("apppath-no-default");
        if (!Resolver(env).TryResolve(cmd, out var exe, out _)) return OrphanEvidence.Failed("apppath-unresolved");
        if (Exists(env, exe)) return OrphanEvidence.Failed("evidence-file-exists", null, [exe]);
        if (!DriveExists(env, exe)) return OrphanEvidence.Failed("drive-missing", null, [exe]);
        return OrphanEvidence.Ok("file-missing", [exe]);
    }

    // ---------- R3: Run / RunOnce ----------

    public static OrphanEvidence RunValue(ExecEnv env, OrphanId id, RegValueState value)
    {
        if (value is not { Exists: true, Type: RegType.String or RegType.ExpandString, Data: string cmd } || string.IsNullOrWhiteSpace(cmd))
            return OrphanEvidence.Failed("run-no-command");
        if (id.Name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase)) return OrphanEvidence.Forbidden("run-system-entry");
        if (!Resolver(env).TryResolve(cmd, out var exe, out _)) return OrphanEvidence.Failed("run-unresolved");
        var file = exe[(exe.LastIndexOf('\\') + 1)..];
        if (Launchers.Contains(file)) return OrphanEvidence.Failed("run-launcher", null, [exe]);
        if (UnderWindows(env, exe)) return OrphanEvidence.Forbidden("run-system-entry");
        if (PathNormalizer.IsEqualOrUnder(exe, Norm(env.Gate.Context.ProgramFiles) + @"\Windows Defender")) return OrphanEvidence.Forbidden("run-system-entry");
        if (Exists(env, exe)) return OrphanEvidence.Failed("evidence-command-exists", null, [exe]);
        if (!DriveExists(env, exe)) return OrphanEvidence.Failed("drive-missing", null, [exe]);
        return OrphanEvidence.Ok("command-unresolved", [exe]);
    }

    static string Norm(string raw) => PathNormalizer.TryNormalize(raw, out var n, out _) ? n : raw;

    // ---------- R4: SharedDLLs ----------

    /// <summary>valueName это имя значения, чей SHA-256 назван в идентификаторе; null: такого значения больше нет (находка исчезла).</summary>
    public static (string? ValueName, OrphanEvidence Evidence) SharedDll(ExecEnv env, OrphanId id, IRegKeyHandle key)
    {
        string? name = null;
        foreach (var n in key.ValueNames())
            if (OrphanId.SharedDllHash(n) == id.Name) { name = n; break; }
        if (name is null) return (null, OrphanEvidence.Failed("finding-gone"));

        if (name.StartsWith(@"\\", StringComparison.Ordinal)) return (name, OrphanEvidence.Failed("shareddll-unc"));
        if (!PathNormalizer.TryNormalize(name, out var path, out _)) return (name, OrphanEvidence.Failed("shareddll-unresolved"));
        if (Exists(env, path)) return (name, OrphanEvidence.Failed("evidence-file-exists", null, [path]));
        if (!DriveExists(env, path)) return (name, OrphanEvidence.Failed("drive-missing", null, [path]));
        return (name, OrphanEvidence.Ok("file-missing", [path]));
    }
}
