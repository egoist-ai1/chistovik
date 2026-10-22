# Досье фактов: продукт «удаление программ + очистка + оптимизация Windows»

Дата сбора: 22.10.2026. Только факты и пометки о надёжности. Выводы и оценки в этот файл не входят.
Метки: [П] — прочитан первоисточник; [В] — вторичный источник или пересказ; «нет данных» — не найдено.
Исполнитель: разработчик-одиночка из России (Egoist), помощники — Claude Code и Codex. Есть опыт WPF/.NET (Egoist Voice, Gate), Egoist DS (лайм-графит).

## 1. Конкуренты (октябрь 2026)

| Продукт | Версия, дата | Цена, модель | Особенности |
|---|---|---|---|
| Revo Uninstaller | Pro 5.5.4 (23.09.2026), Free 2.7.0 (12.05.2026) | Pro $24.95, Portable $29.95, бессрочно + 1 год обновлений; в РФ 770 ₽ у реселлера [revouninstaller.com](https://www.revouninstaller.com/buy-now-revo-uninstaller-pro/) | мониторинг установки, три глубины скана, точка восстановления и копия реестра, закрытый код. Trustpilot 4.8 (317) [Trustpilot](https://www.trustpilot.com/review/www.revouninstaller.com) |
| IObit Uninstaller / Advanced SystemCare | 16.0 / 19–20 | ASC Pro $29.99/год; IU Pro $19.99→$12.97/год | бандлинг и PUP-метки в прошлом; Trustpilot 4.6 (2484), на ld.iobit.com 1.7 (41). Оплата из РФ неясна |
| Geek Uninstaller | 1.6.0.200 (14.10.2026), есть ARM64 | Free личное; Pro от $24.95 | портативный, принудительное удаление, пакетное удаление платное |
| Bulk Crap Uninstaller | v6.3 (07.09.2026) | бесплатно, Apache-2.0, 22k звёзд | UWP, Steam, Features, тихое удаление, портативные программы, слой Confidence для остатков; нет точки восстановления и мониторинга установки; проект ищет мейнтейнеров; ложные срабатывания AV (Wacatac, issue #770) |
| Wise Program Uninstaller / Wise Care 365 | 3.3.1 / 8.0.6 | WPU Pro $19.95/год; WC365 Pro $39.95/год | апсейл, жалобы на чистку реестра |
| CCleaner | 7.x | Pro $44.95/год | Trustpilot 4.0 (28 642); PUA-метка Defender 2020; взлом сборки 2017 (2,27 млн машин); в РФ не продаётся с 2022/2024 [3DNews](https://3dnews.ru/1099575/avast-i-ccleaner-bolshe-ne-rabotayut-v-rossii) |
| Your Uninstaller 7 | 7.5.3.1287 (23.06.2026) | $24.99–$99.99 разово | — |
| Ashampoo UnInstaller 15 / WinOptimizer | v15 / v28 | UnInstaller от $12; WinOptimizer $30 | фоновый мониторинг, снимки системы, нужен аккаунт |
| Glary Utilities | 6.49 | Pro $39.95/год | Trustpilot 3.3–3.6 |
| BleachBit | 6.0.6 (17.10.2026) | бесплатно, GPL-3 | кэши и приватность, деинсталлятора нет, импорт Winapp2.ini |
| PrivaZer | 4.0.127 | Free / донаты / PRO $49 | приватность, портативная версия |
| CleanMyPC | заморожен MacPaw | $39.95/год | не развивается |
| Microsoft PC Manager | 3.22.6 (02.10.2026) | бесплатно | встроенный конкурент; Bing/Edge-навязывание критиковали |
| Windows Storage Sense / Cleanup recommendations | встроено | бесплатно | временные файлы, неиспользуемые приложения |
| O&O AppBuster | 1.4.1345 (07.2024) | бесплатно | только UWP, откат одним кликом, не обновлялся |
| winget / Scoop / Chocolatey | winget 1.12 | бесплатно | лишь вызывают штатный деинсталлятор |
| WinUtil, Sophia Script 8.0, ShutUp10++, Privacy.sexy, Optimizer (архив), Tron | — | бесплатно | твики, не деинсталляторы |
| Uninstalr 3.0 / HiBit / Total Uninstall / Uninstall Tool / Soft Organizer | — | — | в РФ-обзорах Soft Organizer, IObit, Uninstall Tool, Total Uninstall — в топе; Uninstall Tool трудно купить из РФ |

Рынок PC-оптимизаторов 2025: оценки $0.9–5.6 млрд, CAGR 4.9–9.6 % — отчёты без методики, разброс 6×, [В]. Доля ОС (StatCounter, 09.2026, мир): Win11 71.44 %, Win10 27.82 %; Steam: 71.3 / 23.6 %. Россия (StatCounter через Lenta, 07.2026): Win10 51.1 %, Win11 44.6 %, Win7 2.6 % [В]. Поддержка Win10 закончилась 14.10.2025; потребительский ESU до 12.10.2027 ($30 или бесплатно через Rewards/синхронизацию) [П]. Данных о продажах утилит после конца поддержки нет.

## 2. Единственный найденный независимый-ish бенчмарк (с конфликтом интересов)

Автор — разработчик Uninstalr (конкурента), он же автор измерителя WinFindr. 26.03.2026, VM Win11, 1 прогон. Перекрёстно не проверен. [П] https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- Точность по 406 следам (Adobe CC, Discord, игра Steam): Uninstalr 94.3 %, HiBit 89.9 %, Total Uninstall 86.0 %, Geek 68.7 %, Revo 64.8 %, Revo Pro 63.1 %, Wise 62.8 %, BCU 61.3 %, штатные Параметры Windows 59.4 %.
- Портативные Notepad++ и Brave и остатки CCleaner: 8 из 9 инструментов обнаружили 0 из 3.
- Сломанная установка (удалены файлы деинсталлятора): удалили только Total Uninstall и Uninstalr.
- Ложные срабатывания, скорость, повторяемость — не измерялись.
Вывод для плана: независимой методики и корпуса тестов для «полноты удаления» нет — это пустое место рынка и одновременно проблема доказательства «100 %».

## 3. Telegram и X через Egoist Social (22.10.2026)

Выборка: 5 публичных каналов о софте (portablik, programsSoftt, tutsoft, all_tweaker, pc_repackers), 3727 постов, около 119 млн суммарных просмотров. Задание job_zfycns3055eboxqm, completed, без пробелов. Каналы подобраны по веб-поиску (не полная карта сегмента).
- Посты про деинсталляторы ("uninstaller"): 20 постов, 251 тыс. просмотров (≈0,2 % корпуса). "оптимизац*": 142 поста, 2,3 млн просмотров. "cleaner": 19 постов, 240 тыс.
- Слово «лечение» (кряки/патчи) встречается в 751 посте из 3727 (20 %), эти посты дают 39,7 млн просмотров (≈33 % всех). Каналы — преимущественно раздача взломанных платных программ.
- Лучшие посты о деинсталляторах: Revo Uninstaller Pro v5.1.7 на portablik — 68 338 просмотров, 464 пересылки (2023), HiBit Uninstaller — 60 690 / 493 (2023), Display Driver Uninstaller — 41 265 / 121 (07.2026). На programsSoftt те же темы дают ≈2–4 тыс. просмотров.
- Пост Bazis: пользователи рекомендуют «удалить все продукты Базис через Revo Uninstaller Pro или аналог, почистить реестр, поставить заново» — вариант использования «чистая переустановка профессионального ПО» (CAD, драйверы, Adobe).
- Комментарии Telegram недоступны (UNSUPPORTED), только посты.
- X: платформенный поиск отдаёт 5 записей на страницу (partial). Найдены: официальный @VSRevoUnin (посты 74–431 просмотров), SEO-пост MacKeeper про альтернативу Revo, шум (VTuber-спам). Русский запрос — 0 результатов. Данные по X бедные, это ограничение инструмента, а не доказательство отсутствия обсуждения.
- Instagram не использовался.
- Веб-поиск по Reddit/форумам: прямых тредов не нашёл (инструмент не выдал).

## 4. Техническая карта покрытия (кратко; полный список — в отчёте агента, ниже ключевое)

Типы: Win32 EXE (ARP HKLM/HKCU/WOW6432Node, QuietUninstallString; разбор строки только CommandLineToArgvW), MSI (API MsiEnumProductsEx / MsiConfigureProductEx; кэш C:\Windows\Installer не трогать — Microsoft не поддерживает), MSIX/AppX (Remove-AppxPackage -AllUsers, DISM провижининг отдельно, NonRemovable → 0x80073CFA), winget/Choco/Scoop, портативные (нет записи — эвристика), драйверы (pnputil /delete-driver /uninstall), Features/Capabilities (DISM), WSL (необратимо), Visual Studio (InstallCleanup крайняя мера), Office C2R (SaRA), Edge/WebView2 (код 532 на 24H2), игровые лончеры, Java/Adobe/OEM.
Следы вне ARP: Run/RunOnce, StartupApproved, службы, задачи (TaskCache), COM/CLSID/TypeLib, shell-расширения, ассоциации (UserChoice защищён хешем), App Paths, SharedDLLs (счётчик), шрифты, PATH (REG_EXPAND_SZ), правила брандмауэра, WMI, IFEO, AppInit, Winlogon.
Папки: AppData/LocalAppData/LocalLow/ProgramData/ProgramFiles x86 + все профили; WinSxS только через DISM; Package Cache и Installer — только сироты, в карантин; WindowsApps — только через AppX API.
Win10 vs Win11: NonRemovable-пакеты; политика RemoveDefaultMicrosoftStorePackages (Enterprise/Edu); Recall отдельный optional feature (только Copilot+); Smart App Control блокирует неподписанные (с апреля 2026 управляем без переустановки); Dev Drive/ReFS; ARM64 без WOW64-перенаправления для x64 (инструмент должен быть нативным/Arm64X).
Безопасность: Checkpoint-Computer раз в 24 часа (лимит снимается ключом SystemRestorePointCreationFrequency — потом вернуть), экспорт реестра перед каждой правкой, карантин с манифестом (путь, ACL, хеш), запретные зоны (WinSxS, DriverStore, Installer, Package Cache, WindowsApps, общие компоненты), не брать TrustedInstaller/SYSTEM, не принудительно убивать процессы до штатного uninstaller (PendingFileRenameOperations).
Реализация: C#/.NET 8–10 или Rust; реестр с RegistryView 64/32 и обход всех SID/офлайн NTUSER.DAT; MSI через msi.dll; AppX через WinRT PackageManager; DISM API; USN Journal (FSCTL_ENUM_USN_DATA) для быстрого индекса файлов; ETW (Kernel-File/Registry/Process) для «установки под наблюдением» (PID переиспользуются, registry-события дают keyObject).
Аналоги: Revo — traced install с журналом и откатом; BCU — фабрики источников (Registry, StoreApp, Steam, Chocolatey, Scoop, Feature, Update, Oculus, Directory, Predefined), JunkManager + Confidence.
Частые ошибки конкурентов: ложные остатки по имени вендора (общий вендор → поломка соседнего приложения), краш на общих MSI-компонентах, чистка Installer/Package Cache, неверный разбор UninstallString, игнор HKCU/других профилей, AppX только для текущего пользователя, удаление Edge/WebView2, реестровые чистильщики без бэкапа, отсутствие точки восстановления, принудительное убийство процессов.
Не проверено: флаги NSIS/InstallShield/Squirrel/ClickOnce, точный список WRP-ключей, поведение Prism на AppCompatFlags, реакции Defender на массовую чистку автозапуска, параметры WinRT PackageManager.

## 5. Доверие, право, дистрибуция, деньги

- Microsoft не поддерживает чистильщики реестра (KB 2563254) [В]. Reg Organizer (Chemtable) с 31.07.2025 отключил чистку реестра по умолчанию [П]. Хабр (X-Com): чистка реестра почти не ускоряет, риск реален [П]. Критерии PUA Microsoft включают «ложные заявления о файлах и записях реестра» и «преувеличенные заявления о состоянии устройства» [П]: маркетинг «ускорит ПК на 300 %» опасен.
- Тестов утилит очистки AV-TEST/AV-Comparatives за 2025–2026 не найдено.
- Подпись кода: сертификаты с 01.03.2026 живут ≤ 460 дней; OV $150–300/год, EV $400+; ключ на HSM/токене. EV больше не даёт мгновенной репутации SmartScreen [П]. Azure Artifact Signing ($9.99/мес): физлица — только США и Канада; России нет [П]. DigiCert приостановил выдачу сертификатов, связанных с Россией и Беларусью [П]; Sectigo: Россия в запретном списке [В]. Политика остальных CA для подписи кода россиянам — нет данных.
- SignPath Foundation (бесплатная подпись для OSS): нужна OSI-лицензия, без проприетарных компонентов и двойного лицензирования, ручное одобрение релиза; ограничений по странам на странице нет [П]. Это существенно связывает лицензию продукта с путём подписи.
- Microsoft Store: MSIX подписывает Microsoft бесплатно, регистрация без платы; доступна ли из РФ и комиссия — нет данных [П].
- Платежи: Paddle и Stripe — России нет [П]; Gumroad — выплаты в РФ не поддерживаются [В]; Lemon Squeezy и FastSpring — нет данных. Boosty (11,7 % комиссия с 02.2026), ЮKassa и Robokassa с самозанятым, НПД 4 %/6 % до 2,4 млн ₽ [В]; продажа ПО иностранцам самозанятым — нет данных.
- Реестр российского ПО (ПП №1236): возможен для гражданина РФ; применимость требований по ОС к утилите неясна; льгота НДС [В].
- Лицензии источников знаний: Winapp2.ini — CC BY-SA 4.0 (указание авторства, ShareAlike) [П]; BleachBit — GPL-3.0+, CleanerML «не обременён» [П]; BCU — Apache-2.0, NOTICE не проверен [П]. Onest — OFL; Unbounded — не подтверждено; Phosphor — MIT репозитория, лицензия иконок не подтверждена.
- Монетизация категории: бессрочная лицензия $12–30 (Revo, Geek, Your Uninstaller), подписки $20–45/год (IObit, Wise, CCleaner, Glary). Freemium-конверсия потребительских утилит 1–3 % [В]. CAC и B2B/MSP — нет данных.
- Каналы РФ: Telegram замедляется (подтверждено 02.2026), охват 90,6 млн/мес (Mediascope, 01.2025); YouTube замедлен; Хабр скептичен к «ускорителям»; 4PDA раздаёт архивы с твиками без проверок. Данные по MyDiv, OSZone, ru-board, vc.ru — нет.
- Риски платформы 2026: встроенные PC Manager и Storage Sense; HVCI автоматически на подходящих ПК с 13.10.2026; драйверы cross-signed теряют доверие ядра (с 04.2026); агентные функции Windows (выкл. по умолчанию); Secure Boot-сертификаты 2011 истекают.
- Вторичные факты для сверки: Revo 60-дневный возврат; число пользователей у большинства продуктов — нет данных.
