<#
.SYNOPSIS
  ХОСТ: готовит файл .wsb для Windows Sandbox и (только с -Launch) запускает его. Сам ничего не проверяет и не меняет на ПК.
.DESCRIPTION
  По умолчанию только создаёт <ResultsDir>\chistovik-live.wsb и печатает, что дальше. Запуск Sandbox владелец делает сам
  (двойной щелчок по .wsb) либо этим скриптом с ключом -Launch.
  Конфигурация: релиз и стенд смонтированы только на чтение, <ResultsDir> на запись, сеть отключена, vGPU отключён,
  буфер обмена, принтеры, аудио и видео отключены, ProtectedClient включён (если мешает: -NoProtectedClient).
  Токен запуска случайный: его знают только .wsb и скрипты внутри Sandbox; без него стенд отказывается работать.
  PowerShell 5.1.
.PARAMETER StandDir
  Папка с опубликованным стендом (self-contained): dotnet publish tests\Sandbox.Live -c Release -r win-x64 --self-contained -o <папка>.
.PARAMETER ReleaseDir
  Папка с Chistovik.Agent.exe и Chistovik.App.exe (publish-x64 релизной сборки).
.PARAMETER MsiPath
  Необязательно: MSI для сценария 8 (установка/удаление в Sandbox).
.PARAMETER ResultsDir
  Куда Sandbox пишет results.json/results.md/логи. По умолчанию <корень репозитория>\artifacts\sandbox-results\<метка времени>.
#>
param(
  [Parameter(Mandatory = $true)][string]$StandDir,
  [Parameter(Mandatory = $true)][string]$ReleaseDir,
  [string]$MsiPath = '',
  [string]$ResultsDir = '',
  [switch]$SkipIdle,
  [switch]$NoProtectedClient,
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

if (-not $ResultsDir) { $ResultsDir = Join-Path $scripts ('..\..\artifacts\sandbox-results\' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')) }
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$results = (Resolve-Path -LiteralPath $ResultsDir).ProviderPath

$msiDir = $null
if ($MsiPath) {
  if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { throw "MSI не найден: $MsiPath" }
  $msiDir = Split-Path -Parent (Resolve-Path -LiteralPath $MsiPath).ProviderPath
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

$folders = (Folder $stand 'C:\Stand' $true) + (Folder $release 'C:\Release' $true) + (Folder $scripts 'C:\Scripts' $true) + (Folder $results 'C:\Results' $false)
if ($msiDir) {
  # MSI лежит в общей папке рядом с другими файлами: монтируем её только на чтение. В Sandbox нужен один пакет, берётся первый Chistovik-*-x64.msi.
  $folders += (Folder $msiDir 'C:\Msi' $true)
}

$guestArgs = "-Token $token"
if ($SkipIdle) { $guestArgs += ' -SkipIdle' }
if (-not $msiDir) { $guestArgs += ' -SkipInstaller' }
$protected = if ($NoProtectedClient) { 'Disable' } else { 'Enable' }

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
Write-Host 'Сеть в Sandbox отключена, релиз и стенд смонтированы только на чтение.'

if ($Launch) {
  $exe = Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe'
  if (-not (Test-Path -LiteralPath $exe)) { throw 'Компонент «Песочница Windows» не включён (WindowsSandbox.exe не найден).' }
  Start-Process -FilePath $exe -ArgumentList "`"$wsbPath`""
  Write-Host 'Sandbox запущен.'
} else {
  Write-Host 'Sandbox не запускался. Запустить: двойной щелчок по .wsb или повторить команду с ключом -Launch.'
}
