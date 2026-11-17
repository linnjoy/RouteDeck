# Publishes RouteDeck.exe (GUI) and RouteDeck.Service.exe side by side, adds the one-click
# installer scripts, and zips everything into artifacts\RouteDeck-<version>-win-x64.zip.
# The service only grants write access to a GUI living in its own directory, so keep them together.
param([string]$Configuration = "Release", [string]$Output = "$PSScriptRoot\..\artifacts\package\RouteDeck")

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=none", "-o", $Output)

dotnet publish "$root\src\RouteDeck.App\RouteDeck.App.csproj" @common
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish "$root\src\RouteDeck.Service\RouteDeck.Service.csproj" @common
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# One-click installer: Install-RouteDeck.cmd -> install.ps1 (self-elevating); uninstall.ps1 is copied into Program Files.
Copy-Item "$root\installer\oneclick\Install-RouteDeck.cmd", "$root\installer\oneclick\uninstall.ps1" $Output -Force
$install = [IO.File]::ReadAllText("$root\installer\oneclick\install.ps1").Replace("__VERSION__", $version)
[IO.File]::WriteAllText((Join-Path $Output "install.ps1"), $install, (New-Object Text.UTF8Encoding $true))

$zip = Join-Path (Split-Path $Output -Parent | Split-Path -Parent) "RouteDeck-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$Output\*" -DestinationPath $zip

Get-ChildItem $Output | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
Write-Host "安装包：$zip ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"
