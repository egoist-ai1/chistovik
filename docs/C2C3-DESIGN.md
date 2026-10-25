# C2 «Очистка диска» и C3 «Чистка реестра»: проект реализации

Статус: проект (25.10.2026, автор: архитектор Fable, только чтение кода). Основа: `docs/CLEANUP-SPEC.md` (§2, §3, §6–§8), `docs/C1-DESIGN.md` (образец), `docs/DESIGN-0.1.md`, `AGENTS.md`. Решения владельца: «делаем всё и по максимуму»; разрушительные проверки только в Windows Sandbox; юнит-тесты на хосте только во временных каталогах и на подставных портах, настоящих системных путей не касаются. Открытые вопросы спецификации закрыты её рекомендациями: кэши без карантина; inbox-Appx вне объёма; удаление обновлений Windows только показывать; Xbox «оставить»; «Ремонт реестра» не делаем; кэш запущенного браузера пропускаем.

## Решение

**C2.** Агент получает единственную новую «удаляющую» операцию `CleanCategories`: удаление по хэндлу внутри корней из встроенного `catalog/cleanup.json`. Корень берётся из каталога и SID клиента, никогда из запроса. Каждый файл открывается с `FILE_FLAG_OPEN_REPARSE_POINT`, проверяется `GetFinalPathNameByHandle` на принадлежность корню и удаляется `SetFileInformationByHandle(FileDispositionInfoEx)`. Шаги необратимы (`revertible=0`), операция продолжается при отказах (`ContinueOnFailure`). Корзина — отдельный шаг через `SHEmptyRecycleBin`, только если SID токена агента совпадает с SID клиента. `IEmptyVolumeCache2` в C2 не входит (C2b, критерий ниже).

**C3.** Сканеры в App (без прав) находят сироты R1–R4 и прикладывают Evidence; агент получает только идентификаторы (`ApplyFindings`), сам заново перечисляет записи, заново проверяет Evidence и только тогда планирует шаг. Удаление ключа (`RegistryKeyDelete`): собственный обход поддерева по хэндлам с `REG_OPTION_OPEN_LINK` (симлинк где угодно внутри → отказ), копия в `HKLM\SOFTWARE\Chistovik\Quarantine\<opId>\<n>` и `.reg` в `%ProgramData%\Chistovik\quarantine\<opId>\<n>.reg`, сверка SHA-256 копии с оригиналом, удаление по хэндлу (`NtDeleteKey`). Undo копирует обратно. R3/R4 (значения Run/RunOnce и SharedDLLs) удаляются отдельными StepKind с узкими ветками `RegistryPolicy`, которые не ослабляют `reg-run-keys` и `reg-protected-node`. Телеметрия/tweaks v2 и каталог служб/задач (§5–6) вынесены в C3b.

## Почему

- Агент удаляет файлы только внутри корней каталога, поэтому «удалить по пути из запроса» невозможно. Это снимает противоречие №1 спецификации с `AGENTS.md` поправкой формулировки.
- Доказательство сироты проверяет агент, а не App (App недоверенный, как в C1: `UninstallProbe.Read` читает факты заново). Идентификатор R4 это SHA-256 имени, а не путь.
- Копия ключа + `.reg` + SHA-сверка делают `RegistryKeyDelete` обратимым через существующий `UndoService` без новых таблиц; схема журнала остаётся `user_version=2`.
- `IEmptyVolumeCache2` требует COM-обработчиков `cleanmgr`, наличие которых в образе Sandbox неизвестно, поэтому C2b.

## 1. Состав поставки

### 1.1. C2: очистка диска

**`src/Chistovik.Catalog/data/cleanup.json`** встраивается как `catalog/cleanup.json` (csproj Core уже включает `..\Chistovik.Catalog\data\*.json`). Добавить в `AgentComposition.CatalogSha256()`.

Модель (`src/Chistovik.Core/Catalog/CleanupCatalog.cs`, строгие `JsonSerializerOptions` как у `TweakCatalog`, `UnmappedMemberHandling.Disallow`):

```csharp
public sealed record CleanupRoot(string Base, string Sub);   // Base ∈ {windir, programdata, systemdrive, userlocal, userroaming}
public sealed record CleanupCategory(
    string Id, string Title, string Description, string Scope,   // Scope ∈ {machine, user}
    IReadOnlyList<CleanupRoot> Roots, int MinAgeHours,
    IReadOnlyList<string>? KeepNames, IReadOnlyList<string>? Patterns,
    string Mechanism,                 // files | recyclebin | handler (в C2 только карточка без удаления)
    string Risk, bool DefaultOn, string? SkipIfRunning,
    string Restart);                  // none | explorer
public sealed class CleanupCatalog { public static CleanupCatalog Shared; public IReadOnlyList<CleanupCategory> Categories; public CleanupCategory? Find(string id); }
```

Категории C2 (`Mechanism=files`, `MinAgeHours=24`, если не сказано иное):

| Id | Scope | Корни (Base\Sub) | Особое |
|---|---|---|---|
| `temp-system` | machine | `windir\Temp` | |
| `temp-user` | user | `userlocal\Temp` | |
| `wer-system` | machine | `programdata\Microsoft\Windows\WER\ReportQueue`, `...\ReportArchive`, `...\Temp` | |
| `wer-user` | user | `userlocal\Microsoft\Windows\WER\ReportQueue`, `...\ReportArchive` | |
| `logs-cbs` | machine | `windir\Logs\CBS` | `KeepNames=["CBS.log"]`, возраст 7 суток |
| `d3d-shader-cache` | user | `userlocal\D3DSCache` | |
| `thumbcache` | user | `userlocal\Microsoft\Windows\Explorer` | `Patterns=[thumbcache_*.db, iconcache_*.db]`, `Restart=explorer`; занято → пропуск |
| `browser-edge` | user | `userlocal\Microsoft\Edge\User Data\Default\Cache`, `...\Code Cache`, `...\GPUCache` | `SkipIfRunning=msedge` |
| `browser-chrome` | user | `userlocal\Google\Chrome\User Data\Default\Cache`, `...\Code Cache`, `...\GPUCache` | `SkipIfRunning=chrome` |
| `browser-firefox` | user | `userlocal\Mozilla\Firefox\Profiles\*\cache2` | `SkipIfRunning=firefox`; `*` только один уровень и только в `Sub`; если усложняет политику, категорию убрать |
| `recycle-bin` | user | нет | `Mechanism=recyclebin`, `DefaultOn=false` |
| `minidump` | machine | `windir\Minidump` | `DefaultOn=false`, возраст 7 суток |
| `wu-cleanup`, `delivery-optimization`, `driver-packages` | machine | нет | `Mechanism=handler`: в C2 только карточка «размер неизвестен, очищается штатной очисткой диска» |

