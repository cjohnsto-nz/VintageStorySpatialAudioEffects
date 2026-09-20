using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace SpatialAudioEffects;

internal static class CustomSoundRegistry
{
    public static readonly AssetLocation LeafRustleOneAlias = new("spatialaudioeffects:sounds/foliage/leaves-mono-1.ogg");
    public static readonly AssetLocation LeafRustleTwoAlias = new("spatialaudioeffects:sounds/foliage/leaves-mono-2.ogg");
    public static readonly AssetLocation LeafRustleThreeAlias = new("spatialaudioeffects:sounds/foliage/leaves-mono-3.ogg");
    public static readonly AssetLocation LeafRustleFourAlias = new("spatialaudioeffects:sounds/foliage/leaves-mono-4.ogg");
    public static readonly AssetLocation RainOneAlias = new("spatialaudioeffects:sounds/weather/rain-mono-1.ogg");
    public static readonly AssetLocation RainTwoAlias = new("spatialaudioeffects:sounds/weather/rain-mono-2.ogg");
    public static readonly AssetLocation RainThreeAlias = new("spatialaudioeffects:sounds/weather/rain-mono-3.ogg");
    public static readonly AssetLocation RainFourAlias = new("spatialaudioeffects:sounds/weather/rain-mono-4.ogg");

    /// <summary>
    /// The surface emitters' loops, cut from the 5.1 beds by tools/Build-EmitterSamples.ps1: one
    /// set per character of rain, all at the same level, so the weather and the surface pick a set
    /// and the mod sets the loudness.
    /// </summary>
    public static readonly AssetLocation[] RainLightLoops = Loops("rain-light-1", "rain-light-2", "rain-light-3");

    public static readonly AssetLocation[] RainMediumLoops = Loops("rain-medium-1", "rain-medium-2", "rain-medium-3");

    public static readonly AssetLocation[] RainHeavyLoops = Loops("rain-heavy-1", "rain-heavy-2", "rain-heavy-3");

    /// <summary>
    /// Rain on glass: slices of vanilla's own rainwindow recording with the sub bass filtered out
    /// (a pane has none, and through a wall the engine's muffling leaves only the rumble).
    /// </summary>
    public static readonly AssetLocation[] WindowLoops = Loops("rainwindow-1", "rainwindow-2", "rainwindow-3");

    /// <summary>Rain in the leaves, for emitters that land on a canopy.</summary>
    public static readonly AssetLocation[] RainCanopyLoops = Loops("rain-canopy-1", "rain-canopy-2", "rain-canopy-3");

    /// <summary>Wind through leaves, and through bare country: slices of the wind beds' channels.</summary>
    public static readonly AssetLocation[] WindLeafySlices = Loops("wind-leafy-1", "wind-leafy-2", "wind-leafy-3", "wind-leafy-4");

    public static readonly AssetLocation[] WindLeaflessSlices = Loops("wind-leafless-1", "wind-leafless-2", "wind-leafless-3", "wind-leafless-4");

    private static readonly AssetLocation[] Aliases =
        new[]
        {
            LeafRustleOneAlias,
            LeafRustleTwoAlias,
            LeafRustleThreeAlias,
            LeafRustleFourAlias,
            RainOneAlias,
            RainTwoAlias,
            RainThreeAlias,
            RainFourAlias,
        }
        .Concat(RainLightLoops)
        .Concat(RainMediumLoops)
        .Concat(RainHeavyLoops)
        .Concat(RainCanopyLoops)
        .Concat(WindowLoops)
        .Concat(WindLeafySlices)
        .Concat(WindLeaflessSlices)
        .ToArray();

    public static void Register(ICoreClientAPI api, ILogger logger)
    {
        foreach (AssetLocation alias in Aliases)
        {
            IAsset asset = api.Assets.TryGet(alias);
            if (asset?.Data == null)
            {
                logger.Warning("Could not find custom sound asset {0}.", alias);
                continue;
            }

            ScreenManager.soundAudioData[alias] = ScreenManager.LoadSound(asset);
        }
    }

    private static AssetLocation[] Loops(params string[] names) =>
        names.Select(name => new AssetLocation($"spatialaudioeffects:sounds/weather/{name}.ogg")).ToArray();
}
