using System.Buffers.Binary;
using System.Text;

namespace Zeus.Core;

public sealed record Variant(byte Index, byte Type, byte[] Bytes)
{
    public string Text => Type == 2 ? Encoding.UTF8.GetString(Bytes) : "";
    public uint Number => (Type is 5 or 9) && Bytes.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes) : 0;
    public static Variant String(byte index, string text) => new(index, 2, Encoding.UTF8.GetBytes(text));
    public static Variant UInt(byte index, uint number, byte type = 5)
    { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, number); return new(index, type, b); }
}

public static class Protocol
{
    private static readonly HashSet<string> KnownFunctions = ["OnSendToServer", "OnSpawn", "OnRemove", "OnConsoleMessage", "OnDialogRequest", "OnSetPos", "OnNameChanged", "OnTalkBubble", "OnSetClothing", "OnSetFreezeState", "OnParticleEffect", "OnPlayPositioned", "OnReconnect", "OnRequestWorldSelectMenu", "OnSuperMainStartAcceptLogonHrdxs47254722215a"];
    public static uint Type(ReadOnlySpan<byte> packet) => packet.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(packet) : uint.MaxValue;
    public static byte[] TextPacket(uint type, string text)
    {
        byte[] result = new byte[5 + Encoding.UTF8.GetByteCount(text)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, type);
        Encoding.UTF8.GetBytes(text, result.AsSpan(4));
        return result;
    }
    /// <summary>
    /// Text of a type 2/3 message. The last byte is the terminator slot: the game client does not
    /// always zero it, so it can hold a random byte ("/reme" arrived as "/remeu", "/qqR", "/reme�").
    /// The server ignores that byte; so does Zeus (Kulo zeroes it too).
    /// </summary>
    public static string Text(ReadOnlySpan<byte> packet) => packet.Length <= 5 ? "" : Encoding.UTF8.GetString(packet[4..^1].TrimEnd((byte)0));
    public static bool ValidWorld(string world) => world.Length is >= 1 and <= 24 && world.All(char.IsAsciiLetterOrDigit);
    public static string Field(string text, string key)
    {
        foreach (string line in text.Split('\n'))
        {
            string normalized = line.TrimStart('|').TrimEnd('\r');
            int separator = normalized.IndexOf('|');
            if (separator >= 0 && normalized[..separator] == key) return normalized[(separator + 1)..];
        }
        return "";
    }
    public static bool ValidGamePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 60) return false;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(packet[16..]);
        uint extended = BinaryPrimitives.ReadUInt32LittleEndian(packet[56..]);
        return (flags & 8) == 0 || extended <= (uint)(packet.Length - 60);
    }
    public static List<Variant>? ReadVariants(ReadOnlySpan<byte> packet)
    {
        if (Type(packet) != 4 || !ValidGamePacket(packet) || packet[4] != 1) return null;
        if ((BinaryPrimitives.ReadUInt32LittleEndian(packet[16..]) & 8) == 0) return null;
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[56..]);
        ReadOnlySpan<byte> data = packet.Slice(60, size);
        if (data.Length < 1 || data[0] > 32) return null;
        int position = 1;
        var variants = new List<Variant>();
        var seen = new HashSet<byte>();
        for (int i = 0; i < data[0]; i++)
        {
            if (data.Length - position < 2) return null;
            byte index = data[position++], type = data[position++];
            if (index >= 32 || !seen.Add(index)) return null;
            int length = type switch { 1 or 5 or 9 => 4, 3 => 8, 4 => 12, 8 => 16, _ => -1 };
            if (type == 2)
            {
                if (data.Length - position < 4) return null;
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
                position += 4;
                if (value > 1024 * 1024) return null;
                length = (int)value;
            }
            if (length < 0 || length > data.Length - position) return null;
            variants.Add(new(index, type, data.Slice(position, length).ToArray()));
            position += length;
        }
        return position == data.Length ? variants : null;
    }
    public static byte[] FunctionPacket(IEnumerable<Variant> values, byte[]? original = null)
    {
        var variants = values.ToList();
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, true);
        writer.Write(checked((byte)variants.Count));
        foreach (var v in variants)
        {
            writer.Write(v.Index); writer.Write(v.Type);
            if (v.Type == 2) writer.Write(v.Bytes.Length);
            writer.Write(v.Bytes);
        }
        byte[] result = new byte[60 + data.Length];
        if (original is { Length: >= 60 }) original.AsSpan(0, 60).CopyTo(result);
        else { BinaryPrimitives.WriteUInt32LittleEndian(result, 4); result[4] = 1; BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), -1); }
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), flags | 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56), (uint)data.Length);
        data.ToArray().CopyTo(result, 60);
        return result;
    }
    public static string Summary(byte[] packet)
    {
        uint type = Type(packet);
        // Payloads never enter telemetry: login tokens, chat and unknown fields stay in transit.
        if (type is 2 or 3) return $"{(type == 2 ? "Metin" : "Oyun mesajı")} · içerik gizli";
        if (type != 4) return type == uint.MaxValue ? "Eksik başlık" : $"Ağ mesajı {type}";
        if (!ValidGamePacket(packet)) return "Geçersiz oyun paketi";
        var variants = ReadVariants(packet);
        string? function = variants?.FirstOrDefault(v => v.Index == 0)?.Text;
        return function != null && KnownFunctions.Contains(function)
            ? function : $"Oyun paketi · tür {packet[4]}";
    }
    public static bool TryRedirect(byte[] packet, ushort localPort, out byte[] rewritten, out string host, out ushort port)
    {
        rewritten = packet; host = ""; port = 0;
        var values = ReadVariants(packet);
        if (values?.FirstOrDefault(v => v.Index == 0)?.Text != "OnSendToServer") return false;
        var portValue = values.FirstOrDefault(v => v.Index == 1);
        var addressValue = values.FirstOrDefault(v => v.Index == 4);
        if (portValue?.Type is not (5 or 9) || portValue.Number is < 1 or > 65535 || addressValue?.Type != 2) return false;
        string address = addressValue.Text;
        int separator = address.IndexOf('|');
        host = separator < 0 ? address : address[..separator];
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        port = (ushort)portValue.Number;
        values[values.IndexOf(portValue)] = Variant.UInt(1, localPort, portValue.Type);
        values[values.IndexOf(addressValue)] = Variant.String(4, "127.0.0.1" + (separator < 0 ? "" : address[separator..]));
        rewritten = FunctionPacket(values, packet);
        return true;
    }
}
