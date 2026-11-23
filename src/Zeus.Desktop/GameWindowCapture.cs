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
    private readonly DxgiDesktopCapture dxgi = new();
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
            if (CaptureGdi(window, w, h, pixels, out var origin, out bool hasOrigin))
            {
                notedFailure = false;
                status = "yakalanıyor";
                return new ScreenFrame(w, h, pixels);
            }
            string dxgiDetail = "";
            if (hasOrigin && dxgi.TryCaptureMonitorRect(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), origin.X, origin.Y, w, h, pixels, out dxgiDetail))
            {
                notedFailure = false;
                status = "yakalanıyor (DXGI)";
                return new ScreenFrame(w, h, pixels);
            }
            else
            {
                if (!notedFailure)
                {
                    notedFailure = true;
                    status = hasOrigin ? "siyah ekran (" + dxgiDetail + ")" : "pencere yakalanamadı";
                }
                return null;
            }
        }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>PrintWindow (client area only) first, then a screen blit; rejects all-black frames.</summary>
    private static bool CaptureGdi(IntPtr target, int w, int h, byte[] pixels, out Point origin, out bool hasOrigin)
    {
        origin = new Point { X = 0, Y = 0 };
        hasOrigin = ClientToScreen(target, ref origin);
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
            if (!ok && hasOrigin)
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                if (screen != IntPtr.Zero)
                {
                    try { ok = BitBlt(dc, 0, 0, w, h, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000) && !MostlyBlack(bits, w, h); }
                    finally { ReleaseDC(IntPtr.Zero, screen); }
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

    internal static unsafe bool MostlyBlackBytes(byte[] pixels, int w, int h)
    {
        fixed (byte* p = pixels)
            return MostlyBlack((IntPtr)p, w, h);
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

    public void Dispose()
    {
        lock (gate) { dxgi.Dispose(); }
    }

    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
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
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
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

/// <summary>
/// Captures the composited desktop output via DXGI Desktop Duplication (DirectX COM vtables, no extra packages)
/// when GDI capture returns black on fullscreen OpenGL windows.
/// </summary>
internal sealed unsafe class DxgiDesktopCapture : IDisposable
{
    private const int S_OK = 0;
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private IntPtr currentMonitor, device, context, duplication, staging;
    private GameWindowCapture.NativeRect monitorRect;
    private uint stagingW, stagingH;
    private int stagingFormat;
    private bool hasFrame;

    internal bool TryCaptureMonitorRect(IntPtr monitor, int originX, int originY, int w, int h, byte[] pixels, out string detail)
    {
        detail = "";
        if (w <= 0 || h <= 0 || pixels.Length < w * h * 4) { detail = "geçersiz boyut"; return false; }

        if (duplication == IntPtr.Zero || (monitor != IntPtr.Zero && monitor != currentMonitor))
        {
            Reset();
            if (!Init(monitor, out detail)) return false;
        }

        bool reinitialized = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr resource = IntPtr.Zero;
            DxgiOutduplFrameInfo info;
            int hr = AcquireNextFrame(duplication, hasFrame ? 40u : 150u, &info, &resource);
            if (hr == DXGI_ERROR_ACCESS_LOST)
            {
                if (resource != IntPtr.Zero) Release(ref resource);
                if (reinitialized) { Reset(); detail = "DXGI erişimi kesildi"; return false; }
                reinitialized = true;
                Reset();
                if (!Init(monitor, out detail)) return false;
                continue;
            }
            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                if (resource != IntPtr.Zero) Release(ref resource);
                if (hasFrame && staging != IntPtr.Zero) break;
                detail = "DXGI zaman aşımı";
                return false;
            }
            if (hr < 0)
            {
                if (resource != IntPtr.Zero) Release(ref resource);
                Reset();
                detail = $"DXGI 0x{hr:X8}";
                return false;
            }

            bool releasedFrame = false;
            try
            {
                if (info.LastPresentTime == 0 && !hasFrame)
                {
                    Release(ref resource);
                    ReleaseFrame(duplication);
                    releasedFrame = true;
                    continue;
                }

                Guid texId = IID_ID3D11Texture2D;
                hr = QueryInterface(resource, &texId, out IntPtr tex2d);
                if (hr < 0 || tex2d == IntPtr.Zero)
                {
                    detail = $"DXGI QI doku 0x{hr:X8}";
                    return false;
                }
                try
                {
                    D3D11Texture2DDesc desc;
                    GetTexture2DDesc(tex2d, &desc);
                    if (staging == IntPtr.Zero || stagingW != desc.Width || stagingH != desc.Height || stagingFormat != desc.Format)
                    {
                        Release(ref staging);
                        D3D11Texture2DDesc stDesc = new()
                        {
                            Width = desc.Width,
                            Height = desc.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = desc.Format,
                            SampleCount = 1,
                            SampleQuality = 0,
                            Usage = (int)D3D11_USAGE_STAGING,
                            BindFlags = 0,
                            CpuAccessFlags = D3D11_CPU_ACCESS_READ,
                            MiscFlags = 0,
                        };
                        hr = CreateTexture2D(device, &stDesc, IntPtr.Zero, out staging);
                        if (hr < 0 || staging == IntPtr.Zero)
                        {
                            staging = IntPtr.Zero;
                            detail = $"DXGI doku oluşturma 0x{hr:X8}";
                            return false;
                        }
                        stagingW = desc.Width; stagingH = desc.Height; stagingFormat = desc.Format;
                    }
                    CopyResource(context, staging, tex2d);
                    hasFrame = true;
                }
                finally
                {
                    Release(ref tex2d);
                    Release(ref resource);
                }
            }
            finally
            {
                Release(ref resource);
                if (!releasedFrame) ReleaseFrame(duplication);
            }
            break;
        }

        if (!hasFrame || staging == IntPtr.Zero) { detail = "DXGI kare yok"; return false; }

        int srcX = originX - monitorRect.Left;
        int srcY = originY - monitorRect.Top;
        int dstX0 = Math.Max(0, -srcX);
        int copyX0 = Math.Max(0, srcX);
        int copyX1 = Math.Min((int)stagingW, srcX + w);
        int dstY0 = Math.Max(0, -srcY);
        int copyY0 = Math.Max(0, srcY);
        int copyY1 = Math.Min((int)stagingH, srcY + h);
        int copyCols = copyX1 - copyX0;
        int copyRows = copyY1 - copyY0;
        if (copyCols <= 0 || copyRows <= 0) { detail = "pencere monitör dışında"; return false; }

        D3D11MappedSubresource mapped;
        int mapHr = Map(context, staging, 0, D3D11_MAP_READ, 0, &mapped);
        if (mapHr < 0 || mapped.Data == IntPtr.Zero) { detail = $"DXGI haritalama 0x{mapHr:X8}"; return false; }
        try
        {
            if (dstX0 > 0 || dstY0 > 0 || copyCols < w || copyRows < h) Array.Clear(pixels);
            byte* srcBase = (byte*)mapped.Data;
            fixed (byte* dstBase = pixels)
            {
                if (stagingFormat is 87 or 88) // B8G8R8A8_UNORM / B8G8R8X8_UNORM
                {
                    long rowBytes = (long)copyCols * 4;
                    for (int row = 0; row < copyRows; row++)
                    {
                        byte* srcRow = srcBase + (long)(copyY0 + row) * mapped.RowPitch + (long)copyX0 * 4;
                        byte* dstRow = dstBase + (long)((dstY0 + row) * w + dstX0) * 4;
                        Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
                    }
                }
                else if (stagingFormat == 28) // R8G8B8A8_UNORM
                {
                    for (int row = 0; row < copyRows; row++)
                    {
                        byte* src = srcBase + (long)(copyY0 + row) * mapped.RowPitch + (long)copyX0 * 4;
                        byte* dst = dstBase + (long)((dstY0 + row) * w + dstX0) * 4;
                        for (int col = 0; col < copyCols; col++)
                        {
                            dst[0] = src[2]; dst[1] = src[1]; dst[2] = src[0]; dst[3] = src[3];
                            src += 4; dst += 4;
                        }
                    }
                }
                else if (stagingFormat == 10) // R16G16B16A16_FLOAT
                {
                    for (int row = 0; row < copyRows; row++)
                    {
                        ushort* src = (ushort*)(srcBase + (long)(copyY0 + row) * mapped.RowPitch + (long)copyX0 * 8);
                        byte* dst = dstBase + (long)((dstY0 + row) * w + dstX0) * 4;
                        for (int col = 0; col < copyCols; col++)
                        {
                            float r = (float)BitConverter.UInt16BitsToHalf(src[0]);
                            float g = (float)BitConverter.UInt16BitsToHalf(src[1]);
                            float b = (float)BitConverter.UInt16BitsToHalf(src[2]);
                            dst[0] = ToSrgbByte(b);
                            dst[1] = ToSrgbByte(g);
                            dst[2] = ToSrgbByte(r);
                            dst[3] = 255;
                            src += 4; dst += 4;
                        }
                    }
                }
                else
                {
                    detail = $"DXGI desteklenmeyen biçim {stagingFormat}";
                    return false;
                }
            }
        }
        finally
        {
            Unmap(context, staging, 0);
        }

        if (GameWindowCapture.MostlyBlackBytes(pixels, w, h))
        {
            detail = "DXGI siyah kare";
            return false;
        }
        return true;
    }

    private static byte ToSrgbByte(float linear) =>
        (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f) * 255f), 0, 255);

    private bool Init(IntPtr targetMonitor, out string detail)
    {
        detail = "";
        Guid factoryId = IID_IDXGIFactory1;
        int hr = CreateDXGIFactory1(in factoryId, out IntPtr factory);
        if (hr < 0 || factory == IntPtr.Zero) { detail = $"DXGI fabrika 0x{hr:X8}"; return false; }

        try
        {
            for (uint ai = 0; ; ai++)
            {
                int ehr = EnumAdapters(factory, ai, out IntPtr adapter);
                if (ehr < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    for (uint oi = 0; ; oi++)
                    {
                        int ohr = EnumOutputs(adapter, oi, out IntPtr output);
                        if (ohr < 0 || output == IntPtr.Zero) break;
                        try
                        {
                            DxgiOutputDesc desc;
                            if (GetOutputDesc(output, &desc) != S_OK) continue;
                            bool match = targetMonitor == IntPtr.Zero ? desc.AttachedToDesktop != 0 : desc.Monitor == targetMonitor;
                            if (!match) continue;

                            hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                                out device, out _, out context);
                            if (hr < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
                            {
                                Reset();
                                detail = $"D3D11 cihaz 0x{hr:X8}";
                                return false;
                            }

                            Guid out1Id = IID_IDXGIOutput1;
                            hr = QueryInterface(output, &out1Id, out IntPtr output1);
                            if (hr < 0 || output1 == IntPtr.Zero)
                            {
                                Reset();
                                detail = $"DXGI Output1 0x{hr:X8}";
                                return false;
                            }
                            try
                            {
                                hr = DuplicateOutput(output1, device, out duplication);
                                if (hr < 0 || duplication == IntPtr.Zero)
                                {
                                    Reset();
                                    detail = $"DXGI DuplicateOutput 0x{hr:X8}";
                                    return false;
                                }
                                monitorRect = desc.DesktopCoordinates;
                                currentMonitor = desc.Monitor;
                                hasFrame = false;
                                return true;
                            }
                            finally { Release(ref output1); }
                        }
                        finally { Release(ref output); }
                    }
                }
                finally { Release(ref adapter); }
            }
            detail = "monitör çıktısı bulunamadı";
            return false;
        }
        finally { Release(ref factory); }
    }

    private void Reset()
    {
        Release(ref staging);
        Release(ref duplication);
        Release(ref context);
        Release(ref device);
        currentMonitor = IntPtr.Zero;
        stagingW = stagingH = 0; stagingFormat = 0;
        hasFrame = false;
    }

    public void Dispose() => Reset();

    private static IntPtr* Vtbl(IntPtr obj) => *(IntPtr**)obj;
    private static int QueryInterface(IntPtr obj, Guid* iid, out IntPtr result)
    {
        IntPtr p = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(obj)[0])(obj, iid, &p);
        result = p;
        return hr;
    }
    private static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        IntPtr p = obj; obj = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(p)[2])(p);
    }
    private static int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter)
    {
        IntPtr p = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(factory)[7])(factory, index, &p);
        adapter = p;
        return hr;
    }
    private static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        IntPtr p = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(adapter)[7])(adapter, index, &p);
        output = p;
        return hr;
    }
    private static int GetOutputDesc(IntPtr output, DxgiOutputDesc* desc) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, DxgiOutputDesc*, int>)Vtbl(output)[7])(output, desc);
    private static int DuplicateOutput(IntPtr output1, IntPtr d3dDevice, out IntPtr dup)
    {
        IntPtr p = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(output1)[22])(output1, d3dDevice, &p);
        dup = p;
        return hr;
    }
    private static int AcquireNextFrame(IntPtr dup, uint timeoutMs, DxgiOutduplFrameInfo* info, IntPtr* resource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, DxgiOutduplFrameInfo*, IntPtr*, int>)Vtbl(dup)[8])(dup, timeoutMs, info, resource);
    private static int ReleaseFrame(IntPtr dup) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(dup)[14])(dup);
    private static int CreateTexture2D(IntPtr d3dDevice, D3D11Texture2DDesc* desc, IntPtr initialData, out IntPtr tex)
    {
        IntPtr p = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11Texture2DDesc*, IntPtr, IntPtr*, int>)Vtbl(d3dDevice)[5])(d3dDevice, desc, initialData, &p);
        tex = p;
        return hr;
    }
    private static void GetTexture2DDesc(IntPtr tex, D3D11Texture2DDesc* desc) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D11Texture2DDesc*, void>)Vtbl(tex)[10])(tex, desc);
    private static int Map(IntPtr ctx, IntPtr res, uint subresource, uint mapType, uint mapFlags, D3D11MappedSubresource* mapped) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11MappedSubresource*, int>)Vtbl(ctx)[14])(ctx, res, subresource, mapType, mapFlags, mapped);
    private static void Unmap(IntPtr ctx, IntPtr res, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(ctx)[15])(ctx, res, subresource);
    private static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(ctx)[47])(ctx, dst, src);

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutputDesc
    {
        public fixed char DeviceName[32];
        public GameWindowCapture.NativeRect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutduplPointerPosition
    {
        public GameWindowCapture.Point Position;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiOutduplFrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DxgiOutduplPointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags, IntPtr pFeatureLevels, uint featureLevels,
        uint sdkVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);
}