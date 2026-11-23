using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Zeus.Desktop;

/// <summary>
/// Reads the local player's camera zoom and position out of the running Growtopia client.
/// The chain is configured once in memory.json (see docs/BELLEK.md); nothing is written to the game.
/// </summary>
internal sealed class GameMemory : IDisposable
{
    private const uint QueryLimited = 0x1000, VmRead = 0x10, MemCommit = 0x1000, PageGuard = 0x100, PageNoAccess = 0x01;

    private sealed record Settings(
        string Module = "Growtopia.exe",
        long AppRva = 0x00979C80,
        string AppPattern = "",
        int ClientOffset = 0,
        int AvatarOffset = 0,
        int ZoomOffset = 0,
        int PositionOffset = 0)
    {
        public static Settings Load(string? path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            return new Settings();
        }
    }

    private readonly object gate = new();
    private readonly Settings config;
    private IntPtr process, module; private long moduleSize;
    private IntPtr appSlot; private long slotAt, lookedAt;
    private string status = "oyun bekleniyor";

    public GameMemory(string? configPath) => config = Settings.Load(configPath);

    public string Status { get { lock (gate) return status; } }

    /// <summary>The game's own zoom value, or null while it is not known.</summary>
    public double? Zoom
    {
        get
        {
            lock (gate)
            {
                if (!Ensure(out IntPtr client, out _)) return null;
                if (config.ZoomOffset == 0) { status = "zoom ofseti yok (memory.json)"; return null; }
                if (!ReadFloat(client + config.ZoomOffset, out float zoom) || !(zoom is > 0.05f and < 20f))
                { status = "zoom okunamadı"; return null; }
                status = "okunuyor";
                return zoom;
            }
        }
    }

