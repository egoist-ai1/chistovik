using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>
/// Разрешённое удаление поддерева реестра с копией. Source это ключ-сирота (прямой потомок Uninstall или App Paths),
/// Destination это копия в HKLM\SOFTWARE\Chistovik\Quarantine\&lt;opId:N&gt;\&lt;n&gt; (путь строит шлюз, проверяет CheckQuarantineDestination).
/// ExpectedSha256: хэш дерева, посчитанный при открытии; копия обязана дать тот же хэш. Конструктор internal: создаёт только StepGate.
/// </summary>
public sealed class ApprovedRegistryTreeDelete
{
    public RegLocation Source { get; }
    public RegLocation Destination { get; }
    public Guid OpId { get; }
    public int N { get; }
    public string ExpectedSha256 { get; }
    public int Subkeys { get; }

    /// <summary>Путь ключа в формате .reg: HKEY_LOCAL_MACHINE\… или HKEY_USERS\&lt;sid&gt;\….</summary>
    public string FullKeyPath { get; }

    /// <summary>Файл-копия относительно %ProgramData%\Chistovik\quarantine.</summary>
    public string BackupFile { get; }

    internal ApprovedRegistryTreeDelete(RegLocation source, RegLocation destination, Guid opId, int n, string expectedSha256, int subkeys,
        string fullKeyPath, string backupFile)
    {
        Source = source;
        Destination = destination;
        OpId = opId;
        N = n;
        ExpectedSha256 = expectedSha256;
        Subkeys = subkeys;
        FullKeyPath = fullKeyPath;
        BackupFile = backupFile;
    }
}

/// <summary>Разрешённое восстановление ключа из копии (откат RegistryKeyDelete).</summary>
public sealed class ApprovedRegistryTreeRestore
{
    public RegLocation Target { get; }
    public RegLocation Backup { get; }
    public string ExpectedSha256 { get; }

    internal ApprovedRegistryTreeRestore(RegLocation target, RegLocation backup, string expectedSha256)
    {
        Target = target;
        Backup = backup;
        ExpectedSha256 = expectedSha256;
    }
}
