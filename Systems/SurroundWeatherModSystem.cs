using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace SurroundWeather;

/// <summary>
/// Surround weather: vanilla's weather tracks replaced by 5.1 recordings, plus rain and leaf
/// emitters placed around the player.
/// <para>
/// Works with vanilla OpenAL (the beds go straight to the speakers) and with the Steam Audio mod,
/// which plays 5.1 beds from their speakers itself. It patches only vanilla's OpenAL classes
/// (inert while Steam Audio has the audio), never the calls Steam Audio takes over.
/// </para>
/// </summary>
public sealed class SurroundWeatherModSystem : ModSystem
{
    private const string ConfigLibConfigSavedEvent = "configlib:surroundweather:config-saved";
    private const string ConfigLibConfigReloadEvent = "configlib:config-reload";

    private ICoreAPI api;
    private ICoreClientAPI clientApi;
    private Harmony harmony;
    private LeafRustleEmitterSystem leafRustleEmitterSystem;
    private LeafRustleDebugRenderer leafRustleDebugRenderer;
    private RainEmitterSystem rainEmitterSystem;
    private RainEmitterDebugRenderer rainEmitterDebugRenderer;
    private RainSurfaceEmitterSystem rainSurfaceEmitterSystem;
    private RainSurfaceEmitterDebugRenderer rainSurfaceEmitterDebugRenderer;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        this.api = api;
        SurroundWeatherConfigManager.Load(api, Mod.Logger);
        api.Event.RegisterEventBusListener(OnConfigLibEvent, filterByEventName: ConfigLibConfigSavedEvent);
        api.Event.RegisterEventBusListener(OnConfigLibEvent, filterByEventName: ConfigLibConfigReloadEvent);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        clientApi = api;
        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll(typeof(SurroundWeatherModSystem).Assembly);
        CustomSoundRegistry.Register(api, Mod.Logger);
        ApplyRuntimeConfig();
    }

    public override void Dispose()
    {
        DisposeLeafRustleRuntime();
        DisposeRainRuntime();
        WeatherBedOverrides.Restore(Mod.Logger);
        harmony?.UnpatchAll(harmony.Id);
        clientApi = null;
        base.Dispose();
    }

    private void OnConfigLibEvent(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if ((data as ITreeAttribute)?.GetAsString("domain") != Mod.Info.ModID || api == null)
        {
            return;
        }

        SurroundWeatherConfigManager.Load(api, Mod.Logger);
        if (clientApi != null)
        {
            ApplyRuntimeConfig();
        }
    }

    private void ApplyRuntimeConfig()
    {
        SurroundWeatherConfig config = SurroundWeatherConfigManager.Current;
        WeatherBedOverrides.Apply(clientApi, Mod.Logger, config.ReplaceVanillaWeatherBeds, config.ExperimentalRainSurfaceEmitters);

        DisposeLeafRustleRuntime();
        DisposeRainRuntime();

        if (config.EnableLeafRustleEmitters)
        {
            leafRustleEmitterSystem = new LeafRustleEmitterSystem(clientApi);
            leafRustleDebugRenderer = new LeafRustleDebugRenderer(clientApi, leafRustleEmitterSystem);
            clientApi.Event.RegisterRenderer(leafRustleDebugRenderer, EnumRenderStage.Opaque, "surroundweather-leafdebug");
        }

        if (config.ExperimentalRainSurfaceEmitters)
        {
            rainSurfaceEmitterSystem = new RainSurfaceEmitterSystem(clientApi);
            if (config.EnableDebugTools && config.ShowRainSurfaceEmitterDebugVisuals)
            {
                rainSurfaceEmitterDebugRenderer = new RainSurfaceEmitterDebugRenderer(clientApi, rainSurfaceEmitterSystem);
                clientApi.Event.RegisterRenderer(rainSurfaceEmitterDebugRenderer, EnumRenderStage.Opaque, "surroundweather-rainsurfacedebug");
            }
        }

        if (config.EnableRainEmitters)
        {
            rainEmitterSystem = new RainEmitterSystem(clientApi);
            if (config.EnableDebugTools && config.ShowRainEmitterDebugVisuals)
            {
                rainEmitterDebugRenderer = new RainEmitterDebugRenderer(clientApi, rainEmitterSystem);
                clientApi.Event.RegisterRenderer(rainEmitterDebugRenderer, EnumRenderStage.Opaque, "surroundweather-raindebug");
            }
        }
    }

    private void DisposeLeafRustleRuntime()
    {
        if (leafRustleDebugRenderer != null)
        {
            clientApi?.Event.UnregisterRenderer(leafRustleDebugRenderer, EnumRenderStage.Opaque);
            leafRustleDebugRenderer.Dispose();
            leafRustleDebugRenderer = null;
        }

        leafRustleEmitterSystem?.Dispose();
        leafRustleEmitterSystem = null;
    }

    private void DisposeRainRuntime()
    {
        if (rainSurfaceEmitterDebugRenderer != null)
        {
            clientApi?.Event.UnregisterRenderer(rainSurfaceEmitterDebugRenderer, EnumRenderStage.Opaque);
            rainSurfaceEmitterDebugRenderer.Dispose();
            rainSurfaceEmitterDebugRenderer = null;
        }

        rainSurfaceEmitterSystem?.Dispose();
        rainSurfaceEmitterSystem = null;

        if (rainEmitterDebugRenderer != null)
        {
            clientApi?.Event.UnregisterRenderer(rainEmitterDebugRenderer, EnumRenderStage.Opaque);
            rainEmitterDebugRenderer.Dispose();
            rainEmitterDebugRenderer = null;
        }

        rainEmitterSystem?.Dispose();
        rainEmitterSystem = null;
    }
}
