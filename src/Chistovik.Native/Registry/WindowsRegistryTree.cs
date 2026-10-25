using Chistovik.Core.Ports;

namespace Chistovik.Native.Registry;

/// <summary>Заглушка T0: реализация (REG_OPTION_OPEN_LINK, NtDeleteKey) появится в T4.</summary>
public sealed class WindowsRegistryTree : IRegistryTree
{
    public IRegTreeHandle? OpenTree(RegLocation loc, bool forWrite) => throw new NotSupportedException("T4");
}
