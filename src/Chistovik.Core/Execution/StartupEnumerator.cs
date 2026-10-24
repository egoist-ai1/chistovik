using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Запись автозагрузки из собственного перечисления агента: команда (для папки путь файла) и расположение флага StartupApproved.</summary>
internal sealed record StartupEntry(StartupEntryId Id, string Command, string CommandSha256, StartupTarget Target, RegLocation ApprovedLocation);

/// <summary>
/// Собственное перечисление автозагрузки агента (DESIGN-0.1 §3): идентификатор из запроса только указывает, что искать.
/// Run читается через IRegistry (ResolvedPath и символическая ссылка проверяются), папки через IUserFolders и IFileSystem.
/// RunOnce и ярлыки целиком не разбираются: у папки командой считается путь файла (цель ярлыка в 0.1 не раскрывается).
/// </summary>
internal static class StartupEnumerator
{
    const string RunSuffix = @"Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedSuffix = @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static StartupEntry? Find(ExecEnv env, string clientSid, StartupEntryId id)
    {
        if (string.IsNullOrEmpty(clientSid)) return null;
        return id.IsRun ? FindRun(env, clientSid, id) : FindFolder(env, clientSid, id);
    }

    static StartupEntry? FindRun(ExecEnv env, string sid, StartupEntryId id)
    {
        RegLocation run;
        RegLocation approved;
        StartupTarget Target(string cmdSha, string root, string? s, string kind) => new(root, s, kind, id.Name, cmdSha);
        string root;
        string? targetSid;
        string kind;
        switch (id.Location)
        {
            case StartupLocation.Hklm64:
                run = new RegLocation(RegRoot.Machine, null, @"SOFTWARE\" + RunSuffix, id.Name);
                approved = new RegLocation(RegRoot.Machine, null, @"SOFTWARE\" + ApprovedSuffix + "Run", id.Name);
                (root, targetSid, kind) = ("HKLM", null, "Run");
                break;
            case StartupLocation.Hklm32:
                run = new RegLocation(RegRoot.Machine, null, @"SOFTWARE\WOW6432Node\" + RunSuffix, id.Name);
                approved = new RegLocation(RegRoot.Machine, null, @"SOFTWARE\" + ApprovedSuffix + "Run32", id.Name);
                (root, targetSid, kind) = ("HKLM", null, "Run32");
                break;
            default:
                run = new RegLocation(RegRoot.User, sid, @"Software\" + RunSuffix, id.Name);
                approved = new RegLocation(RegRoot.User, sid, @"Software\" + ApprovedSuffix + "Run", id.Name);
                (root, targetSid, kind) = ("HKU", sid, "Run");
                break;
        }

        using var handle = env.Registry.Open(run, false, false);
        if (handle is null || handle.IsSymbolicLink) return null;
        var resolved = handle.ResolvedPath;
        if (string.IsNullOrEmpty(resolved) || !StepGate.SameKey(resolved, StepGate.ExpectedPath(run))) return null;
        var v = handle.Read(id.Name);
        if (!v.Exists || v.Type is not (RegType.String or RegType.ExpandString) || v.Data is not string command || command.Length == 0) return null;
        var sha = StateJson.Sha256Hex(command);
        return new StartupEntry(id, command, sha, Target(sha, root, targetSid, kind), approved);
    }

    static StartupEntry? FindFolder(ExecEnv env, string sid, StartupEntryId id)
    {
        var user = id.Location == StartupLocation.UserFolder;
        var dir = user ? env.Folders.StartupFolder(sid) : env.Folders.CommonStartupFolder();
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var dirNorm = dir.TrimEnd('\\');

        string? found = null;
        foreach (var path in env.Files.ListFiles(dir, "*"))
        {
            var cut = path.LastIndexOf('\\');
            if (cut <= 0) continue;
            var parent = path[..cut].TrimEnd('\\');
            var file = path[(cut + 1)..];
            if (!parent.Equals(dirNorm, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (!file.Equals(id.Name, StringComparison.OrdinalIgnoreCase)) continue;
            found = path;
            break;
        }
        if (found is null) return null;

        var approved = user
            ? new RegLocation(RegRoot.User, sid, @"Software\" + ApprovedSuffix + "StartupFolder", id.Name)
            : new RegLocation(RegRoot.Machine, null, @"SOFTWARE\" + ApprovedSuffix + "StartupFolder", id.Name);
        var sha = StateJson.Sha256Hex(found);
        var target = new StartupTarget(user ? "HKU" : "HKLM", user ? sid : null, "StartupFolder", id.Name, sha);
        return new StartupEntry(id, found, sha, target, approved);
    }
}

/// <summary>Планы для включения и выключения записи автозагрузки.</summary>
internal static class StartupPlanner
{
    /// <summary>Значение StartupApproved: 12 байт, первый байт 02 (включено) или 03 (выключено).</summary>
    public static RegValueState Flag(bool enabled)
    {
        var b = new byte[12];
        b[0] = enabled ? (byte)2 : (byte)3;
        return new RegValueState(true, RegType.Binary, b);
    }

    public static OperationPlan Plan(ExecEnv env, string clientSid, string entryId, bool enabled)
    {
        if (!StartupEntryId.TryParse(entryId, out var id, out _))
            throw new PlanException(StepCodes.PolicyDenied, "EntryId: недопустимый формат");
        var entry = StartupEnumerator.Find(env, clientSid, id)
            ?? throw new PlanException(StepCodes.NotFound, "Запись автозагрузки не найдена в перечислении агента");
        var step = new StartupStep(id, entry.Target, entry.CommandSha256, entry.ApprovedLocation, Flag(enabled), clientSid);
        return new OperationPlan(OperationKind.Startup, id.ToString(), [step]);
    }
}
