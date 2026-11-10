<p align="center"><img src="assets/logo-256.png" width="120" alt="SchafVPN logosu"></p>

# SchafVPN

Tek tuşlu, kendi VLESS profilinle TUN modunda çalışan Windows VPN istemcisi. Kendini otomatik günceller.

## Kurulum

1. [**Releases**](https://github.com/ToprakEsen/SchafVPN/releases/latest) sayfasından `SchafVPN.exe` dosyasını indir.
2. Çalıştır, UAC'ye **Evet** de.
3. İlk açılışta v2rayN'den kopyaladığın `vless://...` linkini yapıştırıp **Kaydet**'e bas.
4. **Bağlan**'a bas.

- Link yalnızca senin bilgisayarında, Windows hesabına özel şifrelenerek (DPAPI) saklanır. Exe'nin içinde hiçbir profil yoktur.
- Profili değiştirmek için alttaki sunucu satırındaki **Değiştir**'e bas.
- **Çarpı** pencereyi gizli simgelere (sistem tepsisi) küçültür. Tepsideki koyuna sağ tıklayarak bağlanabilir veya **Çıkış** yapabilirsin.
- Yeni sürüm yayınlandığında uygulama arka planda indirir ve **Güncelle** düğmesi gösterir.
  Uygulamadan çıkarken indirilmiş bir güncelleme varsa sessizce kurulur.

## Yeni sürüm yayınlama (geliştirici)

```powershell
.\release.ps1 -Version 1.2.0 -Notes "Neler değişti"
```

Script profilsiz exe derler, içinde kişisel profil olmadığını doğrular, sürüm numarasını commit'ler
ve GitHub Release oluşturur. Uygulamayı kullanan herkes bu sürümü otomatik alır.

## Kendin derle (isteğe bağlı)

1. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) kur (x64).
2. **`kur.bat`** dosyasına çift tıkla, linkini yapıştır. `dist\SchafVPN.exe` oluşur.

> `kur.bat` ile derlenen exe linkini içine gömer (ilk açılışta şifreli depoya taşınır); başkasıyla paylaşma.

## Nasıl çalışır

- Açılışta yönetici izni (UAC) ister: TUN arayüzü ve rota tablosu için şart.
- Çekirdekler uygulamanın içine gömülü (v2rayN'in TUN modundaki yapı):
  - **Xray** v26.10.31: VLESS bağlantısı (Reality, ML-KEM şifreleme, xhttp...)
  - **sing-box** v1.14.4: TUN arayüzü, rota ve DNS; trafiği yerel SOCKS üzerinden Xray'e verir
- Varsayılan mod **TUN**: tüm sistem trafiği VPN'den geçer, yerel ağ (192.168.x.x vb.) doğrudan gider.
- DNS: Cloudflare DoH (1.1.1.1) tünel üzerinden.

## Elle derleme

```powershell
copy profile.example.txt profile.txt   # sonra içine kendi vless:// linkini yapıştır
.\build.ps1                            # -> dist\SchafVPN.exe
```

`dist\SchafVPN.exe` tek başına çalışır, .NET kurulumu gerekmez (WebView2 Windows 11'de hazır gelir).
Profili değiştirmek için `profile.txt` dosyasını güncelleyip `build.ps1` dosyasını tekrar çalıştır.

v2rayN'de linki almak için: sunucuya sağ tık → **Paylaşım linkini panoya kopyala**.

## Desteklenen VLESS ayarları

| Alan | Değerler |
|---|---|
| encryption | none, mlkem768x25519plus… (Xray VLESS şifreleme) |
| security | none, tls, reality (pbk, sid, spx, pqv) |
| type (taşıma) | tcp/raw, ws, grpc, httpupgrade, xhttp |
| flow | xtls-rprx-vision |

## Dosyalar

| Yol | İçerik |
|---|---|
| `src/` | C# (WinForms + WebView2) |
| `ui/index.html` | Arayüz |
| `assets/` | Logo (`logo.svg`) ve uygulama simgesi (`app.ico`) |
| `scripts/` | Çekirdekleri indirip SHA256 doğrulayan scriptler |

Çalışma klasörü: `%LOCALAPPDATA%\SchafVPN\` → çekirdekler, config'ler, `xray.log`, `sing-box.log`.
Uygulama kapanınca (çökse bile) çekirdekler de kapanır ve internet normale döner.

> `profile.txt` ve derlenen exe sunucu bilgilerini içerir; paylaşma ve bir git deposuna koyma.
