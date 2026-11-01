# SPDX-License-Identifier: GPL-3.0-or-later
#
# Download the prebuilt Windows native dependency package pinned by
# native-deps.lock and extract it into the cache layout that
# publish-static-app-windows-x64.ps1 consumes (prefix/, build/,
# vcpkg-installed/ under the cache root). Extraction is skipped when the
# marker file already records the pinned tag; pass -Force to redo it.

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64',
    [string] $CacheRoot,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $cacheName = if ($Architecture -eq 'x64') { 'windows-msvc' } else { "windows-msvc-$Architecture" }
    $CacheRoot = Join-Path $repositoryRoot ".cache\$cacheName"
}
elseif (-not [IO.Path]::IsPathRooted($CacheRoot)) {
    $CacheRoot = Join-Path $repositoryRoot $CacheRoot
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)

$lockFile = Join-Path $repositoryRoot 'native-deps.lock'
if (-not (Test-Path -LiteralPath $lockFile)) {
    throw "Dependency pin is missing: $lockFile"
}
$pin = @{}
Get-Content -LiteralPath $lockFile | ForEach-Object {
    $parts = $_ -split '=', 2
    if ($parts.Count -eq 2 -and -not $parts[0].Trim().StartsWith('#')) {
        $pin[$parts[0].Trim()] = $parts[1].Trim()
    }
}
foreach ($key in @('repository', 'tag')) {
    if (-not $pin.ContainsKey($key)) {
        throw "native-deps.lock must set '$key'"
    }
}
$depsRepository = $pin.repository -replace '\.git$', ''
$depsTag = $pin.tag

$marker = Join-Path $CacheRoot '.canx-deps-tag'
if (-not $Force -and (Test-Path -LiteralPath $marker) -and
    ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $depsTag)) {
    "native dependencies for windows-$Architecture already match $depsTag"
    return
}

$downloads = Join-Path $repositoryRoot '.cache\deps-downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$asset = "canx-deps-windows-$Architecture.tar.gz"
$archive = Join-Path $downloads $asset
$checksums = Join-Path $downloads "SHA256SUMS-$depsTag"
$curl = Join-Path $env:SystemRoot 'System32\curl.exe'

if (-not (Test-Path -LiteralPath $checksums)) {
    & $curl -fsSL --retry 3 --retry-delay 2 `
        "$depsRepository/releases/download/$depsTag/SHA256SUMS" --output $checksums
    if ($LASTEXITCODE -ne 0) { throw "Failed to download SHA256SUMS for $depsTag" }
}

$expected = $null
$assetPattern = '^([0-9a-f]{64})  (\./)?' + [regex]::Escape($asset) + '$'
Get-Content -LiteralPath $checksums | ForEach-Object {
    if ($_ -match $assetPattern) { $expected = $Matches[1] }
}
if (-not $expected) {
    throw "SHA256SUMS for $depsTag does not list $asset"
}

$valid = (Test-Path -LiteralPath $archive) -and
    ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected)
if (-not $valid) {
    & $curl -fSL --retry 3 --retry-delay 2 `
        "$depsRepository/releases/download/$depsTag/$asset" --output "$archive.partial"
    if ($LASTEXITCODE -ne 0) { throw "Failed to download $asset" }
    Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Checksum mismatch for $asset (release $depsTag)"
    }
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
foreach ($managed in @('prefix', 'build\aria2-shim', 'build\quickjs-shim', 'vcpkg-installed')) {
    $path = Join-Path $CacheRoot $managed
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
& $tar -xzf $archive -C $CacheRoot
if ($LASTEXITCODE -ne 0) { throw "Failed to extract $asset" }
Set-Content -LiteralPath $marker -Value $depsTag -Encoding ascii
"extracted $asset ($depsTag) into $CacheRoot"
