#Requires -Version 7.0
<#
.SYNOPSIS
    Cuts the looping mono rain samples the surface emitters play out of the 5.1 weather beds.

.DESCRIPTION
    Each bed channel is its own decorrelated rain recording, so one 5.1 file yields five different
    mono sources. For every sample this takes a window of one channel, crossfades its tail over its
    head (equal power, so the seam keeps the level) to make it loop seamlessly, and matches every
    sample to the same average level.

    Run it after changing the beds or the cut list; the samples are committed, so a normal build
    does not need ffmpeg.
#>
[CmdletBinding()]
param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\Audio\Assets'),
    # Seconds of the finished loop, and of the crossfade that closes it.
    [double]$Length = 6.0,
    [double]$Crossfade = 0.5,
    # Average level every sample is matched to (dBFS), which is where the beds already sit.
    [double]$TargetMeanDb = -44.0
)

$ErrorActionPreference = 'Stop'
$assets = (Resolve-Path $AssetDirectory).Path

# name, source bed, channel of the 5.1 layout, and where the window starts.
$cuts = @(
    # The bed each set is cut from carries its character: quiet is a light shower, new a steady
    # rain, loud a downpour, canopy the patter on leaves. Levels are matched, so the mod picks a
    # set for the weather and the surface and sets the loudness itself.
    @{ Name = 'rain-light-1';  Source = 'rain-surround-quiet.ogg';  Channel = 'FR'; Start = 1.0 }
    @{ Name = 'rain-light-2';  Source = 'rain-surround-quiet.ogg';  Channel = 'BL'; Start = 9.0 }
    @{ Name = 'rain-light-3';  Source = 'rain-surround-quiet.ogg';  Channel = 'FC'; Start = 5.0 }
    @{ Name = 'rain-medium-1'; Source = 'rain-surround-new.ogg';    Channel = 'FL'; Start = 0.2 }
    @{ Name = 'rain-medium-2'; Source = 'rain-surround-new.ogg';    Channel = 'BR'; Start = 3.6 }
    @{ Name = 'rain-medium-3'; Source = 'rain-surround-new.ogg';    Channel = 'FC'; Start = 1.8 }
    @{ Name = 'rain-heavy-1';  Source = 'rain-surround-loud.ogg';   Channel = 'FL'; Start = 1.5 }
    @{ Name = 'rain-heavy-2';  Source = 'rain-surround-loud.ogg';   Channel = 'BR'; Start = 9.5 }
    @{ Name = 'rain-heavy-3';  Source = 'rain-surround-loud.ogg';   Channel = 'FC'; Start = 5.5 }
    @{ Name = 'rain-canopy-1'; Source = 'rain-surround-canopy.ogg'; Channel = 'FL'; Start = 0.4 }
    @{ Name = 'rain-canopy-2'; Source = 'rain-surround-canopy.ogg'; Channel = 'BR'; Start = 3.2 }
    @{ Name = 'rain-canopy-3'; Source = 'rain-surround-canopy.ogg'; Channel = 'FC'; Start = 6.0 }
)

function Get-MeanVolumeDb([string]$path) {
    $output = & ffmpeg -hide_banner -nostats -i $path -af volumedetect -f null - 2>&1
    $line = $output | Select-String -Pattern 'mean_volume: (-?\d+(\.\d+)?) dB'
    if (-not $line) { throw "no mean_volume for $path" }
    [double]$line.Matches[0].Groups[1].Value
}

$window = $Length + $Crossfade
foreach ($cut in $cuts) {
    $source = Join-Path $assets $cut.Source
    if (-not (Test-Path $source)) { throw "missing bed $source" }
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) "$($cut.Name).wav"
    $out = Join-Path $assets "$($cut.Name).ogg"

    # One channel, `window` long: the last `Crossfade` seconds fade out over the first, which fade
    # in, so the end runs into the start.
    $chain = @(
        "[0:a]pan=mono|c0=$($cut.Channel),atrim=0:$window,asetpts=N/SR/TB[m]"
        "[m]asplit=3[a][b][c]"
        "[a]atrim=0:$Crossfade,asetpts=N/SR/TB,afade=t=in:st=0:d=${Crossfade}:curve=qsin[head]"
        "[b]atrim=${Crossfade}:$Length,asetpts=N/SR/TB[body]"
        "[c]atrim=${Length}:$window,asetpts=N/SR/TB,afade=t=out:st=0:d=${Crossfade}:curve=qsin[tail]"
        "[head][tail]amix=inputs=2:normalize=0[seam]"
        "[seam][body]concat=n=2:v=0:a=1[out]"
    ) -join ';'

    & ffmpeg -hide_banner -v error -y -ss $cut.Start -i $source -filter_complex $chain -map '[out]' -ar 44100 $temp
    $gain = $TargetMeanDb - (Get-MeanVolumeDb $temp)
    & ffmpeg -hide_banner -v error -y -i $temp -af "volume=$([math]::Round($gain, 2))dB" -c:a libvorbis -q:a 4 $out
    Remove-Item $temp -Force
    Write-Host ("{0,-14} {1,-26} {2,-3} {3,5:N1}s  {4,6:N1} dB  {5,6:N0} KB" -f `
        $cut.Name, $cut.Source, $cut.Channel, $cut.Start, $gain, ((Get-Item $out).Length / 1KB))
}
