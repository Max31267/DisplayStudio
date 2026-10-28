$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Force -Path $taskTools | Out-Null
$taskInstaller = Join-Path $taskTools 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $taskInstaller
& $taskInstaller -Channel 10.0 -InstallDir (Join-Path $taskTools 'dotnet') -NoPath
if (-not (Test-Path -LiteralPath (Join-Path $taskTools 'dotnet\dotnet.exe'))) {
    throw 'Не удалось установить локальный .NET SDK.'
}
Write-Host 'SDK находится в .tools\dotnet. Настройки системы не изменялись.'
