using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Zeus.Core;

namespace Zeus.Desktop;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(14, 17, 24), Nav = Color.FromArgb(10, 13, 19),
        Card = Color.FromArgb(22, 27, 37), Input = Color.FromArgb(29, 35, 47), Border = Color.FromArgb(39, 46, 60),
        Text = Color.FromArgb(235, 239, 246), Muted = Color.FromArgb(142, 154, 176),
        Accent = Color.FromArgb(244, 199, 79), Green = Color.FromArgb(78, 211, 163), Red = Color.FromArgb(241, 111, 121);
    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false) =>
        new() { Text = text, AutoSize = false, ForeColor = color ?? Text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), BackColor = Color.Transparent };
    public static Button Button(string text, bool primary = false) => new()
    {
        Text = text, Height = 40, Width = 150, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        BackColor = primary ? Accent : Input, ForeColor = primary ? Nav : Text,
        Font = new Font("Segoe UI Semibold", 10), FlatAppearance = { BorderSize = 0 },
        Margin = new Padding(0, 0, 12, 0), UseVisualStyleBackColor = false
    };
    public static TextBox TextBox(string text = "") => new()
    {
        Text = text, BorderStyle = BorderStyle.FixedSingle, BackColor = Input, ForeColor = Text,
        Font = new Font("Segoe UI", 10), Height = 30, Dock = DockStyle.Top
    };
}

