namespace Zeus.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test")
        {
            try { File.WriteAllText(args[1], Zeus.Core.LocalDiagnostics.RunAsync().GetAwaiter().GetResult()); }
            catch (Exception error) { File.WriteAllText(args[1], "FAIL: " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        // Outside the try: the memory reader needs this path even when logging fails.
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeusProxy");
        try
        {
            Directory.CreateDirectory(data);
            Zeus.Core.Telemetry.LogFilePath = Path.Combine(data, "zeus.log");
            Zeus.Core.RelayEngine.SettingsPath = Path.Combine(data, "settings.json");
            Zeus.Core.RelayEngine.MapDumpDirectory = data;
            File.AppendAllText(Zeus.Core.Telemetry.LogFilePath, $"{Environment.NewLine}=== Zeus Proxy {Application.ProductVersion} başladı {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch (IOException) { Zeus.Core.Telemetry.LogFilePath = null; }
        // Path mode: Shift + left click in the game walks there. Key state is read the moment the click arrives.
        Zeus.Core.RelayEngine.IsShiftDown = () => (GetAsyncKeyState(0x10) & 0x8000) != 0;
        // Path finds the world in the window picture first; the memory reader supplies zoom and position.
        using var capture = new GameWindowCapture(() => Zeus.Core.RelayEngine.PathEnabled && Zeus.Core.RelayEngine.MouseHookActive);
        using var memory = new GameMemory(Path.Combine(data, "memory.json"));
        Zeus.Core.RelayEngine.GameFrame = capture.Fresh;
        Zeus.Core.RelayEngine.GameFrameStatus = () => capture.Status;
        Zeus.Core.RelayEngine.GameZoom = () => memory.Zoom;
        Zeus.Core.RelayEngine.GameZoomStatus = () => memory.Status;
        Zeus.Core.RelayEngine.GamePosition = () => memory.Position;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Zeus Proxy");
        Application.Run(new MainForm(args.Contains("--start", StringComparer.Ordinal)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}