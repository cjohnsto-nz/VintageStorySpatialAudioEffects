using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>
/// Rain on the windows, from each window, on its weather side.
/// <para>
/// Vanilla plays one sound for every pane within a 32-block section at once, from the nearest
/// point of a box drawn round them all, which indoors is the listener's own head. Here each window
/// near you has its own quiet emitter, standing just outside the pane where the rain lands, so the
/// sound comes through the glass from outside as it should, and the audio engine muffles it.
/// </para>
/// </summary>
internal sealed class RainWindowField : EmitterField
{
    /// <summary>Vanilla's ambient sound for rain on glass; any block that plays it is a window.</summary>
    internal const string WindowSound = "rainwindow";

    private const int VerticalReach = 6;
    private const float MinRainfall = 0.1f;
    /// <summary>Out from the middle of the pane: just past its face, in the weather.</summary>
    private const double OutsideOffset = 0.6;

    public RainWindowField(ICoreClientAPI capi)
        : base(capi)
    {
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    /// <summary>Windows are near things, and a wall of them should sound like several.</summary>
    protected override double NearSpacing => 3.0;

    protected override double Radius => 16.0;

    protected override double MinRadius => 1.0;

    protected override double LifetimeSeconds => 4.0;

    protected override int MaxCount(float intensity) => 10;

    /// <summary>Every window is its own pane, near or far.</summary>
    protected override float RingGain(int level) => 1f;

    protected override bool TryGetIntensity(Entity player, out float intensity)
    {
        intensity = 0f;
        if (!Config.ExperimentalWindowRainEmitters || !WeatherState.TryGetRainfall(capi, out float rainfall) || rainfall <= MinRainfall)
        {
            return false;
        }

        // As vanilla's own rule for a pane: the rainfall is the strength.
        intensity = GameMath.Clamp(rainfall, 0f, 1f);
        return true;
    }

    protected override bool TryGetCell(IBlockAccessor blocks, int x, int z, int size, EntityPos playerPos,
                                       out double px, out double py, out double pz, out int kind)
    {
        px = x + 0.5;
        py = 0;
        pz = z + 0.5;
        kind = 0;

        // The window in this cell nearest the listener, on the side the rain falls.
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
                    double ox = x + dx + 0.5 - playerPos.X;
                    double oy = baseY + dy + 0.5 - playerPos.Y;
                    double oz = z + dz + 0.5 - playerPos.Z;
                    double distance = (ox * ox) + (oy * oy) + (oz * oz);
                    if (distance >= best)
                    {
                        continue;
                    }

                    pos.Set(x + dx, baseY + dy, z + dz);
                    if (!IsWindow(blocks.GetBlock(pos)) || !TryGetWeatherSide(blocks, pos, out BlockFacing side))
                    {
                        continue;
                    }

                    best = distance;
                    px = pos.X + 0.5 + (side.Normali.X * OutsideOffset);
                    py = pos.Y + 0.5 + (side.Normali.Y * OutsideOffset);
                    pz = pos.Z + 0.5 + (side.Normali.Z * OutsideOffset);
                }
            }
        }

        return best < double.MaxValue;
    }

    protected override ILoadedSound CreateSound(in EmitterCell cell, float intensity, out int variant)
    {
        // The rain the window is under: the same sets the surfaces use, lightly pitched apart.
        variant = 0;
        AssetLocation[] samples = intensity < 0.35f ? CustomSoundRegistry.RainLightLoops
            : intensity < 0.7f ? CustomSoundRegistry.RainMediumLoops
            : CustomSoundRegistry.RainHeavyLoops;
        return capi.World.LoadSound(new SoundParams
        {
            Location = samples[random.Next(samples.Length)],
            Position = new Vec3f((float)cell.X, (float)cell.Y, (float)cell.Z),
            RelativePosition = false,
            Range = 12f,
            SoundType = EnumSoundType.Weather,
            Pitch = 1.02f + ((float)random.NextDouble() * 0.12f),  // thinner than rain on the ground
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    protected override float VolumeOf(int variant, in EmitterCell cell, float intensity) =>
        Math.Max(0f, Config.RainWindowVolume) * intensity;

    internal static bool IsWindow(Block block) =>
        block?.Sounds?.Ambient?.Path?.Contains(WindowSound, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The side of a pane the rain falls on: a neighbour that is open air and open to the sky. A
    /// window between two rooms has none, and stays quiet.
    /// </summary>
    private static bool TryGetWeatherSide(IBlockAccessor blocks, BlockPos pos, out BlockFacing side)
    {
        side = null;
        var neighbour = new BlockPos(0, 0, 0, pos.dimension);
        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            if (facing == BlockFacing.DOWN)
            {
                continue;  // rain does not land on a pane from below
            }

            neighbour.Set(pos.X + facing.Normali.X, pos.Y + facing.Normali.Y, pos.Z + facing.Normali.Z);
            Block block = blocks.GetBlock(neighbour);
            if (block == null || IsSolid(block) || blocks.GetRainMapHeightAt(neighbour.X, neighbour.Z) > neighbour.Y)
            {
                continue;  // walled in, or under cover: no rain on this side
            }

            side = facing;
            return true;
        }

        return false;
    }
}
