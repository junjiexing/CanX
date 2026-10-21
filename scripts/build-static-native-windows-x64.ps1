# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [ValidateSet('All', 'Dependencies', 'Zimg', 'Libplacebo', 'Mpv', 'Aria2', 'Shim', 'QuickJs')]
    [string] $Component = 'All',
    [string] $CacheRoot,
    [string] $VcpkgRoot,
    [string] $Msys2Root,
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
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $cacheName = if ($Architecture -eq 'x64') { 'windows-msvc' } else { "windows-msvc-$Architecture" }
    $CacheRoot = Join-Path $repositoryRoot ".cache\$cacheName"
}
elseif (-not [IO.Path]::IsPathRooted($CacheRoot)) {
    $CacheRoot = Join-Path $repositoryRoot $CacheRoot
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
$sourceRoot = Join-Path $CacheRoot 'sources'
$buildRoot = Join-Path $CacheRoot 'build'
$prefixRoot = Join-Path $CacheRoot 'prefix'
$installedRoot = Join-Path $CacheRoot 'vcpkg-installed'
$tripletRoot = Join-Path $installedRoot "$Architecture-windows-static"
$hostTripletRoot = Join-Path $installedRoot 'x64-windows'
$cmakePlatform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$manifestDirectory = Join-Path $repositoryRoot "native\windows-$Architecture"

New-Item -ItemType Directory -Force -Path $CacheRoot, $sourceRoot, $buildRoot, $prefixRoot | Out-Null

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

function Resolve-Executable {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter()] [string[]] $Candidates = @()
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw "Required tool was not found: $Name"
}

