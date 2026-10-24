<#
.SYNOPSIS
  Релизная сборка Чистовика: dotnet publish (self-contained) для x64 и ARM64, MSI на архитектуру,
  SHA256SUMS.txt. Результат: artifacts/release/<версия>/. Ничего не устанавливает и не запускает.
.PARAMETER Archs
  Архитектуры: x64, arm64 (по умолчанию обе).
.PARAMETER Dotnet
  Путь к dotnet. По умолчанию: .dotnet\dotnet.exe проекта, если есть, иначе dotnet из PATH.
.PARAMETER SkipMsi
  Только publish и контрольные суммы (если WiX недоступен).
.PARAMETER SignCommand
  Место для подписи (SignPath или другое). Скриптблок вида { param($Path) ... }, вызывается для каждого
  .exe/.dll (до упаковки в MSI) и для каждого .msi. Пока не задан - артефакты не подписываются.
#>
[CmdletBinding()]
param(
  [ValidateSet('x64', 'arm64')][string[]]$Archs = @('x64', 'arm64'),
  [string]$Dotnet,
  [switch]$SkipMsi,
  [scriptblock]$SignCommand
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $root

# dotnet: локальный в репозитории, если есть
$localDotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not $Dotnet) { $Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' } }
if (Test-Path -LiteralPath $localDotnet) { $env:DOTNET_ROOT = Join-Path $root '.dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
  & $Exe @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Команда завершилась с кодом ${LASTEXITCODE}: $Exe $($Arguments -join ' ')" }
}

# Версия - единственный источник Directory.Build.props
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding UTF8
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'В Directory.Build.props нет <Version>.' }
# MSI принимает только число.число.число[.число]; суффикс предрелиза отбрасываем
$msiVersion = ($version -replace '[-+].*$', '')
if ($msiVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Версия '$version' не подходит для MSI." }

$out = Join-Path $root "artifacts\release\$version"
$work = Join-Path $root "artifacts\release-work\$version"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out, $work | Out-Null

$rid = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }

# Лицензия для мастера: GPL-3 в RTF
$rtf = Join-Path $work 'License.rtf'
$text = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw -Encoding UTF8
$esc = $text.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}') -replace "`r?`n", "\par`n"
Set-Content -LiteralPath $rtf -Encoding ASCII -Value ('{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}\f0\fs18 ' + $esc + '}')

foreach ($arch in $Archs) {
  Write-Host "== $arch =="
  $pub = Join-Path $work "publish-$arch"
  Invoke-Native $Dotnet @('publish', 'src\Chistovik.App\Chistovik.App.csproj', '-c', 'Release',
    '-r', $rid[$arch], '--self-contained', 'true', '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $pub)

  foreach ($f in 'Chistovik.App.exe', 'Chistovik.Agent.exe', 'Chistovik.Agent.dll', 'Chistovik.Core.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $pub $f))) { throw "В publish-папке нет $f ($arch)." }
  }

  if ($SignCommand) {
    Get-ChildItem -LiteralPath $pub -Recurse -Include 'Chistovik.*.exe', 'Chistovik.*.dll' | ForEach-Object { & $SignCommand $_.FullName }
  }

  # Переносимая папка в ZIP не публикуется: основная форма поставки - MSI (решение владельца).
  if (-not $SkipMsi) {
    $msi = Join-Path $out "Chistovik-$version-$arch.msi"
    Invoke-Native $Dotnet @('tool', 'restore')
    $have = (& $Dotnet wix extension list) -join "`n"
    foreach ($e in 'WixToolset.UI.wixext', 'WixToolset.Util.wixext') {
      if ($have -notmatch [regex]::Escape($e)) { Invoke-Native $Dotnet @('wix', 'extension', 'add', "$e/5.0.2") }
    }
    Invoke-Native $Dotnet @('wix', 'build', '-arch', $arch, '-culture', 'ru-RU',
      '-ext', 'WixToolset.UI.wixext', '-ext', 'WixToolset.Util.wixext',
      '-d', "Version=$msiVersion", '-d', "PublishDir=$pub", '-d', "LicenseRtf=$rtf",
      '-d', "AssetsDir=$(Join-Path $root 'installer\assets')",
      '-pdbtype', 'none', '-o', $msi, 'installer\Package.wxs')
    if ($SignCommand) { & $SignCommand $msi }
  }
}

# Контрольные суммы всех артефактов релиза
$sums = Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
  '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
if ($sums) { Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding ASCII }
Write-Host "Готово: $out"
Get-ChildItem -LiteralPath $out | Format-Table Name, Length -AutoSize | Out-String | Write-Host
