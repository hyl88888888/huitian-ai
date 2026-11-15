param([string]$DotNet = 'D:\HuiTian\Runtime\DotNetSDK\dotnet.exe', [string]$ReleaseName = '0.1.0')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Use-BusinessEnvironment.ps1')
$projectDirectory = Split-Path -Parent $PSScriptRoot
if ($ReleaseName -notmatch '^[a-zA-Z0-9._-]+$' -or $ReleaseName -in @('.','..')) { throw '发布名称必须是普通文件夹名称。' }
$releaseDirectory = Join-Path (Join-Path $env:HUITIAN_BUSINESS_HOME 'Releases') $ReleaseName
if (!(Test-Path -LiteralPath $DotNet -PathType Leaf)) { throw '需要 .NET 10 SDK 来构建。成品运行不需要 SDK。' }
& $DotNet publish (Join-Path $projectDirectory 'src\HuiTian.Business.App\HuiTian.Business.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $releaseDirectory
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md'), (Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md') -Destination $releaseDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectDirectory 'docs') -Destination $releaseDirectory -Recurse -Force
$licenseDirectory = Join-Path $env:HUITIAN_BUSINESS_HOME 'Licenses'
foreach ($package in Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Directory) {
    foreach ($version in Get-ChildItem -LiteralPath $package.FullName -Directory) {
        foreach ($notice in Get-ChildItem -LiteralPath $version.FullName -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.?PARTY.?NOTICES)(\.|$)' }) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenseDirectory ($package.Name + '-' + $version.Name + '-' + $notice.Name)) -Force
        }
    }
}
Copy-Item -LiteralPath $licenseDirectory -Destination $releaseDirectory -Recurse -Force
Write-Host "已发布到 $releaseDirectory。模型、用户数据和运行时留在独立产品根目录，不需要安装到 C 盘。"
