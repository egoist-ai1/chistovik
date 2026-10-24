# Спецификация релиза 0.1: журнал, откат, операции агента

Составил архитектор (Opus), 24.10.2026. Обязательна для воркеров. Отклонение от неё — только через руководителя сессии. Живых проверок нет: всё за портами и проверяется на подставных данных.

## 0. Решение

Весь релиз 0.1 идёт через агента. Агент — единственный писатель журнала и единственный, кто меняет систему, включая пользовательский реестр. Путь каждого изменения: запрос с id из каталога или инвентаря → проверка Policy по свежему состоянию в агенте → запись `pending` → действие через узкий порт → `done`. Откат — отдельная операция `Undo`; исходный шаг при этом становится `undone`.

- Одна база, один писатель, один механизм отката. Режим откатывается целиком внутри одной базы.
- Цена: даже твик HKCU требует одного UAC за сеанс. UAC появляется при первом изменении, просмотр работает без прав.
- В запросах нет ключей, путей и команд, только идентификаторы. Агент строит цель из каталога или из своей инвентаризации.
- Запись в систему возможна только через типы `Approved*` с `internal` конструктором в Core. Их создаёт только `StepGate` после решения Policy. Native физически не может записать в обход политики.
- HKCU агент никогда не открывает. Пишет в `HKU\<SID проверенного клиента>`.
- `requiresAdmin` в каталоге = «машинная правка, показать окно подтверждения», а не «нужен агент».

## 1. Журнал (SQLite, Microsoft.Data.Sqlite)

Файл `%ProgramData%\Chistovik\chistovik.db`. Пишет только агент. App открывает `Mode=ReadOnly`, `PRAGMA query_only=1`.

PRAGMA агента: `journal_mode=WAL`; `synchronous=FULL` (обязательно); `foreign_keys=ON`; `busy_timeout=5000`; `trusted_schema=OFF`; `auto_vacuum=INCREMENTAL` (при создании). Версия схемы: `PRAGMA user_version=1` и таблица `meta(key TEXT PK, value TEXT)`. Миграции вперёд, по одной, в транзакции. `user_version` больше известной → код `journal-newer-schema`, только чтение.

**operation:** `id TEXT PK` (Guid "D") · `request_id TEXT UNIQUE NULL` · `kind TEXT` (`Tweaks|Mode|Startup|Service|Task|PowerScheme|PowerSetting|Undo`) · `subject TEXT` · `reverts_op_id TEXT NULL REFERENCES operation(id) ON DELETE SET NULL` · `user_sid` · `agent_version` · `catalog_sha256` · `created_utc INTEGER` (unix ms) · `finished_utc NULL` · `state TEXT` · `code TEXT NULL` · `error TEXT NULL` (≤1 КБ).
Состояния: `running → done | partial | failed | denied | interrupted`; для отката `undone`, `partially_undone`.

**step:** `id INTEGER PK AUTOINCREMENT` · `op_id TEXT NOT NULL REFERENCES operation ON DELETE CASCADE` · `ord INTEGER`, `UNIQUE(op_id, ord)` · `kind` · `target_key` (каноническая строка цели) · `target_json` · `before_json NULL` · `after_json NOT NULL` · `state` (`pending|done|failed|skipped|denied|undone`) · `code NULL` · `policy_rule NULL` · `reverts_step_id NULL REFERENCES step(id) ON DELETE SET NULL` · `restart` · `created_utc` · `updated_utc`.
Индексы: `step(target_key, id DESC)`, `step(state) WHERE state='pending'`, `operation(created_utc)`.

Коды шага (закрытый список): `no-change`, `external-change`, `policy-denied`, `not-found`, `access-denied`, `verify-mismatch`, `recovered-done`, `recovered-not-applied`, `already-reverted`, `win32:<n>`.

before/after: каждый JSON начинается с `"v":1`; сравнение — отдельный `IStateComparer` на вид шага.

| kind | target_json | before/after | Сравнивается |
|---|---|---|---|
| `RegistryValue` | `{root:"HKLM"\|"HKU", sid?, view:"64", key, name}` | `{exists, type, data}`; QWord строкой, Binary base64 | exists + type + data (строки ordinal, хвостовые NUL обрезать) |
| `StartupApproved` | `{root, sid?, approved:"Run\|Run32\|StartupFolder", name, commandSha256}` | `{exists, bytes:base64(12)}` | конфликт: точные байты; no-change: класс вкл/выкл |
| `ServiceStart` | `{name}` | `{start, triggerStart, running}` | только `start` |
| `TaskEnabled` | `{path}` | `{exists, enabled, xmlSha256, xml?}` (xml ≤64 КБ) | exists + enabled |
| `PowerActiveScheme` | `{}` | `{scheme:guid}` | scheme |
| `PowerSetting` | `{scheme, subgroup, setting}` | `{ac:uint, dc:uint}` | ac + dc |
| `PowerSchemeCreate` | `{template, scheme}` (GUID создаёт агент ДО выполнения) | `{exists}` | exists |

