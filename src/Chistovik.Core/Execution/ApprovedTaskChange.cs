using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Разрешённое включение или выключение задачи планировщика. Конструктор internal: создаёт только StepGate после решения Policy (§2).</summary>
public sealed class ApprovedTaskChange
{
    public string Path { get; }
    public bool Enabled { get; }

    internal ApprovedTaskChange(string path, bool enabled)
    {
        Path = path;
        Enabled = enabled;
    }
}
