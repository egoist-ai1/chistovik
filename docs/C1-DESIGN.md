# C1: удаление программ, решение архитектора (25.10.2026)

Автор: архитектор (Fable), только чтение кода. Источник: docs/CLEANUP-SPEC.md §1, §7, этап C1. Статус: решение принято, реализация идёт.

## Решение

MVP C1 удаляет только программы из HKLM (`arp:machine:{x64|x86}:<key>`). MSI удаляется через msi.dll в процессе агента (`MsiConfigureProductEx`), EXE только с `QuietUninstallString` либо с распознанной сигнатурой Inno/NSIS и только из Program Files за ACL-барьером. Программы из HKCU, Steam и другие лаунчеры, интерактивные деинсталляторы это `ReadOnly`. На экране «Чистка» одна кнопка «Удалить программу…» в панели подробностей; предотметки нет. Журнал v2 получает `step.revertible`; Undo отвечает `not-revertible`.

## Почему

- Эскалация через HKCU закрыта грамматикой идентификатора: агент под админом никогда не читает `UninstallString` из HKCU. `UninstallRequest.Validate()` отвергает `arp:user:*` как `bad-request`, `PackagePolicy` дублирует отказ.
- `UninstallString` из HKLM может указывать в каталог с правом записи для Users (Steam, игры): нужен ACL-барьер на exe и его каталог, а не только «под Program Files».
- MSI через msi.dll, а не `msiexec.exe`: нет разбора командной строки и spawn; состояние проверяется тем же API до и после.
- Steam (`steam://uninstall/ID`): тихого режима нет, запуск `steam.exe` под админом вреден. `ReadOnly`, причина `managed-by-launcher`.
- Программы в C0 имеют вердикт Optional/Keep, не Remove, значит `PreselectAllowed` всегда false: галочек нет.
- Следствие: программы текущего пользователя (HKCU, например ATLAS) через агента не удаляются. Их удаляет собственный деинсталлятор от имени пользователя; это отдельное решение.

## План (порядок обязателен; каждый шаг собирается, тесты зелёные)

