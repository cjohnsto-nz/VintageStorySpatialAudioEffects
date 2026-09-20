using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>What a field puts in one grid cell.</summary>
/// <param name="Key">The cell: one emitter per key.</param>
/// <param name="Kind">What is there, in the field's own terms (a canopy, a reed).</param>
/// <param name="Gain">The cell's share of the level: a far cell stands for more ground than a near one.</param>
internal readonly record struct EmitterCell(long Key, double X, double Y, double Z, int Kind, float Gain);

/// <summary>What the debug overlay draws for one emitter.</summary>
internal readonly record struct EmitterVisual(Vec3d Position, float Volume, bool FadingOut);

/// <summary>
/// A field of positional sounds on a deterministic grid around the listener: rain on the ground,
/// leaves in the wind. The grid is fixed to the world and comes in rings: cells of the near
/// spacing out to three of them around the listener, and twice the size in every ring further out. Every cell in range
/// holds exactly one emitter, so coverage is even, dense where it matters, and the same every time.
/// <list type="bullet">
///   <item><b>It leads the listener</b>: the rings are centred ahead along the listener's velocity,
///         so running does not outrun the sound. Cells that leave the rings fade at once, and
///         cells that enter are filled nearest first, a few per step.</item>
///   <item><b>Nothing loops</b>: a cell plays one slice of a recording, then the next slice
///         crossfades in at the same spot. Lives are spread, so cells never change in step.</item>
///   <item><b>Fades are equal-power and run per frame</b>: the game's own fades are linear in dB,
///         nearly silent for most of their length, and crossfading with them dips.</item>
///   <item><b>Cells behind rock are left empty</b> (optional), and emptied when rock comes between.</item>
/// </list>
/// A field says when it is active and how strongly, what is in a cell, and what that sounds like.
/// </summary>
internal abstract class EmitterField : IDisposable
{
    private const int FrameTickMs = 20;
    private const double LogicStepSeconds = 0.1;
    private const long CellRefreshMs = 1000;
    private const double CellMoveRefresh = 1.0;
    private const int MaxSpawnsPerStep = 6;
    private const float FastFadeSeconds = 0.4f;
    private const long OcclusionCacheMs = 1000;
    private const double VelocitySmoothingSeconds = 0.4;
    private const double MovingSpeed = 0.5;
    /// <summary>
    /// Solid blocks in the way that a cell may still be played through. A roof and its rafters
    /// are a few; the depth of a cave is many more. The audio engine muffles what is played.
    /// </summary>
    protected const int BlockedLimit = 4;

    protected readonly ICoreClientAPI capi;
    protected readonly Random random = new();

    private readonly List<Emitter> emitters = new();
    private readonly Dictionary<long, Emitter> liveByCell = new();
    private readonly List<EmitterCell> cells = new();
    private readonly HashSet<long> wantedKeys = new();
    private readonly Dictionary<long, (bool Blocked, long AtMs)> occlusion = new();
    private readonly object emittersLock = new();
    private readonly Vec3d lastPosition = new();
    private readonly Vec3d lastCellCentre = new();
    private long tickListenerId;
    private bool hasLastPosition;
    private bool hasCells;
    private long lastCellRefreshMs;
    private long lastStateLogMs;
    private double velocityX;
    private double velocityZ;
    private double logicAccumulator;
    private float lastIntensity;

    protected EmitterField(ICoreClientAPI capi)
    {
        this.capi = capi;
        tickListenerId = capi.Event.RegisterGameTickListener(OnFrame, FrameTickMs);
    }

    // ---- what a field is ----

    /// <summary>What this field is, for the status command.</summary>
    public abstract string Name { get; }

    /// <summary>What it is doing now.</summary>
    public string Describe()
    {
        lock (emittersLock)
        {
            int playing = 0;
            foreach (Emitter emitter in emitters)
            {
                playing += emitter.Retiring ? 0 : 1;
            }

            return string.Format(
                "{0}: {1} playing, {2} fading, {3} cells wanted, intensity {4:0.00}",
                Name, playing, emitters.Count - playing, cells.Count, lastIntensity);
        }
    }

