using System.Runtime.InteropServices;
using System.Security.Principal;
using Chistovik.Core.Execution;
using Chistovik.Core.Ports;

namespace Chistovik.Native.Tasks;

/// <summary>
/// Планировщик заданий Windows через COM Task Scheduler 2.0 (ProgID Schedule.Service, позднее связывание через dynamic):
/// без interop-сборок и без NuGet. Только чтение списка/свойств и переключение Enabled.
/// </summary>
public sealed class WindowsTaskScheduler : ITaskScheduler
{
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int TaskEnumHidden = 1;

    public IReadOnlyList<string> ListPaths()
    {
        var service = Connect();
        try
        {
            var result = new List<string>();
            Walk(service.GetFolder("\\"), result);
            return result;
        }
        finally { ReleaseCom(service); }
    }

    public ITaskHandle? Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var service = Connect();
        try
        {
            object task;
            try { task = service.GetFolder("\\").GetTask(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException || (e is COMException && e.HResult is HResultFileNotFound or HResultPathNotFound))
            {
                ReleaseCom(service);
                return null;
            }
            return new WindowsTaskHandle(service, task, path);
        }
        catch
        {
            ReleaseCom(service);
            throw;
        }
    }

    internal static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        try { service.Connect(); }
        catch { ReleaseCom(service); throw; }
        return service;
    }

    internal static void ReleaseCom(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }

    private static void Walk(dynamic folder, List<string> result)
    {
        try
        {
            dynamic tasks = folder.GetTasks(TaskEnumHidden);
            try
            {
                int n = tasks.Count;
                for (var i = 1; i <= n; i++)
                {
                    dynamic t = tasks.Item(i);
                    try { result.Add((string)t.Path); }
                    finally { ReleaseCom(t); }
                }
            }
            finally { ReleaseCom(tasks); }
        }
        catch (UnauthorizedAccessException) { /* папка закрыта для чтения: её задач нет в видимом списке */ }
        catch (COMException e) when (e.HResult == HResultAccessDenied) { }

        dynamic subs;
        try { subs = folder.GetFolders(0); }
        catch (UnauthorizedAccessException) { return; }
        catch (COMException e) when (e.HResult == HResultAccessDenied) { return; }
        try
        {
            int n = subs.Count;
            for (var i = 1; i <= n; i++)
            {
                dynamic sub = subs.Item(i);
                try { Walk(sub, result); }
                finally { ReleaseCom(sub); }
            }
        }
        finally { ReleaseCom(subs); }
    }
}

public sealed class WindowsTaskHandle : ITaskHandle
{
    private dynamic? _service;
    private dynamic? _task;
    private readonly string _path;

    internal WindowsTaskHandle(object service, object task, string path)
    {
        _service = service;
        _task = task;
        _path = path;
    }

    public TaskRaw Query()
    {
        var task = Task();
        try
        {
            bool enabled = task.Enabled;
            string xml = task.Xml;
            string path = task.Path;

            string? userId = null;
            string? userSid = null;
            var runLevel = "Limited";
            var logon = "None";
            var actions = new List<TaskAction>();

            dynamic def = task.Definition;
            try
            {
                dynamic principal = def.Principal;
                try
                {
                    string uid = principal.UserId ?? "";
                    userId = uid.Length == 0 ? null : uid;
                    userSid = ResolveSid(userId);
                    runLevel = (int)principal.RunLevel == 1 ? "Highest" : "Limited";
                    logon = LogonName((int)principal.LogonType);
                }
                finally { WindowsTaskScheduler.ReleaseCom(principal); }

                dynamic list = def.Actions;
                try
                {
                    int n = list.Count;
                    for (var i = 1; i <= n; i++)
                    {
                        dynamic a = list.Item(i);
                        try { actions.Add(ReadAction(a)); }
                        finally { WindowsTaskScheduler.ReleaseCom(a); }
                    }
                }
                finally { WindowsTaskScheduler.ReleaseCom(list); }
            }
            finally { WindowsTaskScheduler.ReleaseCom(def); }

            return new TaskRaw(path, enabled, xml, userId, runLevel, logon, actions, userSid);
        }
        catch (COMException e)
        {
            throw new InvalidOperationException($"Не удалось прочитать задачу '{_path}': {e.Message} (0x{e.HResult:X8}).", e);
        }
    }

    public void SetEnabled(ApprovedTaskChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!string.Equals(c.Path, _path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Разрешение выдано для задачи '{c.Path}', а хэндл открыт для '{_path}'.");
        var task = Task();
        try { task.Enabled = c.Enabled; }
        catch (COMException e)
        {
            throw new InvalidOperationException($"Не удалось изменить задачу '{_path}': {e.Message} (0x{e.HResult:X8}).", e);
        }
    }

    public void Dispose()
    {
        var t = (object?)_task;
        var s = (object?)_service;
        _task = null;
        _service = null;
        WindowsTaskScheduler.ReleaseCom(t);
        WindowsTaskScheduler.ReleaseCom(s);
    }

    private dynamic Task() => _task ?? throw new ObjectDisposedException(nameof(WindowsTaskHandle));

    private static TaskAction ReadAction(dynamic a)
    {
        // TASK_ACTION_TYPE: 0 EXEC, 5 COM_HANDLER, 6 SEND_EMAIL, 7 SHOW_MESSAGE.
        switch ((int)a.Type)
        {
            case 0:
                return new TaskAction("Exec", NullIfEmpty(a.Path), NullIfEmpty(a.Arguments));
            case 5:
                return new TaskAction("ComHandler", NullIfEmpty(a.ClassId), NullIfEmpty(a.Data));
            default:
                return new TaskAction("Other", null, null);
        }
    }

    /// <summary>SID учётной записи задачи: SID в тексте принимается как есть, имя разрешается через NTAccount; не разрешилось — null.</summary>
    internal static string? ResolveSid(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        var text = account.Trim();
        try
        {
            if (text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return new SecurityIdentifier(text).Value;
            return new NTAccount(text).Translate(typeof(SecurityIdentifier)) is SecurityIdentifier sid ? sid.Value : null;
        }
        catch (Exception e) when (e is SystemException) // IdentityNotMappedException, ArgumentException, Win32-сбои поиска
        {
            return null;
        }
    }

    private static string? NullIfEmpty(object? o) => o is string s && s.Length > 0 ? s : null;

    // TASK_LOGON_TYPE.
    private static string LogonName(int v) => v switch
    {
        0 => "None",
        1 => "Password",
        2 => "S4U",
        3 => "InteractiveToken",
        4 => "Group",
        5 => "ServiceAccount",
        6 => "InteractiveTokenOrPassword",
        _ => "Unknown",
    };
}
