namespace Chistovik.Core.Ports;

/// <summary>
/// Исключение, имитирующее гибель процесса (только тесты): обработчик операций его не перехватывает и не превращает в ответ internal.
/// В рабочем коде не бросается.
/// </summary>
internal abstract class ProcessAbortException(string message) : Exception(message);

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface IIdGenerator { Guid NewGuid(); }

public interface IFileTrust { SignatureFacts GetSignature(string path); }

/// <summary>Факты о подписи файла (сырые, без решения).</summary>
public sealed record SignatureFacts(bool Valid, bool MicrosoftSigned);

public interface IUserFolders
{
    string? StartupFolder(string userSid);
    string? CommonStartupFolder();
}

public interface IFileSystem { IReadOnlyList<string> ListFiles(string dir, string pattern); }

/// <summary>
/// Запись контроля доступа каталога журнала. GrantsWrite: разрешающий ACE содержит право записи, создания, удаления
/// или смены ACL (маску классифицирует Native, решение принимает JournalAclPolicy). Allow=false для запрещающих ACE.
/// </summary>
public sealed record DirAce(string Sid, bool Allow, bool GrantsWrite);

public sealed record DirFacts(bool Exists, bool IsReparse, string? OwnerSid, IReadOnlyList<DirAce> Aces);

public interface IJournalDirectory
{
    DirFacts Inspect(string path);
    void CreateProtected(string path);
}
