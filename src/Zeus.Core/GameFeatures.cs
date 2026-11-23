using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Zeus.Core;

/// <summary>A roulette result read from a server message.</summary>
/// <param name="Real">True when the server scheduled it with a delay (a genuine spin);
/// player-typed imitations arrive without one.</param>
public sealed record RouletteSpin(string Function, string Name, int Number, char Color, bool Real, int NetId, int Delay);

public static partial class GameFeatures
{
    // GameUpdatePacket fields, offset from the start of the ENet payload (4-byte message type first).
    private const int NetIdOffset = 8;
    // int_data: for call-function packets, the delay in ms before the client shows the message.
    private const int DelayOffset = 24;

    [GeneratedRegex(@"`.")] private static partial Regex ColorCodes();
    [GeneratedRegex(@"^\s*\[?\s*(?<name>.+?) spun the wheel and got (?:`(?<color>[0-9a-zA-Z]))?(?<number>\d{1,2})(?!\d)")]
    private static partial Regex SpinText();
    [GeneratedRegex(@"(?m)^mstate\|[^\n]*")] private static partial Regex ModState();
    [GeneratedRegex(@"(?m)^name\|[^\n]*")] private static partial Regex NameLine();

    public static int Delay(ReadOnlySpan<byte> packet) =>
        packet.Length >= 60 ? BinaryPrimitives.ReadInt32LittleEndian(packet[DelayOffset..]) : 0;
    public static int PacketNetId(ReadOnlySpan<byte> packet) =>
        packet.Length >= 60 ? BinaryPrimitives.ReadInt32LittleEndian(packet[NetIdOffset..]) : 0;

