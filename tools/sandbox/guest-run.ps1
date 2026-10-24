<#
.SYNOPSIS
  Скрипт ВНУТРИ Windows Sandbox: готовит окружение, запускает живой стенд Sandbox.Live и проверку установщика.
.DESCRIPTION
  Вызывается из LogonCommand файла .wsb (его создаёт Run-Sandbox.ps1). Вне Sandbox отказывается работать:
  проверяет, что учётная запись WDAGUtilityAccount, и только тогда пишет маркер с токеном запуска. Стенд (Sandbox.Live.exe)
  дополнительно сверяет маркер и токен сам. Результаты пишутся в C:\Results (папка хоста с правом записи).
  PowerShell 5.1.
#>
param(
  [Parameter(Mandatory = $true)][string]$Token,
  [string]$StandDir = 'C:\Stand',
  [string]$ReleaseDir = 'C:\Release',
  [string]$MsiDir = 'C:\Msi',
  [string]$ResultsDir = 'C:\Results',
  [string]$Only = '',
  [switch]$SkipIdle,
  [switch]$SkipInstaller,
  [switch]$Elevated
)

$ErrorActionPreference = 'Continue'

# 1. Только Windows Sandbox.
# Два независимых признака: имя из токена Windows (не из переменной окружения) и служба CExecSvc, которая есть только в Sandbox.
$sbUser = ([Security.Principal.WindowsIdentity]::GetCurrent().Name -split '\\')[-1]
$sbService = Get-Service -Name 'CExecSvc' -ErrorAction SilentlyContinue
if ($sbUser -ine 'WDAGUtilityAccount' -or -not $sbService) {
  Write-Host 'Только в Windows Sandbox. Скрипт ничего не сделал: нет учётной записи WDAGUtilityAccount или службы CExecSvc.'
  exit 3
}
if ($Token.Length -lt 16) { Write-Host 'Токен запуска слишком короткий.'; exit 2 }

# 2. Права администратора. В Sandbox пользователь администратор и окно UAC не показывается; если токен всё же не повышен, перезапускаемся.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  if ($Elevated) { Write-Host 'Не удалось получить права администратора.'; exit 5 }
  $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Token', $Token, '-Elevated')
  if ($SkipIdle) { $argList += '-SkipIdle' }
  if ($SkipInstaller) { $argList += '-SkipInstaller' }
  if ($Only) { $argList += @('-Only', $Only) }
  Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
  exit 0
}

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$log = Join-Path $ResultsDir 'guest-run.log'
function Log([string]$m) { $line = '{0:HH:mm:ss} {1}' -f (Get-Date), $m; Write-Host $line; Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }

# 3. Маркер Sandbox: стенд требует, чтобы его содержимое совпало с токеном из командной строки.
[IO.File]::WriteAllText('C:\ProgramData\ChistovikSandbox.marker', $Token, (New-Object Text.ASCIIEncoding))
Log 'маркер Sandbox записан'

# 4. Подписанный тестовый файл для служб и задач. Самоподписанный сертификат ставится в доверенные хранилища ЭТОГО Sandbox.
$svcExe = $null
try {
  $svcDir = Join-Path $env:ProgramFiles 'ChistovikTest'
  New-Item -ItemType Directory -Force -Path $svcDir | Out-Null
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ChistovikTest Sandbox' -CertStoreLocation 'Cert:\CurrentUser\My'
  $cer = Join-Path $env:TEMP 'chistovik-test.cer'
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
  $target = Join-Path $svcDir 'svc.exe'
  Copy-Item -LiteralPath (Join-Path $StandDir 'Sandbox.Live.exe') -Destination $target -Force
  $sig = Set-AuthenticodeSignature -FilePath $target -Certificate $cert
  Log ('подпись тестового файла: ' + $sig.Status)
  if ($sig.Status -eq 'Valid') { $svcExe = $target }
} catch {
  Log ('подготовка подписи не удалась: ' + $_.Exception.Message)
}

# 5. Живой стенд.
$stand = Join-Path $StandDir 'Sandbox.Live.exe'
$standArgs = @('--results', $ResultsDir, '--token', $Token, '--agent', (Join-Path $ReleaseDir 'Chistovik.Agent.exe'))
if ($svcExe) { $standArgs += @('--svc-exe', $svcExe) }
if ($SkipIdle) { $standArgs += '--skip-idle' }
if ($Only) { $standArgs += @('--only', $Only) }
Log ('запуск стенда: ' + $stand)
& $stand @standArgs 2>&1 | Tee-Object -FilePath (Join-Path $ResultsDir 'stand.log')
$standCode = $LASTEXITCODE
Log ('стенд завершился с кодом ' + $standCode)
Set-Content -LiteralPath (Join-Path $ResultsDir 'stand-exitcode.txt') -Value $standCode

# 6. Установщик (сценарий 8).
if (-not $SkipInstaller) {
  $msi = Get-ChildItem -LiteralPath $MsiDir -Filter 'Chistovik-*-x64.msi' -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($msi) {
    Log ('проверка установщика: ' + $msi.FullName)
    & (Join-Path $PSScriptRoot 'installer-check.ps1') -MsiPath $msi.FullName -ReleaseDir $ReleaseDir -ResultsDir $ResultsDir
  } else {
    Log 'MSI не передан (папка C:\Msi пуста): сценарий 8 пропущен'
  }
}

Set-Content -LiteralPath (Join-Path $ResultsDir 'DONE.txt') -Value ('{0:u}' -f (Get-Date))
Log 'готово; Sandbox можно закрыть (всё, что стенд создал, исчезнет вместе с ним)'
