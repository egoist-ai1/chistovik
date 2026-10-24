using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённая смена активной схемы питания. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedPowerScheme
{
    public Guid Scheme { get; }

    internal ApprovedPowerScheme(Guid scheme)
    {
        Scheme = scheme;
    }
}
