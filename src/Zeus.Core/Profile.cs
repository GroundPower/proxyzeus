using System.Net;
using System.Text.Json;

namespace Zeus.Core;

public sealed record Profile
{
    public string Name { get; init; } = "Varsayılan";
    public string ServerHost { get; init; } = "127.0.0.1";
    public int ServerPort { get; init; } = 17091;
    public int LocalPort { get; init; } = 17191;
    public bool UbisoftEnvelope { get; init; }
    public bool EnableBootstrap { get; init; }
    public bool BootstrapTls { get; init; }
    public int BootstrapPort { get; init; } = 8080;
    public string Meta { get; init; } = "";
    public string ServerData { get; init; } = "";
    public int BootstrapHttpPort { get; init; }
    public string DiscoveryUrl { get; init; } = "https://www.growtopia1.com/growtopia/server_data.php";
    public string DiscoveryBody { get; init; } = "";
    public string DiscoveryIp { get; init; } = "";
    public bool EnableCommands { get; init; } = true;
    public string[] Bookmarks { get; init; } = [];
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerHost) || ServerHost.Length > 253 ||
            Uri.CheckHostName(ServerHost) is not (UriHostNameType.IPv4 or UriHostNameType.Dns))
            throw new ArgumentException("Geçerli bir IPv4 adresi veya sunucu adı gir.");
        foreach (int port in new[] { ServerPort, LocalPort, BootstrapPort })
            if (port is < 1 or > 65535) throw new ArgumentException("Port 1–65535 aralığında olmalı.");
        if (IPAddress.TryParse(ServerHost, out var address) && IPAddress.IsLoopback(address) && ServerPort == LocalPort)
            throw new ArgumentException("Yerel port ile hedef port aynı olamaz; bağlantı döngüsü oluşur.");
        if (Meta.Length > 16384 || Meta.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("Meta alanı tek satır ve en fazla 16384 karakter olmalı.");
        if (ServerData.Length > 65536 || BootstrapHttpPort is < 0 or > 65535 ||
            (BootstrapHttpPort != 0 && BootstrapHttpPort == BootstrapPort))
            throw new ArgumentException("Sunucu yanıtı veya ek HTTP portu geçersiz.");
        if (Bookmarks.Length > 100 || Bookmarks.Any(w => !Protocol.ValidWorld(w)))
            throw new ArgumentException("Favori dünya adları 1–24 harf veya rakam içermeli.");
    }
}

public sealed class ProfileStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "profile.json");
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeusProxy");
    public Profile Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(FilePath)) ?? throw new IOException("Profil okunamadı.")
        : new Profile();
    public void Save(Profile profile)
    {
        profile.Validate();
        Directory.CreateDirectory(DirectoryPath);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