function Import-VisualStudioEnvironment {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    }
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        throw 'The Windows Program Files (x86) directory could not be resolved.'
    }
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'Visual Studio Installer (vswhere.exe) was not found.'
    }

    $requiredComponent = if ($Architecture -eq 'arm64') {
        'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
    } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $installationPath = (& $vswhere -latest -products '*' `
        -requires $requiredComponent `
        -property installationPath | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Visual Studio with MSVC $Architecture tools is required."
    }

    $devShellModule = Join-Path $installationPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    Import-Module $devShellModule -ErrorAction Stop
    Enter-VsDevShell -VsInstallPath $installationPath -SkipAutomaticLocation `
        -DevCmdArguments "-arch=$Architecture -host_arch=x64" | Out-Null
    return $installationPath
}

$lockEntries = @{}
Get-Content -LiteralPath (Join-Path $repositoryRoot 'native\sources-windows-x64.lock') |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object {
        $parts = $_ -split '\|', 4
        if ($parts.Count -ne 4) {
            throw "Malformed Windows native source lock entry: $_"
        }
        $lockEntries[$parts[0]] = [pscustomobject]@{
            Repository = $parts[1]
            Revision = $parts[2]
        }
    }

$git = Resolve-Executable 'git.exe' @('C:\Program Files\Git\cmd\git.exe')
$gitPrefix = @()
if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
    $gitPrefix = @('-c', "http.proxy=$Proxy", '-c', "https.proxy=$Proxy")
    $env:HTTP_PROXY = $Proxy
    $env:HTTPS_PROXY = $Proxy
}

function Invoke-Git {
    param([Parameter(Mandatory = $true)] [string[]] $Arguments)
    Invoke-Checked $git @($gitPrefix + $Arguments)
}

function Test-Git {
    param([Parameter(Mandatory = $true)] [string[]] $Arguments)

    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $git @($gitPrefix + $Arguments) *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

function Get-PinnedSource {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter()] [switch] $Submodules
    )

    if (-not $lockEntries.ContainsKey($Name)) {
        throw "No source lock entry exists for $Name."
    }
    $entry = $lockEntries[$Name]
    $directory = Join-Path $sourceRoot $Name
    $newSource = $false
    if (-not (Test-Path -LiteralPath (Join-Path $directory '.git'))) {
        Invoke-Git @('clone', '--filter=blob:none', '--no-checkout', '--config', 'core.autocrlf=false', $entry.Repository, $directory) |
            Out-Host
        $newSource = $true
    }

    if (-not (Test-Git @('-C', $directory, 'cat-file', '-e', "$($entry.Revision)^{commit}"))) {
        Invoke-Git @('-C', $directory, 'fetch', '--depth', '1', 'origin', $entry.Revision) |
            Out-Host
    }

    $head = (& $git -C $directory rev-parse HEAD 2>$null)
    if ($newSource -or $head -ne $entry.Revision) {
        if (-not $newSource) {
            $worktreeClean = Test-Git @('-C', $directory, 'diff', '--quiet')
            $indexClean = Test-Git @('-C', $directory, 'diff', '--cached', '--quiet')
            if (-not $worktreeClean -or -not $indexClean) {
                throw "Refusing to switch dirty cached source tree: $directory"
            }
        }
        Invoke-Git @('-C', $directory, 'checkout', '--detach', $entry.Revision) |
            Out-Host
    }

    if ($Submodules) {
        Invoke-Git @('-C', $directory, 'submodule', 'update', '--init', '--recursive') |
            Out-Host
    }
    return $directory
}

function Use-Patch {
    param(
        [Parameter(Mandatory = $true)] [string] $SourceDirectory,
        [Parameter(Mandatory = $true)] [string] $Patch
    )

    if (Test-Git @('-C', $SourceDirectory, 'apply', '--check', '--unidiff-zero', '--ignore-space-change', $Patch)) {
        Invoke-Checked $git @('-C', $SourceDirectory, 'apply', '--unidiff-zero', '--ignore-space-change', $Patch)
        return
    }
    if (-not (Test-Git @('-C', $SourceDirectory, 'apply', '--reverse', '--check', '--unidiff-zero', '--ignore-space-change', $Patch))) {
        throw "Patch does not apply cleanly: $Patch"
    }
}

$visualStudioRoot = Import-VisualStudioEnvironment
$cmake = Resolve-Executable 'cmake.exe' @(
    (Join-Path $visualStudioRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'))
$msbuild = Resolve-Executable 'MSBuild.exe' @(
    (Join-Path $visualStudioRoot 'MSBuild\Current\Bin\amd64\MSBuild.exe'))
$llvmBin = Join-Path $visualStudioRoot 'VC\Tools\Llvm\x64\bin'
foreach ($tool in @('clang.exe', 'clang++.exe', 'llvm-ar.exe', 'llvm-ranlib.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $llvmBin $tool))) {
        throw "Visual Studio LLVM component is missing: $tool"
    }
}
$env:PATH = "$llvmBin;$env:PATH"

if ([string]::IsNullOrWhiteSpace($VcpkgRoot)) {
    $preferredVcpkg = Join-Path $CacheRoot 'vcpkg'
    $legacyVcpkg = Join-Path $repositoryRoot '.cache\windows-static\vcpkg'
    $VcpkgRoot = if (Test-Path -LiteralPath (Join-Path $preferredVcpkg '.git')) {
        $preferredVcpkg
    }
    elseif (Test-Path -LiteralPath (Join-Path $legacyVcpkg '.git')) {
        $legacyVcpkg
    }
    else {
        $preferredVcpkg
    }
}
elseif (-not [IO.Path]::IsPathRooted($VcpkgRoot)) {
    $VcpkgRoot = Join-Path $repositoryRoot $VcpkgRoot
}
$VcpkgRoot = [IO.Path]::GetFullPath($VcpkgRoot)
$env:VCPKG_ROOT = $VcpkgRoot

function Install-Dependencies {
    if (-not (Test-Path -LiteralPath (Join-Path $VcpkgRoot '.git'))) {
        $vcpkgEntry = $lockEntries['vcpkg']
        Invoke-Git @('clone', '--filter=blob:none', '--no-checkout', $vcpkgEntry.Repository, $VcpkgRoot)
    }
    $vcpkgEntry = $lockEntries['vcpkg']
    if (-not (Test-Git @('-C', $VcpkgRoot, 'cat-file', '-e', "$($vcpkgEntry.Revision)^{commit}"))) {
        Invoke-Git @('-C', $VcpkgRoot, 'fetch', '--depth', '1', 'origin', $vcpkgEntry.Revision)
    }
    $vcpkgHead = (& $git -C $VcpkgRoot rev-parse HEAD 2>$null)
    if ($vcpkgHead -ne $vcpkgEntry.Revision) {
        Invoke-Git @('-C', $VcpkgRoot, 'checkout', '--detach', $vcpkgEntry.Revision)
    }

    $vcpkg = Join-Path $VcpkgRoot 'vcpkg.exe'
    if (-not (Test-Path -LiteralPath $vcpkg)) {
        Invoke-Checked (Join-Path $VcpkgRoot 'bootstrap-vcpkg.bat') @('-disableMetrics')
    }
    Invoke-Checked $vcpkg @(
        'install',
        "--triplet=$Architecture-windows-static",
        '--host-triplet=x64-windows',
        "--x-install-root=$installedRoot",
        "--x-manifest-root=$manifestDirectory",
        '--disable-metrics')
}

function Find-VcpkgBuildTool {
    param(
        [Parameter(Mandatory = $true)] [string] $Filter,
        [Parameter(Mandatory = $true)] [string] $Description
    )

    $tool = Get-ChildItem -LiteralPath (Join-Path $VcpkgRoot 'downloads\tools') `
        -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $tool) {
        throw "$Description was not found in the vcpkg tool cache. Run the Dependencies component first."
    }
    return $tool.FullName
}

