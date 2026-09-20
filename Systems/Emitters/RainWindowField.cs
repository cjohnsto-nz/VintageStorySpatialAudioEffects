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
    /// <summary>How far out from a pane to look for cover, to say why a pane is quiet.</summary>
    private const int OutwardReach = 3;
    private const float MinRainfall = 0.1f;
    /// <summary>Out from the middle of the pane: just past its face, in the weather.</summary>
    private const double OutsideOffset = 0.6;

    public override string Name => "rain on windows";

    /// <summary>Whatever the panes hereabouts play for rain, taken from the first one seen.</summary>
    private AssetLocation windowSound;

    public RainWindowField(ICoreClientAPI capi)
        : base(capi)
    {
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    /// <summary>A cell per column, so every pane within reach gets its own emitter.</summary>
    protected override double NearSpacing => 1.0;

    /// <summary>One size throughout: a pane ten blocks off is still a pane.</summary>
    protected override bool RingsWiden => false;

    protected override double Radius => Math.Max(2.0, Config.RainWindowRadius);

    protected override double MinRadius => 0.5;

    protected override double LifetimeSeconds => 4.0;

    protected override int MaxCount(float intensity) => Math.Max(1, Config.RainWindowMaxCount);

    /// <summary>Under investigation: say in the log how many panes are sounding, and where.</summary>
    protected override bool LogState => true;

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
                    Block window = blocks.GetBlock(pos);
                    if (!IsWindow(window) || !TryGetWeatherSide(blocks, pos, out BlockFacing side, out _, out _))
                    {
                        continue;
                    }

                    windowSound = window.Sounds.Ambient;

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
        // The pane's own recording - vanilla's rain on glass - one slice at a time, from outside
        // the glass instead of from a single point that follows the listener about. Every emitter
        // starts somewhere else in it and is pitched a little apart, so a wall of them is rain
        // rather than one sound played several times over.
        variant = 0;
        if (windowSound == null)
        {
            return null;
        }

        return capi.World.LoadSound(new SoundParams
        {
            Location = windowSound,
            Position = new Vec3f((float)cell.X, (float)cell.Y, (float)cell.Z),
            RelativePosition = false,
            Range = 12f,
            SoundType = EnumSoundType.Weather,
            Pitch = 0.95f + ((float)random.NextDouble() * 0.14f),
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    protected override float VolumeOf(int variant, in EmitterCell cell, float intensity) =>
        Math.Max(0f, Config.RainWindowVolume) * intensity;

    /// <summary>Every window within reach and what this field makes of it, for the status command.</summary>
    internal string DescribeWindows(EntityPos playerPos, int reach = 12)
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

                    bool sounds = TryGetWeatherSide(blocks, pos, out BlockFacing side, out BlockFacing covered, out int skyOut);
                    bool playing = false;
                    foreach (EmitterVisual visual in live)
                    {
                        playing |= Math.Abs(visual.Position.X - (pos.X + 0.5)) <= 1
                            && Math.Abs(visual.Position.Y - (pos.Y + 0.5)) <= 1
                            && Math.Abs(visual.Position.Z - (pos.Z + 0.5)) <= 1;
                    }

                    string where = sounds ? "clear sky " + side.Code
                        : covered == null ? "walled in"
                        : skyOut > 0 ? string.Format("under cover {0}, sky {1} blocks out", covered.Code, skyOut)
                        : "under cover " + covered.Code;
                    string state = !sounds ? "silent"
                        : playing ? "PLAYING"
                        : "silent (crowded out, too far, or behind rock)";
                    lines.Add(string.Format("{0},{1},{2}: {3}: {4}", pos.X, pos.Y, pos.Z, where, state));
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
    /// The side of a pane the rain falls on: an open neighbour with clear sky straight above it.
    /// Rain has to land on the glass to be heard through it, so an eave over that neighbour is
    /// enough to keep the pane quiet, and the inside of a room - always under its own roof - can
    /// never be chosen.
    /// <para>
    /// <paramref name="coveredSide"/> and <paramref name="skyStepsOut"/> are for the status
    /// command only: the nearest face that is open air but roofed over, and how far out along it
    /// the sky does open, so a quiet window can say whether it is walled in or under an eave.
    /// </para>
    /// </summary>
    private static bool TryGetWeatherSide(IBlockAccessor blocks, BlockPos pos, out BlockFacing side,
                                          out BlockFacing coveredSide, out int skyStepsOut)
    {
        side = null;
        coveredSide = null;
        skyStepsOut = 0;
        var probe = new BlockPos(0, 0, 0, pos.dimension);
        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            if (facing == BlockFacing.DOWN)
            {
                continue;  // rain does not land on a pane from below
            }

            for (int step = 1; step <= OutwardReach; step++)
            {
                probe.Set(pos.X + (facing.Normali.X * step), pos.Y + (facing.Normali.Y * step), pos.Z + (facing.Normali.Z * step));
                Block block = blocks.GetBlock(probe);
                if (block == null || IsSolid(block))
                {
                    break;  // walled in this way; try another face
                }

                if (blocks.GetRainMapHeightAt(probe.X, probe.Z) > probe.Y)
                {
                    if (step == 1)
                    {
                        coveredSide ??= facing;  // open air, but something overhead
                    }

                    continue;
                }

                if (step == 1)
                {
                    side = facing;
                    return true;  // rain lands right outside the glass: this is the weather side
                }

                if (coveredSide == facing && skyStepsOut == 0)
                {
                    skyStepsOut = step;  // how far past the eave the sky starts, for the report
                }

                break;
            }
        }

        return false;
    }
}
