#requires -Version 7.0
[CmdletBinding()]
param([switch]$ProbeDevice)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'tests\SpatialAudio.Tests\SpatialAudio.Tests.csproj'
dotnet run --project $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Managed spatial tests failed.' }
$runtime = & (Join-Path $PSScriptRoot 'Get-SpatialRuntime.ps1')
$results = Join-Path $root 'bin\spatial-tests\patched'
foreach ($direction in @('Above', 'Front')) {
    dotnet run --project $project -c Release --no-build -- --render $runtime.DllPath (Join-Path $results $direction) $direction
    if ($LASTEXITCODE -ne 0) { throw "Native $direction render failed." }
}
$above = Get-Content -Raw -LiteralPath (Join-Path $results 'Above\result.json') | ConvertFrom-Json
$front = Get-Content -Raw -LiteralPath (Join-Path $results 'Front\result.json') | ConvertFrom-Json
$heightFraction = $above.Wave.HeightEnergy / ($above.Wave.HeightEnergy + $above.Wave.BedEnergy)
if ($heightFraction -lt 0.75 -or $above.Wave.HeightEnergy -le $front.Wave.HeightEnergy * 2) {
    throw 'Overhead source did not produce the expected height-channel localization.'
}
Write-Host ('PASS: Native twelve-channel output; {0:P1} of overhead source energy in height channels.' -f $heightFraction)
$weatherFractions = @{}
foreach ($weather in @('WeatherFlat', 'WeatherRain', 'WeatherWind')) {
    dotnet run --project $project -c Release --no-build -- --render $runtime.DllPath (Join-Path $results $weather) $weather
    if ($LASTEXITCODE -ne 0) { throw "Native $weather render failed." }
    $result = Get-Content -Raw -LiteralPath (Join-Path $results "$weather\result.json") | ConvertFrom-Json
    $weatherFractions[$weather] = $result.Wave.HeightEnergy / ($result.Wave.HeightEnergy + $result.Wave.BedEnergy)
    if ($weather -ne 'WeatherFlat' -and @($result.Wave.Rms[8..11] | Where-Object { $_ -le 0.0001 }).Count) {
        throw "$weather did not cover all four height channels."
    }
}
if ($weatherFractions.WeatherRain -le $weatherFractions.WeatherWind -or
    $weatherFractions.WeatherWind -le $weatherFractions.WeatherFlat + 0.15) {
    throw 'Weather beds did not gain the expected elevated distribution.'
}
Write-Host ('PASS: Height energy: flat bed {0:P1}; wind {1:P1}; rain {2:P1}.' -f $weatherFractions.WeatherFlat, $weatherFractions.WeatherWind, $weatherFractions.WeatherRain)
$pitchResults = @{}
foreach ($scenario in @('PitchOff', 'PitchOn', 'WeatherMono')) {
    dotnet run --project $project -c Release --no-build -- --render $runtime.DllPath (Join-Path $results $scenario) $scenario
    if ($LASTEXITCODE -ne 0) { throw "Native $scenario render failed." }
    $pitchResults[$scenario] = Get-Content -Raw -LiteralPath (Join-Path $results "$scenario\result.json") | ConvertFrom-Json
}
$offFraction = $pitchResults.PitchOff.Wave.HeightEnergy / ($pitchResults.PitchOff.Wave.HeightEnergy + $pitchResults.PitchOff.Wave.BedEnergy)
$onFraction = $pitchResults.PitchOn.Wave.HeightEnergy / ($pitchResults.PitchOn.Wave.HeightEnergy + $pitchResults.PitchOn.Wave.BedEnergy)
$monoFraction = $pitchResults.WeatherMono.Wave.HeightEnergy / ($pitchResults.WeatherMono.Wave.HeightEnergy + $pitchResults.WeatherMono.Wave.BedEnergy)
if ($onFraction -lt $offFraction + 0.2 -or [Math]::Abs($pitchResults.PitchOff.ListenerOrientation[1]) -gt 0.001 -or
    $pitchResults.PitchOn.ListenerOrientation[1] -gt -0.7 -or $monoFraction -lt 0.6) {
    throw 'Pitch toggle or mono weather elevation did not reach the expected native output.'
}
Write-Host ('PASS: Front-source height energy: pitch off {0:P1}, looking down with pitch on {1:P1}; mono weather {2:P1}.' -f $offFraction, $onFraction, $monoFraction)
if ($ProbeDevice) {
    dotnet run --project $project -c Release --no-build -- --spatial-probe $runtime.DllPath (Join-Path $results 'Windows') Front
    if ($LASTEXITCODE -ne 0) { throw 'Windows spatial stream activation failed; see the native log.' }
}
