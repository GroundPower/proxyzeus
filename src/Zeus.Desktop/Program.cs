namespace Zeus.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RegisterNativeResolver();
        if (args.Length == 1 && args[0] == "--ensure-tls")
        {
            try
            {
                var tlsData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeusProxy");
                using var _ = Zeus.Core.CertificateManager.GetOrCreate();
                Zeus.Core.CertificateManager.EnsurePem(Path.Combine(tlsData, "tls"));
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
            return;
        }
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

    private static void RegisterNativeResolver()
    {
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Zeus.Core.NativeHost).Assembly, (name, _, _) =>
        {
            if (!name.Equals("zeus_native", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("zeus_native.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            string local = Path.Combine(AppContext.BaseDirectory, "zeus_native.dll");
            if (File.Exists(local) && System.Runtime.InteropServices.NativeLibrary.TryLoad(local, out IntPtr localHandle))
                return localHandle;
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("zeus_native.dll");
            if (stream == null) return IntPtr.Zero;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeusProxy", "native", hash);
            string target = Path.Combine(dir, "zeus_native.dll");
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(dir);
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, target, overwrite: false);
                }
                catch when (File.Exists(target)) { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
            return System.Runtime.InteropServices.NativeLibrary.Load(target);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}