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
  [string]$InstallersDir = 'C:\Installers',
  [string]$OldMsiDir = 'C:\MsiOld',
  [string]$OldMsiName = '',
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
  if ($OldMsiName) { $argList += @('-OldMsiName', $OldMsiName) }
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
# Нужен для S4 (тестовая служба): ServicePolicy считает файл «подписанным», только если WinVerifyTrust вернул успех,
# а для самоподписанного сертификата это возможно лишь при корне в LocalMachine\Root. Каждый шаг идёт отдельно с запасными путями,
# имя шага и полный текст исключения пишутся в guest-run.log; если подпись не получилась совсем, причина уходит в стенд
# (--svc-skip-reason), и S4/тестовая служба получают ПРОПУСК с этой причиной, а не молчаливое «нет файла».
$svcExe = $null
$svcSkipReason = $null
$script:signFail = New-Object System.Collections.ArrayList

# Выполняет шаг; true, если он прошёл. Ошибка пишется с именем шага и полным текстом исключения и запоминается для причины пропуска.
function Step([string]$name, [scriptblock]$body) {
  try {
    $ErrorActionPreference = 'Stop'
    & $body | Out-Null
    Log ('подпись: шаг «' + $name + '» выполнен')
    return $true
  } catch {
    $full = ($_ | Out-String).Trim() + ' | ' + $_.Exception.ToString()
    Log ('подпись: шаг «' + $name + '» НЕ удался: ' + ($full -replace '\r?\n', ' '))
    [void]$script:signFail.Add($name + ': ' + $_.Exception.Message)
    return $false
  }
}

# Первый из способов, который сработал (каждый способ это scriptblock); false, если не сработал ни один.
function StepAny([string]$name, [scriptblock[]]$ways) {
  $i = 0
  foreach ($w in $ways) {
    $i++
    if (Step ("$name, способ $i") $w) { return $true }
  }
  return $false
}


$integrity = (whoami /groups 2>&1 | Select-String 'Mandatory Label' | ForEach-Object { $_.ToString().Trim() }) -join '; '
Log ('подпись: пользователь ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name + '; ' + $integrity)

$svcDir = $null
$cert = $null
$cer = $null
$target = $null

# 4.1 Каталог для тестового файла: сначала Program Files (путь вне зоны ProgramData), запасной ProgramData.
foreach ($base in @($env:ProgramFiles, $env:ProgramData)) {
  $dir = Join-Path $base 'ChistovikTest'
  if (Step ('создание папки ' + $dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null; if (-not (Test-Path -LiteralPath $dir)) { throw 'папка не появилась' } }) { $svcDir = $dir; break }
}

# 4.2 Самоподписанный сертификат: CurrentUser\My, затем LocalMachine\My (скрипт идёт от администратора), затем .NET CertificateRequest.
if ($svcDir) {
  foreach ($store in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
    if (Step ('New-SelfSignedCertificate в ' + $store) { $script:certTmp = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ChistovikTest Sandbox' -CertStoreLocation $store -NotAfter (Get-Date).AddYears(1); if (-not $script:certTmp) { throw 'сертификат не создан' } }) {
      $cert = $script:certTmp
      break
    }
  }
  if (-not $cert) {
    if (Step 'сертификат через .NET CertificateRequest (запасной путь)' {
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=ChistovikTest Sandbox', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $oids = New-Object Security.Cryptography.OidCollection
        [void]$oids.Add((New-Object Security.Cryptography.Oid('1.3.6.1.5.5.7.3.3')))
        $req.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
        $req.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($oids, $false)))
        $tmp = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(1))
        $bytes = $tmp.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'chistovik-test')
        $script:certTmp = New-Object Security.Cryptography.X509Certificates.X509Certificate2($bytes, 'chistovik-test', [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)
      }) { $cert = $script:certTmp }
  }
}

# 4.3 Файл .cer (открытая часть): Export-Certificate, запасной путь .NET; каталоги TEMP, затем ProgramData.
if ($cert) {
  foreach ($cdir in @($env:TEMP, $env:ProgramData)) {
    $path = Join-Path $cdir 'chistovik-test.cer'
    if (StepAny ('экспорт сертификата в ' + $path) @(
        { Export-Certificate -Cert $cert -FilePath $path -Type CERT | Out-Null; if (-not (Test-Path -LiteralPath $path)) { throw 'файл .cer не появился' } },
        { [IO.File]::WriteAllBytes($path, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)) }
      )) { $cer = $path; break }
  }
}

