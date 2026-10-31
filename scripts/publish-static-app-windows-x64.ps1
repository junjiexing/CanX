# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [string] $CacheRoot,
    [string] $OutputDirectory,
    [switch] $SkipNativeBuild,
    [switch] $SmokeBuild,
    [switch] $SkipSmoke,
    [string] $Proxy,
    [ValidateRange(1, 256)]
    [int] $Jobs = [Environment]::ProcessorCount,
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'support\initialize-windows-build-environment.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime = "win-$Architecture"
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $cacheName = if ($Architecture -eq 'x64') { 'windows-msvc' } else { "windows-msvc-$Architecture" }
    $CacheRoot = Join-Path $repositoryRoot ".cache\$cacheName"
}
elseif (-not [IO.Path]::IsPathRooted($CacheRoot)) {
    $CacheRoot = Join-Path $repositoryRoot $CacheRoot
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = if ($SmokeBuild) {
        Join-Path $repositoryRoot "artifacts\verify\$runtime"
    }
    else {
        Join-Path $repositoryRoot "artifacts\publish\$runtime"
    }
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$symbolsDirectory = if ($SmokeBuild) {
    Join-Path $repositoryRoot "artifacts\verify\$runtime-symbols"
}
else {
    Join-Path $repositoryRoot "artifacts\symbols\$runtime"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter()] [string[]] $Arguments = @()
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

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

if (-not (Test-Path -LiteralPath (Join-Path $CacheRoot 'prefix\mpv\lib\libmpv.a'))) {
    if ($SkipNativeBuild) {
        throw "Prebuilt native dependencies are missing: $CacheRoot. Run scripts/fetch-native-deps.ps1 -Architecture $Architecture first."
    }
    & (Join-Path $PSScriptRoot 'fetch-native-deps.ps1') -Architecture $Architecture -CacheRoot $CacheRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'The prebuilt native dependency download failed.'
    }
}

