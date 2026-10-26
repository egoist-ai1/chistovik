<#
.SYNOPSIS
  ХОСТ: готовит файл .wsb для Windows Sandbox и (только с -Launch) запускает его. Сам ничего не проверяет и не меняет на ПК.
.DESCRIPTION
  По умолчанию только создаёт <ResultsDir>\chistovik-live.wsb и печатает, что дальше. Запуск Sandbox владелец делает сам
  (двойной щелчок по .wsb) либо этим скриптом с ключом -Launch.
  Конфигурация: релиз и стенд смонтированы только на чтение, <ResultsDir> на запись, сеть отключена, vGPU отключён,
  буфер обмена, принтеры, аудио и видео отключены. ProtectedClient по умолчанию ВЫКЛЮЧЕН (первый прогон: он может мешать записи
  в смонтированную папку результатов и запуску агента); включить: -ProtectedClient.
  Скрипты и MSI копируются в <ResultsDir>\scripts и <ResultsDir>\msi, и в Sandbox монтируются именно эти копии на чтение:
  путь без кириллицы, и гость не видит остальное содержимое папок репозитория и MSI.
  Токен запуска случайный: его знают только .wsb и скрипты внутри Sandbox; без него стенд отказывается работать.
  PowerShell 5.1.
.PARAMETER StandDir
  Папка с опубликованным стендом (self-contained): dotnet publish tests\Sandbox.Live -c Release -r win-x64 --self-contained -o <папка>.
.PARAMETER ReleaseDir
  Папка с Chistovik.Agent.exe и Chistovik.App.exe (publish-x64 релизной сборки).
.PARAMETER MsiPath
  Необязательно: MSI для сценария 8 (установка/удаление в Sandbox).
.PARAMETER OldMsiPath
  Необязательно: СТАРЫЙ MSI для проверки обновления поверх (MajorUpgrade) в сценарии 8; нужен вместе с -MsiPath (новый пакет). Копируется один в <ResultsDir>\msi-old и монтируется на чтение как C:\MsiOld.
.PARAMETER InstallersDir
  Необязательно: папка с установщиками для сценария 9 (удаление программ: *.msi и *.exe). Копируется в <ResultsDir>\installers и монтируется на чтение как C:\Installers.
.PARAMETER Only
  Необязательно: выполнить только перечисленные сценарии стенда (например S9 или S1,S2).
.PARAMETER ResultsDir
  Куда Sandbox пишет results.json/results.md/логи. Путь только ASCII (без кириллицы). По умолчанию C:\Users\Public\ChistovikSandbox\<метка времени>.
#>
param(
  [Parameter(Mandatory = $true)][string]$StandDir,
  [Parameter(Mandatory = $true)][string]$ReleaseDir,
  [string]$MsiPath = '',
  [string]$OldMsiPath = '',
  [string]$ResultsDir = '',
  [string]$InstallersDir = '',
  [string]$Only = '',
  [switch]$SkipIdle,
  [switch]$ProtectedClient,
  [switch]$Launch
)

$ErrorActionPreference = 'Stop'

function Resolve-Dir([string]$p, [string]$what) {
  if (-not (Test-Path -LiteralPath $p -PathType Container)) { throw "Не найдена папка $what`: $p" }
  return (Resolve-Path -LiteralPath $p).ProviderPath
}

$stand = Resolve-Dir $StandDir 'стенда'
$release = Resolve-Dir $ReleaseDir 'релиза'
if (-not (Test-Path -LiteralPath (Join-Path $stand 'Sandbox.Live.exe'))) { throw "В $stand нет Sandbox.Live.exe (нужен dotnet publish --self-contained)." }
if (-not (Test-Path -LiteralPath (Join-Path $release 'Chistovik.Agent.exe'))) { throw "В $release нет Chistovik.Agent.exe." }
$scripts = $PSScriptRoot

if (-not $ResultsDir) { $ResultsDir = Join-Path $env:PUBLIC ('ChistovikSandbox\' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')) }
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$results = (Resolve-Path -LiteralPath $ResultsDir).ProviderPath
if ($results -notmatch '^[\x20-\x7E]+$') { throw "Папка результатов должна быть с ASCII-путём (без кириллицы): $results" }

# Гость получает копии скриптов (только *.ps1) на чтение, а не рабочую папку репозитория.
$scriptsCopy = Join-Path $results 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsCopy | Out-Null
Get-ChildItem -LiteralPath $scripts -Filter '*.ps1' -File | Copy-Item -Destination $scriptsCopy -Force

$msiDir = $null
if ($MsiPath) {
  if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { throw "MSI не найден: $MsiPath" }
  # Только сам пакет, без остальных файлов его папки.
  $msiDir = Join-Path $results 'msi'
  New-Item -ItemType Directory -Force -Path $msiDir | Out-Null
  Copy-Item -LiteralPath (Resolve-Path -LiteralPath $MsiPath).ProviderPath -Destination $msiDir -Force
}

