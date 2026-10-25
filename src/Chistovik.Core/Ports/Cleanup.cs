namespace Chistovik.Core.Ports;

/// <summary>Запись каталога без открытия файла. Created и LastWrite в UTC.</summary>
public sealed record DirEntryFacts(string Name, bool IsDirectory, bool IsReparse, long Length, DateTimeOffset Created, DateTimeOffset LastWrite);

/// <summary>Файловые операции очистки диска (C2). Удаление только по хэндлу, по пути удалять нельзя.</summary>
public interface ICleanupFiles
{
    /// <summary>FinalPath с VOLUME_NAME_GUID.</summary>
    FileFacts Inspect(string path);

    /// <summary>Не рекурсивно, без открытия файлов.</summary>
    IEnumerable<DirEntryFacts> Enumerate(string directory);

    /// <summary>
    /// Открывает без перехода по ссылке (FILE_FLAG_OPEN_REPARSE_POINT | BACKUP_SEMANTICS, DELETE | FILE_READ_ATTRIBUTES, share all).
    /// null, если объекта нет; ERROR_SHARING_VIOLATION это <c>SharingViolationException</c> (вводится в T4/T5).
    /// </summary>
    IDeletableHandle? OpenForDelete(string path, bool isDirectory);
}

/// <summary>ERROR_SHARING_VIOLATION при открытии: файл занят, это пропуск с причиной in-use, а не сбой.</summary>
public sealed class SharingViolationException(string message) : IOException(message);

public interface IDeletableHandle : IDisposable
{
    /// <summary>GetFinalPathNameByHandle(VOLUME_NAME_GUID | FILE_NAME_NORMALIZED).</summary>
    string FinalPath { get; }

    /// <summary>GetFileInformationByHandleEx(FileAttributeTagInfo).</summary>
    bool IsReparse { get; }

    /// <summary>Удаление по хэндлу (FileDispositionInfoEx); каталог только пустой.</summary>
    void Delete();
}

public interface IRecycleBin
{
    long QueryBytes();
    void Empty();

    /// <summary>SID токена агента: корзину очищаем, только если он совпадает с SID клиента.</summary>
    string CurrentTokenSid();
}

public interface IProcessList
{
    bool IsRunning(string imageName, string userSid);
}
