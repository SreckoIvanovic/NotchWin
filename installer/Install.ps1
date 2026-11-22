
$ErrorActionPreference = 'Stop'
$root    = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'
$dest    = Join-Path $env:LOCALAPPDATA 'Programs\NotchWin'
$exe     = Join-Path $dest 'NotchWin.exe'
$startMenuLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NotchWin.lnk'
$uninstKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchWin'

Write-Host "Building NotchWin..." -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'NotchWin.csproj') -c Release -r win-x64 -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Write-Host "Closing the running notch..." -ForegroundColor Cyan
if (Get-Process NotchWin -ErrorAction SilentlyContinue) {
    taskkill.exe /IM NotchWin.exe 2>$null | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Process NotchWin -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
    Get-Process NotchWin -ErrorAction SilentlyContinue | Stop-Process -Force
}
Start-Sleep -Milliseconds 500

Write-Host "Installing to $dest" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $publish 'NotchWin.exe') $dest -Force
Copy-Item (Join-Path $root 'Assets\NotchWin.ico') $dest -Force
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') $dest -Force

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenuLnk)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.IconLocation = "$exe,0"
$lnk.Description = 'NotchWin - a notch for Windows'
$lnk.Save()

$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$sizeKb  = [int]((Get-ChildItem $dest -File | Measure-Object Length -Sum).Sum / 1KB)
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dest\Uninstall.ps1`""
New-Item -Path $uninstKey -Force | Out-Null
$props = @{
    DisplayName          = 'NotchWin'
    DisplayVersion       = $version
    Publisher            = 'NotchWin'
    DisplayIcon          = "$exe,0"
    InstallLocation      = $dest
    UninstallString      = $uninstallCmd
    QuietUninstallString = $uninstallCmd
    InstallDate          = (Get-Date -Format 'yyyyMMdd')
}
foreach ($k in $props.Keys) { Set-ItemProperty -Path $uninstKey -Name $k -Value $props[$k] }
Set-ItemProperty -Path $uninstKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $uninstKey -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $uninstKey -Name EstimatedSize -Value $sizeKb -Type DWord

Start-Process explorer.exe -ArgumentList "`"$exe`""

Write-Host ""
Write-Host "NotchWin $version installed." -ForegroundColor Green
Write-Host "Find it in the Start menu; uninstall from Settings > Apps > Installed apps."
