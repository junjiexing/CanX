# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [string] $CacheRoot,
    [switch] $SkipNativeBuild,
    [string] $Proxy,
    [ValidateRange(1, 256)]
    [int] $Jobs = [Environment]::ProcessorCount
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

function Resolve-Dotnet {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw '.NET SDK was not found.'
}

if (-not $SkipNativeBuild) {
    $arguments = @('-Component', 'QuickJs', '-CacheRoot', $CacheRoot, '-Jobs', $Jobs.ToString())
    if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
        $arguments += @('-Proxy', $Proxy)
    }
    & (Join-Path $PSScriptRoot 'build-static-native-windows-x64.ps1') @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'The static QuickJS build failed.'
    }
}

$library = Join-Path $CacheRoot 'build\quickjs-shim\Release\canx_quickjs.lib'
if (-not (Test-Path -LiteralPath $library)) {
    throw "Static QuickJS library was not found: $library"
}

$dotnet = Resolve-Dotnet
$output = Join-Path $CacheRoot 'quickjs-host-publish'
& $dotnet publish (Join-Path $repositoryRoot 'spikes\CanX.Spikes.QuickJsInterop\CanX.Spikes.QuickJsInterop.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained `
    --output $output `
    -p:PublishAot=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:CanXStaticWindowsUcrt=true `
    -p:CanXDirectPInvokeLibraries=canx_quickjs `
    "-p:CanXStaticLibraries=$library"
if ($LASTEXITCODE -ne 0) {
    throw 'The NativeAOT QuickJS host publish failed.'
}

$executable = Join-Path $output 'CanX.Spikes.QuickJsInterop.exe'
& (Join-Path $PSScriptRoot 'audit-native-dependencies-windows-x64.ps1') `
    -Binary $executable `
    -RejectDynamicUcrt `
    -MaximumApiSetLibraries 5

$result = & $executable
if ($LASTEXITCODE -ne 0 -or
    $result -notmatch 'CANX_QUICKJS_OK.+recovery=42.+host=ok.+isolation=ok,deadline_ms=') {
    throw "QuickJS production host smoke test failed: $result"
}
$result | Write-Output
