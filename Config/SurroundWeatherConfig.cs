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

    /// <summary>Blocks between rustles near you (each ring further out doubles it).</summary>
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

    /// <summary>The most rain emitters at once; the grid's nearest cells win.</summary>
    public int RainSurfaceEmitterCount { get; set; } = 48;

    /// <summary>Blocks between rain emitters near you (one every this many blocks out to three times it; each ring beyond doubles it).</summary>
    public float RainSurfaceEmitterSpacing { get; set; } = 3f;

    /// <summary>Seconds an emitter plays before its place moves on, so rain never sits in one spot.</summary>
    public float RainSurfaceEmitterLifetimeSeconds { get; set; } = 3f;

    /// <summary>How far out (blocks) the surface emitters are placed.</summary>
    public float RainSurfaceEmitterRadius { get; set; } = 18f;

    /// <summary>Scales the surface emitters' loudness.</summary>
    public float RainSurfaceEmitterVolume { get; set; } = 1f;

    /// <summary>
    /// Experimental: wind plays from the open ground around you, loudest from upwind, instead of
    /// from a bed at your head. In a cave it comes from the mouth. The wind beds fall silent.
    /// Takes effect when the world is next loaded.
    /// </summary>
    public bool ExperimentalWindEmitters { get; set; } = false;

    /// <summary>Scales the wind emitters' loudness.</summary>
    public float WindEmitterVolume { get; set; } = 1f;

    /// <summary>
    /// Experimental: each window near you gets its own quiet emitter, just outside the pane where
    /// the rain lands, instead of vanilla's one sound for every pane at once (which indoors plays
    /// from your own head). Takes hold within a few seconds, without reloading the world.
    /// </summary>
    public bool ExperimentalWindowRainEmitters { get; set; } = false;

    /// <summary>How loud one window is in full rain.</summary>
    public float RainWindowVolume { get; set; } = 0.4f;

    /// <summary>How far out (blocks) windows are given emitters; every pane within it gets one.</summary>
    public float RainWindowRadius { get; set; } = 10f;

    /// <summary>The most window emitters at once; the nearest panes win.</summary>
    public int RainWindowMaxCount { get; set; } = 24;

    /// <summary>
    /// Experimental: water sounds from the water, an emitter at a time, instead of vanilla's one
    /// sound for a whole lake played from whichever bit of it you stand nearest.
    /// </summary>
    public bool ExperimentalWaterWaveEmitters { get; set; } = true;

    /// <summary>How loud the water lapping at a shore is; open water is quieter than this.</summary>
    public float WaterWaveVolume { get; set; } = 0.75f;

    /// <summary>How far out (blocks) the water is given emitters.</summary>
    public float WaterWaveRadius { get; set; } = 24f;

    /// <summary>The most water emitters at once; the nearest water wins.</summary>
    public int WaterWaveCount { get; set; } = 8;

    /// <summary>Blocks between water emitters around the listener; they thin out further off.</summary>
    public float WaterWaveSpacing { get; set; } = 8f;

    /// <summary>
    /// Experimental: a creek, rapids and a fall sound from the water that makes them, instead of
    /// one sound for a whole river played from whichever bend you stand nearest.
    /// </summary>
    public bool ExperimentalFlowingWaterEmitters { get; set; } = true;

    /// <summary>How loud a fall is; rapids and a creek are quieter than this.</summary>
    public float FlowingWaterVolume { get; set; } = 0.85f;

    /// <summary>How far out (blocks) running water is given emitters.</summary>
    public float FlowingWaterRadius { get; set; } = 32f;

    /// <summary>The most running-water emitters at once; the nearest water wins.</summary>
    public int FlowingWaterCount { get; set; } = 10;

    /// <summary>Blocks between them: a creek is a line a block or two wide, so they sit close.</summary>
    public float FlowingWaterSpacing { get; set; } = 3f;

    /// <summary>
    /// Play a block's ambient sound (a beehive, a translocator) from the nearest block that makes it, rather
    /// than from vanilla's bounding box, which puts it on your head indoors.
    /// </summary>
    public bool PlaceAmbientSoundsOnTheirBlocks { get; set; } = true;

    public bool EnableDebugTools { get; set; } = false;
    public bool ShowLeafRustleDebugVisuals { get; set; } = false;
    public bool ShowRainEmitterDebugVisuals { get; set; } = false;
    public bool ShowRainSurfaceEmitterDebugVisuals { get; set; } = false;
    public bool ShowWindEmitterDebugVisuals { get; set; }

    /// <summary>Draw where the water emitters are.</summary>
    public bool ShowWaterWaveDebugVisuals { get; set; }

    /// <summary>Draw where the running-water emitters are.</summary>
    public bool ShowFlowingWaterDebugVisuals { get; set; } = false;
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
