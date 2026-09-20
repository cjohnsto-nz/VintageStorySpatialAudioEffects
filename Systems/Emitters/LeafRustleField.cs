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

    protected override double Radius => 24.0;

    protected override double NearSpacing => Math.Max(2.0, Config.LeafRustleEmitterSpacing);

    protected override double MinRadius => 0.8;

    protected override double LifetimeSeconds => 4.5;

    protected override float FadeSeconds => Math.Max(0.1f, Config.LeafRustleFadeOutSeconds);

    /// <summary>Foliage is thin enough that every cell sounds alike.</summary>
    protected override float RingGain(int level) => level == 0 ? 0.7f : 1f;

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

    protected override int MaxCount(float intensity) => (int)Math.Round(10f + (26f * intensity));

    protected override bool TryGetCell(IBlockAccessor blocks, int x, int z, int size, EntityPos playerPos, out double y, out int kind)
    {
        // The foliage in the cell nearest a little above head height, where a canopy's edge is.
        // Small cells are searched whole; large ones on a 3 x 3 lattice of columns.
        y = 0;
        kind = Leaves;
        double wantedY = playerPos.Y + 1.5;
        double best = double.MaxValue;
        int stride = Math.Max(1, size / 3);
        int half = size / 2;
        int baseY = (int)Math.Floor(playerPos.Y);
        var pos = new BlockPos(0, 0, 0, playerPos.Dimension);
        for (int dx = -half; dx <= half; dx += stride)
        {
            for (int dz = -half; dz <= half; dz += stride)
            {
                for (int dy = -VerticalReach; dy <= VerticalReach; dy++)
                {
                    double offset = Math.Abs((baseY + dy + 0.5) - wantedY) + ((Math.Abs(dx) + Math.Abs(dz)) * 0.01);
                    if (offset >= best)
                    {
                        continue;
                    }

                    pos.Set(x + dx, baseY + dy, z + dz);
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

                    best = offset;
                    y = baseY + dy + 0.75;
                    kind = reeds ? Reeds : Leaves;
                }
            }
        }

        return best < double.MaxValue;
    }

    protected override ILoadedSound CreateSound(in EmitterCell candidate, float intensity, out int variant)
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
