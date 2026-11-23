using System.Diagnostics;
using System.Runtime.InteropServices;
using Zeus.Core;

namespace Zeus.Desktop;

/// <summary>
/// Watches left clicks and wheel turns inside the Growtopia window (low-level mouse hook) and hands
/// them to the relay for path mode. Clicks in other windows are ignored and nothing is stored.
/// Every click and wheel turn still reaches the game unchanged.
/// </summary>
internal sealed class GameMouseWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_MOUSEWHEEL = 0x020A, VK_SHIFT = 0x10;
    private readonly HookProc callback;
    private IntPtr hook;
    private IntPtr cachedWindow; private bool cachedIsGame;

    public GameMouseWatcher()
    {
        callback = OnMouse;
        hook = SetWindowsHookEx(WH_MOUSE_LL, callback, GetModuleHandle(null), 0);
        RelayEngine.MouseHookActive = hook != IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == WM_LBUTTONDOWN || message == WM_MOUSEWHEEL))
        {
            try
            {
                var info = Marshal.PtrToStructure<MouseInfo>(data);
                IntPtr window = GetForegroundWindow();
                if (IsGameWindow(window) && GetClientRect(window, out var rect))
                {
                    var point = info.Point;
                    ScreenToClient(window, ref point);
                    if (point.X >= 0 && point.Y >= 0 && point.X < rect.Right && point.Y < rect.Bottom && RelayEngine.MouseEvents.Count < 128)
                    {
                        bool wheel = message == WM_MOUSEWHEEL;
                        RelayEngine.MouseEvents.Enqueue(new MouseEvent(wheel ? MouseEventKind.Wheel : MouseEventKind.LeftDown,
                            point.X, point.Y, rect.Right, rect.Bottom, (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0,
                            wheel ? (short)(info.MouseData >> 16) / 120.0 : 0, Stopwatch.GetTimestamp(), window.ToInt64()));
                    }
                }
            }
            catch (Exception) { /* never disturb the user's mouse */ }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private bool IsGameWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        if (window == cachedWindow) return cachedIsGame;
        cachedWindow = window; cachedIsGame = false;
        GetWindowThreadProcessId(window, out uint pid);
        try { using var process = Process.GetProcessById((int)pid); cachedIsGame = process.ProcessName.Equals("Growtopia", StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { }
        return cachedIsGame;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        RelayEngine.MouseHookActive = false;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public NativePoint Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
