# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Binary,
    [int] $MaximumApiSetLibraries = -1,
    [switch] $RejectDynamicUcrt
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$binaryPath = (Resolve-Path -LiteralPath $Binary).Path
$bytes = [IO.File]::ReadAllBytes($binaryPath)

function Read-UInt16([int] $Offset) {
    [BitConverter]::ToUInt16($bytes, $Offset)
}

function Read-UInt32([int] $Offset) {
    [BitConverter]::ToUInt32($bytes, $Offset)
}

function Read-UInt64([int] $Offset) {
    [BitConverter]::ToUInt64($bytes, $Offset)
}

function Read-Ascii([int] $Offset) {
    $end = $Offset
    while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) {
        ++$end
    }
    [Text.Encoding]::ASCII.GetString($bytes, $Offset, $end - $Offset)
}

$peOffset = [int](Read-UInt32 0x3c)
if ($peOffset -lt 0 -or $peOffset + 24 -gt $bytes.Length -or
    (Read-UInt32 $peOffset) -ne 0x00004550) {
    throw "Not a PE image: $binaryPath"
}

$coffOffset = $peOffset + 4
$sectionCount = [int](Read-UInt16 ($coffOffset + 2))
$optionalSize = [int](Read-UInt16 ($coffOffset + 16))
$optionalOffset = $coffOffset + 20
$magic = Read-UInt16 $optionalOffset
$dataDirectoryOffset = switch ($magic) {
    0x10b { $optionalOffset + 96 }
    0x20b { $optionalOffset + 112 }
    default { throw "Unsupported PE optional-header magic 0x$($magic.ToString('x'))" }
}
$importRva = Read-UInt32 ($dataDirectoryOffset + 8)
$sectionTableOffset = $optionalOffset + $optionalSize
$sections = for ($index = 0; $index -lt $sectionCount; ++$index) {
    $offset = $sectionTableOffset + 40 * $index
    [pscustomobject]@{
        VirtualSize = Read-UInt32 ($offset + 8)
        VirtualAddress = Read-UInt32 ($offset + 12)
        RawSize = Read-UInt32 ($offset + 16)
        RawOffset = Read-UInt32 ($offset + 20)
    }
}

function Convert-RvaToOffset([uint32] $Rva) {
    foreach ($section in $sections) {
        $size = [Math]::Max($section.VirtualSize, $section.RawSize)
        if ($Rva -ge $section.VirtualAddress -and
            $Rva -lt $section.VirtualAddress + $size) {
            return [int]($section.RawOffset + $Rva - $section.VirtualAddress)
        }
    }
    throw "RVA 0x$($Rva.ToString('x')) is outside the PE sections"
}

if (-not ('CanX.NativeAudit' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CanX
{
    public static class NativeAudit
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true)]
        public static extern IntPtr GetProcAddressOrdinal(IntPtr module, IntPtr ordinal);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);
    }
}
'@
}

$imports = [Collections.Generic.List[object]]::new()
if ($importRva -ne 0) {
    $descriptorOffset = Convert-RvaToOffset $importRva
    while ($true) {
        $originalThunk = Read-UInt32 $descriptorOffset
        $timeDateStamp = Read-UInt32 ($descriptorOffset + 4)
        $forwarderChain = Read-UInt32 ($descriptorOffset + 8)
        $nameRva = Read-UInt32 ($descriptorOffset + 12)
        $firstThunk = Read-UInt32 ($descriptorOffset + 16)
        if (($originalThunk -bor $timeDateStamp -bor $forwarderChain -bor $nameRva -bor $firstThunk) -eq 0) {
            break
        }

        $library = Read-Ascii (Convert-RvaToOffset $nameRva)
        $symbols = [Collections.Generic.List[object]]::new()
        $thunkRva = if ($originalThunk -ne 0) { $originalThunk } else { $firstThunk }
        if ($thunkRva -ne 0) {
            $thunkOffset = Convert-RvaToOffset $thunkRva
            while ($true) {
                $thunk = Read-UInt64 $thunkOffset
                if ($thunk -eq 0) { break }
                if (($thunk -band 0x8000000000000000L) -ne 0) {
                    $symbols.Add([pscustomobject]@{ Name = $null; Ordinal = [int]($thunk -band 0xffff) })
                }
                else {
                    $nameOffset = Convert-RvaToOffset ([uint32]$thunk)
                    $symbols.Add([pscustomobject]@{ Name = Read-Ascii ($nameOffset + 2); Ordinal = 0 })
                }
                $thunkOffset += 8
            }
        }
        $imports.Add([pscustomobject]@{ Library = $library; Symbols = $symbols })
        $descriptorOffset += 20
    }
}

$systemRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath('System')).TrimEnd('\') + '\'
$failures = [Collections.Generic.List[string]]::new()
$apiSetImports = @($imports | Where-Object {
    $_.Library.StartsWith('api-ms-win-', [StringComparison]::OrdinalIgnoreCase)
})
$dynamicUcrtImports = @($apiSetImports | Where-Object {
    $_.Library.StartsWith('api-ms-win-crt-', [StringComparison]::OrdinalIgnoreCase)
})

if ($RejectDynamicUcrt -and $dynamicUcrtImports.Count -ne 0) {
    $failures.Add(
        "dynamic UCRT imports remain: $($dynamicUcrtImports.Library -join ', ')")
}
if ($MaximumApiSetLibraries -ge 0 -and $apiSetImports.Count -gt $MaximumApiSetLibraries) {
    $failures.Add(
        "API Set import count $($apiSetImports.Count) exceeds the allowed maximum $MaximumApiSetLibraries")
}

foreach ($import in $imports) {
    $module = [CanX.NativeAudit]::LoadLibraryExW($import.Library, [IntPtr]::Zero, 0x800)
    if ($module -eq [IntPtr]::Zero) {
        $failures.Add("missing library: $($import.Library) (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))")
        continue
    }
    try {
        $pathBuilder = [Text.StringBuilder]::new(32768)
        [void][CanX.NativeAudit]::GetModuleFileNameW($module, $pathBuilder, $pathBuilder.Capacity)
        $loadedPath = $pathBuilder.ToString()
        if (-not $loadedPath.StartsWith($systemRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("non-system dependency: $($import.Library) -> $loadedPath")
        }
        foreach ($symbol in $import.Symbols) {
            $address = if ($null -ne $symbol.Name) {
                [CanX.NativeAudit]::GetProcAddress($module, $symbol.Name)
            }
            else {
                [CanX.NativeAudit]::GetProcAddressOrdinal($module, [IntPtr]$symbol.Ordinal)
            }
            if ($address -eq [IntPtr]::Zero) {
                $label = if ($null -ne $symbol.Name) { $symbol.Name } else { "ordinal $($symbol.Ordinal)" }
                $failures.Add("missing entry point: $($import.Library)!$label")
            }
        }
        "{0} -> {1} ({2} imports)" -f $import.Library, $loadedPath, $import.Symbols.Count
    }
    finally {
        [void][CanX.NativeAudit]::FreeLibrary($module)
    }
}

if ($failures.Count -ne 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

"CANX_WINDOWS_NATIVE_IMPORTS_OK binary=$binaryPath libraries=$($imports.Count) api_sets=$($apiSetImports.Count)"
