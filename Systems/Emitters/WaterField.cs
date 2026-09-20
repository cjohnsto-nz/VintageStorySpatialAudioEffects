using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>What water a field is listening to.</summary>
internal enum WaterFieldProfile
{
    /// <summary>Still water: a lake, the sea. Broad, even, and open to the sky.</summary>
    Waves,

    /// <summary>Moving water: a creek, rapids, a fall. Narrow, and wherever it happens to be.</summary>
    Flowing,
}

/// <summary>
/// Water from the water: an emitter on it in every cell of the grid, near and far at once.
/// <para>
/// Vanilla makes one sound for every water block in a 32-block section, merges the sections into
/// one box that can span a whole lake or follow a river out of sight, and plays it from the point
/// of that box nearest the listener. A lake is then a single point a stride away wherever you
/// stand, and a river slides along beside you as you walk its bank: the sound is never where the
/// water is. Here it is, and the audio engine has real positions to work with - a creek in a gorge
/// is muffled by the gorge.
/// </para>
/// <para>
/// Which water a block is, it says itself, the same way vanilla decides: still water asks for
/// waterwaves, water flowing downward for waterfall, and water flowing along for creek or, in a
/// rapid, rapids. A block's own ambient sound is the one its emitter plays, so a modded stream
/// brings its own.
/// </para>
/// </summary>
internal sealed class WaterField : EmitterField
{
    internal const string WaveSound = "waterwaves";
    internal const string CreekSound = "creek";
    internal const string RapidsSound = "rapids";
    internal const string FallSound = "waterfall";

    /// <summary>Water this far above or below the listener still counts as theirs to hear.</summary>
    private const int VerticalReach = 10;

    /// <summary>Just above the water, which stands a little under the top of its block.</summary>
    private const double HeightAboveSurface = 0.05;

    /// <summary>Out from the middle of a falling block, into the air beside it.</summary>
    private const double FallSideOffset = 0.6;

    // Kinds: what the water is doing here. They are also what picks the sound.
    private const int OpenWater = 0;
    private const int Shore = 1;
    private const int Creek = 2;
    private const int Rapids = 3;
    private const int Fall = 4;

    private readonly WaterFieldProfile profile;

    /// <summary>What the blocks hereabouts play, kind by kind, taken from the first one seen.</summary>
    private readonly AssetLocation[] soundsByKind = new AssetLocation[5];

    public WaterField(ICoreClientAPI capi, WaterFieldProfile profile)
        : base(capi)
    {
        this.profile = profile;
    }

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    public override string Name => Still ? "water" : "creeks and falls";

    private bool Still => profile == WaterFieldProfile.Waves;

    /// <summary>
    /// Still water is a broad, even thing: a few emitters well apart read as a lake, where many
    /// close together read as a wall of water standing around the listener. A creek is the other
    /// way about - it is a line a block or two wide, and wants following.
    /// </summary>
    protected override double NearSpacing => Still ? Math.Max(2.0, Config.WaterWaveSpacing) : Math.Max(1.0, Config.FlowingWaterSpacing);

    protected override double Radius => Still ? Math.Max(8.0, Config.WaterWaveRadius) : Math.Max(6.0, Config.FlowingWaterRadius);

    protected override double MinRadius => Still ? 3.0 : 1.5;

    /// <summary>Waves are slow; a slice wants to be long enough to be one.</summary>
    protected override double LifetimeSeconds => Still ? 6.0 : 5.0;

    /// <summary>
    /// Vanilla played one sound for the whole body of water. These are several, and several of
    /// them at once is louder than one of them for nothing: past four, each gives way.
    /// </summary>
    protected override int LoudnessReference => 4;

    protected override int MaxCount(float intensity) =>
        Math.Max(1, Still ? Config.WaterWaveCount : Config.FlowingWaterCount);

    protected override bool TryGetIntensity(Entity player, out float intensity)
    {
        // Water is not weather: it is there or it is not, and how much of it is around the
        // listener - how many cells find some - is what makes a lake louder than a pond.
        intensity = 1f;
        return Still ? Config.ExperimentalWaterWaveEmitters : Config.ExperimentalFlowingWaterEmitters;
    }

