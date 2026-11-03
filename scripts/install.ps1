#Requires -Version 5.1
<#
  Cài Manager Windows: copy vào Program Files, đăng ký Windows Service, tạo shortcut, mở app.
  Chạy bằng install.cmd (tự xin quyền admin).
#>
$ErrorActionPreference = 'Stop'
$ServiceName = 'ManagerWindowsService'
$Src = $PSScriptRoot
$Dest = Join-Path $env:ProgramFiles 'ManagerWindows'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

Write-Host 'Đang dừng phiên bản cũ (nếu có)...'
Get-Process -Name 'ManagerWindows.App' -ErrorAction SilentlyContinue | Stop-Process -Force
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }

Write-Host "Đang copy vào $Dest ..."
New-Item -ItemType Directory -Path $Dest -Force | Out-Null
Get-ChildItem -Path $Src -Exclude 'install.*', 'uninstall.cmd' | Copy-Item -Destination $Dest -Recurse -Force
Copy-Item -Path (Join-Path $Src 'uninstall.ps1') -Destination $Dest -Force

$exe = Join-Path $Dest 'ManagerWindows.Service.exe'
if (-not $svc) {
    Write-Host 'Đang đăng ký dịch vụ nền...'
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Manager Windows Service' `
        -Description 'Dọn rác hệ thống theo lịch, đếm số lần mở ứng dụng, hỗ trợ ưu tiên tài nguyên cho Manager Windows.' `
        -StartupType Automatic | Out-Null
}
# Tự khởi động lại service nếu bị lỗi.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
Start-Service -Name $ServiceName

Write-Host 'Đang tạo shortcut...'
$lnkPath = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Manager Windows.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = Join-Path $Dest 'ManagerWindows.App.exe'
$lnk.WorkingDirectory = $Dest
$lnk.Save()

# Mở app với quyền người dùng thường (qua explorer) chứ không phải quyền admin của trình cài.
Start-Process explorer.exe -ArgumentList "`"$(Join-Path $Dest 'ManagerWindows.App.exe')`""
Write-Host 'Cài đặt xong. Biểu tượng Manager Windows nằm ở khay hệ thống (góc phải thanh taskbar).'
Start-Sleep -Seconds 3
