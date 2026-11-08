#Requires -Version 5.1
<#
  Gỡ Manager Windows. Mặc định hoàn tác mọi thư mục data đã chuyển (đưa về ổ C) trước khi gỡ.
  -KeepRelocations : giữ nguyên các junction (data vẫn ở ổ khác, app như Zalo vẫn chạy bình thường).
  -RemoveData      : xoá cả thống kê/cấu hình trong %LOCALAPPDATA%\ManagerWindows và %ProgramData%\ManagerWindows.
#>
param([switch]$KeepRelocations, [switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ManagerWindowsService'
$Dest = Join-Path $env:ProgramFiles 'ManagerWindows'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($KeepRelocations) { $argList += ' -KeepRelocations' }
    if ($RemoveData) { $argList += ' -RemoveData' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

Get-Process -Name 'ManagerWindows.App' -ErrorAction SilentlyContinue | Stop-Process -Force
$app = Join-Path $Dest 'ManagerWindows.App.exe'
if (-not $KeepRelocations -and (Test-Path $app)) {
    Write-Host 'Đang đưa data đã chuyển về lại ổ C...'
    # Lưu ý: chỉ hoàn tác cho tài khoản đang chạy script. Tài khoản khác hãy mở app > Di chuyển data > Hoàn tác trước khi gỡ.
    Start-Process -FilePath $app -ArgumentList '--rollback-all' -Wait
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
    sc.exe delete $ServiceName | Out-Null
}

# Gỡ chính sách ngủ đông tab trình duyệt do app đặt; chính sách quản trị viên tự đặt thì giữ nguyên.
# Danh sách mục do app đặt: browser-policies.json (dùng chung với bản portable), bản v0.11.0 còn nằm trong service.json.
# Khoá phải khớp BrowserCatalog trong ManagerWindows.Core/Monitoring/BrowserTabSleep.cs.
$policyKeys = @{ Chrome = 'HKLM:\SOFTWARE\Policies\Google\Chrome'; CocCoc = 'HKLM:\SOFTWARE\Policies\CocCoc\Browser'; Brave = 'HKLM:\SOFTWARE\Policies\BraveSoftware\Brave' }
$removedPolicies = $false
foreach ($src in @(@{ File = 'browser-policies.json'; Prop = $null }, @{ File = 'service.json'; Prop = 'BrowserPolicyOwned' })) {
    $path = Join-Path $env:ProgramData (Join-Path 'ManagerWindows' $src.File)
    if (-not (Test-Path $path)) { continue }
    try {
        $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $owned = if ($src.Prop) { $json.($src.Prop) } else { $json }
        if (-not $owned) { continue }
        foreach ($b in $owned.PSObject.Properties) {
            $key = $policyKeys[$b.Name]
            if (-not $key) { continue }
            foreach ($item in $b.Value) {
                if ($item -eq 'TabDiscardingExceptions') { Remove-Item (Join-Path $key $item) -Recurse -Force -ErrorAction SilentlyContinue }
                else { Remove-ItemProperty -Path $key -Name $item -ErrorAction SilentlyContinue }
                $removedPolicies = $true
            }
        }
        if (-not $src.Prop) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
    } catch { Write-Host "Không đọc được $path, bỏ qua bước gỡ chính sách trình duyệt." }
}
if ($removedPolicies) { Write-Host 'Đã gỡ chính sách ngủ đông tab trình duyệt.' }

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ManagerWindows' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Manager Windows.lnk') -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
# Script này nằm trong $Dest\lib: đổi thư mục làm việc ra ngoài để xoá được cả thư mục cài đặt.
Set-Location $env:SystemRoot
Remove-Item $Dest -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'ManagerWindows') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:ProgramData 'ManagerWindows') -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Đã gỡ Manager Windows.'
Start-Sleep -Seconds 3
