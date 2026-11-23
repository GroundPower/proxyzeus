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
    $releaseDir = Join-Path $repoRoot 'dist\ZeusProxy'
    & dotnet publish (Join-Path $repoRoot 'src\Zeus.Desktop\Zeus.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $releaseDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Yayin paketi olusturulamadi.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $releaseDir 'README.md') -Force
    $releaseDocs = Join-Path $releaseDir 'docs'
    [void][IO.Directory]::CreateDirectory($releaseDocs)
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File | Copy-Item -Destination $releaseDocs -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party\ENET-UPSTREAM.md') -Destination (Join-Path $releaseDir 'ENET-UPSTREAM.md') -Force
    foreach ($scriptFile in @('Prepare-Official.ps1', 'Enable-OfficialSession.ps1', 'Hosts.ps1', 'tls-front.cjs', 'Zeus Resmi Baglanti.bat')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptFile) -Destination (Join-Path $releaseDir $scriptFile) -Force
    }
    Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath (Join-Path $repoRoot 'dist\ZeusProxy-win-x64.zip') -Force
}