Не входит: `SoftwareDistribution\Download` (остановка служб, C2b, ExpertOnly), `Windows.old`/`$Windows.~BT` (только показывать), `Prefetch`, `Installer`, `WinSxS`, `Package Cache`, `userlocal\Packages\*`.

**`src/Chistovik.Core/Policy/CleanupPolicy.cs`** (новая, отдельно от `PathPolicy`, отвечающей за карантин):

```csharp
public sealed class CleanupPolicy(PolicyContext ctx)
{
    public static readonly string[] AllowedBases = { "windir", "programdata", "systemdrive", "userlocal", "userroaming" };
    public PolicyDecision CheckCatalog(CleanupCategory c);   // база из списка, Sub без "..", ":" и "\\\\", не пустой; machine-категории без user-баз и наоборот
    public PolicyDecision ResolveRoots(CleanupCategory c, string? profileDir, out IReadOnlyList<string> roots);  // user-база требует profileDir
    public PolicyDecision CheckProfileDir(string sid, string profileDir, DirFacts facts);  // не reparse; владелец = SID клиента, Administrators или SYSTEM; не под WindowsDir/ProgramFiles/ProgramData
    public PolicyDecision CheckRoot(string requestedRoot, FileFacts facts);   // существует, не reparse, FinalPath совпадает с ожидаемым (иначе cleanup-root-reparse / cleanup-root-moved); корень никогда не удаляется
    public PolicyDecision CheckEntry(string rootFinal, DirEntryFacts e, DateTimeOffset now, CleanupCategory c);  // reparse → cleanup-reparse; KeepNames → cleanup-keep-name; Patterns → cleanup-pattern; max(Created,LastWrite) моложе MinAgeHours → cleanup-too-fresh
    public PolicyDecision CheckOpened(string rootFinal, string entryFinal, bool isReparse);  // !IsEqualOrUnder(entryFinal, rootFinal) → cleanup-not-under-root; isReparse → cleanup-reparse
}
```

Коды правил: `cleanup-bad-catalog`, `cleanup-root-outside`, `cleanup-root-reparse`, `cleanup-root-moved`, `cleanup-root-missing` (not-found, пропуск), `cleanup-profile-reparse`, `cleanup-profile-owner`, `cleanup-profile-location`, `cleanup-reparse`, `cleanup-keep-name`, `cleanup-pattern`, `cleanup-too-fresh`, `cleanup-not-under-root`, `cleanup-browser-running`, `recyclebin-foreign-user`.

**Порты** (`src/Chistovik.Core/Ports/Cleanup.cs`):

```csharp
public sealed record DirEntryFacts(string Name, bool IsDirectory, bool IsReparse, long Length, DateTimeOffset Created, DateTimeOffset LastWrite);
public interface ICleanupFiles
{
    FileFacts Inspect(string path);                                  // FinalPath с VOLUME_NAME_GUID
    IEnumerable<DirEntryFacts> Enumerate(string directory);          // не рекурсивно, без открытия файлов
    IDeletableHandle? OpenForDelete(string path, bool isDirectory);  // FILE_FLAG_OPEN_REPARSE_POINT | BACKUP_SEMANTICS, DELETE | FILE_READ_ATTRIBUTES, share all; null если нет; ERROR_SHARING_VIOLATION → SharingViolationException
}
public interface IDeletableHandle : IDisposable
{
    string FinalPath { get; }   // GetFinalPathNameByHandle(VOLUME_NAME_GUID | FILE_NAME_NORMALIZED)
    bool IsReparse { get; }     // GetFileInformationByHandleEx(FileAttributeTagInfo)
    void Delete();              // SetFileInformationByHandle(FileDispositionInfoEx, DELETE|POSIX_SEMANTICS|IGNORE_READONLY_ATTRIBUTE); каталог только пустой (ERROR_DIR_NOT_EMPTY → пропуск)
}
public interface IRecycleBin { long QueryBytes(); void Empty(); string CurrentTokenSid(); }
public interface IProcessList { bool IsRunning(string imageName, string userSid); }
```

В `IUserFolders` (`Ports/Basics.cs`) добавить `string? ProfileDir(string userSid)` (тот же `ProfileList\<sid>\ProfileImagePath`, что в `WindowsEnvironment.StartupFolder`, строки 46–63). `userlocal` = `ProfileDir\AppData\Local`, `userroaming` = `ProfileDir\AppData\Roaming` (фиксированные подпапки, не из `Shell Folders`, иначе App смог бы перенаправить корень через HKCU).

**Native** (`src/Chistovik.Native/Cleanup/WindowsCleanupFiles.cs`, `WindowsRecycleBin.cs`, `WindowsProcessList.cs`; P/Invoke в новом `Interop/Win32Cleanup.cs`): добавить `FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000`, `DELETE = 0x00010000`, `SetFileInformationByHandle`, `GetFileInformationByHandleEx` (`FileAttributeTagInfo = 9`), `FILE_DISPOSITION_INFO_EX` (класс 21; флаги 0x1|0x2|0x10), `VOLUME_NAME_GUID = 0x1`, `SHQueryRecycleBinW`, `SHEmptyRecycleBinW` (флаги 7). Перечисление через `System.IO.Enumeration.FileSystemEnumerable<DirEntryFacts>` с `EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false }`. Запрещено удалять через `File.Delete`/`Directory.Delete` (идут по пути и следуют ссылкам). Fallback на `FileDispositionInfo` без POSIX для не-NTFS.

**Исполнение** (`src/Chistovik.Core/Execution/CleanupSteps.cs`):

```csharp
internal sealed record CleanupTarget(string Category, string Scope, string? Sid, IReadOnlyList<string> Roots);   // target_json
internal sealed class CleanupStep : PlannedStep        // Kind = CleanupCategory, Revertible = false
internal sealed class CleanupSession : IStepSession
internal sealed class RecycleBinStep : PlannedStep     // Kind = RecycleBinEmpty, Revertible = false
internal static class CleanupPlanner { Plan(ExecEnv, CleanCategoriesRequest, string clientSid); Measure(...) }
```

