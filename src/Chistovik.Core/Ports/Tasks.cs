using Chistovik.Core.Execution;

namespace Chistovik.Core.Ports;

/// <summary>Kind: Exec | ComHandler | Other.</summary>
public sealed record TaskAction(string Kind, string? Path, string? Args);

/// <summary>UserSid: SID учётной записи задачи (Native разрешает имя через NTAccount → SID); null, если не разрешилось.</summary>
public sealed record TaskRaw(string Path, bool Enabled, string Xml, string? UserId, string RunLevel, string LogonType,
    IReadOnlyList<TaskAction> Actions, string? UserSid = null);

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
