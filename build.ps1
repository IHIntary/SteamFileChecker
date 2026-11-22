# -Version: shown in the app's About page and written into the .exe (e.g. 1.2.0); release builds pass it from the Git tag.
# -SelfContained: bundle the .NET runtime so users do not need to install .NET 10 Desktop Runtime.
param([switch]$Run, [string]$OutputDirectory = 'artifacts/app', [string]$Version = '', [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
$executable = Join-Path $destination 'SteamFileChecker.Desktop.exe'
$running = Get-CimInstance Win32_Process -Filter "Name = 'SteamFileChecker.Desktop.exe'" | Where-Object { $_.ExecutablePath -eq $executable }
if ($running) { throw '目标目录中的程序正在运行。请指定不同的 -OutputDirectory；不会关闭或覆盖运行中的程序。' }
$sdk = if (Test-Path "$PSScriptRoot/.tools/dotnet/dotnet.exe") { "$PSScriptRoot/.tools/dotnet/dotnet.exe" } else { 'dotnet' }
# External tools (npm, dotnet) report failure through their exit code, which is checked after each step. Their stderr may
# carry harmless warnings (e.g. npm deprecation notices) that Windows PowerShell would otherwise turn into terminating errors.
$ErrorActionPreference = 'Continue'
Push-Location ui
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'UI regression checks failed' }
    # The UI reads the version at build time (import.meta.env.VITE_APP_VERSION); set only for the build, not the tests.
    $env:VITE_APP_VERSION = $Version
    try { npm run build } finally { Remove-Item Env:VITE_APP_VERSION -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
} finally { Pop-Location }
& $sdk run --project tests/SteamFileChecker.Tests
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
$publishArgs = @('publish', 'src/SteamFileChecker.Desktop', '-c', 'Release', '-r', 'win-x64', '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }), '-o', $destination)
if ($Version) { $publishArgs += "-p:Version=$Version" }
& $sdk @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
if ($Run) { Start-Process -FilePath $executable }
