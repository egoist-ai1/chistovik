# Чистка и оптимизация: спецификация модулей (25.10.2026)

Автор: архитектор (Fable), только чтение кода. Источник: запрос владельца «акцент на чистое удаление программ, пакетов, обновлений, очистку диска, реестра, автозагрузки, задач, служб, телеметрию и твики, с объяснением “почему”». Статус: проект, ничего не реализовано; пункты «Открытые вопросы» ждут решения владельца.

## Решение одной строкой

Расширяем Чистовик не новыми «удалялками», а одним слоем `Finding` + `Verdict` с четырьмя обязательными фразами (что это / зачем / что будет, если убрать / как вернуть). Под ним остаются прежние гарантии: Policy решает в агенте по собственному перечислению, в запросах только идентификаторы. Всё, что лежит в закрытых зонах (WinSxS, DriverStore, WindowsApps, Installer), меняет только сама ОС через штатный API пакетов, а не файловые операции агента. Одно сознательное ослабление: узкая примитивная операция «очистить категорию кэша» с удалением внутри корней, зашитых в сборку (не из запроса).

## 1. Удаление программ «всех видов»

Один запрос `Uninstall{FindingId}`. `FindingId` агент разрешает сам: `arp:{machine|user}:{x64|x86|user}:{ключ}`, `appx:{PackageFullName}`, `cbs:{PackageIdentity}` (обновления), `drv:{oemNN.inf}`. Строк удаления, GUID и путей в запросе нет — агент берёт их из своего инвентаря в момент выполнения (как `StartupEnumerator.Find`). Новая `PackagePolicy` классифицирует пакет по признакам (`Forbidden / ReadOnly / ExpertOnly / Modifiable`) и фиксирует допустимый механизм ОС:

| Вид | Механизм (только он) | Класс по умолчанию | Откат |
|---|---|---|---|
| MSI | `MsiConfigureProductEx(ABSENT, REBOOT=ReallySuppress)`, 3010 = перезагрузка | Modifiable; `SystemComponent=1` → ReadOnly | нет (ReinstallOnly) |
| EXE | `QuietUninstallString`; иначе интерактивно; тихие ключи только при распознанной сигнатуре (Inno/NSIS) | Modifiable | нет |
| Рантаймы и пары (VC++ Redist, .NET Runtime/Desktop Runtime, WebView2, Java, DirectX, OEM-фреймворки; `ParentKeyName` непустой) | то же | **ExpertOnly**, не предотмечены; вердикт «оставить»: «общая библиотека, от неё зависят другие программы» | нет |
| MSIX/Appx пользовательские | `PackageManager.RemovePackageAsync(pfn)` для SID клиента | Modifiable | повторная установка из Store |
| Appx inbox Microsoft (Xbox*, Solitaire, Tips, Feedback Hub, Clipchamp, Bing*, Teams consumer, Cortana, Copilot) | удаление **только для пользователя** (файлы остаются staged в WindowsApps → откат `RegisterPackageAsync(AppxManifest.xml)`); для всех пользователей + deprovision — Expert | user: Modifiable; all users: **ExpertOnly** | user: Undoable; all users: ReinstallOnly |
| Appx Framework/Resource/`IsFramework`/`SignatureKind=System`/`NonRemovable`; Store, DesktopAppInstaller, ShellExperienceHost, StartMenuExperienceHost, SecHealthUI, immersivecontrolpanel, AAD.BrokerPlugin, CloudExperienceHost, ContentDeliveryManager, Windows.Search, Edge/WebView2, UI.Xaml, VCLibs, .NET.Native | — | **Forbidden** | — |
| Обновления Windows (`DismGetPackages`) | только `DismRemovePackage` (dismapi.dll через P/Invoke, не wusa.exe) | показать всем; удаление — **ExpertOnly**, Irreversible; `Permanent=true` (SSU), Defender, язык/FOD → Forbidden | только через Windows Update |
| Драйверные пакеты | `pnputil /export-driver oemNN.inf <карантин>` → `pnputil /delete-driver oemNN.inf` **без** `/force` и `/uninstall` | Modifiable только если есть более новая версия того же (Provider, Class, OriginalName) и ни одно устройство не ссылается на inf; иначе ReadOnly | `pnputil /add-driver` (до очистки карантина) |

