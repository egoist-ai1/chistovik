using Chistovik.Core.Catalog;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using PolicyStartMode = Chistovik.Core.Policy.StartMode;

namespace Chistovik.Core.Execution;

/// <summary>Результат проверки шага: Approved != null (можно выполнять) либо отказ с кодом шага и правилом Policy.</summary>
public sealed record GateResult<T>(T? Approved, string? Code, string? Rule) where T : class
{
    public bool IsAllowed => Approved is not null;
    public static GateResult<T> Allow(T approved) => new(approved, null, null);
    public static GateResult<T> Deny(string code, string? rule) => new(null, code, rule);
}

/// <summary>Подтверждение, что запись автозагрузки прошла StartupPolicy (сама запись идёт отдельным ApprovedRegistryWrite).</summary>
public sealed class StartupApproval
{
    public StartupEntryId Id { get; }

    internal StartupApproval(StartupEntryId id) => Id = id;
}

/// <summary>
/// Единственное место, где вызывается Policy и создаются Approved* (DESIGN-0.1 §2). Каждый метод получает факты свежего
/// Query/Read и проверяет три условия: (1) цель выведена из разрешающего источника (запись каталога, перечисление
/// автозагрузки, power.json), (2) Policy = Allow (ExpertOnly считается Deny; поля «эксперт» нет), (3) для реестра
/// ResolvedPath соответствует ожидаемому и ключ не символическая ссылка. Отказ несёт код шага (policy-denied, not-found) и правило.
/// </summary>
public sealed class StepGate
{
    /// <summary>Типы значений и размеры, которые агент вообще пишет в реестр (твики и StartupApproved).</summary>
    const int MaxStringChars = 2048;
    const int MaxBinaryBytes = 64;

    readonly ServicePolicy _service;
    readonly TaskPolicy _task;
    readonly StartupPolicy _startup;
    readonly PowerPolicy _powerPolicy;
    readonly IFileTrust _trust;

    public StepGate(TweakCatalog tweaks, PowerCatalog power, PolicyContext context, IFileTrust trust)
    {
        Tweaks = tweaks;
        Power = power;
        _service = new ServicePolicy(context);
        _task = new TaskPolicy(context);
        _startup = new StartupPolicy(context);
        _powerPolicy = new PowerPolicy(power);
        _trust = trust;
    }

    /// <summary>Шлюз без сведений о подписях файлов (все файлы считаются неподписанными): службы остаются неизменяемыми. Для случаев, где службы не нужны.</summary>
    public StepGate(TweakCatalog tweaks, PowerCatalog power)
        : this(tweaks, power, PolicyContext.FromEnvironment(), new UnknownTrust())
    {
    }

    sealed class UnknownTrust : IFileTrust
    {
        public SignatureFacts GetSignature(string path) => new(false, false);
    }

    public TweakCatalog Tweaks { get; }
    public PowerCatalog Power { get; }

    static GateResult<T> Deny<T>(string rule, string code = StepCodes.PolicyDenied) where T : class => GateResult<T>.Deny(code, rule);

    static GateResult<T> Deny<T>(PolicyDecision d) where T : class => GateResult<T>.Deny(StepCodes.PolicyDenied, d.Rule);

    // ---------- реестр ----------

    /// <summary>Ожидаемый полный путь расположения в форме «HKLM\…» или «HKU\&lt;sid&gt;\…».</summary>
    public static string ExpectedPath(RegLocation l)
        => l.Root == RegRoot.Machine ? "HKLM\\" + l.KeyPath : "HKU\\" + l.UserSid + "\\" + l.KeyPath;

