using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace SurroundWeather;

/// <summary>
/// Swaps the audio behind vanilla's weather tracks: the 5.1 surround recordings, and silence for
/// the rain beds when the experimental surface emitters take rain over.
/// </summary>
/// <remarks>
/// The swap is on the game's sound table, so vanilla keeps driving its own sounds (volume with the
/// rainfall, starts and stops) and simply plays other audio. A sound the game has already created
/// holds the audio it was created with, so a change takes effect when the world is next loaded.
/// </remarks>
internal static class WeatherBedOverrides
{
    private static readonly AssetLocation Silence = new("surroundweather:sounds/weather/silence.ogg");

    /// <summary>Vanilla's rain beds, which the surface emitters replace.</summary>
    private static readonly AssetLocation[] RainBeds =
    {
        new("game:sounds/weather/tracks/rain-leafless.ogg"),
        new("game:sounds/weather/tracks/rain-leafy.ogg"),
        new("game:sounds/weather/tracks/verylowtremble.ogg")
    };

    /// <summary>Vanilla's wind beds, which the wind emitters replace.</summary>
    private static readonly AssetLocation[] WindBeds =
    {
        new("game:sounds/weather/wind-leafless.ogg"),
        new("game:sounds/weather/wind-leafy.ogg")
    };

    private static readonly (AssetLocation Target, AssetLocation Replacement)[] SurroundReplacements =
    {
        (new AssetLocation("game:sounds/weather/tracks/rain-leafless.ogg"), new AssetLocation("surroundweather:sounds/weather/tracks/rain-surround-new.ogg")),
        (new AssetLocation("game:sounds/weather/tracks/rain-leafy.ogg"), new AssetLocation("surroundweather:sounds/weather/tracks/rain-surround-quiet.ogg")),
        (new AssetLocation("game:sounds/weather/wind-leafless.ogg"), new AssetLocation("surroundweather:sounds/weather/wind-surround-leafy2.ogg")),
        (new AssetLocation("game:sounds/weather/wind-leafy.ogg"), new AssetLocation("surroundweather:sounds/weather/wind-surround-leafless2.ogg")),
        (new AssetLocation("game:sounds/weather/lowgrumble.ogg"), new AssetLocation("surroundweather:sounds/weather/rumble-low.ogg")),
        (new AssetLocation("game:sounds/weather/lightning-distant.ogg"), new AssetLocation("surroundweather:sounds/weather/lightning-distant.ogg")),
        (new AssetLocation("game:sounds/weather/tracks/hail.ogg"), new AssetLocation("surroundweather:sounds/weather/hail.wav"))
    };

    private static readonly Dictionary<AssetLocation, AudioData> OriginalAudioDataByTarget = new();
    private static readonly List<AssetLocation> AppliedTargets = new();
    private static bool appliedSurround;
    private static bool appliedSilence;
    private static bool appliedWindSilence;
    private static bool applied;

    /// <param name="surroundBeds">Play the 5.1 recordings instead of vanilla's weather tracks.</param>
    /// <param name="silenceRainBeds">Silence the rain beds: the surface emitters are the rain now.</param>
    /// <param name="silenceWindBeds">Silence the wind beds: the wind emitters are the wind now.</param>
    public static void Apply(ICoreClientAPI api, ILogger logger, bool surroundBeds, bool silenceRainBeds, bool silenceWindBeds)
    {
        if (applied && appliedSurround == surroundBeds && appliedSilence == silenceRainBeds && appliedWindSilence == silenceWindBeds)
        {
            return;
        }

        Restore(logger);
        if (!surroundBeds && !silenceRainBeds && !silenceWindBeds)
        {
            return;
        }

        if (surroundBeds)
        {
            foreach (var (target, replacement) in SurroundReplacements)
            {
                TryRegister(api, logger, target, replacement);
            }
        }

        if (silenceRainBeds)
        {
            foreach (AssetLocation target in RainBeds)
            {
                TryRegister(api, logger, target, Silence);
            }
        }

        if (silenceWindBeds)
        {
            foreach (AssetLocation target in WindBeds)
            {
                TryRegister(api, logger, target, Silence);
            }
        }

        appliedWindSilence = silenceWindBeds;
        appliedSurround = surroundBeds;
        appliedSilence = silenceRainBeds;
        applied = true;
    }

    public static void Restore(ILogger logger)
    {
        if (!applied)
        {
            return;
        }

        foreach (AssetLocation target in AppliedTargets)
        {
            if (OriginalAudioDataByTarget.TryGetValue(target, out AudioData original))
            {
                ScreenManager.soundAudioData[target] = original;
            }
            else
            {
                ScreenManager.soundAudioData.Remove(target);
            }
        }

        AppliedTargets.Clear();
        applied = false;
        appliedSurround = false;
        appliedSilence = false;
        logger.Notification("Restored vanilla weather audio beds.");
    }

    private static void TryRegister(ICoreClientAPI api, ILogger logger, AssetLocation targetLocation, AssetLocation replacementLocation)
    {
        if (!OriginalAudioDataByTarget.ContainsKey(targetLocation) && ScreenManager.soundAudioData.TryGetValue(targetLocation, out AudioData existing))
        {
            OriginalAudioDataByTarget[targetLocation] = existing;
        }

        IAsset asset = api.Assets.TryGet(replacementLocation);
        if (asset?.Data == null)
        {
            logger.Warning("Could not find surround replacement asset {0} for target {1}.", replacementLocation, targetLocation);
            return;
        }

        ScreenManager.soundAudioData[targetLocation] = ScreenManager.LoadSound(asset);
        if (!AppliedTargets.Contains(targetLocation))
        {
            AppliedTargets.Add(targetLocation);
        }

        logger.Notification("Registered weather bed {0} -> {1}.", targetLocation, replacementLocation);
    }
}
