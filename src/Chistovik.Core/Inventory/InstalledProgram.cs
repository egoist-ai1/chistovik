namespace Chistovik.Core.Inventory;

/// <summary>Откуда взята запись об установленной программе.</summary>
public enum ProgramSource
{
    Uninstall64,
    Uninstall32,
    UninstallHkcu,
    Msix,
}

/// <summary>Установленная программа (только чтение, без изменений системы).</summary>
public sealed record InstalledProgram
{
    /// <summary>Как в inventory.ps1: arp:{machine|user}:{x64|x86|user}:{имя ключа}.</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string DisplayVersion { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    /// <summary>Значение DisplayIcon как в реестре: «путь[,индекс]», возможно в кавычках и с переменными среды. Только для показа значка.</summary>
    public string DisplayIcon { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public bool IsMsi { get; init; }
    public string? ProductCode { get; init; }
    public required ProgramSource Source { get; init; }
    public bool IsSystemComponent { get; init; }
    /// <summary>NoRemove=1: производитель запретил удаление.</summary>
    public bool NoRemove { get; init; }
    public bool IsUpdate { get; init; }
    /// <summary>Значение ReleaseType (Update, Hotfix, …); пусто, если не задано.</summary>
    public string ReleaseType { get; init; } = "";
    public string ParentKeyName { get; init; } = "";
    public long? EstimatedSizeKb { get; init; }
    public DateOnly? InstallDate { get; init; }
    /// <summary>Основной путь ключа реестра (HKLM\... / HKCU\...).</summary>
    public required string RegistryKeyPath { get; init; }
    /// <summary>Все пути ключей, склеенные дедупликацией (первый равен RegistryKeyPath).</summary>
    public IReadOnlyList<string> RegistryKeyPaths { get; init; } = Array.Empty<string>();
}