Запрет на WinSxS/DriverStore/WindowsApps/Installer относится к **файловым операциям агента** (`MoveToQuarantine`, `CleanCategory`, Purge) и не ослабляется. Изменения внутри этих зон делает компонент ОС (msiexec, AppX Deployment, CBS, PnP), вызванный по идентификатору пакета.

**Запрещено:** удалять Edge/WebView2, Store, winget, inbox-каркасы; `pnputil /force`, `/uninstall`; `wusa`; `DISM /ResetBase`; трогать `C:\Windows\Installer`, `Package Cache` (не раньше этапа C4 и только сирот); убивать процессы (`TerminateProcess`) — только Restart Manager без принуждения; подставлять тихие ключи «наугад»; предотмечать рантаймы и пары.

**Протокол и журнал:** `OperationKind.Uninstall`; шаги `UninstallRun`, `AppxRemove`, `UpdateRemove`, `DriverDelete`; для `UninstallRun`/`UpdateRemove`/`AppxRemove(all)` поле шага `revertible=false`; `UndoService` на предпроходе возвращает `not-revertible` и список таких шагов, остальные откатываются. Экран показывает обратимость **до** выполнения. Ожидание деинсталлятора: Job Object + мьютекс `_MSIExecute` + опрос процессов из `InstallLocation`, по таймауту вопрос пользователю.

**Sandbox:** MSI 7-Zip и EXE Notepad++ (NSIS): установить, удалить через настоящий `OperationHandler`, проверить `reg query` и отсутствие каталога. Appx: удаление присутствующего inbox-пакета для пользователя, затем `RegisterPackageAsync` из staged-пути, иначе ПРОПУСК. Негативы: `appx:Microsoft.VCLibs…` → `policy-denied`; `cbs:` с `Permanent=true` → отказ; `drv:` inbox inf без `oem` → отказ. Живое удаление драйверов «не проверено на железе».

## 2. Очистка диска

Три механизма, выбор по категории зашит в `cleanup.json` (ресурс сборки; агент не принимает путей):

1. **Обработчики ОС `IEmptyVolumeCache2`** (`HKLM\...\Explorer\VolumeCaches\*`): Windows.old / Previous Installations, Update Cleanup, Delivery Optimization Files, Thumbnail Cache, Windows Error Reporting, D3D Shader Cache, Setup/Upgrade Log Files, Device Driver Packages, Temporary Setup Files. Агент вызывает `Initialize → GetSpaceUsed → Purge` для конкретного handler-ключа из каталога. Корзина — `SHEmptyRecycleBin` для токена клиента. Обработчик «Windows Defender» исключён по имени ключа.
2. **Прямое удаление в корнях категорий** (новая примитивная операция, новая `CleanupPolicy`): `%LOCALAPPDATA%\Temp` клиента, `C:\Windows\Temp`, `CrashDumps`, `Minidump`, `LiveKernelReports`, `Logs\CBS\*.log` кроме текущего, `Logs\DISM`, кэши браузеров только если браузер не запущен (Restart Manager), `SoftwareDistribution\Download` (этап C4, `IUpdateInstaller.IsBusy`, временная остановка `wuauserv`/`bits` узкой операцией `ServiceTransientStop`). Правила на каждый файл: открыть с `FILE_FLAG_OPEN_REPARSE_POINT|BACKUP_SEMANTICS`; reparse-точки **не следуются и не удаляются**; `GetFinalPathNameByHandle(VOLUME_NAME_GUID)` обязан лежать под корнем категории; возраст ≥ 24 ч для Temp; занято → пропуск; удаление `SetFileInformationByHandle(FileDispositionInfoEx, POSIX|IGNORE_READONLY)` тем же хэндлом; **никогда** сам корень. Почему не карантин: содержимое регенерируемое, откат бессмыслен, а место не освобождается.
3. **Карантин** (`MoveToQuarantine`): сироты `Package Cache` (ExpertOnly), крупные установщики в `Downloads` старше 90 дней — только показать.

Предпросмотр: App без прав считает пользовательские категории сам; системные — через read-only запрос агента `MeasureCleanup{Categories}`. Никаких счётчиков «найдено N проблем».