    /// <summary>
    /// Строит расположение значения для твика каталога. HKCU → Root=User и userSid проверенного клиента (HKU\SID);
    /// HKLM → Root=Machine. Отказ: твик не из каталога, RegistryPolicy не Allow, чужой или системный SID.
    /// </summary>
    public GateResult<RegLocation> ResolveTweakLocation(TweakEntry tweak, string clientSid)
    {
        var known = Tweaks.FindTweak(tweak.Id);
        if (known is null || !ReferenceEquals(known, tweak)) return Deny<RegLocation>("tweak-not-in-catalog");
        RegLocation loc;
        if (tweak.Hive == "HKCU") loc = new RegLocation(RegRoot.User, clientSid, tweak.Key, tweak.Value);
        else if (tweak.Hive == "HKLM") loc = new RegLocation(RegRoot.Machine, null, tweak.Key, tweak.Value);
        else return Deny<RegLocation>("tweak-bad-hive");
        var d = RegistryPolicy.CheckSetValue(ExpectedPath(loc), loc.ValueName, clientSid);
        return d.IsAllowed ? GateResult<RegLocation>.Allow(loc) : Deny<RegLocation>(d);
    }

    /// <summary>
    /// Проверка по имени, без хэндла (предпроверка, когда ключа ещё нет): Policy для ключа и имени значения,
    /// допустимые тип и размер значения. Удаление значения идёт через ту же Policy (RegistryPolicy.CheckDeleteValue).
    /// </summary>
    public GateResult<RegLocation> CheckRegistryTarget(RegLocation expected, RegValueState desired, string clientSid)
    {
        if (expected.Root == RegRoot.User && (string.IsNullOrEmpty(expected.UserSid)
            || !string.Equals(expected.UserSid, clientSid, StringComparison.OrdinalIgnoreCase)))
            return Deny<RegLocation>("reg-foreign-sid");
        if (expected.Root == RegRoot.Machine && expected.UserSid is not null) return Deny<RegLocation>("reg-bad-location");
        if (!ValidDesired(desired)) return Deny<RegLocation>("reg-bad-value-type");
        var path = ExpectedPath(expected);
        var d = desired.Exists
            ? RegistryPolicy.CheckSetValue(path, expected.ValueName, clientSid)
            : RegistryPolicy.CheckDeleteValue(path, expected.ValueName, clientSid);
        return d.IsAllowed ? GateResult<RegLocation>.Allow(expected) : Deny<RegLocation>(d);
    }

    /// <summary>
    /// Разрешает запись значения. <paramref name="expected"/> строит планировщик из каталога (не из журнала).
    /// Проверяет: handle.ResolvedPath равен ожидаемому пути, !handle.IsSymbolicLink, Policy для ключа и имени
    /// (удаление значения через RegistryPolicy.CheckDeleteValue, когда desired.Exists=false), причём и для ожидаемого,
    /// и для фактического пути.
    /// </summary>
    public GateResult<ApprovedRegistryWrite> CheckRegistryWrite(RegLocation expected, RegValueState desired, IRegKeyHandle handle, string clientSid)
    {
        var pre = CheckRegistryTarget(expected, desired, clientSid);
        if (!pre.IsAllowed) return GateResult<ApprovedRegistryWrite>.Deny(pre.Code!, pre.Rule);
        if (handle.IsSymbolicLink) return Deny<ApprovedRegistryWrite>("reg-symlink");
        var resolved = handle.ResolvedPath;
        if (string.IsNullOrEmpty(resolved) || !SameKey(resolved, ExpectedPath(expected)))
            return Deny<ApprovedRegistryWrite>("reg-resolved-mismatch");
        var d = desired.Exists
            ? RegistryPolicy.CheckSetValue(resolved, expected.ValueName, clientSid)
            : RegistryPolicy.CheckDeleteValue(resolved, expected.ValueName, clientSid);
        if (!d.IsAllowed) return Deny<ApprovedRegistryWrite>(d);
        return GateResult<ApprovedRegistryWrite>.Allow(new ApprovedRegistryWrite(expected, desired));
    }

    static bool ValidDesired(RegValueState v)
    {
        if (!v.Exists) return true;
        return v.Type switch
        {
            RegType.DWord => v.Data is uint,
            RegType.String => v.Data is string s && s.Length <= MaxStringChars && s.IndexOf('\0') < 0,
            RegType.Binary => v.Data is byte[] b && b.Length <= MaxBinaryBytes,
            _ => false,
        };
    }

