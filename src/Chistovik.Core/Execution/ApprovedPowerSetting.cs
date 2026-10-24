using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённая запись параметра схемы питания (значения от сети и от батареи). Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedPowerSetting
{
    public Guid Scheme { get; }
    public Guid Subgroup { get; }
    public Guid Setting { get; }
    public uint Ac { get; }
    public uint Dc { get; }

    internal ApprovedPowerSetting(Guid scheme, Guid subgroup, Guid setting, uint ac, uint dc)
    {
        Scheme = scheme;
        Subgroup = subgroup;
        Setting = setting;
        Ac = ac;
        Dc = dc;
    }
}
