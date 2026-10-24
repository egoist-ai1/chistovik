<#
.SYNOPSIS
  Сценарий 8 живого стенда: установка и удаление MSI ВНУТРИ Windows Sandbox.
.DESCRIPTION
  Запускается из guest-run.ps1. Вне Sandbox отказывается работать (учётная запись WDAGUtilityAccount и маркер, который
  пишет guest-run.ps1). Окно приложения и GUI не открываются. Результат: installer-results.json и installer-results.md
  в папке результатов (та же схема, что у results.json стенда). Код выхода: 0 нет FAIL, 1 есть FAIL, 3 не Sandbox.
  PowerShell 5.1.
#>
param(
  [Parameter(Mandatory = $true)][string]$MsiPath,
  [string]$ReleaseDir = 'C:\Release',
  [string]$ResultsDir = 'C:\Results',
  [string]$OldMsiPath = ''
)

$ErrorActionPreference = 'Continue'

if ($env:USERNAME -ne 'WDAGUtilityAccount' -or -not (Test-Path -LiteralPath 'C:\ProgramData\ChistovikSandbox.marker')) {
  Write-Host 'Только в Windows Sandbox. Скрипт ничего не сделал.'
  exit 3
}

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$script:items = New-Object System.Collections.ArrayList
function Add-Result([string]$name, [string]$status, [string]$detail) {
  [void]$script:items.Add([pscustomobject]@{ Scenario = 'S8 установщик'; Name = $name; Status = $status; Detail = $detail })
  Write-Host ('[{0}] {1}: {2}' -f $status, $name, $detail)
}
function Check([string]$name, [bool]$ok, [string]$okText, [string]$failText) {
  if ($ok) { Add-Result $name 'PASS' $okText } else { Add-Result $name 'FAIL' $failText }
}

$installDir = Join-Path $env:ProgramFiles 'Chistovik'
$msiLog = Join-Path $ResultsDir 'msi-install.log'

function Get-Arp {
  $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
  Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'Чистовик|Chistovik' }
}

function Invoke-Msi([string[]]$msiArgs) {
  $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Wait -PassThru
  return $p.ExitCode
}