Режим — `operation(kind=Mode)`: шаги по порядку `PowerSchemeCreate` (если нужен) → `PowerActiveScheme` → `RegistryValue` в порядке каталога. Шаги, где текущее значение уже равно целевому, сохраняются как `skipped/no-change`, откат их не трогает.

Срок 90 дней: при старте агента после восстановления удалить операции с `finished_utc < now-90d` и `state != running`, затем `incremental_vacuum`. Время только из `IClock`.

## 2. Порты (Core, `Chistovik.Core.Ports`)

Порты Native возвращают сырые факты; классификация и сравнения в Core.

```csharp
interface IClock { DateTimeOffset UtcNow { get; } }
interface IIdGenerator { Guid NewGuid(); }

record RegLocation(RegRoot Root, string? UserSid, string KeyPath, string ValueName); // RegRoot: Machine|User, всегда Registry64
record RegValueState(bool Exists, RegType Type, object? Data);
interface IRegistry { IRegKeyHandle? Open(RegLocation loc, bool write, bool createKey); }
interface IRegKeyHandle : IDisposable {
  string ResolvedPath { get; }   // NtQueryKey(KeyNameInformation)
  bool IsSymbolicLink { get; }
  RegValueState Read(string name);
  void Write(ApprovedRegistryWrite w);  // exists=false → удаление значения
  IReadOnlyList<string> ValueNames(); }

record ServiceRaw(string Name, uint TypeFlags, uint StartType, bool Delayed, string? ImagePath,
                  bool LaunchProtected, int DependentCount, int TriggerCount, bool Running);
interface IServiceControl { IReadOnlyList<string> ListNames(); IServiceHandle? Open(string name, bool write); }
interface IServiceHandle : IDisposable { ServiceRaw Query(); void SetStart(ApprovedServiceChange c); }

record TaskAction(string Kind /*Exec|ComHandler|Other*/, string? Path, string? Args);
record TaskRaw(string Path, bool Enabled, string Xml, string? UserId, string RunLevel, string LogonType, IReadOnlyList<TaskAction> Actions);
interface ITaskScheduler { IReadOnlyList<string> ListPaths(); ITaskHandle? Open(string path); }
interface ITaskHandle : IDisposable { TaskRaw Query(); void SetEnabled(ApprovedTaskChange c); }

interface IPowerControl { Guid GetActive(); IReadOnlyList<Guid> ListSchemes();
  (uint Ac, uint Dc) Read(Guid scheme, Guid sub, Guid setting);
  void Write(ApprovedPowerSetting c); void SetActive(ApprovedPowerScheme c);
  void Duplicate(ApprovedSchemeCreate c); void Delete(ApprovedSchemeDelete c); }

interface IFileTrust { SignatureFacts GetSignature(string path); }   // Valid, MicrosoftSigned
interface IUserFolders { string? StartupFolder(string userSid); string? CommonStartupFolder(); }
interface IFileSystem { IReadOnlyList<string> ListFiles(string dir, string pattern); }
interface IJournalDirectory { DirFacts Inspect(string path); void CreateProtected(string path); } // Exists, IsReparse, OwnerSid, Aces[]

interface IJournalReader { IReadOnlyList<OperationSummary> List(int skip, int take); OperationView? Get(Guid id);
  StepRecord? LatestEffective(string targetKey); int SchemaVersion { get; } }
interface IJournalWriter : IJournalReader {
  OperationRecord BeginOrGet(NewOperation op);               // дубликат request_id → существующая
  long AddPending(Guid op, int ord, StepKind k, string targetKey, string target, string before, string after);
  void AddTerminal(Guid op, int ord, StepKind k, string targetKey, string target, string? before, string after, StepState s, string code, string? rule);
  void Complete(long stepId, StepState s, string? code, string? error);
  void CompleteRevert(long undoStepId, long originalStepId, string code);  // одна транзакция: undo→done, исходный→undone
  void Finish(Guid op, OperationState s, string? code);
  IReadOnlyList<PendingStep> GetPending(); int Purge(DateTimeOffset cutoff); }
```