    /// <summary>The local player's position in pixels, or null while it is not known.</summary>
    public (float X, float Y)? Position
    {
        get
        {
            lock (gate)
            {
                if (!Ensure(out _, out IntPtr avatar)) return null;
                if (config.PositionOffset == 0) return null;
                if (!ReadFloat(avatar + config.PositionOffset, out float x) || !ReadFloat(avatar + config.PositionOffset + 4, out float y)) return null;
                if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 1e6 || Math.Abs(y) > 1e6) return null;
                return (x, y);
            }
        }
    }

    /// <summary>Process, module and the three chain pointers. Re-resolved at most once a second.</summary>
    private bool Ensure(out IntPtr client, out IntPtr avatar)
    {
        client = avatar = IntPtr.Zero;
        if (process != IntPtr.Zero && (appSlot == IntPtr.Zero || Stopwatch.GetTimestamp() - slotAt > 2 * Stopwatch.Frequency))
            appSlot = IntPtr.Zero;
        if (process == IntPtr.Zero || appSlot == IntPtr.Zero) Attach();
        if (process == IntPtr.Zero || appSlot == IntPtr.Zero) return false;
        if (!ReadPointer(appSlot, out IntPtr app) || !Plausible(app)) { status = "App bulunamadı"; appSlot = IntPtr.Zero; return false; }
        if (config.ClientOffset == 0 || !ReadPointer(app + config.ClientOffset, out client) || !Plausible(client))
        { status = "istemci zinciri yok (memory.json)"; return false; }
        if (config.AvatarOffset == 0 || !ReadPointer(client + config.AvatarOffset, out avatar) || !Plausible(avatar))
        { status = "oyuncu zinciri yok (memory.json)"; return false; }
        return true;
    }

    private void Attach()
    {
        if (Stopwatch.GetTimestamp() - lookedAt < Stopwatch.Frequency) return;
        lookedAt = Stopwatch.GetTimestamp();
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
        module = IntPtr.Zero; moduleSize = 0;
        Process? game = null;
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(config.Module)))
        {
            try { if (!candidate.HasExited && candidate.MainWindowHandle != IntPtr.Zero) { game = candidate; break; } }
            catch (Exception) { }
            finally { if (game != candidate) candidate.Dispose(); }
        }
        if (game == null) { status = "oyun çalışmıyor"; return; }
        try
        {
            process = OpenProcess(QueryLimited | VmRead, false, game.Id);
            if (process == IntPtr.Zero) { status = "oyuna erişilemedi"; return; }
            foreach (ProcessModule entry in game.Modules)
                if (entry.ModuleName.Equals(config.Module, StringComparison.OrdinalIgnoreCase))
                { module = entry.BaseAddress; moduleSize = entry.ModuleMemorySize; break; }
            if (module == IntPtr.Zero) { status = "modül bulunamadı"; return; }
            appSlot = ResolveAppSlot();
            if (appSlot == IntPtr.Zero) { status = "App erişimcisi bulunamadı"; return; }
            slotAt = Stopwatch.GetTimestamp();
            status = "bağlı";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { status = "oyuna erişilemedi"; }
        finally { game.Dispose(); }
    }

    /// <summary>
    /// GetApp is the singleton thunk <c>mov rax,[rip+disp32]; ret</c>. The RVA is enough to read the
    /// displacement; when the build moved it, the same bytes are searched for in .text.
    /// </summary>
    private IntPtr ResolveAppSlot()
    {
        var head = new byte[8];
        if (config.AppRva > 0 && config.AppRva < moduleSize && ReadBytes(module + config.AppRva, head)
            && head[0] == 0x48 && head[1] == 0x8B && head[2] == 0x05)
            return module + config.AppRva + 7 + BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(3));
        if (config.AppPattern.Length == 0) return IntPtr.Zero;
        var (start, length) = TextSection();
        if (start == IntPtr.Zero || !ScanPattern(start, length, config.AppPattern, out IntPtr hit)) return IntPtr.Zero;
        if (!ReadBytes(hit, head) || head[0] != 0x48 || head[1] != 0x8B || head[2] != 0x05) return IntPtr.Zero;
        return hit + 7 + BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(3));
    }

    private (IntPtr Start, long Length) TextSection()
    {
        var dos = new byte[0x40];
        if (!ReadBytes(module, dos)) return (IntPtr.Zero, 0);
        int headers = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        var nt = new byte[24 + 0xF0 + 40 * 96];
        if (headers <= 0 || !ReadBytes(module + headers, nt)) return (IntPtr.Zero, 0);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(nt.AsSpan(20));
        int at = 24 + optionalSize;
        for (int i = 0; i < 96 && at + 40 <= nt.Length; i++, at += 40)
        {
            if (nt[at] != (byte)'.' || nt[at + 1] != (byte)'t' || nt[at + 2] != (byte)'e' || nt[at + 3] != (byte)'x' || nt[at + 4] != (byte)'t') continue;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(at + 8));
            long address = BinaryPrimitives.ReadUInt32LittleEndian(nt.AsSpan(at + 12));
            if (size is > 0 and < 0x10000000 && address > 0 && address + size <= moduleSize) return (module + address, size);
        }
        return (IntPtr.Zero, 0);
    }

    /// <summary>Wildcard scan of a byte pattern ("48 8B 05 ?? ?? ?? ?? C3") in 1 MiB chunks.</summary>
    private bool ScanPattern(IntPtr start, long length, string pattern, out IntPtr hit)
    {
        hit = IntPtr.Zero;
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        var bytes = new byte[parts.Length]; var mask = new bool[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "?" || parts[i] == "??") { mask[i] = true; continue; }
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i])) return false;
        }
        const int Chunk = 1 << 20;
        var buffer = new byte[Chunk + parts.Length];
        for (long offset = 0; offset < length; offset += Chunk)
        {
            int want = (int)Math.Min(buffer.Length, length - offset);
            if (!ReadBytes(start + offset, buffer.AsSpan(0, want))) continue;
            int limit = Math.Min(want, Chunk);
            for (int i = 0; i < limit; i++)
            {
                int k = 0;
                while (k < parts.Length && (mask[k] || buffer[i + k] == bytes[k])) k++;
                if (k == parts.Length) { hit = start + offset + i; return true; }
            }
        }
        return false;
    }

    private static bool Plausible(IntPtr pointer) => (long)pointer > 0x10000 && (long)pointer < 0x7FFFFFFFFFFF;

    private bool ReadPointer(IntPtr address, out IntPtr value)
    {
        value = IntPtr.Zero;
        if (!ReadBytes(address, Pointer()) ) return false;
        value = (IntPtr)BinaryPrimitives.ReadInt64LittleEndian(Pointer());
        return value != IntPtr.Zero;
    }

    private byte[] scratch = new byte[8];
    private Span<byte> Pointer() => scratch;

    private bool ReadFloat(IntPtr address, out float value)
    {
        value = 0;
        if (!ReadBytes(address, scratch.AsSpan(0, 4))) return false;
        value = BinaryPrimitives.ReadSingleLittleEndian(scratch);
        return float.IsFinite(value);
    }

    private bool ReadBytes(IntPtr address, Span<byte> buffer)
    {
        var pinned = new byte[buffer.Length];
        if (!ReadProcessMemory(process, address, pinned, buffer.Length, out IntPtr read) || read.ToInt64() < buffer.Length) return false;
        pinned.CopyTo(buffer);
        return true;
    }

    public void Dispose()
    {
        lock (gate) { if (process != IntPtr.Zero) CloseHandle(process); process = IntPtr.Zero; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}