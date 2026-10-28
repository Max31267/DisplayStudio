param([switch]$NoBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
& $taskDotnet (Join-Path $taskRoot 'src\MiniDisplay.Windows\bin\Release\net10.0-windows10.0.19041.0\MiniDisplay.dll')
if ($LASTEXITCODE -ne 0) { throw 'Приложение завершилось с ошибкой.' }