$vcpkgLibraryRoot = Join-Path $CacheRoot "vcpkg-installed\$Architecture-windows-static\lib"
$libraries = @(
    (Join-Path $CacheRoot 'build\aria2-shim\Release\canx_aria2.lib'),
    (Join-Path $CacheRoot 'build\quickjs-shim\Release\canx_quickjs.lib'),
    (Join-Path $CacheRoot 'prefix\aria2\lib\aria2.lib'),
    (Join-Path $CacheRoot 'prefix\mpv\lib\libmpv.a'),
    (Join-Path $CacheRoot 'prefix\libplacebo\lib\libplacebo.a'),
    (Join-Path $CacheRoot 'prefix\zimg\lib\z.lib'),
    (Join-Path $vcpkgLibraryRoot 'archive.lib'),
    (Join-Path $vcpkgLibraryRoot 'ass.lib'),
    (Join-Path $vcpkgLibraryRoot 'avdevice.lib'),
    (Join-Path $vcpkgLibraryRoot 'avfilter.lib'),
    (Join-Path $vcpkgLibraryRoot 'avformat.lib'),
    (Join-Path $vcpkgLibraryRoot 'avcodec.lib'),
    (Join-Path $vcpkgLibraryRoot 'swscale.lib'),
    (Join-Path $vcpkgLibraryRoot 'swresample.lib'),
    (Join-Path $vcpkgLibraryRoot 'avutil.lib'),
    (Join-Path $vcpkgLibraryRoot 'glslang-default-resource-limits.lib'),
    (Join-Path $vcpkgLibraryRoot 'SPIRV.lib'),
    (Join-Path $vcpkgLibraryRoot 'MachineIndependent.lib'),
    (Join-Path $vcpkgLibraryRoot 'OSDependent.lib'),
    (Join-Path $vcpkgLibraryRoot 'GenericCodeGen.lib'),
    (Join-Path $vcpkgLibraryRoot 'SPIRV-Tools-opt.lib'),
    (Join-Path $vcpkgLibraryRoot 'SPIRV-Tools.lib'),
    (Join-Path $vcpkgLibraryRoot 'shaderc.lib'),
    (Join-Path $vcpkgLibraryRoot 'glslang.lib'),
    (Join-Path $vcpkgLibraryRoot 'shaderc_util.lib'),
    (Join-Path $vcpkgLibraryRoot 'mujs.lib'),
    (Join-Path $vcpkgLibraryRoot 'lua51.lib'),
    (Join-Path $vcpkgLibraryRoot 'uchardet.lib'),
    (Join-Path $vcpkgLibraryRoot 'lcms2.lib'),
    (Join-Path $vcpkgLibraryRoot 'lcms2_fast_float.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-c.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-glsl.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-hlsl.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-msl.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-cpp.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-reflect.lib'),
    (Join-Path $vcpkgLibraryRoot 'spirv-cross-core.lib'),
    (Join-Path $vcpkgLibraryRoot 'fribidi.lib'),
    # Optris embeds HarfBuzz 8.3.1. Do not also link vcpkg harfbuzz.lib;
    # native/windows-msvc-compat supplies the two hb-ft entry points FFmpeg uses.
    (Join-Path $vcpkgLibraryRoot 'fontconfig.lib'),
    (Join-Path $vcpkgLibraryRoot 'freetype.lib'),
    (Join-Path $vcpkgLibraryRoot 'libexpatMT.lib'),
    (Join-Path $vcpkgLibraryRoot 'bz2.lib'),
    (Join-Path $vcpkgLibraryRoot 'libpng16.lib'),
    (Join-Path $vcpkgLibraryRoot 'brotlidec.lib'),
    (Join-Path $vcpkgLibraryRoot 'brotlicommon.lib'),
    (Join-Path $vcpkgLibraryRoot 'jpeg.lib'),
    (Join-Path $vcpkgLibraryRoot 'lzma.lib'),
    (Join-Path $vcpkgLibraryRoot 'lz4.lib'),
    (Join-Path $vcpkgLibraryRoot 'zstd.lib'),
    (Join-Path $vcpkgLibraryRoot 'vpx.lib'),
    (Join-Path $vcpkgLibraryRoot 'iconv.lib'),
    (Join-Path $vcpkgLibraryRoot 'dav1d.lib'),
    (Join-Path $vcpkgLibraryRoot 'openjp2.lib'),
    (Join-Path $vcpkgLibraryRoot 'opus.lib'),
    (Join-Path $vcpkgLibraryRoot 'vorbisenc.lib'),
    (Join-Path $vcpkgLibraryRoot 'vorbis.lib'),
    (Join-Path $vcpkgLibraryRoot 'ogg.lib'),
    (Join-Path $vcpkgLibraryRoot 'vpl.lib'),
    (Join-Path $vcpkgLibraryRoot 'OpenCL.lib'),
    (Join-Path $vcpkgLibraryRoot 'vulkan-1.lib'),
    (Join-Path $vcpkgLibraryRoot 'zs.lib'),
    (Join-Path $vcpkgLibraryRoot 'libssl.lib'),
    (Join-Path $vcpkgLibraryRoot 'libcrypto.lib'),
    (Join-Path $vcpkgLibraryRoot 'sqlite3.lib'),
    (Join-Path $vcpkgLibraryRoot 'libssh2.lib'),
    (Join-Path $vcpkgLibraryRoot 'cares.lib'))

if ($Architecture -eq 'arm64') {
    $libraries = @($libraries | Where-Object {
        [IO.Path]::GetFileName($_) -notin @('lua51.lib', 'vpl.lib')
    }) + (Join-Path $CacheRoot 'prefix\lua\lib\lua.lib')
}

$systemLibraries = @(
    'avrt.lib', 'dwmapi.lib', 'imm32.lib', 'ntdll.lib', 'pathcch.lib',
    # windowsapp.lib is an umbrella import library. When it precedes the
    # concrete Win32 libraries, it resolves the same symbols through dozens of
    # api-ms-win-* contracts. Every symbol needed by this image is already
    # provided by the explicit libraries below or by NativeAOT's SDK libraries.
    'shcore.lib', 'uxtheme.lib', 'runtimeobject.lib',
    'opengl32.lib', 'version.lib', 'xmllite.lib', 'psapi.lib', 'uuid.lib',
    'oleaut32.lib', 'shlwapi.lib', 'shell32.lib', 'vfw32.lib', 'gdi32.lib',
    'mfuuid.lib', 'strmiids.lib', 'mfplat.lib', 'mfreadwrite.lib', 'ole32.lib',
    'crypt32.lib', 'ws2_32.lib', 'wsock32.lib', 'advapi32.lib', 'user32.lib',
    'bcrypt.lib', 'winmm.lib', 'iphlpapi.lib', 'cfgmgr32.lib', 'comctl32.lib')

