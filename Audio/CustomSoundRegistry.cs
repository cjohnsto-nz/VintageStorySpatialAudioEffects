using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace SurroundWeather;

internal static class CustomSoundRegistry
{
    public static readonly AssetLocation LeafRustleOneAlias = new("surroundweather:sounds/foliage/leaves-mono-1.ogg");
    public static readonly AssetLocation LeafRustleTwoAlias = new("surroundweather:sounds/foliage/leaves-mono-2.ogg");
    public static readonly AssetLocation LeafRustleThreeAlias = new("surroundweather:sounds/foliage/leaves-mono-3.ogg");
    public static readonly AssetLocation LeafRustleFourAlias = new("surroundweather:sounds/foliage/leaves-mono-4.ogg");
    public static readonly AssetLocation RainOneAlias = new("surroundweather:sounds/weather/rain-mono-1.ogg");
    public static readonly AssetLocation RainTwoAlias = new("surroundweather:sounds/weather/rain-mono-2.ogg");
    public static readonly AssetLocation RainThreeAlias = new("surroundweather:sounds/weather/rain-mono-3.ogg");
    public static readonly AssetLocation RainFourAlias = new("surroundweather:sounds/weather/rain-mono-4.ogg");

    private static readonly AssetLocation[] Aliases =
    {
        LeafRustleOneAlias,
        LeafRustleTwoAlias,
        LeafRustleThreeAlias,
        LeafRustleFourAlias,
        RainOneAlias,
        RainTwoAlias,
        RainThreeAlias,
        RainFourAlias
    };

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
}
