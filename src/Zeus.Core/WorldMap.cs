using System.Buffers.Binary;
using System.Text;

namespace Zeus.Core;

/// <summary>Owner and access list of a lock tile.</summary>
public sealed record LockInfo(uint Owner, uint[] Access, bool AreaLock);

public struct WorldTile
{
    public ushort Foreground, Background, Flags;
    /// <summary>Index of the lock tile that governs this tile (flag 0x02), or -1.</summary>
    public int LockIndex;
    public LockInfo? Lock;
    public const ushort FlagExtra = 0x01, FlagLocked = 0x02, FlagOn = 0x40, FlagPublic = 0x80;
}

/// <summary>
/// The current world as the server sent it (PACKET_SEND_MAP_DATA), kept up to date with tile
/// changes, and a path finder that only walks where the local player may stand: empty tiles,
/// platforms, open entrances and entrances whose lock gives the player access. Solid blocks and
/// entrances of other people's locks are walls.
/// </summary>
public sealed class WorldMap
{
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public WorldTile[] Tiles { get; }
    private readonly ItemDatabase items;

    private WorldMap(string name, int width, int height, WorldTile[] tiles, ItemDatabase items)
    { Name = name; Width = width; Height = height; Tiles = tiles; this.items = items; }

    internal WorldMap Snapshot() => new(Name, Width, Height, (WorldTile[])Tiles.Clone(), items);
    internal ItemInfo? Item(int id) => items[id];

    // Machines whose tiles carry an extra CBOR blob after the tile extra data.
    private static readonly HashSet<int> CborItems = [15376, 15546, 3548, 12598, 14662, 14666, 8624, 8630, 8636, 8642, 8648, 8654, 8660, 8666, 8672, 8678, 8684, 8690, 8696, 8702, 8708, 8714];
    private static readonly string[] CborFiles = ["AutoSurgeonStation.xml", "ClothesRack.xml", "OperatingTable.xml"];
    private static readonly string[] AreaLockNames = ["Small Lock", "Big Lock", "Huge Lock", "Builder's Lock"];

    /// <summary>Parses a full map packet (message type 4, game packet type 4).</summary>
    public static WorldMap Parse(byte[] packet, ItemDatabase items)
    {
        if (packet.Length < 60 || packet[4] != 4) throw new InvalidDataException("Harita paketi değil.");
        var r = new Reader(packet, 60);
        r.Skip(6);                            // version, flags
        string name = Encoding.UTF8.GetString(r.Bytes(r.U16()));
        int width = (int)r.U32(), height = (int)r.U32(), count = (int)r.U32();
        if (width is <= 0 or > 1000 || height is <= 0 or > 1000 || count != width * height)
            throw new InvalidDataException($"Harita boyutu geçersiz: {width}x{height}, {count} karo.");
        r.Skip(5);
        var tiles = new WorldTile[count];
        for (int i = 0; i < count; i++) tiles[i] = ReadTile(r, items);
        // The dropped-object list follows the tiles; if the tiles were read correctly its size fits exactly.
        r.Skip(12);
        uint objects = r.U32();
        r.Skip(4);
        long left = (long)r.Left - (long)objects * 16;
        if (objects > 100_000 || left < 0 || left > 256)
            throw new InvalidDataException($"Karolardan sonra beklenmeyen veri ({r.Left} bayt, {objects} nesne).");
        return new WorldMap(name, width, height, tiles, items);
    }

    private static WorldTile ReadTile(Reader r, ItemDatabase items)
    {
        var tile = new WorldTile { Foreground = r.U16(), Background = r.U16(), LockIndex = -1 };
        r.Skip(2);                            // parent
        tile.Flags = r.U16();
        if ((tile.Flags & WorldTile.FlagLocked) != 0) tile.LockIndex = r.U16();
        var item = items[tile.Foreground];
        if (item == null) throw new InvalidDataException($"Bilinmeyen eşya {tile.Foreground}.");
        if ((tile.Flags & WorldTile.FlagExtra) != 0)
        {
            byte type = r.U8();
            tile.Lock = ReadExtra(r, type, tile.Foreground, item);
        }
        if (item.Type == 77 || CborItems.Contains(tile.Foreground) || CborFiles.Any(f => item.ExtraFile.Contains(f, StringComparison.OrdinalIgnoreCase)))
            r.Skip((int)r.U32());
        return tile;
    }

