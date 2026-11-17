# RouteDeck 一键安装：双击同目录的 Install-RouteDeck.cmd 运行。
# 复制到 Program Files，注册后台服务，创建快捷方式，并登记到“设置 → 应用”。不会修改任何路由。
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$version = "__VERSION__"
$source = $PSScriptRoot
$dest = Join-Path $env:ProgramFiles "RouteDeck"
$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\RouteDeck"
$uninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RouteDeck"

function Step($text) { Write-Host "  · $text" }

try {
    Write-Host ""
    Write-Host "正在安装 RouteDeck $version …" -ForegroundColor Cyan

    foreach ($file in "RouteDeck.exe", "RouteDeck.Service.exe") {
        if (-not (Test-Path (Join-Path $source $file))) { throw "安装包不完整：缺少 $file" }
    }

    # 升级时先停掉旧版本（服务停止不会改动路由）
    if (Get-Service RouteDeck -ErrorAction SilentlyContinue) {
        Step "停止旧版本服务"
        Stop-Service RouteDeck -Force -ErrorAction SilentlyContinue
    }
    Get-Process RouteDeck -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest\*" } | Stop-Process -Force
    Get-Process RouteDeck.Service -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Step "复制文件到 $dest"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $source "RouteDeck.exe"), (Join-Path $source "RouteDeck.Service.exe"), (Join-Path $source "uninstall.ps1") $dest -Force
    $settings = Join-Path $source "appsettings.json"
    if ((Test-Path $settings) -and -not (Test-Path (Join-Path $dest "appsettings.json"))) { Copy-Item $settings $dest }

    Step "注册并启动后台服务"
    & (Join-Path $dest "RouteDeck.Service.exe") --install | Out-Null
    $service = Get-Service RouteDeck -ErrorAction SilentlyContinue
    if (-not $service) { throw "服务注册失败" }
    try { $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(20)) }
    catch { throw "服务没有成功启动，请查看 C:\ProgramData\RouteDeck\logs\" }

    Step "创建开始菜单和桌面快捷方式"
    New-Item -ItemType Directory -Force $startMenu | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $links = @(
        @{ Path = Join-Path $startMenu "RouteDeck.lnk"; Target = Join-Path $dest "RouteDeck.exe"; Args = "" },
        @{ Path = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "RouteDeck.lnk"; Target = Join-Path $dest "RouteDeck.exe"; Args = "" },
        @{ Path = Join-Path $startMenu "卸载 RouteDeck.lnk"; Target = "powershell.exe"; Args = "-NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`"" }
    )
    foreach ($l in $links) {
        $link = $shell.CreateShortcut($l.Path)
        $link.TargetPath = $l.Target
        $link.Arguments = $l.Args
        $link.WorkingDirectory = $dest
        $link.IconLocation = (Join-Path $dest "RouteDeck.exe") + ",0"
        $link.Save()
    }

    Step "登记到“设置 → 应用”"
    New-Item -Force $uninstallKey | Out-Null
    $sizeKb = [int]((Get-ChildItem $dest | Measure-Object Length -Sum).Sum / 1KB)
    $values = @{
        DisplayName = "RouteDeck"; DisplayVersion = $version; Publisher = "linnjoy"
        DisplayIcon = Join-Path $dest "RouteDeck.exe"; InstallLocation = $dest
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
    }
    foreach ($k in $values.Keys) { Set-ItemProperty $uninstallKey $k $values[$k] }
    Set-ItemProperty $uninstallKey EstimatedSize $sizeKb -Type DWord
    Set-ItemProperty $uninstallKey NoModify 1 -Type DWord
    Set-ItemProperty $uninstallKey NoRepair 1 -Type DWord

    Write-Host ""
    Write-Host "安装完成。正在打开 RouteDeck …" -ForegroundColor Green
    # 通过资源管理器启动，界面以普通用户权限运行（不需要也不应该以管理员运行）
    Start-Process explorer.exe (Join-Path $dest "RouteDeck.exe")
    Start-Sleep -Seconds 3
}
catch {
    Write-Host ""
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "没有修改任何路由。" -ForegroundColor Yellow
    Read-Host "按回车键关闭"
    exit 1
}
