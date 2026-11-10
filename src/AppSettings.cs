using System.Diagnostics;
using System.Text.Json;

namespace SchafVPN;

/// <summary>Kullanıcı ayarları: %LOCALAPPDATA%\SchafVPN\settings.json</summary>
public sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoConnect { get; set; }
    public bool AutoUpdate { get; set; } = true;

    private static string FilePath => Path.Combine(CoreRunner.WorkDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* bozuksa varsayılanlar */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(CoreRunner.WorkDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// Windows açılınca başlatma. Uygulama yönetici izni istediği için kayıt defteri "Run"
/// anahtarı her açılışta UAC sorar; bunun yerine "en yüksek yetkiyle" çalışan bir
/// zamanlanmış görev kullanılır (UAC sormadan başlar).
/// </summary>
public static class StartupTask
{
    private const string TaskName = "SchafVPN";

    public static void Set(bool enabled)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Exe yolu bulunamadı.");
        var args = enabled
            ? $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /IT /TR \"\\\"{exe}\\\" --tray\""
            : $"/Delete /F /TN \"{TaskName}\"";
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        // silerken görev zaten yoksa sorun değil
        if (p.ExitCode != 0 && enabled)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"schtasks kodu {p.ExitCode}" : err.Trim());
    }
}
