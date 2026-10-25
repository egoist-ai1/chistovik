using Chistovik.Core.Ports;

namespace Chistovik.Native.Cleanup;

/// <summary>Заглушка T0: реализация появится в T4.</summary>
public sealed class WindowsProcessList : IProcessList
{
    public bool IsRunning(string imageName, string userSid) => throw new NotSupportedException("T4");
}