Policy живёт в `Chistovik.Core.Execution.StepGate` — единственное место, где вызывается Policy и создаются `Approved*` (internal конструкторы, `InternalsVisibleTo` только тесты). `StepGate` проверяет: (1) цель выводится из разрешающего источника — запись каталога, перечисление автозагрузки, `power.json`; (2) Policy = `Allow` (`ExpertOnly` в 0.1 считается `Deny`, поля «эксперт» в запросах нет); (3) для реестра `ResolvedPath` равен ожидаемому и `!IsSymbolicLink`. Планировщики (`TweakPlanner`, `ModePlanner`, `StartupPlanner`, `ServicePlanner`, `TaskPlanner`, `PowerPlanner`) строят `PlannedStep[]`, общий `StepRunner` выполняет.

Новая Policy: `TaskPolicy`, `PowerPolicy`, `StartupPolicy`, `JournalAclPolicy` (`DirFacts → trusted?`), `RegistryPolicy.CheckDeleteValue` (оболочка над `CheckSetValue`), правило HKU: разрешён только проверенный SID клиента.

## 3. Протокол 0.1 (расширение Messages.cs)

| Тип | Поля | Ограничения |
|---|---|---|
| `Ping` | — | есть; App шлёт раз в 60 с (heartbeat) |
| `ApplyTweaks` | `Changes:[{TweakId, Enabled}]` | 1–32, id без повторов, из каталога |
| `ApplyMode` | `ModeId` | из modes.json |
| `SetStartupEntry` | `EntryId, Enabled` | `run:{hklm64\|hklm32\|hku}:{name}` или `folder:{user\|common}:{file}`, ≤300 симв.; агент находит в СВОЁМ перечислении |
| `SetServiceStart` | `ServiceName, Start:{Automatic\|AutomaticDelayed\|Manual\|Disabled}` | regex из ServicePolicy |
| `SetTaskEnabled` | `TaskPath, Enabled` | `^\\[^\\/:*?"<>\|]{1,255}(\\[^...]{1,255})*$`, без `..` |
| `SetPowerScheme` | `Plan:{saver\|bal\|high\|ult}` | GUID берёт агент; `ult` → `Duplicate`, если нашей схемы ещё нет |
| `SetPowerSetting` | `SettingId` (min,max,boost,display,sleep,aspm,usb из `power.json`), `Ac?:uint, Dc?:uint` | диапазон из каталога; цель — активная схема на момент выполнения |
| `Undo` | `OperationId, Resolutions?:[{StepId, Action:Overwrite\|Skip}]` | StepId принадлежит операции; Undo для Undo запрещён |

Запрещено передавать: ключи, имена значений, пути к файлам, GUID схем и подгрупп, командные строки, XML, свободный текст, флаг эксперта, SID. В `AgentJson.Options` обязательно `JsonStringEnumConverter(allowIntegerValues:false)`.

Ответ: `AgentResponse(Id, Ok, Code, Message?, OperationId?, Steps?:[{StepId,Ord,Kind,State,Code}], Conflicts?:[{StepId,Kind,CurrentDisplay,ChangedByOperationId?}])`. Коды: `ok | no-change | partial | policy-denied | not-found | conflict | busy | journal-unavailable | journal-untrusted | journal-newer-schema | bad-request | unknown-operation | internal`. Повтор запроса с тем же `Id` возвращает сохранённый результат, не выполняя заново.

## 4. Алгоритмы

**Применение.** (1) Валидация и план. (2) Предпроверка всех шагов через `StepGate` (чтение). Любой отказ → `operation(denied)`, шаги `denied`, ничего не выполняется. (3) Для каждого шага: открыть хэндл и ПОВТОРИТЬ проверку по `ResolvedPath`/свежему `Query()` того же хэндла; `cur = Read`; `cur ≈ after` → `AddTerminal(skipped, no-change)`; `AddPending(before=cur)` (коммит при synchronous=FULL); `Write` через тот же хэндл; `v = Read`: `v ≈ after` → `done`; `v ≈ before` → `failed(win32|access-denied)`; иначе `failed(verify-mismatch)`; на первом `failed` стоп. (4) `Finish`: все `done`/`skipped` → `done`; есть `done` и `failed` → `partial`; иначе `failed`. Выполненные шаги автоматически НЕ откатываются.

