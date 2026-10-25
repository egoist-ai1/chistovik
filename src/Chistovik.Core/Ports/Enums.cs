namespace Chistovik.Core.Ports;

/// <summary>Корень реестра. Всегда Registry64; HKCU агент не открывает, пользовательский куст это HKU\SID проверенного клиента.</summary>
public enum RegRoot { Machine, User }

public enum RegType
{
    None, String, ExpandString, Binary, DWord, QWord, MultiString,

    /// <summary>
    /// Тип вне списка выше (REG_DWORD_BIG_ENDIAN, REG_RESOURCE_*, REG_LINK и т. п.). Порт обхода дерева сообщает его вместо тихой подмены на Binary:
    /// такое дерево Core не копирует и не удаляет (reg-tree-bad-type), потому что при откате исходный числовой тип не вернуть.
    /// </summary>
    Other,
}

/// <summary>Вид шага журнала. Имена совпадают со значениями колонки step.kind (§1).</summary>
public enum StepKind { RegistryValue, StartupApproved, ServiceStart, TaskEnabled, PowerActiveScheme, PowerSetting, PowerSchemeCreate, UninstallRun, CleanupCategory, RecycleBinEmpty, RegistryKeyDelete, StartupRemove, SharedDllRemove }

/// <summary>Состояние шага: pending → done | failed | skipped | denied; для отката исходного шага ещё undone.</summary>
public enum StepState { Pending, Done, Failed, Skipped, Denied, Undone }

/// <summary>Состояние операции: running → done | partial | failed | denied | interrupted; для отката undone и partially_undone.</summary>
public enum OperationState { Running, Done, Partial, Failed, Denied, Interrupted, Undone, PartiallyUndone }

public enum OperationKind { Tweaks, Mode, Startup, Service, Task, PowerScheme, PowerSetting, Undo, Uninstall, Cleanup, Registry }

/// <summary>Запуск службы, как его понимает пользователь. Boot и System в запросах невозможны.</summary>
public enum StartMode { Automatic, AutomaticDelayed, Manual, Disabled }
