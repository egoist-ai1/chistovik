namespace Chistovik.Core.Ports;

/// <summary>
/// Факты о файле или каталоге для политики пакетов. FinalPath это путь после GetFinalPathNameByHandle (ссылки раскрыты);
/// владелец и ACE собирает Native, решение принимает PackagePolicy. Exists=false: путь не открылся.
/// RequestedVolume (только очистка диска): том запрошенной буквы диска в виде «\\?\Volume{guid}» (без хвостового «\»), по которому
/// CleanupPolicy сверяет том финального пути. null: том не сообщён (пакеты, старые тесты), сверка пропускается.
/// </summary>
public sealed record FileFacts(bool Exists, bool IsReparse, string? FinalPath, string? OwnerSid, IReadOnlyList<DirAce> Aces, string? RequestedVolume = null);

public interface IFileSecurity
{
    /// <summary>Факты о файле или каталоге. Не бросает исключений на отсутствующем пути: возвращает Exists=false.</summary>
    FileFacts Inspect(string path);

    /// <summary>В файле есть маркер установщика Nullsoft (NSIS); читается ограниченный объём байт.</summary>
    bool HasNsisMarker(string finalPath);
}

/// <summary>Итог запуска деинсталлятора. TimedOut: срок вышел, процесс не завершали (ExitCode тогда не значим).</summary>
public sealed record UninstallOutcome(int ExitCode, bool TimedOut);

public interface IUninstaller
{
    /// <summary>INSTALLSTATE пакета (MsiQueryProductState): 5 установлен, 2 отсутствует (INSTALLSTATE_ABSENT), -1 неизвестен.</summary>
    int QueryMsiState(string productCode);

    /// <summary>Удаляет MSI-пакет через msi.dll без интерфейса и без перезагрузки; результат это код возврата MsiConfigureProductEx.</summary>
    UninstallOutcome RunMsi(string productCode, TimeSpan timeout);

    /// <summary>Запускает деинсталлятор без shell и ждёт завершения всех процессов задания до срока.</summary>
    UninstallOutcome RunExe(string exePath, string arguments, TimeSpan timeout);
}
