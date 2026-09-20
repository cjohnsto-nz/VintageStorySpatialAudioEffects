using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>
/// Water from the water: an emitter on the surface in every cell of the grid, louder where the
/// water meets land.
/// <para>
/// Vanilla makes one sound for every water block in a 32-block section, merges the sections into
/// one box that can span a whole lake, and plays it from the point of that box nearest the
/// listener. A lake is then a single point a stride away, wherever you stand, and walking along the
/// shore drags it with you: the sound is never where the water is. Here the water sounds from the
/// water, near and far at once, and the audio engine has real positions to work with - a lake
/// behind a headland is muffled by the headland.
/// </para>
/// </summary>
internal sealed class WaterWaveField : EmitterField
{
    /// <summary>Vanilla's ambient sound for open water; the block that plays it is what we want.</summary>
    internal const string WaveSound = "waterwaves";

    /// <summary>Surfaces this far above or below the listener still count as theirs to hear.</summary>
    private const int VerticalReach = 10;

    /// <summary>Just above the water, which stands a little under the top of its block.</summary>
    private const double HeightAboveSurface = 0.05;

    // Kinds: what the water is doing here.
    private const int OpenWater = 0;
    private const int Shore = 1;

    public WaterWaveField(ICoreClientAPI capi)
        : base(capi)
    {
    }

    public override string Name => "water";

    private static SurroundWeatherConfig Config => SurroundWeatherConfigManager.Current;

    /// <summary>Whatever the water hereabouts plays, taken from the first block seen.</summary>
    private AssetLocation waveSound;

    /// <summary>
    /// Water is a broad, even thing: a few emitters well apart read as a lake, where many close
    /// together read as a wall of water standing around the listener.
    /// </summary>
    protected override double NearSpacing => Math.Max(2.0, Config.WaterWaveSpacing);

    protected override double Radius => Math.Max(8.0, Config.WaterWaveRadius);

    protected override double MinRadius => 3.0;

    /// <summary>Waves are slow; a slice wants to be long enough to be one.</summary>
    protected override double LifetimeSeconds => 6.0;

    protected override int MaxCount(float intensity) => Math.Max(1, Config.WaterWaveCount);

    protected override bool TryGetIntensity(Entity player, out float intensity)
    {
        // Water is not weather: it is there or it is not, and how much of it is around the
        // listener - how many cells find some - is what makes a lake louder than a pond.
        intensity = 1f;
        return Config.ExperimentalWaterWaveEmitters;
    }

    protected override bool TryGetCell(IBlockAccessor blocks, int x, int z, int size, EntityPos playerPos,
                                       out double px, out double py, out double pz, out int kind)
    {
        px = x + 0.5;
        py = 0;
        pz = z + 0.5;
        kind = OpenWater;

        // The water surface nearest the listener's own level, so a lake below a cliff is below
        // them and a flooded cave above them is above, rather than whatever the sky can see.
        int baseY = (int)Math.Floor(playerPos.Y);
        var pos = new BlockPos(0, 0, 0, playerPos.Dimension);
        for (int step = 0; step <= VerticalReach * 2; step++)
        {
            // The listener's own level first, then a block below, a block above, and outward.
            int offset = (step + 1) / 2;
            int y = baseY + (step % 2 == 0 ? offset : -offset);
            pos.Set(x, y, z);
            if (!IsWaves(blocks.GetBlock(pos, BlockLayersAccess.Fluid)))
            {
                continue;
            }

            pos.Set(x, y + 1, z);
            if (blocks.GetBlock(pos)?.Id != 0)
            {
                continue;  // water under water, or under a block: not a surface
            }

            if (blocks.GetRainMapHeightAt(x, z) > y)
            {
                continue;  // roofed over: a cistern, a well, a flooded cave. Not weather, not waves
            }

            px = x + 0.5;
            py = y + 1.0 + HeightAboveSurface;
            pz = z + 0.5;
            kind = HasShore(blocks, x, y, z, playerPos.Dimension) ? Shore : OpenWater;
            return true;
        }

        return false;
    }

    protected override ILoadedSound CreateSound(in EmitterCell cell, float intensity, out int variant)
    {
        variant = cell.Kind;
        if (waveSound == null)
        {
            return null;
        }

        return capi.World.LoadSound(new SoundParams
        {
            Location = waveSound,
            Position = new Vec3f((float)cell.X, (float)cell.Y, (float)cell.Z),
            RelativePosition = false,
            Range = 24f,
            SoundType = EnumSoundType.Ambient,
            // Open water is the same recording a shade lower than the lapping at its edge.
            Pitch = (cell.Kind == Shore ? 1f : 0.96f) + ((float)random.NextDouble() * 0.06f),
            Volume = 0f,
            ShouldLoop = false,
            DisposeOnFinish = false,
        });
    }

    /// <summary>Water lapping at land is the sound; open water away from it is the bed of it.</summary>
    protected override float VolumeOf(int variant, in EmitterCell cell, float intensity) =>
        Math.Max(0f, Config.WaterWaveVolume) * (variant == Shore ? 1f : 0.6f);

    /// <summary>Whether this is open water: the still water that plays vanilla's waves.</summary>
    private bool IsWaves(Block fluid)
    {
        if (fluid == null || fluid.Id == 0 || fluid.BlockMaterial != EnumBlockMaterial.Water)
        {
            return false;
        }

        AssetLocation ambient = fluid.Sounds?.Ambient;
        if (ambient?.Path?.Contains(WaveSound, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;  // a creek or a waterfall: its own sound, and vanilla places it well enough
        }

        waveSound ??= ambient;
        return true;
    }

    /// <summary>Whether land stands beside this water: the edge, where the sound is made.</summary>
    private static bool HasShore(IBlockAccessor blocks, int x, int y, int z, int dimension)
    {
        var pos = new BlockPos(0, 0, 0, dimension);
        foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
        {
            pos.Set(x + facing.Normali.X, y, z + facing.Normali.Z);
            if (IsSolid(blocks.GetBlock(pos, BlockLayersAccess.MostSolid)))
            {
                return true;
            }
        }

        return false;
    }
}
