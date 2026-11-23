$ErrorActionPreference = 'Stop'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('zeus-hosts-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDirectory)
$testHosts = Join-Path $testDirectory 'hosts'
$original = "# existing user configuration`r`n127.0.0.1 localhost`r`n10.0.0.4 dev.internal`r`n"
[IO.File]::WriteAllText($testHosts, $original, [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Hosts.ps1') -Action Install -HostsPath $testHosts -Confirm:$false
$installed = [IO.File]::ReadAllText($testHosts)
if (-not $installed.StartsWith($original) -or -not $installed.Contains('# BEGIN ZEUS PROXY')) { throw 'Hosts install mevcut satirlari korumadi.' }
& (Join-Path $PSScriptRoot 'Hosts.ps1') -Action Install -HostsPath $testHosts -Confirm:$false
if ([IO.File]::ReadAllText($testHosts) -cne $installed) { throw 'Hosts install idempotent degil.' }
& (Join-Path $PSScriptRoot 'Hosts.ps1') -Action Remove -HostsPath $testHosts -Confirm:$false
if ([IO.File]::ReadAllText($testHosts) -cne $original) { throw 'Hosts remove orijinal metni korumadi.' }
Write-Output 'PASS Hosts Install / tekrar Install / Remove / mevcut satirlar korundu. Sistem hosts dosyasina dokunulmadi.'
