# Чистовик

Chistovik is a Windows 10/11 control center: it uninstalls programs together with their leftovers, keeps what it removed in quarantine, rolls back any change and manages system settings (modes, power plans, startup, services, registry tweaks). Every change is explained before it is applied and written to a journal with an undo. The interface is in Russian.

Чистовик — центр управления Windows 10/11. Удаляет программы вместе с остатками, держит удалённое в карантине, откатывает любое изменение и управляет настройками системы: режимы, схемы питания, автозагрузка, службы, твики реестра.

[Скачать последний выпуск](../../releases/latest) · [Заметки о выпуске 0.2.0](docs/RELEASE-0.2.0.md) · [Лицензия GPL-3.0](LICENSE)

## Установка

Скачайте MSI под свою систему со страницы выпуска и сверьте SHA-256 из `SHA256SUMS.txt`.

| Файл | Система |
|---|---|
| `Chistovik-<версия>-x64.msi` | Windows 10 1809+ и Windows 11 на x64 |
| `Chistovik-<версия>-arm64.msi` | Windows 11 на ARM64 |

```
msiexec /i Chistovik-0.2.0-x64.msi
```

.NET ставить не нужно: пакет самодостаточный. Каталог установки — `C:\Program Files\Chistovik`, ярлык в меню «Пуск». Ключ `/qn` — тихая установка. Обновление ставится поверх, данные пользователя сохраняются.

Пока выпуски не подписаны, SmartScreen предупредит о неизвестном издателе. Подпись кода — отдельный шаг, описан ниже и в [docs/SIGNING.md](docs/SIGNING.md).

## Как это устроено и чему можно доверять

- **Приложение работает без прав администратора.** Изменения системы выполняет отдельный агент `Chistovik.Agent.exe`; он запускается по запросу пользователя через UAC и общается с приложением по именованному каналу. Без повышения приложение остаётся в режиме просмотра.
- **Решение «можно ли это менять» принимает один модуль — `Chistovik.Core.Policy`.** Часть служб, задач и ключей реестра защищена от изменения всегда; негативные тесты политики (`tests/Policy.Negative`) блокируют сборку при любом ослаблении.
- **Журнал и откат.** Каждое изменение записывается в локальную базу SQLite с прежним значением; откат доступен из журнала. Удаляемые файлы сначала попадают в карантин.
- **Превью.** Для чистки и твиков показывается, что именно будет изменено и почему, до применения.

## Конфиденциальность

Чистовик не передаёт никаких данных. В программе нет сетевого кода: нет телеметрии, отчётов об ошибках, проверки обновлений и учётных записей. Журнал, карантин и журналы очистки лежат на вашем компьютере в `%ProgramData%\Chistovik` (каталог с защищённым ACL), настройки окна — в `%LOCALAPPDATA%\Chistovik\ui.json`. Проверить это можно по исходному коду: в `src` нет `HttpClient`, `WebClient` и сокетов во внешнюю сеть.

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. (As shipped, it has no networking code at all.)

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

- **Scope.** Only binaries built from this repository by the GitHub Actions workflow [`release.yml`](.github/workflows/release.yml) from a tagged commit are submitted for signing: `Chistovik.App.exe`, `Chistovik.Agent.exe`, `Chistovik.*.dll` and the MSI packages. Third-party binaries (the .NET runtime, SQLite) keep their own vendors' signatures and are not re-signed.
- **Roles.** Committers and reviewers, and approvers: the project owner, [@egoist-ai1](https://github.com/egoist-ai1). See [docs/SIGNING.md](docs/SIGNING.md).
- **Process.** Every signing request is made by the CI workflow after a successful build; a release signature is approved manually by an approver. Accounts of everyone with signing rights use multi-factor authentication.
- **Privacy.** See the "Конфиденциальность" section above: the program does not send any data.

## Сборка из исходников

Нужны Windows 10/11 и SDK .NET из [global.json](global.json) (локально его можно положить в `.dotnet`).

```
dotnet build Chistovik.sln -c Release
dotnet test tests/Policy.Negative -c Release
dotnet test tests/Core.Tests -c Release
dotnet test tests/Native.Tests -c Release
```

Релизные пакеты собирает `tools/build-release.ps1`: publish для x64 и ARM64, MSI на архитектуру (WiX 5), `SHA256SUMS.txt`; результат в `artifacts/release/<версия>/`. Версия берётся из `Directory.Build.props`.

Сопровождающим: [проверка в песочнице](docs/FACTS.md), [план](docs/PLAN.md), [требования к продукту](docs/PRODUCT.md).

## Снимки экранов

Режим `--snapshots <папка>` рисует каждый экран и диалог в PNG на демонстрационных данных (реестр, службы и файлы системы не читаются и не меняются). Окно не показывается и не забирает фокус.

```
dotnet run --project src/Chistovik.App -c Release -- --snapshots "C:\Temp\chistovik-shots"
```

Имя файла: `<экран>[-состояние]_<ширина>x<высота>_<dpi>.png`. Размеры от 900×600 до 1920×1080, DPI 96 и 192. Перечень задаёт `src/Chistovik.App/Snapshots/SnapshotPlan.cs`, он проверен юнит-тестом.

## Шрифты и значок

Шрифты встроены: Unbounded (заголовки), Onest (текст), JetBrains Mono (команды и пути), все три под SIL OFL 1.1 (`licenses/`). Значок и графика установщика — в `installer/assets`. Сторонние компоненты — [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Горячие клавиши и доступность

Ctrl+1 … Ctrl+8 — разделы, F5 — перечитать, Ctrl+F — поиск, Esc — закрыть окно подтверждения. Окно помнит размер, позицию и раздел. При отключённых анимациях Windows переходы мгновенные; в режиме высокого контраста цвета берутся из системной палитры.

## Сообщить о проблеме

Ошибки и пожелания — в [Issues](../../issues). Проблемы безопасности (обход политики, повышение прав через канал агента) не публикуйте в открытых Issues: используйте приватный отчёт GitHub Security Advisories этого репозитория.

## Лицензия

GPL-3.0-or-later, см. [LICENSE](LICENSE). Copyright (c) 2026 Egoist.
