using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Пустышка T0 (полноценные фейки с деревом, ссылками и подменой путей делает T3).</summary>
public sealed class FakeCleanupFiles : ICleanupFiles
{
    public FileFacts Inspect(string path) => new(false, false, null, null, []);
    public IEnumerable<DirEntryFacts> Enumerate(string directory) => [];
    public IDeletableHandle? OpenForDelete(string path, bool isDirectory) => null;
}
