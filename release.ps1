# Yeni surum yayinlar: profilsiz exe derler, dogrular, GitHub Release olusturur.
# Kullanim:  .\release.ps1 -Version 1.2.0 [-Notes "Degisiklikler..."]
# Yayinlanan her surum, uygulamayi kullanan herkese otomatik guncelleme olarak gider.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
[Environment]::CurrentDirectory = $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Surum X.Y.Z biciminde olmali (or. 1.2.0)." }

# Harici komut: stderr'e yazmasi hata sayilmasin (Windows PowerShell 5.1), sadece cikis kodu onemli
function Invoke-Native([scriptblock]$cmd) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { & $cmd 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $prev }
}
$tag = "v$Version"

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "GitHub CLI (gh) bulunamadi." }

# ---- on kontroller
if (git status --porcelain) { throw "Commit edilmemis degisiklikler var. Once commit et (yayin, depodaki kodla ayni olmali)." }
Invoke-Native { & $gh release view $tag } | Out-Null
if ($LASTEXITCODE -eq 0) { throw "$tag zaten yayinlanmis." }
$csproj = [IO.File]::ReadAllText("$PSScriptRoot\SchafVPN.csproj", [Text.Encoding]::UTF8)
$current = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
if ([Version]$Version -le [Version]$current) { throw "Yeni surum ($Version) mevcut surumden ($current) buyuk olmali." }
if (-not (Test-Path core\sing-box.exe)) { & "$PSScriptRoot\scripts\get-singbox.ps1" }
if (-not (Test-Path core\xray.exe))     { & "$PSScriptRoot\scripts\get-xray.ps1" }

# ---- profilsiz derleme
Remove-Item -Recurse -Force bin, obj, dist-release -ErrorAction SilentlyContinue
dotnet publish SchafVPN.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:EmbedProfile=false -p:Version=$Version -o dist-release
if ($LASTEXITCODE -ne 0) { throw "Derleme basarisiz." }

# ---- sizinti kontrolu: derlenen kodda profil OLMAMALI
$dll = "obj\Release\net10.0-windows\win-x64\SchafVPN.dll"
if (-not (Test-Path $dll)) { throw "Kontrol icin $dll bulunamadi." }
$bytes = [IO.File]::ReadAllBytes((Resolve-Path $dll))
$text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)   # bayt bayt arama
if ($text.Contains("profile.txt")) { throw "GUVENLIK: derlemede profile.txt kaynagi var! Yayin durduruldu." }
if (Test-Path profile.txt) {
    $link = (Get-Content profile.txt | Where-Object { $_.Trim() -like "vless://*" } | Select-Object -First 1)
    if ($link) {
        $u = [Uri]$link.Trim()
        foreach ($s in @([Uri]::UnescapeDataString($u.UserInfo), $u.Host)) {
            if ($s.Length -ge 4 -and $text.Contains($s)) { throw "GUVENLIK: derlemede kisisel profil bilgisi var! Yayin durduruldu." }
        }
    }
}
$exe = "dist-release\SchafVPN.exe"
$v = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $v.StartsWith($Version)) { throw "Exe surumu ($v) beklenen ($Version) degil." }
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$PSScriptRoot\dist-release\SchafVPN.exe.sha256", "$sha  SchafVPN.exe`n")
Write-Host "Dogrulandi: profil yok, surum $Version, SHA256 $sha"

# ---- surum numarasini kaydet ve yayinla
$csproj = $csproj -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText("$PSScriptRoot\SchafVPN.csproj", $csproj, (New-Object Text.UTF8Encoding $false))
Invoke-Native { git add SchafVPN.csproj } | Out-Null
Invoke-Native { git commit -q -m "Surum $Version" } | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git commit basarisiz." }
Invoke-Native { git push -q origin HEAD }
if ($LASTEXITCODE -ne 0) { throw "git push basarisiz." }

if (-not $Notes) { $Notes = "SchafVPN $Version" }
$Notes += "`n`n**Kurulum:** ``SchafVPN.exe`` dosyasini indir ve calistir. Ilk acilista VLESS linkini gir.`nOnceki surumu kullananlar bu guncellemeyi uygulama icinden otomatik alir."
$branch = git rev-parse --abbrev-ref HEAD
Invoke-Native { & $gh release create $tag $exe "dist-release\SchafVPN.exe.sha256" --title "SchafVPN $Version" --notes $Notes --target $branch }
if ($LASTEXITCODE -ne 0) { throw "GitHub Release olusturulamadi." }
Write-Host ""
Write-Host "Yayinlandi: $tag"
