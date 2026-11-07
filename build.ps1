# Tek dosyalik, kurulum gerektirmeyen SchafVPN.exe uretir -> dist\SchafVPN.exe
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path "core\sing-box.exe")) { & "$PSScriptRoot\scripts\get-singbox.ps1" }
if (-not (Test-Path "core\xray.exe")) { & "$PSScriptRoot\scripts\get-xray.ps1" }
if (-not (Test-Path "profile.txt")) {
    throw "profile.txt yok. profile.example.txt dosyasini profile.txt olarak kopyalayip vless:// linkini yapistir."
}

dotnet publish SchafVPN.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { throw "Derleme basarisiz." }

Write-Host ""
Write-Host "Hazir: $PSScriptRoot\dist\SchafVPN.exe"