    protected override bool TryGetCell(IBlockAccessor blocks, int x, int z, int size, EntityPos playerPos,
                                       out double px, out double py, out double pz, out int kind)
    {
        px = x + 0.5;
        py = 0;
        pz = z + 0.5;
        kind = OpenWater;

        // The water nearest the listener's own level, so a lake below a cliff is below them and a
        // creek on a ledge above is above, rather than whatever the sky happens to see.
        int baseY = (int)Math.Floor(playerPos.Y);
        var pos = new BlockPos(0, 0, 0, playerPos.Dimension);
        for (int step = 0; step <= VerticalReach * 2; step++)
        {
            // The listener's own level first, then a block below, a block above, and outward.
            int offset = (step + 1) / 2;
            int y = baseY + (step % 2 == 0 ? offset : -offset);
            pos.Set(x, y, z);
            Block fluid = blocks.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid == null || fluid.Id == 0 || fluid.BlockMaterial != EnumBlockMaterial.Water)
            {
                continue;
            }

            AssetLocation ambient = fluid.Sounds?.Ambient;
            string sound = ambient?.Path;
            if (sound == null)
            {
                continue;
            }

            if (Still)
            {
                if (!Contains(sound, WaveSound) || !TryGetStillSurface(blocks, x, y, z, playerPos.Dimension, out kind))
                {
                    continue;
                }

                py = y + 1.0 + HeightAboveSurface;
            }
            else if (Contains(sound, FallSound))
            {
                if (!TryGetOpenSide(blocks, x, y, z, playerPos.Dimension, out BlockFacing side))
                {
                    continue;  // buried in its own fall: the sound is where the air is
                }

                kind = Fall;
                px = x + 0.5 + (side.Normali.X * FallSideOffset);
                py = y + 0.5;
                pz = z + 0.5 + (side.Normali.Z * FallSideOffset);
            }
            else if (Contains(sound, CreekSound) || Contains(sound, RapidsSound))
            {
                // Vanilla's own rule for running water: the surface of it is what you hear.
                pos.Set(x, y + 1, z);
                if (blocks.GetBlock(pos)?.Id != 0)
                {
                    continue;
                }

                kind = Contains(sound, RapidsSound) ? Rapids : Creek;
                py = y + 1.0 + HeightAboveSurface;
            }
            else
            {
                continue;  // still water, which the other field has
            }

            soundsByKind[kind] ??= ambient;
            return true;
        }

        return false;
    }

    protected override ILoadedSound CreateSound(in EmitterCell cell, float intensity, out int variant)
    {
        variant = cell.Kind;
        AssetLocation location = soundsByKind[cell.Kind];
        if (location == null)
        {
            return null;
        }

        return capi.World.LoadSound(new SoundParams
        {
            Location = location,
            Position = new Vec3f((float)cell.X, (float)cell.Y, (float)cell.Z),
            RelativePosition = false,
            Range = Still ? 24f : 16f,
            SoundType = EnumSoundType.Ambient,
            // Open water is the same recording a shade lower than the lapping at its edge.
            Pitch = (cell.Kind == OpenWater ? 0.96f : 1f) + ((float)random.NextDouble() * 0.06f),
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    /// <summary>Water lapping at land is the sound; a fall is louder than the creek it feeds.</summary>
    protected override float VolumeOf(int variant, in EmitterCell cell, float intensity) => Still
        ? Math.Max(0f, Config.WaterWaveVolume) * (variant == Shore ? 1f : 0.6f)
        : Math.Max(0f, Config.FlowingWaterVolume) * (variant == Fall ? 1f : variant == Rapids ? 0.85f : 0.7f);

    private static bool Contains(string path, string sound) =>
        path.Contains(sound, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this still water is a surface open to the sky, and whether land stands beside it.
    /// A cistern, a well or a flooded cave is under a roof: not weather, and not waves.
    /// </summary>
    private static bool TryGetStillSurface(IBlockAccessor blocks, int x, int y, int z, int dimension, out int kind)
    {
        kind = OpenWater;
        var pos = new BlockPos(x, y + 1, z, dimension);
        if (blocks.GetBlock(pos)?.Id != 0 || blocks.GetRainMapHeightAt(x, z) > y)
        {
            return false;  // water under water or under a block, or roofed over
        }

        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            pos.Set(x + facing.Normali.X, y, z + facing.Normali.Z);
            if (IsSolid(blocks.GetBlock(pos, BlockLayersAccess.MostSolid)))
            {
                kind = Shore;  // the edge, where the sound is made
                return true;
            }
        }

        return true;
    }

    /// <summary>The side of a falling block that is open air, which is vanilla's rule for one.</summary>
    private static bool TryGetOpenSide(IBlockAccessor blocks, int x, int y, int z, int dimension, out BlockFacing side)
    {
        side = null;
        var pos = new BlockPos(0, 0, 0, dimension);
        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            pos.Set(x + facing.Normali.X, y, z + facing.Normali.Z);
            if (IsOpenAir(blocks.GetBlock(pos)) && blocks.GetBlock(pos, BlockLayersAccess.Fluid)?.Id == 0)
            {
                side = facing;
                return true;
            }
        }

        return false;
    }
}
