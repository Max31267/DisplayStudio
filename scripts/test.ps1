param([string]$ZigPath)
. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'build.ps1')
$taskTestDirectory = Join-Path $taskRoot 'artifacts\tests'
New-Item -ItemType Directory -Force -Path $taskTestDirectory | Out-Null
if (-not $ZigPath) {
    $taskZig = Get-ChildItem -Path (Join-Path $taskRoot '.tools\zig-*\zig.exe') -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($taskZig) { $ZigPath = $taskZig.FullName }
}
if ($ZigPath) {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $taskRoot '.tools\zig-cache'
    $taskBridge = Join-Path $taskTestDirectory 'test_device.exe'
    & $ZigPath cc -std=c11 -Wall -Wextra -Werror "-I$(Join-Path $taskRoot 'firmware\portable')" (Join-Path $taskRoot 'firmware\portable\md_protocol.c') (Join-Path $taskRoot 'firmware\portable\md_device.c') (Join-Path $taskRoot 'tests\firmware\test_device.c') -o $taskBridge
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции C-тестов.' }
    & $taskBridge
    if ($LASTEXITCODE -ne 0) { throw 'C-тесты прошивки не прошли.' }
    $env:MD_NATIVE_BRIDGE = $taskBridge
} else {
    Remove-Item Env:MD_NATIVE_BRIDGE -ErrorAction SilentlyContinue
    Write-Host 'C-тесты пропущены: укажите -ZigPath либо распакуйте Zig в .tools.'
}
& $taskDotnet (Join-Path $taskRoot 'tests\MiniDisplay.Tests\bin\Release\net10.0\MiniDisplay.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли.' }
& $taskDotnet (Join-Path $taskRoot 'tests\MiniDisplay.Rendering.Tests\bin\Release\net10.0-windows10.0.19041.0\MiniDisplay.Rendering.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Проверки отрисовки не прошли.' }
