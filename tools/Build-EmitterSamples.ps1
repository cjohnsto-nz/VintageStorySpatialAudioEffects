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
    [double]$TargetMeanDb = -44.0,
    # The game's assets, for the rain-on-glass recording the window emitters play.
    [string]$GameAssetDirectory = (Join-Path ($env:VINTAGE_STORY ?? (Join-Path $env:APPDATA 'Vintagestory')) 'assets')
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

    # Wind: longer slices, so a gust keeps its shape, and at the beds' own (louder) level.
    @{ Name = 'wind-leafy-1';    Source = 'wind-surround-leafy2.ogg';    Channel = 'FL'; Start = 0.5;  Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafy-2';    Source = 'wind-surround-leafy2.ogg';    Channel = 'BR'; Start = 10.5; Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafy-3';    Source = 'wind-surround-leafy2.ogg';    Channel = 'FR'; Start = 5.5;  Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafy-4';    Source = 'wind-surround-leafy2.ogg';    Channel = 'BL'; Start = 3.0;  Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafless-1'; Source = 'wind-surround-leafless2.ogg'; Channel = 'FL'; Start = 0.5;  Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafless-2'; Source = 'wind-surround-leafless2.ogg'; Channel = 'BR'; Start = 5.5;  Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafless-3'; Source = 'wind-surround-leafless2.ogg'; Channel = 'FR'; Start = 3.0;  Length = 10.0; Target = -24.0 }
    @{ Name = 'wind-leafless-4'; Source = 'wind-surround-leafless2.ogg'; Channel = 'BL'; Start = 1.5;  Length = 10.0; Target = -24.0 }
)

function Get-MeanVolumeDb([string]$path) {
    $output = & ffmpeg -hide_banner -nostats -i $path -af volumedetect -f null - 2>&1
    $line = $output | Select-String -Pattern 'mean_volume: (-?\d+(\.\d+)?) dB'
    if (-not $line) { throw "no mean_volume for $path" }
    [double]$line.Matches[0].Groups[1].Value
}

$defaultLength = $Length
foreach ($cut in $cuts) {
    $Length = $cut.Length ?? $defaultLength
    $target = $cut.Target ?? $TargetMeanDb
    $window = $Length + $Crossfade
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
    $gain = $target - (Get-MeanVolumeDb $temp)
    & ffmpeg -hide_banner -v error -y -i $temp -af "volume=$([math]::Round($gain, 2))dB" -c:a libvorbis -q:a 4 $out
    Remove-Item $temp -Force
    Write-Host ("{0,-14} {1,-26} {2,-3} {3,5:N1}s  {4,6:N1} dB  {5,6:N0} KB" -f `
        $cut.Name, $cut.Source, $cut.Channel, $cut.Start, $gain, ((Get-Item $out).Length / 1KB))
}

# Rain on glass, for the window emitters: vanilla's own recording, high-passed.
#
# It is mostly rumble - the energy below 80 Hz is within 1.5 dB of the whole file - which a pane of
# glass does not have, and which the audio engine's muffling through a wall leaves behind when it
# takes the rest away. Two 2-pole stages at 200 Hz (24 dB/octave) take it out. Nothing is put back:
# the samples come out about 7 dB quieter than the original, which is the rumble that left, and the
# mod's own RainWindowVolume sets the loudness from there.
$windowSource = Join-Path $GameAssetDirectory 'survival/sounds/environment/rainwindow.ogg'
$windowCuts = @(
    @{ Name = 'rainwindow-1'; Start = 0.5 }
    @{ Name = 'rainwindow-2'; Start = 6.5 }
    @{ Name = 'rainwindow-3'; Start = 12.5 }
)

if (-not (Test-Path $windowSource)) {
    Write-Warning "No rain-on-glass recording at $windowSource; the window samples are left as they are."
    return
}

$Length = $defaultLength
$window = $Length + $Crossfade
foreach ($cut in $windowCuts) {
    $out = Join-Path $assets "$($cut.Name).ogg"
    $chain = @(
        "[0:a]atrim=0:$window,asetpts=N/SR/TB,highpass=f=200:poles=2,highpass=f=200:poles=2[m]"
        "[m]asplit=3[a][b][c]"
        "[a]atrim=0:$Crossfade,asetpts=N/SR/TB,afade=t=in:st=0:d=${Crossfade}:curve=qsin[head]"
        "[b]atrim=${Crossfade}:$Length,asetpts=N/SR/TB[body]"
        "[c]atrim=${Length}:$window,asetpts=N/SR/TB,afade=t=out:st=0:d=${Crossfade}:curve=qsin[tail]"
        "[head][tail]amix=inputs=2:normalize=0[seam]"
        "[seam][body]concat=n=2:v=0:a=1[out]"
    ) -join ';'

    & ffmpeg -hide_banner -v error -y -ss $cut.Start -i $windowSource -filter_complex $chain -map '[out]' -ar 44100 -c:a libvorbis -q:a 4 $out
    Write-Host ("{0,-14} {1,-26} {2,-3} {3,5:N1}s  {4,6:N1} dB  {5,6:N0} KB" -f `
        $cut.Name, 'rainwindow.ogg', 'hp', $cut.Start, (Get-MeanVolumeDb $out), ((Get-Item $out).Length / 1KB))
}
