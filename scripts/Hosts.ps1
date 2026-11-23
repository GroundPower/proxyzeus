[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Remove')][string]$Action,
    [string]$HostsPath = "$env:SystemRoot\System32\drivers\etc\hosts",
    [ValidateSet('127.0.0.1','127.0.0.2')][string]$Address = '127.0.0.1'
)
$ErrorActionPreference = 'Stop'
$resolvedHosts = (Resolve-Path -LiteralPath $HostsPath).Path
$before = [IO.File]::ReadAllBytes($resolvedHosts)
$reader = [IO.StreamReader]::new($resolvedHosts, [Text.UTF8Encoding]::new($false), $true)
try { $text = $reader.ReadToEnd(); $encoding = $reader.CurrentEncoding } finally { $reader.Dispose() }
$begin = '# BEGIN ZEUS PROXY'
$end = '# END ZEUS PROXY'
$pattern = '(?m)^# BEGIN ZEUS PROXY\r?\n(?:[^\r\n]*(?:\r?\n|$))*?^# END ZEUS PROXY(?:\r?\n|$)'
$matchesFound = [regex]::Matches($text, $pattern)
if ($matchesFound.Count -gt 1) { throw 'Birden fazla Zeus blogu var; dosyayi elle inceleyin.' }
if (($text.Contains($begin) -or $text.Contains($end)) -and $matchesFound.Count -eq 0) {
    throw 'Eksik Zeus blogu bulundu; mevcut dosya degistirilmedi.'
}
$updated = [regex]::Replace($text, $pattern, '')
if ($Action -eq 'Install') {
    if ($updated -match '(?im)^(?!\s*#)[^\r\n]*\s(?:www\.)?growtopia[12]\.com(?:\s|$)') {
        throw 'Mevcut Growtopia yonlendirmesi bulundu; once bu kaydi inceleyin.'
    }
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    if ($updated.Length -gt 0 -and -not $updated.EndsWith("`n")) { $updated += $newline }
    $updated += $begin + $newline + $Address + ' growtopia1.com growtopia2.com www.growtopia1.com www.growtopia2.com' + $newline + $end + $newline
}
if ($updated -ceq $text) { Write-Output 'Degisiklik gerekmiyor.'; return }
if ($PSCmdlet.ShouldProcess($resolvedHosts, "Zeus hosts blogu: $Action")) {
    $current = [IO.File]::ReadAllBytes($resolvedHosts)
    if ([Convert]::ToBase64String($current) -cne [Convert]::ToBase64String($before)) {
        throw 'Hosts dosyasi islem sirasinda degisti; tekrar deneyin.'
    }
    $backup = $resolvedHosts + '.zeus-backup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')
    $tempFile = $resolvedHosts + '.zeus-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($tempFile, $updated, $encoding)
        [IO.File]::Replace($tempFile, $resolvedHosts, $backup)
    }
    finally { if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile } }
    Write-Output "Tamamlandi. Yedek: $backup"
}
