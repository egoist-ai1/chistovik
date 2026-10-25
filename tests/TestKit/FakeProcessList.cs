using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Пустышка T0 (расширяется в T3): запущенных процессов нет.</summary>
public sealed class FakeProcessList : IProcessList
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning(string imageName, string userSid) => Running.Contains(imageName);
}
