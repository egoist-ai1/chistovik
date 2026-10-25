using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Пустышка T0 (обход дерева поверх FakeRegistry делает T3): ключей нет.</summary>
public sealed class FakeRegistryTree : IRegistryTree
{
    public IRegTreeHandle? OpenTree(RegLocation loc, bool forWrite) => null;
}