1. Core: `StepKind.UninstallRun`, `OperationKind.Uninstall` (Ports/Enums.cs). `StepRecord.Revertible`, параметр `bool revertible = true` в `AddPending/AddTerminal` (Ports/Journal.cs); фейки в tests/TestKit.
2. Журнал v2: миграция `ALTER TABLE step ADD COLUMN revertible INTEGER NOT NULL DEFAULT 1`; `StepColumns`, `ReadStep`, `InsertStep`, `SqliteJournalReader` (v1 читается, старые шаги revertible=1). Тест миграции v1→v2.
3. JournalModel: `UninstallTarget(Scope, Arch, Key, Kind msi|exe, ProductCode?)`, `UninstallState(V, Installed)`, регистрация в `JournalJson`; `StepCodes`: `not-revertible`, `timeout`, `in-use`, `skipped-by-rule`; `StateJson` (target_key `arp:machine:{x64|x86}:<key>` в нижнем регистре); `StateComparers` по `Installed`.
4. Протокол: `UninstallRequest(string FindingId)` + `JsonDerivedType "Uninstall"`; `Rules.ArpMachineId = ^arp:machine:(x64|x86):[^\\\x00-\x1F]{1,255}$`, длина ≤300; `ResponseCodes.NotRevertible`. Негативные тесты: `arp:user:*` → bad-request; лишнее поле `command` → отказ.
5. Порты (Ports/Basics.cs): `IFileSecurity.Inspect(path) → FileFacts(FinalPath, OwnerSid, Aces, Exists)`; `IUninstaller` (`QueryMsiState`, `RunMsi`, `RunExe` → `UninstallOutcome(ExitCode, TimedOut)`); в `ExecEnv` свойства `Security`, `Uninstaller`; фейки в TestKit (программируемый исход, `SimulatedCrash`).
6. `Policy/PackagePolicy.cs` (чистый Core). Порядок правил: scope≠machine → Deny `hkcu-scope`; SystemComponent / ParentKeyName / ReleaseType Update|Hotfix|Security Update → ReadOnly; Edge, WebView2, .NET Framework, Store, защищённые каталоги → Forbidden; маркеры рантаймов (вынести `FindingClassifier.RuntimeMarkers`) → ExpertOnly (= отказ в протоколе 0.1); WindowsInstaller=1 и ключ GUID → msi; иначе разбор команды без shell (перегрузка `CommandResolver.TryResolve(cmd, out exe, out args)`); интерпретаторы (cmd, powershell, wscript, cscript, rundll32, mshta, msiexec) и URI-схемы лаунчеров (`steam://`, `com.epicgames.launcher://`, `uplay://`, `goggalaxy://`) → ReadOnly `managed-by-launcher`; exe: FinalPath только под ProgramFiles/ProgramFiles(x86), владелец Администраторы/SYSTEM/TrustedInstaller, нет права записи у Users/Authenticated Users/Everyone/INTERACTIVE ни у файла, ни у каталога, иначе ReadOnly `exe-writable-dir`; есть QuietUninstallString → её аргументы; иначе Inno (`unins\d{3}\.exe` + `.dat`) → `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`; NSIS (маркер «Nullsoft» в overlay) → `/S`; иначе ReadOnly `exe-no-quiet`. Новые коды причин: `managed-by-launcher`, `exe-no-quiet`, `exe-writable-dir`, `hkcu-scope`.
7. `StepGate.CheckUninstall` (internal ctor `ApprovedUninstall`), `UninstallPlanner` (разбор id, открытие ключа ARP в HKLM, `!IsSymbolicLink`, факты, Gate, `PlannedStep(UninstallRun, revertible:false)`), `UninstallSteps` (чтение состояния: MSI `QueryMsiState==5`, EXE ключ существует; запись: `RunMsi` 10 мин с `MsiSetInternalUI(NONE)` и `REBOOT=ReallySuppress` или `RunExe`). Коды: 0 → Done; 3010 → Done + `win32:3010` + restart; 1618 → Failed `in-use`; TimedOut → Failed `timeout` (без TerminateProcess); прочее `win32:n`. `OperationHandler`: `case UninstallRequest`. `StepFactory.Rebuild`: ветка для Recovery. `UndoService`: предпроход; есть Done-шаг с `Revertible=false` → `not-revertible`, ничего не трогать.
8. Native: `Setup/MsiInterop.cs` (msi.dll: `MsiSetInternalUI`, `MsiConfigureProductExW`, `MsiQueryProductStateW`); `ProcessRunner.cs` (CreateProcessW, `CREATE_SUSPENDED` → Job Object → resume, без KILL_ON_JOB_CLOSE, ожидание `JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO` через IOCP, таймаут); `FileSecurity.cs` (`GetFinalPathNameByHandle`, `GetNamedSecurityInfo`, разбор как у `JournalAclPolicy`); регистрация в `AgentComposition`.
9. Таймауты: `AgentPipeServer` после чтения кадра снимает idle-таймаут, после записи ответа ставит заново 5 мин; `AgentClient` per-request таймаут, для Uninstall 15 мин.
10. App: `ChangeService.UninstallAsync(findingId)`, тексты ответов (`not-revertible`, `timeout`, `in-use`, `win32:3010` «нужна перезагрузка»); `ProgramInfo` получает `Id`, `Class`, `ReadOnlyReason` (App считает тем же `PackagePolicy` по фактам без прав); `CleanupScanner.ToSubject(ProgramInfo)`; `FindingClassifier.Program`: класс из subject, Reversibility `ReinstallOnly`, `HowToUndo` = «только повторной установкой»; `CleanupViewModel`: `UninstallCommand` виден при `Area==Programs && Class==Modifiable`, стандартное окно подтверждения («вернуть можно только переустановкой»), затем вызов и перезагрузка списка.
11. Тесты: Core.Tests/Execution/UninstallTests (MSI/EXE; `SimulatedCrash` между pending и complete → Recovery по `QueryMsiState`; Undo → `not-revertible`; timeout; 1618; 3010), Core.Tests/Policy/PackagePolicyNegativeTests (рантаймы и пары, SystemComponent, ParentKeyName, Framework/Store/Edge/WebView2, exe вне корней, каталог с write-ACE для Users, `arp:user:*`, symlink-ключ ARP, интерпретатор в QuietUninstallString, `steam://`, EXE без тихого режима), App.Tests: ничего не предотмечено, кнопка только у Modifiable.
12. Sandbox S9: инсталляторы 7-Zip MSI и Notepad++ NSIS монтируются с хоста, тихая установка, удаление через настоящий агент, `reg query` и отсутствие каталога; негативы `arp:user:*` → bad-request, Undo → `not-revertible`. Живая проба на ПК владельца только после PASS S9.

## Риски и непроверенное

- NSIS/Inno деинсталляторы копируют себя в %TEMP% и завершают родителя: проверять состояние после `ACTIVE_PROCESS_ZERO` и повторным чтением.
- ExpertOnly в протоколе 0.1 это отказ; рантаймы и пары удалить нельзя до отдельного этапа.
- Defender может реагировать на элевированный процесс, порождающий деинсталляторы.
- Миграция журнала необратима для App старой версии: `SqliteJournalReader` терпит новую схему, проверить тестом с v2-файлом.
- Маркер NSIS в overlay нужно проверить на Notepad++ в Sandbox; при ложном срабатывании запасной вариант `exe-no-quiet`.
- Образ Sandbox может не иметь VC-рантаймов для 7-Zip MSI.
- Перед живой пробой: `dotnet test` всех проектов, S9 PASS, ревью Policy/Agent/Undo другой моделью (AGENTS.md).
