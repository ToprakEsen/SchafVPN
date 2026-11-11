using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchafVPN;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string Sha256, long Size, string Notes);

/// <summary>
/// GitHub Releases üzerinden güncelleme: son sürümü denetler, exe'yi indirip SHA256 ile
/// doğrular, uygulama kapandıktan sonra eskisinin üzerine yazıp yeniden başlatır.
/// </summary>
public static class Updater
{
    public const string Repo = "ToprakEsen/SchafVPN";
    private const string AssetName = "SchafVPN.exe";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static string UpdateDir => Path.Combine(CoreRunner.WorkDir, "update");

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SchafVPN-Updater/{Current}");
        return http;
    }

    /// <summary>
    /// Daha yeni bir sürüm varsa bilgisini, yoksa null döner; denetlenemezse hata fırlatır.
    /// Önce github.com/.../releases/latest yönlendirmesi + .sha256 dosyası: GitHub API'nin
    /// saatlik IP sınırına tabi değil. Yalnızca o başarısız olursa API denenir.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        Exception redirectError;
        try { return await CheckRedirectAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { redirectError = e; }
        try { return await CheckApiAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException($"GitHub'a ulaşılamadı ({redirectError.Message}; {e.Message})");
        }
    }

    private static async Task<UpdateInfo?> CheckRedirectAsync(CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SchafVPN-Updater/{Current}");
        using var resp = await http.GetAsync($"https://github.com/{Repo}/releases/latest", ct);
        var location = resp.Headers.Location?.ToString();
        if ((int)resp.StatusCode / 100 != 3 || location == null)
            throw new InvalidOperationException($"releases/latest HTTP {(int)resp.StatusCode}");
        int i = location.LastIndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0) return null; // hiç sürüm yok
        var rawTag = location[(i + 5)..];
        if (!Version.TryParse(rawTag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        if (latest <= Current) return null;

        var url = $"https://github.com/{Repo}/releases/download/{rawTag}/{AssetName}";
        using var dl = NewClient(TimeSpan.FromSeconds(15));
        var sha = (await dl.GetStringAsync(url + ".sha256", ct)).Trim().Split(' ', '\t', '\n')[0];
        if (sha.Length != 64) throw new InvalidDataException("SHA256 dosyası geçersiz.");
        return new UpdateInfo(latest, url, sha.ToLowerInvariant(), 0, "");
    }

    private static async Task<UpdateInfo?> CheckApiAsync(CancellationToken ct)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // henüz sürüm yok
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"API HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        if (latest <= Current) return null;

        string? url = null, sha = null, shaUrl = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            if (name == AssetName)
            {
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:"))
                    sha = dg["sha256:".Length..];
            }
            else if (name == AssetName + ".sha256")
                shaUrl = a.GetProperty("browser_download_url").GetString();
        }
        if (url == null) return null;
        if (sha == null && shaUrl != null)
            sha = (await http.GetStringAsync(shaUrl, ct)).Trim().Split(' ', '\t', '\n')[0];
        if (sha == null || sha.Length != 64) return null; // doğrulanamayan güncelleme kurulmaz

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(latest, url, sha.ToLowerInvariant(), size, notes);
    }

    /// <summary>Güncellemeyi indirir ve SHA256 doğrular; doğrulanmış dosyanın yolunu döner.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdateDir);
        var target = Path.Combine(UpdateDir, $"SchafVPN-{u.Version}.exe");
        if (File.Exists(target) && HashFile(target) == u.Sha256) return target;

        // eski indirmeleri temizle
        foreach (var f in Directory.GetFiles(UpdateDir)) { try { File.Delete(f); } catch { } }

        var part = target + ".part";
        using (var http = NewClient(TimeSpan.FromMinutes(20)))
        using (var resp = await http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long read = 0; int n; double last = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                {
                    double p = (double)read / total;
                    if (p - last >= 0.01) { last = p; progress?.Report(p); }
                }
            }
        }

        if (HashFile(part) != u.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("İndirilen güncellemenin SHA256 değeri uyuşmuyor.");
        }
        File.Move(part, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Uygulama kapandıktan sonra yeni exe'yi eskisinin üzerine kopyalayan gizli bir
    /// yardımcı başlatır. Çağıran taraf hemen ardından uygulamayı kapatmalıdır.
    /// </summary>
    public static void ScheduleApply(string newExe, bool restart)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Exe yolu bulunamadı.");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        var script = new StringBuilder()
            .AppendLine($"$p = {Environment.ProcessId}")
            .AppendLine("try { Wait-Process -Id $p -Timeout 60 -ErrorAction Stop } catch { }")
            .AppendLine("for ($i = 0; $i -lt 40; $i++) {")
            .AppendLine($"  try {{ Copy-Item -LiteralPath {Q(newExe)} -Destination {Q(current)} -Force -ErrorAction Stop; Remove-Item -LiteralPath {Q(newExe)} -Force -ErrorAction SilentlyContinue; break }}")
            .AppendLine("  catch { Start-Sleep -Milliseconds 500 }")
            .AppendLine("}");
        if (restart)
            script.AppendLine($"Start-Process -FilePath {Q(current)} -ArgumentList '--updated'");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
