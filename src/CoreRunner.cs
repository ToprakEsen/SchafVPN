using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SchafVPN;

/// <summary>
/// Gömülü Xray çekirdeğini çıkarır ve yönetir. VLESS bağlantısını da TUN arayüzünü de
/// (rota, DNS, DNS sızıntı engeli) tek süreç olarak Xray kurar; wintun.dll onun yanında durur.
/// </summary>
public sealed class CoreRunner : IDisposable
{
    public static readonly string WorkDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchafVPN");

    private static string XrayExe => Path.Combine(WorkDir, "xray.exe");
    private static string WintunDll => Path.Combine(WorkDir, "wintun.dll");
    public static string XrayLog => Path.Combine(WorkDir, "xray.log");

    private readonly JobObject _job = new();
    private Process? _xray;

    /// <summary>Xray kendiliğinden kapandı (çıkış kodu).</summary>
    public event Action<int>? Exited;

    /// <summary>Bağlanma aşaması değişti (arayüzde gösterilir).</summary>
    public event Action<string>? Stage;

    public bool IsRunning => _xray is { HasExited: false };

    /// <summary>
    /// Gömülü dosyayı diske çıkarır. Uygulama yönetici olarak çalıştığı için, kullanıcı
    /// klasöründeki exe her seferinde özetle doğrulanır; değiştirilmişse üzerine yazılır.
    /// </summary>
    private static void Extract(string resource, string path)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                      ?? throw new InvalidOperationException($"Gömülü {resource} bulunamadı.");
        var expected = SHA256.HashData(s);
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (existing.Length == s.Length && SHA256.HashData(existing).AsSpan().SequenceEqual(expected)) return;
        }
        s.Position = 0;
        using var dst = File.Create(path);
        s.CopyTo(dst);
    }

    /// <summary>Önceki (sing-box'lı) sürümlerden kalan dosyalar; xray.json düz metin link içerirdi.</summary>
    private static void CleanupLegacy()
    {
        foreach (var name in new[] { "sing-box.exe", "sing-box.json", "sing-box.log", "xray.json", "config.json" })
        {
            try { File.Delete(Path.Combine(WorkDir, name)); } catch { }
        }
    }

    /// <summary>Sistemde IPv6 tamamen kapatılmışsa TUN'a IPv6 girdisi verilmemeli (Xray başlamaz).</summary>
    private static bool Ipv6Enabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters");
            if (key?.GetValue("DisabledComponents") is int v && (v & 0xFF) == 0xFF) return false;
        }
        catch { }
        return System.Net.Sockets.Socket.OSSupportsIPv6;
    }

    public async Task StartAsync(VlessProfile profile, CancellationToken ct = default)
    {
        if (IsRunning) return;
        Stop();

        Stage?.Invoke("Çekirdek hazırlanıyor");
        Directory.CreateDirectory(WorkDir);
        CleanupLegacy();
        await Task.Run(() =>
        {
            Extract("xray.exe", XrayExe);
            Extract("wintun.dll", WintunDll);
        }, ct);
        try { File.Delete(XrayLog); } catch { }

        // Config diske yazılmaz: içinde VLESS bilgileri var, Xray'e stdin'den verilir
        var config = profile.BuildXrayConfig(XrayLog, Ipv6Enabled());

        Stage?.Invoke("Xray başlatılıyor");
        _xray = Launch(config);

        Stage?.Invoke("Tünel kuruluyor");
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_xray.HasExited)
                throw new InvalidOperationException(FailMessage("Xray başlatılamadı", _xray));
            // Xray "started" satırını tüm girişler (TUN, rotalar, filtreler) hazır olunca yazar
            if (TunInterfaceUp() && ReadLog(XrayLog).Contains("started", StringComparison.Ordinal)) break;
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException(FailMessage("Tünel arayüzü zamanında açılmadı", _xray));
            await Task.Delay(150, ct);
        }
    }

    private static bool TunInterfaceUp() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.Name == VlessProfile.TunName && n.OperationalStatus == OperationalStatus.Up);

    private static string FailMessage(string title, Process p)
    {
        var tail = ReadLog(XrayLog, 4);
        var code = p.HasExited ? $" (kod {SafeExitCode(p)})" : "";
        return string.IsNullOrWhiteSpace(tail) ? title + code + "." : $"{title}{code}:\n{tail}";
    }

    private Process Launch(string config)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(XrayExe, "run -c stdin:")
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // penceresiz ama konsollu: Ctrl+C gönderebilmek için
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = WorkDir,
            },
            EnableRaisingEvents = true,
        };
        p.Exited += (_, _) =>
        {
            // Stop() sırasında alan null'lanır; o durumda olay bildirilmez
            if (ReferenceEquals(p, _xray)) Exited?.Invoke(SafeExitCode(p));
        };
        p.Start();
        _job.Assign(p); // uygulama çökse bile Xray kapanır
        p.StandardInput.Write(config);
        p.StandardInput.Close();
        return p;
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    /// <summary>
    /// Xray'i durdurur. Önce Ctrl+C: Xray rotaları, DNS'i, WFP filtrelerini ve fiziksel
    /// arayüzdeki ayarı yalnızca düzgün çıkışta geri alır. Yanıt vermezse zorla kapatılır.
    /// </summary>
    public void Stop()
    {
        var p = _xray;
        _xray = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                bool graceful = SendCtrlC(p) && p.WaitForExit(5000);
                if (!graceful && !p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
            }
        }
        catch { }
        finally { p.Dispose(); }
    }

    private static readonly object ConsoleLock = new();

    private static bool SendCtrlC(Process p)
    {
        lock (ConsoleLock)
        {
            // Bu uygulamanın kendi konsolu yok; Xray'in (gizli) konsoluna geçici olarak bağlanılır
            if (!AttachConsole((uint)p.Id)) return false;
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true); // aynı Ctrl+C bu süreci etkilemesin (kalıcı; GUI için zararsız)
                return GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
            }
            finally { FreeConsole(); }
        }
    }

    public static string ReadLog(string path, int lastLines = 0)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            var text = r.ReadToEnd();
            if (lastLines <= 0) return text;
            return string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lastLines));
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        Stop();
        _job.Dispose();
    }

    // ---------------------------------------------------------------- Win32

    private const uint CTRL_C_EVENT = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    private sealed class JobObject : IDisposable
    {
        private readonly IntPtr _handle;

        public JobObject()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)len);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public void Assign(Process p) => AssignProcessToJobObject(_handle, p.Handle);
        public void Dispose() => CloseHandle(_handle);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
