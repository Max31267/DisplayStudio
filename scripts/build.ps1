. (Join-Path $PSScriptRoot 'common.ps1')
& $taskDotnet build (Join-Path $taskRoot 'MiniDisplay.slnx') -c Release --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
