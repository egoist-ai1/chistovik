using Chistovik.Core.Execution;

namespace Chistovik.Core.Ports;

/// <summary>Kind: Exec | ComHandler | Other.</summary>
public sealed record TaskAction(string Kind, string? Path, string? Args);

public sealed record TaskRaw(string Path, bool Enabled, string Xml, string? UserId, string RunLevel, string LogonType,
    IReadOnlyList<TaskAction> Actions);

public interface ITaskScheduler
{
    IReadOnlyList<string> ListPaths();
    ITaskHandle? Open(string path);
}

public interface ITaskHandle : IDisposable
{
    TaskRaw Query();
    void SetEnabled(ApprovedTaskChange c);
}
