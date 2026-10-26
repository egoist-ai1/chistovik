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

$sbUser = ([Security.Principal.WindowsIdentity]::GetCurrent().Name -split '\\')[-1]
$sbService = Get-Service -Name 'CExecSvc' -ErrorAction SilentlyContinue
if ($sbUser -ine 'WDAGUtilityAccount' -or -not $sbService -or -not (Test-Path -LiteralPath 'C:\ProgramData\ChistovikSandbox.marker')) {
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

# Выжимка причины отказа из подробного лога MSI (/l*v): сообщения «Продукт: ... -- ...» и строки перед «Return value 3»
# (в русской локали «Код возврата 3»), чтобы не открывать лог вручную. Лог Windows Installer пишется в UTF-16, Get-Content это распознаёт.
function Get-MsiFailureSummary([string]$logPath) {
  if (-not (Test-Path -LiteralPath $logPath)) { return 'лог не найден' }
  try {
    $lines = @(Get-Content -LiteralPath $logPath -ErrorAction Stop)
    $parts = New-Object System.Collections.ArrayList
    $msgs = @($lines | Where-Object { $_ -match '(Продукт|Product):\s.*\s--\s' } | ForEach-Object { ($_ -replace '^.*?(Продукт|Product):\s*', '').Trim() } | Select-Object -Unique)
    foreach ($m in ($msgs | Select-Object -First 5)) { [void]$parts.Add('сообщение: ' + $m) }
    $idx = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '(Return value|Код возврата)\s+3\b') { $i } })
    if ($idx.Count -gt 0) {
      $first = $idx[0]
      $from = [Math]::Max(0, $first - 6)
      $ctx = @($lines[$from..($first)] | ForEach-Object { ($_ -replace '^MSI \([sc]\) \([0-9A-F:]+\) \[[0-9:]+\]:\s*', '').Trim() } | Where-Object { $_ -and $_ -notmatch '^Note: 1: 22(05|28)' })
      [void]$parts.Add('перед отказом: ' + ($ctx -join ' / '))
    }
    $errs = @($lines | Where-Object { $_ -match 'CustomAction .* returned actual error code|Error \d{4}\.|Ошибка \d{4}\.' } | Select-Object -First 3 | ForEach-Object { $_.Trim() })
    foreach ($e in $errs) { [void]$parts.Add($e) }
    if ($parts.Count -eq 0) { return 'в логе нет понятных сообщений об отказе; смотрите ' + (Split-Path -Leaf $logPath) }
    $text = ($parts -join '; ')
    if ($text.Length -gt 1200) { $text = $text.Substring(0, 1200) + '...' }
    return $text
  } catch {
    return 'не удалось разобрать лог: ' + $_.Exception.Message
  }
}

