# Egoist Clean: инвентарь установленных программ. ТОЛЬКО ЧТЕНИЕ: реестр и список пакетов, ничего не меняется и не отправляется.
# Запуск: powershell -ExecutionPolicy Bypass -File inventory.ps1 [-Out inventory.json]
param([string]$Out = (Join-Path $PSScriptRoot 'inventory.json'))

$ErrorActionPreference = 'SilentlyContinue'
$roots = @(
    @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';             Arch = 'x64'; Scope = 'machine' },
    @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; Arch = 'x86'; Scope = 'machine' },
    @{ Path = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';             Arch = 'user'; Scope = 'user' }
)

$items = New-Object System.Collections.Generic.List[object]
$seen = @{}

foreach ($r in $roots) {
    foreach ($k in Get-ChildItem -LiteralPath $r.Path) {
        $p = Get-ItemProperty -LiteralPath $k.PSPath
        if (-not $p.DisplayName) { continue }
        if ($p.ParentKeyName) { continue }                       # обновления и патчи
        if ($p.DisplayName -match '^(Security )?Update for |^KB\d{6,}') { continue }
        $id = "arp:$($r.Scope):$($r.Arch):$($k.PSChildName)"
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        $date = $null
        if ($p.InstallDate -match '^\d{8}$') { $date = '{0}-{1}-{2}' -f $p.InstallDate.Substring(0,4), $p.InstallDate.Substring(4,2), $p.InstallDate.Substring(6,2) }
        $items.Add([ordered]@{
            id = $id
            name = [string]$p.DisplayName
            publisher = [string]$p.Publisher
            version = [string]$p.DisplayVersion
            installDate = $date
            sizeMB = if ($p.EstimatedSize) { [math]::Round($p.EstimatedSize / 1024, 1) } else { $null }
            source = if ($p.WindowsInstaller -eq 1) { 'msi' } else { 'exe' }
            arch = $r.Arch
            scope = $r.Scope
            installLocation = [string]$p.InstallLocation
            uninstallString = [string]$p.UninstallString
            quietUninstallString = [string]$p.QuietUninstallString
            systemComponent = ($p.SystemComponent -eq 1)
            nonRemovable = (($p.NoRemove -eq 1) -or ($p.SystemComponent -eq 1))
            key = $k.PSChildName
        })
    }
}

foreach ($a in Get-AppxPackage) {
    if ($a.IsFramework -or $a.IsResourcePackage) { continue }
    $id = "appx:$($a.PackageFullName)"
    if ($seen.ContainsKey($id)) { continue }
    $seen[$id] = $true
    $items.Add([ordered]@{
        id = $id
        name = [string]$a.Name
        publisher = [string]($a.Publisher -replace '^CN=([^,]+).*$', '$1')
        version = [string]$a.Version
        installDate = $null
        sizeMB = $null
        source = 'msix'
        arch = [string]$a.Architecture
        scope = 'user'
        installLocation = [string]$a.InstallLocation
        uninstallString = ''
        quietUninstallString = ''
        systemComponent = ($a.SignatureKind -eq 'System')
        nonRemovable = ($a.NonRemovable -eq $true)
        key = [string]$a.PackageFullName
    })
}

$os = Get-CimInstance Win32_OperatingSystem
$doc = [ordered]@{
    generated = (Get-Date).ToString('s')
    schema = 1
    os = "$($os.Caption) $($os.Version) $($os.OSArchitecture)"
    count = $items.Count
    items = $items
}
$json = $doc | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Готово: $($items.Count) записей -> $Out"
