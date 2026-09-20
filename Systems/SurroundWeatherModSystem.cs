using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
    private readonly List<(EmitterField Field, EmitterFieldDebugRenderer Renderer)> fields = new();

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
        RegisterCommands(api);
        AmbientSoundPlacementPatch.Initialize(api);
        RainWindowSuppressor.TryPatch(harmony, Mod.Logger);
        ApplyRuntimeConfig();
    }

    public override void Dispose()
    {
        DisposeFields();
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
        WeatherBedOverrides.Apply(clientApi, Mod.Logger, config.ReplaceVanillaWeatherBeds, config.ExperimentalRainSurfaceEmitters, config.ExperimentalWindEmitters);

        // Every emitter is an EmitterField: they lead the listener, turn over steadily and fade alike.
        DisposeFields();
        if (config.EnableLeafRustleEmitters)
        {
            AddField(new LeafRustleField(clientApi), "leaf", () => Config.ShowLeafRustleDebugVisuals, unchecked((int)0xFF4DFF4D), 0.24f);
        }

        if (config.ExperimentalRainSurfaceEmitters)
        {
            AddField(new RainEmitterField(clientApi, RainFieldProfile.SurfaceLoops), "rainsurface",
                () => Config.EnableDebugTools && Config.ShowRainSurfaceEmitterDebugVisuals, unchecked((int)0xFF4DFFD2), 1f);
        }

        if (config.ExperimentalWindEmitters)
        {
            AddField(new WindEmitterField(clientApi), "wind",
                () => Config.EnableDebugTools && Config.ShowWindEmitterDebugVisuals, unchecked((int)0xFFFFFFFF), 1f);
        }

        if (config.ExperimentalWindowRainEmitters)
        {
            AddField(new RainWindowField(clientApi), "rainwindow",
                () => Config.EnableDebugTools && Config.ShowRainSurfaceEmitterDebugVisuals, unchecked((int)0xFFFFD24D), 0.25f);
        }

        if (config.EnableRainEmitters)
        {
            AddField(new RainEmitterField(clientApi, RainFieldProfile.Splashes), "rain",
                () => Config.EnableDebugTools && Config.ShowRainEmitterDebugVisuals, unchecked((int)0xFF4DA6FF), 0.32f);
        }
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    private void RegisterCommands(ICoreClientAPI api)
    {
        api.ChatCommands.Create("surroundweather")
            .WithDescription("Surround Weather: what its emitters are doing")
            .WithAlias("sw")
            .BeginSubCommand("status")
                .WithDescription("Every emitter field: how many are playing, and how strongly the weather is driving it")
                .HandleWith(_ => TextCommandResult.Success(Status()))
            .EndSubCommand()
            .BeginSubCommand("windows")
                .WithDescription("Every window nearby: which side the rain reaches it from, and whether it is playing")
                .HandleWith(_ => TextCommandResult.Success(Windows()))
            .EndSubCommand();
    }

    private string Status()
    {
        if (fields.Count == 0)
        {
            return "No emitter fields are on. See ModConfig/surroundweather.json.";
        }

        var lines = new List<string>();
        foreach ((EmitterField field, EmitterFieldDebugRenderer _) in fields)
        {
            lines.Add(field.Describe());
        }

        lines.Add(WeatherState.TryGetRainfall(clientApi, out float rainfall)
            ? string.Format(CultureInfo.InvariantCulture, "raining: {0:0.00}", rainfall)
            : "not raining (snow, hail and dry weather count as dry here)");
        return string.Join("\n", lines);
    }

    private string Windows()
    {
        EntityPos position = clientApi?.World?.Player?.Entity?.Pos;
        if (position == null)
        {
            return "no player";
        }

        foreach ((EmitterField field, EmitterFieldDebugRenderer _) in fields)
        {
            if (field is RainWindowField windows)
            {
                return windows.DescribeWindows(position);
            }
        }

        return "The window emitters are off (ExperimentalWindowRainEmitters in ModConfig/surroundweather.json).";
    }

    private void AddField(EmitterField field, string name, Func<bool> debugEnabled, int color, float loudest)
    {
        var renderer = new EmitterFieldDebugRenderer(clientApi, field, debugEnabled, color, loudest);
        clientApi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "surroundweather-" + name + "debug");
        fields.Add((field, renderer));
    }

    private void DisposeFields()
    {
        foreach ((EmitterField field, EmitterFieldDebugRenderer renderer) in fields)
        {
            clientApi?.Event.UnregisterRenderer(renderer, EnumRenderStage.Opaque);
            renderer.Dispose();
            field.Dispose();
        }

        fields.Clear();
    }
}
