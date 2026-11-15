param([ValidateSet('lite')][string]$Model = 'lite')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Use-BusinessEnvironment.ps1"
$modelFolder = Join-Path $businessRoot 'Models'
New-Item -ItemType Directory -Path $modelFolder -Force | Out-Null
    $repo = 'unsloth/Qwen3.5-2B-GGUF'
    $revision = 'f6d5376be1edb4d416d56da11e5397a961aca8ae'
    $modelName = 'Qwen3.5-2B-Q4_K_M.gguf'
    $expectedBytes = 1280835840
    $expectedHash = 'aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223'
$modelTarget = Join-Path $modelFolder $modelName
if (Test-Path -LiteralPath $modelTarget) {
    if ((Get-Item -LiteralPath $modelTarget).Length -eq $expectedBytes -and (Get-FileHash -LiteralPath $modelTarget -Algorithm SHA256).Hash -eq $expectedHash) { Write-Output '模型已经安装且校验通过。'; exit 0 }
    throw '已有同名模型校验不匹配，未覆盖。请先备份并移开该文件。'
}
$partialTarget = "$modelTarget.partial"
foreach ($source in @('https://hf-mirror.com','https://huggingface.co')) {
    Write-Output "正在下载 $modelName 到 D 盘。支持断点续传，文件约 $([math]::Round($expectedBytes/1GB,2)) GiB。"
    & curl.exe --fail --location --silent --show-error --continue-at - --connect-timeout 20 --speed-time 30 --speed-limit 1024 --max-time 1800 --retry 1 --retry-delay 2 --output $partialTarget "${source}/${repo}/resolve/${revision}/${modelName}?download=true"
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $partialTarget) -and (Get-Item -LiteralPath $partialTarget).Length -eq $expectedBytes) { break }
}
if (!(Test-Path -LiteralPath $partialTarget) -or (Get-Item -LiteralPath $partialTarget).Length -ne $expectedBytes) { throw '下载未完成，已保留断点；可再次运行本脚本。' }
if ((Get-FileHash -LiteralPath $partialTarget -Algorithm SHA256).Hash -ne $expectedHash) { throw '模型 SHA256 校验失败，不会启用。' }
Move-Item -LiteralPath $partialTarget -Destination $modelTarget
Write-Output "本地模型安装完成：$modelTarget。未下载视觉投影；不连接付费云端。"
