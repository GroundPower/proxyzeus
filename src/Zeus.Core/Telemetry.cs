namespace Zeus.Core;

public sealed record TrafficRow(long Sequence, DateTimeOffset Time, string Direction, int Bytes, int Channel, string Summary);
public sealed record LogRow(DateTimeOffset Time, string Level, string Message);
public sealed record SessionSnapshot(string State, string Endpoint, long SentPackets, long ReceivedPackets,
    long SentBytes, long ReceivedBytes, long DroppedPackets, uint Rtt, double Seconds,
    string World, string Position, int Players, TrafficRow[] Traffic, LogRow[] Logs);

public sealed class Telemetry
{
    private readonly object gate = new();
    private readonly Queue<TrafficRow> traffic = new();
    private readonly Queue<LogRow> logs = new();
    private long sentPackets, receivedPackets, sentBytes, receivedBytes, dropped, sequence;
    private string state = "Hazır", endpoint = "—", world = "—", position = "—";
    private uint rtt;
    private int players;
    private DateTimeOffset started = DateTimeOffset.UtcNow;
    public void Reset()
    {
        lock (gate) { sentPackets = receivedPackets = sentBytes = receivedBytes = dropped = sequence = 0; traffic.Clear(); started = DateTimeOffset.UtcNow; world = position = "—"; players = 0; rtt = 0; }
    }
    public void State(string value, string? target = null) { lock (gate) { state = value; if (target != null) endpoint = target; } }
    public void Session(string currentWorld, string currentPosition, int playerCount, uint ping)
    { lock (gate) { world = currentWorld; position = currentPosition; players = playerCount; rtt = ping; } }
    /// <summary>Optional diagnostic file (set by the desktop app). Only log lines are written,
    /// never packet contents; the file is rotated at 1 MiB.</summary>
    public static string? LogFilePath { get; set; }
    public void Log(string message, string level = "BİLGİ")
    {
        lock (gate)
        {
            var row = new LogRow(DateTimeOffset.Now, level, message);
            logs.Enqueue(row); while (logs.Count > 256) logs.Dequeue();
            if (LogFilePath is not { } path) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1024 * 1024) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{row.Time:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public void Drop() { lock (gate) dropped++; }
    public void Packet(bool outgoing, byte[] data, int channel)
    {
        string summary = Protocol.Summary(data);
        lock (gate)
        {
            if (outgoing) { sentPackets++; sentBytes += data.Length; } else { receivedPackets++; receivedBytes += data.Length; }
            traffic.Enqueue(new(++sequence, DateTimeOffset.Now, outgoing ? "İstemci → Sunucu" : "Sunucu → İstemci", data.Length, channel, summary));
            while (traffic.Count > 1000) traffic.Dequeue();
        }
    }
    public SessionSnapshot Snapshot()
    {
        lock (gate) return new(state, endpoint, sentPackets, receivedPackets, sentBytes, receivedBytes, dropped,
            rtt, (DateTimeOffset.UtcNow - started).TotalSeconds, world, position, players, traffic.ToArray(), logs.ToArray());
    }
}
