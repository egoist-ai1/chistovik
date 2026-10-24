using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённое создание нашей схемы питания копированием шаблона под заранее выбранным GUID. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedSchemeCreate
{
    public Guid Template { get; }
    public Guid NewScheme { get; }
    public string Name { get; }

    internal ApprovedSchemeCreate(Guid template, Guid newScheme, string name)
    {
        Template = template;
        NewScheme = newScheme;
        Name = name;
    }
}
