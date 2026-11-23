using System.Diagnostics;
using Zeus.Core;

internal static class HandoffRegression
{
    public static async Task RunAsync(int hops, bool changeTransport, bool keepRetiringAlive = false)
    {
        ushort relayPort;
        using (var probe = new NativeHost()) relayPort = probe.Port;
        await using var relay = new RelayEngine();
        var servers = new List<NativeHost> { new() };
        var clients = new List<NativeHost> { new() };
        int hop = 0;
        try
        {
            await relay.StartAsync(new Profile { LocalPort = relayPort, ServerPort = servers[0].Port });
            clients[0].Connect("127.0.0.1", relayPort);
            await Until(() => clients[0].Connected && servers[0].Connected);
            for (hop = 1; hop <= hops; hop++)
            {
                int index = hop;
                var previousClient = clients[^1];
                var previousServer = servers[^1];
                var nextServer = new NativeHost(); servers.Add(nextServer);
                byte[] redirect = Protocol.FunctionPacket([
                    Variant.String(0, "OnSendToServer"), Variant.UInt(1, nextServer.Port),
                    Variant.UInt(2, (uint)(8000 + hop)), Variant.UInt(3, 77), Variant.String(4, "127.0.0.1|DOOR|UUID")]);
                bool receivedRedirect = false;
                previousServer.Send(redirect);
                await Until(() => receivedRedirect, previousClient, e =>
                {
                    if (e.Type == 3 && Protocol.ReadVariants(e.Data)?.FirstOrDefault(v => v.Index == 0)?.Text == "OnSendToServer")
                    {
                        Assert(Protocol.ReadVariants(e.Data)!.Single(v => v.Index == 1).Number == relayPort, "Yerel yönlendirme portu yanlış.");
                        receivedRedirect = true;
                    }
                });
                // The real client can begin its new connection before the old peer
                // sends DISCONNECT. Keep servicing the old peer to reproduce that overlap.
                var nextClient = new NativeHost(outgoingPrefix: changeTransport && hop % 2 == 1,
                    checksum: !changeTransport || hop % 3 != 0);
                clients.Add(nextClient);
                nextClient.Connect("127.0.0.1", relayPort);
                await Until(() => nextClient.Connected && nextServer.Connected);
                byte[] login = Protocol.TextPacket(2, $"token|{8000 + hop}\nuser|77");
                bool receivedLogin = false;
                nextClient.Send(login, 1);
                await Until(() => receivedLogin, nextServer, e =>
                {
                    if (e.Type == 3) { Assert(e.Channel == 1 && e.Data.SequenceEqual(login), "Geçiş giriş verisi / kanal değişti."); receivedLogin = true; }
                });
                // A late disconnect from the retiring connection must not tear down the new one.
                // keepRetiringAlive: retiring clients never disconnect and keep servicing
                // their socket, so stale peers would pile up if the relay did not release them.
                if (!keepRetiringAlive) { previousClient.Reset(); previousClient.Flush(); }
                byte[] reply = Protocol.TextPacket(3, "handoff-confirmed-" + index);
                bool receivedReply = false;
                nextServer.Send(reply);
                await Until(() => receivedReply, nextClient, e =>
                { if (e.Type == 3 && e.Data.SequenceEqual(reply)) receivedReply = true; });
                Assert(relay.Telemetry.Snapshot().State == "Bağlı", "Eski bağlantı yeni oturumu kapattı.");
            }
        }
        catch (Exception error)
        {
            throw new Exception($"Geçiş {hop}/{hops}: {error.Message}; " +
                string.Join(" / ", relay.Telemetry.Snapshot().Logs.TakeLast(6).Select(l => l.Message)), error);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            foreach (var server in servers) server.Dispose();
        }

        async Task Until(Func<bool> done, NativeHost? observed = null, Action<NativeEvent>? inspect = null)
        {
            var timeout = Stopwatch.StartNew();
            while (!done())
            {
                foreach (var host in clients.Concat(servers))
                {
                    NativeEvent? e;
                    while ((e = host.Poll()) != null) if (ReferenceEquals(host, observed)) inspect?.Invoke(e);
                    host.Flush();
                }
                if (timeout.ElapsedMilliseconds > 5000) throw new TimeoutException("Önceki istemci ayrılmadan yeni bağlantı kurulamadı.");
                await Task.Delay(2);
            }
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
