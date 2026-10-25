namespace Chistovik.Core.Ports;

/// <summary>
/// Хранилище .reg-копий удалённых ключей (C3): %ProgramData%\Chistovik\quarantine, каталог с защищённым ACL.
/// Пути относительные («&lt;opId:N&gt;\&lt;n&gt;.reg»), только «\», без «..»: нормализацию и создание каталога делает реализация.
/// Главная копия ключа лежит в реестре (HKLM\SOFTWARE\Chistovik\Quarantine); файл это вторая копия для ручного восстановления.
/// </summary>
public interface IQuarantineStore
{
    /// <summary>Записывает файл целиком (перезаписывает существующий).</summary>
    void WriteFile(string relativePath, byte[] content);

    bool Exists(string relativePath);

    /// <summary>Удаляет файл; отсутствие файла не ошибка.</summary>
    void Delete(string relativePath);

    /// <summary>Операции, для которых в хранилище есть каталог с копиями (имена каталогов вида «&lt;opId:N&gt;»; прочие игнорируются).</summary>
    IReadOnlyList<Guid> ListOperations();

    /// <summary>Удаляет каталог копий операции целиком; отсутствие каталога не ошибка.</summary>
    void DeleteOperation(Guid operationId);
}
