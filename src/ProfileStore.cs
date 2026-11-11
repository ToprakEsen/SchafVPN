using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SchafVPN;

/// <summary>
/// VLESS linkini bilgisayarda Windows DPAPI ile (yalnızca bu kullanıcı çözebilir) saklar.
/// Exe güncellense de profil kalır. Exe'ye gömülü bir profil varsa (kur.bat ile derlenmiş
/// eski sürümler) ilk açılışta buraya taşınır.
/// </summary>
public static class ProfileStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SchafVPN.profile.v1");
    private static string StorePath => Path.Combine(CoreRunner.WorkDir, "profile.dat");

    /// <summary>Kayıtlı link; yoksa gömülü link (ve onu kaydeder); o da yoksa null.</summary>
    public static string? Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser);
                var link = Encoding.UTF8.GetString(plain).Trim();
                if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return link;
            }
        }
        catch { /* bozuk veya başka kullanıcıya ait: yok say */ }

        var embedded = ReadEmbedded();
        if (embedded != null)
        {
            try { Save(embedded); } catch { }
            return embedded;
        }
        return null;
    }

    /// <summary>Linki doğrular ve şifreli kaydeder. Geçersizse FormatException fırlatır.</summary>
    public static VlessProfile Save(string link)
    {
        link = link.Trim();
        var profile = VlessProfile.Parse(link);
        profile.BuildXrayConfig("x", ipv6: true); // eksik alan varsa (ör. pbk) burada yakalansın
        Directory.CreateDirectory(CoreRunner.WorkDir);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(link), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, data);
        return profile;
    }

    private static string? ReadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("profile.txt");
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd()
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("vless://", StringComparison.OrdinalIgnoreCase));
    }
}
