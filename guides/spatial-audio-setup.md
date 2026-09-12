# Experimental spatial audio setup

Maintainers: see the [release plan and acceptance gates](spatial-audio-release-plan.md). The scripts below currently support developer/local testing; a prebuilt public add-on is still planned.

This feature branch adds a 7.1.4 Windows Spatial Audio output path for an Atmos receiver or soundbar with height speakers. It preserves positional mono sounds and existing OpenAL effects. It sends a mixed channel bed, not one dynamic Atmos object per game sound.

## Start the isolated game

In PowerShell 7, from the repository:

```powershell
.\tools\Start-SpatialAudio.ps1
```

The first run requires .NET 10 SDK, CMake, Git, Visual Studio 2022 C++ Build Tools and a Windows SDK. It builds the mod and patched OpenAL runtime. Later runs reuse the native build after checking its hash.

The launcher creates `bin/SpatialSandbox`, copies game binaries and built-in mods, shares the installed read-only game assets via a junction, and creates a separate `Data` folder for settings, mods and worlds. Your normal game and data folders are not modified. Use `-GamePath 'C:\path\to\Vintagestory'` to select a different compatible game installation, and use a separate `-Destination` for each game version.

Use `-PrepareOnly` to build and prepare without starting the game. Use `-Conventional` to start the sandbox with ordinary automatic speaker output for comparison. Close the sandbox game before rerunning the launcher. Do not run the copied EXE directly: the launcher supplies the process-specific configuration and logging before audio initialization.

Before starting, select **Dolby Atmos for home theater** on the intended Windows HDMI output, and make that endpoint the Windows default playback device. The launcher uses a silent standalone preflight to verify that a twelve-channel Windows spatial stream can start. A crash, timeout, fallback or missing activation evidence prevents launching the game; its log is retained under `bin/SpatialSandbox/AudioLogs`. This preflight does not change Windows audio settings and does not prove which format the receiver is decoding.

The isolated data folder starts fresh; sign in if the game requests it and create a disposable test world. The launcher enables the mod's debug tools and selects `WindowsSpatialAudio` (saved enum value 8). Use the same default output device in the game's audio settings. If you select a different in-game device, inspect a fresh in-game report for that endpoint rather than relying on the launcher's default-device preflight.

## Install into the normal game for local testing

This requires more than a normal mod ZIP: the installer replaces `Lib/OpenAL32.dll` in the selected game with the patched runtime. Close that game first, then run:

```powershell
.\tools\Install-LocalSpatialAudio.ps1
```

It builds and installs the mod, preserves the other mod settings while selecting spatial output and enabling debug tools, and backs up the previous mod ZIP, native DLL and mod configuration under `VintagestoryData/SurroundSpatialTest/<timestamp>`. Other mods and worlds are retained. The ZIP keeps the current 1.2.3 metadata for local feature testing.

Use the desktop **Vintage Story - Spatial Audio Test** shortcut. It runs the normal installed game with the normal data directory; its process receives the necessary spatial startup settings and a timestamped native log. The **Vintage Story - Standard Audio** shortcut selects automatic conventional output for comparison, using the same patched runtime. The ordinary shortcut does not supply these explicit spatial startup settings.

To restore the original mod, runtime and settings, close the game and run `pwsh -File '<backup folder>/Restore-LocalSpatialAudio.ps1'`. Restoration saves the latest mod configuration before reverting it and refuses to overwrite a mod or DLL that changed after installation. Use the ordinary game shortcut after restoration. Game updates can replace the native DLL; this is an experimental local setup, not a self-contained mod release.

The sandbox experienced a stream disconnect after successful initialization. A later standalone five-second receiver probe stayed connected; local gameplay and audible height output still need confirmation.

## Listening checks

Open **F9 → Spatial Audio** in the world. Tests emit quiet, broadband mono bursts for eight seconds, anchored to the starting world position. Starting another spatial test or closing the spatial panel stops the current test.