**Запрещено:** путь или маска в запросе; удаление за пределами разрешённого конечного пути; Prefetch, Panther, `System Volume Information`, `$Recycle.Bin` файлами, профили других пользователей, cookies/история/пароли браузеров, `Package Cache` и `Installer` не-сироты, `ResetBase`, hiberfil/pagefile.

**Протокол и журнал:** `CleanCategories{Categories:[id], MeasureOnly}` (1–32 id из каталога). `OperationKind.Cleanup`; шаг `CleanupCategory` на категорию (before `{bytes, files}`, after `{bytes:0}`, факт `{bytes, files, skipped}`); новый флаг плана `ContinueOnFailure`; `revertible=false`; список путей — в `%ProgramData%\Chistovik\cleanup\<opId>.log`.

**Sandbox:** `%TEMP%` и `C:\Windows\Temp` с файлами −2 дня, junction на `C:\Windows\System32`, symlink на файл вне корня, эксклюзивно открытый файл, свежий файл. Критерий: удалены только подходящие, junction/symlink остались и цель нетронута, занятый и свежий пропущены, `before − after` совпадает с суммой удалённых ±1 %. Обработчики: перечислить VolumeCaches, `GetSpaceUsed` для Thumbnail/WER/Shader, Purge одного; «Windows Defender» → `policy-denied`.

## 3. Чистка реестра: сироты с доказательством

Не «чистка от ошибок», а находки, у каждой `Evidence` («файл X не существует»), которое агент перепроверяет перед удалением.

| Класс | Доказательство | Уровень | Действие |
|---|---|---|---|
| R1 `Uninstall\<x>`: exe из `UninstallString` и `InstallLocation` отсутствуют, не MSI (или `MsiQueryProductState≠INSTALLED`), не `SystemComponent` | файл | L2 | удалить ключ |
| R2 `App Paths\<exe>` на отсутствующий файл | файл | L2 | удалить ключ |
| R3 `Run`/`RunOnce`: команда разобрана, файл отсутствует | файл | L2 | удалить значение + байты StartupApproved (шаг `StartupRemove`; нужна узкая ветка в `RegistryPolicy`) |
| R4 `SharedDLLs`: путь к отсутствующему файлу | файл | L2 | удалить значение |
| R5 `MuiCache` | файл | L2, польза ≈ 0 | позже, только показать |
| R6 Ассоциации `.ext` → ProgID с отсутствующей командой | файл | L3 | только показать |
| R7 `CLSID`/`TypeLib`/`Interface` без `InprocServer32`/`LocalServer32` | файл | L3 (много ложных) | только показать; Expert с бэкапом |
| R8 Служба без файла | файл | L3 | только показать; удаление — этап C4 |
| R9 `SOFTWARE\<Vendor>\<Product>` без программы | имя | L3; общий вендор → L4 | показать; L4 никогда |

Бэкап обязателен: `RegCopyTree` → `HKLM\SOFTWARE\Chistovik\Quarantine\<opId>\<n>` **и** текстовый `.reg` (свой сериализатор, не `reg.exe`) в `%ProgramData%\Chistovik\quarantine\<opId>\<n>.reg`, затем `RegDeleteTree` через тот же хэндл с проверкой `ResolvedPath`/`!IsSymbolicLink`. Откат: `RegCopyTree` обратно; если ключ появился — конфликт по существующей схеме.

«Ремонт реестра» как понятие убрать. Вместо него раздел «Целостность системы»: `DismRestoreImage(RestoreHealth)` и `sfc /scannow` (единственный допустимый spawn — `sfc.exe` из System32 с `/scannow`), результат — отчёт. Сброс ассоциаций — никогда; дефрагментация реестра — никогда.

**Запрещено:** всё, что `RegistryPolicy` уже отвергает (Policies, Services, CurrentControlSet, структурные Classes, AppX*/PackagedCom, Defender/UAC/SmartScreen); удалять ключ без `Evidence`; ключ с >50 подключей как «сироту» (→ ExpertOnly); HKCU напрямую (только HKU\<SID клиента>).

**Протокол и журнал:** `ApplyFindings` (§7). Шаг `RegistryKeyDelete`: target `{root, sid?, view, key, evidenceKind, evidenceTarget}`, before `{exists:true, copy, treeSha256}`, after `{exists:false}`.

