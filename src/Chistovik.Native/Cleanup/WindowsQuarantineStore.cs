using System.Text.RegularExpressions;
using Chistovik.Core.Ports;

namespace Chistovik.Native.Cleanup;

/// <summary>
/// .reg-копии удалённых ключей реестра в %ProgramData%\Chistovik\quarantine: подкаталог защищённого каталога агента, права наследуются
/// от него (как у журналов очистки). Принимает только пути вида «&lt;opId:N&gt;\&lt;n&gt;.reg»; каталог и подкаталог операции не должны быть
/// точками повторной обработки. Запись через временный файл и замену. ПРОВЕРИТЬ В SANDBOX: на хосте не запускалась.
/// </summary>
public sealed partial class WindowsQuarantineStore(string directory) : IQuarantineStore
{
    [GeneratedRegex(@"^([0-9a-fA-F]{32})\\([1-9][0-9]{0,5})\.reg$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePath();

    private static bool IsReparse(string path)
    {
        var attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    private (string OpDir, string File)? Resolve(string relativePath)
    {
        if (relativePath is null) return null;
        var m = RelativePath().Match(relativePath);
        if (!m.Success) return null;
        var opDir = Path.Combine(directory, m.Groups[1].Value.ToLowerInvariant());
        return (opDir, Path.Combine(opDir, m.Groups[2].Value + ".reg"));
    }

    private void EnsureDirectories(string opDir)
    {
        Directory.CreateDirectory(directory);
        if (IsReparse(directory)) throw new IOException("Каталог копий является точкой повторной обработки.");
        StoreDirectoryGuard.Require(directory);
        Directory.CreateDirectory(opDir);
        if (IsReparse(opDir)) throw new IOException("Каталог копий операции является точкой повторной обработки.");
        StoreDirectoryGuard.Require(opDir);
    }

    public void WriteFile(string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var r = Resolve(relativePath) ?? throw new ArgumentException("Недопустимый путь копии.", nameof(relativePath));
        EnsureDirectories(r.OpDir);
        var tmp = r.File + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, r.File, overwrite: true);
    }

    public bool Exists(string relativePath)
        => Resolve(relativePath) is { } r && File.Exists(r.File);

    public void Delete(string relativePath)
    {
        if (Resolve(relativePath) is not { } r) throw new ArgumentException("Недопустимый путь копии.", nameof(relativePath));
        if (File.Exists(r.File)) File.Delete(r.File);
    }

    public IReadOnlyList<Guid> ListOperations()
    {
        var ids = new List<Guid>();
        if (!Directory.Exists(directory)) return ids;
        foreach (var d in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(d);
            if (name.Length == 32 && Guid.TryParseExact(name, "N", out var id)) ids.Add(id);
        }
        return ids;
    }

    public void DeleteOperation(Guid operationId)
    {
        var opDir = Path.Combine(directory, operationId.ToString("N"));
        if (!Directory.Exists(opDir)) return;
        // Ссылку удаляем как ссылку (без захода внутрь), обычный каталог целиком.
        if (IsReparse(opDir)) Directory.Delete(opDir, recursive: false);
        else Directory.Delete(opDir, recursive: true);
    }
}
