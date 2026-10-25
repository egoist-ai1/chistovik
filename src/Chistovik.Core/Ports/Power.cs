using Chistovik.Core.Execution;

namespace Chistovik.Core.Ports;

/// <summary>
/// Параметра питания нет в схеме или схемы нет (ERROR_FILE_NOT_FOUND в Native). Шаги журнала получают код not-found, а не win32:n.
/// </summary>
public sealed class PowerSettingNotFoundException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPowerControl
{
    Guid GetActive();
    IReadOnlyList<Guid> ListSchemes();
    (uint Ac, uint Dc) Read(Guid scheme, Guid sub, Guid setting);
    void Write(ApprovedPowerSetting c);
    void SetActive(ApprovedPowerScheme c);
    void Duplicate(ApprovedSchemeCreate c);
    void Delete(ApprovedSchemeDelete c);

    /// <summary>
    /// Имя схемы или null, если порт его не отдаёт. Нужно для удаления: схема считается нашей, только если журнал и имя совпали.
    /// Реализация по умолчанию возвращает null (проверка имени пропускается, пока Native не реализует метод).
    /// </summary>
    string? GetName(Guid scheme) => null;

    /// <summary>
    /// true на устройстве с Modern Standby (S0 low power idle, поле AoAc в SYSTEM_POWER_CAPABILITIES): там есть только
    /// «Сбалансированная», а копирование скрытых схем-шаблонов (ultimate) недоступно или даёт неизвестный результат.
    /// Реализация по умолчанию возвращает false (старое поведение, пока порт не реализует метод).
    /// </summary>
    bool IsModernStandby() => false;
}