    /// <summary>Skips one tile extra block; returns the lock data for lock tiles.</summary>
    private static LockInfo? ReadExtra(Reader r, byte type, int fg, ItemInfo item)
    {
        void Str() => r.Skip(r.U16());
        void List() { uint n = r.U32(); r.Skip(checked((int)n * 4)); }
        switch (type)
        {
            case 0: case 17: case 22: case 34: case 45: case 51: case 52: case 58: case 59: case 74: break;
            case 1: Str(); r.Skip(1); break;
            case 2: Str(); r.Skip(4); break;
            case 3:
            {
                r.Skip(1);                    // settings
                uint owner = r.U32();
                uint n = r.U32();
                if (n > 10_000) throw new InvalidDataException("Kilit listesi geçersiz.");
                var access = new List<uint>();
                for (int i = 0; i < n; i++) { int id = (int)r.U32(); if (id > 0) access.Add((uint)id); }
                r.Skip(8);
                if (fg == 5814) r.Skip(16);   // guild lock
                return new LockInfo(owner, [.. access], AreaLockNames.Contains(item.Name));
            }
            case 4: r.Skip(5); break;
            case 6: case 7: case 12: Str(); Str(); Str(); r.Skip(1); break;
            case 8: case 16: case 66: r.Skip(1); break;
            case 9: r.Skip(fg == 10656 ? 8 : 4); break;
            case 10: r.Skip(5); break;
            case 11: r.Skip(4); Str(); break;
            case 14: Str(); r.Skip(1 + 4 + 18); break;
            case 15: case 23: case 27: case 38: case 39: case 40: case 41: case 50: case 57: case 73: case 79: r.Skip(4); break;
            case 18: case 26: r.Skip(5); break;
            case 19: r.Skip(18); break;
            case 20: Str(); break;
            case 21: Str(); r.Skip(5); break;
            case 24: case 53: r.Skip(8); break;
            case 25: { r.Skip(1); uint n = r.U32(); r.Skip(checked((int)(n / 2) * 8)); break; }
            case 28: r.Skip(6); break;
            case 30: case 60: r.Skip(5); break;
            case 31: r.Skip(1); Str(); r.Skip(4 + 4 + 4 + 1 + 4 + 4 + 4 + 4); break;
            case 32: List(); break;
            case 33: if (fg == 3394) Str(); break;
            case 35: r.Skip(4); Str(); break;
            case 36: Str(); r.Skip(12); break;
            case 37: { Str(); uint n = r.U32(); r.Skip(4); r.Skip(checked((int)n * 4)); break; }
            case 42: r.Skip(21); break;
            case 43: case 69: r.Skip(16); break;
            case 44: r.Skip(1 + 4); List(); break;
            case 47: Str(); r.Skip(5); break;
            case 48:
            {
                Str(); r.Skip(4 + 4 + 5 + 1 + 2 + 2);
                int hat = r.U16(); r.Skip(2 + 4);
                if (hat == 12958) Str();
                break;
            }
            case 49: r.Skip(9); break;
            case 54: r.Skip(r.U16()); break;
            case 55: { r.Skip(4); uint n = r.U32(); r.Skip(checked((int)(n / 2) * 8)); r.Skip(12); break; }
            case 56: Str(); r.Skip(4); break;
            case 61: r.Skip(35); break;
            case 62: r.Skip(14); break;
            case 63: { uint n = r.U32(); r.Skip(checked((int)n * 15)); r.Skip(8); break; }
            case 65: r.Skip(17); break;
            case 67: r.Skip(4); List(); break;
            case 68: r.Skip(4); Str(); Str(); List(); break;
            case 72: r.Skip(12); break;
            case 75: { uint state = r.U32(); r.Skip(2); if (state == 2) r.Skip(1); break; }
            case 77: r.Skip(4); List(); break;
            case 80: r.Skip(8); break;
            case 81: { r.Skip(4 + 2); int n = r.U16(); r.Skip(n * 4); break; }
            default: throw new InvalidDataException($"Bilinmeyen karo verisi türü {type} (eşya {fg}).");
        }
        return null;
    }

    /// <summary>Server tile change (PACKET_TILE_CHANGE_REQUEST): an item placed, or 18 (fist) = a block broken.</summary>
    public void ApplyTileChange(int x, int y, int item)
    {
        if (!Inside(x, y)) return;
        worldLockKnown = false;
        ref var tile = ref Tiles[y * Width + x];
        if (item == 18)
        {
            if (tile.Foreground != 0) { tile.Foreground = 0; tile.Flags = 0; tile.Lock = null; }
            else tile.Background = 0;
            return;
        }
        var info = items[item];
        if (info == null) return;
        if (info.Type == 18) tile.Background = (ushort)item;
        else { tile.Foreground = (ushort)item; tile.Flags &= unchecked((ushort)~(WorldTile.FlagExtra | WorldTile.FlagOn | WorldTile.FlagPublic)); tile.Lock = null; }
    }