**Sandbox:** `...\Uninstall\ChistovikTest_Orphan` с `UninstallString` на несуществующий exe и `ChistovikTest_Live` на существующий; скан даёт ровно одну находку; `.reg` содержит все значения; `Undo` возвращает байт-в-байт (`reg export` до/после). Симлинк ключа HKU→HKLM → отказ `reg-symlink`.

## 4. Автозагрузка: вердикты

Чистая функция в Core (`StartupPolicy.Classify`) из фактов: издатель/подпись, путь, существование файла, каталог ролей `startup-known.json`, события 100/101 `Diagnostics-Performance`.

```
enum VerdictKind { Remove, Optional, Keep, Protected, Unknown }
record Verdict(VerdictKind Kind, string Headline, IReadOnlyList<Reason> Reasons,
               Risk Risk, Reversibility Reversibility, Confidence Confidence);
record Reason(string Code, string Text, string? Evidence);
```

Закрытый список кодов: `class-forbidden`, `class-readonly`, `file-missing`, `publisher-microsoft`, `publisher-known`, `unsigned`, `path-program-files`, `path-appdata`, `path-temp`, `role-updater`, `role-tray`, `role-cloud-sync`, `role-game-launcher`, `role-device-helper`, `role-security`, `impact-ms:<n>`, `impact-unknown`. Правила (первое совпавшее): Forbidden → Protected; ReadOnly/нераскрытая команда → Unknown; `file-missing` → Remove («программа удалена, запись бесполезна»; действие R3); `role-security|device-helper` → Keep; `role-updater` с валидной подписью → Remove («обновления можно запускать вручную»); `path-temp` или unsigned в AppData → Optional, Risk=Medium; остальное → Optional. Действие остаётся `SetStartupEntry`. **Запрещено:** секунды «влияния» без события 100/101; Remove без подписи или без `file-missing`; менять вердикт данными из интерфейса.

## 5. Задачи и службы

Проблема: текущий классификатор закрывает то, что хочет владелец (`DiagTrack`, `dmwappushservice`, `Xbl*`, `WerSvc`, `MapsBroker` — ShareProcess → Forbidden; телеметрические задачи `\Microsoft\Windows\*` → Forbidden). Решение — **каталог-исключение** `services-tasks-catalog.json` (ресурс сборки): точные имена/пути, вердикт, допустимые цели (только понижение `Manual|Disabled`), заметки об редакциях и «WU вернёт». `ServicePolicy.Classify`: имя в каталоге **и** не HardList **и** не PPL **и** не драйвер **и** `DependentCount==0` (проверка в момент выполнения) → Modifiable с целью из каталога. `TaskPolicy`: `\Microsoft\*` остаётся Forbidden, кроме путей каталога, и только для `disable`. Тест: пересечение каталога с HardList пусто; каталог не содержит `wuauserv|UsoSvc|WaaSMedicSvc|WinDefend|wlidsvc|LicenseManager|ClipSVC|StateRepository|AppXSvc|TokenBroker|TextInputManagementService|Dnscache`.

Состав каталога (предложение, ждёт подтверждения, вопрос 4):
- Службы Remove: `RetailDemo`, `WMPNetworkSvc`, `RemoteRegistry`, `Fax`, `DiagTrack`, `dmwappushservice`.
- Optional: `WerSvc`, `MapsBroker`, `lfsvc`, `XblAuthManager`/`XblGameSave`/`XboxNetApiSvc`/`XboxGipSvc` (игровой ПК → по умолчанию Keep), `Spooler` (если принтеров нет), `SSDPSRV`/`upnphost`, `SharedAccess`, `TermService`, `wisvc`, `SCardSvr`.
- Keep с объяснением: `WSearch`, `SysMain`, `PcaSvc`, `WpnService`, `TabletInputService`.
- OEM-обновлялки (gupdate, AdobeUpdateService, Razer/Logitech/ASUS *Update*) — Optional с предупреждением.
- Задачи Remove: `Application Experience\Microsoft Compatibility Appraiser`, `ProgramDataUpdater`, `Customer Experience Improvement Program\Consolidator`, `UsbCeip`, `DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector`, `Feedback\Siuf\DmClient*`, `Windows Error Reporting\QueueReporting`, `Autochk\Proxy`, `Maps\MapsUpdateTask`.
- Задачи Keep: `Application Experience\StartupAppTask`, `Servicing\StartComponentCleanup`, `.NET Framework\NGEN*`, `SystemRestore\SR`, `Device Setup\Metadata Refresh`.
- Задачи Forbidden: `UpdateOrchestrator\*`, `WindowsUpdate\*`, `Windows Defender\*`, `WaaSMedic`, `Chkdsk`, `Time Synchronization`, `BitLocker`, `TPM`, `Shell\*`. Edge update — ReadOnly.

