using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Sandbox.Live;

/// <summary>Запуск внешних утилит (sc, powercfg, schtasks) для независимой проверки: результат не зависит от кода Чистовика.</summary>
static class Proc
{
    public static (int Exit, string Output) Run(string file, IEnumerable<string> args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + file);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, "timeout");
        }
        return (p.ExitCode, so.Result + se.Result);
    }

    /// <summary>Как Run, но со строкой аргументов «как есть» (нужно для runas: он сам разбирает кавычки).</summary>
    public static (int Exit, string Output) RunRaw(string file, string rawArguments, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(file, rawArguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + file);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, "timeout");
        }
        return (p.ExitCode, so.Result + se.Result);
    }

    public static (int Exit, string Output) PowerShell(string command)
        => Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command], 120000);
}

/// <summary>Состояние значения реестра, прочитанное напрямую через Microsoft.Win32 (в обход портов Чистовика).</summary>
public sealed record RegSnap(bool Exists, RegistryValueKind Kind, object? Raw)
{
    public string Text => !Exists ? "<нет значения>" : Raw switch
    {
        int i => unchecked((uint)i).ToString(),
        byte[] b => Convert.ToHexString(b),
        string[] m => string.Join("|", m),
        null => "",
        _ => Raw.ToString() ?? "",
    };

    public bool SameAs(RegSnap o) => Exists == o.Exists && (!Exists || (Kind == o.Kind && Text == o.Text));

    public override string ToString() => Exists ? $"{Kind}:{Text}" : "<нет значения>";
}

static class Reg
{
    static RegistryKey Base(bool user) => RegistryKey.OpenBaseKey(user ? RegistryHive.Users : RegistryHive.LocalMachine, RegistryView.Registry64);

    public static string Full(bool user, string sid, string key) => user ? sid + "\\" + key : key;

    public static bool KeyExists(bool user, string sid, string key)
    {
        using var b = Base(user);
        using var k = b.OpenSubKey(Full(user, sid, key));
        return k is not null;
    }

    public static RegSnap Read(bool user, string sid, string key, string name)
    {
        using var b = Base(user);
        using var k = b.OpenSubKey(Full(user, sid, key));
        if (k is null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new RegSnap(false, RegistryValueKind.None, null);
        return new RegSnap(true, k.GetValueKind(name), k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    public static void SetRaw(bool user, string sid, string key, string name, object value, RegistryValueKind kind)
    {
        using var b = Base(user);
        using var k = b.CreateSubKey(Full(user, sid, key), true);
        k.SetValue(name, value, kind);
    }

    public static void Delete(bool user, string sid, string key, string name)
    {
        using var b = Base(user);
        using var k = b.OpenSubKey(Full(user, sid, key), true);
        k?.DeleteValue(name, false);
    }

    public static void DeleteTree(bool user, string sid, string key)
    {
        using var b = Base(user);
        b.DeleteSubKeyTree(Full(user, sid, key), false);
    }

    /// <summary>Возвращает значение к снимку (нет значения: удаляет).</summary>
    public static void Restore(bool user, string sid, string key, string name, RegSnap snap)
    {
        if (!snap.Exists) { Delete(user, sid, key, name); return; }
        SetRaw(user, sid, key, name, snap.Raw!, snap.Kind);
    }
}

/// <summary>Символьные ссылки реестра (REG_LINK). Нужны только для проверки, что запись через ссылку HKU -> HKLM не проходит.</summary>
static class RegLink
{
    static readonly IntPtr HkeyUsers = new(unchecked((int)0x80000003));
    const int OptionVolatile = 1, OptionCreateLink = 2, OptionOpenLink = 8;
    const int KeyAllAccess = 0xF003F, DeleteRight = 0x10000;
    const int RegLinkType = 6;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegCreateKeyExW(IntPtr hKey, string subKey, int reserved, string? cls, int options, int sam, IntPtr sec, out IntPtr result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegSetValueExW(IntPtr hKey, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegOpenKeyExW(IntPtr hKey, string subKey, int options, int sam, out IntPtr result);

    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr hKey);

    [DllImport("ntdll.dll")]
    static extern int NtDeleteKey(IntPtr hKey);

    static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    const int Wow64Key = 0x100;

    /// <summary>Создаёт в HKU ключ-ссылку (volatile) на путь вида \Registry\Machine\... Возвращает код Win32 (0 = создано).</summary>
    public static int CreateInUsers(string pathUnderUsers, string targetNtPath)
        => Create(HkeyUsers, pathUnderUsers, targetNtPath, OptionCreateLink | OptionVolatile, KeyAllAccess);

    /// <summary>Создаёт в HKLM (64-битное представление) ключ-ссылку на путь вида \Registry\Machine\... Родитель должен существовать. 0 = создано.</summary>
    public static int CreateInMachine(string pathUnderMachine, string targetNtPath)
        => Create(HkeyLocalMachine, pathUnderMachine, targetNtPath, OptionCreateLink, KeyAllAccess | Wow64Key);

    static int Create(IntPtr root, string path, string targetNtPath, int options, int sam)
    {
        var rc = RegCreateKeyExW(root, path, 0, null, options, sam, IntPtr.Zero, out var h, out _);
        if (rc != 0) return rc;
        try
        {
            var data = Encoding.Unicode.GetBytes(targetNtPath);
            return RegSetValueExW(h, "SymbolicLinkValue", 0, RegLinkType, data, data.Length);
        }
        finally { RegCloseKey(h); }
    }

    /// <summary>Удаляет саму ссылку (ключ открывается с REG_OPTION_OPEN_LINK, иначе удалилась бы цель).</summary>
    public static int DeleteInUsers(string pathUnderUsers) => Delete(HkeyUsers, pathUnderUsers, DeleteRight);

    /// <summary>Удаляет ссылку в HKLM, а не её цель.</summary>
    public static int DeleteInMachine(string pathUnderMachine) => Delete(HkeyLocalMachine, pathUnderMachine, DeleteRight | Wow64Key);

    static int Delete(IntPtr root, string path, int sam)
    {
        var rc = RegOpenKeyExW(root, path, OptionOpenLink, sam, out var h);
        if (rc != 0) return rc;
        try { return NtDeleteKey(h) == 0 ? 0 : -1; }
        finally { RegCloseKey(h); }
    }
}