public sealed class MainForm : Form
{
    private static readonly string AppVersion = typeof(MainForm).Assembly.GetName().Version!.ToString(3);
    private readonly RelayEngine engine = new();
    private readonly ProfileStore store = new(ProfileStore.DefaultDirectory);
    private Profile profile = new();
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(30, 10, 30, 24) };
    private readonly Label pageTitle = Theme.Label("Kontrol merkezi", 23, bold: true);
    private readonly Label pageSubtitle = Theme.Label("Bağlantın, trafiğin ve oturumun tek yerde.", 10, Theme.Muted);
    private readonly Label status = Theme.Label("●  HAZIR", 10, Theme.Accent, true);
    private readonly Label notice = Theme.Label("", 10, Theme.Muted);
    private readonly Button start = Theme.Button("▶  Başlat", true), stop = Theme.Button("■  Durdur");
    private readonly Button test = Theme.Button("Yerel bağlantı testi");
    private readonly Label packets = Theme.Label("0", 23, bold: true), bytes = Theme.Label("0 B", 23, bold: true),
        latency = Theme.Label("—", 23, bold: true), dropped = Theme.Label("0", 23, bold: true);
    private readonly Label target = Theme.Label("—", 13, bold: true), session = Theme.Label("Dünya  —\nKonum  —\nOyuncu  0", 12);
    private readonly TrafficGraph graph = new() { Dock = DockStyle.Fill };
    private readonly DataGridView traffic = MakeGrid(), logs = MakeGrid();
    private readonly TextBox filter = Theme.TextBox();
    private readonly CheckBox pause = new() { Text = "Görünümü duraklat", AutoSize = true, ForeColor = Theme.Muted };
    private readonly TextBox host = Theme.TextBox(), remotePort = Theme.TextBox(), localPort = Theme.TextBox(),
        bootstrapPort = Theme.TextBox(), meta = Theme.TextBox(), bookmarks = Theme.TextBox(),
        discoveryUrl = Theme.TextBox(), discoveryBody = Theme.TextBox();
    private readonly CheckBox envelope = Check("Ubisoft çıkış zarfını kullan (deneysel)"),
        bootstrap = Check("Yerel server_data servisini aç"), tls = Check("HTTPS kullan"), commands = Check("Oyun içi proxy komutlarını etkinleştir");
    private readonly Panel settingsPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Dictionary<string, Panel> pages = new();
    private readonly Dictionary<string, Button> navigation = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private long lastBytes, lastSequence;
    private int lastLogCount = -1;
    private DateTimeOffset lastLogTime;
    private string lastLogMessage = "";
    private DateTime lastTick = DateTime.UtcNow;
    private DateTime lastStatusWrite = DateTime.MinValue;
    private bool busy, closing;
    private string currentPage = "overview";
    private readonly GameMouseWatcher mouseWatcher;
    private PathMapForm? pathMap;
    public MainForm(bool autoStart = false)
    {
        mouseWatcher = new();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = $"Zeus Proxy {AppVersion} · Yerel kontrol merkezi";
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(1240, 840); MinimumSize = new Size(1120, 800); StartPosition = FormStartPosition.CenterScreen;
        var nav = new Panel { Dock = DockStyle.Left, Width = 202, BackColor = Theme.Nav, Padding = new Padding(20) };
        var logo = Theme.Label("ZEUS", 26, Theme.Accent, true); logo.SetBounds(22, 30, 175, 55); nav.Controls.Add(logo);
        var edition = Theme.Label("P R O X Y  /  L O C A L", 8, Theme.Muted); edition.SetBounds(27, 84, 172, 25); nav.Controls.Add(edition);
        string[] keys = ["overview", "traffic", "settings", "logs", "guide"];
        string[] names = ["Genel bakış", "Paket inceleyici", "Bağlantı ayarları", "Olay günlüğü", "Kurulum rehberi"];
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i]; var button = Theme.Button(names[i]); button.TextAlign = ContentAlignment.MiddleLeft;
            button.SetBounds(16, 148 + i * 53, 172, 43); button.BackColor = Theme.Nav;
            button.Click += (_, _) => ShowPage(key); nav.Controls.Add(button); navigation[key] = button;
        }
        var navFooter = Theme.Label($"WINDOWS  /  v{AppVersion}\n\nYalnızca bu bilgisayarda\ndinleyen bağlantı paneli", 9, Theme.Muted);
        navFooter.Dock = DockStyle.Bottom; navFooter.Height = 100; nav.Controls.Add(navFooter);
        var main = new Panel { Dock = DockStyle.Fill };
        var header = new Panel { Dock = DockStyle.Top, Height = 116, Padding = new Padding(30) };
        pageTitle.SetBounds(30, 24, 550, 43); pageSubtitle.SetBounds(32, 72, 650, 25);
        status.Dock = DockStyle.Right; status.Width = 240; status.TextAlign = ContentAlignment.TopRight;
        header.Controls.AddRange([pageTitle, pageSubtitle, status]);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(30, 0, 20, 0) };
        notice.Dock = DockStyle.Fill; footer.Controls.Add(notice);
        main.Controls.Add(content); main.Controls.Add(footer); main.Controls.Add(header);
        Controls.Add(main); Controls.Add(nav);
        pages["overview"] = BuildOverview(); pages["traffic"] = BuildTraffic();
        BuildSettings(); pages["settings"] = settingsPage; pages["logs"] = BuildLogs(); pages["guide"] = BuildGuide();
        foreach (var page in pages.Values) { page.Visible = false; content.Controls.Add(page); }
        start.Click += async (_, _) => await StartProxy(); stop.Click += async (_, _) => await StopProxy();
        test.Click += async (_, _) => await Diagnose();
        filter.TextChanged += (_, _) => { lastSequence = -1; RefreshTraffic(engine.Telemetry.Snapshot()); };
        pause.CheckedChanged += (_, _) => { lastSequence = -1; };
        timer.Tick += (_, _) => RefreshState();
        FormClosing += OnClosing;
        engine.PathMapRequested += view =>
        {
            if (IsHandleCreated) BeginInvoke(() => ShowPathMap(view));
        };
        try { profile = store.Load(); profile.Validate(); ApplyProfile(profile); }
        catch (Exception e) { ApplyProfile(new()); engine.Telemetry.Log("Kayıtlı profil yüklenemedi: " + e.Message, "UYARI"); }
        engine.Telemetry.Log("Zeus hazır. Hedef sunucuyu ayarlayabilir veya yerel bağlantı testini çalıştırabilirsin.");
        ShowPage("overview"); RefreshState(); timer.Start();
        if (autoStart) Shown += async (_, _) => await StartProxy();
    }
    private void ShowPathMap(PathMapView view)
    {
        if (closing || !engine.Running) return;
        if (pathMap is { IsDisposed: false }) pathMap.Close();
        var picker = new PathMapForm(view, (x, y) => engine.QueuePathTarget(view.Id, x, y));
        pathMap = picker;
        picker.FormClosed += (_, _) => { if (pathMap == picker) pathMap = null; };
        picker.Show();
        picker.Activate();
    }
    private Panel BuildOverview()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
        layout.RowStyles.Add(new(SizeType.Absolute, 140)); layout.RowStyles.Add(new(SizeType.Absolute, 218));
        layout.RowStyles.Add(new(SizeType.Absolute, 60)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        metrics.ColumnStyles.Clear(); for (int i = 0; i < 4; i++) metrics.ColumnStyles.Add(new(SizeType.Percent, 25));
        string[] labels = ["AKTARILAN PAKET", "TOPLAM TRAFİK", "SUNUCU GECİKMESİ", "ENGELLENEN PAKET"];
        Label[] values = [packets, bytes, latency, dropped];
        for (int i = 0; i < 4; i++)
        {
            var card = Card(); var title = Theme.Label(labels[i], 8, Theme.Muted, true); title.Dock = DockStyle.Top; title.Height = 25;
            values[i].Dock = DockStyle.Fill; card.Controls.Add(values[i]); card.Controls.Add(title); metrics.Controls.Add(card, i, 0);
        }
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        detail.ColumnStyles.Add(new(SizeType.Percent, 62)); detail.ColumnStyles.Add(new(SizeType.Percent, 38));
        var connection = Card(); var ct = Theme.Label("BAĞLANTI", 9, Theme.Muted, true); ct.Dock = DockStyle.Top; ct.Height = 30;
        target.Dock = DockStyle.Top; target.Height = 38;
        var note = Theme.Label("Önce hedef sunucuyu ayarla, sonra proxy'yi başlat.\nİstemcini 127.0.0.1 üzerindeki yerel porta yönlendir.", 10, Theme.Muted); note.Dock = DockStyle.Fill;
        var change = Theme.Button("Ayarları düzenle"); change.Dock = DockStyle.Bottom; change.Height = 35; change.Click += (_, _) => ShowPage("settings");
        connection.Controls.Add(note); connection.Controls.Add(target); connection.Controls.Add(ct); connection.Controls.Add(change);
        var sessionCard = Card(); var st = Theme.Label("OTURUM", 9, Theme.Muted, true); st.Dock = DockStyle.Top; st.Height = 30;
        session.Dock = DockStyle.Fill; sessionCard.Controls.Add(session); sessionCard.Controls.Add(st);
        detail.Controls.Add(connection, 0, 0); detail.Controls.Add(sessionCard, 1, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) };
        test.Width = 185; actions.Controls.AddRange([start, stop, test]);
        var graphCard = Card(); var graphTitle = Theme.Label("CANLI TRAFİK  /  KB/s", 9, Theme.Muted, true); graphTitle.Dock = DockStyle.Top; graphTitle.Height = 27;
        graphCard.Controls.Add(graph); graphCard.Controls.Add(graphTitle);
        layout.Controls.Add(metrics, 0, 0); layout.Controls.Add(detail, 0, 1); layout.Controls.Add(actions, 0, 2); layout.Controls.Add(graphCard, 0, 3);
        page.Controls.Add(layout); return page;
    }
    private Panel BuildTraffic()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var bar = new Panel { Dock = DockStyle.Top, Height = 60 };
        filter.Dock = DockStyle.None; filter.PlaceholderText = "Tür veya yön ara…"; filter.SetBounds(0, 8, 290, 30);
        pause.SetBounds(312, 12, 180, 25);
        var export = Theme.Button("CSV dışa aktar"); export.SetBounds(540, 4, 155, 36); export.Click += (_, _) => ExportTraffic();
        bar.Controls.AddRange([filter, pause, export]);
        foreach (string column in new[] { "Saat", "Yön", "Bayt", "Kanal", "Paket türü" }) traffic.Columns.Add(column, column);
        traffic.Columns[0].FillWeight = 65; traffic.Columns[1].FillWeight = 125; traffic.Columns[2].FillWeight = 45; traffic.Columns[3].FillWeight = 45; traffic.Columns[4].FillWeight = 190;
        var hint = Theme.Label("Son 1.000 paket • İçerik, parola, sohbet ve token kaydedilmez. Duraklatma yalnızca görünümü etkiler.", 9, Theme.Muted);
        hint.Dock = DockStyle.Bottom; hint.Height = 38;
        page.Controls.Add(traffic); page.Controls.Add(bar); page.Controls.Add(hint); return page;
    }
    private Panel BuildLogs()
    {
        foreach (string column in new[] { "Saat", "Seviye", "Olay" }) logs.Columns.Add(column, column);
        logs.Columns[0].FillWeight = 65; logs.Columns[1].FillWeight = 65; logs.Columns[2].FillWeight = 400;
        var page = new Panel { Dock = DockStyle.Fill }; page.Controls.Add(logs); return page;
    }
    private void BuildSettings()
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, Height = 600, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new(SizeType.Percent, 50)); columns.ColumnStyles.Add(new(SizeType.Percent, 50));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 15, 0) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        left.Controls.Add(Heading("HEDEF VE YEREL BAĞLANTI"));
        AddField(left, "Sunucu adresi / IPv4", host); AddField(left, "Sunucu UDP portu", remotePort); AddField(left, "Zeus yerel UDP portu", localPort);
        left.Controls.Add(envelope); left.Controls.Add(commands);
        left.Controls.Add(Heading("İSTEMCİ BAŞLATMA SERVİSİ")); left.Controls.Add(bootstrap); left.Controls.Add(tls);
        AddField(left, "Yerel HTTP / HTTPS portu", bootstrapPort); AddField(left, "Meta (sunucu yanıtından)", meta);
        right.Controls.Add(Heading("SUNUCU KEŞFİ"));
        var discoveryNote = Theme.Label("Sunucunun HTTPS yanıtından adres, port ve meta alır.\nHosts yönlendirmesinden önce kullan.", 9, Theme.Muted); discoveryNote.Size = new Size(410, 45); right.Controls.Add(discoveryNote);
        AddField(right, "server_data HTTPS adresi", discoveryUrl); AddField(right, "POST verisi (sürüm / protokol; gerekiyorsa)", discoveryBody);
        var fetch = Theme.Button("Sunucu bilgisini al"); fetch.Width = 200; fetch.Click += async (_, _) => await Discover(fetch); right.Controls.Add(fetch);
        right.Controls.Add(Heading("FAVORİ DÜNYALAR")); AddField(right, "Virgülle ayır: ZEUS, START", bookmarks);
        right.Controls.Add(Heading("PROFİL VE SERTİFİKA"));
        var save = Theme.Button("Ayarları kaydet", true); save.Width = 200;
        save.Click += (_, _) => Guard(() => { profile = ReadProfile(); store.Save(profile); Notice("Ayarlar kaydedildi."); });
        right.Controls.Add(save);
        var cert = Theme.Button("TLS sertifikasını dışa aktar"); cert.Width = 260; cert.Margin = new Padding(0, 14, 0, 0); cert.Click += (_, _) => ExportCertificate(); right.Controls.Add(cert);
        var data = Theme.Button("Profil klasörünü aç"); data.Width = 220; data.Margin = new Padding(0, 14, 0, 0);
        data.Click += (_, _) => Guard(() => { Directory.CreateDirectory(store.DirectoryPath); Process.Start(new ProcessStartInfo("explorer.exe", store.DirectoryPath) { UseShellExecute = true }); }); right.Controls.Add(data);
        var certNote = Theme.Label("Sertifika otomatik olarak güvenilir yapılmaz.\nKurulum ve bağlantı sınırları rehberde açıklanır.", 9, Theme.Muted); certNote.Size = new Size(410, 55); certNote.Margin = new Padding(0, 16, 0, 0); right.Controls.Add(certNote);
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0); settingsPage.Controls.Add(columns);
    }
    private Panel BuildGuide()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var text = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 11), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical
        };
        text.Text = "ZEUS PROXY  /  BAŞLANGIÇ\n\n" +
            "1   Önce yerel bağlantı testini çalıştır.\n" +
            "Genel bakış → Yerel bağlantı testi, gerçek ENet trafiğini iki yönde ve parçalı paketle sınar. Growtopia hesabı veya internet gerekmez.\n\n" +
            "2   Hedef sunucuyu ayarla.\n" +
            "Bağlantı ayarlarında gerçek sunucu adresini ve UDP portunu gir. Resmî sunucu bilgisini almak için sunucu keşfini hosts yönlendirmesinden önce çalıştır. Yanıt alınamazsa sürüm/protokol gereksinimlerini kontrol et.\n\n" +
            "3   Proxy'yi başlat ve istemcini yönlendir.\n" +
            "Doğrudan bağlantı destekleyen istemciler için 127.0.0.1 ve yerel UDP portunu kullan. server_data kullanan istemciler için yerel servisi etkinleştir. Standart HTTPS bağlantıları genellikle 443 portunu gerektirir.\n\n" +
            "RESMÎ WINDOWS İSTEMCİSİ\n" +
            "22 Kasım 2026 tarihinde Windows istemcisi 5.62 ile resmî giriş, dünya yükleme ve /ping yanıtı doğrulandı. Resmî bağlantı kısayolu canlı sunucu keşfini ve yerel OpenSSL katmanını başlatır. Diğer istemci sürümleri ayrıca doğrulanmalıdır.\n" +
            "Uygulama hosts dosyasını değiştirmez. Gerekirse uygulamanın yanındaki Hosts.ps1 betiği, yönetici PowerShell'de -Action Install ile yalnızca Zeus bloğunu ekler; -Action Remove ile kaldırır. Önce sunucu keşfini tamamla. Betik yedek alır ve mevcut satırları korur.\n" +
            "HTTPS sertifikası kullanıcıya özel üretilir; gizli anahtar Windows kullanıcı sertifika deposunda kalır. Dışa aktarma yalnızca açık sertifikayı verir. Otomatik güven veya sertifika denetimini kapatma uygulanmaz.\n\n" +
            "OYUN İÇİ KOMUTLAR\n" +
            "/proxy            Proxy ve komutlar hakkında bilgi\n/options          Özellikleri hızlıca aç/kapat\n/status           Bağlantı ve dünya\n/ping             İsminin solunda renkli ping etiketi (aç/kapat)\n/pos              Karo konumu\n/fav              Favori dünyalar\n/warp AD          Normal dünya geçiş isteği\n/back             Önceki dünyaya geçiş isteği\n/path             Shift + sol tık ile hedef seçimini aç/kapat\n/path map         Karo haritasını aç\n/path X Y         Hedef karonun koordinatını gir\n/relog            Dünyadan çıkıp tekrar girer\n/antigravity      Düşük yerçekimi (aç/kapat; on/off ile kesin)\n/reme             Rulet sonucunun rakam toplamını yazar (aç/kapat)\n/qq               Rulet sonucunun son rakamını yazar (aç/kapat)\n/keep             Oyun içi ayarları kalıcı kaydeder\nReme ve QQ aynı anda açık olabilir. Rulet sonuçları dönüş bitmeden anında yazılır.\nSınırsız uzaklaştırma her zaman açıktır.\nTanı kaydı: %LOCALAPPDATA%\\ZeusProxy\\zeus.log\n(Eski /zeus ... yazımı da çalışır. /help gibi diğer komutlar oyuna gider.)\n\n" +
            "Veriler %LOCALAPPDATA%\\ZeusProxy altında tutulur. Trafik paneli yalnızca paket türü, boyut, yön ve kanalı gösterir. Giriş bilgileri, sohbet ve ham paket içeriği kaydedilmez.\n\n" +
            "Durdur düğmesi tüm Zeus bağlantılarını ve yerel server_data servisini kapatır. Hosts betiğini kullandıysan oyunu doğrudan açmadan önce Zeus bloğunu kaldır.\n";
        page.Controls.Add(text); return page;
    }
    private async Task StartProxy()
    {
        if (busy) return; busy = true; RefreshState();
        try { profile = ReadProfile(); store.Save(profile); await engine.StartAsync(profile); Notice("Proxy başlatıldı. Yerel istemci bağlantısı bekleniyor."); }
        catch (Exception e) { Notice(e.Message, true); }
        finally { busy = false; RefreshState(); }
    }
    private async Task StopProxy()
    {
        if (busy) return; busy = true; RefreshState();
        pathMap?.Close();
        try { await engine.StopAsync(); Notice("Proxy durduruldu."); }
        catch (Exception e) { Notice(e.Message, true); }
        finally { busy = false; RefreshState(); }
    }
    private async Task Diagnose()
    {
        test.Enabled = false; Notice("Yerel istemci ve test sunucusu arasında gerçek trafik doğrulanıyor…");
        try { string result = await Task.Run(() => LocalDiagnostics.RunAsync()); engine.Telemetry.Log(result); Notice(result); }
        catch (Exception e) { engine.Telemetry.Log("Yerel test: " + e.Message, "HATA"); Notice(e.Message, true); }
        finally { test.Enabled = true; }
    }
    private async Task Discover(Button sender)
    {
        sender.Enabled = false; Notice("HTTPS üzerinden sunucu bilgisi alınıyor…");
        try
        {
            var result = await Discovery.FetchAsync(ReadProfile());
            profile = result; ApplyProfile(result); Notice("Sunucu bilgisi alındı. Ayarları kaydedebilirsin.");
        }
        catch (Exception e) { Notice("Keşif başarısız: " + e.Message, true); }
        finally { sender.Enabled = true; }
    }
    private Profile ReadProfile()
    {
        if (!int.TryParse(remotePort.Text, out int remote) || !int.TryParse(localPort.Text, out int local) || !int.TryParse(bootstrapPort.Text, out int web))
            throw new ArgumentException("Port alanlarına sayı gir.");
        var result = profile with
        {
            ServerHost = host.Text.Trim(), ServerPort = remote, LocalPort = local, UbisoftEnvelope = envelope.Checked,
            EnableBootstrap = bootstrap.Checked, BootstrapTls = tls.Checked, BootstrapPort = web, Meta = meta.Text.Trim(),
            EnableCommands = commands.Checked, DiscoveryUrl = discoveryUrl.Text.Trim(), DiscoveryBody = discoveryBody.Text,
            Bookmarks = bookmarks.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToUpperInvariant()).Distinct().ToArray()
        };
        result.Validate(); return result;
    }
    private void ApplyProfile(Profile p)
    {
        host.Text = p.ServerHost; remotePort.Text = p.ServerPort.ToString(); localPort.Text = p.LocalPort.ToString();
        envelope.Checked = p.UbisoftEnvelope; commands.Checked = p.EnableCommands;
        bootstrap.Checked = p.EnableBootstrap; tls.Checked = p.BootstrapTls; bootstrapPort.Text = p.BootstrapPort.ToString();
        meta.Text = p.Meta; meta.UseSystemPasswordChar = true;
        bookmarks.Text = string.Join(", ", p.Bookmarks); discoveryUrl.Text = p.DiscoveryUrl; discoveryBody.Text = p.DiscoveryBody;
    }
    private void RefreshState()
    {
        var s = engine.Telemetry.Snapshot();
        if ((DateTime.UtcNow - lastStatusWrite).TotalSeconds >= 1)
        {
            lastStatusWrite = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(store.DirectoryPath);
                File.WriteAllText(Path.Combine(store.DirectoryPath, "connection-status.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { Version = AppVersion, s.State, s.Endpoint, s.SentPackets, s.ReceivedPackets, s.DroppedPackets, s.World, s.Position, Logs = s.Logs.TakeLast(80).ToArray() }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        start.Enabled = !busy && !engine.Running; stop.Enabled = !busy && engine.Running; settingsPage.Enabled = !busy && !engine.Running;
        status.Text = "●  " + (busy ? "İŞLENİYOR" : s.State.ToUpper(new CultureInfo("tr-TR")));
        status.ForeColor = s.State == "Bağlı" ? Theme.Green : s.State == "Hata" ? Theme.Red : Theme.Accent;
        packets.Text = (s.SentPackets + s.ReceivedPackets).ToString("N0"); bytes.Text = FormatBytes(s.SentBytes + s.ReceivedBytes);
        latency.Text = s.State == "Bağlı" ? s.Rtt + " ms" : "—"; dropped.Text = s.DroppedPackets.ToString("N0");
        target.Text = engine.Running ? s.Endpoint : $"{host.Text}:{remotePort.Text}";
        session.Text = $"Dünya     {s.World}\n\nKonum    {s.Position}     ·     Oyuncu  {s.Players}";
        long total = s.SentBytes + s.ReceivedBytes; var now = DateTime.UtcNow;
        double delta = (now - lastTick).TotalSeconds;
        if (delta > .1) { graph.Add(engine.Running ? Math.Max(0, total - lastBytes) / 1024d / delta : 0); lastTick = now; lastBytes = total; }
        if (currentPage == "traffic") RefreshTraffic(s);
        if (currentPage == "logs") RefreshLogs(s);
    }
    private void RefreshLogs(SessionSnapshot s)
    {
        var last = s.Logs.LastOrDefault();
        int count = s.Logs.Length;
        DateTimeOffset time = last?.Time ?? default;
        string message = last?.Message ?? "";
        if (count == lastLogCount && time == lastLogTime && message == lastLogMessage) return;
        lastLogCount = count; lastLogTime = time; lastLogMessage = message;
        logs.Rows.Clear();
        foreach (var row in s.Logs) logs.Rows.Add(row.Time.ToString("HH:mm:ss"), row.Level, row.Message);
        if (logs.Rows.Count > 0) logs.FirstDisplayedScrollingRowIndex = logs.Rows.Count - 1;
    }
    private void RefreshTraffic(SessionSnapshot s)
    {
        long sequence = s.Traffic.LastOrDefault()?.Sequence ?? 0;
        if (pause.Checked || lastSequence == sequence) return;
        lastSequence = sequence;
        traffic.Rows.Clear(); string query = filter.Text.Trim();
        foreach (var row in s.Traffic.Where(r => query.Length == 0 || r.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Direction.Contains(query, StringComparison.OrdinalIgnoreCase)))
            traffic.Rows.Add(row.Time.ToString("HH:mm:ss.fff"), row.Direction, row.Bytes, row.Channel, row.Summary);
        if (traffic.Rows.Count > 0) traffic.FirstDisplayedScrollingRowIndex = traffic.Rows.Count - 1;
    }
    private void ExportTraffic() => Guard(() =>
    {
        using var dialog = new SaveFileDialog { Filter = "CSV dosyası|*.csv", FileName = "zeus-trafik.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var csv = new StringBuilder("Saat,Yon,Bayt,Kanal,Tur\r\n");
        foreach (var r in engine.Telemetry.Snapshot().Traffic)
            csv.AppendLine($"{r.Time:O},{r.Direction},{r.Bytes},{r.Channel},\"{r.Summary.Replace("\"", "\"\"")}\"");
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true)); Notice("Trafik özeti dışa aktarıldı.");
    });
    private void ExportCertificate() => Guard(() =>
    {
        using var dialog = new SaveFileDialog { Filter = "Açık sertifika|*.cer", FileName = "ZeusProxy-Local.cer" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        using var cert = CertificateManager.GetOrCreate(); File.WriteAllBytes(dialog.FileName, cert.Export(X509ContentType.Cert));
        Notice("Açık sertifika dışa aktarıldı; güven ayarı değiştirilmedi.");
    });
    private void ShowPage(string key)
    {
        currentPage = key;
        if (key == "logs") lastLogCount = -1;
        foreach (var (name, page) in pages) page.Visible = name == key;
        foreach (var (name, button) in navigation) { button.BackColor = name == key ? Theme.Card : Theme.Nav; button.ForeColor = name == key ? Theme.Accent : Theme.Muted; }
        pageTitle.Text = key switch { "traffic" => "Paket inceleyici", "settings" => "Bağlantı ayarları", "logs" => "Olay günlüğü", "guide" => "Kurulum rehberi", _ => "Kontrol merkezi" };
        pageSubtitle.Text = key switch { "traffic" => "Trafiğin akışını izle. Hassas içerik kayıtlara alınmaz.", "settings" => "Kendi bağlantını yapılandır, profilini sakla.", "logs" => "Bağlantı olayları, uyarılar ve test sonuçları.", "guide" => "İlk bağlantıdan oyun içi komutlara.", _ => "Bağlantın, trafiğin ve oturumun tek yerde." };
        RefreshState();
    }
    private void Notice(string text, bool error = false) { notice.Text = text; notice.ForeColor = error ? Theme.Red : Theme.Muted; }
    private void Guard(Action action) { try { action(); } catch (Exception e) { Notice(e.Message, true); } }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; Enabled = false; timer.Stop();
        try
        {
            pathMap?.Close();
            mouseWatcher.Dispose();
            await engine.DisposeAsync();
        }
        finally { timer.Dispose(); Close(); }
    }
    private static string FormatBytes(long value) => value >= 1024 * 1024 ? $"{value / 1048576d:0.0} MB" : value >= 1024 ? $"{value / 1024d:0.0} KB" : value + " B";
    private static CheckBox Check(string title) => new() { Text = title, AutoSize = false, Width = 410, Height = 29, ForeColor = Theme.Text, Margin = new Padding(0, 2, 0, 2) };
    private static Panel Card() => new() { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(18), Margin = new Padding(0, 0, 12, 14) };
    private static Label Heading(string title) { var label = Theme.Label(title, 9, Theme.Accent, true); label.Size = new Size(410, 35); label.Margin = new Padding(0, 10, 0, 4); return label; }
    private static void AddField(FlowLayoutPanel panel, string title, TextBox input)
    {
        var wrapper = new Panel { Width = 400, Height = 63, Margin = new Padding(0, 0, 0, 3) };
        var label = Theme.Label(title, 9, Theme.Muted); label.Dock = DockStyle.Top; label.Height = 23;
        input.Dock = DockStyle.Top; wrapper.Controls.Add(input); wrapper.Controls.Add(label); panel.Controls.Add(wrapper);
    }
    private static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill, BackgroundColor = Theme.Card, BorderStyle = BorderStyle.None,
        ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        GridColor = Theme.Border, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        RowTemplate = { Height = 32 }, ColumnHeadersHeight = 38, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        DefaultCellStyle = new() { BackColor = Theme.Card, ForeColor = Theme.Text, SelectionBackColor = Theme.Input, SelectionForeColor = Theme.Accent, Padding = new Padding(8, 0, 0, 0) },
        ColumnHeadersDefaultCellStyle = new() { BackColor = Theme.Input, ForeColor = Theme.Muted, Padding = new Padding(8, 0, 0, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) }
    };
}

