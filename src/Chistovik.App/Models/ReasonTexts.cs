namespace Chistovik.App.Models;

/// <summary>
/// Таблица «код правила политики → объяснение человеческим языком». Коды берутся из Core.Policy (поле Rule решения);
/// если код неизвестен или пуст, показывается общий текст: система не уверена, значит только показывает.
/// </summary>
public static class ReasonTexts
{
    public const string Unsure = "Система не уверена, поэтому только показываем.";

    // Коды, которые формирует сам App (политика их не знает).
    public const string ServiceNoAccess = "service-access-denied";
    public const string TaskNoAccess = "task-access-denied";
    public const string StartupRunOnce = "startup-runonce";
    public const string StartupBadId = "startup-bad-id";

    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // службы
        ["bad-name"] = "Имя службы необычное, менять такую запись небезопасно.",
        ["service-hardlist"] = "Защищённая служба Windows: от неё зависят система или безопасность.",
        ["service-ppl"] = "Служба работает как защищённый процесс Windows, её запуск менять нельзя.",
        ["service-security-product"] = "Служба антивируса или брандмауэра: защиту не отключаем.",
        ["service-driver"] = "Это драйвер ядра, а не обычная служба.",
        ["service-shared"] = "Служба работает в общем процессе вместе с системными службами: запуск не меняем.",
        ["service-unknown-type"] = "Тип службы не распознан.",
        ["service-edge"] = "Служба Microsoft Edge: обновляется и чинится самим Edge.",
        ["service-no-image"] = "Не удалось определить файл службы.",
        ["service-in-windows"] = "Файл службы лежит в каталоге Windows.",
        ["service-system-zone"] = "Файл службы лежит в системной зоне (Defender, магазин, компоненты Windows).",
        ["service-has-dependents"] = "От этой службы зависят другие службы: отключение может сломать их.",
        ["service-microsoft-unowned"] = "Служба Microsoft, не связанная с установленной программой.",
        ["service-unknown"] = "У файла службы нет действительной подписи, и программу-владельца найти не удалось.",
        ["service-boot-start"] = "Служба запускается вместе с ядром системы.",
        ["service-elevate-outside-program-files"] = "Файл службы вне Program Files: включать её запуск напрямую нельзя.",
        [ServiceNoAccess] = "Windows не даёт прочитать настройки этой службы без прав администратора.",
        // задачи
        ["task-bad-path"] = "Путь задачи содержит недопустимые символы.",
        ["task-protected-tree"] = "Задача Windows или самого Чистовика: служебные задачи не меняем.",
        ["task-no-actions"] = "У задачи нет распознанных действий.",
        ["task-action-not-exec"] = "Задача запускает не программу (например, COM-обработчик): последствия изменения неясны.",
        ["task-action-unresolved"] = "Не удалось определить, какую программу запускает задача.",
        ["task-exec-in-windows"] = "Задача запускает файл из каталога Windows.",
        ["task-exec-system-zone"] = "Задача запускает файл из системной зоны.",
        ["task-exec-outside-roots"] = "Задача запускает программу вне Program Files, ProgramData и папок пользователей.",
        ["task-elevated"] = "Задача с повышенными правами: напрямую не меняем, откатывается только собственное изменение.",
        ["task-privileged-principal"] = "Задача от имени SYSTEM или служебной учётной записи: напрямую не меняем.",
        ["task-unknown-runlevel"] = "Уровень прав задачи не распознан.",
        ["task-unknown-principal"] = "Не указана учётная запись задачи.",
        [TaskNoAccess] = "Планировщик не даёт прочитать эту задачу без прав администратора.",
        // автозагрузка
        ["startup-security-component"] = "Компонент безопасности Windows: защиту не отключаем.",
        ["startup-unparsed"] = "Не удалось однозначно определить, какую программу запускает запись.",
        ["startup-microsoft-in-windows"] = "Файл Microsoft из каталога Windows.",
        ["startup-in-windows"] = "Файл лежит в каталоге Windows.",
        ["startup-system-zone"] = "Файл лежит в системной зоне (Defender, Security, магазин).",
        ["startup-bad-signature-facts"] = "Сведения о подписи файла противоречат друг другу.",
        [StartupRunOnce] = "Однократный запуск: запись сама исчезнет после запуска.",
        [StartupBadId] = "Имя записи нельзя безопасно передать агенту.",
    };

    /// <summary>true — для кода есть собственный текст.</summary>
    public static bool IsKnown(string? rule) => !string.IsNullOrEmpty(rule) && Map.ContainsKey(rule);

    /// <summary>Объяснение причины. Неизвестный или пустой код даёт общий текст «система не уверена».</summary>
    public static string Explain(string? rule) => !string.IsNullOrEmpty(rule) && Map.TryGetValue(rule, out var text) ? text : Unsure;

    /// <summary>Для объектов, которые можно менять, объяснение причины не нужно: берётся пояснение политики.</summary>
    public static string ExplainOrKeep(Changeability cls, string? rule, string policyReason)
        => cls == Changeability.Modifiable ? policyReason : Explain(rule);
}
