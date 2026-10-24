using Chistovik.Core.Execution;

namespace Chistovik.Core.Ports;

public sealed record ServiceRaw(string Name, uint TypeFlags, uint StartType, bool Delayed, string? ImagePath,
    bool LaunchProtected, int DependentCount, int TriggerCount, bool Running);

public interface IServiceControl
{
    IReadOnlyList<string> ListNames();
    IServiceHandle? Open(string name, bool write);
}

public interface IServiceHandle : IDisposable
{
    ServiceRaw Query();
    void SetStart(ApprovedServiceChange c);
}
