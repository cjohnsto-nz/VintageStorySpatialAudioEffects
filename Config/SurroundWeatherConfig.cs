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

    public bool EnableDebugTools { get; set; } = false;
    public bool ShowLeafRustleDebugVisuals { get; set; } = false;
    public bool ShowRainEmitterDebugVisuals { get; set; } = false;
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
