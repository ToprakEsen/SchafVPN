using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SchafVPN;

/// <summary>
/// Gömülü çekirdekleri çıkarır ve yönetir:
///   Xray     -> VLESS bağlantısı (yerel SOCKS 127.0.0.1:rastgele)
///   sing-box -> TUN arayüzü, rota, DNS; trafiği Xray'in SOCKS'una verir
/// </summary>
public sealed class CoreRunner : IDisposable
{
    public static readonly string WorkDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchafVPN");

    private static string SingBoxExe => Path.Combine(WorkDir, "sing-box.exe");
    private static string XrayExe => Path.Combine(WorkDir, "xray.exe");
    public static string SingBoxLog => Path.Combine(WorkDir, "sing-box.log");
    public static string XrayLog => Path.Combine(WorkDir, "xray.log");

    private readonly JobObject _job = new();
    private Process? _xray;
    private Process? _singbox;

    /// <summary>Beklenmedik kapanma: (hangi çekirdek, çıkış kodu)</summary>
    public event Action<string, int>? Exited;

    public bool IsRunning => _xray is { HasExited: false } && _singbox is { HasExited: false };

    public int ClashPort { get; private set; }
    public string ClashSecret { get; private set; } = "";

    private static void Extract(string resource, string path)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                      ?? throw new InvalidOperationException($"Gömülü {resource} bulunamadı.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes))) return;
        }
        File.WriteAllBytes(path, bytes);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public async Task StartAsync(VlessProfile profile, CancellationToken ct = default)
    {
        if (IsRunning) return;
        Stop();

        Directory.CreateDirectory(WorkDir);
        Extract("xray.exe", XrayExe);
        Extract("sing-box.exe", SingBoxExe);
        foreach (var f in new[] { SingBoxLog, XrayLog }) { try { File.Delete(f); } catch { } }

        int socksPort = FreePort();
        string socksUser = RandomToken(), socksPass = RandomToken();
        ClashPort = FreePort();
        ClashSecret = RandomToken();

        var xrayCfg = Path.Combine(WorkDir, "xray.json");
        var sbCfg = Path.Combine(WorkDir, "sing-box.json");
        File.WriteAllText(xrayCfg, profile.BuildXrayConfig(XrayLog, socksPort, socksUser, socksPass));
        File.WriteAllText(sbCfg, profile.BuildSingBoxConfig(SingBoxLog, ClashPort, ClashSecret, socksPort, socksUser, socksPass));

        // 1) Xray: SOCKS portu dinlemeye başlayana kadar bekle
        _xray = Launch(XrayExe, $"run -c \"{xrayCfg}\"", "xray");
        if (!await WaitForPortAsync(socksPort, _xray, TimeSpan.FromSeconds(10), ct))
            throw new InvalidOperationException(FailMessage("Xray başlatılamadı", XrayLog, _xray));

        // 2) sing-box: TUN arayüzü
        _singbox = Launch(SingBoxExe, $"run -c \"{sbCfg}\" -D \"{WorkDir}\"", "sing-box");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_singbox.HasExited)
                throw new InvalidOperationException(FailMessage("TUN arayüzü açılamadı", SingBoxLog, _singbox));
            if (ReadLog(SingBoxLog).Contains("sing-box started", StringComparison.Ordinal) ||
                NetworkInterfaceUp("SchafVPN")) break;
            await Task.Delay(200, ct);
        }
        // log seviyesi warn olduğunda "started" yazmayabilir; arayüz kontrolü yeterli
        await Task.Delay(300, ct);
        if (_singbox.HasExited)
            throw new InvalidOperationException(FailMessage("TUN arayüzü açılamadı", SingBoxLog, _singbox));
    }

    private static bool NetworkInterfaceUp(string name) =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.Name == name && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);

    private static string FailMessage(string title, string log, Process p)
    {
        var tail = ReadLog(log, 4);
        var code = p.HasExited ? $" (kod {p.ExitCode})" : "";
        return string.IsNullOrWhiteSpace(tail) ? title + code + "." : $"{title}{code}:\n{tail}";
    }

    private Process Launch(string exe, string args, string name)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = WorkDir,
            },
            EnableRaisingEvents = true,
        };
        p.Exited += (_, _) =>
        {
            // Stop() sırasında alanlar null'lanır; o durumda olay bildirilmez
            if (ReferenceEquals(p, _xray) || ReferenceEquals(p, _singbox))
                Exited?.Invoke(name, SafeExitCode(p));
        };
        p.Start();
        _job.Assign(p); // uygulama çökse bile çekirdekler kapanır
        return p;
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    private static async Task<bool> WaitForPortAsync(int port, Process p, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (p.HasExited) return false;
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port, ct);
                return true;
            }
            catch (SocketException) { await Task.Delay(150, ct); }
        }
        return false;
    }

    public void Stop()
    {
        // önce sing-box (TUN + rotalar kalksın), sonra Xray
        var sb = _singbox; var xr = _xray;
        _singbox = null; _xray = null;
        Kill(sb);
        Kill(xr);
    }

    private static void Kill(Process? p)
    {
        if (p == null) return;
        try
        {
            if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
        }
        catch { }
        finally { p.Dispose(); }
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

    // ---------------------------------------------------------------- Job Object

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
