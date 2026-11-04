#Requires -Version 5.1
<#
  Cài Manager Windows: copy vào Program Files, đăng ký Windows Service, tạo shortcut, mở app.
  Chạy bằng install.cmd (tự xin quyền admin). Script này nằm trong lib\ của gói:
    <gói>\ManagerWindows.App.exe, install.cmd, uninstall.cmd, Huong-dan-su-dung.txt
    <gói>\lib\   dịch vụ nền, thư viện, script
  Thư mục cài đặt giữ đúng cấu trúc đó.
#>
$ErrorActionPreference = 'Stop'
$ServiceName = 'ManagerWindowsService'
$Src = Split-Path -Parent $PSScriptRoot
$Dest = Join-Path $env:ProgramFiles 'ManagerWindows'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

if (-not (Test-Path (Join-Path $Src 'lib\ManagerWindows.Service.exe'))) {
    throw 'Thiếu thư mục lib cạnh install.cmd. Hãy giải nén cả gói rồi chạy lại install.cmd.'
}

Write-Host 'Đang dừng phiên bản cũ (nếu có)...'
Get-Process -Name 'ManagerWindows.App' -ErrorAction SilentlyContinue | Stop-Process -Force
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }

Write-Host "Đang copy vào $Dest ..."
# Xoá bản cũ (trước 0.8.0 hàng trăm file dll nằm ngay trong thư mục cài). Dữ liệu người dùng không nằm ở đây.
if (Test-Path $Dest) {
    Start-Sleep -Seconds 1 # chờ tiến trình vừa dừng nhả file
    Get-ChildItem -Path $Dest -Force | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $Dest 'lib') -Force | Out-Null
foreach ($f in 'ManagerWindows.App.exe', 'uninstall.cmd', 'Huong-dan-su-dung.txt') {
    Copy-Item -Path (Join-Path $Src $f) -Destination $Dest -Force
}
Get-ChildItem -Path (Join-Path $Src 'lib') -Exclude 'install.ps1' | Copy-Item -Destination (Join-Path $Dest 'lib') -Recurse -Force

$exe = Join-Path $Dest 'lib\ManagerWindows.Service.exe'
if ($svc) {
    # Đã cài bản cũ: trỏ dịch vụ sang vị trí mới trong lib\.
    $r = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" | Invoke-CimMethod -MethodName Change -Arguments @{ PathName = "`"$exe`"" }
    if ($r.ReturnValue -ne 0) { throw "Không cập nhật được đường dẫn dịch vụ (mã $($r.ReturnValue))." }
}
else {
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