    /// <summary>Server tile update (PACKET_SEND_TILE_UPDATE_DATA): one serialized tile.</summary>
    public bool ApplyTileUpdate(byte[] packet)
    {
        if (packet.Length < 60) return false;
        int x = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(48)), y = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(52));
        if (!Inside(x, y)) return false;
        worldLockKnown = false;
        try { Tiles[y * Width + x] = ReadTile(new Reader(packet, 60), items); return true; }
        catch (InvalidDataException) { return false; }
    }

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    private LockInfo? worldLock; private bool worldLockKnown;
    private LockInfo? WorldLock()
    {
        if (worldLockKnown) return worldLock;
        worldLockKnown = true; worldLock = null;
        foreach (var tile in Tiles) if (tile.Lock is { AreaLock: false } world) { worldLock = world; break; }
        return worldLock;
    }

    /// <summary>True when the player may use the tile's entrance: no lock, or owner / on the access list.</summary>
    public bool HasAccess(int index, uint userId)
    {
        var world = WorldLock();
        if (world != null && (world.Owner == userId || world.Access.Contains(userId))) return true;
        var tile = Tiles[index];
        LockInfo? governing = tile.LockIndex >= 0 && tile.LockIndex < Tiles.Length ? Tiles[tile.LockIndex].Lock : world;
        return governing == null || governing.Owner == userId || governing.Access.Contains(userId);
    }

    public bool Passable(int x, int y, uint userId)
    {
        if (!Inside(x, y)) return false;
        int index = y * Width + x;
        var tile = Tiles[index];
        if (tile.Foreground == 0) return true;
        var item = items[tile.Foreground];
        if (item == null) return false;
        return item.Collision switch
        {
            0 or 2 or 7 => true,                                                   // air, platforms, waterfalls
            3 => (tile.Flags & WorldTile.FlagPublic) != 0 || HasAccess(index, userId), // entrances
            6 or 10 or 11 or 13 => HasAccess(index, userId),                       // VIP/team/guild/friends entrances
            9 => (tile.Flags & WorldTile.FlagOn) == 0,                             // deathtrap-style walls
            _ => false,                                                            // solid, one-way and the rest
        };
    }

    /// <summary>
    /// Path for a click on (tx, ty). A free tile is walked to directly. If the tile is solid or
    /// walled off (unreachable), it finds the closest reachable tile to (tx, ty) across the map.
    /// </summary>
    public (List<(int X, int Y)> Path, int X, int Y)? FindPathToClick(int sx, int sy, int tx, int ty, uint userId)
    {
        if (!Inside(tx, ty) || !Inside(sx, sy)) return null;
        var previous = Reach(sx, sy, userId);
        if (Passable(tx, ty, userId) && previous[ty * Width + tx] != -1)
            return (Trace(previous, sx, sy, tx, ty), tx, ty);

        double best = double.MaxValue; int bx = -1, by = -1;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (previous[y * Width + x] == -1) continue;
                double score = (x - tx) * (x - tx) + (y - ty) * (y - ty);
                if (y + 1 < Height && !Passable(x, y + 1, userId)) score -= 0.4; // something to stand on
                if (y < ty) score -= 0.2;                                         // on top rather than below
                if (score < best) { best = score; bx = x; by = y; }
            }
        return bx < 0 ? null : (Trace(previous, sx, sy, bx, by), bx, by);
    }

    /// <summary>Breadth-first search from a tile: previous[i] is the tile it was reached from, -1 if unreachable.</summary>
    private int[] Reach(int sx, int sy, uint userId)
    {
        var previous = new int[Tiles.Length];
        Array.Fill(previous, -1);
        int start = sy * Width + sx;
        previous[start] = start;
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int cx = current % Width, cy = current / Width;
            foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
            {
                if (!Inside(nx, ny)) continue;
                int next = ny * Width + nx;
                if (previous[next] != -1 || !Passable(nx, ny, userId)) continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }
        return previous;
    }

    private List<(int X, int Y)> Trace(int[] previous, int sx, int sy, int ex, int ey)
    {
        var path = new List<(int X, int Y)>();
        int start = sy * Width + sx;
        for (int at = ey * Width + ex; at != start; at = previous[at]) path.Add((at % Width, at / Width));
        path.Reverse();
        return path;
    }

    /// <summary>Shortest 4-direction path from start to end (start excluded), or null when walled off.</summary>
    public List<(int X, int Y)>? FindPath(int sx, int sy, int ex, int ey, uint userId)
    {
        if (!Inside(sx, sy) || !Passable(ex, ey, userId)) return null;
        if (sx == ex && sy == ey) return [];
        var previous = new int[Tiles.Length];
        Array.Fill(previous, -1);
        int start = sy * Width + sx, end = ey * Width + ex;
        previous[start] = start;
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (current == end) break;
            int cx = current % Width, cy = current / Width;
            foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
            {
                if (!Inside(nx, ny)) continue;
                int next = ny * Width + nx;
                if (previous[next] != -1 || !Passable(nx, ny, userId)) continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }
        if (previous[end] == -1) return null;
        var path = new List<(int X, int Y)>();
        for (int at = end; at != start; at = previous[at]) path.Add((at % Width, at / Width));
        path.Reverse();
        return path;
    }

    private sealed class Reader(byte[] data, int position)
    {
        public int Left => data.Length - position;
        private void Need(int count)
        {
            if (count < 0 || data.Length - position < count) throw new InvalidDataException("Harita verisi beklenenden kısa.");
        }
        public byte U8() { Need(1); return data[position++]; }
        public ushort U16() { Need(2); ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position)); position += 2; return v; }
        public uint U32() { Need(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)); position += 4; return v; }
        public void Skip(int count) { Need(count); position += count; }
        public byte[] Bytes(int count) { Need(count); byte[] v = data.AsSpan(position, count).ToArray(); position += count; return v; }
    }
}
