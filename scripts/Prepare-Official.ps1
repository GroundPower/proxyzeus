[CmdletBinding()]
param([switch]$Start, [switch]$OpenSslFrontend)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseExe = Join-Path $repoRoot 'dist\ZeusProxy\ZeusProxy.exe'
if ($Start) {
    $existingProxy = Get-Process ZeusProxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $releaseExe }
    if ($existingProxy) { Write-Output 'Zeus zaten acik. Mevcut oturum kullanilmaya devam ediyor.'; return }
    $pendingRelease = Join-Path $repoRoot 'artifacts\diagnostic-build'
    if ((Test-Path -LiteralPath (Join-Path $pendingRelease 'ZeusProxy.exe')) -and
        (Test-Path -LiteralPath (Join-Path $pendingRelease 'zeus_native.dll'))) {
        Copy-Item -LiteralPath (Join-Path $pendingRelease 'ZeusProxy.exe') -Destination $releaseExe -Force
        Copy-Item -LiteralPath (Join-Path $pendingRelease 'zeus_native.dll') -Destination (Join-Path $repoRoot 'dist\ZeusProxy\zeus_native.dll') -Force
    }
}
$dataPath = Join-Path $env:LOCALAPPDATA 'ZeusProxy'
[void][IO.Directory]::CreateDirectory($dataPath)
$profilePath = Join-Path $dataPath 'profile.json'
$domain = 'www.growtopia1.com'
$addresses = @(Resolve-DnsName -Name $domain -Type A -DnsOnly -NoHostsFile -QuickTimeout | Where-Object Type -eq A | Select-Object -ExpandProperty IPAddress)
if ($addresses.Count -eq 0) { throw 'Resmi sunucu kesfi icin DNS adresi bulunamadi.' }
$body = $null
foreach ($address in $addresses) {
    $reply = & curl.exe --silent --show-error --fail --noproxy '*' --connect-timeout 5 --max-time 10 --resolve "${domain}:443:${address}" -X POST -H 'Content-Length: 0' -A 'UbiServices_SDK_2019.Release.27_PC64_unicode_static' "https://$domain/growtopia/server_data.php"
    if ($LASTEXITCODE -eq 0 -and ($reply -join "`n") -match '(?m)^server\|') { $body = $reply -join "`n"; break }
}
if (-not $body -or $body.Length -gt 65536) { throw 'Dogrulanmis HTTPS sunucu yaniti alinamadi.' }
$fields = @{}
foreach ($line in $body.Split("`n")) {
    $parts = $line.TrimEnd("`r").Split('|',2)
    if ($parts.Count -eq 2) { $fields[$parts[0]] = $parts[1] }
}
$parsedIp = $null
if (-not [Net.IPAddress]::TryParse($fields['server'], [ref]$parsedIp)) { throw 'Resmi yanitta gecersiz sunucu adresi var.' }
$remotePort = [int]$fields['port']
if ($remotePort -lt 1 -or $remotePort -gt 65535) { throw 'Resmi yanitta gecersiz sunucu portu var.' }
if (Test-Path -LiteralPath $profilePath) {
    $backup = $profilePath + '.before-official-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')
    Copy-Item -LiteralPath $profilePath -Destination $backup
}
$profile = [ordered]@{
    Name='Resmi Growtopia'; ServerHost=$fields['server']; ServerPort=$remotePort;
    LocalPort=17191; UbisoftEnvelope=$true; EnableBootstrap=$true; BootstrapTls=$true;
    BootstrapPort=443; BootstrapHttpPort=80; Meta=$fields['meta']; ServerData=$body;
    DiscoveryUrl="https://$domain/growtopia/server_data.php"; DiscoveryBody=''; DiscoveryIp=$address; EnableCommands=$true; Bookmarks=@()
}
[IO.File]::WriteAllText($profilePath, ($profile | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output ("Official target: {0}:{1}; bootstrap 127.0.0.1:443/80; UDP 17191" -f $profile.ServerHost,$profile.ServerPort)
if ($Start) {
    if ($OpenSslFrontend) {
        $openSslCertPath = Join-Path $dataPath 'tls\cert.pem'
        if (-not (Test-Path -LiteralPath $openSslCertPath)) { throw 'OpenSSL yerel sertifikasi bulunamadi.' }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($openSslCertPath)
        $certificate.FriendlyName = 'Zeus Proxy OpenSSL Local'
    }
    else {
        $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=Zeus Proxy Local' -and $_.FriendlyName -eq 'Zeus Proxy Local TLS' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) } | Select-Object -First 1
    }
    if (-not $certificate) { throw 'Yerel Zeus sertifikasi bulunamadi. Uygulamadan sertifikayi bir kez olusturun.' }
    if (-not (Test-Path -LiteralPath ('Cert:\CurrentUser\Root\' + $certificate.Thumbprint))) {
        $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $rootStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root','CurrentUser')
        try { $rootStore.Open('ReadWrite'); $rootStore.Add($publicCertificate) } finally { $rootStore.Dispose(); $publicCertificate.Dispose() }
    }
    $exe = $releaseExe
    $proxy = Start-Process -FilePath $exe -ArgumentList '--start' -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Normal -PassThru
    $healthy = $false
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ($proxy.HasExited) { throw 'Zeus uygulamasi baslatilamadi.' }
        try { if ((Invoke-WebRequest -Uri 'http://127.0.0.1/health' -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200) { $healthy=$true; break } } catch {}
    }
    if (-not $healthy) { throw 'Yerel HTTP(S) servisi baslatilamadi; portlarin bos oldugunu kontrol edin.' }
    $redirectAddress = '127.0.0.1'
    if ($OpenSslFrontend) {
        $tlsDirectory = Join-Path $dataPath 'tls'
        $tlsCertificate = Join-Path $tlsDirectory 'cert.pem'
        $tlsKey = Join-Path $tlsDirectory 'key.pem'
        if (-not (Test-Path -LiteralPath $tlsCertificate) -or -not (Test-Path -LiteralPath $tlsKey)) { throw 'OpenSSL yerel sertifika dosyalari bulunamadi.' }
        $nodePath = (Get-Command node.exe -ErrorAction Stop).Source
        $frontendArguments = '"' + (Join-Path $PSScriptRoot 'tls-front.cjs') + '" "' + $tlsCertificate + '" "' + $tlsKey + '" "' + (Join-Path $tlsDirectory 'frontend.log') + '" ' + $proxy.Id
        Start-Process -FilePath $nodePath -ArgumentList $frontendArguments -WindowStyle Hidden | Out-Null
        Start-Sleep -Milliseconds 500
        if ((Invoke-WebRequest -Uri 'http://127.0.0.2/health' -UseBasicParsing -TimeoutSec 3).StatusCode -ne 200) { throw 'OpenSSL yerel katmani baslatilamadi.' }
        $redirectAddress = '127.0.0.2'
    }
    $resultPath = Join-Path $dataPath 'official-session-status.json'
    [IO.File]::WriteAllText($resultPath, '{"status":"awaiting-uac"}')
    $helperPath = Join-Path $PSScriptRoot 'Enable-OfficialSession.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $helperPath + '" -ProxyProcessId ' + $proxy.Id + ' -ResultPath "' + $resultPath + '" -RedirectAddress ' + $redirectAddress
    try { Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden | Out-Null }
    catch { [IO.File]::WriteAllText($resultPath, '{"status":"uac-cancelled"}'); throw }
    Write-Output "Zeus started. Windows administrator approval is required for the hosts block. Status: $resultPath"
}
