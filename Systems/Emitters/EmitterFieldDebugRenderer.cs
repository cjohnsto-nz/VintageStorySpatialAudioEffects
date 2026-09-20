using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SpatialAudioEffects;

/// <summary>Draws a field's emitters: a column at each, as tall as it is loud, grey while it fades out.</summary>
internal sealed class EmitterFieldDebugRenderer : IRenderer
{
    private const int FadingColor = unchecked((int)0xFF808080);

    private readonly ICoreClientAPI capi;
    private readonly EmitterField field;
    private readonly Func<bool> enabled;
    private readonly int color;
    private readonly float loudest;

    /// <param name="loudest">The volume drawn at full height (rustles are far quieter than rain).</param>
    public EmitterFieldDebugRenderer(ICoreClientAPI capi, EmitterField field, Func<bool> enabled, int color, float loudest)
    {
        this.capi = capi;
        this.field = field;
        this.enabled = enabled;
        this.color = color;
        this.loudest = Math.Max(0.01f, loudest);
    }

    public double RenderOrder => 0.52;

    public int RenderRange => 999;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque || !enabled())
        {
            return;
        }

        var player = capi.World?.Player?.Entity;
        if (player?.Pos == null)
        {
            return;
        }

        List<EmitterVisual> emitters = field.GetSnapshot();
        var origin = new BlockPos((int)Math.Floor(player.Pos.X), (int)Math.Floor(player.Pos.Y), (int)Math.Floor(player.Pos.Z), player.Pos.Dimension);
        foreach (EmitterVisual emitter in emitters)
        {
            int c = emitter.FadingOut ? FadingColor : color;
            float x = (float)(emitter.Position.X - origin.X);
            float y = (float)(emitter.Position.Y - origin.Y);
            float z = (float)(emitter.Position.Z - origin.Z);
            float height = 0.3f + (Math.Min(1f, emitter.Volume / loudest) * 3f);
            capi.Render.RenderLine(origin, x, y, z, x, y + height, z, c);
            capi.Render.RenderLine(origin, x - 0.4f, y, z, x + 0.4f, y, z, c);
            capi.Render.RenderLine(origin, x, y, z - 0.4f, x, y, z + 0.4f, c);
        }
    }

    public void Dispose()
    {
    }
}
