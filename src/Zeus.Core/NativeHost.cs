using System.Runtime.InteropServices;

namespace Zeus.Core;

public sealed class NativeHost : IDisposable
{
    private IntPtr handle;
    private readonly byte[] buffer = new byte[4 * 1024 * 1024];
    private static readonly Lazy<bool> Initialized = new(() => Native.zeus_init() == 0);
    public NativeHost(ushort port = 0, bool outgoingPrefix = false, bool incomingPrefix = false, string bindAddress = "127.0.0.1", bool detectIncomingPrefix = false, bool checksum = true)
    {
        if (!Initialized.Value) throw new IOException("ENet başlatılamadı.");
        handle = Native.zeus_open(bindAddress, port, outgoingPrefix ? 1 : 0, incomingPrefix ? 1 : detectIncomingPrefix ? 2 : 0);
        if (handle == IntPtr.Zero) throw new IOException($"UDP {port} açılamadı. Port kullanımda olabilir.");
        if (!checksum) Native.zeus_checksum(handle, 0);
    }
    public ushort Port => Native.zeus_port(handle);
    public bool Connected => Native.zeus_connected(handle) != 0;
    public uint Rtt => Native.zeus_rtt(handle);
    public uint ReceivedDatagrams => Native.zeus_received_datagrams(handle);
    public int ReceiveFormat => Native.zeus_receive_format(handle);
    public void Connect(string ip, ushort port)
    {
        if (Native.zeus_connect(handle, ip, port) != 0) throw new IOException("Uzak bağlantı başlatılamadı.");
    }
    public NativeEvent? Poll()
    {
        int result = Native.zeus_poll(handle, buffer, buffer.Length, out int type, out int length, out int channel, out int flags);
        if (result < 0) throw new IOException($"ENet olay hatası ({result}).");
        return result == 0 ? null : new NativeEvent(type, buffer.AsSpan(0, length).ToArray(), channel, flags);
    }
    public void Send(byte[] data, int channel = 0, int flags = 1)
    {
        if (Native.zeus_send(handle, data, data.Length, channel, flags) != 0) throw new IOException("Paket gönderilemedi; bağlantıyı kontrol et.");
    }
    public void Flush() => Native.zeus_flush(handle);
    public void Reset() => Native.zeus_reset(handle);
    public void Dispose()
    {
        if (handle != IntPtr.Zero) { Native.zeus_close(handle); handle = IntPtr.Zero; }
    }
    private static class Native
    {
        private const string Lib = "zeus_native";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int zeus_init();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern IntPtr zeus_open(string ip, ushort port, int outgoing, int incoming);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int zeus_connect(IntPtr host, string ip, ushort port);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int zeus_poll(IntPtr host, byte[] buffer, int capacity, out int type, out int length, out int channel, out int flags);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int zeus_send(IntPtr host, byte[] data, int length, int channel, int flags);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void zeus_flush(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void zeus_reset(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint zeus_rtt(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int zeus_connected(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern ushort zeus_port(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint zeus_received_datagrams(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void zeus_checksum(IntPtr host, int enabled);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int zeus_receive_format(IntPtr host);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void zeus_close(IntPtr host);
    }
}
public sealed record NativeEvent(int Type, byte[] Data, int Channel, int Flags);
