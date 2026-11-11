# Xray-core Windows paketini indirir, SHA256 dogrular; core\xray.exe ve (TUN icin) core\wintun.dll cikarir.
param(
    [string]$Version = "26.10.31",
    [string]$Sha256  = "2e04a9995a6f2ad1b28c0bb25c708ecb2695e43a1c9c7889c4b6ac2b48cf7a6a"
)
$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $PSScriptRoot
$coreDir = Join-Path $root "core"
$url     = "https://github.com/XTLS/Xray-core/releases/download/v$Version/Xray-windows-64.zip"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("xray-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp "xray.zip"
    Write-Host "Indiriliyor: $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $Sha256.ToLower()) { throw "SHA256 uyusmuyor! Beklenen $Sha256, gelen $hash" }

    Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp "x")
    New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
    Copy-Item (Join-Path $tmp "x\xray.exe") (Join-Path $coreDir "xray.exe") -Force
    Copy-Item (Join-Path $tmp "x\wintun.dll") (Join-Path $coreDir "wintun.dll") -Force
    Write-Host "Tamam: $coreDir\xray.exe, wintun.dll"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
