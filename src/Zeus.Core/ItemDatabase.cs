using System.Buffers.Binary;
using System.Text;

namespace Zeus.Core;

/// <summary>What Zeus needs to know about one item: its name, action type, collision type and
/// the extra file it uses (some machines carry CBOR data in the world map).</summary>
public sealed record ItemInfo(int Id, string Name, byte Type, byte Collision, string ExtraFile)
{
    public string Texture { get; init; } = "";
    public byte TextureX { get; init; }
    public byte TextureY { get; init; }
}

/// <summary>
/// Reader for the game's own items.dat (normally %LOCALAPPDATA%\Growtopia\cache\items.dat).
/// Field order follows the public parsers up to version 24; the version 25-27 tail
/// (string, u32, u8, string) was worked out against a real version 27 file, where all 16,580
/// items parse and end exactly at the end of the file.
/// </summary>
public sealed class ItemDatabase
{
    private const string NameKey = "PBG892FXX982ABC*";
    public int Version { get; }
    public IReadOnlyList<ItemInfo> Items { get; }
    private ItemDatabase(int version, ItemInfo[] items) { Version = version; Items = items; }

    public ItemInfo? this[int id] => id >= 0 && id < Items.Count ? Items[id] : null;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Growtopia", "cache", "items.dat");

    public static ItemDatabase Load(string path) => Parse(File.ReadAllBytes(path));

    public static ItemDatabase Parse(byte[] data)
    {
        var r = new Reader(data);
        int version = r.U16();
        int count = checked((int)r.U32());
        if (version < 11 || version > 27) throw new InvalidDataException($"Desteklenmeyen items.dat sürümü: {version}");
        if (count is <= 0 or > 100_000) throw new InvalidDataException("items.dat eşya sayısı geçersiz.");
        var items = new ItemInfo[count];
        for (int i = 0; i < count; i++)
        {
            int id = (int)r.U32();
            if (id != i) throw new InvalidDataException($"items.dat okunamadı: {i}. eşyanın numarası {id}.");
            r.Skip(2);                       // properties
            byte type = r.U8();
            r.Skip(1);                       // material
            byte[] encrypted = r.Bytes(r.U16());
            var name = new StringBuilder(encrypted.Length);
            for (int j = 0; j < encrypted.Length; j++) name.Append((char)(encrypted[j] ^ NameKey[(j + id) % NameKey.Length]));
            string texture = r.Str();
            r.Skip(4 + 1 + 4);                // texture hash, visual type, cook time
            byte textureX = r.U8(), textureY = r.U8();
            r.Skip(1 + 1);                    // storage, layer
            byte collision = r.U8();
            r.Skip(1 + 4 + 1 + 2 + 1);       // hardness, regen time, clothing type, rarity, max amount
            r.Str(); r.Skip(4 + 4);          // alt file, hash, anim ms
            r.Str(); r.Str(); r.Str();       // pet name / prefix / suffix (v4)
            r.Str();                         // pet ability (v5)
            r.Skip(4 + 8 + 4 + 4);           // seed/tree looks, colours, seeds, bloom time
            r.Skip(4); r.Str();              // anim type, anim string (v7)
            r.Str(); r.Str(); r.Skip(8);     // anim tex, anim string 2, layers (v8)
            r.Skip(2 + 62);                  // v9
            r.Skip(8);                       // tile / pile range (v10)
            r.Str();                         // custom punch (v11)
            if (version >= 12) r.Skip(13);
            if (version >= 13) r.Skip(4);
            if (version >= 14) r.Skip(4);
            if (version >= 15) { r.Skip(25); r.Str(); }
            string extraFile = version >= 16 ? r.Str() : "";
            if (version >= 17) r.Skip(4);
            if (version >= 18) r.Skip(4);
            if (version >= 19) r.Skip(9);
            if (version >= 21) r.Skip(2);
            if (version >= 22) r.Str();      // description
            if (version >= 23) r.Skip(4);
            if (version >= 24) r.Skip(1);
            if (version >= 25) { r.Str(); r.Skip(4); }
            if (version >= 26) r.Skip(1);
            if (version >= 27) r.Str();
            items[i] = new ItemInfo(id, name.ToString(), type, collision, extraFile) { Texture = texture, TextureX = textureX, TextureY = textureY };
        }
        if (!r.AtEnd) throw new InvalidDataException("items.dat sonunda okunmamış veri kaldı (yeni sürüm olabilir).");
        return new ItemDatabase(version, items);
    }

    private sealed class Reader(byte[] data)
    {
        private int position;
        public bool AtEnd => position == data.Length;
        private void Need(int count)
        {
            if (count < 0 || data.Length - position < count) throw new InvalidDataException("items.dat beklenenden kısa.");
        }
        public byte U8() { Need(1); return data[position++]; }
        public ushort U16() { Need(2); ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position)); position += 2; return v; }
        public uint U32() { Need(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)); position += 4; return v; }
        public void Skip(int count) { Need(count); position += count; }
        public byte[] Bytes(int count) { Need(count); byte[] v = data.AsSpan(position, count).ToArray(); position += count; return v; }
        public string Str() => Encoding.UTF8.GetString(Bytes(U16()));
    }
}