function Initialize-MesonEnvironment {
    $script:python = Find-VcpkgBuildTool 'python.exe' 'Python'
    $script:meson = Find-VcpkgBuildTool 'meson.py' 'Meson'
    $script:ninja = Find-VcpkgBuildTool 'ninja.exe' 'Ninja'
    $pkgconf = Join-Path $hostTripletRoot 'tools\pkgconf\pkgconf.exe'
    if (-not (Test-Path -LiteralPath $pkgconf)) {
        throw "pkgconf was not installed at $pkgconf"
    }
    $env:CC = 'clang'
    $env:CXX = 'clang++'
    $env:AR = 'llvm-ar'
    $env:PKG_CONFIG = $pkgconf
    $env:LIB = "$(Join-Path $tripletRoot 'lib');$env:LIB"
    $env:PATH = "$(Split-Path $ninja);$llvmBin;$env:PATH"
}

function Invoke-MesonSetup {
    param(
        [Parameter(Mandatory = $true)] [string] $SourceDirectory,
        [Parameter(Mandatory = $true)] [string] $BuildDirectory,
        [Parameter(Mandatory = $true)] [string[]] $Options
    )

    $arguments = @($meson, 'setup', $BuildDirectory, $SourceDirectory) + $Options
    if ($Architecture -eq 'arm64') {
        $crossFile = Join-Path $CacheRoot 'meson-arm64.ini'
        $pkgconfPath = (Join-Path $hostTripletRoot 'tools\pkgconf\pkgconf.exe').Replace('\', '/')
        $configuration = @"
[binaries]
c = ['clang', '--target=aarch64-pc-windows-msvc']
cpp = ['clang++', '--target=aarch64-pc-windows-msvc']
ar = 'llvm-ar'
strip = 'llvm-strip'
pkg-config = '$pkgconfPath'
[host_machine]
system = 'windows'
cpu_family = 'aarch64'
cpu = 'aarch64'
endian = 'little'
[properties]
needs_exe_wrapper = true
"@
        [IO.File]::WriteAllText($crossFile, $configuration, [Text.UTF8Encoding]::new($false))
        $arguments += @('--cross-file', $crossFile)
    }
    if (Test-Path -LiteralPath (Join-Path $BuildDirectory 'meson-private\coredata.dat')) {
        $arguments += '--reconfigure'
    }
    Invoke-Checked $python $arguments
}

function Build-Zimg {
    $source = Get-PinnedSource 'zimg'
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\zimg-windows-static-runtime.patch')
    $build = Join-Path $buildRoot 'zimg'
    $prefix = Join-Path $prefixRoot 'zimg'
    $lib = Join-Path $prefix 'lib'
    $include = Join-Path $prefix 'include'
    New-Item -ItemType Directory -Force -Path $build, $lib, $include | Out-Null
    Invoke-Checked $msbuild @(
        (Join-Path $source '_msvc\zimg\zimg.vcxproj'),
        '/m', '/t:Rebuild', '/p:Configuration=Release', "/p:Platform=$cmakePlatform",
        "/p:OutDir=$lib\", "/p:IntDir=$build\obj\")
    Copy-Item -LiteralPath (Join-Path $source 'src\zimg\api\zimg.h') -Destination $include -Force
    Copy-Item -LiteralPath (Join-Path $source 'src\zimg\api\zimg++.hpp') -Destination $include -Force
    $pcDirectory = Join-Path $lib 'pkgconfig'
    New-Item -ItemType Directory -Force -Path $pcDirectory | Out-Null
    $prefixForPc = $prefix.Replace('\', '/')
    $pc = "prefix=$prefixForPc`nexec_prefix=`${prefix}`nlibdir=`${prefix}/lib`nincludedir=`${prefix}/include`n`nName: zimg`nDescription: Scaling, colorspace conversion, and dithering library`nVersion: 3.0.6`nLibs: -L`${libdir} -lz`nCflags: -I`${includedir}`n"
    [IO.File]::WriteAllText((Join-Path $pcDirectory 'zimg.pc'), $pc, [Text.UTF8Encoding]::new($false))
}

function Build-Libplacebo {
    Initialize-MesonEnvironment
    $source = Get-PinnedSource 'libplacebo' -Submodules
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\libplacebo-windows-msvc-static.patch')
    $build = Join-Path $buildRoot 'libplacebo-release'
    $prefix = Join-Path $prefixRoot 'libplacebo'
    $env:PKG_CONFIG_LIBDIR = "$(Join-Path $tripletRoot 'lib\pkgconfig');$(Join-Path $tripletRoot 'share\pkgconfig')"
    $env:PKG_CONFIG_PATH = $env:PKG_CONFIG_LIBDIR
    Invoke-MesonSetup $source $build @(
        '-Db_vscrt=mt', '-Db_staticpic=true', '-Dauto_features=disabled',
        '-Ddemos=false', '-Dtests=false', '-Dbench=false', '-Dfuzz=false',
        '-Dopengl=enabled', '-Dgl-proc-addr=enabled', '-Dlcms=enabled',
        '-Dvulkan=enabled', '-Dvk-proc-addr=enabled', '-Dglslang=enabled',
        '-Dshaderc=disabled', '-Dd3d11=enabled',
        "-Dvulkan-sdk=$($tripletRoot.Replace('\', '/'))",
        "-Dc_args=-I$($tripletRoot.Replace('\', '/'))/include",
        "-Dcpp_args=-I$($tripletRoot.Replace('\', '/'))/include",
        "-Dprefix=$prefix", '-Dlibdir=lib', '-Dbuildtype=release',
        '-Ddefault_library=static')
    Invoke-Checked $python @($meson, 'compile', '-C', $build, '-j', $Jobs.ToString())
    Invoke-Checked $python @($meson, 'install', '-C', $build)
}

function Build-Mpv {
    Initialize-MesonEnvironment
    $source = Get-PinnedSource 'mpv'
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\mpv-windows-msvc.patch')
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\mpv-windows-egl-registry.patch')
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\mpv-windows-static-angle.patch')
    $build = Join-Path $buildRoot 'mpv-release'
    $prefix = Join-Path $prefixRoot 'mpv'
    $pkgRoots = @(
        (Join-Path $prefixRoot 'libplacebo\lib\pkgconfig'),
        (Join-Path $prefixRoot 'zimg\lib\pkgconfig'),
        (Join-Path $tripletRoot 'lib\pkgconfig'),
        (Join-Path $tripletRoot 'share\pkgconfig'))
    if ($Architecture -eq 'arm64') {
        $luaHeader = Get-Content -LiteralPath (Join-Path $tripletRoot 'include\lua.h') -Raw
        $luaMajor = [regex]::Match($luaHeader, '#define\s+LUA_VERSION_MAJOR(?:_N)?\s+"?(\d+)')
        $luaMinor = [regex]::Match($luaHeader, '#define\s+LUA_VERSION_MINOR(?:_N)?\s+"?(\d+)')
        if (-not $luaMajor.Success -or -not $luaMinor.Success) { throw 'Lua version was not found in lua.h.' }
        $luaVersion = '{0}.{1}' -f $luaMajor.Groups[1].Value, $luaMinor.Groups[1].Value
        $luaPrefix = $tripletRoot.Replace('\', '/')
        $luaPc = "prefix=$luaPrefix`nlibdir=`${prefix}/lib`nincludedir=`${prefix}/include`n`nName: Lua`nDescription: Lua interpreter`nVersion: $luaVersion`nLibs: -L`${libdir} -llua`nCflags: -I`${includedir}`n"
        $luaPcDirectory = Join-Path $tripletRoot 'lib\pkgconfig'
        New-Item -ItemType Directory -Force $luaPcDirectory | Out-Null
        [IO.File]::WriteAllText((Join-Path $luaPcDirectory 'lua.pc'), $luaPc, [Text.UTF8Encoding]::new($false))
    }
    $env:PKG_CONFIG_LIBDIR = $pkgRoots -join ';'
    $env:PKG_CONFIG_PATH = $env:PKG_CONFIG_LIBDIR
    $env:LIB = "$(Join-Path $prefixRoot 'libplacebo\lib');$(Join-Path $prefixRoot 'zimg\lib');$env:LIB"
    $lua = if ($Architecture -eq 'arm64') { 'lua' } else { 'luajit' }
    $desktopAcceleration = if ($Architecture -eq 'arm64') { 'disabled' } else { 'enabled' }
    Invoke-MesonSetup $source $build @(
        '-Db_vscrt=mt', '-Db_staticpic=true',
        "-Dc_args=-DCANX_STATIC_ANGLE=1 -I$($tripletRoot.Replace('\', '/'))/include",
        "-Dcpp_args=-I$($tripletRoot.Replace('\', '/'))/include",
        '-Dauto_features=disabled', '-Dgpl=true', '-Dlibmpv=true',
        '-Dcplayer=false', '-Dbuild-date=false', '-Dtests=false', '-Dfuzzers=false',
        '-Dmanpage-build=disabled', '-Diconv=enabled', '-Djavascript=enabled',
        '-Djpeg=enabled', '-Dlcms2=enabled', '-Dlibarchive=enabled',
        '-Dlibavdevice=enabled', "-Dlua=$lua", '-Duchardet=enabled',
        '-Dvector=enabled', '-Dzimg=enabled', '-Dzlib=enabled',
        '-Dwasapi=enabled', '-Dwin32-threads=enabled', '-Dwin32-smtc=enabled',
        "-Damf=$desktopAcceleration", '-Dgl=enabled', '-Dplain-gl=enabled', '-Dgl-win32=enabled',
        '-Degl-angle=enabled', '-Degl-angle-lib=disabled', '-Degl-angle-win32=disabled',
        '-Dshaderc=enabled', '-Dspirv-cross=enabled', '-Dd3d11=enabled',
        '-Dvulkan=enabled', "-Dcuda-hwaccel=$desktopAcceleration", "-Dcuda-interop=$desktopAcceleration",
        '-Dd3d-hwaccel=enabled', '-Dd3d9-hwaccel=enabled',
        "-Dprefix=$prefix", '-Dlibdir=lib', '-Dbuildtype=release',
        '-Ddefault_library=static')
    Invoke-Checked $python @($meson, 'compile', '-C', $build, '-j', $Jobs.ToString())
    Invoke-Checked $python @($meson, 'install', '-C', $build)
}

function Resolve-Msys2Root {
    if (-not [string]::IsNullOrWhiteSpace($Msys2Root)) {
        return [IO.Path]::GetFullPath($Msys2Root)
    }
    foreach ($candidate in @('C:\msys64', 'E:\msys64', 'D:\msys64')) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'usr\bin\bash.exe')) {
            return $candidate
        }
    }
    throw 'MSYS2 was not found. Pass -Msys2Root; it is used only for aria2 Autotools/Make.'
}

function Build-Aria2 {
    $source = Get-PinnedSource 'aria2'
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\aria2-windows-msvc.patch')
    Use-Patch $source (Join-Path $repositoryRoot 'native\patches\aria2-runtime-priority-clear.patch')
    $msysRoot = Resolve-Msys2Root
    $env:CANX_ARIA2_SOURCE = $source
    $env:CANX_ARIA2_BUILD = Join-Path $buildRoot 'aria2-release'
    $env:CANX_ARIA2_PREFIX = Join-Path $prefixRoot 'aria2'
    $env:CANX_VCPKG_INSTALLED = $tripletRoot
    $env:CANX_WINDOWS_COMPAT_INCLUDE = Join-Path $repositoryRoot 'native\windows-msvc-compat\include'
    $env:CANX_LLVM_BIN = $llvmBin
    $env:CANX_JOBS = $Jobs.ToString()
    $env:CANX_WINDOWS_ARCHITECTURE = $Architecture
    $bash = Join-Path $msysRoot 'usr\bin\bash.exe'
    $builder = (Join-Path $repositoryRoot 'scripts\support\build-aria2-windows-msvc.sh').Replace('\', '/')
    Invoke-Checked $bash @($builder)
}

function Build-Shim {
    $ariaPrefix = Join-Path $prefixRoot 'aria2'
    $build = Join-Path $buildRoot 'aria2-shim'
    Invoke-Checked $cmake @(
        '-S', (Join-Path $repositoryRoot 'native\aria2-shim'),
        '-B', $build,
        '-G', 'Visual Studio 17 2022', '-A', $cmakePlatform,
        "-DARIA2_INCLUDE_DIR=$(Join-Path $ariaPrefix 'include')",
        "-DARIA2_LIBRARY=$(Join-Path $ariaPrefix 'lib\aria2.lib')",
        '-DCANX_ARIA2_LIBRARY_TYPE=STATIC',
        '-DCANX_ARIA2_EXTENDED_API=ON',
        "-DCANX_WINDOWS_NATIVE_INCLUDE_DIR=$(Join-Path $tripletRoot 'include')",
        '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
    Invoke-Checked $cmake @('--build', $build, '--config', 'Release', '--parallel', $Jobs.ToString())
}

function Get-QuickJsSource {
    $lockPath = Join-Path $repositoryRoot 'spikes\CanX.Spikes.QuickJsInterop\native-source.lock'
    $sourceLock = @{}
    Get-Content -LiteralPath $lockPath | ForEach-Object {
        $parts = $_ -split '=', 2
        if ($parts.Count -eq 2) {
            $sourceLock[$parts[0].Trim()] = $parts[1].Trim()
        }
    }
    foreach ($key in @('url', 'sha256', 'version')) {
        if (-not $sourceLock.ContainsKey($key)) {
            throw "QuickJS source lock is missing '$key': $lockPath"
        }
    }

    $downloads = Join-Path $CacheRoot 'downloads'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    $archive = Join-Path $downloads "quickjs-$($sourceLock.version).tar.xz"
    $archiveValid = (Test-Path -LiteralPath $archive) -and
        ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sourceLock.sha256)
    if (-not $archiveValid) {
        if (Test-Path -LiteralPath $archive) {
            Remove-Item -LiteralPath $archive -Force
        }
        $curl = Resolve-Executable 'curl.exe' @("$env:SystemRoot\System32\curl.exe")
        $download = "$archive.partial"
        $arguments = @('-fL', '--retry', '3', '--output', $download)
        if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
            $arguments += @('--proxy', $Proxy)
        }
        $arguments += $sourceLock.url
        Invoke-Checked $curl $arguments
        Move-Item -LiteralPath $download -Destination $archive -Force
    }
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $sourceLock.sha256) {
        throw "QuickJS source hash mismatch: expected $($sourceLock.sha256), got $actualHash"
    }

    $source = Join-Path $sourceRoot "quickjs-$($sourceLock.version)"
    $versionFile = Join-Path $source 'VERSION'
    if (-not (Test-Path -LiteralPath $versionFile)) {
        if (Test-Path -LiteralPath $source) {
            $expectedSource = [IO.Path]::GetFullPath((Join-Path $sourceRoot "quickjs-$($sourceLock.version)"))
            if ([IO.Path]::GetFullPath($source) -ne $expectedSource) {
                throw "Refusing to clean unexpected QuickJS source directory: $source"
            }
            Remove-Item -LiteralPath $source -Recurse -Force
        }
        Push-Location $sourceRoot
        try {
            Invoke-Checked $cmake @('-E', 'tar', 'xvf', $archive) | Out-Host
        }
        finally {
            Pop-Location
        }
    }
    if (-not (Test-Path -LiteralPath $versionFile) -or
        (Get-Content -LiteralPath $versionFile -Raw).Trim() -ne $sourceLock.version) {
        throw "QuickJS source version mismatch in $source"
    }

    $patch = Join-Path $repositoryRoot 'native\patches\quickjs-windows-msvc.patch'
    $previousCeiling = $env:GIT_CEILING_DIRECTORIES
    $env:GIT_CEILING_DIRECTORIES = Split-Path $source -Parent
    Push-Location $source
    try {
        $previousErrorAction = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $git apply --no-index --check $patch *> $null
            $patchApplies = $LASTEXITCODE -eq 0
            & $git apply --no-index --reverse --check $patch *> $null
            $patchAlreadyApplied = $LASTEXITCODE -eq 0
        }
        finally {
            $ErrorActionPreference = $previousErrorAction
        }
        if ($patchApplies) {
            Invoke-Checked $git @('apply', '--no-index', $patch) | Out-Host
        }
        elseif (-not $patchAlreadyApplied) {
            throw "QuickJS Windows patch does not apply cleanly: $patch"
        }
    }
    finally {
        Pop-Location
        $env:GIT_CEILING_DIRECTORIES = $previousCeiling
    }
    return $source
}