**Восстановление** при старте агента до открытия канала. Для каждого `pending`: `cur ≈ after` → `done(recovered-done)`; `cur ≈ before` → `skipped(recovered-not-applied)`; иначе `failed(external-change)`. Операции автоматически не продолжаются. Операция `running` закрывается как `interrupted`. Шаг отмены, восстановленный как `done`, переводит исходный в `undone` через `CompleteRevert`. `PowerSchemeCreate` проверяется по заранее созданному GUID.

**Откат.** Кандидаты — шаги `done` в обратном `ord`. Предпроход только чтение. `target_json` из базы НЕ доверенный: цель снова проверяется через `StepGate` по тем же источникам, что при применении. Для каждого шага: `cur ≈ before` → `already-reverted`; `cur ≈ after` → можно вернуть; иначе конфликт. Конфликты без `Resolutions` → ответ `conflict` со списком, ничего не пишется (`ChangedByOperationId` из `LatestEffective(target_key)`). Повтор с решениями создаёт `operation(kind=Undo, reverts_op_id)`: для каждого шага `pending` (before=текущее, after=исходное before), запись, проверка, `CompleteRevert`. Если состояние изменилось после предпрохода — стоп с `partial`. Откат режима = откат его операции целиком; `PowerSchemeCreate` в откате удаляет только нашу схему и только если она не активна (обратный порядок это гарантирует).

**Идемпотентность:** `request_id UNIQUE`; записи абсолютные; сравнение перед записью; операции строго последовательно (`SemaphoreSlim(1)`).

## 5. Граница App и Agent

App без прав: инвентарь; список служб (имена, конфигурация; класс — тот же Core-классификатор как подсказка); видимые задачи; чтение питания; чтение журнала; эффекты после ответа агента (`SystemParametersInfo` с `SPIF_SENDCHANGE` БЕЗ `SPIF_UPDATEINIFILE`, `WM_SETTINGCHANGE`, перезапуск Проводника по кнопке); запуск агента (`runas`) при первом изменении, дальше heartbeat. Agent: все записи; только в `HKU\<SID клиента>`. Журнал пишет только агент. Операций без агента в 0.1 нет.

## 6. Рабочие пакеты

| Пакет | Папки | Готово, когда |
|---|---|---|
| W0 контракты | `Core/Ipc/Messages.cs`, `Core/Ports/*`, `Core/Journal/JournalModel.cs`, `Core/Execution/Approved*.cs`, сигнатура `StepGate`, `Catalog/data/power.json`, поле `effect` в tweaks.json (`none\|spi-animation\|spi-dragfull\|spi-mouse\|broadcast`), `ProjectReference` App→Core | сборка зелёная; круговая сериализация payload; негативные тесты протокола |
| W1 Журнал | `Core/Journal/Sqlite*.cs`, `tests/Core.Tests/Journal/` | схема v1, PRAGMA, `CompleteRevert` транзакцией, дедупликация request_id, очистка через FakeClock, отказ при новой схеме, база только на чтение во временном каталоге |
| W2 Исполнение | `Core/Execution/**`, `tests/Fixtures/**` (FakeRegistry с симлинками, FakeScm, FakeTaskScheduler, FakePower, FakeClock, InMemoryJournal), `tests/Core.Tests/Execution/` | все алгоритмы §4 на подставных портах, включая «обрыв» между `AddPending` и `Complete` для каждого вида шага |
| W3 Policy | `Core/Policy/{Task,Power,Startup,JournalAcl}Policy.cs`, `CheckDeleteValue`, правило HKU, `tests/Policy.Negative/*New*.cs` | негативные тесты §7 зелёные |
| W4 Native и Agent | `Native/{Registry,Services,Tasks,Power,Trust,Journal}/`, `Agent/Program.cs`, `tests/Native.Tests/` (чтение живой системы допустимо, писателей нет) | тонкие реализации без логики; агент собирает граф, выполняет восстановление и очистку до канала |
| W5 App | `src/Chistovik.App/**` | `AgentClient` (runas, канал, heartbeat), `JournalReader`, экраны Твики/Режимы/Автозагрузка/Службы/Задачи/Питание, диалог конфликтов, эффекты; Mocks заменены |
| W6 интеграция | `tests/` | сквозной тест через настоящий канал с подставными портами и SQLite во временном каталоге |

Ревью дифа: модель, не писавшая W2 и W3.

## 7. Негативные тесты (`tests/Policy.Negative`)

