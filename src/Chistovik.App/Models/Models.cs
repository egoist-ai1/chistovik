using Chistovik.Core.Ports;

namespace Chistovik.App.Models;

/// <summary>
/// Установленная программа. Id (arp:machine:x64:ключ) и Class это подсказка по PackagePolicy (той же, что у агента):
/// удалить через агента можно только Modifiable; окончательное решение агент принимает сам.
/// </summary>
public sealed record ProgramInfo(
    string Name, string Publisher, string Version, long SizeBytes, DateTime? InstalledOn, string Source,
    string Id = "", Changeability Class = Changeability.ReadOnly, string Rule = "", string? ReadOnlyReason = null);

/// <summary>
/// Запись автозагрузки. EntryId в формате протокола (run:hklm64:Имя, folder:user:файл) или пусто, если менять нельзя.
/// ExePath, FileMissing и подписи это факты для объяснений в «Чистке»: null значит «не выяснено», а не «нет».
/// </summary>
public sealed record StartupInfo(
    string EntryId, string Name, string Command, string Origin, bool Enabled, bool CanToggle, string? ReadOnlyReason,
    Changeability Class = Changeability.Modifiable, string Rule = "",
    string? ExePath = null, bool? FileMissing = null, bool? SignatureValid = null, bool? MicrosoftSigned = null);

/// <summary>Класс объекта для интерфейса: подсказка App, окончательное решение принимает агент.</summary>
public enum Changeability { Modifiable, ReadOnly, Forbidden }

public sealed record ServiceInfo(
    string Name, string DisplayName, StartMode? Start, Changeability Class, string Reason, string ImagePath,
    string Rule = "", bool NoAccess = false, int TriggerCount = 0)
{
    /// <summary>Служба из перечня исключений владельца: только «Отключить» или понижение с автозапуска на «Вручную».</summary>
    public bool CarveOut => Rule == "service-catalog-carveout";
}

public sealed record TaskInfo(
    string Path, string Name, bool Enabled, Changeability Class, string Reason, string Summary,
    string Rule = "", bool NoAccess = false)
{
    /// <summary>Задача из перечня исключений владельца: выключить можно, включить только откатом из журнала.</summary>
    public bool DisableOnly => Rule == "task-catalog-carveout";
}

public sealed record JournalStep(long Id, string Title, string Detail, StepState State, string StateText);

public sealed record JournalOperation(
    Guid Id, OperationKind Kind, string Title, DateTime When, OperationState State, string StatusText, bool Reversible, string? Error);

public enum TweakState { Unknown, On, Off }

public sealed record PowerSnapshot(Guid? ActiveScheme, IReadOnlyDictionary<string, (uint Ac, uint Dc)> Settings, string? Problem);