internal sealed class TrafficGraph : Control
{
    private readonly Queue<double> samples = new();
    public TrafficGraph() { DoubleBuffered = true; BackColor = Theme.Card; }
    public void Add(double sample) { samples.Enqueue(sample); while (samples.Count > 90) samples.Dequeue(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Width < 70 || Height < 40) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 12, Width - 54, Height - 36);
        using var grid = new Pen(Theme.Border);
        for (int i = 0; i < 4; i++) { float y = rect.Top + rect.Height * i / 3f; e.Graphics.DrawLine(grid, 0, y, rect.Right, y); }
        double maximum = Math.Max(1, samples.DefaultIfEmpty().Max() * 1.2);
        using var brush = new SolidBrush(Theme.Muted);
        e.Graphics.DrawString($"{maximum:0.0}", Font, brush, rect.Right + 7, rect.Top - 7);
        e.Graphics.DrawString("0", Font, brush, rect.Right + 7, rect.Bottom - 7);
        e.Graphics.DrawString("Son 45 saniye", Font, brush, 0, Height - 20);
        if (samples.Count < 2) return;
        var points = samples.Select((s, i) => new PointF(rect.Right - (samples.Count - 1 - i) * rect.Width / 89f,
            rect.Bottom - (float)(s / maximum * rect.Height))).ToArray();
        using var line = new Pen(Theme.Accent, 2); e.Graphics.DrawLines(line, points);
    }
}
