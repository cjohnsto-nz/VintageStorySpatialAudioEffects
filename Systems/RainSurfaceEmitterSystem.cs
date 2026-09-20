using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>
/// Experimental: rain as looping emitters on the surfaces it lands on, instead of a bed at the
/// listener. Each emitter sits on top of a column's rain-blocking block, which is the ground
/// outside, the roof over a building, and the canopy of a tree.
/// <para>
/// Placement, not muffling, decides what you hear. Columns on a fixed grid around you are scored
/// by how clear the way from your ears to them is and how near your level they are, so rain is
/// heard where it can actually reach you: from every side in the open, and from the opening alone
/// in a cave. Emitters that end up behind rock are dropped and their places given to spots that
/// are not.
/// </para>
/// </summary>
internal sealed class RainSurfaceEmitterSystem : IDisposable
{
    private const int TickMs = 250;
    private const float FadeSeconds = 1.2f;
    /// <summary>A blocked emitter is not worth hearing out: it goes quickly.</summary>
    private const float StopFadeSeconds = 0.5f;
    private const double MinRadius = 2.0;
    /// <summary>A surface further above or below than this is another world (a cave roof, a cliff).</summary>
    private const double MaxVerticalOffset = 20.0;
    private const float Range = 16f;
    private const float BaseVolume = 0.55f;
    private const int MaxSetChangesPerTick = 2;
    private const float MediumRainFrom = 0.35f;
    private const float HeavyRainFrom = 0.7f;

    /// <summary>Columns are looked at every this many blocks: a fixed grid, so placement repeats.</summary>
    private const int CandidateStep = 2;
    private const long CandidateRefreshMs = 500;
    private const double CandidateMoveRefresh = 2.0;
    /// <summary>The best of the grid get the (dearer) look along the line from the listener.</summary>
    private const int MaxCandidatesTested = 56;
    /// <summary>Solid blocks in the way that a spot may still be played through. More: rain you would not hear.</summary>
    private const int BlockedLimit = 2;
    private const int Octants = 8;

    private readonly ICoreClientAPI capi;
    private readonly Random random = new();
    private readonly List<Emitter> emitters = new();
    private readonly List<Emitter> fading = new();
    private readonly List<Candidate> candidates = new();
    private readonly List<Candidate> rough = new();
    private readonly object snapshotLock = new();
    private long tickListenerId;
    private long lastCandidateRefreshMs;
    private readonly Vec3d lastCandidateCenter = new();
    private bool hasCandidateCenter;