Алгоритм `CleanupSession.Write` по корням: `CheckRoot` → обход в глубину по `Enumerate`; для элемента `CheckEntry`; файл → `OpenForDelete` → `CheckOpened` → `Delete`; каталог → сначала дети, потом сам (`OpenForDelete(isDirectory:true)`, непустой → пропуск); reparse-каталог не входим и не удаляем; корень не удаляем. Счётчики `deleted/freedBytes/skipped{reason→n}/failed`. Отказ доступа и sharing violation это пропуск с причиной, не исключение. Лог в `%ProgramData%\Chistovik\cleanup\<opId>.log` (каталог создаётся как `ProtectedTemp.Create`; строки `D <финальный путь>` / `S <причина> <путь>`; предел 10 МиБ, потом `… truncated`). Пределы: ≤ 500 000 элементов и ≤ 20 минут на категорию, иначе `StepCodes.Timeout`. Корень перепроверяется каждые 1000 элементов.

`OperationPlan` получает `public bool ContinueOnFailure { get; init; }`; `StepRunner.RunPlan` (строка 126): после `Failed` `if (!plan.ContinueOnFailure) break;`. Итог: все Done → Done, часть → Partial, ни одного → Failed (предпроверка `SimState` остаётся: отказ политики → вся операция Denied).

Измерение (`MeasureOnly=true`): не журналируется; `OperationHandler.Measure(request, sid, ...)` под тем же `SemaphoreSlim`; результат в новом поле ответа `Facts`.

### 1.2. C3: чистка реестра

Идентификаторы (путей нет):

| R | Что | Идентификатор | Evidence (App предлагает, Agent проверяет заново) |
|---|---|---|---|
| R1 | `Uninstall\<key>` HKLM x64/x86, HKU | `arp:machine:{x64|x86}:{key}` (`Rules.ArpMachineId`), `arp:user:{x64|x86}:{key}` (SID клиента) | `uninstall-orphan`: `InstallLocation` отсутствует/пуст и exe из `UninstallString` не существует (`CommandResolver`); либо `msi-unknown`: `WindowsInstaller=1` и `MsiQueryProductState == -1`. Keep/Protected: `SystemComponent=1`, `ParentKeyName`, `ReleaseType ∈ {Update, Hotfix, Security Update}`, `InstallLocation` под `WindowsApps`/`Windows`, нет `DisplayName` → Unknown |
| R2 | `App Paths\<exe>` | `apppath:{machine|user}:{x64|x86}:{exe}`, regex `^apppath:(machine|user):(x64|x86):[^\\/:\u0000-\u001f\u007f]{1,255}\z` | значение по умолчанию (развёрнутые `%VAR%`, снятые кавычки): файла нет, диск существует |
| R3 | `Run`, `RunOnce` HKLM x64/x86, HKU | Run: `run:{hklm64|hklm32|hku}:{name}` (`StartupEntryId`); RunOnce: `runonce:{hklm64|hklm32|hku}:{name}` (новый `OrphanIds`) | `command-unresolved`; `rundll32`/`cmd`/`powershell`/`explorer` с аргументами → Unknown |
| R4 | `SharedDLLs` HKLM x64/x86 | `shareddll:{x64|x86}:{sha256hex(lower(valueName))}` | файла нет и диск существует (`Z:\…` без тома → Unknown `drive-missing`); `\\` → Unknown |

Новые методы `RegistryPolicy` (существующие ветки не меняются):

```csharp
CheckOrphanKeyDelete(RegKey k, OrphanEvidence e, int subkeyCount, bool expertConfirmed, string clientSid);
// = CheckDeleteKey(k, clientSid) ∧ e.Verified ∧ (subkeyCount ≤ 50 → Allow; ≤ 500 ∧ expertConfirmed → Allow("reg-tree-expert"); иначе Deny("reg-tree-too-large"))
CheckQuarantineDestination(RegKey dest, Guid opId, int n);   // Allow только HKLM\SOFTWARE\Chistovik\Quarantine\<opId:N>\<n>; иначе Deny("reg-quarantine-dest")
CheckStartupRemove(RegKey k, string valueName, OrphanEvidence e, string clientSid);   // Run/RunOnce x64/x86/HKU<sid>; e.Verified → Allow("reg-run-orphan")
CheckStartupRestore(RegKey k, string valueName, RegType type, string clientSid);      // откат: String/ExpandString ≤ 2048 → Allow("reg-run-restore")
CheckSharedDllRemove(RegKey k, string valueName, OrphanEvidence e);                   // только ...\CurrentVersion\SharedDLLs; Allow("reg-shareddll-orphan")
CheckOrphanKeyRestore(...)                                                            // Allow только для тех же двух родителей (Uninstall, App Paths)
```

`CheckSetValue` для `Run*` остаётся `reg-run-keys`; `HKLM\SOFTWARE\Chistovik` остаётся `reg-protected-node` для обычных записей (копия создаётся только внутри `ApprovedRegistryTreeDelete` после `CheckQuarantineDestination`). В `SoftwareDenyEqual` добавить Deny на удаление самого ключа `SharedDLLs` (`reg-structure`): единственная правка существующих таблиц.

Порт дерева (`Ports/Registry.cs`):

```csharp
public interface IRegistryTree { IRegTreeHandle? OpenTree(RegLocation loc, bool forWrite); }   // REG_OPTION_OPEN_LINK; KEY_READ|KEY_WOW64_64KEY (+KEY_WRITE|DELETE)
public interface IRegTreeHandle : IDisposable
{
    string ResolvedPath { get; } bool IsSymbolicLink { get; }
    RegKeyInfo Info(); IReadOnlyList<string> SubKeyNames();
    IRegTreeHandle? OpenSub(string name, bool forWrite);          // REG_OPTION_OPEN_LINK
    IReadOnlyList<(string Name, RegType Type, byte[] Raw)> RawValues();
    IRegTreeHandle CreateSub(string name); void SetRaw(string name, RegType type, byte[] raw);
    void DeleteSelf();                                            // NtDeleteKey(handle), нужен DELETE в SAM
}
```

P/Invoke: добавить `RegEnumKeyExW`, `RegQueryInfoKeyW`, `NtDeleteKey`, `DELETE 0x10000`. `RegCopyTree`/`RegDeleteTree` не используем (следуют по ссылкам).

Исполнение (`Execution/RegistryTreeSteps.cs`, `OrphanEvidence.cs`):

```csharp
internal sealed record OrphanEvidence(string Kind, IReadOnlyList<string> Paths, string? ProductCode);  // uninstall-orphan | msi-unknown | file-missing | command-unresolved
internal static class OrphanProbe { Uninstall(...); AppPath(...); RunValue(...); SharedDll(...); }   // агентская перепроверка
internal sealed class RegistryKeyDeleteStep : PlannedStep     // Revertible = true
internal sealed class RegistryKeyRestoreStep : PlannedStep    // копия обратно; оригинал существует → Conflict (только Skip)
internal sealed class StartupRemoveStep, SharedDllRemoveStep
internal static class RegistryTreeHasher { Sha256(IRegTreeHandle root, int maxSubkeys, long maxBytes, out int subkeys, out bool hasLink); }  // канонический порядок: имена подключей и значений lower, тип, длина, байты
internal static class RegExport { ToRegFile(string fullKeyPath, IRegTreeHandle root); }   // "Windows Registry Editor Version 5.00", hex(b): для не-String типов, UTF-16LE с BOM
internal static class FindingsPlanner { Plan(ExecEnv, ApplyFindingsRequest, string clientSid, Guid opId); }
```

