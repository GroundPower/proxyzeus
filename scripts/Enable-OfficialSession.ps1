[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$ProxyProcessId,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [ValidateSet('127.0.0.1','127.0.0.2')][string]$RedirectAddress = '127.0.0.1'
)
$ErrorActionPreference = 'Stop'
$installed = $false
try {
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Hosts kurulumu yonetici izni gerektirir.' }
    $proxyProcess = Get-Process -Id $ProxyProcessId -ErrorAction Stop
    $expectedExe = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\ZeusProxy\ZeusProxy.exe'))
    if ($proxyProcess.Path -ine $expectedExe) { throw 'Beklenen Zeus uygulamasi bulunamadi.' }
    & (Join-Path $PSScriptRoot 'Hosts.ps1') -Action Install -Address $RedirectAddress -Confirm:$false | Out-Null
    $installed = $true
    Clear-DnsClientCache
    [IO.File]::WriteAllText($ResultPath, '{"status":"installed"}')
    $failures = 0
    while (-not $proxyProcess.HasExited) {
        Start-Sleep -Seconds 2
        try {
            $health = Invoke-WebRequest -Uri 'http://127.0.0.1/health' -UseBasicParsing -TimeoutSec 2
            if ($health.StatusCode -ne 200) { throw 'Health check failed' }
            $failures = 0
        }
        catch { $failures++; if ($failures -ge 2) { break } }
        $proxyProcess.Refresh()
    }
}
catch {
    [IO.File]::WriteAllText($ResultPath, (@{status='error'; message=$_.Exception.Message} | ConvertTo-Json -Compress))
}
finally {
    if ($installed) {
        try {
            & (Join-Path $PSScriptRoot 'Hosts.ps1') -Action Remove -Confirm:$false | Out-Null
            Clear-DnsClientCache
            [IO.File]::WriteAllText($ResultPath, '{"status":"restored"}')
        }
        catch { [IO.File]::WriteAllText($ResultPath, (@{status='restore-error'; message=$_.Exception.Message} | ConvertTo-Json -Compress)) }
    }
}
