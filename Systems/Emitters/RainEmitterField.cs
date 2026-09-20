using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>What a rain field plays.</summary>
internal enum RainFieldProfile
{
    /// <summary>Experimental: short slices of rain on every surface, in place of the rain beds.</summary>
    SurfaceLoops,

    /// <summary>Sparse one-shot splashes further out, on top of whatever bed plays.</summary>
    Splashes,
}

/// <summary>
/// Rain from the surfaces it lands on. A column's rain-blocking top block (from the game's rain
/// height map) is the ground outside, the roof over a building, and the ground under a canopy, so
/// the emitters are simply where rain is. Nothing is muffled for being indoors: the audio engine
/// does that for whatever is behind a wall or roof.
/// </summary>
internal sealed class RainEmitterField : EmitterField
{
    private const int GridStep = 2;
    private const float MediumRainFrom = 0.35f;
    private const float HeavyRainFrom = 0.7f;
    private const float BaseVolume = 0.85f;
    private const float SplashVolume = 0.32f;

    // Kinds: what the rain lands on.
    private const int OnGround = 0;
    private const int OnLeaves = 1;
    private const int OnWater = 2;

    // Variants: which recordings play.
    private const int Light = 0;
    private const int Medium = 1;
    private const int Heavy = 2;
    private const int Canopy = 3;
    private const int Splash = 4;

    private static readonly AssetLocation[] SplashSamples =
    {
        CustomSoundRegistry.RainOneAlias,
        CustomSoundRegistry.RainTwoAlias,
        CustomSoundRegistry.RainThreeAlias,
        CustomSoundRegistry.RainFourAlias,
    };

    private readonly RainFieldProfile profile;

    public RainEmitterField(ICoreClientAPI capi, RainFieldProfile profile)
        : base(capi)
    {
        this.profile = profile;
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    private bool Loops => profile == RainFieldProfile.SurfaceLoops;

    protected override double Radius => Loops ? Config.RainSurfaceEmitterRadius : 18.0;

    protected override double MinRadius => Loops ? 1.5 : 7.5;

    protected override double Spacing => Loops ? Config.RainSurfaceEmitterSpacing : 4.0;

    protected override double LifetimeSeconds => Loops ? Config.RainSurfaceEmitterLifetimeSeconds : 6.0;

    /// <summary>A slice of steady rain starts anywhere; a splash starts at its start.</summary>
    protected override float RandomStartFraction => Loops ? 1f : 0f;

    protected override bool TryGetIntensity(Entity player, out float intensity)
    {
        intensity = 0f;
        bool enabled = Loops ? Config.ExperimentalRainSurfaceEmitters : Config.EnableRainEmitters;
        if (!enabled || !WeatherState.TryGetRainfall(capi, out float rainfall))
        {
            return false;
        }

        intensity = GameMath.Clamp(rainfall * 2f, 0f, 1f);
        return true;
    }

    protected override int TargetCount(float intensity) => Loops
        ? (int)Math.Round(Math.Max(2, Config.RainSurfaceEmitterCount) * (0.35f + (0.65f * intensity)))
        : (int)Math.Round(6f + (10f * intensity));

    protected override void CollectCandidates(Vec3d centre, double radius, EntityPos playerPos, List<EmitterCandidate> into)
    {
        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return;
        }

        // A fixed grid (not the centre's own cell), so the same spots come up as the field moves.
        int reach = (int)Math.Ceiling(radius);
        int baseX = ((int)Math.Floor(centre.X) / GridStep) * GridStep;
        int baseZ = ((int)Math.Floor(centre.Z) / GridStep) * GridStep;
        var pos = new BlockPos(0, 0, 0, playerPos.Dimension);
        for (int dx = -reach; dx <= reach; dx += GridStep)
        {
            for (int dz = -reach; dz <= reach; dz += GridStep)
            {
                int x = baseX + dx;
                int z = baseZ + dz;
                double ex = x + 0.5;
                double ez = z + 0.5;
                double cx = ex - centre.X;
                double cz = ez - centre.Z;
                if ((cx * cx) + (cz * cz) > radius * radius)
                {
                    continue;
                }

                // The top of this column's rain-blocking block: the ground, a roof.
                int surfaceY = blocks.GetRainMapHeightAt(x, z);
                double ey = surfaceY + 1.0;
                double verticalOffset = Math.Abs(ey - playerPos.Y);
                if (verticalOffset > MaxVerticalOffset)
                {
                    continue;
                }

                pos.Set(x, surfaceY, z);
                EnumBlockMaterial material = blocks.GetBlock(pos)?.BlockMaterial ?? EnumBlockMaterial.Air;
                int kind = material == EnumBlockMaterial.Leaves ? OnLeaves : material == EnumBlockMaterial.Water ? OnWater : OnGround;
                into.Add(new EmitterCandidate(ex, ey, ez, 1.0 - (verticalOffset / MaxVerticalOffset), kind));
            }
        }
    }

    protected override ILoadedSound CreateSound(in EmitterCandidate candidate, float intensity, out int variant)
    {
        // What the rain lands on picks the sound as much as how hard it falls.
        variant = !Loops ? Splash : candidate.Kind == OnLeaves ? Canopy : SetFor(intensity);
        AssetLocation[] samples = variant switch
        {
            Light => CustomSoundRegistry.RainLightLoops,
            Heavy => CustomSoundRegistry.RainHeavyLoops,
            Canopy => CustomSoundRegistry.RainCanopyLoops,
            Splash => SplashSamples,
            _ => CustomSoundRegistry.RainMediumLoops,
        };

        // A little spread keeps the emitters from ringing as one voice; rain on water is brighter.
        float pitch = 0.94f + ((float)random.NextDouble() * 0.12f) + (candidate.Kind == OnWater ? 0.06f : 0f);
        return capi.World.LoadSound(new SoundParams
        {
            Location = samples[random.Next(samples.Length)],
            Position = new Vec3f((float)candidate.X, (float)candidate.Y, (float)candidate.Z),
            RelativePosition = false,
            Range = Loops ? 16f : 70f,
            SoundType = Loops ? EnumSoundType.Weather : EnumSoundType.Ambient,
            Pitch = pitch,
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    protected override float VolumeOf(int variant, int kind, float intensity)
    {
        if (variant == Splash)
        {
            return SplashVolume * (0.5f + (0.5f * intensity));
        }

        // A downpour is louder than a shower beyond what its recording says, and the level rises
        // faster than the rainfall, so heavy rain lands as heavy.
        float gain = variant switch
        {
            Light => 0.8f,
            Heavy => 1.3f,
            _ => 1.0f,
        };
        return BaseVolume * MathF.Pow(intensity, 0.75f) * gain * Math.Max(0f, Config.RainSurfaceEmitterVolume);
    }

    /// <summary>The rain picked up or eased off: emitters of the old character go as their turn comes.</summary>
    protected override bool IsStale(int variant, int kind, float intensity) =>
        variant is Light or Medium or Heavy && variant != SetFor(intensity);

    private static int SetFor(float intensity) =>
        intensity < MediumRainFrom ? Light : intensity < HeavyRainFrom ? Medium : Heavy;
}
