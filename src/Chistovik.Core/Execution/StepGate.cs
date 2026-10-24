using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Результат проверки шага: Approved != null (можно выполнять) либо отказ с кодом шага и правилом Policy.</summary>
public sealed record GateResult<T>(T? Approved, string? Code, string? Rule) where T : class
{
    public bool IsAllowed => Approved is not null;
    public static GateResult<T> Allow(T approved) => new(approved, null, null);
    public static GateResult<T> Deny(string code, string? rule) => new(null, code, rule);
}

/// <summary>
/// Единственное место, где вызывается Policy и создаются Approved* (DESIGN-0.1 §2). Это ЗАГЛУШКА пакета W0:
/// сигнатуры зафиксированы, тела бросают NotImplementedException; реализует пакет W2.
/// Общие требования ко всем Check*: (1) цель выводится из разрешающего источника (запись каталога, перечисление
/// автозагрузки, power.json), а не из переданных строк журнала; (2) Policy = Allow, ExpertOnly считается Deny;
/// (3) проверка повторяется по свежему состоянию того же хэндла (ResolvedPath, Query) непосредственно перед записью.
/// </summary>
public sealed class StepGate
{
    public StepGate(TweakCatalog tweaks, PowerCatalog power)
    {
        Tweaks = tweaks;
        Power = power;
    }

    public TweakCatalog Tweaks { get; }
    public PowerCatalog Power { get; }

    /// <summary>
    /// Строит расположение значения для твика каталога. HKCU → Root=User и userSid проверенного клиента (HKU\SID);
    /// HKLM → Root=Machine. Отказ: RegistryPolicy.CheckSetValue не Allow, чужой или системный SID.
    /// </summary>
    public GateResult<RegLocation> ResolveTweakLocation(TweakEntry tweak, string clientSid)
        => throw new NotImplementedException("W2");

    /// <summary>
    /// Разрешает запись значения. <paramref name="expected"/> строит планировщик из каталога (не из журнала).
    /// Проверяет: handle.ResolvedPath равен ожидаемому пути, !handle.IsSymbolicLink, Policy для ключа и имени
    /// (удаление значения через RegistryPolicy.CheckDeleteValue, когда desired.Exists=false).
    /// </summary>
    public GateResult<ApprovedRegistryWrite> CheckRegistryWrite(RegLocation expected, RegValueState desired, IRegKeyHandle handle)
        => throw new NotImplementedException("W2");

    /// <summary>
    /// Разрешает смену запуска службы по свежему <paramref name="raw"/> (Query того же хэндла): ServicePolicy,
    /// ImagePath, зависимые, общий svchost. Повышение запуска (Disabled→Manual/Auto, Manual→Auto) разрешено напрямую
    /// только для файла службы в Program Files / Program Files (x86); иначе только откатом собственного шага
    /// (<paramref name="isRevertOfOwnStep"/>). Boot и System как цель или before при откате запрещены.
    /// </summary>
    public GateResult<ApprovedServiceChange> CheckServiceChange(ServiceRaw raw, StartMode desired, bool isRevertOfOwnStep)
        => throw new NotImplementedException("W2");

    /// <summary>
    /// Разрешает включение или выключение задачи по свежему <paramref name="raw"/>: TaskPolicy (запрет поддеревьев
    /// \Microsoft\* и \Chistovik\*, действие в каталоге Windows, ComHandler). Включение задачи от SYSTEM или с
    /// HighestAvailable разрешено только откатом собственного шага.
    /// </summary>
    public GateResult<ApprovedTaskChange> CheckTaskChange(TaskRaw raw, bool desiredEnabled, bool isRevertOfOwnStep)
        => throw new NotImplementedException("W2");

    /// <summary>
    /// Разрешает запись параметра питания в активную схему: параметр из power.json, значения в диапазоне каталога,
    /// схема равна <paramref name="activeScheme"/> (цель берётся на момент выполнения; GUID схемы не приходит от клиента).
    /// </summary>
    public GateResult<ApprovedPowerSetting> CheckPowerSetting(PowerSettingEntry setting, Guid activeScheme, uint ac, uint dc)
        => throw new NotImplementedException("W2");

    /// <summary>Разрешает смену активной схемы: схема из power.json (или наша созданная копия) и присутствует в IPowerControl.ListSchemes().</summary>
    public GateResult<ApprovedPowerScheme> CheckPowerScheme(PowerSchemeEntry scheme, Guid resolvedGuid, IReadOnlyCollection<Guid> installedSchemes)
        => throw new NotImplementedException("W2");

    /// <summary>Разрешает создание нашей схемы копированием шаблона: только entry.CreateFromTemplate != null, новый GUID выбрал агент и его ещё нет среди установленных.</summary>
    public GateResult<ApprovedSchemeCreate> CheckSchemeCreate(PowerSchemeEntry scheme, Guid newScheme, IReadOnlyCollection<Guid> installedSchemes)
        => throw new NotImplementedException("W2");

    /// <summary>
    /// Разрешает удаление схемы: только созданная нами (<paramref name="createdByUs"/>, по шагу PowerSchemeCreate журнала)
    /// и не активная. Встроенные и чужие схемы — отказ.
    /// </summary>
    public GateResult<ApprovedSchemeDelete> CheckSchemeDelete(Guid scheme, Guid activeScheme, bool createdByUs)
        => throw new NotImplementedException("W2");
}