**Запрещено:** отключать то, чего нет в каталоге; отключать с `DependentCount>0`; повышать запуск напрямую; `Disabled` для служб с триггерами без предупреждения. Протокол без изменений (`SetServiceStart`, `SetTaskEnabled`); в `Verdict.Reasons` добавляется `wu-may-revert`.

**Sandbox:** `DiagTrack` Manual/Disabled через каталог → ok, откат → ok; `BITS` → `service-hardlist`; Appraiser: выключение ok, включение напрямую → отказ, откатом → ok; `UpdateOrchestrator\*` → отказ.

## 6. Телеметрия и приватность

Каталог `tweaks.json` версии 2 с полями `editions: [home, pro, ent]`, `revertedBy: none|feature-update|cumulative-update|app`, `scope`, `verified`.
- Значения: `AllowTelemetry` (на Home/Pro = «Необходимые»), `AdvertisingInfo\Enabled`, `Privacy\TailoredExperiencesWithDiagnosticDataEnabled`, `ContentDeliveryManager` (SilentInstalledAppsEnabled, OemPreInstalledAppsEnabled, PreInstalledAppsEnabled, SoftLandingEnabled, SubscribedContent-338388/338389/353694/353696Enabled, SystemPaneSuggestionsEnabled, RotatingLockScreenOverlayEnabled), `InputPersonalization`, `Siuf\Rules\NumberOfSIUFInPeriod=0`, `CapabilityAccessManager\ConsentStore\{location,userAccountInformation,appDiagnostics}`=Deny, `DODownloadMode=1`, `TaskbarDa=0`, `BingSearchEnabled=0`, `CortanaConsent=0`.
- Политики (каждая — отдельная allow-строка `PoliciesMicrosoftWriteAllow`): `System\{EnableActivityFeed, PublishUserActivities, UploadUserActivities}` (Pro+), `WindowsCopilot\TurnOffWindowsCopilot`, `WindowsAI\DisableAIDataAnalysis`, `CloudContent\DisableWindowsConsumerFeatures` (**только Enterprise/Education**, помечать), `Windows Search\DisableWebSearch`; `Remote Assistance\fAllowToGetHelp=0`.
- Пресет «Приватность» в `modes.json` собирает значения + службы/задачи + Appx с предпросмотром.

Что откатывает Windows: ContentDeliveryManager и Appx-провизия — feature update; задачи Appraiser/CEIP — накопительные; `DiagTrack` — feature update. Записывается в `revertedBy` и в объяснение.

**Запрещено:** Defender/SmartScreen/UAC, Windows Update и его службы/политики, `wlidsvc`/лицензирование, hosts/DNS/брандмауэр для «блокировки телеметрии» (сеть не трогаем), уведомления Windows Security, Core Isolation, Hello. Новые allow-строки получают негативные тесты (значение рядом в той же ветке Policies — отказ).

## 7. Общая модель Finding и экран «Чистка»

```
record Finding(
  string Id,                    // "<area>:<kind>:<canonical>", ≤300 симв., агент разрешает сам
  FindingArea Area,             // Programs|Appx|Updates|Drivers|Disk|Registry|Startup|Services|Tasks|Privacy
  FindingObject Object,         // Display, Path/Key/Name, Publisher, Signature, SizeBytes, InstallDate
  Verdict Verdict,
  Explanation Explanation,      // WhatIsIt, WhyNeeded, IfRemoved, HowToUndo — четыре обязательные строки
  ChangeClass Class,            // Forbidden|ReadOnly|RevertOnly|Modifiable (+ ExpertOnly)
  Confidence Confidence,        // L0..L4
  Reversibility Reversibility,  // Undoable | UndoableUntilPurge(7d) | ReinstallOnly | Irreversible
  Effect Effect,                // Bytes?, StartupMs?, Count?
  FindingAction Action,
  bool PreselectAllowed);       // Verdict=Remove && Risk<=Low && Reversibility!=Irreversible && Confidence<=L2 && Class=Modifiable
```

