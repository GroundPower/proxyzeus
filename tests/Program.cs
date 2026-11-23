using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Zeus.Core;

int passed = 0, failed = 0;
if (args.Contains("--handoff-only"))
{
    await Run("Eski istemci ayrılmadan 12 ardışık dünya geçişi", () => HandoffRegression.RunAsync(12, false));
    Console.WriteLine($"SONUÇ: {passed} başarılı, {failed} başarısız.");
    Environment.ExitCode = failed == 0 ? 0 : 1;
    return;
}
async Task ScreenLocatorTest()
{
    var cases = new List<ScreenFixtures.Case>
    {
        // name, seed, window, tile size, player tile, camera lag (tiles)
        ScreenFixtures.Make("çok uzak, dünya ekrandan küçük", 1, 1024, 768, 6.283, 67.5, 23.5, 0, 0),
        ScreenFixtures.Make("uzak", 2, 1024, 768, 9.1, 40.5, 23.5, 1.3, -0.4),
        ScreenFixtures.Make("orta, sol kenar", 3, 1024, 768, 13.7, 3.5, 22.5, 0, 0),
        ScreenFixtures.Make("orta, gecikmeli kamera", 4, 1024, 768, 20.5, 55.5, 30.5, -2.7, 1.6),
        ScreenFixtures.Make("orta", 5, 1024, 768, 24.1, 82.5, 22.5, 0.4, 0.2),
        ScreenFixtures.Make("normal", 6, 1024, 768, 32, 50.5, 23.5, 0, 0),
        ScreenFixtures.Make("yakın", 7, 1024, 768, 45.5, 20.5, 40.5, 0.8, -0.6),
        ScreenFixtures.Make("çok yakın", 8, 1024, 768, 64, 70.5, 23.5, 0, 0),
        ScreenFixtures.Make("tam ekran, alt kenar", 9, 1920, 1009, 27.3, 91.5, 57.5, 0, 0),
        ScreenFixtures.Make("tam ekran, yakın", 10, 1920, 1009, 58.7, 12.5, 23.5, -1.1, 0.9),
    };
    var clock = Stopwatch.StartNew();
    foreach (var c in cases)
    {
        clock.Restart();
        var fit = ScreenLocator.Locate(c.Frame, c.Keys, c.MapWidth, c.MapHeight, c.PlayerX, c.PlayerY, out string note);
        long ms = clock.ElapsedMilliseconds;
        Console.WriteLine($"  {c.Name}: gerçek ölçek {c.Scale:0.000} köken {c.OriginX:0.0},{c.OriginY:0.0} -> {note} ({ms} ms)");
        Assert(fit != null, c.Name + ": eşleşme bulunamadı: " + note);
        Assert(Math.Abs(fit!.Scale - c.Scale) < 0.004 * c.Scale + 0.01, c.Name + ": ölçek yanlış");
        string? error = ScreenFixtures.Check(c, fit);
        Assert(error == null, c.Name + ": " + error);
        Assert(ms < 1500, c.Name + ": çok yavaş");
    }
    // Nothing to see (a blank, loading or menu screen): no guess.
    var blank = new ScreenFrame(1024, 768, Enumerable.Repeat((byte)90, 1024 * 768 * 4).ToArray());
    Assert(ScreenLocator.Locate(blank, cases[0].Keys, 100, 60, 50, 30, out _) == null, "boş ekranda tahmin yapıldı");
    // The wrong world: the image does not match the map.
    var other = ScreenFixtures.World(77);
    var wrong = ScreenLocator.Locate(cases[5].Frame, other, 100, 60, 50.5, 23.5, out string wrongNote);
    Console.WriteLine("  yanlış harita: " + wrongNote);
    Assert(wrong == null, "yanlış haritada eşleşme bulundu: " + wrongNote);
}
if (args.Contains("--screen-only"))
{
    await Run("Ekran görüntüsünden karo bulma", ScreenLocatorTest);
    Console.WriteLine($"SONUÇ: {passed} başarılı, {failed} başarısız.");
    Environment.ExitCode = failed == 0 ? 0 : 1;
    return;
}
await Run("Klasik ENet / iki yön / kanal / parçalama", async () => { Console.WriteLine(await LocalDiagnostics.RunAsync()); });
await Run("Güncel istemci zarfının yerel proxy’de otomatik algılanması", async () => { Console.WriteLine(await LocalDiagnostics.RunAsync(ubisoftClient: true)); });
await Run("CRC kullanmayan klasik yerel istemci", async () => { Console.WriteLine(await LocalDiagnostics.RunAsync(clientChecksum: false)); });
await Run("CRC kullanmayan Ubisoft yerel istemci", async () => { Console.WriteLine(await LocalDiagnostics.RunAsync(ubisoftClient: true, clientChecksum: false)); });
await Run("Ubisoft zarfı / checksum / sıkıştırma / parçalama", async () =>
{
    using var server = new NativeHost(incomingPrefix: true);
    using var client = new NativeHost(outgoingPrefix: true);
    client.Connect("127.0.0.1", server.Port);
    await Pump(() => server.Connected && client.Connected, client, server);
    byte[] data = Enumerable.Range(0, 60000).Select(i => (byte)(i % 17)).ToArray();
    byte[]? result = null;
    client.Send(data, 1); await Pump(() => result != null, client, server, e => { if(e.Type == 3) result = e.Data; });
    Assert(data.SequenceEqual(result!), "zarf içindeki payload değişti");
    result = null; server.Send(data, 1);
    await Pump(() => result != null, server, client, e => { if(e.Type == 3) result = e.Data; });
    Assert(data.SequenceEqual(result!), "klasik dönüş payload değişti");
});
await Run("Variant kodlama ve sunucu yönlendirmesi", () =>
{
    var packet = Protocol.FunctionPacket([Variant.String(0, "OnSendToServer"), Variant.UInt(1, 17000), Variant.UInt(2, 9981), Variant.UInt(3, 725), Variant.String(4, "127.0.0.1|DOOR|UUID"), Variant.UInt(5, 1)]);
    Assert(Protocol.TryRedirect(packet, 17191, out var rewritten, out var host, out var port), "yönlendirme okunamadı");
    var values = Protocol.ReadVariants(rewritten)!;
    Assert(host == "127.0.0.1" && port == 17000, "hedef kayboldu");
    Assert(values.Single(v => v.Index == 1).Number == 17191, "yerel port yazılmadı");
    Assert(values.Single(v => v.Index == 4).Text == "127.0.0.1|DOOR|UUID", "kapı / UUID değişti");
    Assert(values.Single(v => v.Index == 2).Number == 9981, "token değişti");
    for (int i = 0; i < packet.Length; i++) Assert(Protocol.ReadVariants(packet[..i]) == null, "kesik variant kabul edildi");
    var invalid = packet.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(56), uint.MaxValue);
    Assert(!Protocol.ValidGamePacket(invalid), "boyut taşması kabul edildi");
    Assert(!Protocol.TryRedirect(Protocol.FunctionPacket([Variant.String(0, "OnSendToServer"), Variant.UInt(1, 0), Variant.String(4, "bad\nserver")]), 17191, out _, out _, out _), "bozuk hedef kabul edildi");
    return Task.CompletedTask;
});
await Run("Ayrıştırıcı rastgele / kesik veri dayanıklılığı", () =>
{
    var random = new Random(881);
    for (int i = 0; i < 20000; i++)
    {
        byte[] data = new byte[random.Next(0, 513)]; random.NextBytes(data);
        if (data.Length >= 4 && i % 2 == 0) BinaryPrimitives.WriteUInt32LittleEndian(data, 4);
        _ = Protocol.Summary(data); _ = Protocol.ReadVariants(data);
        _ = Protocol.TryRedirect(data, 17191, out _, out _, out _);
    }
    return Task.CompletedTask;
});
await Run("Gerçek sunucu geçişi / yeniden bağlantı", async () =>
{
    using var first = new NativeHost(); using var second = new NativeHost();
    ushort port; using (var p = new NativeHost()) port = p.Port;
    await using var relay = new RelayEngine();
    await relay.StartAsync(new Profile { LocalPort = port, ServerPort = first.Port });
    using var client = new NativeHost(); client.Connect("127.0.0.1", port);
    await Pump(() => first.Connected && client.Connected, client, first);
    byte[] redirect = Protocol.FunctionPacket([Variant.String(0, "OnSendToServer"), Variant.UInt(1, second.Port), Variant.UInt(2, 981), Variant.UInt(3, 12), Variant.String(4, "127.0.0.1|DOOR")]);
    first.Send(redirect); byte[]? result = null;
    await Pump(() => result != null, first, client, e => { if (e.Type == 3) result = e.Data; });
    Assert(Protocol.ReadVariants(result!)!.Single(v => v.Index == 1).Number == port, "geçiş yerel porta dönmedi");
    client.Reset(); client.Flush();
    // Let the disconnect event reach the relay before reconnecting its single client slot.
    var until = Stopwatch.StartNew();
    while (until.ElapsedMilliseconds < 100) { client.Poll(); first.Poll(); await Task.Delay(2); }
    client.Connect("127.0.0.1", port);
    await Pump(() => second.Connected && client.Connected, client, second);
    byte[] login = Protocol.TextPacket(2, "token|981\nuser|12"); byte[]? forwarded = null;
    client.Send(login);
    await Pump(() => forwarded != null, client, second, e => { if (e.Type == 3) forwarded = e.Data; });
    Assert(login.SequenceEqual(forwarded!), "geçiş sonrası giriş verisi değişti");
});
await Run("Eski istemci ayrılmadan 12 ardışık dünya geçişi", () => HandoffRegression.RunAsync(12, false));
await Run("Farklı zarf ve CRC biçimleriyle 24 örtüşen geçiş", () => HandoffRegression.RunAsync(24, true));
await Run("Hiç ayrılmayan 16 eski bağlantıyla ardışık geçiş", () => HandoffRegression.RunAsync(16, true, keepRetiringAlive: true));
await Run("Geçiş sürerken iptal ve temiz yeniden giriş", () => RecoveryRegression.RunAsync(false));
await Run("Ulaşılamayan dünya sunucusundan sonra yeniden giriş", () => RecoveryRegression.RunAsync(true));
await Run("Giriş gizliliği / metadata kaydı", () =>
{
    var telemetry = new Telemetry();
    telemetry.Packet(true, Protocol.TextPacket(2, "tankIDPass|DO_NOT_LOG_ME\ntoken|TOP_SECRET\nnewField|UNKNOWN_SECRET"), 0);
    string output = System.Text.Json.JsonSerializer.Serialize(telemetry.Snapshot());
    Assert(!output.Contains("DO_NOT_LOG_ME") && !output.Contains("TOP_SECRET") && !output.Contains("UNKNOWN_SECRET"), "gizli içerik kayda girdi");
    telemetry.Packet(false, Protocol.FunctionPacket([Variant.String(0, "UNKNOWN_SECRET")]), 0);
    Assert(!System.Text.Json.JsonSerializer.Serialize(telemetry.Snapshot()).Contains("UNKNOWN_SECRET"), "bilinmeyen variant adı kayda girdi");
    for (int i = 0; i < 1200; i++) telemetry.Packet(false, Protocol.TextPacket(3, "chat|secret"), 0);
    Assert(telemetry.Snapshot().Traffic.Length == 1000, "halka tampon sınırı aşılmış");
    return Task.CompletedTask;
});
await Run("Öneksiz komutlar / /zeus takma adı / oyun komutu şeffaflığı", async () =>
{
    using var server = new NativeHost(); ushort port; using (var p = new NativeHost()) port = p.Port;
    await using var relay = new RelayEngine(); await relay.StartAsync(new Profile { LocalPort = port, ServerPort = server.Port });
    using var client = new NativeHost(); client.Connect("127.0.0.1", port);
    await Pump(() => server.Connected && client.Connected, client, server);
    foreach (string command in new[] { "/status", "/zeus status", "/STATUS" })
    {
        client.Send(Protocol.TextPacket(2, "action|input\n|text|" + command));
        NativeEvent? reply = null; bool leaked = false;
        var clock = Stopwatch.StartNew();
        while (reply == null && clock.ElapsedMilliseconds < 3000)
        {
            var ce = client.Poll(); if(ce?.Type == 3) reply = ce;
            var se = server.Poll(); if(se?.Type == 3) leaked = true;
            client.Flush(); server.Flush(); await Task.Delay(2);
        }
        Assert(reply != null && Protocol.Text(reply.Data).Contains("Dunya:"), command + " yanıtı yok");
        Assert(!leaked, command + " sunucuya gitti");
    }
    string? join = null;
    client.Send(Protocol.TextPacket(2, "action|input\n|text|/warp start2"));
    await Pump(() => join != null, client, server, e => { if (e.Type == 3) join = Protocol.Text(e.Data); });
    Assert(join!.Contains("action|join_request") && join.Contains("name|START2"), "/warp dünya isteği göndermedi");
    Assert(!join.Contains("/warp"), "/warp ham komut olarak sunucuya gitti");
    foreach (string gameCommand in new[] { "/help", "/msg arkadas selam", "/warpx", "selam /ping" })
    {
        byte[] normal = Protocol.TextPacket(2, "action|input\n|text|" + gameCommand); byte[]? got = null;
        client.Send(normal); await Pump(() => got != null, client, server, e => { if(e.Type == 3) got = e.Data; });
        Assert(normal.SequenceEqual(got!), gameCommand + " oyun komutu değişti");
    }
});
await Run("Rulet / reme / qq / zoom / isim etiketi / oyun metni", () =>
{
    int[] reme = [0,1,2,3,4,5,6,7,8,9, 1,2,3,4,5,6,7,8,9,0, 2,3,4,5,6,7,8,9,0,1, 3,4,5,6,7,8,9];
    for (int n = 0; n <= 36; n++)
    {
        Assert(GameFeatures.Reme(n) == reme[n], $"reme {n}");
        Assert(GameFeatures.Qq(n) == n % 10, $"qq {n}");
    }
    byte[] real = SpinPacket("OnTalkBubble", 7, "`7[```wAyse`` spun the wheel and got `423`7!`7]``", 1800);
    Assert(GameFeatures.TryRouletteSpin(real, out var spin), "gerçek rulet okunamadı");
    Assert(spin.Real && spin.Number == 23 && spin.Name == "Ayse" && spin.Color == '4' && spin.NetId == 7 && spin.Delay == 1800, "rulet alanları yanlış: " + spin);
    byte[] zero = GameFeatures.WithoutDelay(real);
    Assert(GameFeatures.Delay(zero) == 0 && GameFeatures.Delay(real) == 1800 && zero.AsSpan(28).SequenceEqual(real.AsSpan(28)), "gecikme kaldırma hatalı");
    Assert(GameFeatures.TryRouletteSpin(SpinPacket("OnConsoleMessage", 0, "`7[``Mert spun the wheel and got `20`7!]``", 1800), out spin)
        && spin.Number == 0 && spin.Name == "Mert", "konsol rulet mesajı okunamadı");
    Assert(GameFeatures.TryRouletteSpin(SpinPacket("OnTalkBubble", 9, "`wHile spun the wheel and got `436`7!", 0), out spin) && !spin.Real, "sahte mesaj gerçek sayıldı");
    Assert(!GameFeatures.TryRouletteSpin(SpinPacket("OnTalkBubble", 9, "x spun the wheel and got 37!", 1800), out _), "37 kabul edildi");
    Assert(!GameFeatures.TryRouletteSpin(SpinPacket("OnTalkBubble", 9, "merhaba", 1800), out _), "normal sohbet rulet sayıldı");
    string Bubble(byte[] packet) => Protocol.ReadVariants(packet)!.Single(v => v.Index == 2).Text;
    Assert(GameFeatures.SpinSuffix(23, false, false) == "" && GameFeatures.SpinSuffix(23, true, false) == " `cREME `95"
        && GameFeatures.SpinSuffix(23, false, true) == " `#QQ `83" && GameFeatures.SpinSuffix(23, true, true) == " `cREME `95 `#QQ `83", "reme/qq eki yanlış");
    Assert(string.Concat(Enumerable.Range(0, 10).Select(GameFeatures.RemeColor)) == "2448899^^2"
        && string.Concat(Enumerable.Range(0, 10).Select(GameFeatures.QqColor)) == "2448899^^2", "reme/qq renkleri yanlış");
    Assert(GameFeatures.SpinSuffix(19, true, false) == " `cREME `20" && GameFeatures.SpinSuffix(17, true, true) == " `cREME `^8 `#QQ `^7"
        && GameFeatures.SpinSuffix(28, true, true) == " `cREME `20 `#QQ `^8" && GameFeatures.SpinSuffix(29, true, true) == " `cREME `41 `#QQ `29", "renkli değer yanlış");
    Assert(GameFeatures.TryRouletteSpin(real, out var ayse) && GameFeatures.FormatRealSpin(ayse, " `cREME `85")
        == "`w[`2REAL`w] [Ayse spun the wheel and got `423`w!] `cREME `85``", "REAL biçimi yanlış");
    Assert(Bubble(GameFeatures.ReplaceSpinText(real, "OnTalkBubble", t => "x" + t)) == "x" + Bubble(real), "balon metni değiştirilemedi");
    byte[] console = GameFeatures.TagConsoleMessage(Protocol.FunctionPacket([Variant.String(0, "OnConsoleMessage"), Variant.String(1, "selam")]));
    Assert(Protocol.ReadVariants(GameFeatures.TagConsoleMessage(console))!.Single(v => v.Index == 1).Text == GameFeatures.ConsoleTag + "selam", "konsol etiketi yanlış / iki kez eklendi");
    Assert(Protocol.Text(GameFeatures.TagLogText(Protocol.TextPacket(3, "action|log\nmsg|merhaba"))) == "action|log\nmsg|" + GameFeatures.ConsoleTag + "merhaba", "log etiketi yanlış");

    string spawn = "spawn|avatar\nnetID|5\nuserID|99\nname|`wBen``\ninvis|0\nmstate|0\nsmstate|0\ntype|local\n";
    byte[] spawnPacket = Protocol.FunctionPacket([Variant.String(0, "OnSpawn"), Variant.String(1, spawn)]);
    Assert(GameFeatures.TryLocalSpawn(spawnPacket, null, out var unlocked, out int localId, out string localName), "zoom açılmadı");
    string unlockedText = Protocol.ReadVariants(unlocked)!.Single(v => v.Index == 1).Text;
    Assert(localId == 5 && localName == "`wBen``" && unlockedText == spawn.Replace("\nmstate|0", "\nmstate|1"), "OnSpawn diğer alanları değişti");
    Assert(GameFeatures.TryLocalSpawn(spawnPacket, n => "`2[45]`` " + n, out unlocked, out _, out _)
        && Protocol.Field(Protocol.ReadVariants(unlocked)!.Single(v => v.Index == 1).Text, "name") == "`2[45]`` `wBen``", "spawn isim etiketi yok");
    Assert(!GameFeatures.TryLocalSpawn(Protocol.FunctionPacket([Variant.String(0, "OnSpawn"), Variant.String(1, spawn.Replace("type|local\n", ""))]), null, out _, out _, out _), "başka oyuncunun spawn paketi değişti");
    Assert(GameFeatures.TryLocalSpawn(Protocol.FunctionPacket([Variant.String(0, "OnSpawn"), Variant.String(1, "netID|3\ntype|local\n")]), null, out unlocked, out _, out _)
        && Protocol.Field(Protocol.ReadVariants(unlocked)!.Single(v => v.Index == 1).Text, "mstate") == "1", "mstate yoksa eklenmedi");

    byte[] rename = GameFeatures.NameChangedPacket(5, "`2[45]`` `wBen``");
    Assert(GameFeatures.TryNameChanged(rename, out int renamedId, out string renamed) && renamedId == 5 && renamed == "`2[45]`` `wBen``", "OnNameChanged hedefi yanlış");
    Assert(GameFeatures.PingColor(50) == '2' && GameFeatures.PingColor(150) == '9' && GameFeatures.PingColor(300) == '4' && GameFeatures.PingColor(600) == 'b', "ping renkleri yanlış");
    Assert(GameFeatures.ToGameText("Reme modu kapalı · İşğüöç ÇŞĞÜÖ|x") == "Reme modu kapali - Isguoc CSGUO/x", "oyun metni dönüştürmesi yanlış");
    return Task.CompletedTask;
});
await Run("Proxy üzerinden rulet / reme+qq / ping etiketi / panel / komut sızıntısı", async () =>
{
    using var server = new NativeHost(); ushort port; using (var p = new NativeHost()) port = p.Port;
    await using var relay = new RelayEngine(); await relay.StartAsync(new Profile { LocalPort = port, ServerPort = server.Port });
    using var client = new NativeHost(); client.Connect("127.0.0.1", port);
    await Pump(() => server.Connected && client.Connected, client, server);
    var logs = new List<string>(); var bubbles = new List<byte[]>(); var consoles = new List<byte[]>(); var names = new List<byte[]>(); var dialogs = new List<string>();
    var serverTexts = new List<string>(); string? spawnText = null; int spawnCount = 0;
    async Task Drive(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            NativeEvent? e;
            while ((e = client.Poll()) != null)
            {
                if (e.Type != 3) continue;
                if (Protocol.Type(e.Data) == 3) { logs.Add(Protocol.Text(e.Data)); continue; }
                var values = Protocol.ReadVariants(e.Data);
                string? f = values?.FirstOrDefault(v => v.Index == 0)?.Text;
                if (f == "OnSpawn") { spawnText = values!.Single(v => v.Index == 1).Text; spawnCount++; }
                if (f == "OnTalkBubble") bubbles.Add(e.Data);
                if (f == "OnConsoleMessage") consoles.Add(e.Data);
                if (f == "OnNameChanged") names.Add(e.Data);
                if (f == "OnDialogRequest") dialogs.Add(values!.Single(v => v.Index == 1).Text);
            }
            while ((e = server.Poll()) != null) if (e.Type == 3) serverTexts.Add(Protocol.Text(e.Data));
            client.Flush(); server.Flush();
            if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("test zaman aşımı; son log: " + logs.LastOrDefault());
            await Task.Delay(2);
        }
    }
    async Task Command(string text, uint type = 2)
    {
        int before = logs.Count + dialogs.Count;
        client.Send(Protocol.TextPacket(type, "action|input\n|text|" + text));
        await Drive(() => logs.Count + dialogs.Count > before);
    }
    // Login acceptance, then a second one as after a server transition: the message appears once.
    for (int i = 1; i <= 2; i++)
    {
        server.Send(Protocol.FunctionPacket([Variant.String(0, "OnSuperMainStartAcceptLogonHrdxs47254722215a"), Variant.UInt(1, 123)]));
        server.Send(Protocol.FunctionPacket([Variant.String(0, "OnSpawn"), Variant.String(1, "netID|5\nname|`wBen``\nmstate|0\ntype|local\n")]));
        await Drive(() => spawnCount == i);
    }
    Assert(logs.Count(l => l.Contains("Zeus Proxy v") && l.Contains("`2Proxy baglantisi basariyla kuruldu!")) == 1
        && logs.Count(l => l.Contains("/proxy") && l.Contains("/options")) == 1, "giriş mesajı bir kez yazılmadı: " + string.Join(" / ", logs));
    Assert(Protocol.Field(spawnText!, "mstate") == "1", "istemciye mstate|1 ulaşmadı");

    string BubbleText(byte[] packet) => Protocol.ReadVariants(packet)!.Single(v => v.Index == 2).Text;
    string ConsoleText(byte[] packet) => Protocol.ReadVariants(packet)!.Single(v => v.Index == 1).Text;
    // Modes off: the server line stays as is (no extra message), only the delay goes.
    int logsBeforeSpins = logs.Count;
    server.Send(SpinPacket("OnConsoleMessage", 0, "`7[``Ayse spun the wheel and got `431`7!]``", 1800));
    await Drive(() => consoles.Count == 1);
    Assert(ConsoleText(consoles[0]) == GameFeatures.ConsoleTag + "`w[`2REAL`w] [Ayse spun the wheel and got `431`w!]``" && GameFeatures.Delay(consoles[0]) == 0,
        "modlar kapalıyken konsol satırı yanlış: " + ConsoleText(consoles[0]));

    await Command("/reme");
    await Command("/qq", type: 3); // also intercepted when the client sends it as a game message
    Assert(logs[^2].Contains("Reme Modu: `2AKTIF") && logs[^1].Contains("QQ Modu: `2AKTIF"), "mod yanıtları yanlış: " + logs[^1]);
    logsBeforeSpins = logs.Count;

    server.Send(SpinPacket("OnTalkBubble", 5, "`7[```wBen`` spun the wheel and got `423`7!`7]``", 0));
    await Drive(() => bubbles.Count == 1);
    Assert(BubbleText(bubbles[0]) == "`w[`2REAL`w] [Ben spun the wheel and got `423`w!] `cREME `95 `#QQ `83``",
        "kendi balonun düzenlenmedi: " + BubbleText(bubbles[0]));
    server.Send(SpinPacket("OnConsoleMessage", 0, "`7[``Ayse spun the wheel and got `214`7!]``", 1800));
    await Drive(() => consoles.Count == 2);
    Assert(ConsoleText(consoles[1]) == GameFeatures.ConsoleTag + "`w[`2REAL`w] [Ayse spun the wheel and got `214`w!] `cREME `95 `#QQ `84``" && GameFeatures.Delay(consoles[1]) == 0,
        "diğer oyuncunun satırı düzenlenmedi: " + ConsoleText(consoles[1]));
    server.Send(SpinPacket("OnTalkBubble", 9, "`wHile spun the wheel and got `436`7!", 0));
    await Drive(() => bubbles.Count == 2);
    Assert(BubbleText(bubbles[1]) == "`w[`4FAKE`w]`` `wHile spun the wheel and got `436`7!", "sahte balon yanlış: " + BubbleText(bubbles[1]));
    Assert(logs.Count == logsBeforeSpins, "rulet için ekstra mesaj gönderildi: " + string.Join(" / ", logs.Skip(logsBeforeSpins)));

    await Command("/ping");
    await Drive(() => names.Count >= 1);
    Assert(GameFeatures.TryNameChanged(names[^1], out int tagged, out string tag) && tagged == 5
        && System.Text.RegularExpressions.Regex.IsMatch(tag, @"^`[294b]\[\d+\]`` `wBen``$"), "ping etiketi yanlış: " + tag);
    int namesBefore = names.Count;
    await Command("/ping");
    await Drive(() => names.Count > namesBefore);
    Assert(GameFeatures.TryNameChanged(names[^1], out _, out string restored) && restored == "`wBen``", "isim geri yüklenmedi: " + restored);

    await Command("/proxy");
    bool Checked(string dialog, string name, int value) =>
        System.Text.RegularExpressions.Regex.IsMatch(dialog, $@"(?m)^add_checkbox\|{name}\|[^\n]*\|{value}\|$");
    // /proxy: information only (commands, no toggles), with a button that opens /options.
    Assert(dialogs.Count == 1 && dialogs[0].Contains("end_dialog|zeus_proxy|") && !dialogs[0].Contains("add_checkbox")
        && dialogs[0].Contains("/warp DUNYA") && dialogs[0].Contains("/options") && dialogs[0].Contains("/path")
        && dialogs[0].Contains("add_button|zeus_open_options|") && dialogs[0].Contains("add_quick_exit|"), "bilgi paneli yanlış: " + dialogs[0]);
    client.Send(Protocol.TextPacket(2, "action|dialog_return\ndialog_name|zeus_proxy\nbuttonClicked|zeus_open_options\n"));
    await Drive(() => dialogs.Count == 2);
    // /options: the toggles with a short explanation each.
    Assert(dialogs[1].Contains("end_dialog|zeus_options|") && Checked(dialogs[1], "zeus_reme", 1) && Checked(dialogs[1], "zeus_qq", 1)
        && Checked(dialogs[1], "zeus_ping", 0) && Checked(dialogs[1], "zeus_antigravity", 0) && Checked(dialogs[1], "zeus_path", 0)
        && dialogs[1].Contains("add_button|zeus_keep|"), "ayar paneli yanlış: " + dialogs[1]);
    await Command("/options");
    Assert(dialogs.Count == 3 && dialogs[2].Contains("end_dialog|zeus_options|"), "/options açılmadı");
    int beforeApply = logs.Count;
    client.Send(Protocol.TextPacket(2, "action|dialog_return\ndialog_name|zeus_options\nzeus_reme|0\nzeus_qq|1\nzeus_ping|0\nzeus_antigravity|0\nzeus_path|0\n"));
    await Drive(() => logs.Count > beforeApply);
    Assert(logs[^1].Contains("Ayarlar uygulandi") && logs[^1].Contains("Reme: `4KAPALI") && logs[^1].Contains("QQ: `2AKTIF"), "panel ayarı uygulanmadı: " + logs[^1]);

    // The client sometimes appends a stray non-UTF-8 byte; the command must still be recognised.
    // The terminator byte is not always zeroed by the client: seen live as "/remeu", "/qqR", "/reme�".
    foreach (byte garbage in new byte[] { (byte)'u', 0xB0, (byte)'R' })
    {
        byte[] stray = [.. Protocol.TextPacket(2, "action|input\n|text|/reme").SkipLast(1), garbage];
        int beforeStray = logs.Count;
        client.Send(stray);
        await Drive(() => logs.Count > beforeStray);
        Assert(logs[^1].Contains("Reme Modu:"), $"sonu '{(char)garbage}' olan /reme yakalanmadı: " + logs[^1]);
    }
    Assert(logs[^1].Contains("Reme Modu: `2AKTIF"), "/reme üç kez geçiş yapmadı: " + logs[^1]);

    // Tutorial packets are dropped; normal packets around them still arrive.
    int consolesBefore = consoles.Count, dialogsBefore = dialogs.Count;
    server.Send(Protocol.FunctionPacket([Variant.String(0, "OnTutorialArrow"), Variant.UInt(1, 5)]));
    server.Send(Protocol.FunctionPacket([Variant.String(0, "OnFtueButtonDataSet"), Variant.String(1, "step|1")]));
    server.Send(GameFeatures.DialogPacket("add_label|big|Hosgeldin|left|\nend_dialog|tutorial_intro|||\n"));
    server.Send(Protocol.FunctionPacket([Variant.String(0, "OnConsoleMessage"), Variant.String(1, "sentinel")]));
    await Drive(() => consoles.Count > consolesBefore);
    Assert(dialogs.Count == dialogsBefore && ConsoleText(consoles[^1]) == GameFeatures.ConsoleTag + "sentinel", "tutorial paketi istemciye ulaştı");

    Assert(logs.Concat(dialogs).All(t => t.All(c => c < 128)), "oyuna ASCII dışı karakter gitti");
    byte[] help = Protocol.TextPacket(2, "action|input\n|text|/help");
    client.Send(help);
    await Drive(() => serverTexts.Count > 0);
    Assert(serverTexts.Count == 1 && serverTexts[0] == Protocol.Text(help), "proxy komutu sunucuya sızdı: " + string.Join(" / ", serverTexts));
});
await Run("/relog ve /antigravity", async () =>
{
    using var server = new NativeHost(); ushort port; using (var p = new NativeHost()) port = p.Port;
    await using var relay = new RelayEngine(); await relay.StartAsync(new Profile { LocalPort = port, ServerPort = server.Port });
    using var client = new NativeHost(); client.Connect("127.0.0.1", port);
    await Pump(() => server.Connected && client.Connected, client, server);
    var logs = new List<string>(); var raw = new List<byte[]>(); var serverTexts = new List<string>(); int spawns = 0;
    async Task Drive(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            NativeEvent? e;
            while ((e = client.Poll()) != null)
            {
                if (e.Type != 3) continue;
                if (Protocol.Type(e.Data) == 3) logs.Add(Protocol.Text(e.Data));
                else if (Protocol.Type(e.Data) == 4 && e.Data[4] == 1 && GameFeatures.FunctionName(e.Data) == "OnSpawn") spawns++;
                else if (Protocol.Type(e.Data) == 4 && e.Data[4] != 1 && e.Data[4] != 4) raw.Add(e.Data);
            }
            while ((e = server.Poll()) != null) if (e.Type == 3) serverTexts.Add(Protocol.Text(e.Data));
            client.Flush(); server.Flush();
            if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("test zaman aşımı; son log: " + logs.LastOrDefault());
            await Task.Delay(2);
        }
    }
    void Command(string text) => client.Send(Protocol.TextPacket(2, "action|input\n|text|" + text));

    await Command2("/relog");
    async Task Command2(string text) { int before = logs.Count; Command(text); await Drive(() => logs.Count > before); }
    Assert(logs[^1].Contains("Relog yapilamadi") && serverTexts.Count == 0, "dünya yokken relog: " + logs[^1]);

    // World map data (name at offset 68), then the local spawn.
    string worldName = "TESTWORLD";
    byte[] map = new byte[68 + worldName.Length];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(map, 4); map[4] = 4;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(56), (uint)(8 + worldName.Length));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(66), (ushort)worldName.Length);
    System.Text.Encoding.ASCII.GetBytes(worldName, map.AsSpan(68));
    server.Send(map);
    server.Send(Protocol.FunctionPacket([Variant.String(0, "OnSpawn"), Variant.String(1, "netID|7\nname|`wBen``\nposXY|320|640\nmstate|0\ntype|local\n")]));
    await Drive(() => spawns == 1);

    await Command2("/antigravity");
    Assert(logs[^1].Contains("Anti Gravity: `2AKTIF"), "antigravity açılmadı: " + logs[^1]);
    await Drive(() => raw.Count >= 3);
    int Int(byte[] p, int at) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(at));
    float Float(byte[] p, int at) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at));
    Assert(raw[0][4] == 3 && Int(raw[0], 24) == 4992 && Int(raw[0], 48) == 99 && Int(raw[0], 52) == 59, "jeneratör yerleştirilmedi");
    Assert(raw[1][4] == 8 && Int(raw[1], 8) == 7 && Float(raw[1], 28) == 320 && Float(raw[1], 32) == 640 && Int(raw[1], 48) == 99, "jeneratör etkinleştirilmedi");
    Assert(raw[2][4] == 0 && Int(raw[2], 16) == 2592 && Int(raw[2], 24) == 18, "yumruk durumu gönderilmedi");
    Assert(serverTexts.Count == 0, "antigravity sunucuya paket gönderdi");

    await Command2("/relog");
    await Drive(() => serverTexts.Count >= 2);
    Assert(serverTexts[0] == "action|quit_to_exit" && serverTexts[1] == "action|join_request\nname|TESTWORLD\ninvitedWorld|0",
        "relog paketleri yanlış: " + string.Join(" / ", serverTexts));

    // Explicit on while already on: no change, no surprise.
    await Command2("/antigravity on");
    Assert(logs[^1].Contains("zaten `2AKTIF") && raw.Count == 3, "açıkken '/antigravity on' durum değiştirdi: " + logs[^1]);
    // Turning it off removes the client-only generator in place: no relog.
    await Command2("/antigravity");
    Assert(logs[^1].Contains("Anti Gravity: `4KAPALI"), "antigravity kapanmadı: " + logs[^1]);
    await Drive(() => raw.Count >= 4);
    Assert(raw[3][4] == 3 && Int(raw[3], 24) == 18 && Int(raw[3], 48) == 99 && Int(raw[3], 52) == 59, "jeneratör kaldırılmadı");
    await Drive(() => true);
    Assert(serverTexts.Count == 2, "antigravity kapatınca relog yapıldı: " + string.Join(" / ", serverTexts));
});
await Run("items.dat / harita okuma / erişimli path", () =>
{
    var items = ItemDatabase.Parse(PathFixtures.ItemsDat());
    Assert(items.Version == 27 && items.Items.Count == 250 && items[PathFixtures.Entrance]!.Name == "House Entrance"
        && items[PathFixtures.Entrance]!.Collision == 3 && items[PathFixtures.Dirt]!.Collision == 1, "sentetik items.dat okunamadı");
    if (File.Exists(ItemDatabase.DefaultPath))
    {
        // The real file on this machine: every item must parse to the very last byte.
        var real = ItemDatabase.Load(ItemDatabase.DefaultPath);
        Assert(real.Items.Count > 10_000 && real[2]!.Name == "Dirt" && real[2]!.Collision == 1 && real[102]!.Collision == 2, "gerçek items.dat yanlış okundu");
    }
    const uint me = 5, other = 99;
    WorldMap Map(byte[] packet) => WorldMap.Parse(packet, items);
    var locked = Map(PathFixtures.WalledWorld(other));
    Assert(locked.Name == "PATHTEST" && locked.Width == 7 && locked.Height == 3, "harita başlığı yanlış");
    Assert(locked.FindPath(0, 1, 2, 1, me)!.Count == 2, "aynı taraftaki yol bulunamadı");
    Assert(locked.FindPath(0, 1, 5, 1, me) == null && !locked.Passable(3, 1, me), "başkasının kilitli kapısından geçildi");
    Assert(!locked.Passable(3, 0, me) && locked.Passable(3, 1, other), "duvar/kapı sahibi kuralı yanlış");
    var path = Map(PathFixtures.WalledWorld(other, publicEntrance: true)).FindPath(0, 1, 5, 1, me);
    Assert(path != null && path.Count == 5 && path[2] == (3, 1), "herkese açık kapıdan geçilemedi");
    Assert(Map(PathFixtures.WalledWorld(other, access: [me])).FindPath(0, 1, 5, 1, me) != null, "erişim listesindeki oyuncu geçemedi");
    Assert(Map(PathFixtures.WalledWorld(other, worldLockOwner: me)).FindPath(0, 1, 5, 1, me) != null, "dünya sahibi geçemedi");
    // Tile changes keep the map current: breaking the wall opens a way around the locked door.
    locked.ApplyTileChange(3, 2, 18);
    Assert(locked.FindPath(0, 1, 5, 1, me) is { Count: 7 }, "kırılan blok haritaya işlenmedi");
    locked.ApplyTileChange(3, 2, PathFixtures.Dirt);
    Assert(locked.FindPath(0, 1, 5, 1, me) == null, "konan blok haritaya işlenmedi");
    // A world that cannot be read is refused instead of guessed.
    byte[] broken = PathFixtures.WalledWorld(other);
    Array.Resize(ref broken, broken.Length - 30);
    bool refused = false;
    try { Map(broken); } catch (InvalidDataException) { refused = true; }
    Assert(refused, "bozuk harita kabul edildi");
    return Task.CompletedTask;
});
await Run("Kamera modeli: imleçten karoya", () =>
{
    var camera = new CameraModel();
    // Nothing is assumed about the zoom: no measured click, no answer (a stale guess would teleport wrongly).
    Assert(!camera.TryTile(1280, 720, 640, 360, 50.5, 30.5, out _, out _), "ölçülmemiş yakınlaştırmada tahmin yapıldı");
    // 52 px per tile, player slightly above centre (offset -20 px vertically).
    double scale = 52, oy = -20;
    (double X, double Y) Screen(int tx, int ty, double px, double py) => (640 + scale * (tx + 0.5 - px), 360 + oy + scale * (ty + 0.5 - py));
    var p = (X: 50.5, Y: 30.5);
    foreach (var (tx, ty) in new[] { (52, 30), (49, 31), (50, 28), (48, 30) })
    {
        var sp = Screen(tx, ty, p.X, p.Y);
        camera.AddSample(1280, 720, sp.X, sp.Y, p.X, p.Y, tx, ty);
    }
    // A mis-paired click is ignored.
    camera.AddSample(1280, 720, 10, 10, p.X, p.Y, 52, 30);
    foreach (var (tx, ty) in new[] { (60, 25), (41, 35), (50, 30), (55, 34) })
    {
        var sp = Screen(tx, ty, 47.5, 33.5);
        Assert(camera.TryTile(1280, 720, sp.X, sp.Y, 47.5, 33.5, out int gx, out int gy) && gx == tx && gy == ty, $"imleç karosu yanlış: {tx},{ty} yerine {gx},{gy}");
    }
    Assert(!camera.TryTile(1920, 1080, 960 + 52 * 3, 540, 50.5, 30.5, out _, out _), "pencere boyutu değişince eski ölçek kullanıldı");

    // Zoom levels set by Zeus: level 0 = 70 px per tile, each level up multiplies by 1.12.
    var zoom = new CameraModel();
    double z = 70;
    (double X, double Y) Z(int tx, int ty) => (640 + z * (tx + 0.5 - 50.5), 360 + z * (ty + 0.5 - 30.5));
    void Click(int tx, int ty) { var sp = Z(tx, ty); zoom.AddSample(1280, 720, sp.X, sp.Y, 50.5, 30.5, tx, ty); }
    bool Hits(CameraModel model, int tx, int ty) { var sp = Z(tx, ty); return model.TryTile(1280, 720, sp.X, sp.Y, 50.5, 30.5, out int gx, out int gy) && gx == tx && gy == ty; }
    Click(52, 30); Click(49, 29);
    Assert(Hits(zoom, 62, 25) && Hits(zoom, 40, 36), "seviye 0 tıklamalardan ölçülmedi");
    zoom.SetLevel(-3); z = 70 * Math.Pow(1.12, -3);
    Assert(!zoom.TryTile(1280, 720, 100, 100, 50.5, 30.5, out _, out _), "seviye katsayısı bilinmeden tahmin yapıldı");
    Click(52, 31); Click(48, 30);
    Assert(Hits(zoom, 70, 20) && Hits(zoom, 30, 44), "yeni seviye kendi tıklamalarıyla ölçülmedi");
    Assert(Math.Abs(zoom.Memory.StepFactor - 1.12) < 0.01 && Math.Abs(zoom.Memory.Scale0 - 70) < 1, $"seviye değerleri öğrenilmedi: {zoom.Memory}");
    zoom.SetLevel(-7); z = 70 * Math.Pow(1.12, -7);
    Assert(Hits(zoom, 78, 18) && Hits(zoom, 22, 47), "ölçülmemiş seviye katsayıyla hesaplanmadı");
    // Zeus sets the same zoom in every session, so a new run starts with every level known.
    var next = new CameraModel(zoom.Memory);
    next.SetLevel(2); z = 70 * Math.Pow(1.12, 2);
    Assert(Hits(next, 58, 26) && Hits(next, 44, 34), "kayıtlı seviye ölçeği yeni oturumda kullanılmadı");

    // Near the world's edges the camera stops (100x60 world, 40 px per tile = 16 x 9 tiles each side of the centre).
    var edge = new CameraModel();
    double Cam(double player, double half, int size) => Math.Clamp(player, half, size - half);
    (double X, double Y) At(int tx, int ty, double px, double py) =>
        (640 + 40 * (tx + 0.5 - Cam(px, 16, 100)), 360 + 40 * (ty + 0.5 - Cam(py, 9, 60)));
    foreach (var (tx, ty, px, py) in new[] { (5, 30, 3.5, 30.5), (1, 28, 3.5, 30.5), (8, 33, 4.5, 31.5), (50, 30, 50.5, 30.5) })
    {
        var sp = At(tx, ty, px, py);
        edge.AddSample(1280, 720, sp.X, sp.Y, px, py, tx, ty, 100, 60);
    }
    foreach (var (tx, ty, px, py) in new[] { (0, 2, 2.5, 3.5), (20, 5, 2.5, 3.5), (99, 58, 97.5, 57.5), (85, 50, 97.5, 57.5), (45, 30, 50.5, 30.5) })
    {
        var sp = At(tx, ty, px, py);
        Assert(edge.TryTile(1280, 720, sp.X, sp.Y, px, py, out int gx, out int gy, 100, 60) && gx == tx && gy == ty,
            $"dünya kenarında imleç karosu yanlış: {tx},{ty} yerine {gx},{gy}");
    }
    var memZoom = new CameraModel();
    memZoom.SetZoom(2.0);
    foreach (var (tx, ty) in new[] { (52, 31), (53, 29), (54, 31) })
        memZoom.AddSample(1280, 720, 640 + 40 * (tx + 0.5 - 50.5), 360 + 40 * (ty + 0.5 - 30.5), 50.5, 30.5, tx, ty);
    Assert(memZoom.Memory.ZoomHeight == 720 && memZoom.Memory.ZoomK > 0, $"bellek zoom korunmadı: {memZoom.Memory}");
    return Task.CompletedTask;
});
await Run("Ekran görüntüsünden karo bulma", ScreenLocatorTest);
await Run("Proxy üzerinden /path", async () =>
{
    string fixture = Path.Combine(Path.GetTempPath(), "zeus-path-" + Guid.NewGuid().ToString("N"));
    string items = Path.Combine(fixture, "cache", "items.dat");
    Directory.CreateDirectory(Path.Combine(fixture, "cache"));
    File.WriteAllBytes(items, PathFixtures.ItemsDat());
    string previousItems = RelayEngine.ItemsPath;
    RelayEngine.ItemsPath = items;
    try
    {
        using var server = new NativeHost(); ushort port; using (var p = new NativeHost()) port = p.Port;
        await using var relay = new RelayEngine(); await relay.StartAsync(new Profile { LocalPort = port, ServerPort = server.Port });
        using var client = new NativeHost(); client.Connect("127.0.0.1", port);
        await Pump(() => server.Connected && client.Connected, client, server);
        var logs = new List<string>(); var steps = new List<byte[]>(); var setPos = new List<byte[]>(); int spawns = 0;
        var clicks = new List<byte[]>(); var states = new List<byte[]>(); var overlays = new List<string>(); bool shift = false;
        RelayEngine.IsShiftDown = () => shift;
        async Task Drive(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                NativeEvent? e;
                while ((e = client.Poll()) != null)
                {
                    if (e.Type != 3) continue;
                    if (Protocol.Type(e.Data) == 3) logs.Add(Protocol.Text(e.Data));
                    else if (Protocol.Type(e.Data) == 4 && e.Data[4] == 1)
                    {
                        string? f = GameFeatures.FunctionName(e.Data);
                        if (f == "OnSpawn") spawns++;
                        if (f == "OnSetPos") setPos.Add(e.Data);
                        if (f == "OnTextOverlay") overlays.Add(Protocol.ReadVariants(e.Data)!.Single(v => v.Index == 1).Text);
                    }
                    else if (Protocol.Type(e.Data) == 4 && e.Data[4] == 20) states.Add(e.Data);
                }
                while ((e = server.Poll()) != null)
                {
                    if (e.Type != 3 || Protocol.Type(e.Data) != 4) continue;
                    if (e.Data[4] == 0) steps.Add(e.Data);
                    if (e.Data[4] == 3) clicks.Add(e.Data);
                }
                client.Flush(); server.Flush();
                if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("test zaman aşımı; son log: " + logs.LastOrDefault());
                await Task.Delay(2);
            }
        }
        async Task Command(string text) { int before = logs.Count; client.Send(Protocol.TextPacket(2, "action|input\n|text|" + text)); await Drive(() => logs.Count > before); }
        void Enter(byte[] map)
        {
            server.Send(map);
            server.Send(Protocol.FunctionPacket([Variant.String(0, "OnSpawn"), Variant.String(1, "netID|7\nuserID|5\nname|`wBen``\nposXY|0|32\nmstate|0\ntype|local\n")]));
        }
        int I(byte[] p, int at) => BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(at));
        float F(byte[] p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at));
        byte[] Click(int x, int y) => GameFeatures.GameUpdate(3, intData: 18, tileX: x, tileY: y);
        async Task<string> Route(string command)
        {
            int before = overlays.Count;
            client.Send(Protocol.TextPacket(2, "action|input\n|text|" + command));
            await Drive(() => overlays.Count > before);
            return overlays[^1];
        }
        Enter(PathFixtures.WalledWorld(99));
        await Drive(() => spawns == 1);
        // The server's character state reaches the client unchanged while path is off.
        byte[] state = GameFeatures.GameUpdate(20, netId: 7); state[6] = 128; state[7] = 128;
        server.Send(state);
        await Drive(() => states.Count == 1);
        Assert(states[0][6] == 128 && states[0][7] == 128, "path kapalıyken menzil değişti");

        await Command("/path");
        Assert(logs[^1].Contains("Path: `2AKTIF") && logs[^1].Contains("Shift"), "path açılmadı: " + logs[^1]);
        await Drive(() => true);
        Assert(states.Count == 1, "path karakter durumunu değiştirdi");

        // With no hook, a packet target can be the game's reach limit.
        shift = true;
        int logsBefore = logs.Count;
        client.Send(Click(5, 1));
        await Drive(() => overlays.Count == 1);
        Assert(overlays[0].Contains("belirlenemedi") && steps.Count == 0 && clicks.Count == 0 && logs.Count == logsBefore,
            "fare izleme yokken path tahmin etti: " + overlays[0]);
        Assert((await Route("/path 5 1")).Contains("Yol bulunamadi") && setPos.Count == 0, "kilitli kapıdan path yapıldı");
        Assert((await Route("/path 3 0")).Contains("Yol bulunamadi") && setPos.Count == 0, "engelin yanına gidildi");
        steps.Clear(); setPos.Clear();
        // Without Shift a click is a normal punch and reaches the server unchanged.
        shift = false;
        client.Send(Click(1, 1));
        await Drive(() => clicks.Count == 1);
        Assert(I(clicks[0], 48) == 1, "normal tıklama sunucuya gitmedi");

        Enter(PathFixtures.WalledWorld(99, publicEntrance: true));
        await Drive(() => spawns == 2);
        shift = true;
        await Route("/path 5 1");
        await Drive(() => steps.Count >= 5 && setPos.Count >= 1);
        Assert(steps.Select(s => (I(s, 48), I(s, 52))).SequenceEqual([(1, 1), (2, 1), (3, 1), (4, 1), (5, 1)])
            && F(steps[^1], 28) == 160 && F(steps[^1], 32) == 32, "path adımları yanlış");
        var pos = Protocol.ReadVariants(setPos[0])!.Single(v => v.Index == 1);
        Assert(I(setPos[0], 8) == 7 && pos.Type == 3 && BinaryPrimitives.ReadSingleLittleEndian(pos.Bytes) == 160, "OnSetPos yanlış");
        Assert(clicks.Count == 1 && overlays[^1].Contains("Path 5 blok"), "Shift + tık sunucuya yumruk olarak gitti");

        RelayEngine.MouseHookActive = true;
        Enter(PathFixtures.MapPacket("LONGPATH", 100, 5, (x, y) =>
            x == 40 && y == 0 ? new PathFixtures.Tile(PathFixtures.Dirt) : null));
        await Drive(() => spawns == 3);

        var picker = new TaskCompletionSource<PathMapView>(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.PathMapRequested += view => picker.TrySetResult(view);
        int lastOverlay = overlays.Count;
        steps.Clear(); setPos.Clear();
        RelayEngine.MouseEvents.Enqueue(new MouseEvent(MouseEventKind.LeftDown, 752, 305, 800, 600, true, Timestamp: Stopwatch.GetTimestamp(), WindowId: 42));
        client.Send(GameFeatures.GameUpdate(0, flags: 0x820, x: 0, y: 32, tileX: 3, tileY: 1));
        await Drive(() => overlays.Count > lastOverlay);
        Assert(overlays[^1].Contains("belirlenemedi") && setPos.Count == 0 && !picker.Task.IsCompleted,
            "belirsiz tıklama rota başlattı veya haritayı açtı");
        client.Send(Protocol.TextPacket(2, "action|input\n|text|/path map"));
        await Drive(() => picker.Task.IsCompleted);
        var view = await picker.Task;
        Assert(view.World == "LONGPATH" && view.Width == 100 && view.Height == 5 && view.PlayerX == 0 && view.PlayerY == 1
            && !view.OpenTiles[40] && view.OpenTiles[100 + 84], "karo haritası yanlış");

        Assert(relay.QueuePathTarget(view.Id, 84, 1), "harita seçimi kuyruğa alınmadı");
        await Drive(() => setPos.Count == 1 && steps.Any(s => I(s, 48) == 84));
        Assert(steps.Where(s => I(s, 24) == 2250).Select(s => I(s, 48)).SequenceEqual(Enumerable.Range(1, 84)) && overlays[^1].Contains("Path 84 blok")
            && BinaryPrimitives.ReadSingleLittleEndian(Protocol.ReadVariants(setPos[0])!.Single(v => v.Index == 1).Bytes) == 84 * 32,
            $"ilk uzun path tam hedefe ulaşmadı: {steps.Count} paket, {overlays[^1]}");

        Assert((await Route("/path 40 0")).Contains("Yol bulunamadi") && setPos.Count == 1, "engelli hedefin yanına gidildi");
        Assert((await Route("/path 94 1")).Contains("Path 10 blok") && setPos.Count == 2
            && BinaryPrimitives.ReadSingleLittleEndian(Protocol.ReadVariants(setPos[^1])!.Single(v => v.Index == 1).Bytes) == 94 * 32,
            "koordinat komutu tam hedefe ulaşmadı");

        Enter(PathFixtures.WalledWorld(99));
        await Drive(() => spawns == 4);
        lastOverlay = overlays.Count;
        Assert(relay.QueuePathTarget(view.Id, 2, 1), "eski harita seçimi taşınamadı");
        await Drive(() => overlays.Count > lastOverlay);
        Assert(setPos.Count == 2 && overlays[^1].Contains("/path map"), "önceki dünyanın haritasıyla path yapıldı");

        // The zoom stays the player's: Zeus never sends a zoom of its own.
        Enter(PathFixtures.MapPacket("ZOOMPATH", 100, 5, (x, y) => null));
        await Drive(() => spawns == 5);
        await Task.Delay(1200);

        // 800x600 window, 40 px per tile. The player stands at (0, 1): the camera is held at x = 10
        // by the world's left edge, and at y = 2.5 because the 5-row world is shorter than the view.
        int Sx(double tile, double camera) => (int)Math.Round(400 + 40 * (tile + 0.5 - camera));
        int Sy(double tile) => (int)Math.Round(300 + 40 * (tile + 0.5 - 2.5));
        // A nearby Shift click the game reports exactly: used directly and it teaches level 0's scale.
        steps.Clear(); setPos.Clear(); lastOverlay = overlays.Count;
        RelayEngine.MouseEvents.Enqueue(new MouseEvent(MouseEventKind.LeftDown, Sx(2, 10), Sy(1), 800, 600, true, Timestamp: Stopwatch.GetTimestamp(), WindowId: 42));
        await Drive(() => true); await Task.Delay(10); await Drive(() => true);
        client.Send(GameFeatures.GameUpdate(0, flags: 0x820, x: 0, y: 32, tileX: 2, tileY: 1));
        await Drive(() => setPos.Count == 1);
        Assert(overlays[^1].Contains("Path 2 blok"), "yakın Shift tıklaması kullanılmadı: " + overlays[^1]);
        await Task.Delay(1000);
        // Now a far Shift click resolves from the cursor alone.
        steps.Clear(); setPos.Clear();
        RelayEngine.MouseEvents.Enqueue(new MouseEvent(MouseEventKind.LeftDown, Sx(12, 10), Sy(1), 800, 600, true, Timestamp: Stopwatch.GetTimestamp(), WindowId: 42));
        await Drive(() => setPos.Count == 1);
        Assert(I(steps[^1], 48) == 12 && I(steps[^1], 52) == 1 && overlays[^1].Contains("Path 10 blok"), "uzak Shift tıklaması imleçten çözülmedi: " + overlays[^1]);

        // With the game's zoom value read from its memory, one nearby click teaches scale = K × zoom;
        // after the zoom changes, a far click needs no new measurement.
        double gameZoom = 2.0;
        RelayEngine.GameZoom = () => gameZoom;
        await Task.Delay(1000);
        steps.Clear(); setPos.Clear();
        RelayEngine.MouseEvents.Enqueue(new MouseEvent(MouseEventKind.LeftDown, Sx(13, 12.5), Sy(1), 800, 600, true, Timestamp: Stopwatch.GetTimestamp(), WindowId: 42));
        await Drive(() => true); await Task.Delay(10); await Drive(() => true);
        client.Send(GameFeatures.GameUpdate(0, flags: 0x820, x: 384, y: 32, tileX: 13, tileY: 1));
        await Drive(() => setPos.Count == 1);
        await Task.Delay(1000);
        gameZoom = 1.5; // 30 px per tile now; the camera is held at x = 400 / 30 by the left edge
        int Zx(double tile) => (int)Math.Round(400 + 30 * (tile + 0.5 - 400 / 30.0));
        int Zy(double tile) => (int)Math.Round(300 + 30 * (tile + 0.5 - 2.5));
        steps.Clear(); setPos.Clear();
        RelayEngine.MouseEvents.Enqueue(new MouseEvent(MouseEventKind.LeftDown, Zx(21), Zy(1), 800, 600, true, Timestamp: Stopwatch.GetTimestamp(), WindowId: 42));
        await Drive(() => setPos.Count == 1);
        Assert(I(steps[^1], 48) == 21 && overlays[^1].Contains("Path 8 blok"), "oyunun yakınlaştırma değeriyle uzak tıklama çözülmedi: " + overlays[^1]
            + " | " + string.Join(" / ", relay.Telemetry.Snapshot().Logs.TakeLast(4).Select(l => l.Message)));
        RelayEngine.GameZoom = null;

        await Command("/path off");
        Assert(logs[^1].Contains("Path: `4KAPALI"), "path kapanmadı: " + logs[^1]);
    }
    finally { RelayEngine.ItemsPath = previousItems; RelayEngine.IsShiftDown = null; RelayEngine.MouseHookActive = false; RelayEngine.GameZoom = null; RelayEngine.MouseEvents.Clear(); Directory.Delete(fixture, recursive: true); }
});
await Run("Yerel tanı testi açık oturumun PathEnabled bayrağını bozmaz", async () =>
{
    bool before = RelayEngine.PathEnabled;
    RelayEngine.PathEnabled = true;
    try
    {
        await LocalDiagnostics.RunAsync();
        Assert(RelayEngine.PathEnabled, "LocalDiagnostics PathEnabled bayrağını sıfırladı");
    }
    finally { RelayEngine.PathEnabled = before; }
});
await Run("/keep ayarları kalıcı kaydeder ve yeniden açılışta yükler", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "zeus-keep-" + Guid.NewGuid().ToString("N"));
    RelayEngine.SettingsPath = Path.Combine(directory, "settings.json");
    try
    {
        for (int run = 0; run < 2; run++)
        {
            using var server = new NativeHost(); ushort port; using (var p = new NativeHost()) port = p.Port;
            await using var relay = new RelayEngine(); await relay.StartAsync(new Profile { LocalPort = port, ServerPort = server.Port });
            using var client = new NativeHost(); client.Connect("127.0.0.1", port);
            await Pump(() => server.Connected && client.Connected, client, server);
            var logs = new List<string>(); string? dialog = null;
            void Collect(NativeEvent e)
            {
                if (e.Type != 3) return;
                if (Protocol.Type(e.Data) == 3) logs.Add(Protocol.Text(e.Data));
                else if (GameFeatures.FunctionName(e.Data) == "OnDialogRequest") dialog = Protocol.ReadVariants(e.Data)!.Single(v => v.Index == 1).Text;
            }
            async Task Send(string text, Func<bool> done) { client.Send(Protocol.TextPacket(2, text)); await Pump(done, server, client, Collect); }
            if (run == 0)
            {
                await Send("action|input\n|text|/reme", () => logs.Count == 1);
                await Send("action|input\n|text|/ping", () => logs.Count == 2);
                await Send("action|input\n|text|/keep", () => logs.Count == 3);
                Assert(logs[^1].Contains("kalici olarak kaydedildi") && File.Exists(RelayEngine.SettingsPath), "keep kaydetmedi: " + logs[^1]);
                Assert(GameSettings.Load(RelayEngine.SettingsPath) == new GameSettings(true, false, true), "kayıtlı ayarlar yanlış");
            }
            else
            {
                server.Send(Protocol.FunctionPacket([Variant.String(0, "OnSuperMainStartAcceptLogonHrdxs47254722215a")]));
                await Pump(() => logs.Any(l => l.Contains("Kayitli ayarlarin yuklendi")), server, client, Collect);
                await Send("action|input\n|text|/options", () => dialog != null);
                Assert(System.Text.RegularExpressions.Regex.IsMatch(dialog!, @"(?m)^add_checkbox\|zeus_reme\|[^\n]*\|1\|$")
                    && System.Text.RegularExpressions.Regex.IsMatch(dialog!, @"(?m)^add_checkbox\|zeus_qq\|[^\n]*\|0\|$"), "ayarlar yüklenmedi: " + dialog);
                // The panel's keep button applies the checkboxes and saves them.
                await Send("action|dialog_return\ndialog_name|zeus_options\nbuttonClicked|zeus_keep\nzeus_reme|0\nzeus_qq|1\nzeus_ping|0\n", () => logs.Any(l => l.Contains("kalici olarak kaydedildi")));
                Assert(GameSettings.Load(RelayEngine.SettingsPath) == new GameSettings(false, true, false), "panel kaydet düğmesi çalışmadı");
            }
        }
    }
    finally { RelayEngine.SettingsPath = null; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
await Run("Profil doğrulama / atomik kayıt / yeniden okuma", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "zeus-test-" + Guid.NewGuid().ToString("N"));
    var store = new ProfileStore(directory); var profile = new Profile { ServerHost = "example.com", Bookmarks = ["ZEUS", "START"] };
    store.Save(profile); var read = store.Load(); Assert(read.ServerHost == profile.ServerHost && read.Bookmarks.SequenceEqual(profile.Bookmarks), "profil değişti");
    bool rejected = false; try { (profile with { LocalPort = 0 }).Validate(); } catch (ArgumentException) { rejected = true; }
    Assert(rejected, "geçersiz port kabul edildi");
    rejected = false; try { (profile with { Meta = "x\nserver|bad" }).Validate(); } catch (ArgumentException) { rejected = true; }
    Assert(rejected, "meta satır enjeksiyonu kabul edildi");
    File.Delete(store.FilePath); Directory.Delete(directory); return Task.CompletedTask;
});
await Run("HTTP bootstrap / keşif / kapatma", async () =>
{
    int port = FreeTcpPort();
    var profile = new Profile { EnableBootstrap = true, BootstrapPort = port, Meta = "metadata" };
    await using var bootstrap = new BootstrapServer(); await bootstrap.StartAsync(profile, default);
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    string data = await http.GetStringAsync($"http://127.0.0.1:{port}/growtopia/server_data.php");
    var discovered = Discovery.Parse(data, profile with { LocalPort = 17192 }); Assert(discovered.ServerHost == "127.0.0.1" && discovered.ServerPort == 17191 && discovered.Meta == "metadata", "bootstrap yanıtı yanlış");
    using var notFound = await http.GetAsync($"http://127.0.0.1:{port}/game/../../profile.json"); Assert(notFound.StatusCode == HttpStatusCode.NotFound, "bilinmeyen yol servis edildi");
    using var method = await http.PutAsync($"http://127.0.0.1:{port}/growtopia/server_data.php", null); Assert(method.StatusCode == HttpStatusCode.MethodNotAllowed, "yanlış HTTP metodu kabul edildi");
});
await Run("Güncel giriş adresi ve ek sunucu alanlarının korunması", () =>
{
    string upstream = "server|213.179.209.175\nport|17042\nloginurl|login.growtopiagame.com\ntype|1\nmeta|sample\nfuture_field|preserve-this\nRTENDMARKERBS1001\n";
    var p = Discovery.Parse(upstream, new Profile());
    string local = BootstrapServer.BuildServerData(p);
    Assert(Protocol.Field(local, "server") == "127.0.0.1" && Protocol.Field(local, "port") == "17191", "yerel adres yazılmadı");
    Assert(Protocol.Field(local, "loginurl") == "login.growtopiagame.com" && Protocol.Field(local, "future_field") == "preserve-this", "giriş veya gelecekteki alan kayboldu");
    return Task.CompletedTask;
});
await Run("Başlat / durdur / portları serbest bırak", async () =>
{
    ushort port; using (var p = new NativeHost()) port = p.Port;
    await using var relay = new RelayEngine();
    for (int i = 0; i < 3; i++) { await relay.StartAsync(new Profile { LocalPort = port }); await relay.StopAsync(); using var rebound = new NativeHost(port); }
    Assert(!relay.Running, "iş parçacığı çalışmaya devam ediyor");
});
await Run("TLS bootstrap / kullanıcıya özel sertifika", async () =>
{
    if (!OperatingSystem.IsWindows()) return;
    using var cert = CertificateManager.GetOrCreate(); using var again = CertificateManager.GetOrCreate();
    Assert(cert.HasPrivateKey && cert.Thumbprint == again.Thumbprint, "sertifika yeniden kullanılmıyor");
    int port = FreeTcpPort();
    await using var server = new BootstrapServer(); await server.StartAsync(new Profile { BootstrapPort = port, BootstrapTls = true }, default);
    // Test trusts only this exact generated certificate; production discovery uses default TLS validation.
    using var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote?.Thumbprint == cert.Thumbprint };
    using var http = new HttpClient(handler);
    string data = await http.GetStringAsync($"https://127.0.0.1:{port}/health"); Assert(data.Contains("Zeus Proxy"), "TLS health yanıtı yok");
});
Console.WriteLine($"SONUÇ: {passed} başarılı, {failed} başarısız.");
Environment.ExitCode = failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch(Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static int FreeTcpPort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
static async Task Pump(Func<bool> done, NativeHost sender, NativeHost receiver, Action<NativeEvent>? receive = null)
{
    var timeout = Stopwatch.StartNew();
    while (!done())
    {
        while(sender.Poll() != null) { }
        NativeEvent? e; while((e = receiver.Poll()) != null) receive?.Invoke(e);
        sender.Flush(); receiver.Flush();
        if(timeout.ElapsedMilliseconds > 5000) throw new TimeoutException("ENet test zaman aşımı");
        await Task.Delay(2);
    }
}
static byte[] SpinPacket(string function, int netId, string text, int delay)
{
    var values = new List<Variant> { Variant.String(0, function) };
    if (function == "OnTalkBubble") { values.Add(Variant.UInt(1, (uint)netId, 9)); values.Add(Variant.String(2, text)); }
    else values.Add(Variant.String(1, text));
    byte[] packet = Protocol.FunctionPacket(values);
    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24), delay);
    return packet;
}
