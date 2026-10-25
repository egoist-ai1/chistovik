using Chistovik.Core.Ports;

namespace Chistovik.Native.Cleanup;

/// <summary>Заглушка T0: реализация (SHQueryRecycleBin / SHEmptyRecycleBin) появится в T4.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public long QueryBytes() => throw new NotSupportedException("T4");
    public void Empty() => throw new NotSupportedException("T4");
    public string CurrentTokenSid() => throw new NotSupportedException("T4");
}
