using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Zeus.Core;

internal static class RecoveryRegression
{
    public static async Task RunAsync(bool waitForUnreachableTarget)
    {
        using var loginServer = new NativeHost();
        using var blackHole = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        ushort deadPort = (ushort)((IPEndPoint)blackHole.Client.LocalEndPoint!).Port;
        ushort localPort; using (var probe = new NativeHost()) localPort = probe.Port;
        var tcpProbe = new TcpListener(IPAddress.Loopback, 0); tcpProbe.Start();
        int httpPort = ((IPEndPoint)tcpProbe.LocalEndpoint).Port; tcpProbe.Stop();
        await using var relay = new RelayEngine();
        await relay.StartAsync(new Profile { LocalPort = localPort, ServerPort = loginServer.Port,
            EnableBootstrap = true, BootstrapPort = httpPort });
        var clients = new List<NativeHost>();
        try
        {
            var first = new NativeHost(); clients.Add(first); first.Connect("127.0.0.1", localPort);
            await Until(() => first.Connected && loginServer.Connected);
            bool redirected = false;
            loginServer.Send(Protocol.FunctionPacket([Variant.String(0, "OnSendToServer"), Variant.UInt(1, deadPort),
                Variant.UInt(2, 1001), Variant.UInt(3, 77), Variant.String(4, "127.0.0.1|DOOR")]));
            await Until(() => redirected, first, e => { if (e.Type == 3 && Protocol.ReadVariants(e.Data)?.FirstOrDefault(v => v.Index == 0)?.Text == "OnSendToServer") redirected = true; });
            if (waitForUnreachableTarget)
            {
                var failing = new NativeHost(); clients.Add(failing); failing.Connect("127.0.0.1", localPort);
                await Until(() => failing.Connected);
                await Until(() => relay.Telemetry.Snapshot().Logs.Any(l => l.Level == "UYARI"), maximumMs: 15000);
            }
            else
            {
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                string data = await http.GetStringAsync($"http://127.0.0.1:{httpPort}/growtopia/server_data.php");
                Check(Protocol.Field(data, "server") == "127.0.0.1", "Yeniden giriş adresi yanlış.");
                await Until(() => relay.Telemetry.Snapshot().Logs.Any(l => l.Message.Contains("önceki dünya geçişi temizlendi")));
            }
            var fresh = new NativeHost(); clients.Add(fresh); fresh.Connect("127.0.0.1", localPort);
            await Until(() => fresh.Connected && loginServer.Connected);
            bool gotLogin = false;
            byte[] login = Protocol.TextPacket(2, "fresh-login|safe-test"); fresh.Send(login);
            await Until(() => gotLogin, loginServer, e => { if (e.Type == 3 && e.Data.SequenceEqual(login)) gotLogin = true; });
            byte[] reply = Protocol.TextPacket(3, "recovered-login"); bool gotReply = false;
            loginServer.Send(reply);
            await Until(() => gotReply, fresh, e => { if(e.Type == 3 && e.Data.SequenceEqual(reply)) gotReply = true; });
            Check(relay.Telemetry.Snapshot().State == "Bağlı", "Yeniden giriş takılı kaldı.");
        }
        finally { foreach (var client in clients) client.Dispose(); }

        async Task Until(Func<bool> done, NativeHost? observed = null, Action<NativeEvent>? inspect = null, int maximumMs = 5000)
        {
            var timeout = Stopwatch.StartNew();
            while (!done())
            {
                foreach (var host in clients.Append(loginServer))
                {
                    NativeEvent? e;
                    while ((e = host.Poll()) != null) if (ReferenceEquals(host, observed)) inspect?.Invoke(e);
                    host.Flush();
                }
                if (timeout.ElapsedMilliseconds > maximumMs)
                    throw new TimeoutException(string.Join(" / ", relay.Telemetry.Snapshot().Logs.TakeLast(6).Select(l => l.Message)));
                await Task.Delay(2);
            }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
