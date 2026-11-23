using System.Diagnostics;
using System.Runtime.InteropServices;
using Zeus.Core;

namespace Zeus.Desktop;

/// <summary>
/// Grabs the client area of the Growtopia window (the one in front, else the first one) as a BGRA
/// frame on demand for ScreenLocator. Only runs while path is on; each grab allocates its own
/// buffer so there is no race with the relay and zero idle CPU/memory overhead.
/// </summary>
internal sealed class GameWindowCapture : IDisposable
{
    private const int MaxWidth = 4096, MaxHeight = 2304;
    private const int PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;
    private readonly Func<bool> active;
    private readonly object gate = new();
    private IntPtr window; private long lookedAt; private bool notedFailure;
    private volatile string status = "oyun penceresi bekleniyor";
    public string Status => status;

    public GameWindowCapture(Func<bool> active) => this.active = active;

    /// <summary>Captures a fresh client-area frame, or null while inactive or unavailable.</summary>
    public ScreenFrame? Fresh()
    {
        lock (gate)
        {
            if (!active()) { window = IntPtr.Zero; status = "bekliyor"; return null; }
            long now = Stopwatch.GetTimestamp();
            if (window == IntPtr.Zero || !IsWindow(window) || now - lookedAt > Stopwatch.Frequency)
            {
                lookedAt = now;
                window = FindWindow();
                if (window == IntPtr.Zero) { status = "oyun penceresi bulunamadı"; return null; }
            }
            if (IsIconic(window) || !GetClientRect(window, out var rect)) return null;
            int w = rect.Right, h = rect.Bottom;
            if (w < 64 || h < 64 || w > MaxWidth || h > MaxHeight) return null;
            var pixels = new byte[w * h * 4];
            if (!Capture(window, w, h, pixels))
            {
                if (!notedFailure) { notedFailure = true; status = "pencere yakalanamadı"; }
                return null;
            }
            notedFailure = false;
            status = "yakalanıyor";
            return new ScreenFrame(w, h, pixels);
        }
    }

    /// <summary>PrintWindow (client area only) first, then a screen blit.</summary>
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
            bool ok = PrintWindow(target, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT) && !MostlyBlack(bits, w, h);
            if (!ok)
            {
                var origin = new Point { X = 0, Y = 0 };
                if (ClientToScreen(target, ref origin))
                {
                    IntPtr screen = GetDC(IntPtr.Zero);
                    if (screen != IntPtr.Zero)
                    {
                        try { ok = BitBlt(dc, 0, 0, w, h, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000); }
                        finally { ReleaseDC(IntPtr.Zero, screen); }
                    }
                }
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
        for (int row = 0; row < 8; row++)
        {
            int y = (int)((row + 0.5) * h / 8.0);
            for (int col = 0; col < 8; col++)
            {
                int x = (int)((col + 0.5) * w / 8.0);
                Marshal.Copy(bits + (y * w + x) * 4, probe, 0, 4);
                if (probe[0] + probe[1] + probe[2] > 12) return false;
            }
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

    public void Dispose() { }

    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public int Colors; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
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