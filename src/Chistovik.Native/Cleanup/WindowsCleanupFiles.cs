using Chistovik.Core.Ports;

namespace Chistovik.Native.Cleanup;

/// <summary>Заглушка T0: реализация (P/Invoke, удаление по хэндлу) появится в T4. Ничего не делает, бросает NotSupportedException.</summary>
public sealed class WindowsCleanupFiles : ICleanupFiles
{
    public FileFacts Inspect(string path) => throw new NotSupportedException("T4");
    public IEnumerable<DirEntryFacts> Enumerate(string directory) => throw new NotSupportedException("T4");
    public IDeletableHandle? OpenForDelete(string path, bool isDirectory) => throw new NotSupportedException("T4");
}
