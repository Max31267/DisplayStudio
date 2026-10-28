param([string]$OutputDirectory = 'artifacts\app')
. (Join-Path $PSScriptRoot 'common.ps1')
$taskOutput = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }
& $taskDotnet publish (Join-Path $taskRoot 'src\MiniDisplay.Windows\MiniDisplay.Windows.csproj') -c Release -r win-x64 --self-contained true -o $taskOutput --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { throw 'Публикация завершилась с ошибкой.' }
Write-Host "Готово: $(Join-Path $taskOutput 'MiniDisplay.exe'). Для переноса скопируйте всю папку сборки."
