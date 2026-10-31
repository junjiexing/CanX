# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [string] $CacheRoot,
    [switch] $SkipNodeInstall,
    [switch] $SkipNativeBuild,
    [switch] $SkipNativeHost
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $CacheRoot = Join-Path $repositoryRoot '.cache\windows-msvc'
}
elseif (-not [IO.Path]::IsPathRooted($CacheRoot)) {
    $CacheRoot = Join-Path $repositoryRoot $CacheRoot
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)

function Resolve-Executable {
    param(
        [Parameter(Mandatory = $true)] [string[]] $Names,
        [Parameter()] [string[]] $Candidates = @()
    )

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) {
            return $command.Source
        }
    }
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw "Required executable was not found: $($Names -join ', ')"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter()] [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)] [string] $WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

$dotnetCandidates = @()
if ($env:ProgramFiles) {
    $dotnetCandidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}
if (${env:ProgramFiles(x86)}) {
    $dotnetCandidates += Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'
}
if ($env:LOCALAPPDATA) {
    $dotnetCandidates += Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
}
$dotnet = Resolve-Executable -Names @('dotnet.exe', 'dotnet') -Candidates $dotnetCandidates
$npm = Resolve-Executable -Names @('npm.cmd', 'npm.exe', 'npm')

Invoke-Checked $dotnet @(
    'test', (Join-Path $repositoryRoot 'tests\CanX.Plugins.Tests\CanX.Plugins.Tests.csproj'),
    '--configuration', 'Release') $repositoryRoot
Invoke-Checked $dotnet @(
    'test', (Join-Path $repositoryRoot 'tests\CanX.Foundation.Tests\CanX.Foundation.Tests.csproj'),
    '--configuration', 'Release') $repositoryRoot

$nodeProjects = @(
    (Join-Path $repositoryRoot 'sdk\canx-plugin-api'),
    (Join-Path $repositoryRoot 'plugins\example\internet-archive-video'),
    (Join-Path $repositoryRoot 'plugins\example\opensubtitles-search'),
    (Join-Path $repositoryRoot 'plugins\example\playback-filter-panel'),
    (Join-Path $repositoryRoot 'plugins\example\mock-magnet-search'),
    (Join-Path $repositoryRoot 'plugins\bangumi-anime'),
    (Join-Path $repositoryRoot 'plugins\anime-find'),
    (Join-Path $repositoryRoot 'plugins\subtitle-assrt'),
    (Join-Path $repositoryRoot 'plugins\seekurl'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-knaben'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-apibay'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-nyaa'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-internet-archive'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-jackett'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-prowlarr'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-bitmagnet'),
    (Join-Path $repositoryRoot 'plugins\torrent-search-tribler'))

if (-not $SkipNodeInstall) {
    foreach ($project in $nodeProjects) {
        Invoke-Checked $npm @('ci', '--ignore-scripts') $project
    }
}

$sdk = $nodeProjects[0]
Invoke-Checked $npm @('run', 'lint') $sdk
Invoke-Checked $npm @('run', 'typecheck') $sdk
Invoke-Checked $npm @('test') $sdk

foreach ($plugin in $nodeProjects[1..($nodeProjects.Count - 1)]) {
    Invoke-Checked $npm @('test') $plugin
    # Plugin manifests point at compiled modules under dist/. A clean checkout
    # therefore has to build before the workspace can be linted.
    Invoke-Checked $npm @('run', 'lint') $plugin
    Invoke-Checked $npm @('run', 'pack:canx') $plugin
}

$nativeStatus = 'skipped'
if (-not $SkipNativeHost) {
    $nativeArguments = @{
        CacheRoot = $CacheRoot
    }
    if ($SkipNativeBuild) {
        $nativeArguments.SkipNativeBuild = $true
    }
    & (Join-Path $PSScriptRoot 'verify-quickjs-host-windows-x64.ps1') @nativeArguments
    $nativeStatus = 'passed'
}

"CANX_PLUGIN_STAGE_OK managed=passed sdk=passed plugins=$($nodeProjects.Count - 1) native=$nativeStatus"
