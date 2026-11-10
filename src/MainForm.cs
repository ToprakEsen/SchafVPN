using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SchafVPN;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(9, 11, 16);

    /// <summary>Arayüzde sunucu adı yerine gösterilen sabit isim.</summary>
    private const string ServerLabel = "KoyunVPN";

    // Ekranda görünen (96 DPI) boyut; %150 ölçekte 690x1290 fiziksel piksel olur
    private const int LogicalWidth = 460;
    private const int LogicalHeight = 860;

    private readonly WebView2 _web;
    private readonly CoreRunner _core = new();
    private VlessProfile? _profile;
    private string? _profileError;

    private string _state = "idle";
    private long _connectedAt;
    private bool _userStopping;
    private CancellationTokenSource? _trafficCts;
    private int _ipGen;

    // tepsi
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayToggle;
    private readonly Icon _baseIcon;
    private Icon? _trayIcon;
    private bool _exiting;
    private bool _trayHintShown;

    // ayarlar
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly bool _startHidden;
    private bool _autoConnectDone;

    // güncelleme
    private readonly bool _justUpdated;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private UpdateInfo? _update;
    private string? _updateFile;
    private bool _updateBusy;

    // ikinci kopyadan "göster" sinyali
    private readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, Program.ShowEventName);
    private readonly RegisteredWaitHandle _showWait;

    public MainForm(bool justUpdated = false, bool startHidden = false)
    {
        _justUpdated = justUpdated;
        _startHidden = startHidden;
        if (startHidden) { Opacity = 0; ShowInTaskbar = false; } // Windows açılışında tepside başla
        Text = "SchafVPN";
        FormBorderStyle = FormBorderStyle.None; // başlık çubuğunu arayüz çiziyor
        AutoScaleMode = AutoScaleMode.None;     // boyutu OnLoad'da DPI'a göre biz ayarlıyoruz
        ClientSize = new Size(LogicalWidth, LogicalHeight);
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        _baseIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        Icon = _baseIcon;

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
        Controls.Add(_web);

        LoadProfile();

        // ---- tepsi simgesi
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("SchafVPN'i aç", null, (_, _) => ShowFromTray()) { Font = new Font(SystemFonts.MenuFont ?? DefaultFont, FontStyle.Bold) };
        _trayToggle = new ToolStripMenuItem("Bağlan", null, async (_, _) => await ToggleAsync());
        var check = new ToolStripMenuItem("Güncellemeleri denetle", null, async (_, _) => await CheckForUpdateAsync(manual: true));
        var exit = new ToolStripMenuItem("Çıkış", null, (_, _) => ExitApp());
        menu.Items.AddRange([open, new ToolStripSeparator(), _trayToggle, new ToolStripSeparator(), check, exit]);
        _tray = new NotifyIcon { Text = "SchafVPN", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        UpdateTray();

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => { try { BeginInvoke(ShowFromTray); } catch { } }, null, -1, false);

        _updateTimer.Tick += async (_, _) => { if (_settings.AutoUpdate) await CheckForUpdateAsync(manual: false); };
        _core.Exited += (name, code) => { try { BeginInvoke(() => OnCoreExited(name, code)); } catch { } };
        Load += async (_, _) => await InitWebAsync();
    }

    private void LoadProfile()
    {
        _profile = null;
        _profileError = null;
        var link = ProfileStore.Load();
        if (link == null) return; // profil yok: arayüz kurulum ekranını açar
        try { _profile = VlessProfile.Parse(link); }
        catch (Exception ex) { _profileError = ex.Message; }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToScreen();
        DpiChanged += (_, args) => Bounds = args.SuggestedRectangle; // başka monitöre taşınınca
        if (_startHidden)
        {
            BeginInvoke(() => { Hide(); Opacity = 1; ShowInTaskbar = true; });
            _trayHintShown = true;
        }
        // exe taşınmış/güncellenmiş olabilir: başlangıç görevini güncel yolla yenile
        if (_settings.StartWithWindows)
            _ = Task.Run(() => { try { StartupTask.Set(true); } catch { } });
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
            ExitApp();
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
        JsonElement msg;
        string cmd;
        try
        {
            msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
            cmd = msg.GetProperty("cmd").GetString() ?? "";
        }
        catch { return; }

        switch (cmd)
        {
            case "ready":
                PushState();
                PushSettings();
                Send(new { type = "version", version = Updater.Current.ToString(3) });
                PushUpdate();
                _ = RefreshIpAsync();
                if (_justUpdated)
                    Send(new { type = "notice", text = $"SchafVPN {Updater.Current.ToString(3)} sürümüne güncellendi." });
                // ilk denetim açılıştan kısa süre sonra, sonra 6 saatte bir
                if (_settings.AutoUpdate)
                    _ = Task.Delay(4000).ContinueWith(_ => BeginInvoke(async () => await CheckForUpdateAsync(manual: false)));
                _updateTimer.Start();
                if (_settings.AutoConnect && !_autoConnectDone && _profile != null && _state == "idle")
                {
                    _autoConnectDone = true;
                    await ToggleAsync();
                }
                break;
            case "setSetting":
                ApplySetting(msg.GetProperty("key").GetString() ?? "", msg.GetProperty("value").GetBoolean());
                break;
            case "toggle": await ToggleAsync(); break;
            case "saveProfile":
                SaveProfile(msg.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "");
                break;
            case "paste":
                Send(new { type = "paste", text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "" });
                break;
            case "applyUpdate": ApplyUpdate(restart: true); break;
            case "checkUpdate": await CheckForUpdateAsync(manual: true); break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "close":
                if (_settings.CloseToTray) HideToTray(); else ExitApp();
                break;
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

    private void PushState(string? error = null, string? errorTitle = null)
    {
        Send(new
        {
            type = "state",
            state = _state,
            hasProfile = _profile != null || _profileError != null,
            name = _profileError != null ? "Profil okunamadı" : ServerLabel,
            detail = _profileError ?? _profile?.Detail ?? "VLESS",
            profileError = _profileError != null,
            connectedAt = _connectedAt,
            error,
            errorTitle,
        });
        UpdateTray();
    }

    private void PushSettings() => Send(new
    {
        type = "settings",
        closeToTray = _settings.CloseToTray,
        startWithWindows = _settings.StartWithWindows,
        autoConnect = _settings.AutoConnect,
        autoUpdate = _settings.AutoUpdate,
    });

    private void ApplySetting(string key, bool value)
    {
        try
        {
            switch (key)
            {
                case "closeToTray": _settings.CloseToTray = value; break;
                case "autoConnect": _settings.AutoConnect = value; break;
                case "autoUpdate":
                    _settings.AutoUpdate = value;
                    if (value) _ = CheckForUpdateAsync(manual: false);
                    break;
                case "startWithWindows":
                    StartupTask.Set(value);
                    _settings.StartWithWindows = value;
                    break;
                default: return;
            }
            _settings.Save();
        }
        catch (Exception ex)
        {
            Send(new { type = "notice", text = "Ayar kaydedilemedi: " + ex.Message });
        }
        PushSettings(); // hata olduysa anahtar eski haline döner
    }

    private void SaveProfile(string link)
    {
        if (_state != "idle")
        {
            Send(new { type = "profileResult", ok = false, message = "Profili değiştirmek için önce bağlantıyı kes." });
            return;
        }
        try
        {
            _profile = ProfileStore.Save(link);
            _profileError = null;
            Send(new { type = "profileResult", ok = true });
            PushState();
        }
        catch (Exception ex)
        {
            Send(new { type = "profileResult", ok = false, message = ex.Message });
        }
    }

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
        if (_state == "connecting") return;
        if (_state == "connected") { Disconnect(); return; }
        if (_profile == null) { ShowFromTray(); PushState(); return; } // kurulum ekranı açılsın

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
            if (!Visible) _tray.ShowBalloonTip(5000, "SchafVPN", "Bağlantı başlatılamadı.", ToolTipIcon.Error);
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
        if (!Visible) _tray.ShowBalloonTip(5000, "SchafVPN", "VPN bağlantısı kesildi.", ToolTipIcon.Warning);
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

    // ---------------------------------------------------------------- güncelleme

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (manual) Send(new { type = "update", status = "checking" });
            var info = await Updater.CheckAsync();
            if (info == null)
            {
                if (manual)
                {
                    Send(new { type = "update", status = "none", version = Updater.Current.ToString(3) });
                    if (!Visible) _tray.ShowBalloonTip(3000, "SchafVPN", $"En güncel sürümü kullanıyorsun ({Updater.Current.ToString(3)}).", ToolTipIcon.Info);
                }
                return;
            }
            if (_updateFile != null && _update?.Version == info.Version) { PushUpdate(); return; }

            _update = info;
            Send(new { type = "update", status = "downloading", version = info.Version.ToString(3), progress = 0.0 });
            var progress = new Progress<double>(p =>
                Send(new { type = "update", status = "downloading", version = info.Version.ToString(3), progress = p }));
            _updateFile = await Updater.DownloadAsync(info, progress);
            PushUpdate();
            if (!Visible)
                _tray.ShowBalloonTip(6000, "SchafVPN güncellemesi hazır",
                    $"Sürüm {info.Version.ToString(3)} indirildi. Kurmak için tıkla.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (manual) Send(new { type = "update", status = "error", message = ex.Message });
        }
        finally { _updateBusy = false; }
    }

    private void PushUpdate()
    {
        if (_update != null && _updateFile != null)
            Send(new { type = "update", status = "ready", version = _update.Version.ToString(3), connected = _state != "idle" });
    }

    /// <summary>İndirilen güncellemeyi uygular: uygulama kapanır, yeni exe yerine geçer.</summary>
    private void ApplyUpdate(bool restart)
    {
        if (_updateFile == null || !File.Exists(_updateFile)) return;
        try
        {
            Updater.ScheduleApply(_updateFile, restart);
            _updateFile = null;
            ExitApp(applyPending: false);
        }
        catch (Exception ex)
        {
            Send(new { type = "update", status = "error", message = "Güncelleme uygulanamadı: " + ex.Message });
        }
    }

    // ---------------------------------------------------------------- tepsi

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "SchafVPN arka planda çalışıyor",
                "Açmak için gizli simgelerdeki koyuna tıkla. Kapatmak için sağ tık → Çıkış.", ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        if (IsDisposed) return;
        // bekleyen güncelleme balonuna tıklandıysa doğrudan göster
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp(bool applyPending = true)
    {
        // kapanırken indirilmiş bir güncelleme varsa sessizce kur (yeniden başlatmadan)
        if (applyPending && _updateFile != null && File.Exists(_updateFile))
        {
            try { Updater.ScheduleApply(_updateFile, restart: false); } catch { }
        }
        _exiting = true;
        _tray.Visible = false;
        Close();
    }

    private void UpdateTray()
    {
        string status = _state switch
        {
            "connected" => "Bağlı",
            "connecting" => "Bağlanıyor…",
            _ => "Bağlı değil",
        };
        _tray.Text = $"SchafVPN — {status}";
        _trayToggle.Text = _state == "connected" ? "Bağlantıyı kes" : "Bağlan";
        _trayToggle.Enabled = _state != "connecting";

        Color? dot = _state switch
        {
            "connected" => Color.FromArgb(61, 220, 151),
            "connecting" => Color.FromArgb(245, 181, 68),
            _ => null,
        };
        var old = _trayIcon;
        _trayIcon = MakeTrayIcon(dot);
        _tray.Icon = _trayIcon;
        if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    /// <summary>Uygulama simgesi + sağ altta durum noktası.</summary>
    private Icon MakeTrayIcon(Color? dot)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var src = new Icon(_baseIcon, size)) g.DrawIcon(src, new Rectangle(Point.Empty, size));
            if (dot is { } c)
            {
                float d = size.Width * 0.5f;
                var r = new RectangleF(size.Width - d, size.Height - d, d - 0.5f, d - 0.5f);
                using var ring = new SolidBrush(Color.FromArgb(9, 11, 16));
                g.FillEllipse(ring, RectangleF.Inflate(r, 1f, 1f));
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, r);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Çarpı / Alt+F4: ayara göre tepsiye küçül ya da çık. Windows kapanırken gerçekten kapan.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (_settings.CloseToTray) HideToTray();
            else BeginInvoke(() => ExitApp());
            return;
        }
        _userStopping = true;
        _updateTimer.Stop();
        StopTraffic();
        _core.Dispose();
        _showWait.Unregister(null);
        _showEvent.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
