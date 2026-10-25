using Chistovik.Core.Policy;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённое удаление программы. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedUninstall
{
    public string Id { get; }
    public UninstallCommand Command { get; }

    internal ApprovedUninstall(string id, UninstallCommand command)
    {
        Id = id;
        Command = command;
    }
}