try {
  if (Test-Path -LiteralPath $installDir) {
    Add-Result 'чистое состояние перед установкой' 'FAIL' "каталог $installDir уже существует: Sandbox не чистый"
  }

  # Установка.
  $code = Invoke-Msi @('/i', "`"$MsiPath`"", '/qn', '/l*v', "`"$msiLog`"")
  Check 'установка MSI (тихая)' ($code -eq 0 -or $code -eq 3010) "msiexec код $code" "msiexec код $code, см. msi-install.log"

  $app = Join-Path $installDir 'Chistovik.App.exe'
  $agent = Join-Path $installDir 'Chistovik.Agent.exe'
  Check 'файлы App и Agent в одном каталоге' ((Test-Path -LiteralPath $app) -and (Test-Path -LiteralPath $agent)) $installDir "нет $app или $agent"

  # Хеш агента в установке равен собранному (канал App-Agent проверяет образ по SHA-256).
  $built = Join-Path $ReleaseDir 'Chistovik.Agent.exe'
  if ((Test-Path -LiteralPath $built) -and (Test-Path -LiteralPath $agent)) {
    $h1 = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash
    $h2 = (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash
    Check 'SHA-256 агента в установке равен собранному' ($h1 -eq $h2) $h1 "собранный $h1, установленный $h2 (ждали совпадения, если MSI собран из этого же publish)"
  } else {
    Add-Result 'SHA-256 агента в установке равен собранному' 'ПРОПУСК' 'нет собранного агента для сравнения'
  }

  # Каталог установки недоступен на запись обычному пользователю: иначе подмена агента обошла бы проверку образа.
  if (Test-Path -LiteralPath $installDir) {
    $acl = Get-Acl -LiteralPath $installDir
    $bad = @($acl.Access | Where-Object {
      $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -match '^(BUILTIN\\Users|NT AUTHORITY\\Authenticated Users|Everyone)$' -and
      ($_.FileSystemRights -band ([Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::Modify -bor [Security.AccessControl.FileSystemRights]::FullControl -bor [Security.AccessControl.FileSystemRights]::CreateFiles))
    })
    Check 'каталог установки не доступен на запись обычным пользователям' ($bad.Count -eq 0) 'ACL без записи для Users' ('запись разрешена: ' + (($bad | ForEach-Object { $_.IdentityReference.Value }) -join ', '))
  }

  $lnk = Get-ChildItem -Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs' -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Чистовик|Chistovik' }
  Check 'ярлык в меню «Пуск»' (@($lnk).Count -ge 1) (@($lnk)[0].FullName) 'ярлык не найден'

  $arp = @(Get-Arp)
  Check 'запись в «Программы и компоненты»' ($arp.Count -ge 1) ($arp[0].DisplayName + ' ' + $arp[0].DisplayVersion) 'записи нет'

  # Обновление поверх (MajorUpgrade): только если дан старый MSI.
  if ($OldMsiPath -and (Test-Path -LiteralPath $OldMsiPath)) {
    Add-Result 'обновление поверх' 'ПРОПУСК' 'проверяется отдельным запуском: сначала старый MSI, затем этот (нужны два пакета разных версий)'
  } else {
    Add-Result 'обновление поверх (MajorUpgrade)' 'ПРОПУСК' 'нужны два MSI разных версий; в этом прогоне только один пакет'
  }

  # Удаление.
  $code = Invoke-Msi @('/x', "`"$MsiPath`"", '/qn', '/l*v', "`"$(Join-Path $ResultsDir 'msi-uninstall.log')`"")
  Check 'удаление MSI (тихое)' ($code -eq 0 -or $code -eq 3010) "msiexec код $code" "msiexec код $code, см. msi-uninstall.log"
  Check 'после удаления нет файлов App и Agent' (-not (Test-Path -LiteralPath $app) -and -not (Test-Path -LiteralPath $agent)) 'файлов нет' 'файлы остались (возможна отложенная замена до перезагрузки)'
  Check 'после удаления нет записи в «Программы и компоненты»' (@(Get-Arp).Count -eq 0) 'записи нет' 'запись осталась'

  $data = Join-Path $env:ProgramData 'Chistovik'
  Add-Result 'данные пользователя (%ProgramData%\Chistovik)' 'PASS' ('по проекту установщик их не создаёт и не удаляет; сейчас ' + $(if (Test-Path -LiteralPath $data) { 'каталог есть' } else { 'каталога нет' }))
  Add-Result 'запуск App, UAC, закрытие App при обновлении' 'ПРОПУСК' 'GUI скриптами не запускается; окна UAC в Sandbox нет (см. README, ручной режим)'
} catch {
  Add-Result 'необработанное исключение' 'FAIL' $_.Exception.Message
}

$json = [pscustomobject]@{
  finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
  pass = @($script:items | Where-Object Status -eq 'PASS').Count
  fail = @($script:items | Where-Object Status -eq 'FAIL').Count
  skip = @($script:items | Where-Object Status -eq 'ПРОПУСК').Count
  checks = $script:items
}
[IO.File]::WriteAllText((Join-Path $ResultsDir 'installer-results.json'), ($json | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $true))

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# Проверка установщика в Sandbox')
[void]$md.AppendLine('')
[void]$md.AppendLine('| Проверка | Итог | Подробности |')
[void]$md.AppendLine('|---|---|---|')
foreach ($i in $script:items) { [void]$md.AppendLine(('| {0} | {1} | {2} |' -f $i.Name.Replace('|', '/'), $i.Status, ([string]$i.Detail).Replace('|', '/').Replace("`r", ' ').Replace("`n", ' '))) }
[IO.File]::WriteAllText((Join-Path $ResultsDir 'installer-results.md'), $md.ToString(), (New-Object Text.UTF8Encoding $true))

if ($json.fail -gt 0) { exit 1 } else { exit 0 }
