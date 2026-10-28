param([string]$ProjectDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference='Stop'
if(-not $env:IDF_PATH){throw 'Откройте ESP-IDF 5.5 PowerShell перед запуском этого скрипта.'}
if(-not $ProjectDirectory){$ProjectDirectory=if(Test-Path (Join-Path $PSScriptRoot 'CMakeLists.txt')){$PSScriptRoot}else{Join-Path $PSScriptRoot '..\firmware'}}
$taskProject=[IO.Path]::GetFullPath($ProjectDirectory)
if($taskProject -match '[^\x00-\x7F]|\s'){throw 'Для сборки ESP-IDF нужна папка без пробелов и кириллицы, например C:\EspProjects\display.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $taskProject 'binaries'}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory); New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskProfiles=@(@('default','sdkconfig.defaults','st7789-y0.bin'),@('gap80','sdkconfig.defaults;profiles/gap80.defaults','st7789-y80.bin'),@('bgr','sdkconfig.defaults;profiles/bgr.defaults','st7789-y0-bgr.bin'),@('gap80-bgr','sdkconfig.defaults;profiles/gap80-bgr.defaults','st7789-y80-bgr.bin'))
Push-Location $taskProject
try{
    foreach($taskProfile in $taskProfiles){
        & python (Join-Path $env:IDF_PATH 'tools\idf.py') -B ('build-'+$taskProfile[0]) -D ('SDKCONFIG=sdkconfig.'+$taskProfile[0]) -D ('SDKCONFIG_DEFAULTS='+$taskProfile[1]) build merge-bin
        if($LASTEXITCODE -ne 0){throw ('Не собран вариант '+$taskProfile[0])}
        Copy-Item -LiteralPath (Join-Path $taskProject ('build-'+$taskProfile[0]+'\merged-binary.bin')) -Destination (Join-Path $taskOutput $taskProfile[2]) -Force
    }
}finally{Pop-Location}
