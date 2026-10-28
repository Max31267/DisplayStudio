$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskLocalDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $taskLocalDotnet) {
    $taskDotnet = $taskLocalDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
} else {
    $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
