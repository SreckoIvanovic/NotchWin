$ErrorActionPreference = 'SilentlyContinue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\NotchWin'

taskkill.exe /IM NotchWin.exe | Out-Null
for ($i = 0; $i -lt 30 -and (Get-Process NotchWin); $i++) { Start-Sleep -Milliseconds 100 }
Get-Process NotchWin | Stop-Process -Force
Start-Sleep -Milliseconds 500

schtasks.exe /Delete /TN "NotchWin" /F | Out-Null
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NotchWin'

Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NotchWin.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchWin' -Recurse -Force
Remove-Item (Join-Path $env:APPDATA 'NotchWin') -Recurse -Force
Remove-Item (Join-Path $env:TEMP 'NotchWin') -Recurse -Force

Start-Process cmd.exe -WindowStyle Hidden -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`""

Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show('NotchWin has been uninstalled.', 'NotchWin') | Out-Null
