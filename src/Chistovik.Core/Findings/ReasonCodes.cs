namespace Chistovik.Core.Findings;

/// <summary>
/// Закрытый список кодов причин. Код нельзя придумать на лету: каждая причина вердикта берётся отсюда,
/// текст у кода один и тот же везде. Семейство <c>impact-ms:&lt;n&gt;</c> зарезервировано под измерения событий 100/101
/// и в C0 не выдаётся: время запуска без измерения не показываем.
/// </summary>
public static class ReasonCodes
{
    public const string ClassForbidden = "class-forbidden";
    public const string ClassReadOnly = "class-readonly";
    public const string FileMissing = "file-missing";
    public const string CommandUnresolved = "command-unresolved";
    public const string PublisherMicrosoft = "publisher-microsoft";
    public const string PublisherKnown = "publisher-known";
    public const string Unsigned = "unsigned";
    public const string PathProgramFiles = "path-program-files";
    public const string PathAppData = "path-appdata";
    public const string PathTemp = "path-temp";
    public const string RoleUpdater = "role-updater";
    public const string RoleTray = "role-tray";
    public const string RoleCloudSync = "role-cloud-sync";
    public const string RoleGameLauncher = "role-game-launcher";
    public const string RoleDeviceHelper = "role-device-helper";
    public const string RoleSecurity = "role-security";
    public const string RoleTelemetry = "role-telemetry";
    public const string RuntimeShared = "runtime-shared";
    public const string ProgramInstalled = "program-installed";
    public const string CatalogAdvice = "catalog-advice";
    public const string WuMayRevert = "wu-may-revert";
    public const string ImpactUnknown = "impact-unknown";
    public const string TriggerStart = "trigger-start";

    // C2 и C3: очистка диска и записи реестра без файлов.
    public const string CacheRegenerable = "cache-regenerable";
    public const string EvidenceInstallDirMissing = "evidence-install-dir-missing";
    public const string EvidenceUninstallerMissing = "evidence-uninstaller-missing";
    public const string EvidenceMsiUnknown = "evidence-msi-unknown";
    public const string EvidenceFileMissing = "evidence-file-missing";
    public const string DriveMissing = "drive-missing";
    public const string KeyTooLarge = "key-too-large";
    public const string BrowserRunning = "browser-running";
    public const string SizeNeedsAgent = "size-needs-agent";

    static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        [ClassForbidden] = "Объект защищён: программа не позволяет его менять.",
        [ClassReadOnly] = "Объект можно только смотреть: безопасно изменить его программа не может.",
        [FileMissing] = "Файл, который запускает запись, не найден на диске.",
        [CommandUnresolved] = "Команду запуска не удалось разобрать, поэтому совет не даётся.",
        [PublisherMicrosoft] = "Файл подписан Microsoft.",
        [PublisherKnown] = "Программа есть во встроенном каталоге известных.",
        [Unsigned] = "У файла нет действительной цифровой подписи.",
        [PathProgramFiles] = "Файл лежит в Program Files, куда программы ставит установщик.",
        [PathAppData] = "Файл лежит в профиле пользователя (AppData).",
        [PathTemp] = "Файл лежит во временной папке.",
        [RoleUpdater] = "Запись нужна только для проверки обновлений программы.",
        [RoleTray] = "Программа живёт в области уведомлений и ждёт, пока её откроют.",
        [RoleCloudSync] = "Программа синхронизирует файлы с облаком.",
        [RoleGameLauncher] = "Программа запускает или обслуживает игры.",
        [RoleDeviceHelper] = "Помощник устройства: отвечает за работу оборудования.",
        [RoleSecurity] = "Относится к защите компьютера.",
        [RoleTelemetry] = "Относится к сбору диагностических данных и отправке их в Microsoft.",
        [RuntimeShared] = "Общая библиотека: от неё могут зависеть другие программы.",
        [ProgramInstalled] = "Обычная установленная программа.",
        [CatalogAdvice] = "Совет взят из встроенного каталога.",
        [WuMayRevert] = "Windows может вернуть настройку после обновления.",
        [ImpactUnknown] = "Влияние на время запуска не измерялось.",
        [TriggerStart] = "Запускается по событию: отключение остановит и такие запуски.",
        [CacheRegenerable] = "Это кэш: программы создают его заново, когда он понадобится.",
        [EvidenceInstallDirMissing] = "Папки, куда программа была установлена, на диске нет.",
        [EvidenceUninstallerMissing] = "Файла удаления программы на диске нет, запустить его нечем.",
        [EvidenceMsiUnknown] = "Windows Installer не знает такого продукта: программа уже удалена.",
        [EvidenceFileMissing] = "Файла, на который указывает запись, на диске нет.",
        [DriveMissing] = "Диск, на котором должен лежать файл, сейчас недоступен. Возможно, он просто отключён, поэтому запись не трогаем.",
        [KeyTooLarge] = "В записи слишком много вложенных ключей, автоматически её не удаляем.",
        [BrowserRunning] = "Браузер сейчас запущен: его кэш не очищается, пока он открыт.",
        [SizeNeedsAgent] = "Точный размер покажет агент после запроса прав администратора.",
    };

    /// <summary>Роли, которые может назначить запись каталога автозагрузки.</summary>
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        RoleUpdater, RoleTray, RoleCloudSync, RoleGameLauncher, RoleDeviceHelper, RoleSecurity,
    };

    /// <summary>Коды, которые запись каталога служб и задач может приложить к вердикту.</summary>
    public static readonly IReadOnlySet<string> CatalogReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        RoleUpdater, RoleGameLauncher, RoleSecurity, RoleTelemetry, RoleDeviceHelper, RoleTray, RoleCloudSync,
    };

    public static IEnumerable<string> All => Texts.Keys;

    public static bool IsKnown(string code) => Texts.ContainsKey(code);

    /// <summary>Текст кода. Неизвестный код: исключение, чтобы опечатка не доехала до экрана.</summary>
    public static string TextOf(string code)
        => Texts.TryGetValue(code, out var t) ? t : throw new ArgumentOutOfRangeException(nameof(code), code, "Код причины не из списка");

    public static Reason Make(string code, string? evidence = null) => new(code, TextOf(code), evidence);
}
