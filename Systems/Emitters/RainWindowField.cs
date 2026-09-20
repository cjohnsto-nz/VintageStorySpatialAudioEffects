using System;
using System.Collections.Generic;
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
    /// <summary>Vanilla's own gate: a pane sounds when rain falls within two blocks of it.</summary>
    private const int MaxDistanceToRain = 2;

    public override string Name => "rain on windows";

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
                    if (!IsWindow(blocks.GetBlock(pos)) || !TryGetWeatherSide(blocks, pos, out BlockFacing side, out _))
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

    /// <summary>Every window within reach and what this field makes of it, for the status command.</summary>
    internal string DescribeWindows(EntityPos playerPos, int reach = 10)
    {
        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return "no world";
        }

        List<EmitterVisual> live = GetSnapshot();
        var lines = new List<string>();
        var pos = new BlockPos(0, 0, 0, playerPos.Dimension);
        int baseX = (int)Math.Floor(playerPos.X);
        int baseY = (int)Math.Floor(playerPos.Y);
        int baseZ = (int)Math.Floor(playerPos.Z);
        for (int dx = -reach; dx <= reach; dx++)
        {
            for (int dy = -VerticalReach; dy <= VerticalReach; dy++)
            {
                for (int dz = -reach; dz <= reach; dz++)
                {
                    pos.Set(baseX + dx, baseY + dy, baseZ + dz);
                    if (!IsWindow(blocks.GetBlock(pos)))
                    {
                        continue;
                    }

                    bool sounds = TryGetWeatherSide(blocks, pos, out BlockFacing side, out int distance);
                    bool playing = false;
                    foreach (EmitterVisual visual in live)
                    {
                        playing |= Math.Abs(visual.Position.X - (pos.X + 0.5)) <= 1
                            && Math.Abs(visual.Position.Y - (pos.Y + 0.5)) <= 1
                            && Math.Abs(visual.Position.Z - (pos.Z + 0.5)) <= 1;
                    }

                    string state = !sounds ? "no rain reaches it"
                        : playing ? "PLAYING"
                        : "silent (crowded out, too far, or behind rock)";
                    lines.Add(string.Format(
                        "{0},{1},{2}: {3}, rain {4} away: {5}",
                        pos.X, pos.Y, pos.Z,
                        side == null ? "walled in" : "weather side " + side.Code,
                        distance == int.MaxValue ? "never" : distance.ToString(),
                        state));
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines.Count == 0
            ? string.Format("no windows within {0} blocks", reach)
            : string.Format("{0} window(s) within {1} blocks, {2} emitter(s) live:", lines.Count, reach, live.Count)
              + "\n" + string.Join("\n", lines);
    }

    internal static bool IsWindow(Block block) =>
        block?.Sounds?.Ambient?.Path?.Contains(WindowSound, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The side of a pane the rain falls on: the open neighbour the rain is nearest to, by the
    /// game's own reckoning. GetDistanceToRainFall floods out through open air only, never through
    /// a wall, so from outside a pane it is 0 (a block or two under an eave) and from inside a room
    /// it finds nothing. A window between two rooms therefore has no weather side and stays quiet,
    /// as does one deep under cover, which is vanilla's rule for sounding a pane at all.
    /// </summary>
    private static bool TryGetWeatherSide(IBlockAccessor blocks, BlockPos pos, out BlockFacing side, out int distanceToRain)
    {
        side = null;
        distanceToRain = int.MaxValue;
        var neighbour = new BlockPos(0, 0, 0, pos.dimension);
        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            if (facing == BlockFacing.DOWN)
            {
                continue;  // rain does not land on a pane from below
            }

            neighbour.Set(pos.X + facing.Normali.X, pos.Y + facing.Normali.Y, pos.Z + facing.Normali.Z);
            Block block = blocks.GetBlock(neighbour);
            if (block == null || IsSolid(block))
            {
                continue;  // walled in on this side
            }

            // Open to the sky is rain at once; otherwise ask how far the rain is from there.
            int distance = blocks.GetRainMapHeightAt(neighbour.X, neighbour.Z) <= neighbour.Y
                ? 0
                : blocks.GetDistanceToRainFall(neighbour, 3, 2);
            if (distance >= distanceToRain)
            {
                continue;
            }

            distanceToRain = distance;
            side = facing;
        }

        return side != null && distanceToRain <= MaxDistanceToRain;
    }
}
