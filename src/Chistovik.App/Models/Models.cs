using Chistovik.Core.Ports;

namespace Chistovik.App.Models;

public sealed record ProgramInfo(
    string Name, string Publisher, string Version, long SizeBytes, DateTime? InstalledOn, string Source);

/// <summary>Запись автозагрузки. EntryId в формате протокола (run:hklm64:Имя, folder:user:файл) или пусто, если менять нельзя.</summary>
public sealed record StartupInfo(
    string EntryId, string Name, string Command, string Origin, bool Enabled, bool CanToggle, string? ReadOnlyReason);

/// <summary>Класс объекта для интерфейса: подсказка App, окончательное решение принимает агент.</summary>
public enum Changeability { Modifiable, ReadOnly, Forbidden }

public sealed record ServiceInfo(
    string Name, string DisplayName, StartMode? Start, Changeability Class, string Reason, string ImagePath);

public sealed record TaskInfo(
    string Path, string Name, bool Enabled, Changeability Class, string Reason, string Summary);

public sealed record JournalStep(long Id, string Title, string Detail, StepState State, string StateText);

public sealed record JournalOperation(
    Guid Id, OperationKind Kind, string Title, DateTime When, OperationState State, string StatusText, bool Reversible, string? Error);

public enum TweakState { Unknown, On, Off }

public sealed record PowerSnapshot(Guid? ActiveScheme, IReadOnlyDictionary<string, (uint Ac, uint Dc)> Settings, string? Problem);
