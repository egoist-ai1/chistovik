using Chistovik.Core.Ports;

namespace Chistovik.Native.Cleanup;

/// <summary>
/// Журналы очистки в %ProgramData%\Chistovik\cleanup: подкаталог защищённого каталога агента, права наследуются от него.
/// Принимает только простое имя файла; запись через временный файл и замену.
/// </summary>
public sealed class WindowsCleanupLogStore(string directory) : ICleanupLogStore
{
    public void Write(string fileName, byte[] content)
    {
        if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.StartsWith('.'))
            throw new ArgumentException("Нужно простое имя файла.", nameof(fileName));

        Directory.CreateDirectory(directory);
        StoreDirectoryGuard.Require(directory);
        var target = Path.Combine(directory, fileName);
        var tmp = target + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, target, overwrite: true);
    }
}