$oldMsiDir = $null
$oldMsiName = ''
if ($OldMsiPath) {
  if (-not $MsiPath) { throw '-OldMsiPath требует -MsiPath (новый пакет для обновления поверх).' }
  if (-not (Test-Path -LiteralPath $OldMsiPath -PathType Leaf)) { throw "Старый MSI не найден: $OldMsiPath" }
  $oldMsiName = Split-Path -Leaf $OldMsiPath
  if ($oldMsiName -notmatch '^[A-Za-z0-9._-]+$') { throw "Имя старого MSI должно быть ASCII без пробелов: $oldMsiName" }
  # Только сам старый пакет, отдельная папка: guest-run берёт «первый Chistovik-*-x64.msi» только из C:\Msi.
  $oldMsiDir = Join-Path $results 'msi-old'
  New-Item -ItemType Directory -Force -Path $oldMsiDir | Out-Null
  Copy-Item -LiteralPath (Resolve-Path -LiteralPath $OldMsiPath).ProviderPath -Destination $oldMsiDir -Force
}

$installersCopy = $null
if ($InstallersDir) {
  if (-not (Test-Path -LiteralPath $InstallersDir -PathType Container)) { throw "Не найдена папка установщиков: $InstallersDir" }
  # Только установщики, без остальных файлов (fetch.txt и прочего).
  $installersCopy = Join-Path $results 'installers'
  New-Item -ItemType Directory -Force -Path $installersCopy | Out-Null
  Get-ChildItem -LiteralPath $InstallersDir -File | Where-Object { $_.Extension -in '.msi', '.exe' } | Copy-Item -Destination $installersCopy -Force
}

# Токен: 32 случайных шестнадцатеричных символа.
$bytes = New-Object byte[] 16
$rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($bytes)
$rng.Dispose()
$token = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''

function X([string]$s) { [Security.SecurityElement]::Escape($s) }

function Folder([string]$host_, [string]$guest, [bool]$readOnly) {
  $ro = if ($readOnly) { 'true' } else { 'false' }
  return "    <MappedFolder>`r`n      <HostFolder>$(X $host_)</HostFolder>`r`n      <SandboxFolder>$guest</SandboxFolder>`r`n      <ReadOnly>$ro</ReadOnly>`r`n    </MappedFolder>`r`n"
}

$folders = (Folder $stand 'C:\Stand' $true) + (Folder $release 'C:\Release' $true) + (Folder $scriptsCopy 'C:\Scripts' $true) + (Folder $results 'C:\Results' $false)
if ($msiDir) {
  # Копия единственного MSI, на чтение. В Sandbox берётся первый Chistovik-*-x64.msi.
  $folders += (Folder $msiDir 'C:\Msi' $true)
}

if ($oldMsiDir) { $folders += (Folder $oldMsiDir 'C:\MsiOld' $true) }

if ($installersCopy) { $folders += (Folder $installersCopy 'C:\Installers' $true) }

$guestArgs = "-Token $token"
if ($Only) { $guestArgs += " -Only $Only" }
if ($SkipIdle) { $guestArgs += ' -SkipIdle' }
if (-not $msiDir) { $guestArgs += ' -SkipInstaller' }
if ($oldMsiDir) { $guestArgs += " -OldMsiName $oldMsiName" }
$protected = if ($ProtectedClient) { 'Enable' } else { 'Disable' }

$wsb = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <ProtectedClient>$protected</ProtectedClient>
  <MappedFolders>
$folders  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File C:\Scripts\guest-run.ps1 $guestArgs</Command>
  </LogonCommand>
</Configuration>
"@

$wsbPath = Join-Path $results 'chistovik-live.wsb'
[IO.File]::WriteAllText($wsbPath, $wsb, (New-Object Text.UTF8Encoding $false))

Write-Host "Файл конфигурации: $wsbPath"
Write-Host "Результаты появятся в: $results (results.md, results.json, stand.log, installer-results.md)"
Write-Host 'Сеть в Sandbox отключена, релиз, стенд, скрипты и MSI смонтированы только на чтение (скрипты и MSI это копии в папке результатов).'

if ($Launch) {
  $exe = Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe'
  if (-not (Test-Path -LiteralPath $exe)) { throw 'Компонент «Песочница Windows» не включён (WindowsSandbox.exe не найден).' }
  Start-Process -FilePath $exe -ArgumentList "`"$wsbPath`""
  Write-Host 'Sandbox запущен.'
} else {
  Write-Host 'Sandbox не запускался. Запустить: двойной щелчок по .wsb или повторить команду с ключом -Launch.'
}
