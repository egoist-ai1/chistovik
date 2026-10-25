using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Журналы очистки в памяти: имя файла → содержимое.</summary>
public sealed class FakeCleanupLog : ICleanupLogStore
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public bool Throw { get; set; }

    public void Write(string fileName, byte[] content)
    {
        if (Throw) throw new IOException("диск недоступен");
        Files[fileName] = content;
    }
}
