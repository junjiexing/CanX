# SPDX-License-Identifier: GPL-3.0-or-later

[CmdletBinding()]
param(
    [string] $Application,
    [string] $OutputDirectory,
    [string] $FFmpeg = 'ffmpeg'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Windows PowerShell 5.1 leaves $PSScriptRoot empty inside [CmdletBinding()]
# parameter defaults, so resolve repository-relative defaults here.
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Application)) {
    $Application = Join-Path $repositoryRoot 'artifacts\verify\win-x64\CanX.App.exe'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\build\hls-smoke'
}
$Application = [IO.Path]::GetFullPath($Application)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

foreach ($format in @('mpegts', 'fmp4', 'aes128')) {
    $fixture = Join-Path $OutputDirectory $format
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null
    $playlist = Join-Path $fixture 'video.m3u8'
    $segmentFlags = if ($format -eq 'fmp4') { 'single_file' } else { 'independent_segments' }
    $segmentType = if ($format -eq 'fmp4') { 'fmp4' } else { 'mpegts' }
    $encryptionArguments = @()
    if ($format -eq 'aes128') {
        $keyFile = Join-Path $fixture 'key.bin'
        # RandomNumberGenerator.GetBytes(int) is .NET 6+; keep this runnable
        # from Windows PowerShell 5.1 (.NET Framework) as well.
        $key = New-Object byte[] 16
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($key) } finally { $random.Dispose() }
        [IO.File]::WriteAllBytes($keyFile, $key)
        $keyInfo = Join-Path $fixture 'key.info'
        [IO.File]::WriteAllText($keyInfo, "key.bin`n$keyFile`n")
        $encryptionArguments = @('-hls_key_info_file', $keyInfo)
    }
    Push-Location $fixture
    try {
        & $FFmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=320x180:rate=24' `
            -f lavfi -i 'sine=frequency=440:sample_rate=48000' -t 24 -c:v libx264 -preset ultrafast `
            -pix_fmt yuv420p -g 72 -keyint_min 72 -sc_threshold 0 -c:a aac -b:a 96k `
            -f hls -hls_time 3 -hls_playlist_type vod -hls_segment_type $segmentType -hls_flags $segmentFlags @encryptionArguments $playlist
        if ($LASTEXITCODE -ne 0) { throw "Could not generate $format HLS fixture." }
    }
    finally { Pop-Location }
    $master = Join-Path $fixture 'master.m3u8'
    [IO.File]::WriteAllText($master, "#EXTM3U`n#EXT-X-STREAM-INF:BANDWIDTH=1000000`nvideo.m3u8`n")
    $env:CANX_DATA_DIRECTORY = Join-Path $fixture 'app-data'
    $stdout = Join-Path $fixture 'playback.stdout.log'
    $stderr = Join-Path $fixture 'playback.stderr.log'
    $process = Start-Process -FilePath $Application -ArgumentList @('--playback-hls-smoke', "`"$master`"", '24') `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Windows PowerShell 5.1 only reports ExitCode for a Start-Process result
    # whose native handle was opened while the process was still running.
    $null = $process.Handle
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (-not $process.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -gt $deadline) { throw "$format HLS smoke timed out." }
        }
        $process.Refresh()
        $result = [IO.File]::ReadAllText($stdout)
        if ($process.ExitCode -ne 0 -or $result -notmatch 'CANX_PLAYBACK_HLS_OK') {
            throw "$format HLS smoke failed.`n$result`n$([IO.File]::ReadAllText($stderr))"
        }
        Write-Output ($result -split "`n" | Where-Object { $_ -match 'CANX_PLAYBACK_HLS_OK' })
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}
Write-Output 'CANX_PLAYBACK_HLS_MATRIX_OK'