function Build-QuickJs {
    $source = Get-QuickJsSource
    $build = Join-Path $buildRoot 'quickjs-shim'
    Invoke-Checked $cmake @(
        '-S', (Join-Path $repositoryRoot 'native\quickjs-shim'),
        '-B', $build,
        '-G', 'Visual Studio 17 2022', '-A', $cmakePlatform, '-T', 'ClangCL',
        "-DQUICKJS_SOURCE_DIR=$source",
        '-DCANX_QUICKJS_LIBRARY_TYPE=STATIC',
        '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
    Invoke-Checked $cmake @('--build', $build, '--config', 'Release', '--parallel', $Jobs.ToString())
}

if ($Component -in @('All', 'Dependencies')) { Install-Dependencies }
if ($Component -ne 'QuickJs' -and -not (Test-Path -LiteralPath $tripletRoot)) {
    throw "Static vcpkg dependencies were not found at $tripletRoot. Build the Dependencies component first."
}
if ($Component -in @('All', 'Zimg')) { Build-Zimg }
if ($Component -in @('All', 'Libplacebo')) { Build-Libplacebo }
if ($Component -in @('All', 'Mpv')) { Build-Mpv }
if ($Component -in @('All', 'Aria2')) { Build-Aria2 }
if ($Component -in @('All', 'Shim')) { Build-Shim }
if ($Component -in @('All', 'QuickJs')) { Build-QuickJs }

$expected = @(
    (Join-Path $prefixRoot 'aria2\lib\aria2.lib'),
    (Join-Path $prefixRoot 'mpv\lib\libmpv.a'),
    (Join-Path $prefixRoot 'libplacebo\lib\libplacebo.a'),
    (Join-Path $prefixRoot 'zimg\lib\z.lib'),
    (Join-Path $buildRoot 'aria2-shim\Release\canx_aria2.lib'),
    (Join-Path $buildRoot 'quickjs-shim\Release\canx_quickjs.lib'))
if ($Component -eq 'All') {
    $missing = @($expected | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missing.Count -ne 0) {
        throw "Native build did not produce:`n$($missing -join "`n")"
    }
}

"CANX_WINDOWS_NATIVE_BUILD_OK component=$Component cache=$CacheRoot"