try {
  if (Test-Path -LiteralPath $installDir) {
    Add-Result 'чистое состояние перед установкой' 'FAIL' "каталог $installDir уже существует: Sandbox не чистый"
  }

  # Установка.
  $code = Invoke-Msi @('/i', "`"$MsiPath`"", '/qn', '/l*v', "`"$msiLog`"")
  $installOk = ($code -eq 0 -or $code -eq 3010)
  $why = if ($installOk) { '' } else { '; причина: ' + (Get-MsiFailureSummary $msiLog) }
  Check 'установка MSI (тихая)' $installOk "msiexec код $code" "msiexec код $code, см. msi-install.log$why"

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

  # Обновление поверх (MajorUpgrade) выполняется ниже, после основного удаления (система снова чистая).
  $doUpgrade = [bool]($OldMsiPath -and (Test-Path -LiteralPath $OldMsiPath -PathType Leaf))
  if (-not $doUpgrade) {
    Add-Result 'обновление поверх (MajorUpgrade)' 'ПРОПУСК' 'нужны два MSI разных версий; в этом прогоне только один пакет (ключ -OldMsiPath не задан)'
  }

  # Удаление.
  # Если установка не удалась, удалять нечего: «нет файлов» и «нет записи» прошли бы вхолостую (так было в первом прогоне),
  # поэтому эти проверки тогда ПРОПУСК, а не PASS, и msiexec /x (код 1605) не запускается.
  if (-not $installOk) {
    $skipWhy = 'установка не удалась (код ' + $code + '), удалять нечего'
    Add-Result 'удаление MSI (тихое)' 'ПРОПУСК' $skipWhy
    Add-Result 'после удаления нет файлов App и Agent' 'ПРОПУСК' $skipWhy
    Add-Result 'после удаления нет записи в «Программы и компоненты»' 'ПРОПУСК' $skipWhy
  } else {
    $ulog = Join-Path $ResultsDir 'msi-uninstall.log'
    $ucode = Invoke-Msi @('/x', "`"$MsiPath`"", '/qn', '/l*v', "`"$ulog`"")
    $uwhy = if ($ucode -eq 0 -or $ucode -eq 3010) { '' } else { '; причина: ' + (Get-MsiFailureSummary $ulog) }
    Check 'удаление MSI (тихое)' ($ucode -eq 0 -or $ucode -eq 3010) "msiexec код $ucode" "msiexec код $ucode, см. msi-uninstall.log$uwhy"
    Check 'после удаления нет файлов App и Agent' (-not (Test-Path -LiteralPath $app) -and -not (Test-Path -LiteralPath $agent)) 'файлов нет' 'файлы остались (возможна отложенная замена до перезагрузки)'
    Check 'после удаления нет записи в «Программы и компоненты»' (@(Get-Arp).Count -eq 0) 'записи нет' 'запись осталась'
  }

  $data = Join-Path $env:ProgramData 'Chistovik'

  # Сценарий обновления поверх: старый MSI, маркер данных пользователя, новый MSI поверх, удаление нового.
  if ($doUpgrade) {
    $upMarker = Join-Path $data 'upgrade-marker.txt'
    $dataCreated = $false
    $newInstalled = $false
    try {
      $preArp = @(Get-Arp)
      if ((Test-Path -LiteralPath $installDir) -or $preArp.Count -gt 0) {
        Add-Result 'обновление поверх: чистое состояние' 'FAIL' 'после основного удаления остались файлы или запись ARP, сценарий обновления не выполнялся'
      } else {
        # Версия нового пакета из имени файла Chistovik-<версия>-x64.msi.
        $newVer = ''
        if ((Split-Path -Leaf $MsiPath) -match '^Chistovik-(.+)-x64\.msi$') { $newVer = $Matches[1] }
        # Сравнение версий без хвостовых «.0» (MSI может нормализовать 1.2.0 в 1.2.0 или 1.2).
        function Norm([string]$v) { return (($v.Trim()) -replace '(\.0+)+$', '') }

        # (а) старая версия.
        $olog = Join-Path $ResultsDir 'msi-old-install.log'
        $ocode = Invoke-Msi @('/i', "`"$OldMsiPath`"", '/qn', '/l*v', "`"$olog`"")
        $oldOk = ($ocode -eq 0 -or $ocode -eq 3010)
        $owhy = if ($oldOk) { '' } else { '; причина: ' + (Get-MsiFailureSummary $olog) }
        Check 'обновление поверх: установка старой версии' $oldOk "msiexec код $ocode" "msiexec код $ocode, см. msi-old-install.log$owhy"

        if ($oldOk) {
          $oldArp = @(Get-Arp)
          $oldVer = if ($oldArp.Count -ge 1) { [string]$oldArp[0].DisplayVersion } else { '' }
          $oldAgentVer = if (Test-Path -LiteralPath $agent) { [string](Get-Item -LiteralPath $agent).VersionInfo.FileVersion } else { '' }
          Check 'обновление поверх: версия старого пакета в ARP' ($oldVer -ne '') ("DisplayVersion $oldVer, файл агента $oldAgentVer") 'запись ARP старого пакета не найдена'

          # (б) данные пользователя.
          if (-not (Test-Path -LiteralPath $data)) { New-Item -ItemType Directory -Force -Path $data | Out-Null; $dataCreated = $true }
          Set-Content -LiteralPath $upMarker -Value 'upgrade-marker' -Encoding ASCII

          # (в) новый пакет поверх.
          $nlog = Join-Path $ResultsDir 'msi-upgrade.log'
          $ncode = Invoke-Msi @('/i', "`"$MsiPath`"", '/qn', '/l*v', "`"$nlog`"")
          $newOk = ($ncode -eq 0 -or $ncode -eq 3010)
          $newInstalled = $true
          $nwhy = if ($newOk) { '' } else { '; причина: ' + (Get-MsiFailureSummary $nlog) }
          Check 'обновление поверх: установка нового пакета' $newOk "msiexec код $ncode" "msiexec код $ncode, см. msi-upgrade.log$nwhy"

          if ($newOk) {
            # (г) ровно одна запись ARP, версия новая и не равна старой.
            $upArp = @(Get-Arp)
            Check 'обновление поверх: в ARP ровно одна запись' ($upArp.Count -eq 1) ('одна запись: ' + $upArp[0].DisplayName) ('записей: ' + $upArp.Count + ' (старая версия не заменена)')
            $curVer = if ($upArp.Count -ge 1) { [string]$upArp[0].DisplayVersion } else { '' }
            if ($newVer) {
              Check 'обновление поверх: версия в ARP равна версии нового пакета' ((Norm $curVer) -eq (Norm $newVer)) "DisplayVersion $curVer" "DisplayVersion $curVer, ждали $newVer (из имени файла)"
            } else {
              Add-Result 'обновление поверх: версия в ARP равна версии нового пакета' 'ПРОПУСК' 'версию нового пакета не удалось взять из имени файла'
            }
            Check 'обновление поверх: версия в ARP отличается от старой' ((Norm $curVer) -ne (Norm $oldVer)) "было $oldVer, стало $curVer" "версия не изменилась ($curVer): два пакета с одной версией MajorUpgrade не заменит"

            # (д) файлы и версия агента.
            Check 'обновление поверх: файлы App и Agent на месте' ((Test-Path -LiteralPath $app) -and (Test-Path -LiteralPath $agent)) $installDir "нет $app или $agent"
            $curAgentVer = if (Test-Path -LiteralPath $agent) { [string](Get-Item -LiteralPath $agent).VersionInfo.FileVersion } else { '' }
            Check 'обновление поверх: версия файла агента изменилась' ($curAgentVer -ne '' -and (Norm $curAgentVer) -ne (Norm $oldAgentVer)) "было $oldAgentVer, стало $curAgentVer" "версия файла агента не изменилась: было $oldAgentVer, стало $curAgentVer (агент не заменён или собран с одной версией)"

            # (е) ровно один ярлык.
            $upLnk = @(Get-ChildItem -Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs' -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Чистовик|Chistovik' })
            Check 'обновление поверх: ровно один ярлык в меню «Пуск»' ($upLnk.Count -eq 1) $upLnk[0].FullName ('ярлыков: ' + $upLnk.Count)

            # (ж) данные пользователя на месте.
            Check 'обновление поверх: маркер данных пользователя сохранился' (Test-Path -LiteralPath $upMarker) $upMarker 'маркер в ProgramData пропал'
          }
        }
      }
    } catch {
      Add-Result 'обновление поверх: необработанное исключение' 'FAIL' $_.Exception.Message
    }

    # (з) удаление и уборка: что бы ни случилось выше, оставляем систему чистой.
    $leftArp = @(Get-Arp)
    if ($newInstalled -or $leftArp.Count -gt 0) {
      $xlog = Join-Path $ResultsDir 'msi-upgrade-uninstall.log'
      $xcode = Invoke-Msi @('/x', "`"$MsiPath`"", '/qn', '/l*v', "`"$xlog`"")
      $xwhy = if ($xcode -eq 0 -or $xcode -eq 3010) { '' } else { '; причина: ' + (Get-MsiFailureSummary $xlog) }
      Check 'обновление поверх: удаление нового пакета' ($xcode -eq 0 -or $xcode -eq 3010) "msiexec код $xcode" "msiexec код $xcode, см. msi-upgrade-uninstall.log$xwhy"
      Check 'обновление поверх: после удаления нет файлов и записи ARP' (-not (Test-Path -LiteralPath $app) -and -not (Test-Path -LiteralPath $agent) -and @(Get-Arp).Count -eq 0) 'файлов и записи нет' 'файлы или запись ARP остались'
    }
    if (Test-Path -LiteralPath $upMarker) { Remove-Item -LiteralPath $upMarker -Force -ErrorAction SilentlyContinue }
    if ($dataCreated -and (Test-Path -LiteralPath $data) -and @(Get-ChildItem -LiteralPath $data -Force -ErrorAction SilentlyContinue).Count -eq 0) {
      Remove-Item -LiteralPath $data -Force -ErrorAction SilentlyContinue
    }
  }

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