# 4.4 Доверие: корень обязателен для WinVerifyTrust, TrustedPublisher полезен, но не обязателен.
$rootOk = $false
if ($cer) {
  $rootOk = StepAny 'импорт в LocalMachine\Root' @(
    { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null },
    { $o = & certutil.exe -addstore -f Root $cer 2>&1; if ($LASTEXITCODE -ne 0) { throw ('certutil -addstore Root, код ' + $LASTEXITCODE + ': ' + ($o -join ' ')) } },
    { $x = New-Object Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine'); $x.Open('ReadWrite'); try { $x.Add((New-Object Security.Cryptography.X509Certificates.X509Certificate2($cer))) } finally { $x.Close() } }
  )
  [void](StepAny 'импорт в LocalMachine\TrustedPublisher' @(
    { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null },
    { $o = & certutil.exe -addstore -f TrustedPublisher $cer 2>&1; if ($LASTEXITCODE -ne 0) { throw ('certutil -addstore TrustedPublisher, код ' + $LASTEXITCODE + ': ' + ($o -join ' ')) } },
    { $x = New-Object Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'LocalMachine'); $x.Open('ReadWrite'); try { $x.Add((New-Object Security.Cryptography.X509Certificates.X509Certificate2($cer))) } finally { $x.Close() } }
  ))
}

# 4.5 Копия стенда и подпись.
if ($svcDir -and $cert) {
  $target = Join-Path $svcDir 'svc.exe'
  $copied = Step ('копирование стенда в ' + $target) { Copy-Item -LiteralPath (Join-Path $StandDir 'Sandbox.Live.exe') -Destination $target -Force; if (-not (Test-Path -LiteralPath $target)) { throw 'файл не появился' } }
  if ($copied) {
    $signed = Step 'Set-AuthenticodeSignature' {
      $sig = Set-AuthenticodeSignature -FilePath $target -Certificate $cert -HashAlgorithm SHA256
      Log ('подпись: Set-AuthenticodeSignature вернул статус ' + $sig.Status + ' (' + $sig.StatusMessage + ')')
      if ($sig.Status -ne 'Valid') { throw ('статус подписи ' + $sig.Status + ': ' + $sig.StatusMessage + $(if (-not $rootOk) { ' (корневой сертификат в LocalMachine\Root не установлен)' } else { '' })) }
    }
    $after = Get-AuthenticodeSignature -FilePath $target -ErrorAction SilentlyContinue
    if ($after) { Log ('подпись: повторная проверка Get-AuthenticodeSignature: ' + $after.Status + ' (' + $after.StatusMessage + ')') }
    if ($signed -and $after -and $after.Status -eq 'Valid') { $svcExe = $target }
  }
}

if (-not $svcExe) {
  $why = if ($script:signFail.Count -gt 0) { ($script:signFail -join ' | ') } else { 'подготовка не дошла до подписи' }
  $svcSkipReason = ('подписанный файл не получился (' + $why + '). Служба без действительной подписи политикой не считается изменяемой, поэтому проверка пропущена') -replace '"', "'" -replace '\r?\n', ' '
  if ($svcSkipReason.Length -gt 400) { $svcSkipReason = $svcSkipReason.Substring(0, 400) + '...' }
  Log ('подпись: тестовая служба будет пропущена. ' + $svcSkipReason)
} else {
  Log ('подпись: готов подписанный файл ' + $svcExe)
}

# 5. Живой стенд.
$stand = Join-Path $StandDir 'Sandbox.Live.exe'
$standArgs = @('--results', $ResultsDir, '--token', $Token, '--agent', (Join-Path $ReleaseDir 'Chistovik.Agent.exe'))
if ($svcExe) { $standArgs += @("--svc-exe", $svcExe) }
if ($svcSkipReason) { $standArgs += @("--svc-skip-reason", $svcSkipReason) }
if ($SkipIdle) { $standArgs += '--skip-idle' }
if ($Only) { $standArgs += @('--only', $Only) }
if (Test-Path -LiteralPath $InstallersDir) { $standArgs += @('--installers', $InstallersDir) }
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
    $checkArgs = @{ MsiPath = $msi.FullName; ReleaseDir = $ReleaseDir; ResultsDir = $ResultsDir }
    # Старый пакет лежит в отдельной папке (C:\MsiOld), с C:\Msi не смешивается.
    if ($OldMsiName) {
      $oldMsi = Join-Path $OldMsiDir $OldMsiName
      if (Test-Path -LiteralPath $oldMsi -PathType Leaf) {
        $checkArgs['OldMsiPath'] = $oldMsi
        Log ('старый MSI для проверки обновления: ' + $oldMsi)
      } else {
        Log ('старый MSI не найден: ' + $oldMsi + '; проверка обновления пропущена')
      }
    }
    & (Join-Path $PSScriptRoot 'installer-check.ps1') @checkArgs
  } else {
    Log 'MSI не передан (папка C:\Msi пуста): сценарий 8 пропущен'
  }
}

Set-Content -LiteralPath (Join-Path $ResultsDir 'DONE.txt') -Value ('{0:u}' -f (Get-Date))
Log 'готово; Sandbox можно закрыть (всё, что стенд создал, исчезнет вместе с ним)'
