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
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeusProxy");
            Directory.CreateDirectory(data);
            Zeus.Core.Telemetry.LogFilePath = Path.Combine(data, "zeus.log");
            Zeus.Core.RelayEngine.SettingsPath = Path.Combine(data, "settings.json");
            Zeus.Core.RelayEngine.MapDumpDirectory = data;
            File.AppendAllText(Zeus.Core.Telemetry.LogFilePath, $"{Environment.NewLine}=== Zeus Proxy {Application.ProductVersion} başladı {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch (IOException) { Zeus.Core.Telemetry.LogFilePath = null; }
        // Path mode: Shift + left click in the game walks there. Key state is read the moment the click arrives.
        Zeus.Core.RelayEngine.IsShiftDown = () => (GetAsyncKeyState(0x10) & 0x8000) != 0;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Zeus Proxy");
        Application.Run(new MainForm(args.Contains("--start", StringComparer.Ordinal)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
