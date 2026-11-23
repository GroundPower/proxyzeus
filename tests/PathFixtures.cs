using System.Buffers.Binary;
using System.Text;

/// <summary>Synthetic items.dat (version 27 layout) and world map packets for the /path tests.</summary>
static class PathFixtures
{
    public const int Dirt = 2, SmallLock = 202, Entrance = 224, WorldLock = 242;

    public static byte[] ItemsDat(bool viewportSprites = false)
    {
        var items = new Dictionary<int, (string Name, byte Type, byte Collision)>
        {
            [0] = ("Blank", 17, 0), [Dirt] = ("Dirt", 17, 1), [SmallLock] = ("Small Lock", 3, 1),
            [Entrance] = ("House Entrance", 9, 3), [WorldLock] = ("World Lock", 3, 1),
            [6] = ("Main Door", 13, 0), [8] = ("Bedrock", 15, 1),
        };
        using var data = new MemoryStream();
        using var w = new BinaryWriter(data);
        void Str(string s) { byte[] b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
        void Zero(int n) => w.Write(new byte[n]);
        const string key = "PBG892FXX982ABC*";
        w.Write((ushort)27); w.Write(250);
        for (int id = 0; id < 250; id++)
        {
            var (name, type, collision) = items.TryGetValue(id, out var item) ? item : ("", (byte)0, (byte)0);
            w.Write(id); Zero(2); w.Write(type); Zero(1);
            w.Write((ushort)name.Length);
            for (int j = 0; j < name.Length; j++) w.Write((byte)(name[j] ^ key[(j + id) % key.Length]));
            Str(viewportSprites && id is 6 or 8 ? "viewport.rttex" : "");
            Zero(4 + 1 + 4); w.Write((byte)(viewportSprites && id == 8 ? 1 : 0)); Zero(1 + 1 + 1);
            w.Write(collision); Zero(1 + 4 + 1 + 2 + 1);
            Str(""); Zero(8); Str(""); Str(""); Str(""); Str(""); Zero(24); Str(""); Str(""); Str(""); Zero(8);
            Zero(64); Zero(8); Str(""); Zero(13); Zero(4); Zero(4); Zero(25); Str(""); Str(""); Zero(4); Zero(4); Zero(9); Zero(2);
            Str(""); Zero(4); Zero(1); Str(""); Zero(4); Zero(1); Str("");
        }
        return data.ToArray();
    }

    public sealed record Tile(int Fg, ushort Flags = 0, int LockIndex = -1, uint Owner = 0, uint[]? Access = null);

    /// <summary>A map packet (message type 4, game packet type 4) with the given tiles (row-major).</summary>
    public static byte[] MapPacket(string name, int width, int height, Func<int, int, Tile?> tileAt)
    {
        using var data = new MemoryStream();
        using var w = new BinaryWriter(data);
        w.Write(new byte[6]);
        w.Write((ushort)name.Length); w.Write(Encoding.ASCII.GetBytes(name));
        w.Write(width); w.Write(height); w.Write(width * height); w.Write(new byte[5]);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var tile = tileAt(x, y) ?? new Tile(0);
                ushort flags = tile.Flags;
                if (tile.LockIndex >= 0) flags |= 0x02;
                bool isLock = tile.Fg is SmallLock or WorldLock;
                if (isLock) flags |= 0x01;
                w.Write((ushort)tile.Fg); w.Write((ushort)0); w.Write((ushort)0); w.Write(flags);
                if (tile.LockIndex >= 0) w.Write((ushort)tile.LockIndex);
                if (isLock)
                {
                    w.Write((byte)3); w.Write((byte)0); w.Write(tile.Owner);
                    var access = tile.Access ?? [];
                    w.Write(access.Length); foreach (uint a in access) w.Write(a);
                    w.Write(new byte[8]);
                }
            }
        w.Write(new byte[12]); w.Write(0); w.Write(0); w.Write(new byte[12]);
        byte[] extended = data.ToArray();
        byte[] packet = new byte[60 + extended.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 4);
        packet[4] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(56), (uint)extended.Length);
        extended.CopyTo(packet, 60);
        return packet;
    }

    /// <summary>
    /// 7x3 world: a dirt wall at x = 3 with a House Entrance at (3, 1), governed by a small lock
    /// at (6, 0) owned by <paramref name="lockOwner"/>.
    /// </summary>
    public static byte[] WalledWorld(uint lockOwner, uint[]? access = null, bool publicEntrance = false, uint worldLockOwner = 0) =>
        MapPacket("PATHTEST", 7, 3, (x, y) =>
            x == 3 && y == 1 ? new Tile(Entrance, publicEntrance ? (ushort)0x80 : (ushort)0, LockIndex: 6)
            : x == 3 ? new Tile(Dirt)
            : x == 6 && y == 0 ? new Tile(SmallLock, Owner: lockOwner, Access: access)
            : x == 0 && y == 0 && worldLockOwner != 0 ? new Tile(WorldLock, Owner: worldLockOwner)
            : null);
}