    /// <summary>Whether the field plays now, and how strongly (0..1): the rainfall, the wind.</summary>
    protected abstract bool TryGetIntensity(Entity player, out float intensity);

    /// <summary>The most emitters at once; the nearest cells win.</summary>
    protected abstract int MaxCount(float intensity);

    /// <summary>Blocks between cells around the listener; every ring outward doubles it.</summary>
    protected abstract double NearSpacing { get; }

    /// <summary>How far out (blocks) the rings go.</summary>
    protected abstract double Radius { get; }

    /// <summary>No cell nearer the listener's head than this plays (in any direction).</summary>
    protected virtual double MinRadius => 1.5;

    /// <summary>Seconds a slice plays before the next takes over (each gets half to one and a half of it).</summary>
    protected abstract double LifetimeSeconds { get; }

    protected virtual float FadeSeconds => 1.0f;

    /// <summary>Seconds of the listener's travel the rings are centred ahead by.</summary>
    protected virtual double LeadSeconds => 1.5;

    /// <summary>
    /// Cells double in size in every ring outward (rain and wind cover ground, and the far side of
    /// it needs less). False keeps one size throughout: for small things that each want their own
    /// emitter, such as a window pane.
    /// </summary>
    protected virtual bool RingsWiden => true;

    /// <summary>Write what this field is doing to the log every few seconds, to track a fault down.</summary>
    protected virtual bool LogState => false;

    /// <summary>Leave cells with more than BlockedLimit solid blocks in the way empty.</summary>
    protected virtual bool CullOccluded => true;

    /// <summary>1 starts a slice anywhere it fits in the recording; 0 always at its start (a splash has an attack).</summary>
    protected virtual float RandomStartFraction => 1f;

    /// <summary>
    /// Where this field plays in the cell whose centre column is (<paramref name="x"/>,
    /// <paramref name="z"/>) and which is <paramref name="size"/> blocks wide, or false if nothing
    /// of it is there. Must give the same answer for the same cell.
    /// </summary>
    protected abstract bool TryGetCell(IBlockAccessor blocks, int x, int z, int size, EntityPos playerPos,
                                       out double px, out double py, out double pz, out int kind);

    /// <summary>The sound for a cell: created, not started. <paramref name="variant"/> is the field's note of what it chose.</summary>
    protected abstract ILoadedSound CreateSound(in EmitterCell cell, float intensity, out int variant);

    /// <summary>How loud an emitter in a near cell should be now (the cell's gain is applied on top).</summary>
    protected abstract float VolumeOf(int variant, in EmitterCell cell, float intensity);

    /// <summary>True when the weather has moved on from what this emitter plays: its cell takes a new slice.</summary>
    protected virtual bool IsStale(int variant, int kind, float intensity) => false;

    /// <summary>How much louder a cell of ring <paramref name="level"/> plays than a near one (it stands for more ground).</summary>
    protected virtual float RingGain(int level) => level switch
    {
        0 => 0.45f,
        1 => 0.7f,
        _ => 1f,
    };

    // ---- shared machinery ----

    public void Dispose()
    {
        if (tickListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }

        lock (emittersLock)
        {
            foreach (Emitter emitter in emitters)
            {
                Release(emitter);
            }

            emitters.Clear();
            liveByCell.Clear();
        }
    }

    public List<EmitterVisual> GetSnapshot()
    {
        lock (emittersLock)
        {
            var snapshot = new List<EmitterVisual>(emitters.Count);
            foreach (Emitter emitter in emitters)
            {
                snapshot.Add(new EmitterVisual(new Vec3d(emitter.Cell.X, emitter.Cell.Y, emitter.Cell.Z), emitter.Applied, emitter.Retiring));
            }

            return snapshot;
        }
    }

