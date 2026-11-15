param([string]$PackageName = ('HuiTian-Business-0.1.0-preview-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Use-BusinessEnvironment.ps1')
if ($PackageName -notmatch '^[a-zA-Z0-9._-]+$' -or $PackageName -in @('.','..')) { throw '安装包名称必须是普通文件夹名称。' }
$productRoot = $env:HUITIAN_BUSINESS_HOME
$destination = Join-Path (Join-Path $productRoot 'Packages') $PackageName
if (Test-Path -LiteralPath $destination) { throw '已有同名包目录，未覆盖。请使用新名称。' }
$modelSelection = Get-Content -LiteralPath (Join-Path $productRoot 'Data\model-selection.json') -Raw | ConvertFrom-Json
$modelName = $modelSelection.fileName
if ($modelName -ne 'Qwen3.5-2B-Q4_K_M.gguf') { throw '本产品仅导出经配置的 2B 模型，未导出。' }
$modelSource = Join-Path (Join-Path $productRoot 'Models') $modelName
if (!(Test-Path -LiteralPath $modelSource -PathType Leaf)) { throw '模型不存在。' }
foreach ($name in @('Releases','Runtime','Models','Data')) { New-Item -ItemType Directory -Path (Join-Path $destination $name) -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $productRoot 'Releases\0.1.0') -Destination (Join-Path $destination 'Releases') -Recurse
Copy-Item -LiteralPath (Join-Path $productRoot 'Runtime\llama') -Destination (Join-Path $destination 'Runtime') -Recurse
Copy-Item -LiteralPath $modelSource -Destination (Join-Path $destination 'Models')
Copy-Item -LiteralPath (Join-Path $productRoot 'Data\model-selection.json') -Destination (Join-Path $destination 'Data')
Copy-Item -LiteralPath (Join-Path $productRoot 'product.marker'), (Join-Path $productRoot '启动商业版.cmd') -Destination $destination
Copy-Item -LiteralPath (Join-Path $productRoot 'Licenses') -Destination $destination -Recurse
Copy-Item -LiteralPath (Join-Path $productRoot 'Project\README.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $productRoot 'Project\docs') -Destination $destination -Recurse
Write-Host "已导出干净预览包：$destination"
Write-Host '未包含会话、联系人、浏览器登录资料、密钥、任务产物、测试资源、开发缓存或自用版内容。请放在非系统盘再启动。此包尚未通过正式商用验收。'
