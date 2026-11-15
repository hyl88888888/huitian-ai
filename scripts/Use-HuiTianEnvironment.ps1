$HuiTianProjectRoot = 'D:\HuiTian'

$env:HF_HOME = Join-Path $HuiTianProjectRoot 'Cache\HuggingFace'
$env:HUGGINGFACE_HUB_CACHE = Join-Path $HuiTianProjectRoot 'Cache\HuggingFace\Hub'
$env:MODELSCOPE_CACHE = Join-Path $HuiTianProjectRoot 'Cache\ModelScope'
$env:PIP_CACHE_DIR = Join-Path $HuiTianProjectRoot 'Cache\Pip'
$env:npm_config_cache = Join-Path $HuiTianProjectRoot 'Cache\npm'
$env:pnpm_config_store_dir = Join-Path $HuiTianProjectRoot 'Cache\pnpm-store'
$env:CARGO_HOME = Join-Path $HuiTianProjectRoot 'Cache\Cargo'
$env:RUSTUP_HOME = Join-Path $HuiTianProjectRoot 'Runtime\Rustup'
$env:NUGET_PACKAGES = Join-Path $HuiTianProjectRoot 'Cache\NuGet'
$env:DOTNET_CLI_HOME = Join-Path $HuiTianProjectRoot 'Cache\DotNetHome'
$env:TEMP = Join-Path $HuiTianProjectRoot 'Temp'
$env:TMP = Join-Path $HuiTianProjectRoot 'Temp'

$HuiTianRequiredFolders = @(
  $env:HF_HOME,
  $env:HUGGINGFACE_HUB_CACHE,
  $env:MODELSCOPE_CACHE,
  $env:PIP_CACHE_DIR,
  $env:npm_config_cache,
  $env:pnpm_config_store_dir,
  $env:CARGO_HOME,
  $env:RUSTUP_HOME,
  $env:NUGET_PACKAGES,
  $env:DOTNET_CLI_HOME,
  $env:TEMP
)

foreach ($HuiTianFolder in $HuiTianRequiredFolders) {
  New-Item -ItemType Directory -Path $HuiTianFolder -Force | Out-Null
}

Write-Host '回天开发环境已切换到 D:\HuiTian。'
