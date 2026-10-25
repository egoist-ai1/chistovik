using Chistovik.Core.Policy;
using Chistovik.Native.Journal;

namespace Chistovik.Native.Setup;

/// <summary>
/// Временный каталог для запуска деинсталлятора: %ProgramData%\Chistovik\tmp\&lt;guid&gt; с тем же защищённым ACL, что у журнала
/// (Users только читают). Деинсталляторы Inno и NSIS копируют себя в TEMP и дальше работают с правами администратора:
/// если бы TEMP был пользовательским, процесс без прав мог бы подложить рядом с копией свою библиотеку.
/// </summary>
internal static class ProtectedTemp
{
    public static string Create()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Chistovik");
        var tmp = Path.Combine(root, "tmp");
        var directory = new WindowsJournalDirectory();

        // Корень и tmp: либо создаём защищённо, либо существующий обязан пройти ту же проверку, что каталог журнала.
        foreach (var dir in new[] { root, tmp })
        {
            if (!directory.Inspect(dir).Exists) directory.CreateProtected(dir);
            var acl = JournalAclPolicy.Evaluate(directory.Inspect(dir));
            if (!acl.IsTrusted) throw new IOException("Временный каталог небезопасен: " + acl.Rule);
        }

        var op = Path.Combine(tmp, Guid.NewGuid().ToString("N"));
        directory.CreateProtected(op);
        return op;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Directory.Delete(path, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* занят деинсталлятором: уберётся при следующем запуске */ }
    }
}