    /// <summary>Равенство ключей после разбора (\REGISTRY\MACHINE, \REGISTRY\USER, ControlSet00N → CurrentControlSet), без учёта регистра.</summary>
    internal static bool SameKey(string a, string b)
    {
        if (!RegistryPolicy.TryParse(a, out var ka, out _) || !RegistryPolicy.TryParse(b, out var kb, out _)) return false;
        if (ka.Hive != kb.Hive || ka.Segments.Length != kb.Segments.Length) return false;
        for (var i = 0; i < ka.Segments.Length; i++)
            if (!ka.Segments[i].Equals(kb.Segments[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ---------- службы ----------

    /// <summary>Факты о службе для ServicePolicy. Владельца-программы в портах нет, поэтому OwnedByInstalledProgram=false: изменяемы только службы с действительной подписью не от Microsoft.</summary>
    ServiceFacts Facts(ServiceRaw raw, PolicyStartMode current)
    {
        var exe = _service.ResolveImagePath(raw.ImagePath);
        var sig = exe is null ? new SignatureFacts(false, false) : _trust.GetSignature(exe);
        return new ServiceFacts(raw.Name, MapType(raw.TypeFlags), raw.ImagePath, sig.Valid, sig.MicrosoftSigned, raw.LaunchProtected,
            raw.DependentCount, false, current);
    }

    static ServiceType MapType(uint flags)
    {
        if ((flags & 0x1) != 0) return ServiceType.KernelDriver;
        if ((flags & 0x2) != 0) return ServiceType.FileSystemDriver;
        if ((flags & 0x8) != 0) return ServiceType.KernelDriver;
        if ((flags & 0x20) != 0) return ServiceType.ShareProcess;
        if ((flags & 0x10) != 0) return ServiceType.OwnProcess;
        return ServiceType.Other;
    }

    /// <summary>Текущий запуск службы по сырым данным; false для значений вне 0..4.</summary>
    public static bool TryCurrentStart(ServiceRaw raw, out PolicyStartMode mode)
    {
        mode = raw.StartType switch
        {
            0 => PolicyStartMode.Boot,
            1 => PolicyStartMode.System,
            2 => raw.Delayed ? PolicyStartMode.AutomaticDelayed : PolicyStartMode.Automatic,
            3 => PolicyStartMode.Manual,
            4 => PolicyStartMode.Disabled,
            _ => PolicyStartMode.Boot,
        };
        return raw.StartType <= 4;
    }

    /// <summary>
    /// Разрешает смену запуска службы по свежему <paramref name="raw"/> (Query того же хэндла): ServicePolicy,
    /// ImagePath, зависимые, общий svchost. Повышение запуска (Disabled→Manual/Auto, Manual→Auto) разрешено напрямую
    /// только для файла службы в Program Files / Program Files (x86); иначе только откатом собственного шага
    /// (<paramref name="isRevertOfOwnStep"/>). Boot и System как цель или текущее значение запрещены.
    /// </summary>
    public GateResult<ApprovedServiceChange> CheckServiceChange(ServiceRaw raw, Ports.StartMode desired, bool isRevertOfOwnStep, string expectedName)
    {
        if (!string.Equals(raw.Name, expectedName, StringComparison.OrdinalIgnoreCase)) return Deny<ApprovedServiceChange>("service-name-mismatch");
        if (!Enum.IsDefined(desired)) return Deny<ApprovedServiceChange>("service-bad-start");
        if (!TryCurrentStart(raw, out var current)) return Deny<ApprovedServiceChange>("service-unknown-start");
        if (current is PolicyStartMode.Boot or PolicyStartMode.System) return Deny<ApprovedServiceChange>("service-boot-start");
        var d = _service.CheckSetStart(Facts(raw, current), MapDesired(desired), isRevertOfOwnStep);
        if (!d.IsAllowed) return Deny<ApprovedServiceChange>(d);
        return GateResult<ApprovedServiceChange>.Allow(new ApprovedServiceChange(raw.Name, desired));
    }

    static PolicyStartMode MapDesired(Ports.StartMode m) => m switch
    {
        Ports.StartMode.Automatic => PolicyStartMode.Automatic,
        Ports.StartMode.AutomaticDelayed => PolicyStartMode.AutomaticDelayed,
        Ports.StartMode.Manual => PolicyStartMode.Manual,
        _ => PolicyStartMode.Disabled,
    };

    // ---------- задачи ----------

    /// <summary>
    /// Разрешает включение или выключение задачи по свежему <paramref name="raw"/>: TaskPolicy (запрет поддеревьев
    /// \Microsoft\* и \Chistovik\*, действие в каталоге Windows, ComHandler). Включение задачи от SYSTEM или с
    /// HighestAvailable разрешено только откатом собственного шага.
    /// </summary>
    public GateResult<ApprovedTaskChange> CheckTaskChange(TaskRaw raw, bool desiredEnabled, bool isRevertOfOwnStep, string expectedPath)
    {
        if (!string.Equals(raw.Path, expectedPath, StringComparison.OrdinalIgnoreCase)) return Deny<ApprovedTaskChange>("task-path-mismatch");
        var d = _task.CheckSetEnabled(raw, desiredEnabled, isRevertOfOwnStep);
        if (!d.IsAllowed) return Deny<ApprovedTaskChange>(d);
        return GateResult<ApprovedTaskChange>.Allow(new ApprovedTaskChange(raw.Path, desiredEnabled));
    }

    // ---------- автозагрузка ----------

    /// <summary>
    /// Проверяет запись автозагрузки из СОБСТВЕННОГО перечисления агента: команда и подпись файла (IFileTrust) идут в StartupPolicy.
    /// Системные переменные в команде раскрываются самой политикой по белому списку; прочие переменные дают отказ (ReadOnly).
    /// </summary>
    public GateResult<StartupApproval> CheckStartupEntry(StartupEntryId id, string? command)
    {
        var sig = new SignatureFacts(false, false);
        if (_startup.TryResolveCommand(command, out var exe, out _)) sig = _trust.GetSignature(exe);
        var d = _startup.CheckToggle(id, command, sig);
        return d.IsAllowed ? GateResult<StartupApproval>.Allow(new StartupApproval(id)) : Deny<StartupApproval>(d);
    }

    // ---------- питание ----------

    /// <summary>
    /// Разрешает запись параметра питания: параметр из power.json (пара подгруппа+параметр), значения в диапазоне каталога,
    /// схема установлена и разрешена PowerPolicy. При <paramref name="requireActive"/> схема обязана быть активной на момент
    /// выполнения (GUID схемы не приходит от клиента); при откате требование снимается.
    /// </summary>
    public GateResult<ApprovedPowerSetting> CheckPowerSetting(PowerSettingEntry setting, Guid scheme, Guid activeScheme, bool requireActive,
        bool schemeCreatedByUs, IReadOnlyCollection<Guid> installedSchemes, uint ac, uint dc)
    {
        var known = Power.FindSetting(setting.Id);
        if (known is null || known.Subgroup != setting.Subgroup || known.Setting != setting.Setting) return Deny<ApprovedPowerSetting>("power-setting-not-in-catalog");
        if (!installedSchemes.Contains(scheme)) return GateResult<ApprovedPowerSetting>.Deny(StepCodes.NotFound, "power-scheme-not-installed");
        if (requireActive && scheme != activeScheme) return Deny<ApprovedPowerSetting>("power-scheme-not-active");
        var d = _powerPolicy.CheckWriteSetting(scheme, schemeCreatedByUs, known.Subgroup, known.Setting, ac, dc);
        if (!d.IsAllowed) return Deny<ApprovedPowerSetting>(d);
        return GateResult<ApprovedPowerSetting>.Allow(new ApprovedPowerSetting(scheme, known.Subgroup, known.Setting, ac, dc));
    }

    /// <summary>
    /// Разрешает смену активной схемы: схема из power.json (или наша созданная копия) и присутствует в IPowerControl.ListSchemes().
    /// <paramref name="entry"/> = null допустим только при откате (<paramref name="isRevertOfOwnStep"/>): вернуть уже установленную схему,
    /// которая была активной до нас (в том числе пользовательскую, её PowerPolicy иначе не пропустит).
    /// </summary>
    public GateResult<ApprovedPowerScheme> CheckPowerScheme(PowerSchemeEntry? entry, Guid resolvedGuid, IReadOnlyCollection<Guid> installedSchemes,
        bool createdByUs, bool isRevertOfOwnStep)
    {
        if (entry is null && !isRevertOfOwnStep) return Deny<ApprovedPowerScheme>("power-scheme-not-in-catalog");
        if (entry is not null)
        {
            var known = Power.FindScheme(entry.Id);
            if (known is null || known.Guid != entry.Guid) return Deny<ApprovedPowerScheme>("power-scheme-not-in-catalog");
            var isCatalogGuid = resolvedGuid == entry.Guid;
            if (!isCatalogGuid && !(entry.CreateFromTemplate is not null && createdByUs)) return Deny<ApprovedPowerScheme>("power-scheme-mismatch");
        }
        if (!installedSchemes.Contains(resolvedGuid)) return GateResult<ApprovedPowerScheme>.Deny(StepCodes.NotFound, "power-scheme-not-installed");
        var d = _powerPolicy.CheckSetActive(resolvedGuid, createdByUs || isRevertOfOwnStep);
        return d.IsAllowed ? GateResult<ApprovedPowerScheme>.Allow(new ApprovedPowerScheme(resolvedGuid)) : Deny<ApprovedPowerScheme>(d);
    }

    /// <summary>Имя нашей копии схемы (PowerPolicy требует префикс «Chistovik »).</summary>
    public static string OwnSchemeName(PowerSchemeEntry entry)
        => entry.Id == "ult" ? PowerPolicy.OwnNamePrefix + "Ultimate Performance" : PowerPolicy.OwnNamePrefix + entry.Id;

    /// <summary>Разрешает создание нашей схемы копированием шаблона: только entry.CreateFromTemplate != null, новый GUID выбрал агент и его ещё нет среди установленных.</summary>
    public GateResult<ApprovedSchemeCreate> CheckSchemeCreate(PowerSchemeEntry scheme, Guid newScheme, IReadOnlyCollection<Guid> installedSchemes)
    {
        var known = Power.FindScheme(scheme.Id);
        if (known is null || known.Guid != scheme.Guid || scheme.CreateFromTemplate is not { } template) return Deny<ApprovedSchemeCreate>("power-bad-template");
        var name = OwnSchemeName(scheme);
        var d = _powerPolicy.CheckDuplicate(template, newScheme, name, installedSchemes);
        return d.IsAllowed ? GateResult<ApprovedSchemeCreate>.Allow(new ApprovedSchemeCreate(template, newScheme, name)) : Deny<ApprovedSchemeCreate>(d);
    }

    /// <summary>
    /// Разрешает удаление схемы: только созданная нами (<paramref name="createdByUs"/>, по шагу PowerSchemeCreate журнала)
    /// и не активная. Встроенные и чужие схемы — отказ.
    /// </summary>
    public GateResult<ApprovedSchemeDelete> CheckSchemeDelete(Guid scheme, Guid activeScheme, bool createdByUs)
    {
        var d = _powerPolicy.CheckDelete(scheme, activeScheme, createdByUs);
        return d.IsAllowed ? GateResult<ApprovedSchemeDelete>.Allow(new ApprovedSchemeDelete(scheme)) : Deny<ApprovedSchemeDelete>(d);
    }
}
