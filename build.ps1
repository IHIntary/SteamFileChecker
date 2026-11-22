param([switch]$Run, [string]$OutputDirectory = 'artifacts/app')
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
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
} finally { Pop-Location }
& $sdk run --project tests/SteamFileChecker.Tests
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
& $sdk publish src/SteamFileChecker.Desktop -c Release -r win-x64 --self-contained false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
if ($Run) { Start-Process -FilePath $executable }
