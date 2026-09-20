# Spatial Audio Effects

A Vintage Story client mod that makes ambient sound come from where it is made.

Vanilla plays weather as a bed locked to your head, and gives every block of a kind within a
32-block section a *single* looping sound, played from the point of a merged bounding box nearest
you. A lake is then one point a stride away wherever you stand; a wall of windows is one window that
follows you around the room; a river slides along beside you as you walk its bank.

This mod replaces those with **emitter fields**: a grid of short, positioned sounds on the surfaces
and blocks that actually make them, so a spatial audio engine has real positions to work with. It
works with vanilla audio, and is built to sit under the
[Spatial Audio mod](https://github.com/cjohnsto-nz/VintageStorySpatialAudio), which muffles and reflects
what the fields place.

## What it does

| Field | What it places |
|---|---|
| Rain on surfaces | The rain-blocking top of each column: the ground outside, the roof over a building, the canopy over a clearing. |
| Rain splashes | Sparse one-shots further out, over whatever else is playing. |
| Rain on windows | One emitter per pane, outside the glass, where the rain lands. |
| Wind | Slices of the wind beds, placed around you rather than at your ears. |
| Leaf rustles | The canopy near you, thinned so one tree is not a chorus. |
| Water | Still water: the surface, louder where it meets land. |
| Creeks and falls | Running water: its surface, and the open side of a falling block. |

Each field is a deterministic, world-anchored grid, so coverage is even and does not shimmer as you
move. The rings lead your velocity, so running does not outrun the sound. Nothing loops: a cell
plays one slice of a recording and the next crossfades in at the same spot, with equal-power fades
run per frame (the game's own fades are linear in dB, and crossfading with them dips).

Sounds that are not copies of one another add as powers, so a field can say how many emitters its
volumes are written for (`LoudnessReference`); past that, each gives way by the root of how many
more there are, and the field as a whole holds its level.

Where a field takes over a vanilla sound, that sound is stripped from the ambient scan while the
field runs, and handed straight back when you turn the field off.

## Settings

Every field can be turned off, and its volume, radius, spacing and emitter count set, in
`ModConfig/spatialaudioeffects.json`. The fields read the settings object every step, so with
ConfigLib installed the dialog's changes take hold as you make them; editing the file by hand takes
effect when the game next starts.

In game, `.spatialaudio status` (alias `.sae`) reports what every field is doing, and
`.spatialaudio windows` lists the panes nearby with why each is sounding or silent.

## Build and deploy

Windows, PowerShell 7:

```powershell
dotnet build VintageStorySpatialAudioEffects.csproj
pwsh ./deploy.ps1        # builds, packages into %APPDATA%\VintagestoryData\Mods, relaunches the game
```

The emitter samples are committed, so a normal build needs no ffmpeg.
`tools/Build-EmitterSamples.ps1` cuts them again from the beds and from the game's own recordings
(it needs ffmpeg on PATH).

## History

This began as a branch of [Surround Sound](https://github.com/cjohnsto-nz/VintageStorySurroundSound)
and keeps its history. The spatialiser of that mod is gone: the audio engine does that work now, and
what remains here is where the sound is placed.
