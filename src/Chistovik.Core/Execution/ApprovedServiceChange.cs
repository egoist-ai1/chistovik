using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённая смена типа запуска службы. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedServiceChange
{
    public string Name { get; }
    public StartMode Start { get; }

    internal ApprovedServiceChange(string name, StartMode start)
    {
        Name = name;
        Start = start;
    }
}
