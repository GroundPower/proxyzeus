using System.Diagnostics;

namespace Zeus.Core;

public static class LocalDiagnostics
{
    public static async Task<string> RunAsync(bool ubisoftClient = false, bool clientChecksum = true)
    {
        using var server = new NativeHost();
        ushort relayPort;
        using (var probe = new NativeHost()) relayPort = probe.Port;
        await using var relay = new RelayEngine();
        await relay.StartAsync(new Profile { LocalPort = relayPort, ServerPort = server.Port });
        using var client = new NativeHost(outgoingPrefix: ubisoftClient, checksum: clientChecksum);
        client.Connect("127.0.0.1", relayPort);
        await PumpUntil(() => client.Connected && server.Connected, null);
        byte[] request = Protocol.TextPacket(2, "action|input\n|text|zeus-local-test");
        byte[]? received = null;
        client.Send(request, 1);
        await PumpUntil(() => received != null, e => { if (e.Type == 3) { if (e.Channel != 1) throw new IOException("Kanal korunmadı."); received = e.Data; } });
        if (!request.SequenceEqual(received!)) throw new IOException("Giden paket değişti.");
        byte[] response = new byte[20004];
        Random.Shared.NextBytes(response); response[0] = 99; response[1] = response[2] = response[3] = 0;
        byte[]? answer = null;
        server.Send(response, 1);
        await PumpUntil(() => answer != null, null, e => { if (e.Type == 3) answer = e.Data; });
        if (!response.SequenceEqual(answer!)) throw new IOException("Parçalı dönüş paketi değişti.");
        await relay.StopAsync();
        return "Yerel test başarılı: istemci → Zeus → sunucu, iki yön, kanal 1 ve 20 KB parçalı paket doğrulandı.";

        async Task PumpUntil(Func<bool> done, Action<NativeEvent>? onServer, Action<NativeEvent>? onClient = null)
        {
            var timeout = Stopwatch.StartNew();
            while (!done())
            {
                NativeEvent? e;
                while ((e = client.Poll()) != null) onClient?.Invoke(e);
                while ((e = server.Poll()) != null) onServer?.Invoke(e);
                client.Flush(); server.Flush();
                if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new IOException("Yerel bağlantı testi zaman aşımına uğradı.");
                await Task.Delay(2);
            }
        }
    }
}
