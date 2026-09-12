#requires -Version 7.0
[CmdletBinding()]
param([string]$BuildRoot = (Join-Path $PSScriptRoot '..\bin'))
$ErrorActionPreference = 'Stop'
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$sourceRoot = Join-Path $BuildRoot 'openal-source'
$source = Join-Path $sourceRoot 'openal-soft-1.25.2'
$build = Join-Path $BuildRoot 'openal-build'
$archive = Join-Path $sourceRoot '1.25.2.zip'
$archiveHash = '47e22c066dffa2f65a1747272978344db845dcef9c7ee118449ec560a44adc8f'
$patch = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\native\openal-soft-spatial-ownership.patch'))
$patchHash = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash
$dll = Join-Path $build 'Release\OpenAL32.dll'
$manifestPath = Join-Path $build 'surround-runtime.json'
if ((Test-Path -LiteralPath $manifestPath) -and (Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $source)) {
    $cached = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($cached.PatchSha256 -eq $patchHash -and $cached.ArchiveSha256 -eq $archiveHash -and
        $cached.DllSha256 -eq (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) {
        return $cached
    }
}
Get-Command cmake,git -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/kcat/openal-soft/archive/refs/tags/1.25.2.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
    throw "OpenAL source checksum mismatch: $archive. Remove this archive and retry."
}
Expand-Archive -LiteralPath $archive -DestinationPath $sourceRoot -Force
git -C $source apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'Native patch no longer applies to the pinned source.' }
git -C $source apply $patch
if ($LASTEXITCODE -ne 0) { throw 'Could not apply native patch.' }
cmake -S $source -B $build -G 'Visual Studio 17 2022' -A x64 `
    -DALSOFT_EXAMPLES=OFF -DALSOFT_UTILS=OFF -DALSOFT_TESTS=OFF `
    -DALSOFT_BACKEND_WASAPI=ON -DALSOFT_BACKEND_WAVE=ON `
    -DALSOFT_UPDATE_BUILD_VERSION=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Native configure failed. Install VS 2022 C++ Build Tools and a Windows SDK.' }
cmake --build $build --config Release --parallel 4 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Native runtime build failed.' }
$manifest = [pscustomobject]@{
    Version = '1.25.2+surround-spatial1'
    DllPath = $dll
    DllSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    ArchiveSha256 = $archiveHash
    PatchSha256 = $patchHash
    SourcePath = $source
    SourceUrl = 'https://github.com/kcat/openal-soft/tree/1.25.2'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
$manifest
