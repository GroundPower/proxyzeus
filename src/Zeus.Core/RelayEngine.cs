using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Zeus.Core;

public sealed record PathMapView(long Id, string World, int Width, int Height, int PlayerX, int PlayerY, bool[] OpenTiles);

public sealed class RelayEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentQueue<string> commands = new();
    private readonly ConcurrentQueue<(long MapId, int X, int Y)> pathTargets = new();
    private long lastMapId;
    private volatile bool pathZoomActive;
    /// <summary>True while path controls the game's zoom: the desktop app then hands wheel turns over
    /// the game window to Zeus instead of the game.</summary>
    public bool PathZoomActive => pathZoomActive;
    public event Action<PathMapView>? PathMapRequested;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    public Telemetry Telemetry { get; } = new();
    public bool Running => worker is { IsCompleted: false };
    /// <summary>True for self-tests so they never alter global desktop settings, mouse events or PathEnabled.</summary>
    public bool Isolated { get; init; }
    /// <summary>Where /keep stores in-game settings (set by the desktop app; null keeps them in memory only).</summary>
    public static string? SettingsPath { get; set; }
    /// <summary>The game's items.dat, read for /path (collision types).</summary>
    public static string ItemsPath { get; set; } = ItemDatabase.DefaultPath;
    /// <summary>Where a world map that could not be read is saved for diagnosis (local only).</summary>
    public static string? MapDumpDirectory { get; set; }
    /// <summary>True while Shift is held (set by the desktop app); Shift + click walks with /path.</summary>
    public static Func<bool>? IsShiftDown { get; set; }
    /// <summary>Clicks and wheel turns inside the game window, from the desktop app's mouse hook.</summary>
    public static readonly ConcurrentQueue<MouseEvent> MouseEvents = new();
    /// <summary>True while the desktop app watches the game window's mouse (path reads the cursor itself).</summary>
    public static bool MouseHookActive { get; set; }
    /// <summary>The game's current zoom value read from its memory by the desktop app (null while not located).</summary>
    public static Func<double?>? GameZoom { get; set; }
    /// <summary>Where locating the game's zoom stands, for the diagnostic log.</summary>
    public static Func<string>? GameZoomStatus { get; set; }
    /// <summary>A recent picture of the game window's client area (from the desktop app).</summary>
    public static Func<ScreenFrame?>? GameFrame { get; set; }
    /// <summary>Where the window picture stands, for the diagnostic log.</summary>
    public static Func<string>? GameFrameStatus { get; set; }
    /// <summary>The local player's position read from the game's memory by the desktop app.</summary>
    public static Func<(float X, float Y)?>? GamePosition { get; set; }
    /// <summary>True while path is on; the desktop app only captures the window then.</summary>
    public static volatile bool PathEnabled;
    /// <summary>How long a Shift click waits for the client's own report of the clicked tile.</summary>
    private const int ShiftWaitMs = 150;
    /// <summary>Clicks this close to the player are always reported exactly by the client (no clamping).</summary>
    private const int SafeReach = 2;

    public async Task StartAsync(Profile profile)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (Running) throw new InvalidOperationException("Proxy zaten çalışıyor.");
            profile.Validate();
            while (commands.TryDequeue(out _)) { }
            pathTargets.Clear();
            if (!Isolated) MouseEvents.Clear();
            cancellation?.Dispose(); cancellation = new();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Telemetry.Reset();
            worker = Task.Run(() => RunAsync(profile, ready, cancellation.Token));
            await ready.Task;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            cancellation?.Cancel();
            if (worker != null) await worker;
        }
        finally { lifecycle.Release(); }
    }

    public bool QueueCommand(string text)
    {
        if (!Running || text.Length > 128 || commands.Count >= 32) return false;
        commands.Enqueue(text); return true;
    }

    public bool QueuePathTarget(long mapId, int x, int y)
    {
        if (!Running || pathTargets.Count >= 8) return false;
        pathTargets.Enqueue((mapId, x, y));
        return true;
    }

    public static async Task<string> ResolveAsync(string hostname, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var addresses = await Dns.GetHostAddressesAsync(hostname, timeout.Token);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
            ?? throw new IOException("Sunucunun IPv4 adresi bulunamadı.");
    }

    private async Task RunAsync(Profile profile, TaskCompletionSource ready, CancellationToken token)
    {
        NativeHost? local = null, upstream = null;
        BootstrapServer? bootstrap = null;
        var pending = new Queue<NativeEvent>();
        var discoveredEndpoints = new ConcurrentQueue<Profile>();
        int pendingBytes = 0;
        string initialIp = "", targetIp = "", world = "—", previousWorld = "", position = "—";
        ushort targetPort = (ushort)profile.ServerPort;
        bool redirecting = false;
        var players = new HashSet<int>();
        long connectingAt = 0, redirectAt = 0;
        bool notedLocalDatagrams = false;

        // Session-wide toggles survive server/world transitions inside this run.
        var kept = Isolated ? new GameSettings() : GameSettings.Load(SettingsPath);
        bool remeMode = kept.Reme, qqMode = kept.Qq, pingTag = kept.PingTag, antiGravity = kept.AntiGravity, pathMode = kept.Path;
        if (!Isolated) PathEnabled = pathMode;

        byte[]? lastCharacterState = null; // the server's latest SET_CHARACTER_STATE for the local player

        // Path sets the game's zoom itself, so the camera model knows the scale of every level and maps
        // a click in the game window to a tile by arithmetic alone. Learned values persist (camera.json).
        string? cameraPath = Isolated || SettingsPath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!, "camera.json");
        var camera = new CameraModel(CameraMemory.Load(cameraPath));
        camera.Learned += memory =>
        {
            if (cameraPath != null) memory.Save(cameraPath);
            Telemetry.Log($"Path: kamera öğrenildi (seviye 0 ölçeği {memory.Scale0:0.00}, seviye katsayısı {memory.StepFactor:0.0000}, kayma {memory.OffsetX:0.00},{memory.OffsetY:0.00}).", "TANI");
        };

        int zoomLevel = 0; double wheelRemainder = 0;
        MouseEvent? pendingClick = null; double pendingClickAt = 0, cameraSettlesAt = 0;
        double pendingPlayerX = 0, pendingPlayerY = 0;

        // Targets the client itself reported for a click (tile change, or the punch target in a
        // state packet), with the time they arrived. A Shift click waits briefly for one of these.
        var reportedTargets = new List<(double At, int X, int Y)>();
        int[]? locatorKeys = null; long locatorKeysMapId = 0;
        long locateGen = 0, pendingLocateGen = 0;
        Task<(ScreenFit? Fit, string Note)>? pendingLocate = null;
        long worldGen = 0, pathWalkGen = 0;
        double lastMemoryPosCheckAt = 0;
        MouseEvent? pendingShift = null; double pendingShiftAt = 0, pendingShiftPlayerX = 0, pendingShiftPlayerY = 0;
        float pendingShiftVelX = 0, pendingShiftVelY = 0;
        double? pendingShiftZoom = null, pendingClickZoom = null; // the game's zoom at the moment of the click
        double suppressTileUntil = 0;

        // Millisecond clock with sub-millisecond resolution (TickCount64 ticks only every ~15 ms).
        static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        float posX = 0, posY = 0, velX = 0, velY = 0; double lastMovePacketAt = 0; // local player position and velocity in pixels
        double walkLockUntil = 0; float walkLockX = 0, walkLockY = 0;
        WorldMap? map = null; uint userId = 0;
        long mapId = 0, mouseWindow = 0;
        int mouseWidth = 0, mouseHeight = 0;
        bool positionKnown = false;
        ItemDatabase? itemDb = null; string itemDbError = ""; DateTime itemDbStamp = default;

        ItemDatabase? Items()
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(ItemsPath);
                if (itemDb != null && stamp == itemDbStamp) return itemDb;
                itemDb = ItemDatabase.Load(ItemsPath); itemDbStamp = stamp; itemDbError = "";
                Telemetry.Log($"items.dat okundu: sürüm {itemDb.Version}, {itemDb.Items.Count} eşya.", "TANI");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                if (itemDbError != error.Message) Telemetry.Log("items.dat okunamadı: " + error.Message, "TANI");
                itemDb = null; itemDbError = error.Message;
            }
            return itemDb;
        }

        var timers = new List<(long At, Action Run)>(); // delayed actions, run on the session loop
        void Later(int milliseconds, Action run) => timers.Add((Environment.TickCount64 + milliseconds, run));
        var seenFunctions = new HashSet<string>(); // names only, for the diagnostic log
        int localNetId = 0; string localName = "", shownTag = ""; long tagCheckedAt = 0;
        bool welcomed = false; // once per login, not per server/world transition
        int notedFormat = 0;

        try
        {
            initialIp = targetIp = await ResolveAsync(profile.ServerHost, token);
            if (IPAddress.IsLoopback(IPAddress.Parse(initialIp)) && targetPort == profile.LocalPort)
                throw new IOException("Hedef sunucu yerel proxy portuna işaret ediyor.");
            local = new NativeHost((ushort)profile.LocalPort, detectIncomingPrefix: true);
            if (profile.EnableBootstrap)
            {
                bootstrap = new BootstrapServer(() => Telemetry.Log("İstemci yerel server_data bilgisini aldı."), message => Telemetry.Log(message, "TANI"), discoveredEndpoints.Enqueue);
                await bootstrap.StartAsync(profile, token);
            }
            Telemetry.State("İstemci bekleniyor", $"{profile.ServerHost}:{targetPort}");
            Telemetry.Log($"Zeus dinliyor · 127.0.0.1:{profile.LocalPort} · {(profile.UbisoftEnvelope ? "Ubisoft zarfı" : "Klasik ENet")}");
            ready.TrySetResult();

            while (!token.IsCancellationRequested)
            {
                while (discoveredEndpoints.TryDequeue(out var discovered))
                {
                    string discoveredIp = await ResolveAsync(discovered.ServerHost, token);
                    if (IPAddress.IsLoopback(IPAddress.Parse(discoveredIp)) && discovered.ServerPort == profile.LocalPort)
                        throw new IOException("Keşif yanıtı yerel bağlantı döngüsü oluşturuyor.");
                    profile = discovered;
                    if (redirecting)
                    {
                        upstream?.Dispose(); upstream = null;
                        local.Reset(); pending.Clear(); pendingBytes = 0;
                        Telemetry.Log("Yeni giriş isteği alındı; önceki dünya geçişi temizlendi.");
                    }
                    redirecting = false;
                    initialIp = targetIp = discoveredIp;
                    targetPort = (ushort)discovered.ServerPort;
                    Telemetry.State("İstemci bekleniyor", $"{targetIp}:{targetPort}");
                    Telemetry.Log("İstemcinin sürüm/protokol isteğiyle güncel resmî hedef alındı.");
                }

                // All native transport operations belong to this single serialized loop.
                for (int batch = 0; batch < 64; batch++)
                {
                    var e = local.Poll();
                    if (e == null) break;
                    if (e.Type == 1)
                    {
                        upstream?.Dispose();
                        upstream = new NativeHost(outgoingPrefix: profile.UbisoftEnvelope, bindAddress: "0.0.0.0");
                        upstream.Connect(targetIp, targetPort);
                        redirecting = false; connectingAt = Environment.TickCount64;
                        pending.Clear(); pendingBytes = 0; players.Clear(); world = position = "—";
                        localNetId = 0; shownTag = "";
                        ClearPathWorld();
                        Telemetry.State("Sunucuya bağlanıyor", $"{targetIp}:{targetPort}");
                        Telemetry.Log("Yerel istemci bağlandı.");
                    }
                    else if (e.Type == 2)
                    {
                        upstream?.Dispose(); upstream = null; pending.Clear(); pendingBytes = 0;
                        players.Clear(); world = position = "—";
                        ClearPathWorld();
                        if (!redirecting) { targetIp = initialIp; targetPort = (ushort)profile.ServerPort; welcomed = false; }
                        else redirectAt = Environment.TickCount64;
                        Telemetry.State("İstemci bekleniyor");
                        Telemetry.Log(redirecting ? "Sunucu geçişi için yeniden bağlantı bekleniyor." : "İstemci ayrıldı.");
                    }
                    else if (e.Type == 3)
                    {
                        DrainMouse();
                        Telemetry.Packet(true, e.Data, e.Channel);
                        if (!Valid(e.Data)) { Telemetry.Drop(); continue; }
                        if (profile.EnableCommands && Protocol.Type(e.Data) is 2 or 3 && InterceptText(e.Data)) continue;
                        if (Protocol.Type(e.Data) == 4 && e.Data.Length >= 60 && e.Data[4] == 0) NoteStateTarget(e.Data);
                        if (Protocol.Type(e.Data) == 4 && e.Data.Length >= 60 && e.Data[4] == 3)
                        {
                            NoteTarget(e.Data, tileRequest: true);
                            if (pathMode && InterceptClick()) continue;
                        }
                        Observe(e.Data, true);
                        if (upstream?.Connected == true) upstream.Send(e.Data, e.Channel, e.Flags);
                        else if (upstream != null && pending.Count < 256 && pendingBytes + e.Data.Length <= 4 * 1024 * 1024)
                        { pending.Enqueue(e); pendingBytes += e.Data.Length; }
                        else { Telemetry.Drop(); ResetSession("Bağlantı kuyruğu doldu; istemci yeniden bağlanmalı."); }
                    }
                }

                for (int batch = 0; upstream != null && batch < 64; batch++)
                {
                    var e = upstream.Poll();
                    if (e == null) break;
                    if (e.Type == 1)
                    {
                        Telemetry.State("Bağlı"); Telemetry.Log("Uzak sunucuyla bağlantı kuruldu.");
                        while (pending.TryDequeue(out var queued)) upstream.Send(queued.Data, queued.Channel, queued.Flags);
                        pendingBytes = 0;
                    }
                    else if (e.Type == 2)
                    {
                        if (!redirecting) ResetSession("Uzak sunucu bağlantıyı kapattı.");
                        else { upstream.Dispose(); upstream = null; }
                        break;
                    }
                    else if (e.Type == 3 && local.Connected)
                    {
                        Telemetry.Packet(false, e.Data, e.Channel);
                        if (!Valid(e.Data)) { Telemetry.Drop(); continue; }
                        byte[] forwarded = e.Data;
                        if (Protocol.TryRedirect(e.Data, (ushort)profile.LocalPort, out var rewritten, out var host, out var port))
                        {
                            string ip = await ResolveAsync(host, token);
                            if (IPAddress.IsLoopback(IPAddress.Parse(ip)) && port == profile.LocalPort)
                                throw new IOException("Sunucu yönlendirmesi bağlantı döngüsü oluşturuyor.");
                            targetIp = ip; targetPort = port; forwarded = rewritten;
                            redirecting = true; redirectAt = Environment.TickCount64;
                            ClearPathWorld();
                            Telemetry.Log("Sunucu geçişi alındı; erişim bilgileri bellekte korunuyor.");
                        }
                        if (Protocol.Type(forwarded) == 4 && forwarded.Length > 4 && forwarded[4] == 1)
                        {
                            string? function = GameFeatures.FunctionName(forwarded);
                            if (function != null && seenFunctions.Count < 512 && seenFunctions.Add(function))
                                Telemetry.Log($"Sunucu işlevi: {function}", "TANI");
                            if (function == "OnDialogRequest")
                            {
                                // Dialog name only (e.g. a tutorial window), never its contents.
                                string dialog = Protocol.ReadVariants(forwarded)?.FirstOrDefault(v => v.Index == 1)?.Text ?? "";
                                int end = dialog.LastIndexOf("end_dialog|", StringComparison.Ordinal);
                                string dialogName = end < 0 ? "?" : dialog[(end + 11)..].Split('|', 2)[0];
                                if (seenFunctions.Count < 512 && seenFunctions.Add("dialog:" + dialogName))
                                    Telemetry.Log($"Sunucu penceresi: {(dialogName.Length > 40 ? dialogName[..40] : dialogName)}", "TANI");
                            }
                            if (GameFeatures.IsTutorial(forwarded, function))
                            {
                                Telemetry.Log($"Tutorial paketi engellendi: {function}", "TANI");
                                Observe(e.Data, false);
                                continue;
                            }
                            forwarded = RewriteIncoming(forwarded);
                            if (function == "OnConsoleMessage") forwarded = GameFeatures.TagConsoleMessage(forwarded);
                        }
                        else forwarded = GameFeatures.TagLogText(forwarded);

                        if (Protocol.Type(forwarded) == 4 && forwarded.Length >= 60 && forwarded[4] == 20 && localNetId != 0
                            && BinaryPrimitives.ReadInt32LittleEndian(forwarded.AsSpan(8)) == localNetId)
                        {
                            if (lastCharacterState == null)
                                Telemetry.Log($"Sunucunun karakter durumu alındı: inşa menzili {forwarded[6]}, vuruş menzili {forwarded[7]}.", "TANI");
                            lastCharacterState = forwarded.ToArray();
                        }

                        Observe(e.Data, false);
                        local.Send(forwarded, e.Channel, e.Flags);
                        if (!welcomed && GameFeatures.IsLogonAccepted(e.Data))
                        {
                            welcomed = true;
                            string version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";
                            Reply($"`2Proxy bağlantısı başarıyla kuruldu! `wZeus Proxy v{version} `oaktif ve çalışıyor.");
                            Reply("`oKomutlar ve bilgi için `w/proxy`o, özellikleri açıp kapatmak için `w/options `oyazabilirsin.");
                            if (kept != new GameSettings())
                                Reply($"`oKayıtlı ayarların yüklendi: {SettingsSummary()}");
                            if (local.Connected) local.Send(GameFeatures.TextOverlayPacket($"`2Zeus Proxy v{version} `wbağlandı!"));
                        }
                    }
                }

                if (redirecting && Environment.TickCount64 - redirectAt > 30000) ResetSession("Sunucu geçişi zaman aşımına uğradı; yerel bağlantı yeniden hazırlandı.");
                if (upstream != null && !upstream.Connected && !redirecting && Environment.TickCount64 - connectingAt > 10000)
                    ResetSession("Sunucuya 10 saniyede ulaşılamadı. Adres, port ve ENet modunu kontrol et.");

                for (int i = 0; i < 8 && commands.TryDequeue(out string? command); i++) HandleCommand(command);
                DrainMouse();
                double now = Now();
                if (now - lastMemoryPosCheckAt >= 250 && now - lastMovePacketAt >= 250)
                {
                    lastMemoryPosCheckAt = now;
                    SyncMemoryPosition();
                }
                if (pendingShift != null) ResolveShift();
                while (pathTargets.TryDequeue(out var target))
                {
                    if (target.MapId != mapId || map == null) { PathResult("`4Dünya değişti. `o/path map ile yeni haritayı aç."); continue; }
                    WalkTo(target.X, target.Y);
                }

                for (int i = 0; i < timers.Count;)
                {
                    if (timers[i].At > Environment.TickCount64) { i++; continue; }
                    var run = timers[i].Run; timers.RemoveAt(i); run();
                }

                if (pingTag && Environment.TickCount64 - tagCheckedAt >= 1000) { tagCheckedAt = Environment.TickCount64; RefreshPingTag(); }
                local.Flush(); upstream?.Flush();

                if (!notedLocalDatagrams && local.ReceivedDatagrams > 0)
                {
                    notedLocalDatagrams = true;
                    Telemetry.Log("Oyun istemcisinin yerel UDP paketleri alındı.");
                }
                if (local.ReceiveFormat != 0 && notedFormat != local.ReceiveFormat)
                {
                    notedFormat = local.ReceiveFormat;
                    Telemetry.Log($"Yerel ENet biçimi: ön ek {notedFormat / 10 - 1} bayt, CRC {(notedFormat % 10 == 1 ? "açık" : "kapalı")}.");
                }
                Telemetry.Session(world, position, players.Count, upstream?.Connected == true ? upstream.Rtt : 0);
                await Task.Delay(2, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception error)
        {
            Telemetry.Log(error is SocketException ? "Sunucu adresi çözümlenemedi." : error.Message, "HATA");
            Telemetry.State("Hata"); ready.TrySetException(error);
        }
        finally
        {
            pathZoomActive = false;
            if (!Isolated) PathEnabled = false;
            upstream?.Dispose(); local?.Dispose();
            if (bootstrap != null) await bootstrap.DisposeAsync();
            if (token.IsCancellationRequested) Telemetry.State("Durduruldu");
            Telemetry.Session("—", "—", 0, 0);
            Telemetry.Log("Bağlantı kaynakları kapatıldı.");
        }

        bool Valid(byte[] data) => data.Length >= 4 && (Protocol.Type(data) != 4 || Protocol.ValidGamePacket(data));

        void ResetSession(string reason)
        {
            upstream?.Dispose(); upstream = null;
            local?.Dispose();
            local = new NativeHost((ushort)profile.LocalPort, detectIncomingPrefix: true);
            notedLocalDatagrams = false; notedFormat = 0;
            pending.Clear(); pendingBytes = 0; redirecting = false;
            targetIp = initialIp; targetPort = (ushort)profile.ServerPort;
            players.Clear(); world = position = "—"; welcomed = false; timers.Clear(); map = null; lastCharacterState = null;
            ClearPathWorld();
            Telemetry.State("İstemci bekleniyor"); Telemetry.Log(reason, "UYARI");
        }

        void Reply(string message) => GameLog(GameFeatures.ConsoleTag + "`o" + message);

        void GameLog(string message)
        {
            if (local?.Connected == true)
                local.Send(Protocol.TextPacket(3, "action|log\nmsg|" + GameFeatures.ToGameText(message)));
            Telemetry.Log(System.Text.RegularExpressions.Regex.Replace(message, "`.", ""));
        }

        bool InterceptText(byte[] data)
        {
            string text = Protocol.Text(data);
            string action = Protocol.Field(text, "action");
            if (action == "dialog_return" && Protocol.Field(text, "dialog_name").StartsWith("zeus_", StringComparison.Ordinal))
            { ApplyPanel(text); return true; }
            if (action != "input")
            {
                // Diagnostic only: a slash command in an unexpected packet shape.
                if (text.Contains("|text|/", StringComparison.Ordinal))
                    Telemetry.Log($"Beklenmeyen komut paketi: tür {Protocol.Type(data)}, action '{action}'.", "TANI");
                return false;
            }
            string chat = GameFeatures.CleanCommand(Protocol.Field(text, "text"));
            if (HandleCommand(chat)) return true;
            string first = chat.TrimStart().Split(' ', 2)[0];
            if (first.StartsWith('/'))
                Telemetry.Log($"Oyun komutu aktarıldı: {(first.Length > 24 ? first[..24] : first)} (tür {Protocol.Type(data)}).", "TANI");
            return false;
        }

        byte[] RewriteIncoming(byte[] packet)
        {
            if (GameFeatures.TryLocalSpawn(packet, pingTag ? CurrentTag : null, out var spawn, out int netId, out string name))
            {
                // Unlimited zoom is always on: the local copy of OnSpawn carries mod state 1.
                localNetId = netId; localName = name; shownTag = pingTag ? CurrentTag(name) : "";
                string spawnText = Protocol.ReadVariants(packet)?.FirstOrDefault(v => v.Index == 1)?.Text ?? "";
                if (GameFeatures.TrySpawnPosition(spawnText, out float sx, out float sy) && float.IsFinite(sx) && float.IsFinite(sy))
                {
                    posX = sx; posY = sy; positionKnown = true;
                    cameraSettlesAt = Now() + 350;
                }
                if (uint.TryParse(Protocol.Field(spawnText, "userID"), out uint uid)) userId = uid;
                pathZoomActive = pathMode && map != null;
                // A new world (or a server switch) may reset the camera: set path's zoom again.
                // A new world has no generator yet: put it back once the spawn has reached the client.
                if (antiGravity) Later(500, ApplyAntiGravity);
                return spawn;
            }
            if (GameFeatures.TryNameChanged(packet, out int changedId, out string changedName) && localNetId != 0 && changedId == localNetId)
            {
                localName = changedName;
                if (!pingTag) return packet;
                shownTag = CurrentTag(changedName);
                return GameFeatures.NameChangedPacket(localNetId, shownTag, packet);
            }
            if (!GameFeatures.LooksLikeSpin(packet)) return packet;
            if (!GameFeatures.TryRouletteSpin(packet, out var spin))
            {
                Telemetry.Log("Rulet mesajı ayrıştırılamadı.", "TANI");
                return packet;
            }
            bool own = spin.NetId != 0 && spin.NetId == localNetId;
            Telemetry.Log($"Rulet: {spin.Function}, netID {spin.NetId}{(own ? " (sen)" : "")}, gecikme {spin.Delay} ms, sayı {spin.Number}.", "TANI");
            // Genuine spins carry the animation delay (~1.8 s). Player-typed copies do not, except
            // that a bubble on our own netID cannot come from someone else.
            bool real = spin.Real || own;
            // The server's own line is rewritten (no extra message) with fixed colours:
            // "[REAL] [Name spun the wheel and got 4!] REME 4". Fake bubbles get a [FAKE] marker.
            if (real)
            {
                string formatted = GameFeatures.FormatRealSpin(spin, GameFeatures.SpinSuffix(spin.Number, remeMode, qqMode));
                packet = GameFeatures.ReplaceSpinText(packet, spin.Function, _ => formatted);
            }
            else if (spin.Function == "OnTalkBubble")
                packet = GameFeatures.ReplaceSpinText(packet, spin.Function, text => "`w[`4FAKE`w]`` " + text);
            // The server sends the result as the wheel starts; show it now instead of waiting.
            return real ? GameFeatures.WithoutDelay(packet) : packet;
        }

        string CurrentTag(string name) =>
            (upstream?.Connected == true ? GameFeatures.PingTag(upstream.Rtt) : "`b[--]`` ") + name;

        void RefreshPingTag()
        {
            if (localNetId == 0 || local?.Connected != true || localName.Length == 0) return;
            string tag = CurrentTag(localName);
            if (tag == shownTag) return;
            shownTag = tag;
            local.Send(GameFeatures.NameChangedPacket(localNetId, tag));
        }

        void SetPingTag(bool enabled)
        {
            if (pingTag == enabled) return;
            pingTag = enabled; tagCheckedAt = 0;
            if (!enabled && shownTag.Length > 0 && localNetId != 0 && local?.Connected == true)
                local.Send(GameFeatures.NameChangedPacket(localNetId, localName));
            shownTag = "";
            if (enabled) RefreshPingTag();
        }

        string T(string text) => GameFeatures.ToGameText(text);
        string Version() => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";
        string StatusLine()
        {
            bool connected = upstream?.Connected == true;
            uint rtt = connected ? upstream!.Rtt : 0;
            return $"add_smalltext|`ov{Version()}   {(connected ? "`2" + T("Bağlı") : "`4" + T("Bağlantı yok"))}   `oPing `{GameFeatures.PingColor(rtt)}{rtt} ms   `o{T("Dünya")} `w{T(world)}|";
        }

        // /proxy: about Zeus and every command, read-only.
        void ShowPanel()
        {
            if (local?.Connected != true) return;
            var dialog = new System.Text.StringBuilder();
            void Line(string line) => dialog.Append(line).Append('\n');
            void Help(string command, string description) => Line($"add_smalltext|`w{command} `o- {T(description)}|");
            Line("set_default_color|`o");
            Line("add_label_with_icon|big|`wZeus Proxy``|left|1790|");
            Line(StatusLine());
            Line("add_spacer|small|");
            Line($"add_textbox|{T("Zeus Proxy, oyun istemcisi ile sunucu arasında çalışan bir yardımcı yazılımdır. Rulet sonuçlarını anında gösterir, sınırsız kamera uzaklaştırması sağlar ve aşağıda listelenen komutları sunar.")}|left|");
            Line("add_spacer|small|");
            Line($"add_label_with_icon|small|`w{T("Paneller")}``|left|32|");
            Help("/proxy", "Proxy bilgilerini ve komut listesini görüntüler");
            Help("/options", "Özellik ayarları panelini açar");
            Line("add_spacer|small|");
            Line($"add_label_with_icon|small|`w{T("Özellikler")}``|left|758|");
            Help("/reme", "Reme modunu etkinleştirir veya devre dışı bırakır");
            Help("/qq", "QQ modunu etkinleştirir veya devre dışı bırakır");
            Help("/ping", "Ping göstergesini etkinleştirir veya devre dışı bırakır");
            Help("/antigravity", "Anti Gravity özelliğini etkinleştirir veya devre dışı bırakır");
            Help("/path", "Shift tıklamasını açar/kapatır. /path map haritayı açar, /path X Y hedef karoyu seçer.");
            Help("/keep", "Geçerli ayarları kalıcı olarak kaydeder");
            Line("add_spacer|small|");
            Line($"add_label_with_icon|small|`w{T("Dünya")}``|left|3802|");
            Help("/warp DUNYA", "Belirtilen dünyaya geçiş yapar");
            Help("/back", "Bir önceki dünyaya geri döner");
            Help("/relog", "Bulunulan dünyadan çıkıp yeniden giriş yapar");
            Help("/status  /pos  /fav", "Bağlantı durumu, karakter konumu ve favori dünyalar");
            Line("add_spacer|small|");
            Line($"add_button|zeus_open_options|`w{T("Ayarlar Paneli")}|noflags|0|0|");
            Line("add_quick_exit|");
            Line("end_dialog|zeus_proxy|Kapat||");
            local.Send(GameFeatures.DialogPacket(dialog.ToString()));
        }

        // /options: quick toggles, one line each, the explanation small in parentheses.
        void ShowOptions()
        {
            if (local?.Connected != true) return;
            var dialog = new System.Text.StringBuilder();
            void Line(string line) => dialog.Append(line).Append('\n');
            void Option(string id, string label, bool on, string hint) =>
                Line($"add_checkbox|{id}|`w{T(label)}  `o({T(hint)})|{(on ? "1" : "0")}|");
            void Section(string title, int icon) => Line($"add_label_with_icon|small|`w{T(title)}``|left|{icon}|");
            Line("set_default_color|`o");
            Line($"add_label_with_icon|big|`w{T("Zeus Ayarları")}``|left|1790|");
            Line(StatusLine());
            Line("add_spacer|small|");
            Section("Rulet Araçları", 758);
            Option("zeus_reme", "Reme Modu", remeMode, "Rulet sonuçlarında Reme değerini gösterir");
            Option("zeus_qq", "QQ Modu", qqMode, "Rulet sonuçlarında QQ değerini gösterir");
            Line("add_spacer|small|");
            Section("Görünüm", 482);
            Option("zeus_ping", "Ping Göstergesi", pingTag, "Bağlantı gecikmesini kullanıcı adının yanında gösterir");
            Line("add_spacer|small|");
            Section("Hareket", GameFeatures.AntiGravityItem);
            Option("zeus_antigravity", "Anti Gravity", antiGravity, "Karakter üzerindeki yerçekimi etkisini azaltır");
            Option("zeus_path", "Path", pathMode, "Shift + Sol Tık ile seçilen noktaya güvenli rota oluşturur");
            Line("add_spacer|small|");
            Line($"add_button|zeus_keep|`2{T("Kaydet ve Kalıcı Yap")}|noflags|0|0|");
            Line("add_quick_exit|");
            Line("end_dialog|zeus_options|Kapat|Uygula|");
            local.Send(GameFeatures.DialogPacket(dialog.ToString()));
        }

        string SettingsSummary() => $"Reme: {OnOff(remeMode)} - QQ: {OnOff(qqMode)} - Ping: {OnOff(pingTag)} - Anti Gravity: {OnOff(antiGravity)} - Path: {OnOff(pathMode)}";

        void ApplyAntiGravity()
        {
            if (!antiGravity || local?.Connected != true || localNetId == 0) return;
            long agWorldGen = worldGen;
            local.Send(GameFeatures.AntiGravityPlace());
            Later(1000, () =>
            {
                if (agWorldGen != worldGen || !antiGravity || local?.Connected != true || localNetId == 0) return;
                foreach (var packet in GameFeatures.AntiGravityActivate(localNetId, posX, posY)) local.Send(packet);
            });
        }

        void SetAntiGravity(bool enabled, bool announce = true)
        {
            if (antiGravity == enabled)
            {
                if (announce) Reply($"`wAnti Gravity zaten {OnOff(enabled)}`o.");
                return;
            }
            antiGravity = enabled;
            bool inWorld = localNetId != 0 && Protocol.ValidWorld(world);
            if (enabled) ApplyAntiGravity();
            else if (inWorld && local?.Connected == true)
            {
                // The generator exists only in the client's copy of the world: take it away and put
                // back what really stands there. No relog needed.
                int original = map?.Inside(GameFeatures.AntiGravityX, GameFeatures.AntiGravityY) == true
                    ? map.Tiles[GameFeatures.AntiGravityY * map.Width + GameFeatures.AntiGravityX].Foreground : 0;
                foreach (var packet in GameFeatures.AntiGravityRemove(original)) local.Send(packet);
            }
            if (announce)
                Reply(enabled
                    ? "`wAnti Gravity: `2AKTİF `o- Yerçekimi azaltıldı; her dünyada kendiliğinden açılır." + (inWorld ? "" : " Bir dünyaya girdiğinde devreye girecek.")
                    : "`wAnti Gravity: `4KAPALI `o- Yerçekimi normale döndü.");
        }

        // Shift clicks can choose a route once the cursor resolves to a tile.
        void SetPathMode(bool enabled, bool announce = true)
        {
            if (pathMode == enabled)
            {
                if (announce) Reply($"`wPath zaten {OnOff(enabled)}`o.");
                return;
            }
            pathMode = enabled;
            if (!Isolated) PathEnabled = enabled;
            pendingShift = null; locateGen++; pendingLocate = null; suppressTileUntil = 0;
            pathZoomActive = enabled && map != null && localNetId != 0;
            if (enabled) camera.SetLevel(zoomLevel);
            if (enabled) Telemetry.Log(MouseHookActive ? "Path: oyun penceresindeki imleç konumu kullanılıyor." : "Path: fare izleme yok; harita ve koordinat seçimi kullanılabilir.", "TANI");
            if (announce)
                Reply(enabled
                    ? "`wPath: `2AKTİF `o- `wShift + Sol Tık `oile seçilen karoya gidilir."
                    : "`wPath: `4KAPALI `o- Tıklamalar normal çalışıyor.");
        }

        /// <summary>A click (tile change request) from the client while path mode is on. True = consumed.</summary>
        bool InterceptClick()
        {
            if (MouseHookActive) return Now() <= suppressTileUntil;
            if (IsShiftDown?.Invoke() != true) return false;
            ReportUnknownTarget();
            return true;
        }

        double PlayerTileX() => (posX + 16) / 32.0;
        double PlayerTileY() => (posY + 16) / 32.0;

        void SyncMemoryPosition()
        {
            if (Isolated || map == null || localNetId == 0 || Now() < walkLockUntil) return;
            if (GamePosition?.Invoke() is { } mp && float.IsFinite(mp.X) && float.IsFinite(mp.Y)
                && Math.Abs(mp.X) < 1e6 && Math.Abs(mp.Y) < 1e6)
            {
                posX = mp.X; posY = mp.Y; positionKnown = true;
                position = $"{Math.Floor(posX / 32):0}, {Math.Floor(posY / 32):0}";
            }
        }

        void DrainMouse()
        {
            if (Isolated) return;
            for (int i = 0; i < 64 && MouseEvents.TryDequeue(out var mouse); i++) HandleMouse(mouse);
        }

        void ClearPathWorld()
        {
            worldGen++; pathWalkGen++;
            locateGen++; pendingLocate = null;
            map = null; mapId = 0; positionKnown = false;
            pathZoomActive = false;
            suppressTileUntil = 0;
            pendingClick = pendingShift = null; reportedTargets.Clear();
            camera.Reset(); walkLockUntil = cameraSettlesAt = 0;
        }

        void HandleMouse(MouseEvent mouse)
        {
            double clickedAt = mouse.Timestamp == 0 ? Now() : mouse.Timestamp * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (Now() - clickedAt > 250 || mouse.Width <= 0 || mouse.Height <= 0) return;
            suppressTileUntil = 0;
            if (mouse.WindowId != mouseWindow || mouse.Width != mouseWidth || mouse.Height != mouseHeight)
            {
                mouseWindow = mouse.WindowId; mouseWidth = mouse.Width; mouseHeight = mouse.Height; camera.Reset();
                pendingClick = pendingShift = null; locateGen++; pendingLocate = null; reportedTargets.Clear();
            }
            if (mouse.Kind == MouseEventKind.Wheel)
            {
                // The wheel zooms the game as usual; the model counts the notches (relative level).
                wheelRemainder += mouse.Delta;
                int notches = (int)Math.Truncate(wheelRemainder);
                if (notches == 0) return;
                wheelRemainder -= notches;
                zoomLevel = Math.Clamp(zoomLevel + notches, -6, 4);
                camera.SetLevel(zoomLevel);
                pendingClick = pendingShift = null; locateGen++; pendingLocate = null;
                cameraSettlesAt = Math.Max(cameraSettlesAt, clickedAt + 250);
                return;
            }
            if (map == null || localNetId == 0 || !positionKnown) return;
            pendingClick = pendingShift = null; locateGen++; pendingLocate = null;
            reportedTargets.Clear();
            if (!mouse.Shift)
            {
                // An ordinary click: when the game answers with a tile change, it teaches the camera model.
                pendingClick = mouse; pendingClickAt = clickedAt;
                pendingPlayerX = PlayerTileX(); pendingPlayerY = PlayerTileY();
                pendingClickZoom = pathMode ? GameZoom?.Invoke() : null;
                return;
            }
            if (!pathMode) return;
            // While standing still no movement packet arrives: prefer the game's own value.
            SyncMemoryPosition();
            pendingShift = mouse; pendingShiftAt = clickedAt;
            suppressTileUntil = clickedAt + ShiftWaitMs;
            pendingShiftPlayerX = PlayerTileX(); pendingShiftPlayerY = PlayerTileY();
            pendingShiftVelX = (Now() - lastMovePacketAt < 300) ? velX : 0;
            pendingShiftVelY = (Now() - lastMovePacketAt < 300) ? velY : 0;
            pendingShiftZoom = GameZoom?.Invoke();
            locateGen++; pendingLocate = null;
            if (GameFrame is { } getFrame)
            {
                if (locatorKeys == null || locatorKeysMapId != mapId) { locatorKeys = ScreenLocator.Keys(map); locatorKeysMapId = mapId; }
                var keys = locatorKeys; int mw = map.Width, mh = map.Height;
                double px0 = pendingShiftPlayerX, py0 = pendingShiftPlayerY;
                var getStatus = GameFrameStatus;
                pendingLocateGen = locateGen;
                pendingLocate = Task.Run(() =>
                {
                    try
                    {
                        var frame = getFrame();
                        if (frame == null) return ((ScreenFit?)null, "görüntü yok (" + (getStatus?.Invoke() ?? "yakalayıcı yok") + ")");
                        var fit = ScreenLocator.Locate(frame, keys, mw, mh, px0, py0, out string note);
                        return (fit, note);
                    }
                    catch (Exception ex) { return ((ScreenFit?)null, "hata (" + ex.GetType().Name + ")"); }
                });
            }
        }

        // A Shift click: a tile the game itself reports close to the player is exact and teaches the
        // scale of the current level; anything else is computed from the cursor and the known scale.
        void ResolveShift()
        {
            var mouse = pendingShift!; double at = pendingShiftAt;
            if (map == null || !positionKnown) { pendingShift = null; locateGen++; pendingLocate = null; return; }
            double elapsed = Now() - at;
            if (elapsed < ShiftWaitMs) return;
            ScreenFit? fit = null; string locateNote = "";
            if (pendingLocate != null && pendingLocateGen == locateGen)
            {
                if (!pendingLocate.IsCompleted && elapsed < 1500) return;
                if (pendingLocate.IsCompletedSuccessfully) (fit, locateNote) = pendingLocate.Result;
                else if (pendingLocate.IsFaulted) locateNote = "hata";
                else locateNote = "zaman aşımı";
                pendingLocate = null;
            }
            else locateNote = "görüntü yok (" + (GameFrameStatus?.Invoke() ?? "yakalayıcı yok") + ")";
            pendingShift = null;

            double px = pendingShiftPlayerX, py = pendingShiftPlayerY;
            // Compensate for camera glide lag while player is running or falling:
            // Growtopia's 2D camera interpolation trails behind the player center by ~100ms horizontally and ~75ms vertically.
            double camPx = px, camPy = py;
            if (Math.Abs(pendingShiftVelX) > 40)
                camPx = Math.Clamp(px - (pendingShiftVelX * 0.10f) / 32.0, 0, (map.Width > 0 ? map.Width : 100));
            if (Math.Abs(pendingShiftVelY) > 60)
                camPy = Math.Clamp(py - (pendingShiftVelY * 0.075f) / 32.0, 0, (map.Height > 0 ? map.Height : 60));

            camera.SetZoom(pendingShiftZoom);
            // Preferred: find the world in the window picture — exact whatever the zoom is. Only when
            // the picture does not decide it does the learned camera model take over.
            int cx = 0, cy = 0;
            bool cursorKnown = false;
            if (fit != null)
            {
                var (fx, fy) = fit.Tile(mouse.X, mouse.Y);
                if (map.Inside(fx, fy)) { cx = fx; cy = fy; cursorKnown = true; }
            }

            if (!cursorKnown)
                cursorKnown = camera.TryTile(mouse.Width, mouse.Height, mouse.X, mouse.Y, camPx, camPy, out cx, out cy, map.Width, map.Height);

            var report = reportedTargets.LastOrDefault(t => t.At > at && t.At - at <= ShiftWaitMs + 100);
            bool reported = report.At > 0;
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py);

            // Beyond reach the client reports the tile 3-5 steps away in the facing direction, so a
            // report is trusted only close to the player or when it agrees with the cursor.
            bool trusted = reported && (report.X != ix || report.Y != iy) && (cursorKnown
                ? Math.Max(Math.Abs(report.X - cx), Math.Abs(report.Y - cy)) <= 1
                : Math.Max(Math.Abs(report.X - ix), Math.Abs(report.Y - iy)) <= SafeReach);

            Telemetry.Log($"Path tıklaması: imleç {(cursorKnown ? $"{cx},{cy}" : "bilinmiyor")}, oyunun bildirdiği {(reported ? $"{report.X},{report.Y}" : "-")}, "
                + $"konum {ix},{iy}, ekran {mouse.X},{mouse.Y}/{mouse.Width}x{mouse.Height}, seviye {zoomLevel}, ölçek {(camera.CurrentScale is double cs ? cs.ToString("0.0") : "-")}, "
                + $"oyun yakınlaştırması {(pendingShiftZoom is double gz ? gz.ToString("0.0000") : "-")} ({GameZoomStatus?.Invoke() ?? "okuyucu yok"}), görüntü {locateNote}.", "TANI");

            if (trusted)
            {
                if (cursorKnown || Math.Max(Math.Abs(report.X - ix), Math.Abs(report.Y - iy)) <= SafeReach)
                    Learn(mouse, px, py, report.X, report.Y, at);
                int targetX = cursorKnown ? Math.Clamp(cx, 0, map.Width - 1) : report.X;
                int targetY = cursorKnown ? Math.Clamp(cy, 0, map.Height - 1) : report.Y;
                WalkTo(targetX, targetY, nearest: true);
                return;
            }
            if (!cursorKnown)
            {
                ReportUnknownTarget();
                return;
            }
            WalkTo(Math.Clamp(cx, 0, map.Width - 1), Math.Clamp(cy, 0, map.Height - 1), nearest: true);
        }

        void ReportUnknownTarget() => PathResult("`4Path hedefi belirlenemedi.");

        void NoteTarget(byte[] data, bool tileRequest)
        {
            int x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48)), y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52));
            if (map != null ? !map.Inside(x, y) : x < 0 || y < 0) return;
            reportedTargets.Add((Now(), x, y));
            if (reportedTargets.Count > 16) reportedTargets.RemoveAt(0);
            // The first report after an ordinary click belongs to that click.
            if (pendingClick != null)
            {
                var click = pendingClick; pendingClick = null;
                int px = (int)Math.Floor(pendingPlayerX), py = (int)Math.Floor(pendingPlayerY);
                bool close = Math.Max(Math.Abs(x - px), Math.Abs(y - py)) <= SafeReach;
                if (tileRequest && Now() - pendingClickAt <= ShiftWaitMs && (x != px || y != py) && close
                    && Math.Abs(PlayerTileX() - pendingPlayerX) <= 0.15 && Math.Abs(PlayerTileY() - pendingPlayerY) <= 0.15)
                {
                    camera.SetZoom(pendingClickZoom);
                    Learn(click, pendingPlayerX, pendingPlayerY, x, y, pendingClickAt);
                }
            }
        }

        // State packets carry a tile only while the player is punching: the punch target.
        void NoteStateTarget(byte[] data)
        {
            if (pendingShift == null || (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)) & 0x800) == 0) return;
            int x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48)), y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52));
            if ((x == 0 && y == 0) || x < 0 || y < 0) return;
            NoteTarget(data, tileRequest: false);
        }

        // A click the game resolved to a tile teaches the camera: screen point + exact tile at the current level.
        void Learn(MouseEvent click, double playerX, double playerY, int tileX, int tileY, double clickedAt)
        {
            if (clickedAt < cameraSettlesAt || !pathMode) return; // the camera was still moving, or the zoom is not Zeus's
            int before = camera.SampleCount;
            camera.AddSample(click.Width, click.Height, click.X, click.Y, playerX, playerY, tileX, tileY, map?.Width ?? 0, map?.Height ?? 0);
            if (camera.SampleCount != before && camera.SampleCount <= 8)
                Telemetry.Log($"Path: kamera örneği {camera.SampleCount} (seviye {zoomLevel}, ölçek {(camera.CurrentScale is double cs ? cs.ToString("0.0") : "-")}).", "TANI");
        }

        void PathResult(string text)
        {
            if (local?.Connected == true) local.Send(GameFeatures.TextOverlayPacket(text));
            Telemetry.Log("Path: " + System.Text.RegularExpressions.Regex.Replace(text, "`.", ""), "TANI");
        }

        void FinalizeWalk(int ex, int ey, int pathCount, int tx, int ty, double seconds)
        {
            posX = ex * 32; posY = ey * 32; position = $"{ex}, {ey}";
            velX = velY = 0;
            walkLockUntil = Now() + 600; walkLockX = posX; walkLockY = posY;
            local?.Send(GameFeatures.SetPositionPacket(localNetId, posX, posY));
            cameraSettlesAt = Now() + 900; // the camera glides to the new spot
            string targetInfo = (ex != tx || ey != ty) ? $" (en yakın: {ex},{ey})" : "";
            PathResult($"`2Path {pathCount} blok{targetInfo}. `9({seconds:0.00}sn)");
        }

        // nearest: a Shift click on a block goes to the free tile beside it; coordinates and the map picker are exact.
        void WalkTo(int tx, int ty, bool nearest = false)
        {
            pendingShift = null; locateGen++; pendingLocate = null;
            SyncMemoryPosition();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int sx = (int)MathF.Floor(posX / 32), sy = (int)MathF.Floor(posY / 32);
            Telemetry.Log($"Path hedefi: {tx}, {ty} - konum {sx}, {sy}.", "TANI");
            if (upstream?.Connected != true || local?.Connected != true || localNetId == 0 || !positionKnown)
            { PathResult("`4Path başarısız. `oÖnce bir dünyaya girin."); return; }
            if (map == null)
            {
                PathResult(Items() == null ? "`4Path başarısız. `oitems.dat okunamadı." : "`4Path başarısız. `oDünya haritası okunamadı.");
                return;
            }
            // A click on a block means "go there": the nearest reachable free tile by it.
            var found = nearest ? map.FindPathToClick(sx, sy, tx, ty, userId)
                : map.FindPath(sx, sy, tx, ty, userId) is { } exact ? (exact, tx, ty) : ((List<(int X, int Y)>, int, int)?)null;
            double seconds = clock.Elapsed.TotalSeconds;
            if (found == null)
            {
                PathResult($"`4Yol bulunamadı. `oHedef {tx}, {ty} kapalı veya erişilemiyor. `9({seconds:0.00}sn)");
                return;
            }
            var (path, ex, ey) = found.Value;
            if (path.Count == 0)
            {
                PathResult(ex == tx && ey == ty ? "`2Zaten hedef karodasınız." : "`3Zaten hedefe en yakın karodasınız.");
                return;
            }
            long walkGen = ++pathWalkGen;
            long walkMapId = mapId;
            const int BatchSize = 25;
            if (path.Count <= BatchSize)
            {
                foreach (var (x, y) in path) upstream.Send(GameFeatures.PathStep(x, y));
                FinalizeWalk(ex, ey, path.Count, tx, ty, seconds);
            }
            else
            {
                // Paced transmission: send steps in batches of 25 with 20ms micro-intervals
                // to prevent server-side packet flood / speed violation kicks on long teleports.
                int total = path.Count;
                for (int offset = 0; offset < total; offset += BatchSize)
                {
                    int start = offset;
                    int count = Math.Min(BatchSize, total - start);
                    var slice = path.GetRange(start, count);
                    bool isLast = (start + count >= total);
                    int delay = (start / BatchSize) * 20;
                    if (delay == 0)
                    {
                        foreach (var (x, y) in slice) upstream.Send(GameFeatures.PathStep(x, y));
                        if (isLast) FinalizeWalk(ex, ey, total, tx, ty, seconds);
                    }
                    else
                    {
                        Later(delay, () =>
                        {
                            if (walkGen != pathWalkGen || walkMapId != mapId || upstream?.Connected != true) return;
                            foreach (var (x, y) in slice) upstream.Send(GameFeatures.PathStep(x, y));
                            if (isLast) FinalizeWalk(ex, ey, total, tx, ty, seconds);
                        });
                    }
                }
            }
        }

        void LoadMap(byte[] data)
        {
            ClearPathWorld();
            var db = Items();
            if (db == null) return;
            try
            {
                map = WorldMap.Parse(data, db);
                mapId = ++lastMapId;
                pathZoomActive = pathMode && localNetId != 0;
                Telemetry.Log($"Harita okundu: {map.Name} {map.Width}x{map.Height}.", "TANI");
            }
            catch (InvalidDataException error)
            {
                Telemetry.Log("Harita okunamadı: " + error.Message, "TANI");
                if (MapDumpDirectory == null) return;
                try
                {
                    Directory.CreateDirectory(MapDumpDirectory);
                    File.WriteAllBytes(Path.Combine(MapDumpDirectory, "okunamayan-harita.bin"), data);
                    Telemetry.Log($"Okunamayan harita tanı için kaydedildi: {Path.Combine(MapDumpDirectory, "okunamayan-harita.bin")}", "TANI");
                }
                catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            }
        }

        void Relog(bool quiet = false)
        {
            if (upstream?.Connected != true || !Protocol.ValidWorld(world))
            { Reply("`4Relog yapılamadı. `oÖnce bir dünyaya gir."); return; }
            string target = world;
            upstream.Send(Protocol.TextPacket(3, "action|quit_to_exit"));
            int attempts = 0;
            void Join()
            {
                // A server switch may be in progress; wait for the new connection (up to ~10 s).
                if (upstream?.Connected == true)
                    upstream.Send(Protocol.TextPacket(3, $"action|join_request\nname|{target}\ninvitedWorld|0"));
                else if (++attempts < 20) Later(500, Join);
            }
            Later(500, Join);
            if (!quiet) Reply($"`oRelog: `w{target} `odünyasından çıkılıp tekrar giriliyor...");
        }

        void Keep()
        {
            kept = new GameSettings(remeMode, qqMode, pingTag, antiGravity, pathMode);
            if (SettingsPath == null) { Reply("`4Ayarlar kaydedilemedi: `okayıt konumu tanımlı değil."); return; }
            try
            {
                kept.Save(SettingsPath);
                Reply($"`2Ayarların kalıcı olarak kaydedildi. `oZeus her açılışta bu ayarlarla başlayacak: {SettingsSummary()}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Reply("`4Ayarlar kaydedilemedi: `o" + error.Message); }
        }

        void ApplyPanel(string text)
        {
            string dialogName = Protocol.Field(text, "dialog_name"), button = Protocol.Field(text, "buttonClicked");
            if (dialogName == "zeus_proxy") { if (button == "zeus_open_options") ShowOptions(); return; }
            if (dialogName != "zeus_options") return;
            string reme = Protocol.Field(text, "zeus_reme"), qq = Protocol.Field(text, "zeus_qq"), ping = Protocol.Field(text, "zeus_ping");
            string gravity = Protocol.Field(text, "zeus_antigravity"), path = Protocol.Field(text, "zeus_path");
            if (reme.Length == 0 && qq.Length == 0 && ping.Length == 0 && gravity.Length == 0 && path.Length == 0) return;
            remeMode = reme == "1"; qqMode = qq == "1"; SetPingTag(ping == "1");
            if (gravity.Length > 0) SetAntiGravity(gravity == "1", announce: false);
            if (path.Length > 0) SetPathMode(path == "1", announce: false);
            if (button == "zeus_keep") Keep();
            else Reply($"`2Ayarlar uygulandı. `o{SettingsSummary()} `o- Kalıcı yapmak için `w/keep`o.");
        }

        // "/x" switches; "/x on|ac" and "/x off|kapat" set the state explicitly.
        static bool Toggle(string argument, bool current) => argument.ToLowerInvariant() switch
        {
            "on" or "ac" or "1" => true,
            "off" or "kapat" or "kapa" or "0" => false,
            _ => !current,
        };

        static string OnOff(bool on) => on ? "`2AKTİF`o" : "`4KAPALI`o";

        bool HandleCommand(string input)
        {
            string trimmed = GameFeatures.CleanCommand(input);
            if (!trimmed.StartsWith('/')) return false;
            // Commands are typed directly (/warp, /ping). The old "/zeus warp" form stays as an alias.
            bool prefixed = trimmed.Equals("/zeus", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("/zeus ", StringComparison.OrdinalIgnoreCase);
            string body = prefixed ? trimmed[5..].Trim() : trimmed[1..];
            var parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string name = parts.Length > 0 ? parts[0].ToLowerInvariant() : "proxy";
            string argument = parts.Length > 1 ? parts[1] : "";
            switch (name)
            {
                case "proxy":
                case "help" when prefixed:
                    ShowPanel(); break;
                case "status":
                {
                    uint rtt = upstream?.Connected == true ? upstream.Rtt : 0;
                    Reply($"`wBağlantı: `2{Telemetry.Snapshot().State} `o- Dünya: `w{world} `o- Oyuncu: `w{players.Count} `o- Ping: `{GameFeatures.PingColor(rtt)}{rtt} ms");
                    break;
                }
                case "ping":
                    SetPingTag(!pingTag);
                    Reply(pingTag
                        ? "`wPing Göstergesi: `2AKTİF `o- Gecikmen isminin solunda gösteriliyor: `2yeşil `o100 ms altı, `9sarı `o200 ms altı, `4kırmızı `o400 ms altı, `bsiyah `o400 ms ve üzeri."
                        : "`wPing Göstergesi: `4KAPALI `o- İsmin eski haline döndü.");
                    break;
                case "pos":
                    SyncMemoryPosition();
                    Reply(position == "—" ? "`oKonum henüz bilinmiyor. Bir dünyaya girip hareket ettiğinde gösterilir."
                        : $"`wKarakter konumu (karo): `o{position}");
                    break;
                case "fav":
                    Reply(profile.Bookmarks.Length == 0
                        ? "`oFavori dünya listen henüz boş. Masaüstü panelindeki `wAyarlar `obölümünden ekleyebilirsin."
                        : $"`wFavori dünyalar ({profile.Bookmarks.Length}): `o" + string.Join(", ", profile.Bookmarks));
                    break;
                case "keep": Keep(); break;
                case "back":
                    if (previousWorld.Length == 0 || previousWorld == "—")
                        Reply("`oHenüz önceki bir dünya kaydı yok. Bir dünyadan diğerine geçtikten sonra `w/back `okullanılabilir.");
                    else Warp(previousWorld);
                    break;
                case "options":
                case "option":
                case "settings":
                    ShowOptions(); break;
                case "path":
                    if (argument.Equals("map", StringComparison.OrdinalIgnoreCase) || argument.Equals("harita", StringComparison.OrdinalIgnoreCase))
                        OpenPathMap();
                    else if (argument.Length == 0 || argument.ToLowerInvariant() is "on" or "ac" or "off" or "kapa" or "kapat" or "1" or "0")
                        SetPathMode(Toggle(argument, pathMode));
                    else
                    {
                        var xy = argument.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y)) WalkTo(x, y);
                        else Reply("`oKullanım `w/path map `oveya `w/path X Y`o.");
                    }
                    break;
                case "relog":
                case "rejoin":
                    Relog(); break;
                case "antigravity":
                case "antigrav":
                case "ag":
                    SetAntiGravity(Toggle(argument, antiGravity)); break;
                case "reme":
                    remeMode = !remeMode;
                    Reply(remeMode
                        ? "`wReme Modu: `2AKTİF `o- Rulet sonuçlarının yanına Reme değeri eklenecek (örnek: 23 -> Reme 5)."
                        : "`wReme Modu: `4KAPALI `o- Rulet sonuçlarına Reme değeri eklenmeyecek.");
                    break;
                case "qq":
                case "qeme":
                    qqMode = !qqMode;
                    Reply(qqMode
                        ? "`wQQ Modu: `2AKTİF `o- Rulet sonuçlarının yanına QQ değeri eklenecek (örnek: 23 -> QQ 3)."
                        : "`wQQ Modu: `4KAPALI `o- Rulet sonuçlarına QQ değeri eklenmeyecek.");
                    break;
                case "warp":
                    if (argument.Length == 0) Reply("`wKullanım: `o/warp `wDUNYA_ADI `o- Örnek: `w/warp START");
                    else Warp(argument);
                    break;
                default:
                    // Anything that is not a proxy command (/help, /msg, ...) goes to the game unchanged.
                    if (!prefixed) return false;
                    Reply("`4Bilinmeyen Zeus komutu. `oTüm komutları görmek için `w/proxy `oyaz.");
                    break;
            }
            return true;
        }

        void Warp(string name)
        {
            if (!Protocol.ValidWorld(name)) { Reply("`4Geçersiz dünya adı. `oDünya adı 1-24 harf veya rakamdan oluşmalı."); return; }
            if (upstream?.Connected != true) { Reply("`4Sunucu bağlantısı yok. `oÖnce oyuna giriş yapıp bir dünyaya gir."); return; }
            upstream.Send(Protocol.TextPacket(3, $"action|join_request\nname|{name.ToUpperInvariant()}\ninvitedWorld|0"));
            Reply($"`oDünya geçişi istendi: `w{name.ToUpperInvariant()}`o. Sunucunun yanıtı bekleniyor...");
        }

        void OpenPathMap()
        {
            SyncMemoryPosition();
            if (map == null || !positionKnown || localNetId == 0) { Reply("`oHaritayı açmak için bir dünyaya gir."); return; }
            var requested = PathMapRequested;
            if (requested == null) { Reply("`oBu oturumda `w/path X Y`o ile hedef seç."); return; }
            bool[] open = new bool[map.Tiles.Length];
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++) open[y * map.Width + x] = map.Passable(x, y, userId);
            requested(new(mapId, map.Name, map.Width, map.Height,
                (int)Math.Floor(posX / 32), (int)Math.Floor(posY / 32), open));
        }

        void Observe(byte[] data, bool outgoing)
        {
            if (Protocol.Type(data) != 4 || !Protocol.ValidGamePacket(data)) return;
            if (outgoing && data[4] == 0)
            {
                float x = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(28));
                float y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(32));
                // Right after a walk the client still reports where it was until it applies OnSetPos.
                bool stale = Now() < walkLockUntil && (Math.Abs(x - walkLockX) > 48 || Math.Abs(y - walkLockY) > 48);
                if (!stale && float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) < 1e6 && Math.Abs(y) < 1e6)
                {
                    position = $"{Math.Floor(x / 32):0}, {Math.Floor(y / 32):0}";
                    posX = x; posY = y;
                    if (data.Length >= 44)
                    {
                        float vx = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(36));
                        float vy = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(40));
                        velX = float.IsFinite(vx) && Math.Abs(vx) < 5000 ? vx : 0;
                        velY = float.IsFinite(vy) && Math.Abs(vy) < 5000 ? vy : 0;
                    }
                    lastMovePacketAt = Now();
                    if (localNetId != 0) positionKnown = true;
                }
            }
            if (!outgoing && data[4] == 4 && data.Length >= 68)
            {
                uint ext = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(56));
                int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(66));
                if (length is > 0 and <= 24 && ext >= 8 + length && data.Length >= 68 + length)
                {
                    string name = System.Text.Encoding.UTF8.GetString(data, 68, length);
                    if (Protocol.ValidWorld(name) && world != name) { previousWorld = world == "—" ? previousWorld : world; world = name; players.Clear(); }
                }
                LoadMap(data);
            }
            if (!outgoing && map != null && data[4] == 3)
                map.ApplyTileChange(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52)),
                    BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24)));
            if (!outgoing && map != null && data[4] == 5 && !map.ApplyTileUpdate(data))
                Telemetry.Log("Karo güncellemesi okunamadı; harita bir sonraki girişte yenilenecek.", "TANI");
            if (outgoing) return;
            var values = Protocol.ReadVariants(data);
            string? function = values?.FirstOrDefault(v => v.Index == 0)?.Text;
            string content = values?.FirstOrDefault(v => v.Index == 1)?.Text ?? "";
            if (function == "OnSpawn" && players.Count < 1024 && int.TryParse(Protocol.Field(content, "netID"), out int id)) players.Add(id);
            if (function == "OnRemove" && int.TryParse(Protocol.Field(content, "netID"), out id)) players.Remove(id);
        }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); lifecycle.Dispose(); }
}