Протокол: неизвестный `type`; лишнее поле `key`/`path`/`command`; `Start=0`/`99` числом; строка `Start:"Boot"`; `Changes` из 33 или с повтором; `TweakId`/`ModeId` не из каталога; `ServiceName` с `\`, NUL, хвостовым пробелом, >257; `TaskPath` с `..`, `/`, UNC, без ведущего `\`; `SettingId` вне каталога; `Ac=101` для proc-min; повтор `request_id` выполняется один раз; `Resolutions` с StepId чужой операции; Undo для Undo.

Реестр: `ResolvedPath` в `HKLM\...\Policies\System` при симлинке из HKU — отказ; `HKU\S-1-5-18` и чужой SID; `EnableLUA` в любом ключе; подменённая строка журнала (`target_json` на запретный ключ) — Undo отклоняется; цель отката, которую не строит ни один планировщик 0.1, — отказ.

Службы: WinDefend, wscsvc, `CDPUserSvc_1a2b3`, служба в общем svchost, служба с зависимыми; `ImagePath` меняется на `C:\Windows\...` между предпроходом и выполнением — отказ на повторном `Query`; в `before` при откате Boot/System; ПОВЫШЕНИЕ запуска (Disabled→Manual/Auto, Manual→Auto) напрямую разрешено только если файл службы в Program Files / Program Files (x86), иначе только откатом собственного шага.

Задачи: поддеревья `\Microsoft\*` и `\Chistovik\*`; действие в каталоге Windows; `ComHandler`; включение задачи от SYSTEM или `HighestAvailable` — только откатом.

Автозагрузка: запись с файлом в `%WINDIR%` (SecurityHealthSystray) — отказ; `EntryId` нет в перечислении — `not-found`.

Питание: удалить встроенную/чужую/активную схему — отказ.

Журнал: `JournalAclPolicy` отклоняет каталог-reparse; владельца не из Administrators/SYSTEM; ACE с записью для Users/Authenticated Users/Everyone/INTERACTIVE → `journal-untrusted` (права сам не исправляет).

Восстановление: `pending` с текущим не равным before/after → `failed(external-change)` без записи; `pending` не продолжается автоматически.

Прочее: ключи `Policies` (telemetry, bing) может переписать GPO — при откате конфликт; AllowTelemetry=0 на Home/Pro работает как 1 — написать в описании. «Выключить» ≠ «откатить»: OffValue фиксированный, откат возвращает `before`; в интерфейсе основное действие — «Откатить».

## 8. Противоречия в документах (исправить в PLAN.md при случае)

1. AGENTS.md и PLAN.md упоминают Hyper-V, решение — только Windows Sandbox позже. 2. PLAN.md: «0.1 без служб» — теперь 0.1 = M1+M2. 3. В PLAN §3 нет состояния `denied`. 4. «Один UAC при запуске» — на деле при первом изменении, плюс heartbeat. 5. `ApplyRegistryValue` заменён на `ApplyTweaks(tweakId)`. 6. «Зависимые — запрет» vs ReadOnly в ServicePolicy. 7. modes.json обещает «без лишних служб», в режимах служб нет. 8. Нет строгого конвертера enum и `CheckDeleteValue`. 9. ZIP vs MSI: каталог `%ProgramData%\Chistovik` создаёт агент, если его нет. 10. «Отложить» автозагрузку (задача Delay) в 0.1 исключено, вернуть в 0.2 с тестом.

## 9. Не уверен (проверить позже в Sandbox/чтением)

WAL для читателя без права записи (при выходе агент делает `wal_checkpoint(TRUNCATE)` и `journal_mode=DELETE`, при старте `WAL`; тест W1 во временном каталоге). Байты StartupApproved (чётный первый байт — включено; сверить с Диспетчером задач). Схемы high/saver на Modern Standby: `skipped(not-found)`. Симлинки HKU→HKLM: защита через `ResolvedPath` нужна при любом ответе. `PowerDuplicateScheme` с заданным GUID: если нельзя, искать схему по имени.

Ограничение 0.1: цель ярлыка `.lnk` в папке «Автозагрузка» не разрешается — политика видит только путь самого ярлыка. В 0.2 — порт `IShortcutResolver` (IShellLink, только чтение).

Ограничение 0.1: если исключение случилось после Write, но до Complete, операция закрывается как failed/internal; шаг при следующем старте чинит Recovery (по сравнению с after/before), но состояние самой операции остаётся failed.

Ограничение 0.1: откат включения задачи класса RevertOnly сравнивает только exists/enabled, без xmlSha256; усиление проверки запланировано на 0.2.
