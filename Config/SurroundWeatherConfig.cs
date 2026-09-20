using Vintagestory.API.Common;

namespace SurroundWeather;

public sealed class SurroundWeatherConfig
{
    /// <summary>Swap vanilla's weather tracks (rain, wind, hail, rumble, distant thunder) for 5.1 surround recordings.</summary>
    public bool ReplaceVanillaWeatherBeds { get; set; } = true;

    /// <summary>Rustling leaves from the trees around you in the wind.</summary>
    public bool EnableLeafRustleEmitters { get; set; } = true;
    public float LeafRustleVolumeMultiplier { get; set; } = 1.75f;
    public float LeafRustlePitchVariationMultiplier { get; set; } = 1.5f;

    /// <summary>Blocks between rustles playing at once: one tree gets about one at a time.</summary>
    public float LeafRustleEmitterSpacing { get; set; } = 5f;

    /// <summary>Seconds a rustle takes to fade out once it falls behind you or out of range.</summary>
    public float LeafRustleFadeOutSeconds { get; set; } = 0.8f;

    /// <summary>Rain landing on the ground around you, from where it lands.</summary>
    public bool EnableRainEmitters { get; set; } = true;

    /// <summary>
    /// Experimental: rain plays from the surfaces it lands on (ground, roofs, canopies) instead of
    /// from a bed at your head. The rain beds fall silent; wind and hail keep theirs. Nothing is
    /// muffled for being indoors: the emitters are simply where the rain is, and the audio engine
    /// muffles what is behind a wall or roof. Takes effect when the world is next loaded.
    /// </summary>
    public bool ExperimentalRainSurfaceEmitters { get; set; } = false;

    /// <summary>Rain emitters around you at full rain (fewer in light rain).</summary>
    public int RainSurfaceEmitterCount { get; set; } = 16;

    /// <summary>Blocks between rain emitters. Lower packs more into a small opening, such as a cave mouth.</summary>
    public float RainSurfaceEmitterSpacing { get; set; } = 3f;

    /// <summary>How far out (blocks) the surface emitters are placed.</summary>
    public float RainSurfaceEmitterRadius { get; set; } = 18f;

    /// <summary>Scales the surface emitters' loudness.</summary>
    public float RainSurfaceEmitterVolume { get; set; } = 1f;

    public bool EnableDebugTools { get; set; } = false;
    public bool ShowLeafRustleDebugVisuals { get; set; } = false;
    public bool ShowRainEmitterDebugVisuals { get; set; } = false;
    public bool ShowRainSurfaceEmitterDebugVisuals { get; set; } = false;
}

internal static class SurroundWeatherConfigManager
{
    internal const string ConfigFileName = "surroundweather.json";

    public static SurroundWeatherConfig Current { get; private set; } = new();

    public static void Load(ICoreAPI api, ILogger logger)
    {
        try
        {
            Current = api.LoadModConfig<SurroundWeatherConfig>(ConfigFileName) ?? new SurroundWeatherConfig();
            api.StoreModConfig(Current, ConfigFileName);
        }
        catch (System.Exception ex)
        {
            logger.Warning("[SurroundWeather] Failed to load config, using defaults: " + ex.Message);
            Current = new SurroundWeatherConfig();
        }
    }
}