    public RainSurfaceEmitterSystem(ICoreClientAPI capi)
    {
        this.capi = capi;
        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, TickMs);
    }

    public void Dispose()
    {
        if (tickListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = 0;
        }

        lock (snapshotLock)
        {
            foreach (Emitter emitter in emitters)
            {
                Release(emitter);
            }

            foreach (Emitter emitter in fading)
            {
                Release(emitter);
            }

            emitters.Clear();
            fading.Clear();
        }
    }

    /// <summary>Where the rain is playing from now, for the debug overlay.</summary>
    public List<RainSurfaceEmitterVisual> GetActiveEmittersSnapshot()
    {
        lock (snapshotLock)
        {
            var snapshot = new List<RainSurfaceEmitterVisual>(emitters.Count + fading.Count);
            foreach (Emitter emitter in emitters)
            {
                snapshot.Add(new RainSurfaceEmitterVisual(new Vec3d(emitter.X, emitter.Y, emitter.Z), emitter.Volume, false));
            }

            foreach (Emitter emitter in fading)
            {
                snapshot.Add(new RainSurfaceEmitterVisual(new Vec3d(emitter.X, emitter.Y, emitter.Z), emitter.Volume, true));
            }

            return snapshot;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (snapshotLock)
            {
                return emitters.Count;
            }
        }
    }

    private void OnGameTick(float deltaTime)
    {
        long nowMs = capi.ElapsedMilliseconds;
        DisposeFaded(nowMs);

        SurroundWeatherConfig config = SurroundWeatherConfigManager.Current;
        Entity player = capi.World?.Player?.Entity;
        if (!config.ExperimentalRainSurfaceEmitters || player?.Pos == null)
        {
            FadeOutAll(nowMs, StopFadeSeconds);
            return;
        }

        if (!WeatherState.TryGetRainfall(capi, out float rainfall))
        {
            FadeOutAll(nowMs, FadeSeconds);
            return;
        }

        Vec3d ears = Ears(player);
        float intensity = GameMath.Clamp(rainfall * 2f, 0f, 1f);
        float volume = GameMath.Clamp(BaseVolume * intensity * Math.Max(0f, config.RainSurfaceEmitterVolume), 0.01f, 1f);
        int wanted = (int)Math.Round(Math.Max(2, config.RainSurfaceEmitterCount) * (0.45f + (0.55f * intensity)));
        RainSampleSet wantedSet = SetForIntensity(intensity);

        UpdateLiveEmitters(player.Pos, ears, config, volume, wantedSet, wanted, nowMs);
        RefreshCandidates(player.Pos, ears, config, nowMs);
        Fill(player.Pos, volume, wantedSet, config, wanted);
    }

    /// <summary>Range, occlusion, the set the weather asks for, and the volume it asks for.</summary>
    private void UpdateLiveEmitters(EntityPos playerPos, Vec3d ears, SurroundWeatherConfig config, float volume, RainSampleSet wantedSet, int wanted, long nowMs)
    {
        double keep = Math.Max(MinRadius + 1.0, config.RainSurfaceEmitterRadius) * 1.6;
        double keepSq = keep * keep;
        lock (snapshotLock)
        {
            int changed = 0;
            for (int i = emitters.Count - 1; i >= 0; i--)
            {
                Emitter emitter = emitters[i];
                if (emitter.Sound.IsDisposed)
                {
                    emitters.RemoveAt(i);
                    continue;
                }

                double dx = emitter.X - playerPos.X;
                double dz = emitter.Z - playerPos.Z;
                if ((dx * dx) + (dz * dz) > keepSq || Math.Abs(emitter.Y - playerPos.Y) > MaxVerticalOffset)
                {
                    BeginFade(emitter, nowMs, FadeSeconds);
                    emitters.RemoveAt(i);
                    continue;
                }

                // Has rock come between us? Walking into a cave puts every emitter behind stone,
                // and rain you cannot hear should not hold a place that a clear spot could take.
                if (BlocksBetween(ears, emitter.X, emitter.Y, emitter.Z) > BlockedLimit)
                {
                    BeginFade(emitter, nowMs, StopFadeSeconds);
                    emitters.RemoveAt(i);
                    continue;
                }

                // The rain has picked up or eased off: let a couple of emitters at a time take on
                // its new character. A canopy keeps its own patter whatever the weather does.
                if (emitter.Set != RainSampleSet.Canopy && emitter.Set != wantedSet && changed < MaxSetChangesPerTick)
                {
                    BeginFade(emitter, nowMs, FadeSeconds);
                    emitters.RemoveAt(i);
                    changed++;
                    continue;
                }

                emitter.Volume = volume;
                if (!emitter.Sound.IsFadingIn && !emitter.Sound.IsFadingOut)
                {
                    emitter.Sound.SetVolume(volume);
                }
            }

            while (emitters.Count > wanted)
            {
                BeginFade(emitters[^1], nowMs, FadeSeconds);
                emitters.RemoveAt(emitters.Count - 1);
            }
        }
    }

    /// <summary>
    /// Every column on the grid around the listener, by the rain height map, scored by how clear
    /// the way to it is and how near it is. Kept for half a second, or until the listener moves on.
    /// </summary>
    private void RefreshCandidates(EntityPos playerPos, Vec3d ears, SurroundWeatherConfig config, long nowMs)
    {
        if (hasCandidateCenter && nowMs - lastCandidateRefreshMs < CandidateRefreshMs
            && lastCandidateCenter.SquareDistanceTo(playerPos.X, playerPos.Y, playerPos.Z) < CandidateMoveRefresh * CandidateMoveRefresh)
        {
            return;
        }

        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return;
        }

        candidates.Clear();
        rough.Clear();
        double maxRadius = Math.Max(MinRadius + 1.0, config.RainSurfaceEmitterRadius);
        int reach = (int)Math.Ceiling(maxRadius);
        int baseX = (int)Math.Floor(playerPos.X);
        int baseZ = (int)Math.Floor(playerPos.Z);

        for (int dx = -reach; dx <= reach; dx += CandidateStep)
        {
            for (int dz = -reach; dz <= reach; dz += CandidateStep)
            {
                int x = baseX + dx;
                int z = baseZ + dz;
                double ex = x + 0.5;
                double ez = z + 0.5;
                double relX = ex - playerPos.X;
                double relZ = ez - playerPos.Z;
                double horizontal = Math.Sqrt((relX * relX) + (relZ * relZ));
                if (horizontal < MinRadius || horizontal > maxRadius)
                {
                    continue;
                }

                // The top of this column's rain-blocking block: the ground, a roof, or a canopy.
                int surfaceY = blocks.GetRainMapHeightAt(x, z);
                double ey = surfaceY + 1.0;
                double verticalOffset = Math.Abs(ey - playerPos.Y);
                if (verticalOffset > MaxVerticalOffset)
                {
                    continue;
                }

                // Near, and near your level, before anything is traced.
                double nearness = 1.0 - (horizontal / maxRadius);
                double level = 1.0 - (verticalOffset / MaxVerticalOffset);
                rough.Add(new Candidate(ex, ey, ez, x, surfaceY, z, Octant(relX, relZ), (nearness * 0.6) + (level * 0.4)));
            }
        }

        rough.Sort((left, right) => right.Score.CompareTo(left.Score));
        int tested = Math.Min(rough.Count, MaxCandidatesTested);
        for (int i = 0; i < tested; i++)
        {
            Candidate candidate = rough[i];
            int blocked = BlocksBetween(ears, candidate.X, candidate.Y, candidate.Z);
            if (blocked > BlockedLimit)
            {
                continue;  // rain we could not hear from here
            }

            // A clear way counts for most of it: in a cave that is the mouth, and little else.
            double openness = 1.0 - (blocked / (double)(BlockedLimit + 1));
            candidates.Add(candidate with { Score = (openness * 0.6) + (candidate.Score * 0.4) });
        }

        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        lastCandidateRefreshMs = nowMs;
        lastCandidateCenter.Set(playerPos.X, playerPos.Y, playerPos.Z);
        hasCandidateCenter = true;
    }

    /// <summary>
    /// Fills the free places from the best candidates, spreading them around the listener: the
    /// octant with the fewest emitters goes first, so the open sounds like every side at once and a
    /// cave like its mouth.
    /// </summary>
    private void Fill(EntityPos playerPos, float volume, RainSampleSet wantedSet, SurroundWeatherConfig config, int wanted)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        double spacing = Math.Max(0.5, config.RainSurfaceEmitterSpacing);
        var used = new HashSet<int>();
        int missing = wanted - ActiveCount;
        for (int placed = 0; placed < missing; placed++)
        {
            int[] perOctant = OccupiedOctants(playerPos);
            int best = -1;
            double bestScore = double.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (used.Contains(i))
                {
                    continue;
                }

                Candidate candidate = candidates[i];
                if (IsNearPlayingVoice(candidate.X, candidate.Y, candidate.Z, spacing))
                {
                    continue;
                }

                double score = candidate.Score - (0.12 * perOctant[candidate.Octant]);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
            {
                return;  // nowhere left that rain reaches: try again next tick
            }

            used.Add(best);
            Candidate chosen = candidates[best];
            Block surface = capi.World.BlockAccessor.GetBlock(new BlockPos(chosen.BlockX, chosen.SurfaceY, chosen.BlockZ, playerPos.Dimension));
            // What the rain lands on picks the sound as much as how hard it falls.
            RainSampleSet set = surface?.BlockMaterial == EnumBlockMaterial.Leaves ? RainSampleSet.Canopy : wantedSet;
            if (!Play(chosen.X, chosen.Y, chosen.Z, surface, set, volume))
            {
                return;
            }
        }
    }

    private int[] OccupiedOctants(EntityPos playerPos)
    {
        var counts = new int[Octants];
        lock (snapshotLock)
        {
            foreach (Emitter emitter in emitters)
            {
                counts[Octant(emitter.X - playerPos.X, emitter.Z - playerPos.Z)]++;
            }
        }

        return counts;
    }

    private bool Play(double x, double y, double z, Block surface, RainSampleSet set, float volume)
    {
        // Rain on water is brighter; otherwise a little random spread keeps the emitters from
        // ringing as one voice. The canopy set is already rain in leaves, so it is left alone.
        float pitch = 0.94f + ((float)random.NextDouble() * 0.12f);
        if (surface?.BlockMaterial == EnumBlockMaterial.Water)
        {
            pitch += 0.06f;
        }

        AssetLocation[] samples = SamplesFor(set);
        ILoadedSound sound = capi.World.LoadSound(new SoundParams
        {
            Location = samples[random.Next(samples.Length)],
            Position = new Vec3f((float)x, (float)y, (float)z),
            RelativePosition = false,
            Range = Range,
            SoundType = EnumSoundType.Weather,
            Pitch = pitch,
            Volume = 0.01f,
            ShouldLoop = true,
            DisposeOnFinish = false,
        });
        if (sound == null)
        {
            return false;
        }

        sound.Start();
        // Each emitter enters the loop somewhere else, so they do not pulse together.
        sound.PlaybackPosition = (float)random.NextDouble() * Math.Max(0.1f, sound.SoundLengthSeconds);
        sound.FadeTo(volume, FadeSeconds, _ => { });
        lock (snapshotLock)
        {
            emitters.Add(new Emitter(sound, x, y, z) { Volume = volume, Set = set });
        }

        return true;
    }

    /// <summary>A light shower, a steady rain or a downpour: the weather picks the set.</summary>
    private static RainSampleSet SetForIntensity(float intensity) =>
        intensity < MediumRainFrom ? RainSampleSet.Light
        : intensity < HeavyRainFrom ? RainSampleSet.Medium
        : RainSampleSet.Heavy;

    private static AssetLocation[] SamplesFor(RainSampleSet set) => set switch
    {
        RainSampleSet.Light => CustomSoundRegistry.RainLightLoops,
        RainSampleSet.Heavy => CustomSoundRegistry.RainHeavyLoops,
        RainSampleSet.Canopy => CustomSoundRegistry.RainCanopyLoops,
        _ => CustomSoundRegistry.RainMediumLoops,
    };

    private static Vec3d Ears(Entity player) =>
        new(player.Pos.X + player.LocalEyePos.X, player.Pos.Y + player.LocalEyePos.Y, player.Pos.Z + player.LocalEyePos.Z);

    /// <summary>Which eighth of the compass a spot lies in, from the listener.</summary>
    private static int Octant(double relX, double relZ)
    {
        double angle = Math.Atan2(relZ, relX) + Math.PI;  // 0..2pi
        return (int)(angle / (Math.PI * 2.0) * Octants) % Octants;
    }

    /// <summary>
    /// Solid blocks between the listener and a spot, counted up to BlockedLimit + 1. Leaves, water
    /// and the like let rain through and are not counted; the blocks the two ends sit in are the
    /// listener's own and the surface itself.
    /// </summary>
    private int BlocksBetween(Vec3d from, double toX, double toY, double toZ)
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
            if (!Blocks(blocks.GetBlock(pos)))
            {
                continue;
            }

            if (++blocked > BlockedLimit)
            {
                return blocked;  // no need to count the rest of the hill
            }
        }

        return blocked;
    }

    private static bool Blocks(Block block) =>
        block != null
        && block.Id != 0
        && !block.RainPermeable
        && block.Replaceable < 6000
        && block.BlockMaterial != EnumBlockMaterial.Air
        && block.BlockMaterial != EnumBlockMaterial.Water
        && block.BlockMaterial != EnumBlockMaterial.Plant;

    private bool IsNearPlayingVoice(double x, double y, double z, double spacing)
    {
        double spacingSq = spacing * spacing;
        lock (snapshotLock)
        {
            foreach (Emitter emitter in emitters)
            {
                double dx = emitter.X - x;
                double dy = emitter.Y - y;
                double dz = emitter.Z - z;
                if ((dx * dx) + (dy * dy) + (dz * dz) < spacingSq)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void FadeOutAll(long nowMs, float seconds)
    {
        lock (snapshotLock)
        {
            foreach (Emitter emitter in emitters)
            {
                BeginFade(emitter, nowMs, seconds);
            }

            emitters.Clear();
        }
    }

    private void BeginFade(Emitter emitter, long nowMs, float seconds)
    {
        if (!emitter.Sound.IsDisposed)
        {
            emitter.Sound.FadeOutAndStop(seconds);
        }

        emitter.DisposeAtMs = nowMs + (long)(seconds * 1000f) + 250;
        fading.Add(emitter);
    }

    private void DisposeFaded(long nowMs)
    {
        lock (snapshotLock)
        {
            for (int i = fading.Count - 1; i >= 0; i--)
            {
                if (fading[i].DisposeAtMs > nowMs)
                {
                    continue;
                }

                Release(fading[i]);
                fading.RemoveAt(i);
            }
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

    /// <param name="Score">How well worth playing this spot is: clear of rock, near, near your level.</param>
    private readonly record struct Candidate(double X, double Y, double Z, int BlockX, int SurfaceY, int BlockZ, int Octant, double Score);

    private sealed class Emitter(ILoadedSound sound, double x, double y, double z)
    {
        public ILoadedSound Sound { get; } = sound;

        public double X { get; } = x;

        public double Y { get; } = y;

        public double Z { get; } = z;

        public float Volume { get; set; }

        public RainSampleSet Set { get; set; }

        public long DisposeAtMs { get; set; }
    }
}

/// <summary>Which recording an emitter plays: the character of the rain, or what it lands on.</summary>
internal enum RainSampleSet
{
    Light,
    Medium,
    Heavy,
    Canopy
}

internal readonly record struct RainSurfaceEmitterVisual(Vec3d Position, float Volume, bool FadingOut);
