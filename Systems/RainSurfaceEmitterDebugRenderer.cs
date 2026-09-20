using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>Where the surface rain emitters are: a column at each, blue while it fades out.</summary>
internal sealed class RainSurfaceEmitterDebugRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly RainSurfaceEmitterSystem emitterSystem;

    public double RenderOrder => 0.52;

    public int RenderRange => 999;

    public RainSurfaceEmitterDebugRenderer(ICoreClientAPI capi, RainSurfaceEmitterSystem emitterSystem)
    {
        this.capi = capi;
        this.emitterSystem = emitterSystem;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!SurroundWeatherConfigManager.Current.EnableDebugTools
            || !SurroundWeatherConfigManager.Current.ShowRainSurfaceEmitterDebugVisuals
            || stage != EnumRenderStage.Opaque)
        {
            return;
        }

        var player = capi.World?.Player?.Entity;
        if (player?.Pos == null)
        {
            return;
        }

        List<RainSurfaceEmitterVisual> emitters = emitterSystem.GetActiveEmittersSnapshot();
        if (emitters.Count == 0)
        {
            return;
        }

        var origin = new BlockPos((int)Math.Floor(player.Pos.X), (int)Math.Floor(player.Pos.Y), (int)Math.Floor(player.Pos.Z), player.Pos.Dimension);
        foreach (RainSurfaceEmitterVisual emitter in emitters)
        {
            int color = emitter.FadingOut ? unchecked((int)0xFF3A6BFF) : unchecked((int)0xFF4DFFD2);
            float x = (float)(emitter.Position.X - origin.X);
            float y = (float)(emitter.Position.Y - origin.Y);
            float z = (float)(emitter.Position.Z - origin.Z);
            float height = 0.5f + (emitter.Volume * 3f);
            capi.Render.RenderLine(origin, x, y, z, x, y + height, z, color);
            capi.Render.RenderLine(origin, x - 0.4f, y, z, x + 0.4f, y, z, color);
            capi.Render.RenderLine(origin, x, y, z - 0.4f, x, y, z + 0.4f, color);
        }
    }

    public void Dispose()
    {
    }
}