Алгоритм `RegistryKeyDeleteStep`: `OpenTree(loc, write)` → `IsSymbolicLink` → `reg-symlink`; `ResolvedPath` ≠ `StepGate.ExpectedPath` → `reg-resolved-mismatch`; `OrphanProbe` → `evidence-failed`; `RegistryTreeHasher` (обход с OPEN_LINK; `hasLink` → `reg-tree-has-link`; подключей > предела или данных > 1 МиБ → `reg-tree-too-large`) → `Gate.CheckOrphanKeyDelete` → `ApprovedRegistryTreeDelete(source, destination, expectedSha)` → копия в `HKLM\SOFTWARE\Chistovik\Quarantine\<opId>\<n>`, хэш копии == expectedSha (иначе `backup-mismatch`, копия удаляется, шаг Failed), запись `.reg`, рекурсивное удаление (дети `OpenSub(...).DeleteSelf()` в глубину, последним корень), проверка, что ключа нет → Done.

Undo (`RegistryKeyRestoreStep`): оригинал существует → Conflict, разрешён только `Skip` (Overwrite → `bad-request`, иначе потеряем чужие данные); копия отсутствует → `backup-missing`; хэш копии ≠ `before.treeSha256` → `backup-mismatch`; иначе копирование обратно. Копия остаётся до Purge.

