using System.IO;
using System.Security;
using Chistovik.App.Services.Agent;
using Chistovik.Core.Journal;

namespace Chistovik.App.Services;

/// <summary>Понятное сообщение об ошибке: что случилось, что делать и техническая строка для подробностей.</summary>
public sealed record ErrorInfo(string Message, string Hint, string Details)
{
    /// <summary>Основной текст для пользователя: сообщение и подсказка одной строкой.</summary>
    public string Text => Hint.Length == 0 ? Message : Message + " " + Hint;
}

/// <summary>
/// Сопоставляет исключение и код клиента с русским текстом. Исходное <see cref="Exception.Message"/> (на английском
/// в .NET) в основной текст не попадает: оно остаётся только в <see cref="ErrorInfo.Details"/> для подсказки и диагностики.
/// </summary>
public static class ErrorText
{
    public static ErrorInfo Describe(Exception e)
    {
        var details = e.GetType().Name + ": " + e.Message;
        var (message, hint) = e switch
        {
            UnauthorizedAccessException or SecurityException
                => ("Нет доступа к данным Windows.", "Запустите Чистовик от учётной записи администратора или повторите после перезапуска."),
            TimeoutException or TaskCanceledException
                => ("Служба Чистовика не ответила вовремя.", "Повторите попытку."),
            AgentUntrustedException
                => ("Служба Чистовика не прошла проверку подлинности.", "Переустановите Чистовик и повторите попытку."),
            JournalException { Code: JournalException.Corrupt }
                => ("Журнал повреждён и не читается.", "Перезапустите Чистовик; если не поможет, сообщите об ошибке."),
            JournalException { Code: JournalException.NewerSchema }
                => ("Журнал создан более новой версией Чистовика.", "Обновите программу."),
            IOException
                => ("Не удалось прочитать данные: файл занят или недоступен.", "Повторите чуть позже."),
            _ => ("Не удалось получить данные.", "Нажмите «Повторить»."),
        };
        return new ErrorInfo(message, hint, details);
    }

    /// <summary>Текст для кода клиента (<see cref="ClientCodes"/>); неизвестный код даёт общее сообщение.</summary>
    public static ErrorInfo ForClientCode(string? code, string? details = null)
    {
        var d = details ?? code ?? "";
        return code switch
        {
            ClientCodes.Timeout or ClientCodes.NotRunning or ClientCodes.Disconnected
                => new ErrorInfo("Служба Чистовика не ответила вовремя.", "Повторите попытку.", d),
            ClientCodes.Declined or ClientCodes.NoAdminAccount
                => new ErrorInfo("Нет доступа к данным Windows.", "Запустите Чистовик от учётной записи администратора или повторите после перезапуска.", d),
            _ => new ErrorInfo("Не удалось получить данные.", "Нажмите «Повторить».", d),
        };
    }
}