$missing = @($libraries | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -ne 0) {
    throw "Static native libraries are missing. Run scripts/fetch-native-deps.ps1 -Architecture $Architecture first:`n$($missing -join "`n")"
}

$dotnet = Resolve-Dotnet
$stagingDirectory = Join-Path $CacheRoot 'publish-stage'
$expectedStaging = [IO.Path]::GetFullPath((Join-Path $CacheRoot 'publish-stage'))
if (([IO.Path]::GetFullPath($stagingDirectory)) -ne $expectedStaging) {
    throw 'Refusing to clean an unexpected staging directory.'
}
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

$publishArguments = @(
    'publish', (Join-Path $repositoryRoot 'src\CanX.App\CanX.App.csproj'),
    '--configuration', 'Release',
    '--runtime', $runtime,
    '--self-contained',
    '--output', $stagingDirectory,
    '-p:PublishAot=true',
    '-p:PublishSingleFile=true',
    '-p:CanXProductionPublish=true',
    '-p:DebugSymbols=true',
    '-p:DebugType=portable',
    ('-p:CanXIncludeSmokeTests=' + $SmokeBuild.ToString().ToLowerInvariant()),
    '-p:CanXStaticWindowsUcrt=true',
    '-p:CanXDirectPInvokeLibraries=canx_aria2|mpv|canx_quickjs',
    ('-p:CanXStaticLibraries=' + (($libraries + $systemLibraries) -join '|')),
    '-p:CanXStaticNativeSidecars=canx_aria2.dll|mpv.dll|canx_quickjs.dll')
Invoke-Checked $dotnet $publishArguments

$stagedExecutable = Join-Path $stagingDirectory 'CanX.App.exe'
if (-not (Test-Path -LiteralPath $stagedExecutable)) {
    throw "NativeAOT executable was not produced: $stagedExecutable"
}

$auditArguments = @{
    Binary = $stagedExecutable
    ExpectedArchitecture = $Architecture
    RejectDynamicUcrt = $true
    MaximumApiSetLibraries = 5
}
$hostVulkanLoader = Join-Path $CacheRoot 'vcpkg-installed\x64-windows\bin\vulkan-1.dll'
if (Test-Path -LiteralPath $hostVulkanLoader) {
    $auditArguments.VulkanLoader = $hostVulkanLoader
}
& (Join-Path $PSScriptRoot 'audit-native-dependencies-windows-x64.ps1') @auditArguments

if ($SmokeBuild -and -not $SkipSmoke) {
    $stdout = Join-Path $CacheRoot 'windows-shell-smoke.stdout.log'
    $stderr = Join-Path $CacheRoot 'windows-shell-smoke.stderr.log'
    $process = Start-Process -FilePath $stagedExecutable -ArgumentList '--smoke' `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id -Force
        throw 'CanX.App --smoke timed out after 30 seconds.'
    }
    $smokeOutput = if (Test-Path -LiteralPath $stdout) {
        Get-Content -LiteralPath $stdout -Raw
    }
    else {
        ''
    }
    $smokeError = if (Test-Path -LiteralPath $stderr) {
        Get-Content -LiteralPath $stderr -Raw
    }
    else {
        ''
    }
    if ($smokeOutput -notmatch 'CANX_APP_SHELL_OK') {
        throw "CanX.App --smoke failed.`n$smokeOutput`n$smokeError"
    }
    $smokeOutput.TrimEnd() | Write-Output
}

$outputRootName = if ($SmokeBuild) { 'verify' } else { 'publish' }
$defaultOutputRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\$outputRootName")).TrimEnd('\') + '\'
if (-not $OutputDirectory.StartsWith($defaultOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay below $defaultOutputRoot so it can be replaced safely."
}
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$finalExecutable = Join-Path $OutputDirectory 'CanX.App.exe'
Copy-Item -LiteralPath $stagedExecutable -Destination $finalExecutable

New-Item -ItemType Directory -Force -Path $symbolsDirectory | Out-Null
Get-ChildItem -LiteralPath $symbolsDirectory -Filter '*.pdb' -File | Remove-Item -Force
$symbolFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -Filter '*.pdb' -File)
if ($symbolFiles.Count -eq 0) {
    throw "Windows debug symbols were not produced: $stagingDirectory"
}
foreach ($symbolFile in $symbolFiles) {
    Move-Item -LiteralPath $symbolFile.FullName -Destination $symbolsDirectory -Force
}

$unexpected = @(Get-ChildItem -LiteralPath $OutputDirectory -Force |
    Where-Object { $_.Name -ne 'CanX.App.exe' })
if ($unexpected.Count -ne 0) {
    throw "Single-file output contains unexpected files: $($unexpected.Name -join ', ')"
}

$hash = (Get-FileHash -LiteralPath $finalExecutable -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $finalExecutable).Length
"CANX_WINDOWS_STATIC_APP_OK output=$finalExecutable symbols=$symbolsDirectory smoke=$($SmokeBuild.ToString().ToLowerInvariant()) bytes=$size sha256=$hash"
