[CmdletBinding()]
param(
  [Parameter()]
  [string]$CacheDirectory = 'D:\HuiTian\WeatherDataCache\geonames'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SourceUri = [Uri]'https://download.geonames.org/export/dump/cities15000.zip'
$ArchiveName = 'cities15000.zip'
$ExtractedName = 'cities15000.txt'
$MaximumArchiveBytes = 15MB
$UserAgent = 'HuiTian/1.0'

function Get-DDrivePath {
  param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$Description
  )

  if ([string]::IsNullOrWhiteSpace($Path)) {
    throw "$Description cannot be empty."
  }

  $FullPath = [IO.Path]::GetFullPath($Path)
  $Root = [IO.Path]::GetPathRoot($FullPath)
  if (-not [string]::Equals($Root, 'D:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Description must be on the D: drive. Received: $FullPath"
  }

  return $FullPath
}

function Assert-ArchiveLength {
  param(
    [Parameter(Mandatory)]
    [long]$Length,

    [Parameter(Mandatory)]
    [string]$Stage
  )

  if ($Length -le 0 -or $Length -gt $MaximumArchiveBytes) {
    throw "$Stage size must be greater than 0 and no more than $MaximumArchiveBytes bytes. Received: $Length bytes."
  }
}

function Get-ResponseContentLength {
  param(
    [Parameter(Mandatory)]
    [string]$HeadersPath,

    [Parameter(Mandatory)]
    [string]$Stage
  )

  $HeaderText = [IO.File]::ReadAllText($HeadersPath)
  $StatusMatches = [Text.RegularExpressions.Regex]::Matches(
    $HeaderText,
    '(?im)^HTTP/[^\s]+\s+(\d{3})\b')
  if ($StatusMatches.Count -eq 0) {
    throw "$Stage response did not contain an HTTP status line."
  }

  $FinalStatus = $StatusMatches[$StatusMatches.Count - 1]
  $StatusCode = [int]$FinalStatus.Groups[1].Value
  if ($StatusCode -ne 200) {
    throw "$Stage request returned HTTP $StatusCode instead of HTTP 200."
  }

  $FinalHeaderBlock = $HeaderText.Substring($FinalStatus.Index)
  $HeaderEnd = [Text.RegularExpressions.Regex]::Match($FinalHeaderBlock, '\r?\n\r?\n')
  if ($HeaderEnd.Success) {
    $FinalHeaderBlock = $FinalHeaderBlock.Substring(0, $HeaderEnd.Index)
  }

  $LengthMatch = [Text.RegularExpressions.Regex]::Match(
    $FinalHeaderBlock,
    '(?im)^Content-Length:\s*(\d+)\s*$')
  if (-not $LengthMatch.Success) {
    throw "$Stage response did not include Content-Length."
  }

  return [long]$LengthMatch.Groups[1].Value
}

$CachePath = Get-DDrivePath -Path $CacheDirectory -Description 'GeoNames cache directory'
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputPath = Get-DDrivePath -Path (Join-Path $ProjectRoot 'src\HuiTian.Infrastructure\Data\geonames-cn-cities.tsv') -Description 'GeoNames output file'
$OutputDirectory = Split-Path -Parent $OutputPath
$ArchivePath = Join-Path $CachePath $ArchiveName
$RunId = [Guid]::NewGuid().ToString('N')
$HeadHeadersPath = Join-Path $CachePath ".$ArchiveName.$RunId.head.headers"
$GetHeadersPath = Join-Path $CachePath ".$ArchiveName.$RunId.get.headers"
$DownloadPath = Join-Path $CachePath ".$ArchiveName.$RunId.download"
$ExtractDirectory = Join-Path $CachePath "extract-$RunId"
$OutputTemporaryPath = Join-Path $OutputDirectory ".geonames-cn-cities.$RunId.tmp"
$CurlPath = (Get-Command 'curl.exe' -CommandType Application -ErrorAction Stop).Source

New-Item -ItemType Directory -Path $CachePath -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$CityCount = 0

try {
  & $CurlPath `
    --disable `
    --ipv4 `
    --proto '=https' `
    --head `
    --fail `
    --silent `
    --show-error `
    --connect-timeout 30 `
    --max-time 120 `
    --user-agent $UserAgent `
    --output $HeadHeadersPath `
    $SourceUri.AbsoluteUri
  if ($LASTEXITCODE -ne 0) {
    throw "GeoNames HEAD request failed with curl exit code $LASTEXITCODE."
  }

  $ExpectedLength = Get-ResponseContentLength -HeadersPath $HeadHeadersPath -Stage 'GeoNames HEAD'
  Assert-ArchiveLength -Length $ExpectedLength -Stage 'GeoNames HEAD Content-Length'

  & $CurlPath `
    --disable `
    --ipv4 `
    --proto '=https' `
    --fail `
    --silent `
    --show-error `
    --connect-timeout 30 `
    --max-time 120 `
    --max-filesize $MaximumArchiveBytes `
    --user-agent $UserAgent `
    --dump-header $GetHeadersPath `
    --output $DownloadPath `
    $SourceUri.AbsoluteUri
  if ($LASTEXITCODE -ne 0) {
    throw "GeoNames GET request failed with curl exit code $LASTEXITCODE."
  }

  $GetLength = Get-ResponseContentLength -HeadersPath $GetHeadersPath -Stage 'GeoNames GET'
  Assert-ArchiveLength -Length $GetLength -Stage 'GeoNames GET Content-Length'
  if ($GetLength -ne $ExpectedLength) {
    throw "GeoNames GET Content-Length ($GetLength) did not match HEAD Content-Length ($ExpectedLength)."
  }

  $DownloadedLength = (Get-Item -LiteralPath $DownloadPath).Length
  Assert-ArchiveLength -Length $DownloadedLength -Stage 'Downloaded GeoNames archive'
  if ($DownloadedLength -ne $ExpectedLength) {
    throw "Downloaded GeoNames archive size ($DownloadedLength) did not match HEAD Content-Length ($ExpectedLength)."
  }

  Move-Item -LiteralPath $DownloadPath -Destination $ArchivePath -Force

  New-Item -ItemType Directory -Path $ExtractDirectory | Out-Null
  Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractDirectory
  $ExtractedPath = Join-Path $ExtractDirectory $ExtractedName
  if (-not (Test-Path -LiteralPath $ExtractedPath -PathType Leaf)) {
    throw "The GeoNames archive did not contain $ExtractedName."
  }

  $Utf8WithoutBom = [Text.UTF8Encoding]::new($false)
  $Reader = [IO.StreamReader]::new($ExtractedPath, $Utf8WithoutBom, $true)
  try {
    $Writer = [IO.StreamWriter]::new($OutputTemporaryPath, $false, $Utf8WithoutBom)
    try {
      $Writer.NewLine = "`n"
      $Writer.WriteLine('geoname_id' + "`t" + 'name' + "`t" + 'aliases' + "`t" + 'latitude' + "`t" + 'longitude' + "`t" + 'feature_code' + "`t" + 'population' + "`t" + 'elevation' + "`t" + 'timezone')

      while ($null -ne ($Line = $Reader.ReadLine())) {
        if ($Line.Length -eq 0) {
          continue
        }

        $Fields = $Line.Split([char[]]@("`t"), [StringSplitOptions]::None)
        if ($Fields.Length -lt 19) {
          throw "Malformed GeoNames row: expected at least 19 fields, received $($Fields.Length)."
        }

        if ($Fields[8] -ne 'CN') {
          continue
        }

        $Elevation = $Fields[15]
        if ([string]::IsNullOrEmpty($Elevation)) {
          $Elevation = $Fields[16]
        }

        $SelectedFields = @(
          $Fields[0],
          $Fields[1],
          $Fields[3],
          $Fields[4],
          $Fields[5],
          $Fields[7],
          $Fields[14],
          $Elevation,
          $Fields[17]
        )
        $Writer.WriteLine(($SelectedFields -join "`t"))
        $CityCount++
      }
    }
    finally {
      $Writer.Dispose()
    }
  }
  finally {
    $Reader.Dispose()
  }

  Move-Item -LiteralPath $OutputTemporaryPath -Destination $OutputPath -Force
  $LineCount = $CityCount + 1
  Write-Output "Generated $CityCount CN city rows plus a header ($LineCount physical lines) at $OutputPath"
}
finally {
  foreach ($TemporaryPath in @($HeadHeadersPath, $GetHeadersPath, $DownloadPath, $OutputTemporaryPath)) {
    if (Test-Path -LiteralPath $TemporaryPath) {
      Remove-Item -LiteralPath $TemporaryPath -Force
    }
  }

  if (Test-Path -LiteralPath $ExtractDirectory) {
    $VerifiedExtractPath = Get-DDrivePath -Path $ExtractDirectory -Description 'GeoNames extraction directory'
    $CachePrefix = $CachePath.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    if (-not $VerifiedExtractPath.StartsWith($CachePrefix, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Refusing to remove extraction directory outside the GeoNames cache: $VerifiedExtractPath"
    }

    Remove-Item -LiteralPath $VerifiedExtractPath -Recurse -Force
  }
}
