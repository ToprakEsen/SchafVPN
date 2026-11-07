# Kullanicidan VLESS linkini alir ve profile.txt dosyasina yazar.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$profilePath = Join-Path $root "profile.txt"

function Test-VlessLink([string]$link) {
    if (-not $link.StartsWith("vless://", [StringComparison]::OrdinalIgnoreCase)) { return "Link 'vless://' ile baslamali." }
    try { $u = [Uri]$link } catch { return "Link okunamadi, eksik kopyalanmis olabilir." }
    if ([string]::IsNullOrEmpty($u.UserInfo)) { return "Linkte UUID yok." }
    if ([string]::IsNullOrEmpty($u.Host))     { return "Linkte sunucu adresi yok." }
    if ($u.Port -le 0)                         { return "Linkte port yok." }
    return $null
}

if (Test-Path $profilePath) {
    $existing = Get-Content $profilePath | Where-Object { $_.Trim() -like "vless://*" } | Select-Object -First 1
    if ($existing) {
        $name = try { [Uri]::UnescapeDataString(([Uri]$existing.Trim()).Fragment.TrimStart('#')) } catch { "" }
        if (-not $name) { $name = "(isimsiz)" }
        Write-Host ""
        Write-Host "  Kayitli bir profil var: $name"
        $ans = Read-Host "  Bu profil kullanilsin mi? (E = evet, H = yeni link gir)"
        if ($ans -notmatch '^[hHnN]') { exit 0 }
    }
}

Write-Host ""
Write-Host "  v2rayN'de sunucuna sag tikla -> 'Paylasim linkini panoya kopyala'"
Write-Host "  Sonra linki buraya yapistir (sag tik veya Ctrl+V) ve Enter'a bas."
Write-Host ""

while ($true) {
    $link = (Read-Host "  VLESS linki").Trim()
    $err = Test-VlessLink $link
    if (-not $err) { break }
    Write-Host "  Hata: $err Tekrar dene." -ForegroundColor Yellow
}

[IO.File]::WriteAllText($profilePath, $link + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "  Profil kaydedildi." -ForegroundColor Green