Сканеры `IFindingScanner` (Core) работают в App без прав; агент при выполнении **пересобирает находку сам** по `Id` и заново классифицирует — данные App недоверенные. Поток: «Сканировать» → список по областям с фильтром по вердикту и раскрываемым объяснением → выбор (предотмечено только `PreselectAllowed`) → «Предпросмотр» = `ApplyFindings{Items, DryRun:true}` (план с кодами `denied`, `not-found`, `changed`, `not-revertible`, без записи в журнал) → «Выполнить» (`DryRun:false`) → итог «удалили N из M» → «Откатить» в журнале. Один UAC на сеанс.

Протокол: `ApplyFindings{Items:[{FindingId, Action}] (1–256), DryRun, ExpertConfirmations?:[FindingId]}`. Подтверждение эксперта агент принимает только если нормализованная цель `PolicyDecision.Target` совпадает с объектом, выведенным им самим из `FindingId`. Deny не снимается. Запрещённые поля прежние: пути, ключи, команды, SID.

Журнал: `OperationKind.Cleanup` (смешанные шаги), `user_version=2`; новые `StepKind`: `UninstallRun, AppxRemove, UpdateRemove, DriverDelete, CleanupCategory, RegistryKeyDelete, StartupRemove, FileQuarantine, QuarantinePurge`; таблица `quarantine_item(step_id, src, dst, sha256, sddl, attrs, times, link_target)`; колонка `step.revertible`. Новые `Approved*` с `internal`-конструкторами и `StepGate.Check*` на каждый вид; `StepFactory.Rebuild` строит каждый новый вид из `target_json` с повторной проверкой. `StepRunner`: `ContinueOnFailure` для Cleanup; итог `partial`. Новые коды шага: `not-revertible`, `timeout`, `in-use`, `skipped-by-rule`.

**Запрещено:** автовыбор вне `PreselectAllowed`; «одна кнопка — почистить всё»; счётчики проблем; вердикт из App в запросе.

## 8. Этапы поставки и антивирусы

| Этап | Состав | Критерий приёмки |
|---|---|---|
| **C0 Вердикты** | `Finding`/`Verdict`/`Explanation`, сканеры автозагрузки/служб/задач/программ (чтение), каталоги `startup-known.json` и `services-tasks-catalog.json`, экран «Чистка» (без новых действий), carve-out телеметрии в `ServicePolicy`/`TaskPolicy` + негативные тесты | У каждой находки четыре фразы и ≥1 код причины; пересечение каталога с HardList пусто (тест); `DiagTrack` Disabled/откат на подставных портах; ни одна служба вне каталога не стала Modifiable |
| **C1 Удаление программ** | `PackagePolicy`, `Uninstall`, `UninstallRun`/`AppxRemove(user)`, Job Object, Restart Manager, `not-revertible` в Undo, миграция журнала v2 | MSI/EXE/Appx на подставных портах: применение, обрыв, восстановление; рантаймы/пары → ExpertOnly, не предотмечены; Framework/Store → отказ; Sandbox: 7-Zip MSI, Notepad++ |
| **C2 Очистка диска** | `cleanup.json`, `CleanupPolicy`, `IEmptyVolumeCache2`, прямое удаление в корнях, `MeasureCleanup`, Корзина, `ContinueOnFailure` | Property-тест: ни один конечный путь вне корня не удаляется (300+ путей с junction/symlink/8.3/GUID тома); Sandbox §2; Defender-обработчик → отказ |
| **C3 Реестр и приватность** | R1–R4, `RegistryKeyDelete` с `RegCopyTree`+`.reg`, `StartupRemove`, tweaks v2, allow-строки Policies, пресет «Приватность», Appx all-users/deprovision (Expert), `ExpertConfirmations` | Удаление без Evidence → отказ; `.reg` восстанавливает байт-в-байт; у каждой allow-строки есть негативный сосед |
| **C4 Остатки и пакеты ОС** | M4 плана (USN-дифф, `FileQuarantine`, карантин 7 дней), `DriverDelete` с export, `UpdateRemove` через DISM API (Expert), `SoftwareDistribution` с `ServiceTransientStop`, R8 | 0 ложных удалений на парах; откат карантина хеш+SDDL; `Permanent` пакет → отказ |
| Отложено | R5–R7, R9, ассоциации, кэши браузеров при запущенном браузере, `Package Cache` сироты, ETW | — |

