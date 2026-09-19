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
