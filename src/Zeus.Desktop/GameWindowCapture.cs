using System.Diagnostics;
using System.Runtime.InteropServices;
using Zeus.Core;

namespace Zeus.Desktop;

/// <summary>
/// Grabs the client area of the Growtopia window (the one in front, else the first one) as a BGRA
/// frame for ScreenLocator. Only runs while path is on; two buffers alternate so a frame is never
/// overwritten while the relay is still reading it.
/// </summary>
internal sealed class GameWindowCapture : IDisposable
{
    private const int MaxWidth = 4096, MaxHeight = 2304, IntervalMs = 125, IdleMs = 400, MaxAgeMs = 300;
    private const int PW_RENDERFULLCONTENT = 2;
    private readonly Func<bool> active;
    private readonly Thread thread;
    private readonly object gate = new();
    private volatile bool stopping;
    private ScreenFrame? latest; private long latestAt;
    private byte[]? bufferA, bufferB; private bool useSecond;
    private IntPtr window; private long lookedAt; private bool notedFailure;
    public string Status { get; private set; } = "oyun penceresi bekleniyor";

    public GameWindowCapture(Func<bool> active)
    {
        this.active = active;
        thread = new Thread(Loop) { IsBackground = true, Name = "Zeus ekran" };
        thread.Start();
    }

    /// <summary>The newest frame, or null when the last one is older than the allowed age.</summary>
    public ScreenFrame? Fresh()
    {
        lock (gate)
            return latest != null && Stopwatch.GetTimestamp() - latestAt <= MaxAgeMs * Stopwatch.Frequency / 1000 ? latest : null;
    }

    private void Loop()
    {
        while (!stopping)
        {
            if (!active()) { Drop(); window = IntPtr.Zero; Status = "bekliyor"; Thread.Sleep(IdleMs); continue; }
            try { Grab(); } catch (Exception) { }
            Thread.Sleep(IntervalMs);
        }
    }

    private void Grab()
    {
        if (window == IntPtr.Zero || Stopwatch.GetTimestamp() - lookedAt > Stopwatch.Frequency)
        {
            lookedAt = Stopwatch.GetTimestamp();
            window = FindWindow();
            Status = window == IntPtr.Zero ? "oyun penceresi bulunamadı" : "yakalanıyor";
        }
        if (window == IntPtr.Zero || IsIconic(window) || !GetClientRect(window, out var rect)) { Drop(); return; }
        int w = rect.Right, h = rect.Bottom;
        if (w < 64 || h < 64 || w > MaxWidth || h > MaxHeight) { Drop(); return; }
        var pixels = Buffer(w * h * 4);
        if (!Capture(window, w, h, pixels))
        {
            if (!notedFailure) { notedFailure = true; Status = "pencere yakalanamadı"; }
            Drop();
            return;
        }
        notedFailure = false;
        lock (gate) { latest = new ScreenFrame(w, h, pixels); latestAt = Stopwatch.GetTimestamp(); }
    }

    private void Drop() { lock (gate) latest = null; }

    private byte[] Buffer(int size)
    {
        if (bufferA == null || bufferA.Length != size) { bufferA = new byte[size]; bufferB = new byte[size]; }
        useSecond = !useSecond;
        return (useSecond ? bufferB : bufferA)!;
    }

    /// <summary>PrintWindow first (works while the game is covered), then a screen blit.</summary>
    private static bool Capture(IntPtr target, int w, int h, byte[] pixels)
    {
        var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 } };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        IntPtr bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) { DeleteDC(dc); return false; }
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            if (old == IntPtr.Zero) return false;
            bool ok = PrintWindow(target, dc, PW_RENDERFULLCONTENT) && !MostlyBlack(bits, w, h);
            if (!ok)
            {
                var origin = new Point { X = 0, Y = 0 };
                ClientToScreen(target, ref origin);
                IntPtr screen = GetDC(IntPtr.Zero);
                ok = BitBlt(dc, 0, 0, w, h, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000);
                ReleaseDC(IntPtr.Zero, screen);
            }
            if (!ok) return false;
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return true;
        }
        finally
        {
            SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc);
        }
    }

    private static bool MostlyBlack(IntPtr bits, int w, int h)
    {
        var probe = new byte[4];
        for (int i = 0; i < 64; i++)
        {
            Marshal.Copy(bits + ((i * 977) % (w * h)) * 4, probe, 0, 4);
            if (probe[0] + probe[1] + probe[2] > 12) return false;
        }
        return true;
    }

    /// <summary>The foreground Growtopia window, else the first visible one.</summary>
    private static IntPtr FindWindow()
    {
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Growtopia"))
        { try { pids.Add((uint)process.Id); } catch (Exception) { } finally { process.Dispose(); } }
        if (pids.Count == 0) return IntPtr.Zero;
        IntPtr first = IntPtr.Zero, foreground = GetForegroundWindow();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (!pids.Contains(pid)) return true;
            if (first == IntPtr.Zero) first = handle;
            if (handle == foreground) { first = handle; return false; }
            return true;
        }, IntPtr.Zero);
        return first;
    }

    public void Dispose()
    {
        stopping = true;
        if (thread.IsAlive) thread.Join(1000);
    }

    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public int Colors; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref Point point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int w, int h, IntPtr source, int sx, int sy, int rop);
}