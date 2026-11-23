[CmdletBinding()]
param([switch]$Publish, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) { $cmakePath = $cmakeCommand.Source }
else {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath)) { throw 'Visual Studio C++ Build Tools / CMake bulunamadi.' }
    $visualStudioPath = & $vswherePath -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $cmakePath = Join-Path $visualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
}
& $cmakePath -S (Join-Path $repoRoot 'native') -B (Join-Path $repoRoot 'build\native') -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'Native yapilandirma basarisiz.' }
& $cmakePath --build (Join-Path $repoRoot 'build\native') --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native derleme basarisiz.' }
& dotnet build (Join-Path $repoRoot 'src\Zeus.Desktop\Zeus.Desktop.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Masaustu derleme basarisiz.' }
if (-not $SkipTests) {
    & dotnet run --project (Join-Path $repoRoot 'tests\Zeus.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Testler basarisiz.' }
    & (Join-Path $PSScriptRoot 'Test-Hosts.ps1')
}
if ($Publish) {
    & dotnet publish (Join-Path $repoRoot 'src\Zeus.Desktop\Zeus.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o (Join-Path $repoRoot 'dist\ZeusProxy') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Yayin paketi olusturulamadi.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $repoRoot 'dist\ZeusProxy\README.md')
    $releaseDocs = Join-Path $repoRoot 'dist\ZeusProxy\docs'
    [void][IO.Directory]::CreateDirectory($releaseDocs)
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File | Copy-Item -Destination $releaseDocs -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party\ENET-UPSTREAM.md') -Destination (Join-Path $repoRoot 'dist\ZeusProxy\ENET-UPSTREAM.md')
}
