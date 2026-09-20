using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>Leaves and reeds rustling in the wind, from the foliage around the listener.</summary>
internal sealed class LeafRustleField : EmitterField
{
    private const int GridStep = 2;
    private const int VerticalReach = 10;
    private const float MinWind = 0.08f;

    private const int Leaves = 0;
    private const int Reeds = 1;

    private static readonly AssetLocation[] BrightRustles = { CustomSoundRegistry.LeafRustleOneAlias, CustomSoundRegistry.LeafRustleTwoAlias };
    private static readonly AssetLocation[] SoftRustles = { CustomSoundRegistry.LeafRustleThreeAlias, CustomSoundRegistry.LeafRustleFourAlias };

    public LeafRustleField(ICoreClientAPI capi)
        : base(capi)
    {
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    protected override double Radius => 22.0;

    protected override double MinRadius => 0.8;

    protected override double Spacing => Config.LeafRustleEmitterSpacing;

    protected override double LifetimeSeconds => 4.5;

    protected override float FadeSeconds => Math.Max(0.1f, Config.LeafRustleFadeOutSeconds);

    protected override double MaxVerticalOffset => VerticalReach + 2;

    /// <summary>The recordings are long gusts: entering in their first half varies them.</summary>
    protected override float RandomStartFraction => 0.5f;

    protected override bool TryGetIntensity(Entity player, out float intensity)
    {
        intensity = 0f;
        if (!Config.EnableLeafRustleEmitters)
        {
            return false;
        }

        Vec3d wind = capi.World?.BlockAccessor?.GetWindSpeedAt(player.Pos.XYZ);
        if (wind == null)
        {
            return false;
        }

        intensity = GameMath.Clamp((float)(Math.Sqrt((wind.X * wind.X) + (wind.Z * wind.Z)) / 2.5), 0f, 1f);
        return intensity >= MinWind;
    }

    protected override int TargetCount(float intensity) => (int)Math.Round(8f + (20f * intensity));

    protected override void CollectCandidates(Vec3d centre, double radius, EntityPos playerPos, List<EmitterCandidate> into)
    {
        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return;
        }

        // A fixed grid through the foliage: every second block each way is plenty for a rustle.
        int reach = (int)Math.Ceiling(radius);
        int baseX = ((int)Math.Floor(centre.X) / GridStep) * GridStep;
        int baseY = ((int)Math.Floor(playerPos.Y) / GridStep) * GridStep;
        int baseZ = ((int)Math.Floor(centre.Z) / GridStep) * GridStep;
        var pos = new BlockPos(0, 0, 0, playerPos.Dimension);
        for (int dx = -reach; dx <= reach; dx += GridStep)
        {
            for (int dz = -reach; dz <= reach; dz += GridStep)
            {
                double cx = baseX + dx + 0.5 - centre.X;
                double cz = baseZ + dz + 0.5 - centre.Z;
                if ((cx * cx) + (cz * cz) > radius * radius)
                {
                    continue;
                }

                for (int dy = -VerticalReach; dy <= VerticalReach; dy += GridStep)
                {
                    pos.Set(baseX + dx, baseY + dy, baseZ + dz);
                    Block block = blocks.GetBlock(pos);
                    if (block == null || block.Id == 0)
                    {
                        continue;
                    }

                    bool reeds = IsReedLike(block);
                    if (!reeds && block.BlockMaterial != EnumBlockMaterial.Leaves)
                    {
                        continue;
                    }

                    // Best a little above head height, where a canopy's edge is.
                    double offset = Math.Abs((pos.Y + 0.5) - (playerPos.Y + 1.0));
                    double quality = 1.0 - Math.Min(1.0, offset / (VerticalReach + 2));
                    into.Add(new EmitterCandidate(
                        pos.X + 0.5 + ((random.NextDouble() - 0.5) * 0.4),
                        pos.Y + 0.55 + (random.NextDouble() * 0.45),
                        pos.Z + 0.5 + ((random.NextDouble() - 0.5) * 0.4),
                        quality,
                        reeds ? Reeds : Leaves));
                }
            }
        }
    }

    protected override ILoadedSound CreateSound(in EmitterCandidate candidate, float intensity, out int variant)
    {
        variant = 0;
        // Stronger wind, brighter rustles.
        float softProbability = GameMath.Clamp(0.75f - (intensity * 0.5f), 0.25f, 0.75f);
        AssetLocation[] samples = random.NextDouble() < softProbability ? SoftRustles : BrightRustles;

        float spread = Math.Max(0f, Config.LeafRustlePitchVariationMultiplier);
        float centred = ((float)random.NextDouble() * 2f) - 1f;
        float pitch = candidate.Kind == Reeds
            ? GameMath.Clamp(0.67f + (centred * 0.17f * spread) + ((intensity - 0.5f) * 0.07f), 0.4f, 0.98f)
            : GameMath.Clamp(1f + (centred * 0.28f * spread) + ((intensity - 0.5f) * 0.12f), 0.6f, 1.42f);

        return capi.World.LoadSound(new SoundParams
        {
            Location = samples[random.Next(samples.Length)],
            Position = new Vec3f((float)candidate.X, (float)candidate.Y, (float)candidate.Z),
            RelativePosition = false,
            Range = 150f,
            SoundType = EnumSoundType.Ambient,
            Pitch = pitch,
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    protected override float VolumeOf(int variant, int kind, float intensity)
    {
        float volume = GameMath.Clamp(0.047f + (intensity * 0.048f), 0.036f, 0.15f);
        return GameMath.Clamp(volume * Math.Max(0f, Config.LeafRustleVolumeMultiplier), 0f, 0.24f);
    }

    private static bool IsReedLike(Block block)
    {
        string path = block.Code?.Path;
        return path != null
            && (path.Contains("reed", StringComparison.OrdinalIgnoreCase)
                || path.Contains("rush", StringComparison.OrdinalIgnoreCase)
                || path.Contains("cattail", StringComparison.OrdinalIgnoreCase));
    }
}
