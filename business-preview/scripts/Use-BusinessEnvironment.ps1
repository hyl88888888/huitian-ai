$businessRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$env:HUITIAN_BUSINESS_HOME = $businessRoot
$env:DOTNET_CLI_HOME = "$businessRoot\Cache\DotNetHome"
$env:NUGET_PACKAGES = "$businessRoot\Cache\NuGet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:TEMP = "$businessRoot\Cache\Temp"
$env:TMP = $env:TEMP
$env:PLAYWRIGHT_BROWSERS_PATH = "$businessRoot\Runtime\Browsers"
$env:HF_HOME = "$businessRoot\Cache\HuggingFace"
$env:MODELSCOPE_CACHE = "$businessRoot\Cache\ModelScope"
foreach ($businessFolder in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $env:PLAYWRIGHT_BROWSERS_PATH, $env:HF_HOME, $env:MODELSCOPE_CACHE)) {
    New-Item -ItemType Directory -Path $businessFolder -Force | Out-Null
}
