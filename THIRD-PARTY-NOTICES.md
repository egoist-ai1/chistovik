# Third-party notices / Сторонние компоненты

Chistovik is licensed under GPL-3.0-or-later (see [LICENSE](LICENSE)). The release packages bundle the components below. Their licenses permit redistribution inside a GPL-3 program.

Чистовик распространяется под GPL-3.0-or-later ([LICENSE](LICENSE)). В пакет выпуска входят компоненты ниже; их лицензии разрешают распространение внутри программы под GPL-3.

| Component | Version | License | Where used |
|---|---|---|---|
| .NET runtime and libraries (self-contained) | 10.x | MIT | runtime of App and Agent |
| Windows Presentation Foundation (WPF) | 10.x | MIT | App UI |
| Microsoft.Data.Sqlite | 10.0.0 | MIT | journal and settings database |
| SQLitePCLRaw (core, provider, `lib.e_sqlite3`) | 2.1.14 | Apache-2.0 | native SQLite binding |
| SQLite (`e_sqlite3.dll`) | bundled with SQLitePCLRaw | public domain | embedded database engine |
| Unbounded | bundled | SIL OFL 1.1 ([text](licenses/Unbounded-OFL.txt)) | headings |
| Onest | bundled | SIL OFL 1.1 ([text](licenses/Onest-OFL.txt)) | body text |
| JetBrains Mono | bundled | SIL OFL 1.1 ([text](licenses/JetBrainsMono-OFL.txt)) | commands and paths |

Build and test tools are not shipped in the packages: WiX Toolset 5 (MS-RL, builds the MSI), xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

Fonts are used under the SIL Open Font License 1.1: they are not sold on their own, and the license texts travel with the sources in `licenses/`.

The exact list of NuGet packages is in the `*.csproj` files; the license text of each package is in its `.nupkg`.