    private void OnFrame(float deltaSeconds)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.25f);
        lock (emittersLock)
        {
            UpdateFades(deltaSeconds);
            logicAccumulator += deltaSeconds;
            if (logicAccumulator < LogicStepSeconds)
            {
                return;
            }

            double step = logicAccumulator;
            logicAccumulator = 0;
            Step(step);
        }
    }

    /// <summary>Every frame: each emitter's level along its equal-power fade.</summary>
    private void UpdateFades(float deltaSeconds)
    {
        for (int i = emitters.Count - 1; i >= 0; i--)
        {
            Emitter emitter = emitters[i];
            if (emitter.Sound.IsDisposed)
            {
                Forget(emitter);
                emitters.RemoveAt(i);
                continue;
            }

            if (emitter.Retiring)
            {
                emitter.Level -= deltaSeconds / Math.Max(0.05f, emitter.RetireSeconds);
                if (emitter.Level <= 0f)
                {
                    Release(emitter);
                    emitters.RemoveAt(i);
                    continue;
                }
            }
            else if (emitter.Level < 1f)
            {
                emitter.Level = Math.Min(1f, emitter.Level + (deltaSeconds / Math.Max(0.05f, FadeSeconds)));
            }

            float applied = emitter.TargetVolume * MathF.Sin(emitter.Level * MathF.PI / 2f);
            if (Math.Abs(applied - emitter.Applied) > 0.002f || (applied == 0f && emitter.Applied != 0f))
            {
                emitter.Applied = applied;
                emitter.Sound.SetVolume(applied);
            }
        }
    }

    private void Step(double stepSeconds)
    {
        long nowMs = capi.ElapsedMilliseconds;
        Entity player = capi.World?.Player?.Entity;
        if (player?.Pos == null || !TryGetIntensity(player, out float intensity))
        {
            foreach (Emitter emitter in emitters)
            {
                Retire(emitter, FadeSeconds);
            }

            lastIntensity = 0f;
            hasLastPosition = false;
            hasCells = false;
            return;
        }

        lastIntensity = intensity;
        EntityPos playerPos = player.Pos;
        (double dirX, double dirZ, double speed) = UpdateMotion(playerPos, stepSeconds);
        double radius = Math.Max(NearSpacing * 2.0, Radius);
        double lead = Math.Min(speed * LeadSeconds, radius * 0.6);
        var centre = new Vec3d(playerPos.X + (dirX * lead), playerPos.Y, playerPos.Z + (dirZ * lead));
        Vec3d ears = new(playerPos.X + player.LocalEyePos.X, playerPos.Y + player.LocalEyePos.Y, playerPos.Z + player.LocalEyePos.Z);

        RefreshCells(centre, radius, playerPos, ears, intensity, nowMs);

        // Cells that left the rings (or went behind rock) empty at once; the rest follow the weather.
        foreach (Emitter emitter in emitters)
        {
            if (emitter.Retiring)
            {
                continue;
            }

            if (!wantedKeys.Contains(emitter.Cell.Key))
            {
                Retire(emitter, FastFadeSeconds);
                continue;
            }

            emitter.TargetVolume = GameMath.Clamp(VolumeOf(emitter.Variant, emitter.Cell, intensity) * emitter.Cell.Gain, 0f, 1f);
        }

        // Empty cells fill nearest first; then cells whose slice has run its life take the next
        // one, crossfading in place. A few per step, so nothing starts in step.
        if (LogState && nowMs - lastStateLogMs > 5000)
        {
            lastStateLogMs = nowMs;
            var where = new List<string>();
            foreach (EmitterVisual visual in GetSnapshot())
            {
                where.Add(string.Format(
                    CultureInfo.InvariantCulture, "({0:0.0},{1:0.0},{2:0.0}){3}",
                    visual.Position.X, visual.Position.Y, visual.Position.Z, visual.FadingOut ? " fading" : ""));
            }

            capi.Logger.Notification("[surroundweather] {0} | {1}", Describe(), string.Join(" ", where));
        }

        int spawned = 0;
        foreach (EmitterCell cell in cells)
        {
            if (spawned >= MaxSpawnsPerStep)
            {
                break;
            }

            if (!liveByCell.ContainsKey(cell.Key) && Spawn(cell, intensity, nowMs))
            {
                spawned++;
            }
        }

        foreach (EmitterCell cell in cells)
        {
            if (spawned >= MaxSpawnsPerStep)
            {
                break;
            }

            if (!liveByCell.TryGetValue(cell.Key, out Emitter playing))
            {
                continue;
            }

            bool over = playing.ExpiresMs <= nowMs
                || (nowMs - playing.BornMs > 500 && playing.Sound.HasStopped)
                || IsStale(playing.Variant, cell.Kind, intensity);
            if (!over)
            {
                continue;
            }

            Retire(playing, FadeSeconds);
            if (Spawn(cell, intensity, nowMs))
            {
                spawned++;
            }
        }
    }

    /// <summary>
    /// The cells that should be playing: every cell of every ring around the centre that holds
    /// something, is not at the listener's head or behind rock, nearest first, up to MaxCount.
    /// </summary>
    private void RefreshCells(Vec3d centre, double radius, EntityPos playerPos, Vec3d ears, float intensity, long nowMs)
    {
        if (hasCells && nowMs - lastCellRefreshMs < CellRefreshMs
            && lastCellCentre.SquareDistanceTo(centre.X, centre.Y, centre.Z) < CellMoveRefresh * CellMoveRefresh)
        {
            return;
        }

        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return;
        }

        cells.Clear();
        int nearSize = Math.Max(1, (int)Math.Round(NearSpacing));
        if (!RingsWiden)
        {
            AddRing(blocks, centre, 0.0, radius, nearSize, 0, playerPos, ears, nowMs);
        }
        else
        {
            double inner = 0.0;
            for (int level = 0; inner < radius && level < 6; level++)
            {
                int size = nearSize << level;
                double outer = Math.Min(radius, nearSize * 3.0 * (1 << level));
                if (level == 5)
                {
                    outer = radius;
                }

                AddRing(blocks, centre, inner, outer, size, level, playerPos, ears, nowMs);
                inner = outer;
            }
        }

        cells.Sort((left, right) => DistanceSq(left, ears).CompareTo(DistanceSq(right, ears)));
        int max = Math.Max(0, MaxCount(intensity));
        if (cells.Count > max)
        {
            cells.RemoveRange(max, cells.Count - max);
        }

        wantedKeys.Clear();
        foreach (EmitterCell cell in cells)
        {
            wantedKeys.Add(cell.Key);
        }

        if (occlusion.Count > 4096)
        {
            occlusion.Clear();
        }

        lastCellRefreshMs = nowMs;
        lastCellCentre.Set(centre.X, centre.Y, centre.Z);
        hasCells = true;
    }

    /// <summary>The cells of one size whose centres lie between two radii of the centre.</summary>
    private void AddRing(IBlockAccessor blocks, Vec3d centre, double inner, double outer, int size, int level, EntityPos playerPos, Vec3d ears, long nowMs)
    {
        // Cells are fixed to the world (multiples of their size), so they stay put as the listener moves.
        int minCellX = (int)Math.Floor((centre.X - outer) / size);
        int maxCellX = (int)Math.Floor((centre.X + outer) / size);
        int minCellZ = (int)Math.Floor((centre.Z - outer) / size);
        int maxCellZ = (int)Math.Floor((centre.Z + outer) / size);
        double minRadiusSq = MinRadius * MinRadius;
        float gain = RingGain(level);
        for (int cellX = minCellX; cellX <= maxCellX; cellX++)
        {
            for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            {
                int x = (cellX * size) + (size / 2);
                int z = (cellZ * size) + (size / 2);
                double dx = x + 0.5 - centre.X;
                double dz = z + 0.5 - centre.Z;
                double distanceSq = (dx * dx) + (dz * dz);
                if (distanceSq < inner * inner || distanceSq >= outer * outer)
                {
                    continue;
                }

                if (!TryGetCell(blocks, x, z, size, playerPos, out double ex, out double ey, out double ez, out int kind))
                {
                    continue;
                }

                double px = ex - ears.X;
                double py = ey - ears.Y;
                double pz = ez - ears.Z;
                if ((px * px) + (py * py) + (pz * pz) < minRadiusSq)
                {
                    continue;
                }

                long key = ((long)level << 58) | ((long)(cellX & 0x1FFFFFF) << 29) | (long)(cellZ & 0x1FFFFFF);
                if (CullOccluded && IsBlocked(key, ears, ex, ey, ez, nowMs))
                {
                    continue;
                }

                cells.Add(new EmitterCell(key, ex, ey, ez, kind, gain));
            }
        }
    }

    /// <summary>Whether rock lies between the listener and a cell; looked at once a second per cell.</summary>
    private bool IsBlocked(long key, Vec3d ears, double x, double y, double z, long nowMs)
    {
        if (occlusion.TryGetValue(key, out (bool Blocked, long AtMs) known) && nowMs - known.AtMs < OcclusionCacheMs)
        {
            return known.Blocked;
        }

        bool blocked = BlocksBetween(ears, x, y, z) > BlockedLimit;
        occlusion[key] = (blocked, nowMs);
        return blocked;
    }

    private static double DistanceSq(in EmitterCell cell, Vec3d ears)
    {
        double dx = cell.X - ears.X;
        double dy = cell.Y - ears.Y;
        double dz = cell.Z - ears.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>One slice of a recording in a cell: never a loop, started anywhere it fits.</summary>
    private bool Spawn(in EmitterCell cell, float intensity, long nowMs)
    {
        ILoadedSound sound = CreateSound(cell, intensity, out int variant);
        if (sound == null)
        {
            return false;
        }

        double lifetime = Math.Max(0.5, LifetimeSeconds) * (0.5 + random.NextDouble());
        float length = sound.SoundLengthSeconds;
        if (length > 0.2f)
        {
            lifetime = Math.Min(lifetime, Math.Max(0.5, length - FadeSeconds - 0.1));
        }

        sound.SetVolume(0f);
        sound.Start();
        double room = length - lifetime - FadeSeconds - 0.1;
        if (RandomStartFraction > 0f && room > 0.05)
        {
            sound.PlaybackPosition = (float)(random.NextDouble() * RandomStartFraction * room);
        }

        var emitter = new Emitter(sound, cell)
        {
            Variant = variant,
            TargetVolume = GameMath.Clamp(VolumeOf(variant, cell, intensity) * cell.Gain, 0f, 1f),
            BornMs = nowMs,
            ExpiresMs = nowMs + (long)(lifetime * 1000.0),
        };
        emitters.Add(emitter);
        liveByCell[cell.Key] = emitter;
        return true;
    }

    private void Retire(Emitter emitter, float seconds)
    {
        Forget(emitter);
        if (emitter.Retiring)
        {
            emitter.RetireSeconds = Math.Min(emitter.RetireSeconds, seconds);
            return;
        }

        emitter.Retiring = true;
        emitter.RetireSeconds = seconds;
    }

    /// <summary>The emitter no longer holds its cell (it may still be fading out there).</summary>
    private void Forget(Emitter emitter)
    {
        if (liveByCell.TryGetValue(emitter.Cell.Key, out Emitter holder) && ReferenceEquals(holder, emitter))
        {
            liveByCell.Remove(emitter.Cell.Key);
        }
    }

    private static void Release(Emitter emitter)
    {
        if (!emitter.Sound.IsDisposed)
        {
            emitter.Sound.Stop();
            emitter.Sound.Dispose();
        }
    }

    /// <summary>The listener's smoothed velocity: the way they go and how fast (0 when standing).</summary>
    private (double DirX, double DirZ, double Speed) UpdateMotion(EntityPos pos, double stepSeconds)
    {
        if (hasLastPosition && stepSeconds > 1e-4)
        {
            double vx = (pos.X - lastPosition.X) / stepSeconds;
            double vz = (pos.Z - lastPosition.Z) / stepSeconds;
            if ((vx * vx) + (vz * vz) > 40.0 * 40.0)
            {
                vx = vz = 0;  // teleported
            }

            double blend = 1.0 - Math.Exp(-stepSeconds / VelocitySmoothingSeconds);
            velocityX += (vx - velocityX) * blend;
            velocityZ += (vz - velocityZ) * blend;
        }

        lastPosition.Set(pos.X, pos.Y, pos.Z);
        hasLastPosition = true;
        double speed = Math.Sqrt((velocityX * velocityX) + (velocityZ * velocityZ));
        return speed > MovingSpeed ? (velocityX / speed, velocityZ / speed, speed) : (0, 0, 0);
    }

    /// <summary>
    /// Solid blocks between the listener and a spot, counted up to BlockedLimit + 1. Leaves, water
    /// and plants let sound and rain through; the two ends' own blocks are not counted.
    /// </summary>
    protected int BlocksBetween(Vec3d from, double toX, double toY, double toZ)
    {
        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return 0;
        }

        double dx = toX - from.X;
        double dy = toY - from.Y;
        double dz = toZ - from.Z;
        double distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (distance < 1e-3)
        {
            return 0;
        }

        int steps = (int)Math.Ceiling(distance / 0.5);
        int lastX = int.MinValue;
        int lastY = int.MinValue;
        int lastZ = int.MinValue;
        int endX = (int)Math.Floor(toX);
        int endY = (int)Math.Floor(toY);
        int endZ = (int)Math.Floor(toZ);
        int blocked = 0;
        var pos = new BlockPos(0, 0, 0);
        for (int i = 1; i < steps; i++)
        {
            double t = i / (double)steps;
            int x = (int)Math.Floor(from.X + (dx * t));
            int y = (int)Math.Floor(from.Y + (dy * t));
            int z = (int)Math.Floor(from.Z + (dz * t));
            if ((x == lastX && y == lastY && z == lastZ) || (x == endX && y == endY && z == endZ))
            {
                continue;
            }

            lastX = x;
            lastY = y;
            lastZ = z;
            pos.Set(x, y, z);
            if (IsSolid(blocks.GetBlock(pos)) && ++blocked > BlockedLimit)
            {
                return blocked;
            }
        }

        return blocked;
    }

    /// <summary>
    /// Space a sound can stand in. Thatch and leaves let the rain through, so they are not solid
    /// to the weather and <see cref="IsSolid"/> passes them over - but they are still a block, and
    /// an emitter inside one is a sound coming out of the roof.
    /// </summary>
    protected static bool IsOpenAir(Block block) =>
        block != null && (block.Id == 0 || block.Replaceable >= 6000);

    protected static bool IsSolid(Block block) =>
        block != null
        && block.Id != 0
        && !block.RainPermeable
        && block.Replaceable < 6000
        && block.BlockMaterial != EnumBlockMaterial.Air
        && block.BlockMaterial != EnumBlockMaterial.Water
        && block.BlockMaterial != EnumBlockMaterial.Plant
        && block.BlockMaterial != EnumBlockMaterial.Leaves;

    private sealed class Emitter(ILoadedSound sound, EmitterCell cell)
    {
        public ILoadedSound Sound { get; } = sound;

        public EmitterCell Cell { get; } = cell;

        public int Variant { get; init; }

        public long BornMs { get; init; }

        public long ExpiresMs { get; init; }

        public float TargetVolume { get; set; }

        /// <summary>0..1 along the fade; the volume applied is the target times sin(level * pi / 2).</summary>
        public float Level { get; set; }

        public float Applied { get; set; }

        public bool Retiring { get; set; }

        public float RetireSeconds { get; set; }
    }
}