- Compare Front, Rear, Above and Below while looking around.
- Try the four elevated corner positions and the Below to above sweep.
- Record what you heard with the observation buttons. These observations are linked to the test in the session log.
- Check the receiver's input-format display separately. An Atmos indicator alone does not confirm correct height direction; a source test is not an isolated speaker-channel test.
- Test roof rain, canopy foliage, entities above/below the player, occlusion and reverberation. Compare music/UI and weather beds as well.

7.1.4 has no speakers below the listener. The Below test checks negative-elevation routing; it does not promise a dedicated floor channel.

The stereo upmix stays in the horizontal 7.1 bed when the spatial layout is requested; mono world sources provide actual height information. It does not copy music into ceiling channels.

In spatial mode, the rain, hail, storm tremble/rumble and distant-thunder beds are elevated; multichannel beds target approximately 45 degrees, while wind targets approximately 30 degrees. Mono weather beds use a broad overhead source. Multichannel spatialization and source radius retain a broad surround distribution; the bed follows the listener's position with a world-up offset. Rain impact emitters, foliage emitters, nearby lightning effects and explicit world-positioned uses of these assets are excluded. Conventional output keeps the existing bed routing. Changes take effect when sounds are recreated, normally on world load. The hail replacement now targets the actual `sounds/weather/tracks/hail.ogg` asset. LFE content remains handled as bass rather than a directional height channel.

Vintage Story 1.22.7 passes only the horizontal view vector to its audio listener (`SystemSoundEngine.OnRenderFrame` explicitly supplies zero for Y). **Output → Follow camera pitch** (`FollowCameraPitch`, default false) optionally applies the full player-view pitch after that update, with a perpendicular listener-up vector. Enabled: looking down moves a fixed front source upward relative to the listener, and a fixed overhead source rearward. Disabled: the original yaw-only listener remains in control. This follows the player-view direction used by the game's sound engine; it does not implement camera roll or a detached third-person/free-camera listener. It works with ordinary OpenAL output as well, though audible elevation depends on the renderer and speakers/headphones.

Save through ConfigLib to apply the setting; the existing config reload rebuilds the audio context. The local pitch-test installation enables it explicitly while the default remains off. F9 → Spatial Audio shows its state, and Write report captures `FollowCameraPitch` plus the six current OpenAL `ListenerOrientation` values (forward XYZ, up XYZ) for runtime verification.

The status panel distinguishes missing setup, restart required, horizontal fallback, unverified configuration, and a spatial stream started at initialization. `Any/Auto` in the OpenAL output query can include 7.1.4. `StreamActive` is based on the patched native log for the current game-context initialization; it does not certify receiver format or continued device availability after a disconnection. Write report includes loaded DLL path/version, process config/log paths and the spatial status.

The existing F9 summary panel also exposes a Spatial Audio button. Full paths and extended diagnostics are written to reports rather than squeezed into the spatial panel.

## Automated verification

```powershell
.\tools\Test-SpatialAudio.ps1
.\tools\Test-SpatialAudio.ps1 -ProbeDevice
```

The first command checks context attributes, fallback reporting, source coordinates and signal bounds, then renders front/overhead sources through the actual patched native library to twelve-channel WAV files. It asserts strong overhead-channel localization. The second additionally attempts a silent spatial stream on the default Windows endpoint.

Results and native logs are under `bin/spatial-tests/patched`. Hardware tests remain necessary for receiver Atmos indication, perceived direction, latency, dropout handling and actual gameplay.

## Why a patched native library is required

The game's bundled OpenAL Soft 1.23.0 predates the Windows spatial backend. During implementation, the upstream 1.25.2 spatial path also reproduced a native ownership bug on this machine. The feature therefore builds a pinned 1.25.2 source revision with a small allocation/cleanup correction and an activation diagnostic. See [native/README.md](../native/README.md) for the root cause, source patch and rebuild details.

Installing only the mod DLL/ZIP is insufficient for this prototype. The experimental launcher is the supported setup path on this branch. No mod release, native binary publication or upstream bug submission is included.
