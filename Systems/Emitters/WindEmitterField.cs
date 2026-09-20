using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>
/// Experimental: wind from the open air around the listener, instead of a bed at their head.
/// <para>
/// Wind is heard where air moves freely, so the emitters stand a little above the ground wherever
/// the sky is open (the rain height map again). That alone gives the wind a place: on a plain it
/// is all around, under a cliff it is out from the cliff, and in a cave it is the mouth, with the
/// audio engine muffling it through the rock. It is loudest from upwind, where it comes from.
/// </para>
/// </summary>
internal sealed class WindEmitterField : EmitterField
{
    private const float MinWind = 0.05f;
    /// <summary>Above the ground: wind is in the air, not on the floor.</summary>
    private const double HeightAboveSurface = 2.5;
    private const double MaxVerticalOffset = 24.0;
    private const float BaseVolume = 0.6f;
    /// <summary>How much quieter the wind is from straight downwind than from upwind.</summary>
    private const float DownwindLevel = 0.45f;

    private const int Leafy = 0;
    private const int Leafless = 1;

    private double upwindX;
    private double upwindZ;
    private double listenerX;
    private double listenerZ;

    public WindEmitterField(ICoreClientAPI capi)
        : base(capi)
    {
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    /// <summary>Wind is broad: fewer, wider cells than rain.</summary>
    protected override double NearSpacing => 5.0;

    protected override double Radius => 30.0;

    protected override double MinRadius => 3.0;

    /// <summary>Long slices with slow fades, so a gust keeps its shape.</summary>
    protected override double LifetimeSeconds => 6.0;

    protected override float FadeSeconds => 2.0f;

    protected override int MaxCount(float intensity) => 28;

    protected override float RingGain(int level) => level == 0 ? 0.6f : 1f;

    protected override bool TryGetIntensity(Entity player, out float intensity)
    {
        intensity = 0f;
        if (!Config.ExperimentalWindEmitters)
        {
            return false;
        }

        Vec3d wind = capi.World?.BlockAccessor?.GetWindSpeedAt(player.Pos.XYZ);
        if (wind == null)
        {
            return false;
        }

        double speed = Math.Sqrt((wind.X * wind.X) + (wind.Z * wind.Z));
        if (speed > 1e-3)
        {
            // Wind is heard from where it comes from.
            upwindX = -wind.X / speed;
            upwindZ = -wind.Z / speed;
        }

        listenerX = player.Pos.X;
        listenerZ = player.Pos.Z;
        // As the game's own bed: nothing below a light breeze, full by a gale.
        intensity = GameMath.Clamp((float)((speed - 0.3) * 1.2), 0f, 1f);
        return intensity >= MinWind;
    }

    protected override bool TryGetCell(IBlockAccessor blocks, int x, int z, int size, EntityPos playerPos,
                                       out double px, out double py, out double pz, out int kind)
    {
        // Open to the sky at the cell's centre: the air above the ground, a roof, a hilltop.
        px = x + 0.5;
        py = blocks.GetRainMapHeightAt(x, z) + 1.0 + HeightAboveSurface;
        pz = z + 0.5;
        kind = 0;
        return Math.Abs(py - playerPos.Y) <= MaxVerticalOffset;
    }

    protected override ILoadedSound CreateSound(in EmitterCell cell, float intensity, out int variant)
    {
        // Among trees the wind is in the leaves; in bare country it is the air itself.
        float leafiness = GameMath.Clamp(GlobalConstants.CurrentNearbyRelLeavesCountClient * 60f, 0f, 1f);
        variant = random.NextDouble() < leafiness ? Leafy : Leafless;
        AssetLocation[] samples = variant == Leafy ? CustomSoundRegistry.WindLeafySlices : CustomSoundRegistry.WindLeaflessSlices;
        return capi.World.LoadSound(new SoundParams
        {
            Location = samples[random.Next(samples.Length)],
            Position = new Vec3f((float)cell.X, (float)cell.Y, (float)cell.Z),
            RelativePosition = false,
            Range = 48f,
            SoundType = EnumSoundType.Weather,
            Pitch = 0.95f + ((float)random.NextDouble() * 0.1f),
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    protected override float VolumeOf(int variant, in EmitterCell cell, float intensity)
    {
        double dx = cell.X - listenerX;
        double dz = cell.Z - listenerZ;
        double distance = Math.Sqrt((dx * dx) + (dz * dz));
        double facingWind = distance < 1e-6 ? 0.0 : ((dx * upwindX) + (dz * upwindZ)) / distance;  // 1 upwind, -1 downwind
        float direction = DownwindLevel + ((1f - DownwindLevel) * (float)((facingWind + 1.0) * 0.5));
        return BaseVolume * intensity * direction * Math.Max(0f, Config.WindEmitterVolume);
    }
}
