using Chistovik.Core.Execution;

namespace Chistovik.Core.Ports;

public interface IPowerControl
{
    Guid GetActive();
    IReadOnlyList<Guid> ListSchemes();
    (uint Ac, uint Dc) Read(Guid scheme, Guid sub, Guid setting);
    void Write(ApprovedPowerSetting c);
    void SetActive(ApprovedPowerScheme c);
    void Duplicate(ApprovedSchemeCreate c);
    void Delete(ApprovedSchemeDelete c);
}
