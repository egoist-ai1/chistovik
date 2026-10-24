using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённое удаление нашей (созданной приложением, неактивной) схемы питания. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedSchemeDelete
{
    public Guid Scheme { get; }

    internal ApprovedSchemeDelete(Guid scheme)
    {
        Scheme = scheme;
    }
}
