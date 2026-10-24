using Chistovik.Core.Catalog;

namespace Chistovik.Core.Policy;

/// <summary>
/// Политика схем питания (DESIGN-0.1 §7). Допустимы только схемы из power.json (saver, bal, high, ult) и наши копии
/// «Chistovik …», GUID которых журнал записал как созданные нами. Параметры: только пары подгруппа+параметр из каталога
/// и значения в его диапазоне. Всё прочее Deny. Ни один метод не читает систему и не принимает строк от клиента.
/// </summary>
public sealed class PowerPolicy
{
    /// <summary>Префикс имени схемы, которую создаёт Чистовик (проверяется при создании копии).</summary>
    public const string OwnNamePrefix = "Chistovik ";

    readonly PowerCatalog _catalog;

    /// <summary>Политика поверх встроенного каталога.</summary>
    public PowerPolicy() : this(PowerCatalog.Shared) { }

    /// <summary>Политика поверх заданного каталога (для тестов).</summary>
    public PowerPolicy(PowerCatalog catalog) => _catalog = catalog;

    bool IsCatalogScheme(Guid g) => g != Guid.Empty && _catalog.Schemes.Any(s => s.Guid == g);

    /// <summary>
    /// Можно ли сделать схему активной. Сигнатура: <c>CheckSetActive(Guid scheme, bool createdByUs)</c>.
    /// Allow: GUID из power.json либо <paramref name="createdByUs"/>=true (шаг PowerSchemeCreate в журнале). Остальное Deny.
    /// Присутствие схемы в IPowerControl.ListSchemes() проверяет StepGate. Target = «power-scheme:&lt;guid&gt;».
    /// </summary>
    public PolicyDecision CheckSetActive(Guid scheme, bool createdByUs)
    {
        var target = "power-scheme:" + scheme.ToString("D");
        if (scheme == Guid.Empty) return PolicyDecision.Deny("Пустой GUID схемы", "power-bad-guid").WithTarget(target);
        if (IsCatalogScheme(scheme)) return PolicyDecision.Allow("power-catalog-scheme").WithTarget(target);
        if (createdByUs) return PolicyDecision.Allow("power-own-scheme").WithTarget(target);
        return PolicyDecision.Deny("Чужая схема питания не изменяется", "power-foreign-scheme").WithTarget(target);
    }

    /// <summary>
    /// Можно ли создать копию схемы. Сигнатура: <c>CheckDuplicate(Guid template, Guid newScheme, string? newName, IReadOnlyCollection&lt;Guid&gt; installed)</c>.
    /// Allow: <paramref name="template"/> это CreateFromTemplate записи каталога (сейчас только ult); <paramref name="newScheme"/>
    /// непустой, не совпадает с GUID каталога и отсутствует среди <paramref name="installed"/>; имя начинается с «Chistovik »
    /// и не содержит управляющих символов, длиной не более 64. Target = «power-scheme:&lt;новый guid&gt;».
    /// </summary>
    public PolicyDecision CheckDuplicate(Guid template, Guid newScheme, string? newName, IReadOnlyCollection<Guid> installed)
    {
        var target = "power-scheme:" + newScheme.ToString("D");
        if (template == Guid.Empty || !_catalog.Schemes.Any(s => s.CreateFromTemplate == template))
            return PolicyDecision.Deny("Шаблон схемы не из каталога", "power-bad-template").WithTarget(target);
        if (newScheme == Guid.Empty || IsCatalogScheme(newScheme) || installed.Contains(newScheme))
            return PolicyDecision.Deny("GUID новой схемы пуст или уже занят", "power-bad-guid").WithTarget(target);
        if (newName is null || newName.Length <= OwnNamePrefix.Length || newName.Length > 64
            || !newName.StartsWith(OwnNamePrefix, StringComparison.Ordinal) || newName.Any(char.IsControl))
            return PolicyDecision.Deny("Имя копии должно начинаться с «Chistovik »", "power-bad-name").WithTarget(target);
        return PolicyDecision.Allow("power-create-own").WithTarget(target);
    }

    /// <summary>
    /// Можно ли удалить схему. Сигнатура: <c>CheckDelete(Guid scheme, Guid activeScheme, bool createdByUs)</c>.
    /// Allow только для схемы, созданной нами (<paramref name="createdByUs"/> по журналу), не из каталога и не активной.
    /// Встроенные, чужие и активная схемы это Deny. Target = «power-scheme:&lt;guid&gt;».
    /// </summary>
    public PolicyDecision CheckDelete(Guid scheme, Guid activeScheme, bool createdByUs)
    {
        var target = "power-scheme:" + scheme.ToString("D");
        if (scheme == Guid.Empty) return PolicyDecision.Deny("Пустой GUID схемы", "power-bad-guid").WithTarget(target);
        if (IsCatalogScheme(scheme)) return PolicyDecision.Deny("Встроенные схемы не удаляются", "power-delete-builtin").WithTarget(target);
        if (scheme == activeScheme) return PolicyDecision.Deny("Активная схема не удаляется", "power-delete-active").WithTarget(target);
        if (!createdByUs) return PolicyDecision.Deny("Удаляется только схема, созданная Чистовиком", "power-delete-foreign").WithTarget(target);
        return PolicyDecision.Allow("power-delete-own").WithTarget(target);
    }

    /// <summary>
    /// Можно ли записать параметр. Сигнатура:
    /// <c>CheckWriteSetting(Guid scheme, bool schemeCreatedByUs, Guid subgroup, Guid setting, uint ac, uint dc)</c>.
    /// Allow: схема из каталога или наша; пара subgroup+setting есть в power.json (одинаковый setting под другой подгруппой
    /// это Deny); <paramref name="ac"/> и <paramref name="dc"/> лежат в [Min, Max] записи включительно.
    /// Target = «power-setting:&lt;scheme&gt;:&lt;subgroup&gt;:&lt;setting&gt;».
    /// </summary>
    public PolicyDecision CheckWriteSetting(Guid scheme, bool schemeCreatedByUs, Guid subgroup, Guid setting, uint ac, uint dc)
    {
        var target = $"power-setting:{scheme:D}:{subgroup:D}:{setting:D}";
        var schemeOk = CheckSetActive(scheme, schemeCreatedByUs);
        if (!schemeOk.IsAllowed)
            return PolicyDecision.Deny(schemeOk.Reason, schemeOk.Rule).WithTarget(target);
        var entry = _catalog.Settings.FirstOrDefault(s => s.Subgroup == subgroup && s.Setting == setting);
        if (entry is null)
            return PolicyDecision.Deny("Параметр питания не входит в каталог", "power-unknown-setting").WithTarget(target);
        if (ac < entry.Min || ac > entry.Max || dc < entry.Min || dc > entry.Max)
            return PolicyDecision.Deny($"Значение вне диапазона {entry.Min}..{entry.Max}", "power-out-of-range").WithTarget(target);
        return PolicyDecision.Allow("power-catalog-setting").WithTarget(target);
    }
}
