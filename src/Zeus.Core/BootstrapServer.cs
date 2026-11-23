using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Zeus.Core;

public sealed class BootstrapServer(Action? onRequest = null, Action<string>? onDiagnostic = null, Action<Profile>? onResolved = null) : IAsyncDisposable
{
    private WebApplication? app;
    private X509Certificate2? certificate;
    public async Task StartAsync(Profile profile, CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (onDiagnostic != null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddProvider(new TlsDiagnosticProvider(onDiagnostic));
        }
        if (profile.BootstrapTls)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Yerel TLS sertifikası Windows gerektirir.");
            certificate = CertificateManager.GetOrCreate();
        }
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(IPAddress.Loopback, profile.BootstrapPort, listen =>
            { if (certificate != null) listen.UseHttps(certificate); });
            if (profile.BootstrapHttpPort > 0) options.Listen(IPAddress.Loopback, profile.BootstrapHttpPort);
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/health" && context.Request.Path != "/growtopia/server_data.php")
                onDiagnostic?.Invoke("Yerel HTTP servisine farklı bir yol için istek geldi.");
            await next(context);
        });
        app.MapMethods("/growtopia/server_data.php", ["GET", "POST"], async context =>
        {
            onRequest?.Invoke();
            Profile responseProfile = profile;
            if (!string.IsNullOrEmpty(profile.DiscoveryIp))
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                    string body = await reader.ReadToEndAsync(context.RequestAborted);
                    responseProfile = await Discovery.FetchAsync(profile with { DiscoveryBody = body }, context.RequestAborted);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ArgumentException)
                {
                    onDiagnostic?.Invoke("Resmî sunucu bilgisi alınamadı: " + error.GetType().Name);
                    context.Response.StatusCode = 502;
                    return;
                }
            }
            onResolved?.Invoke(responseProfile);
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(BuildServerData(responseProfile), context.RequestAborted);
        });
        app.MapGet("/health", () => Results.Json(new { service = "Zeus Proxy", status = "ok" }));
        await app.StartAsync(token);
    }
    public static string BuildServerData(Profile profile)
    {
        if (string.IsNullOrEmpty(profile.ServerData))
            return $"server|127.0.0.1\nport|{profile.LocalPort}\ntype|1\nmeta|{profile.Meta}\nRTENDMARKERBS1001\n";
        return string.Join("\n", profile.ServerData.Replace("\r\n", "\n").Split('\n').Select(line =>
            line.StartsWith("server|", StringComparison.Ordinal) ? "server|127.0.0.1" :
            line.StartsWith("port|", StringComparison.Ordinal) ? $"port|{profile.LocalPort}" :
            line.StartsWith("meta|", StringComparison.Ordinal) ? "meta|" + profile.Meta : line));
    }
    public async ValueTask DisposeAsync()
    {
        if (app != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); }
        }
        certificate?.Dispose();
    }
}

internal sealed class TlsDiagnosticProvider(Action<string> report) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TlsDiagnosticLogger(categoryName, report);
    public void Dispose() { }
    private sealed class TlsDiagnosticLogger(string category, Action<string> report) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => category.Contains(".Https.", StringComparison.Ordinal) && logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string detail = exception?.GetBaseException().Message ?? formatter(state, exception);
            if (detail.Length > 240) detail = detail[..240];
            report("Yerel TLS: " + detail.Replace('\r', ' ').Replace('\n', ' '));
        }
    }
}

public static class Discovery
{
    public static Profile Parse(string text, Profile profile)
    {
        if (text.Length > 65536) throw new IOException("Sunucu yanıtı çok büyük.");
        string host = Protocol.Field(text, "server");
        if (!int.TryParse(Protocol.Field(text, "port"), out int port)) throw new IOException("Sunucu yanıtında geçerli port yok.");
        var result = profile with { ServerHost = host, ServerPort = port, Meta = Protocol.Field(text, "meta"), ServerData = text };
        result.Validate();
        return result;
    }
    public static async Task<Profile> FetchAsync(Profile profile, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(profile.DiscoveryUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Keşif adresi kimlik bilgisi içermeyen bir HTTPS adresi olmalı.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        if (!string.IsNullOrEmpty(profile.DiscoveryIp))
        {
            if (!IPAddress.TryParse(profile.DiscoveryIp, out var directIp)) throw new ArgumentException("Keşif IPv4 adresi geçersiz.");
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(directIp, context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
        }
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.UserAgent.ParseAdd("UbiServices_SDK_2019.Release.27_PC64_unicode_static");
        request.Content = new StringContent(profile.DiscoveryBody, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new IOException("Sunucu yanıtı çok büyük.");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var content = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (content.Length + count > 65536) throw new IOException("Sunucu yanıtı çok büyük.");
            content.Write(buffer, 0, count);
        }
        return Parse(Encoding.UTF8.GetString(content.ToArray()), profile);
    }
}

public static class CertificateManager
{
    private const string Subject = "CN=Zeus Proxy Local";
    private static readonly string[] LocalHosts = ["localhost", "growtopia1.com", "growtopia2.com", "www.growtopia1.com", "www.growtopia2.com"];

    private static (RSA Key, X509Certificate2 Cert) CreateLocalCertificate()
    {
        var key = RSA.Create(2048);
        try
        {
            var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            foreach (string host in LocalHosts) san.AddDnsName(host);
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.Parse("127.0.0.2"));
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            return (key, cert);
        }
        catch { key.Dispose(); throw; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static X509Certificate2 GetOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Cast<X509Certificate2>().FirstOrDefault(c =>
            c.Subject == Subject && c.FriendlyName == "Zeus Proxy Local TLS" && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(7));
        if (existing != null) return existing;
        var (key, temporary) = CreateLocalCertificate();
        using (key)
        using (temporary)
        {
            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var certificate = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx, password), password,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            certificate.FriendlyName = "Zeus Proxy Local TLS";
            store.Add(certificate);
            return certificate;
        }
    }

    public static void EnsurePem(string directory)
    {
        string certPath = Path.Combine(directory, "cert.pem"), keyPath = Path.Combine(directory, "key.pem");
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            try
            {
                using var existing = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                if (existing.NotAfter > DateTime.Now.AddDays(7)) return;
            }
            catch (Exception error) when (error is CryptographicException or IOException or ArgumentException) { }
        }
        Directory.CreateDirectory(directory);
        var (key, cert) = CreateLocalCertificate();
        using (key)
        using (cert)
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(certPath, cert.ExportCertificatePem(), utf8);
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem(), utf8);
        }
    }
}