Purge: `AgentComposition` после `Journal.Purge(90d)` удаляет `Quarantine\<opId>` и `%ProgramData%\Chistovik\quarantine\<opId>\` для операций, которых больше нет в журнале. Если `IJournalWriter.Purge` не возвращает список, добавить `IReadOnlyList<Guid> ExistingOperationIds()` в читатель (не проверено).

### 1.3. C3b: откладываем

- Tweaks v2 (§6): в C3 только опциональные поля `TweakEntry` (`editions`, `revertedBy`, `scope`, `verified`, все `?`); новые записи каталога и allow-строки `PoliciesMicrosoftWriteAllow` с негативными тестами в C3b.
- Каталог служб/задач (§5): остаётся советами; управление через существующие `SetServiceStart`/`SetTaskEnabled`. Xbox «оставить».
- `IEmptyVolumeCache2` (C2b): критерий годности в Sandbox: есть `%windir%\System32\cleanmgr.exe` и ключи `...\Explorer\VolumeCaches\{Update Cleanup, Delivery Optimization Files}`, а `Initialize`+`GetSpaceUsed` завершаются ≤ 5 мин. Пока не доказано, только карточки без размера.
- `SoftwareDistribution\Download`, Expert-удаление ключей 51–500 подключей.

## 2. Протокол и журнал

### 2.1. Messages.cs

```csharp
public sealed record CleanCategoriesRequest(IReadOnlyList<string> Categories, bool MeasureOnly) : AgentRequest
// Validate: 1..32, каждый по Rules.CatalogId, без повторов (OrdinalIgnoreCase)
public sealed record ApplyFindingsRequest(IReadOnlyList<FindingAction> Items, bool DryRun, IReadOnlyList<string>? ExpertConfirmations) : AgentRequest
// Validate: Items 1..256, FindingId ≤ 300, без управляющих символов, без повторов; ExpertConfirmations ≤ 256, каждый ∈ Items
public sealed record FindingAction(string FindingId, FindingActionKind Action);
public enum FindingActionKind { Remove }
```

Оба добавить в `[JsonDerivedType]`. Разбор идентификатора в агенте (`OrphanIds.TryParse`); неизвестный класс → `PlanException(PolicyDenied)`. `Rules` пополняется `ArpUserId`, `AppPathId`, `RunOnceId`, `SharedDllId`.

`AgentResponse` + `IReadOnlyList<FactEntry>? Facts = null`, `FactEntry(string Key, string Json)`, Json ≤ 4096 символов, ≤ 256 записей: measure (`Key = cleanup:...`), DryRun (`Key = targetKey`, Json `{"v":1,"display":"...","subkeys":n,"revertible":true,"rule":null}`), итог очистки по шагам. `ResponseCodes` + `dry-run` (Ok=true, ничего не записано). `StepCodes` + `evidence-failed`, `reg-tree-too-large`, `reg-tree-has-link`, `backup-mismatch`, `backup-missing`, `interrupted`; `IsKnown` и `JournalModelTests` обновить.

Таймауты `AgentClientOptions`: `CleanupTimeout = 25 мин` для `CleanCategoriesRequest`, `FindingsTimeout = 5 мин` для `ApplyFindingsRequest` (по типу запроса, как для `UninstallRequest`).

### 2.2. Журнал

`StepKind` + `CleanupCategory`, `RecycleBinEmpty`, `RegistryKeyDelete`, `StartupRemove`, `SharedDllRemove`. `OperationKind` + `Cleanup`, `Registry`. Схема остаётся v2, новых столбцов нет (`revertible` уже есть, факты в `after_json`, копия в `target_json`). `[JsonSerializable]` для `CleanupTarget`, `CleanupState`, `RegKeyDeleteTarget`, `RegKeyState`, `ValueRemoveTarget`; все записи `"v":1`.

| Kind | target_key | target_json | before | desired/after |
|---|---|---|---|---|
| CleanupCategory | `cleanup:{machine|user}:{sid|-}:{id}` | `{"v":1,"category","scope","sid","roots":[...],"minAgeHours"}` | `{"v":1,"done":false,"bytes":N,"files":M}` | desired `{"v":1,"done":true}`; after `{"v":1,"done":true,"deleted":D,"freedBytes":B,"skipped":{"in-use":a,"too-fresh":b,"reparse":c,"access-denied":d},"failed":f}` |
| RecycleBinEmpty | `recyclebin:{sid}` | `{"v":1,"sid"}` | `{"v":1,"done":false,"bytes":N}` | `{"v":1,"done":true,"freedBytes":B}` |
| RegistryKeyDelete | `regkey:{hklm|hku}:{sid|-}:{x64|x86}:{key lower}` | `{"v":1,"root","sid","view","key","evidence":{"kind","paths":[...],"productCode"},"backup":{"key":"SOFTWARE\\Chistovik\\Quarantine\\<opId>\\<n>","file":"<opId>\\<n>.reg"},"expert":false,"display":"<DisplayName>"}` | `{"v":1,"exists":true,"subkeys":n,"values":m,"treeSha256":"..."}` | `{"v":1,"exists":false}` |
| StartupRemove / SharedDllRemove | `reg:{hklm|hku}:{sid}:{key}:{value}` | `{"v":1,...RegLocation..., "evidence":{...}}` | `RegValueState` | `{"v":1,"exists":false}` |

Компараторы (`StateComparers.All`, +5): `CleanupComparer.IsNoChange(cur, desired)` = `cur.files == 0`; `IsSame` = `a.done == b.done`; `RegKeyComparer` по `exists`; значения: существующий компаратор вида `reg:`.

`StepFactory.Rebuild`: `CleanupCategory`/`RecycleBinEmpty`: `isRevert` → `RefusedStep(NotRevertible)`, иначе `CleanupStep { RecoveryOnly = true }`. `RegistryKeyDelete`: разбор `RegKeyDeleteTarget`; `sid` для hku = `clientSid` (`journal-foreign-sid`); пересчитанный targetKey ≠ записанному → `journal-target-key-mismatch`; `isRevert` → `RegistryKeyRestoreStep(target, expectedSha из before)`; иначе `RegistryKeyDeleteStep { RecoveryOnly = true }`. `StartupRemove`/`SharedDllRemove`: `isRevert` → шаг восстановления значения (desired = before) через `CheckStartupRestore`/стандартный `CheckRegistryWrite`.

`RecoveryService`: pending `CleanupCategory`/`RecycleBinEmpty` → `Failed` с `interrupted`; `RegistryKeyDelete` стандартно (ключа нет и копия есть → `recovered-done`; ключ есть → `recovered-not-applied`, копию уберёт Purge). `UndoService`: правило «Done && !Revertible → NotRevertible» остаётся для `Cleanup`; операция `Registry` целиком обратима; для Conflict у `RegistryKeyDelete` принимается только `Skip`.

## 3. Политика и безопасность

**Доверие.** App недоверенный и только предлагает идентификаторы. Агент: сам раскрывает корни из каталога, профиль из `ProfileList` по проверенному SID; сам перечисляет; сам читает Evidence; Policy решает; удаление только по хэндлу после проверки финального пути/`ResolvedPath`. Путь, имя значения или ключ из запроса не достигает Win32.

**Корни C2** закрытый список; любой корень вне `AllowedBases` отвергается при загрузке каталога. Корень сам никогда; reparse не следуем и не удаляем; `KeepNames`; возраст; Temp другого SID недостижим; браузер запущен → категория целиком `Skipped(cleanup-browser-running)`.

**TOCTOU/reparse.** Между `Enumerate` и `OpenForDelete` подмена каталога junction'ом на `System32` закрывается проверкой `FinalPath` открытого хэндла под `rootFinal` (оба `VOLUME_NAME_GUID`, нормализованы): путь вне корня → `cleanup-not-under-root`, хэндл закрывается без удаления. Каталоги удаляются только пустыми и по хэндлу. Реестр: `OPEN_LINK` на каждом уровне, `ResolvedPath` корня через `NtQueryKey`, удаление только `NtDeleteKey(handle)`.

**Антивирус (§8 спецификации).** Нет `cmd /c del`, `reg.exe`, `cleanmgr /sagerun`; нет записи в `Run*` (только удаление с доказательством и восстановление собственной записи); нет Defender/UAC/SmartScreen; `.reg` в защищённый `%ProgramData%\Chistovik`; нет сети. В UI ни слова «найдено N ошибок»: «записей без файлов: n» с причиной. Текст о KB 2563254 («Microsoft не поддерживает чистильщики реестра») в описании области «Реестр». Проверка с включённым Defender в Sandbox (Info, не Fail).

**Обязательные негативные тесты** (`tests/Policy.Negative/CleanupNegativeTests.cs`, `RegistryCleanupNegativeTests.cs`; фейки `TestKit/FakeCleanupFiles.cs`, расширенный `FakeRegistry`):

1. Junction `Temp\J` → `Windows\System32`: не входим, не удаляем; `skipped.reparse == 1`.
2. Симлинк файла `Temp\L` → `explorer.exe`: пропуск `reparse`, сам линк не удаляем.
3. Подмена каталога после перечисления (fake: `OnOpen` возвращает хэндл с `FinalPath` вне корня) → `cleanup-not-under-root`, `Delete` не вызван.
4. Корень `Temp` сам reparse → Denied `cleanup-root-reparse`.
5. `ProfileImagePath` клиента: junction / владелец другой SID / под `C:\Windows` → `cleanup-profile-*`.
6. Файл моложе 24 ч не удалён; каталог с ним тоже.
7. Sharing violation → `in-use`, операция продолжается, итог Partial/Done.
8. Каталог с `Base="windir", Sub="System32"` или `Sub=".."` → `CleanupCatalog` падает при загрузке.
9. `Categories=["temp-user"]` от SID A при наличии профиля B: раскрыт только профиль A.
10. `recycle-bin` при SID токена агента ≠ SID клиента → Denied `recyclebin-foreign-user`.
11. `KeepNames`: `CBS.log` остаётся.
12. R1: `InstallLocation` существует → `evidence-failed`; `WindowsInstaller=1` и продукт установлен → `evidence-failed`; `SystemComponent=1` → Deny `arp-system-component`; `InstallLocation` под `WindowsApps` → Deny.
13. Ключ симлинк → `reg-symlink`; подключ симлинк → `reg-tree-has-link`, копия не создана, ничего не удалено.
14. `ResolvedPath` ≠ ожидаемому → `reg-resolved-mismatch`.
15. 51 подключ без подтверждения → `reg-tree-too-large`; 501 с подтверждением → тоже Deny.
16. `arp:machine:x64:Uninstall` (сам узел) → `reg-structure`; `apppath:` с `\` в имени → `bad-request`.
17. Копия не в `Chistovik\Quarantine\<текущий opId>\<n>` → `reg-quarantine-dest`.
18. Хэш копии ≠ оригинала (fake `DropWrites` на destination) → `backup-mismatch`, оригинал цел.
19. R3: команда разрешается в существующий файл → `evidence-failed`; HKU чужого SID → `reg-foreign-user`; HKCU → `reg-agent-no-hkcu`; `CheckSetValue` на `Run` по-прежнему `reg-run-keys` (регрессия).
20. R4: файл существует → `evidence-failed`; диск отсутствует → `drive-missing` (Unknown); удаление самого ключа `SharedDLLs` → `reg-structure`.
21. Undo `RegistryKeyDelete` при существующем оригинале → Conflict; `Overwrite` → `bad-request`; `Skip` → `Undone` без записи.
22. Undo операции `Cleanup` → `not-revertible`.
23. `ApplyFindings` с `DryRun=true` → в журнале ни одной операции.
24. `ExpertConfirmations` не из `Items` → `bad-request`.

## 4. App

**Сканеры** (`src/Chistovik.App/Services/Scanners/`): `IFindingScanner { FindingArea Area; Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken) }`; `InventoryFindingScanner` (обёртка нынешней логики `CleanupScanner`: Programs/Startup/Services/Tasks); `DiskCleanupScanner(CleanupCatalog, IDiskMeasure, IProcessList)` (Area.Disk, одна находка на категорию; user-категории меряет App без прав, machine «размер уточнит агент»); `RegistryOrphanScanner(IRegistryReader, IFileExists, IMsiState, CommandResolver)` (Area.Registry, R1–R4). `CleanupScanner` получает конструктор от `IEnumerable<IFindingScanner>`, старый сохраняется для `CleanupTests`. `MsiQueryProductStateW` читатель в `Services/Windows/SystemReaders.cs`.

**Модель находок** (`src/Chistovik.Core/Findings/`): `FindingArea` + `Disk`, `Registry`; `ReasonCodes` + `cache-regenerable`, `evidence-install-dir-missing`, `evidence-uninstaller-missing`, `evidence-msi-unknown`, `evidence-file-missing`, `drive-missing`, `key-too-large`, `browser-running`, `size-needs-agent`; `Reversibility`: Disk → `Irreversible`, Registry → `Undoable`. Тексты в `App/Models/ReasonTexts.cs`. Вердикты: Disk `Optional` (рекомендованные `DefaultOn` → `Remove`), Registry с подтверждённым Evidence `Remove` (Confidence L3), `Unknown` при `drive-missing`/неразобранной команде, `Protected` для системных записей.

**Экран «Чистка»** (`CleanupViewModel`, `FindingRowViewModel`, `CleanupView.xaml`): чипы «Диск» и «Реестр»; у строк `IsChecked`; `CanClean => Area==Disk && Verdict != Protected`, `CanRemoveKey => Area==Registry && Verdict==Remove`.

- `CleanDiskCommand`: выбранные категории → `Changes.MeasureCleanupAsync(ids)` → `ConfirmContent("Очистить: N категорий", "Будет удалено ~X МБ. Файлы моложе 24 часов, занятые и ссылки остаются. Откат невозможен: это кэш, он создаётся заново. …", "Очистить", Danger:true)` → `Changes.CleanAsync(ids)` → «Освобождено X МБ; пропущено: занято n, свежих m», ссылка на журнал.
- `ApplyRegistryCommand`: выбранные → `ApplyFindingsAsync(items, dryRun:true)` → диалог со списком (display, «подключей: n», «копия сохранится на 90 дней, откат из журнала»), Denied с причиной → подтверждение → `ApplyFindingsAsync(items, dryRun:false, null)` → итог, `undoable:true`.

`IChangeService`/`ChangeService` +: `MeasureCleanupAsync(ids)`, `CleanAsync(ids)`, `ApplyFindingsAsync(items, dryRun, expert)`. `ChangeOutcome` + `Facts`. `ResponseText.KindName` («Очистка диска», «Реестр»), `StepKindName` (5 новых), `StepCodeName` (новые коды); `SubjectText.OfTarget`: `cleanup:` → название категории из `CleanupCatalog`, `regkey:` → `display` из target_json (через `programName`, как Uninstall), `recyclebin:` → «Корзина».

`MainViewModel`: пункт-заглушку «Очистка диска» (строка 96) убрать (функция живёт в «Чистке»); проверить `SnapshotPlanTests`/`WiringTests` на `AllNavItems`. `MockSources`: демо-находки Disk/Registry. `AutomationName` строк: «{Title}, {Area}, {Verdict}, {Size}». ДО выполнения показываются: объём, список, обратимость («нет» для диска, «да, 90 дней» для реестра), порог по возрасту, причина пропусков, предупреждение про перезапуск Explorer для `thumbcache`.

## 5. Sandbox

Сценарии регистрируются в `tests/Sandbox.Live/Program.cs` рядом с `("S10", S10Ui.Run)`.

### S11: диск (`tests/Sandbox.Live/S11Disk.cs`)

Подготовка: в `C:\Windows\Temp`: `old.tmp` (1 МиБ, Created/LastWrite −3 дня), `fresh.tmp`, `locked.tmp` (держим `FileShare.None`), `olddir\a.log` (старый), `emptyold\`, junction `J-System32 → C:\Windows\System32` (`mklink /J`), symlink-файл `L-notepad → C:\Windows\notepad.exe`, symlink-каталог `LD-Sys → C:\Windows\System32`; канарейка `C:\Windows\System32\chistovik-canary.txt`; в `%LOCALAPPDATA%\Temp`: `old.tmp`, `fresh.tmp`; `C:\ProgramData\Microsoft\Windows\WER\ReportQueue\Report.test\Report.wer` (старый); в Корзину файл через `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., SendToRecycleBin)`.

Шаги и PASS:
1. `CleanCategories(MeasureOnly)` → `Facts["cleanup:machine:-:temp-system"].bytes ≥ 1 МиБ`, `files` не считает `fresh.tmp`.
2. `CleanCategories(["temp-system","temp-user","wer-system","recycle-bin"])` → Ok, Done или Partial; удалены `old.tmp`, `olddir`, `emptyold`, `Report.test`, user `old.tmp`; существуют `fresh.tmp`×2, `locked.tmp`, `J-System32`, `L-notepad`, `LD-Sys`, `C:\Windows\Temp`, `%LOCALAPPDATA%\Temp`, канарейка, `notepad.exe`; число файлов в `System32` до/после равно; `skipped.reparse ≥ 3`, `in-use ≥ 1`; Корзина пуста; лог `C:\ProgramData\Chistovik\cleanup\<opId>.log` содержит `D …\old.tmp` и не содержит `System32`.
3. `Undo(opId)` → `not-revertible`.
4. Повтор шага 2 → Done с `deleted=0`.
5. Ловушка корня: `%LOCALAPPDATA%\Temp` переименовать в `Temp.bak`, создать junction `Temp → C:\Windows\System32`; `CleanCategories(["temp-user"])` → Denied `cleanup-root-reparse`, канарейка жива; вернуть обратно.
6. Браузер: запустить `msedge.exe`, `CleanCategories(["browser-edge"])` → Skipped `cleanup-browser-running`; закрыть, повторить → Done.
7. `Get-MpThreatDetection` после сценария пуст (Info).

### S12: реестр (`tests/Sandbox.Live/S12Registry.cs`)

Подготовка под `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\`: `ChistovikOrphan` (DisplayName, `InstallLocation=C:\Program Files\ChistovikOrphan` — не существует, `UninstallString="C:\Program Files\ChistovikOrphan\unins.exe" /S`, 3 подключа, значения REG_SZ/DWORD/BINARY/MULTI_SZ/EXPAND_SZ); `ChistovikLive` (`InstallLocation` на реальный каталог); `ChistovikLink` — ключ-симлинк на `ChistovikLive` (`RegCreateKeyEx(REG_OPTION_CREATE_LINK)` + `SymbolicLinkValue`); `ChistovikBig` — сирота с 60 подключами; `ChistovikOrphan2\Link` — сирота с подключом-симлинком на `ChistovikLive`; `ChistovikSys` — сирота с `SystemComponent=1`. `App Paths\chistoviktest.exe` → `C:\nope\t.exe`. `Run\ChistovikRun = "C:\nope\run.exe" /x` в HKLM и `HKU\<sid>`; `RunOnce\ChistovikOnce`. `SharedDLLs`: `C:\nope\shared.dll=1`, `Z:\x.dll=1`. Канарейка: число подключей `ChistovikLive` и значение `Run\SecurityHealth`.

Шаги и PASS:
1. `ApplyFindings(DryRun)` со всеми id → `Code=dry-run`, журнал без операций; Orphan: `revertible:true, subkeys:3`; Link → Denied `reg-symlink`; Big → `reg-tree-too-large`; Orphan2 → `reg-tree-has-link`; Live → `evidence-failed`; Sys → `arp-system-component`; `Z:\x.dll` → `drive-missing`.
2. `ApplyFindings` (Orphan, App Paths, Run×2, RunOnce, `C:\nope\shared.dll`) → Ok, Done; ключи/значения отсутствуют; `Quarantine\<opId>\1` существует и хэш равен `before.treeSha256`; `C:\ProgramData\Chistovik\quarantine\<opId>\1.reg` начинается с `Windows Registry Editor Version 5.00` и содержит `[HKEY_LOCAL_MACHINE\SOFTWARE\...\ChistovikOrphan]`; `reg.exe import` восстанавливает ключ (потом снова `reg delete`); `ChistovikLive` цел, `SecurityHealth` цел, `StartupApproved` не изменился.
3. `Undo(opId)` → Ok; `ChistovikOrphan` восстановлен, хэш равен исходному; `Run\ChistovikRun` (HKLM и HKU) восстановлены с тем же типом и данными; `SharedDLLs` восстановлено; повтор `Undo` → `already-reverted`.
4. Повтор шага 2, затем вручную создать `ChistovikOrphan` с другим содержимым → `Undo` → Conflict; `Resolutions=[Skip]` → Undone, ручной ключ цел.
5. Регрессия C1: `Uninstall(arp:machine:x64:ChistovikOrphan)` → `not-found`/Denied, поведение прежнее.

### S10: расширение UI

После существующих проверок: в «Чистке» видны чипы «Диск» и «Реестр»; строка «Временные файлы пользователя» → флажок → кнопка «Очистить выбранные категории» → диалог `FindDialogOk(app, "Очистить: ")` → итог содержит «Освобождено»; строка «ChistovikOrphan» → «Удалить выбранные записи реестра» → диалог «Удалить из реестра: » с «копия» → OK → итог; «Журнал и откат» содержит операцию «Реестр» → «Откатить» → после «Сканировать систему заново» строка снова в списке. Защищённые строки (`ChistovikSys`, `Live`) без флажка.

## 6. План работ

Общие файлы, которые нельзя править параллельно: `Ports/Enums.cs`, `Ipc/Messages.cs`, `Journal/JournalModel.cs` (`StepCodes`, `JournalJson`), `Execution/StateComparers.cs`, `Execution/StepFactory.cs`, `Execution/ExecEnv.cs`, `Agent/AgentComposition.cs`, `TestKit/TestEnv.cs`, `App/ViewModels/MainViewModel.cs`, `App/Services/Interfaces.cs`. Их меняет один исполнитель в T0/T7/T10.

| № | Задача | Файлы | Зависит | Готово, когда | Ревью |
|---|---|---|---|---|---|
| T0 | Общая поверхность: enums, 2 запроса + `FactEntry`/`Facts`, `StepCodes`, `ResponseCodes.DryRun`, `OperationPlan.ContinueOnFailure`, пустые записи `JournalJson`, заглушки в `StateComparers`/`StepFactory` (`journal-unknown-kind` до T7), новые порты в `ExecEnv` (`required`), `TestEnv`/фейки-пустышки | общие файлы, `Ports/Cleanup.cs`, `Ports/Registry.cs` | нет | тесты зелёные; `ProtocolTests` покрывают Validate обоих запросов (границы 0/1/32/33, 0/1/256/257, повторы, управляющие символы) | Fable/Opus |
| T1 | `cleanup.json` + `CleanupCatalog` + `CatalogSha256` | `Catalog/data/cleanup.json`, `Core/Catalog/CleanupCatalog.cs`, `tests/Core.Tests/Catalog/CleanupCatalogTests.cs` | T0 | тест: Id уникальны, базы ∈ AllowedBases, machine/user согласованы, `handler`-категории без корней | |
| T2 | `CleanupPolicy` + негативные тесты 1–11 | `Core/Policy/CleanupPolicy.cs`, `tests/Policy.Negative/CleanupNegativeTests.cs` | T0 | 11 тестов зелёные, регрессия `PathPolicy.appdata-temp` | другая модель обязательно |
| T3 | Фейки: `FakeCleanupFiles` (дерево в памяти, reparse, sharing violation, подмена FinalPath), `FakeRecycleBin`, `FakeProcessList`, `FakeRegistry` tree-операции (`OpenTree`, `AddSymlink`, `DropWrites`) | `tests/TestKit/*` | T0 | используются в T5/T6 | |
| T4 | Native: `WindowsCleanupFiles`, `WindowsRecycleBin`, `WindowsProcessList`, `WindowsRegistryTree` + P/Invoke; `IUserFolders.ProfileDir`; порты в `AgentPorts.Windows()` | `Native/Cleanup/*`, `Native/Registry/WindowsRegistryTree.cs`, `Native/Interop/*`, `Native/Env/WindowsEnvironment.cs`, `Agent/AgentComposition.cs` | T0 | `Native.Tests` только чтение: перечисление `%TEMP%` без удаления; `OpenTree` на `HKLM\SOFTWARE\Classes` читает `IsSymbolicLink`; ничего разрушительного на хосте | другая модель |
| T5 | Исполнение C2: `CleanupSteps.cs`, `StepRunner.ContinueOnFailure`, `OperationHandler.Measure`, компараторы, Recovery `interrupted`, лог | `Core/Execution/CleanupSteps.cs`, `StepRunner.cs`, `OperationHandler.cs`, `StateJson.cs`, `StateComparers.cs`, `RecoveryService.cs`, `tests/Core.Tests/Execution/CleanupTests.cs` | T0–T3 | measure не пишет журнал; Partial при in-use; Undo → not-revertible; Recovery pending → interrupted; идемпотентность | другая модель обязательно |
| T6 | Исполнение C3: `OrphanIds`, `OrphanProbe`, `RegistryTreeHasher`, `RegExport`, 4 шага, `FindingsPlanner`, `RegistryPolicy` новые методы + `SharedDLLs` в `SoftwareDenyEqual`, `StepGate.Check*`, `ApprovedRegistryTreeDelete` | `Core/Execution/RegistryTreeSteps.cs`, `OrphanEvidence.cs`, `Core/Policy/RegistryPolicy.cs`, `OrphanIds.cs`, `StepGate.cs`, `tests/Policy.Negative/RegistryCleanupNegativeTests.cs`, `tests/Core.Tests/Execution/RegistryCleanupTests.cs` | T0, T3 | негативные 12–24; регрессия существующих `RegistryPolicy` тестов; `.reg` для всех `RegType` round-trip через парсер в тесте | другая модель обязательно; `Policy.Negative` блокирует коммит |
| T7 | `StepFactory.Rebuild` для 5 видов, `UndoService` (Skip-only для RegistryKeyDelete), `RecoveryService`, `StateDisplay.Of`, Purge копий | `StepFactory.cs`, `UndoService.cs`, `RecoveryService.cs`, `Responses.cs`, `AgentComposition.cs`, `tests/Core.Tests/Execution/{UndoTests,RecoveryTests,JournalContractTests}.cs` | T5, T6 | тесты Undo/Recovery/Rebuild для всех новых видов; `JournalContractTests` знает новые коды | другая модель обязательно |
| T8 | App-сканеры и модель находок | `Core/Findings/{FindingModels,ReasonCodes}.cs`, `App/Services/Scanners/*`, `SystemReaders.cs` (Msi), `App/Models/ReasonTexts.cs`, `App/Mocks/MockSources.cs`, `tests/App.Tests/ScannersTests.cs` | T0, T1 | на фейковом `IRegistryReader` R1–R4 верно классифицируют 8 образцов; Disk меряет временный каталог теста | |
| T9 | App связь: `IChangeService` 3 метода, `ChangeService`, `AgentClientOptions`, `ResponseText`, `SubjectText`, `ChangeOutcome.Facts` | `App/Services/Interfaces.cs`, `Changes/*`, `Agent/AgentClient.cs`, `tests/App.Tests/{ChangeServiceTests,AgentClientTests}.cs` | T0 | тесты с фейковым `IAgentClient` | |
| T10 | Экран «Чистка»: VM, строки, XAML, `MainViewModel` (убрать заглушку «Очистка диска»), AutomationName | `App/ViewModels/{CleanupViewModel,MainViewModel}.cs`, `Views/CleanupView.xaml`, `tests/App.Tests/{CleanupTests,ViewModelTests,SnapshotPlanTests,AutomationNameTests}.cs` | T8, T9 | диалоги показывают объём/обратимость; DryRun до подтверждения; Protected без флажка | skill `code-review` |
| T11 | Sandbox S11, S12, S10-ext; запуск по образцу S9/S10 | `tests/Sandbox.Live/{S11Disk,S12Registry,S10Ui,Program}.cs`, `tools/sandbox/*` | T4–T10 | прогон в Sandbox: все `Report.Check` PASS | владелец смотрит отчёт |
| T12 | Документы: `AGENTS.md` (формулировка правила удаления файлов), `CLEANUP-SPEC.md` (закрытые вопросы) | docs | T11 | | |

Параллельно: {T1, T2, T3} после T0; {T4, T5, T6, T8, T9} после T3 (`StateJson.cs` отдать T5, T6 использует свой `RegTreeJson.cs`). Последовательно: T0 → … → T7 → T10 → T11.

## 7. Риски и непроверенное

- Sandbox не равен живому ПК: `thumbcache`/`iconcache` почти всегда заняты Explorer; профили браузеров на реальной машине другие (`Profile N`); перенаправленные профили (`D:\Users`) в Sandbox не воспроизводятся.
- `SHEmptyRecycleBin` под повышенным токеном: случай «стандартный пользователь + чужие админ-учётные данные» (SID ≠ SID) проверяется только юнит-тестом отказа.
- `FileDispositionInfoEx` с POSIX_SEMANTICS требует Windows 10 1709+ и NTFS; на ReFS/FAT fallback на `FileDispositionInfo`, не проверено на не-NTFS.
- Производительность: Temp с сотнями тысяч файлов (оценка 5–10 тыс. файлов/с), предел 20 мин на категорию.
- `.reg`-формат: `REG_MULTI_SZ`/`REG_BINARY`/`REG_QWORD` как `hex(7):`, `hex:`, `hex(b):` с переносами по 80 символов; round-trip проверяется в S12 через `reg.exe import`; `REG_NONE` и редкие типы не проверены.
- Антивирус: подписанной сборки нет; эвристика PUA на «удаление ключей Uninstall + запись .reg» непредсказуема; проверка только Defender в Sandbox.
- `ExpertConfirmations` противоречит DESIGN-0.1 §3 («флага эксперта нет»). Здесь это список идентификаторов, не флаг, и агент его не верифицирует: UX-барьер, не граница безопасности; Deny остаётся Deny.
- Не проверено: возвращает ли `IJournalWriter.Purge` список; форма `ReasonTexts`-теста полноты; `SnapshotPlanTests` на `AllNavItems`; `JournalModelTests` закрытые списки. Реализаторы T0/T8/T10 прогоняют и чинят ожидания.
- Firefox `Profiles\*` единственная подстановка; ограничить одним уровнем и не-reparse каталогами, иначе категорию убрать.
- Работает ли `cleanmgr` в Sandbox, неизвестно (критерий C2b).

## Вопросы владельцу (по умолчанию принято первое)

1. `ExpertConfirmations` в `ApplyFindings`: принять, либо держать >50 подключей всегда под запретом и вернуться в C3b.
2. `recycle-bin` по умолчанию выключена.
3. Браузеры: Edge, Chrome, Firefox достаточно, или добавить Yandex/Opera/Brave (только строки каталога).
