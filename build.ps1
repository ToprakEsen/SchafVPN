# Tek dosyalik, kurulum gerektirmeyen SchafVPN.exe uretir -> dist\SchafVPN.exe
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path "core\xray.exe") -or -not (Test-Path "core\wintun.dll")) { & "$PSScriptRoot\scripts\get-xray.ps1" }
if (-not (Test-Path "profile.txt")) {
    Write-Host "Not: profil gomulmuyor; uygulama ilk acilista VLESS linkini soracak."
}

dotnet publish SchafVPN.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { throw "Derleme basarisiz." }

Write-Host ""
Write-Host "Hazir: $PSScriptRoot\dist\SchafVPN.exe"