Антивирусы: риск реальный (BCU #770 «Wacatac», см. FACTS). Неподписанный exe, который перечисляет ARP, запускает msiexec, массово удаляет файлы и правит Run/службы — прямое попадание в поведенческую эвристику. Меры: сканирование и вердикты только в неповышенном App; агент без PowerShell, `cmd`, `reg.exe`, без динамической загрузки сборок; `sfc.exe`/`pnputil.exe` — единственные spawn, с фиксированными аргументами; удаление файлов только через хэндл в корнях категорий (не рекурсивный `Directory.Delete`); никаких слов «Optimizer/Booster» в метаданных; VirusTotal и запуск с включённым Defender в Sandbox перед каждым релизом, заявка WDSI при срабатывании. Риск по этапам: C0 низкий, C1 средний, C2 средний, C3 низкий, C4 высокий.

## Открытые вопросы владельцу

1. Прямое удаление кэшей (место освобождается сразу, отката нет) против карантина 7 дней (откат есть, место позже). Рекомендация: прямое для категорий §2.2; меняет границу AGENTS.md.
2. Inbox-приложения Microsoft: по умолчанию удалять только для пользователя (откат есть, после feature update вернутся) или сразу deprovision (Expert)? Рекомендация: первое.
3. Удаление обновлений Windows: оставить (Expert, необратимо) или только показывать?
4. Подтвердить список carve-out телеметрии (§5) поимённо: единственное место, где политика ослабляется для общих svchost и `\Microsoft\*`.
5. Xbox-службы и Game Bar на игровом ПК: по умолчанию «оставить» или «по желанию»?
6. «Ремонт реестра» переименовать в «Целостность системы» (DISM RestoreHealth + SFC)?
7. Кэши браузеров: пропускать запущенный браузер с подсказкой или не включать категорию?

## Противоречия с текущими документами (поправить)

1. `AGENTS.md` «в Agent нет удалить файл» → «нет удаления по пути из запроса; удаление только внутри корней категорий из зашитого каталога и внутри карантина, по хэндлу».
2. `AGENTS.md`/`PLAN.md §2` «WinSxS/DriverStore/WindowsApps/Installer закрыты всегда» → добавить «для файловых операций агента; изменения в них делает ОС через API пакетов».
3. `TaskPolicy` Forbidden `\Microsoft\*` и `ServicePolicy` ShareProcess→Forbidden противоречат цели «отключить телеметрию» → каталог-исключение §5 с тестом на HardList.
4. `COUNCIL.md §4` «Никогда: чистка реестра, кэши в v2, счётчики проблем» → «сироты с доказательством, без счётчиков»; `PRODUCT.md` «Не цели» уточнить так же.
5. `DESIGN-0.1 §0/§2` «ExpertOnly считается Deny, поля эксперт нет» против «Эксперт нужен» → протокол получает `ExpertConfirmations`.
6. `RegistryPolicy` `reg-run-keys` закрывает Run как значение → для R3 нужна узкая ветка удаления по Evidence.
7. `StepRunner` «стоп на первой ошибке» → для Cleanup `ContinueOnFailure`.
8. `PLAN.md §2` «WindowsUpdate не трогаем» → настройки и службы WU не меняем; кэш WU чистится обработчиком ОС; пакеты удаляются только через DISM API и только эксперт.
9. `PathPolicy` `Package Cache` = ExpertOnly против `COUNCIL` «запретная зона» → ExpertOnly только для сирот, не раньше C4.
10. Срок карантина 7 дней и очистка «при запуске агента» без резидента → записать в UI: карантин стирается при следующем запуске агента (UAC), не по расписанию.

## Что проверить до реализации

Наличие и имена `IEmptyVolumeCache`-обработчиков в образе Sandbox и их работа in-proc; `DeprovisionPackageForAllUsersAsync` и `RegisterPackageAsync` из staged-пути; `DEVPKEY_Device_DriverInfPath` как признак использования драйвера; маршалинг DISM API из .NET; реакция Defender на массовое удаление в Temp из повышенного процесса (только живой прогон); в Sandbox нет Store и WU, поэтому Appx-переустановка и удаление KB проверяются лишь негативами.
