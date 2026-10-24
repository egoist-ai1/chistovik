using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённая запись значения реестра; Value.Exists=false означает удаление значения. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedRegistryWrite
{
    public RegLocation Location { get; }
    public RegValueState Value { get; }

    internal ApprovedRegistryWrite(RegLocation location, RegValueState value)
    {
        Location = location;
        Value = value;
    }
}
