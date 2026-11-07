# sing-box Windows cekirdegini indirir, SHA256 dogrular ve core\sing-box.exe olarak koyar.
param(
    [string]$Version = "1.14.4",
    [string]$Sha256  = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df"
)
$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $PSScriptRoot
$coreDir = Join-Path $root "core"
$name    = "sing-box-$Version-windows-amd64"
$url     = "https://github.com/SagerNet/sing-box/releases/download/v$Version/$name.zip"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("singbox-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp "$name.zip"
    Write-Host "Indiriliyor: $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $Sha256.ToLower()) { throw "SHA256 uyusmuyor! Beklenen $Sha256, gelen $hash" }

    Expand-Archive -Path $zip -DestinationPath $tmp
    New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
    Copy-Item (Join-Path $tmp "$name\sing-box.exe") (Join-Path $coreDir "sing-box.exe") -Force
    Write-Host "Tamam: $coreDir\sing-box.exe"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