    public static byte[] WithoutDelay(byte[] packet)
    {
        if (packet.Length < 60 || Delay(packet) == 0) return packet;
        byte[] copy = (byte[])packet.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(DelayOffset), 0);
        return copy;
    }

    /// <summary>
    /// The game font only has ASCII glyphs; Turkish letters render as stray icons
    /// (ı showed up as a cake slice). Everything shown in game goes through this.
    /// '|' and newlines are field separators in game text, so they are replaced too.
    /// </summary>
    public static string ToGameText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                'ı' => "i", 'İ' => "I", 'ş' => "s", 'Ş' => "S", 'ğ' => "g", 'Ğ' => "G",
                'ü' => "u", 'Ü' => "U", 'ö' => "o", 'Ö' => "O", 'ç' => "c", 'Ç' => "C",
                'â' => "a", 'Â' => "A", 'î' => "i", 'û' => "u",
                '·' or '–' or '—' or '•' => "-", '’' or '‘' => "'", '“' or '”' => "\"", '…' => "...",
                '|' => "/", '\n' or '\r' => " ",
                _ when c < 128 => c.ToString(),
                _ => "?",
            });
        }
        return builder.ToString();
    }

    /// <summary>
    /// Commands are plain ASCII. The client sometimes appends a stray non-UTF-8 byte (decoded as
    /// U+FFFD), which made "/reme" fall through to the game as an unknown command.
    /// </summary>
    public static string CleanCommand(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text) if (c is >= ' ' and <= '~') builder.Append(c);
        return builder.ToString().Trim();
    }

    public static string? FunctionName(byte[] packet) =>
        Protocol.ReadVariants(packet)?.FirstOrDefault(v => v.Index == 0)?.Text;

    /// <summary>Server packets that drive the new-player tutorial (pointer hand, NPC, tutorial dialogs).</summary>
    public static bool IsTutorial(byte[] packet, string? function)
    {
        if (function == null) return false;
        // FTUE = "first time user experience", the new-player tutorial (OnFtueButtonDataSet seen at login).
        if (function.Contains("tutorial", StringComparison.OrdinalIgnoreCase)
            || function.Contains("ftue", StringComparison.OrdinalIgnoreCase)) return true;
        if (function != "OnDialogRequest") return false;
        string dialog = Protocol.ReadVariants(packet)?.FirstOrDefault(v => v.Index == 1)?.Text ?? "";
        int end = dialog.LastIndexOf("end_dialog|", StringComparison.Ordinal);
        if (end < 0) return false;
        string name = dialog[(end + 11)..].Split('|', 2)[0];
        return name.Contains("tutorial", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Digit sum, last digit (19 → 10 → 0).</summary>
    public static int Reme(int number) => (number / 10 + number % 10) % 10;
    public static int Qq(int number) => number % 10;

    public static bool LooksLikeSpin(ReadOnlySpan<byte> packet) => packet.IndexOf("spun the wheel"u8) >= 0;

    public static bool TryRouletteSpin(byte[] packet, out RouletteSpin spin)
    {
        spin = null!;
        var values = Protocol.ReadVariants(packet);
        string? function = values?.FirstOrDefault(v => v.Index == 0)?.Text;
        if (function is not ("OnTalkBubble" or "OnConsoleMessage")) return false;
        string raw = values!.FirstOrDefault(v => v.Index == (function == "OnTalkBubble" ? 2 : 1))?.Text ?? "";
        int at = raw.IndexOf(" spun the wheel and got", StringComparison.Ordinal);
        if (at < 0) return false;
        // Colour codes inside the name are dropped; the one in front of the number is kept.
        var match = SpinText().Match(ColorCodes().Replace(raw[..at], "") + raw[at..]);
        if (!match.Success) return false;
        int number = int.Parse(match.Groups["number"].Value);
        if (number > 36) return false;
        int netId = function == "OnTalkBubble" ? (int)(values!.FirstOrDefault(v => v.Index == 1)?.Number ?? 0) : 0;
        char color = match.Groups["color"].Success ? match.Groups["color"].Value[0] : 'w';
        int delay = Delay(packet);
        spin = new(function, match.Groups["name"].Value.Trim(), number, color, delay > 0, netId, delay);
        return true;
    }

    /// <summary>Text appended to a genuine spin: " REME 5 QQ 3"; empty when both modes are off.
    /// The value is coloured by how good it is: green near a win, orange middle, red near a loss.</summary>
    public static string SpinSuffix(int number, bool reme, bool qq)
    {
        int r = Reme(number), q = Qq(number);
        // Labels use colours outside the value scale: REME cyan, QQ pink.
        return (reme ? $" `cREME `{RemeColor(r)}{r}" : "") + (qq ? $" `#QQ `{QqColor(q)}{q}" : "");
    }

    public static char RemeColor(int value) => TierColors[value];
    public static char QqColor(int value) => TierColors[value];

    /// <summary>
    /// Five strength tiers, the same for reme and qq (0 is an automatic win in both):
    /// 1-2 red (most likely to lose), 3-4 orange, 5-6 yellow, 7-8 light green, 0 and 9 green (most likely to win).
    /// </summary>
    private static readonly char[] TierColors = ['2', '4', '4', '8', '8', '9', '9', '^', '^', '2'];

    /// <summary>
    /// A genuine spin rebuilt with fixed colours, so it always looks the same whatever colour codes
    /// the server used: "[REAL] [Name spun the wheel and got 4!] REME 4" (white text, coloured number).
    /// </summary>
    public static string FormatRealSpin(RouletteSpin spin, string suffix) =>
        $"`w[`2REAL`w] [{spin.Name} spun the wheel and got `{spin.Color}{spin.Number}`w!]{suffix}``";

    /// <summary>Replaces the spin's text in the server packet (talk bubble or console line); no extra message.</summary>
    public static byte[] ReplaceSpinText(byte[] packet, string function, Func<string, string> rewrite)
    {
        var values = Protocol.ReadVariants(packet);
        byte index = (byte)(function == "OnTalkBubble" ? 2 : 1);
        var text = values?.FirstOrDefault(v => v.Index == index);
        if (values == null || text?.Type != 2) return packet;
        values[values.IndexOf(text)] = Variant.String(index, rewrite(text.Text));
        return Protocol.FunctionPacket(values, packet);
    }

    /// <summary>Local "[Zeus Proxy]" tag in front of console lines (only this client sees it).</summary>
    public const string ConsoleTag = "`w[`9Zeus Proxy`w]`` ";

    public static byte[] TagConsoleMessage(byte[] packet) =>
        ReplaceSpinText(packet, "OnConsoleMessage", text => text.StartsWith(ConsoleTag, StringComparison.Ordinal) ? text : ConsoleTag + text);

    /// <summary>Tags a server "action|log" text message the same way; other messages are returned unchanged.</summary>
    public static byte[] TagLogText(byte[] packet)
    {
        if (Protocol.Type(packet) != 3) return packet;
        string text = Protocol.Text(packet);
        if (Protocol.Field(text, "action") != "log" || text.Contains(ConsoleTag, StringComparison.Ordinal)) return packet;
        int at = text.IndexOf("msg|", StringComparison.Ordinal);
        if (at < 0) return packet;
        return Protocol.TextPacket(3, text[..(at + 4)] + ConsoleTag + text[(at + 4)..]);
    }

    public static byte[] TextOverlayPacket(string text) =>
        Protocol.FunctionPacket([Variant.String(0, "OnTextOverlay"), Variant.String(1, ToGameText(text))]);

    /// <summary>
    /// Rewrites the local player's OnSpawn. The client limits zoom by the mod state in it, so
    /// mstate is raised on the local copy only (unlimited zoom; the server keeps its own state).
    /// When <paramref name="displayName"/> is given the name tag is replaced as well.
    /// </summary>
    public static bool TryLocalSpawn(byte[] packet, Func<string, string>? displayName, out byte[] rewritten, out int localNetId, out string localName)
    {
        rewritten = packet; localNetId = 0; localName = "";
        var values = Protocol.ReadVariants(packet);
        if (values?.FirstOrDefault(v => v.Index == 0)?.Text != "OnSpawn") return false;
        var content = values.FirstOrDefault(v => v.Index == 1);
        if (content?.Type != 2 || Protocol.Field(content.Text, "type") != "local") return false;
        int.TryParse(Protocol.Field(content.Text, "netID"), out localNetId);
        localName = Protocol.Field(content.Text, "name");
        string text = content.Text;
        text = ModState().IsMatch(text) ? ModState().Replace(text, "mstate|1", 1) : text.TrimEnd('\n') + "\nmstate|1\n";
        if (displayName != null && NameLine().IsMatch(text))
        {
            string shown = displayName(localName);
            text = NameLine().Replace(text, _ => "name|" + shown, 1);
        }
        values[values.IndexOf(content)] = Variant.String(1, text);
        rewritten = Protocol.FunctionPacket(values, packet);
        return true;
    }

    public static bool TryNameChanged(byte[] packet, out int netId, out string name)
    {
        netId = 0; name = "";
        var values = Protocol.ReadVariants(packet);
        if (values?.FirstOrDefault(v => v.Index == 0)?.Text != "OnNameChanged") return false;
        var value = values.FirstOrDefault(v => v.Index == 1);
        if (value?.Type != 2) return false;
        netId = PacketNetId(packet); name = value.Text;
        return true;
    }

    /// <summary>OnNameChanged addressed to a player (the target is the packet's netID field).</summary>
    public static byte[] NameChangedPacket(int netId, string name, byte[]? original = null)
    {
        var values = original != null ? Protocol.ReadVariants(original) : null;
        byte[] packet;
        if (values != null)
        {
            var value = values.First(v => v.Index == 1);
            values[values.IndexOf(value)] = Variant.String(1, name);
            packet = Protocol.FunctionPacket(values, original);
        }
        else packet = Protocol.FunctionPacket([Variant.String(0, "OnNameChanged"), Variant.String(1, name)]);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(NetIdOffset), netId);
        return packet;
    }

    /// <summary>Green under 100 ms, yellow under 200, red under 400, black above.</summary>
    public static char PingColor(uint ms) => ms switch { < 100 => '2', < 200 => '9', < 400 => '4', _ => 'b' };
    public static string PingTag(uint ms) => $"`{PingColor(ms)}[{ms}]`` ";

    /// <summary>The server's login acceptance (the function name carries a varying suffix).</summary>
    public static bool IsLogonAccepted(byte[] packet) =>
        packet.AsSpan().IndexOf("OnSuperMainStartAcceptLogon"u8) >= 0
        && Protocol.ReadVariants(packet)?.FirstOrDefault(v => v.Index == 0)?.Text.StartsWith("OnSuperMainStartAcceptLogon", StringComparison.Ordinal) == true;

    public static byte[] DialogPacket(string dialog) =>
        Protocol.FunctionPacket([Variant.String(0, "OnDialogRequest"), Variant.String(1, dialog)]);

    /// <summary>A plain 56-byte game update packet (message type 4 in front, no extended data).</summary>
    public static byte[] GameUpdate(byte type, int netId = 0, int flags = 0, int intData = 0,
        float x = 0, float y = 0, int tileX = 0, int tileY = 0)
    {
        var packet = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 4);
        packet[4] = type;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), netId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), flags);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24), intData);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), y);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(48), tileX);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(52), tileY);
        return packet;
    }

    // Anti gravity (as in Kulo): the client is told an Anti Gravity Generator stands in the
    // bottom-right corner of the world and is switched on. Only the local client sees it.
    public const int AntiGravityItem = 4992, AntiGravityX = 99, AntiGravityY = 59;
    /// <summary>Tile change: the generator appears at (99, 59).</summary>
    public static byte[] AntiGravityPlace() => GameUpdate(3, intData: AntiGravityItem, tileX: AntiGravityX, tileY: AntiGravityY);
    /// <summary>The local player activates the generator, followed by the punch state that switches it on.</summary>
    public static byte[][] AntiGravityActivate(int netId, float x, float y) =>
    [
        GameUpdate(8, netId, intData: 6, x: x, y: y, tileX: AntiGravityX, tileY: AntiGravityY),
        GameUpdate(0, flags: 2592, intData: 18, x: x, y: y, tileX: AntiGravityX, tileY: AntiGravityY),
    ];

    /// <summary>One movement step sent to the server, as Kulo's path does: standing on tile (x, y).</summary>
    public static byte[] PathStep(int x, int y) =>
        GameUpdate(0, flags: 32 | (1 << 10) | (1 << 11), intData: 2250, x: x * 32, y: y * 32, tileX: x, tileY: y);

    /// <summary>OnSetPos for the local player: moves the character on the client's screen.</summary>
    public static byte[] SetPositionPacket(int netId, float x, float y)
    {
        var position = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(position, x);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(4), y);
        byte[] packet = Protocol.FunctionPacket([Variant.String(0, "OnSetPos"), new Variant(1, 3, position)]);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), netId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24), -1); // as Kulo sends it
        return packet;
    }

    /// <summary>Takes the client-only generator away (fist = broken) and puts back the real block, if any.</summary>
    public static byte[][] AntiGravityRemove(int originalForeground) =>
        originalForeground is 0 or AntiGravityItem
            ? [GameUpdate(3, intData: 18, tileX: AntiGravityX, tileY: AntiGravityY)]
            : [GameUpdate(3, intData: 18, tileX: AntiGravityX, tileY: AntiGravityY), GameUpdate(3, intData: originalForeground, tileX: AntiGravityX, tileY: AntiGravityY)];

    /// <summary>"posXY|x|y" from an OnSpawn text.</summary>
    public static bool TrySpawnPosition(string spawnText, out float x, out float y)
    {
        x = y = 0;
        var parts = Protocol.Field(spawnText, "posXY").Split('|');
        return parts.Length >= 2
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
    }
}
