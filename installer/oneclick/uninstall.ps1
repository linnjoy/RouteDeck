# RouteDeck 卸载。可以从开始菜单“卸载 RouteDeck”或“设置 → 应用”运行。
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$dest = Join-Path $env:ProgramFiles "RouteDeck"

# 脚本本身在安装目录里，先复制到临时目录再运行，才能删掉整个目录
if ($PSScriptRoot -eq $dest) {
    $temp = Join-Path $env:TEMP "routedeck-uninstall.ps1"
    Copy-Item $PSCommandPath $temp -Force
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$temp`""
    exit
}

Write-Host ""
Write-Host "卸载 RouteDeck" -ForegroundColor Cyan
Write-Host "卸载不会删除 RouteDeck 添加过的路由：临时路由重启后自动消失，持久路由需要手动删除。"
Write-Host "如果还有需要撤销的修改，请先在 RouteDeck 的“操作历史”中撤销，再继续。"
$answer = Read-Host "确定卸载吗？(Y/N)"
if ($answer -notmatch '^[Yy]') { exit }

try {
    Get-Process RouteDeck -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest\*" } | Stop-Process -Force
    if (Test-Path (Join-Path $dest "RouteDeck.Service.exe")) {
        & (Join-Path $dest "RouteDeck.Service.exe") --uninstall | Out-Null
    }
    Get-Process RouteDeck.Service -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\RouteDeck") -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "RouteDeck.lnk") -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RouteDeck" -ErrorAction SilentlyContinue
    Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" RouteDeck -ErrorAction SilentlyContinue

    $data = Join-Path $env:ProgramData "RouteDeck"
    if ((Test-Path $data) -and (Read-Host "同时删除操作历史和日志（$data）吗？(Y/N)") -match '^[Yy]') {
        Remove-Item -Recurse -Force $data
    }
    Write-Host "RouteDeck 已卸载。" -ForegroundColor Green
}
catch {
    Write-Host "卸载时出错：$($_.Exception.Message)" -ForegroundColor Red
}
Read-Host "按回车键关闭"
