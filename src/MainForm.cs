using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SchafVPN;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(9, 11, 16);

    private readonly WebView2 _web;
    private readonly CoreRunner _core = new();
    private readonly VlessProfile? _profile;
    private readonly string? _profileError;

    private string _state = "idle";
    private long _connectedAt;
    private bool _userStopping;
    private CancellationTokenSource? _trafficCts;
    private int _ipGen;

    public MainForm()
    {
        Text = "SchafVPN";
        FormBorderStyle = FormBorderStyle.None; // başlık çubuğunu arayüz çiziyor
        AutoScaleMode = AutoScaleMode.None;     // boyutu OnLoad'da DPI'a göre biz ayarlıyoruz
        ClientSize = new Size(LogicalWidth, LogicalHeight);
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
        Controls.Add(_web);

        try { _profile = VlessProfile.Parse(CoreRunner.ReadEmbeddedProfile()); }
        catch (Exception ex) { _profileError = ex.Message; }

        _core.Exited += (name, code) => { try { BeginInvoke(() => OnCoreExited(name, code)); } catch { } };
        Load += async (_, _) => await InitWebAsync();
    }

    // Ekranda görünen (96 DPI) boyut; %150 ölçekte 690x1290 fiziksel piksel olur
    /// <summary>Arayüzde sunucu adı yerine gösterilen sabit isim.</summary>
    private const string ServerLabel = "KoyunVPN";

    private const int LogicalWidth = 460;
    private const int LogicalHeight = 860;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToScreen();
        DpiChanged += (_, args) => Bounds = args.SuggestedRectangle; // başka monitöre taşınınca
    }

    /// <summary>Boyutu ekran ölçeğine göre ayarlar, ekrana sığdırır ve ortalar.</summary>
    private void FitToScreen()
    {
        float scale = DeviceDpi / 96f;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = (int)Math.Round(LogicalWidth * scale);
        int h = (int)Math.Round(LogicalHeight * scale);
        h = Math.Min(h, (int)(area.Height * 0.94));
        w = Math.Min(w, (int)(area.Width * 0.94));
        ClientSize = new Size(w, h);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX: görev çubuğundan küçült/geri getir
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int v = 1;
        DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int));          // koyu mod
        v = 2;
        DwmSetWindowAttribute(Handle, 2, ref v, sizeof(int));           // DWM çizimi açık -> gölge
        v = 2;
        DwmSetWindowAttribute(Handle, 33, ref v, sizeof(int));          // Windows 11 yuvarlak köşe
        v = 0x00302A26;                                                 // ince kenarlık (#262a30)
        DwmSetWindowAttribute(Handle, 34, ref v, sizeof(int));
        var m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(Handle, ref m);
    }

    private async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(CoreRunner.WorkDir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Arayüz başlatılamadı. Microsoft Edge WebView2 Runtime gerekli:\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" + ex.Message,
                "SchafVPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsPinchZoomEnabled = false;

        _web.CoreWebView2.WebMessageReceived += OnWebMessage;
        _web.CoreWebView2.NavigateToString(LoadHtml());
    }

    private static string LoadHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---------------------------------------------------------------- web <-> C#

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd;
        try { cmd = JsonDocument.Parse(e.WebMessageAsJson).RootElement.GetProperty("cmd").GetString() ?? ""; }
        catch { return; }

        switch (cmd)
        {
            case "ready":
                PushState();
                _ = RefreshIpAsync();
                break;
            case "toggle": await ToggleAsync(); break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "close": Close(); break;
            case "openLog":
                if (Directory.Exists(CoreRunner.WorkDir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CoreRunner.WorkDir}\"") { UseShellExecute = true });
                break;
        }
    }

    private void Send(object message)
    {
        if (_web.CoreWebView2 == null || IsDisposed) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void PushState(string? error = null, string? errorTitle = null) => Send(new
    {
        type = "state",
        state = _state,
        name = _profileError != null ? "Profil okunamadı" : ServerLabel,
        detail = _profileError ?? _profile!.Detail,
        profileError = _profileError != null,
        connectedAt = _connectedAt,
        error,
        errorTitle,
    });

    /// <summary>Dışarıdan görünen IP ve konum (bağlıyken tünelden geçer).</summary>
    private async Task RefreshIpAsync(int delayMs = 0)
    {
        int gen = ++_ipGen;
        Send(new { type = "ip", loading = true });
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (gen != _ipGen) return;
            // her seferinde yeni bağlantı: tünel öncesi açılmış bağlantı yeniden kullanılmasın
            using var http = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.Zero,
            }) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SchafVPN/1.0");
            var json = await http.GetStringAsync("https://ipinfo.io/json");
            if (gen != _ipGen) return;
            var root = JsonDocument.Parse(json).RootElement;
            string Get(string k) => root.TryGetProperty(k, out var p) ? p.GetString() ?? "" : "";
            Send(new { type = "ip", ip = Get("ip"), city = Get("city"), country = Get("country") });
        }
        catch
        {
            if (gen == _ipGen) Send(new { type = "ip", error = true });
        }
    }

    // ---------------------------------------------------------------- bağlantı

    private async Task ToggleAsync()
    {
        if (_state == "connecting" || _profile == null) return;
        if (_state == "connected") { Disconnect(); return; }

        _state = "connecting";
        PushState();
        try
        {
            _userStopping = true; // başlatma sırasında çıkışları StartAsync raporlar
            await _core.StartAsync(_profile);
            _userStopping = false;
            if (!_core.IsRunning) throw new InvalidOperationException("Çekirdek beklenmedik şekilde kapandı.");

            _state = "connected";
            _connectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PushState();
            StartTraffic();
            _ = RefreshIpAsync(800);
        }
        catch (Exception ex)
        {
            _userStopping = true;
            _core.Stop();
            _state = "idle";
            PushState(ex.Message, "Bağlantı başlatılamadı");
            _ = RefreshIpAsync(500);
        }
    }

    private void Disconnect()
    {
        _userStopping = true;
        StopTraffic();
        _core.Stop();
        _state = "idle";
        PushState();
        _ = RefreshIpAsync(800);
    }

    private void OnCoreExited(string name, int code)
    {
        if (_userStopping || IsDisposed || _state != "connected") return;
        _userStopping = true;
        StopTraffic();
        _core.Stop();
        _state = "idle";
        var tail = CoreRunner.ReadLog(name == "xray" ? CoreRunner.XrayLog : CoreRunner.SingBoxLog, 4);
        PushState(string.IsNullOrWhiteSpace(tail) ? $"{name} kapandı (kod {code})." : tail,
                  "Bağlantı kesildi");
        _ = RefreshIpAsync(800);
    }

    private void StartTraffic()
    {
        StopTraffic();
        _trafficCts = new CancellationTokenSource();
        var ct = _trafficCts.Token;
        _ = TrafficMonitor.RunAsync(_core.ClashPort, _core.ClashSecret,
            (up, down) =>
            {
                if (ct.IsCancellationRequested || IsDisposed) return;
                try { BeginInvoke(() => Send(new { type = "traffic", up, down })); } catch { }
            }, ct);
    }

    private void StopTraffic()
    {
        _trafficCts?.Cancel();
        _trafficCts?.Dispose();
        _trafficCts = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _userStopping = true;
        StopTraffic();
        _core.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
