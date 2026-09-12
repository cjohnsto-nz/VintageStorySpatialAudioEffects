# Spatial audio validation — 2026-09-12

Branch: `codex/spatial-audio`. SDK/game installation: Vintage Story **1.22.7 Stable**, .NET 10, Windows x64.

## Passed

- Release mod build: zero warnings, zero errors.
- 38 managed checks: configuration compatibility, height-preserving context attributes, HRTF behavior, runtime version detection, truthful fallback/activation reporting, context-scoped native log evidence, world-space test positions and bounded test audio.
- Native OpenAL 1.25.2 plus the local ownership patch built with MSVC 19.42 and Windows SDK 10.0.22621. The initial native build emitted one upstream size-conversion warning in `core/voice.cpp`; it did not affect the build result.
- Native front and overhead renders produced twelve-channel float WAV files. Approximately **97.1%** of the overhead source's channel energy reached the four height outputs, versus approximately **28.3%** for the front source. These are renderer measurements, not microphone measurements of the room. The decoder spreads energy across speakers; this is not a claim of isolated-channel output.
- A silent native probe successfully activated and started a **7.1.4 Windows spatial stream** on `AV Receiver (NVIDIA High Definition Audio)`. The activation mask was `0x1ffe`, sample rate 48 kHz, update/buffer sizes 480/960 frames. Those sizes do not measure end-to-end latency.
- Scripted source download/hash verification, patch application and cached native build worked. The sandbox preparation and preflight succeeded.
- The isolated game entered a world and initialized a 7.1.4 spatial stream. Its native log later recorded a callback timeout and `ISpatialAudioObjectRenderStream::Reset failed: 0x88890100`; audio was unavailable during the user's sandbox test. This remains unresolved and is not proof that the sandbox directory caused the failure.
- A subsequent standalone silent probe kept the receiver stream connected for five seconds and queried `ALC_CONNECTED` successfully. The preflight now checks this instead of waiting only 150 ms. This short check does not establish sustained gameplay reliability.
- Installed the full local test into the normal 1.22.7 game, with original mod ZIP, OpenAL DLL and mod settings backed up under `VintagestoryData/SurroundSpatialTest/20260912-185059`. Verified installed hashes, root mod metadata/DLL, forward-slash ZIP entries, and both launcher shortcuts. A five-second probe using the installed DLL also passed; the game was left closed for manual testing.

## Native failure found and corrected

Stock OpenAL Soft 1.25.2 reproduced Windows exception `0xc0000374` during spatial activation. The same crash occurred with upstream `openal-info64.exe`, independently of the managed probe. The 1.24.3 binary also failed. The cause identified in source was a `VT_BLOB` borrowing stack memory that its owning `PROPVARIANT` later frees. The patch allocates and copies with `CoTaskMemAlloc`; the patched build then passed stream activation and startup. See [native patch notes](../native/README.md).

## Evidence locations

- `bin/spatial-tests/patched/Above/result.json` and `render.wav`
- `bin/spatial-tests/patched/Front/result.json` and `render.wav`
- `bin/spatial-tests/patched/Windows/result.json` and `openal.log`
- `bin/spatial-tests/sustained/result.json` and `openal.log` (five-second connected check)
- `bin/openal-build/surround-runtime.json` (source, patch and binary hashes)
- `bin/SpatialSandbox/AudioLogs/` (launcher preflight and game native logs)
- `bin/SpatialSandbox/Data/Logs/client-main.log` (game startup)

## Weather-bed follow-up

- The user reported that the installed spatial test works. Receiver input-format indication was not separately recorded.
- Added rain-bed elevation of approximately 45 degrees and wind-bed elevation of approximately 30 degrees, restricted to the named background loops in spatial mode. Ground rain emitters and other effects are excluded.
- 47 managed checks pass, including asset selection and emitter exclusions. Native six-channel bed renders into 7.1.4 show height-channel energy fractions of 27.9% for the baseline, 56.6% for wind and 71.9% for rain, with signal in all four height outputs. These are software measurements with synthetic independent channel signals; listening with the weather assets remains necessary.
- Inspected the installed 1.22.7 engine: `SystemSoundEngine.OnRenderFrame` passes `viewVector.X, 0f, viewVector.Z` to the audio listener, so camera pitch is intentionally absent from current listener orientation. This change preserves that behavior.

## Pitch and remaining weather beds follow-up

- Added opt-in `FollowCameraPitch`, with an orthonormal listener forward/up basis applied after the engine's flattened listener update. Default remains false; enabled explicitly in the local test configuration. Reports now include the setting and actual OpenAL orientation.
- Included hail, storm tremble/rumble, distant-thunder and additional replacement rain beds. Mono ambient beds also receive broad overhead rendering. Explicit world-positioned effects and ground rain emitters remain excluded. Fixed the hail override target to the engine's actual `sounds/weather/tracks/hail.ogg` path.
- Release build: zero warnings/errors. 66 managed checks passed, including setting-off behavior, the game's pitch convention, front/overhead direction transformations and vertical-view orthogonality.
- Native renders verified front-source height energy changing from 28.3% with pitch off to 72.0% while looking down 45 degrees with pitch on; native orientation readback matched each setting. The mono weather bed produced 96.4% height-channel energy. Existing multichannel weather comparisons still passed. These remain renderer measurements, not room measurements.
- Installed the updated ZIP and enabled pitch in normal game data. Prior prototype/settings were backed up under `SurroundSpatialTest/20260912-185059/updates/20260912-192216`; the original pre-spatial restore backup remains intact. Native runtime unchanged. In-game listening and patch activation remain to be checked by the user.

## Remaining hardware/game checks

Receiver Atmos format indication and perceived overhead direction, in-world debug panel layout and playback, ordinary gameplay effects, world reload, device disconnect/reconnect, latency and sustained-load dropouts still require validation. No other Vintage Story version has been qualified by this run. The mod's existing version metadata is retained because this is feature-branch work, not a published release.
