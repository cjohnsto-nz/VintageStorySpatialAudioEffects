using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>A place a field could play from, as its kind of field found it.</summary>
/// <param name="Quality">0..1, the field's own judgement of the spot (its level, its exposure).</param>
/// <param name="Kind">What is there, in the field's own terms (a canopy, a reed).</param>
internal readonly record struct EmitterCandidate(double X, double Y, double Z, double Quality, int Kind)
{
    public double Score { get; init; }
}

/// <summary>What the debug overlay draws for one emitter.</summary>
internal readonly record struct EmitterVisual(Vec3d Position, float Volume, bool FadingOut);

/// <summary>
/// A field of short-lived positional sounds kept around the listener: rain on the ground, leaves in
/// the wind. What every such field needs is here, so they all behave alike:
/// <list type="bullet">
///   <item><b>It leads the listener.</b> The field is centred ahead along the listener's velocity
///         (LeadSeconds of travel), in the direction they move, not where they look. Emitters the
///         field has left behind are retired at once and new ones open ahead, so running does not
///         outrun the sound.</item>
///   <item><b>Turnover is a steady trickle.</b> Births and retirements are paid for from a budget
///         that fills at count / lifetime per second (faster when moving), a few per step at most.
///         Nothing happens in bursts, so the field never swells and dies in waves, and no emitter
///         stays long enough to be heard as a fixed speaker.</item>
///   <item><b>Fades are equal-power and run per frame.</b> The game's own fades are linear in dB,
///         nearly silent for most of their length; crossfading with them dips.</item>
///   <item><b>Spots behind rock are not played</b> (optional), and live emitters that end up there
///         are retired.</item>
/// </list>
/// A field says when it is active and how strongly, where its candidates are, and what each sounds
/// like.
/// </summary>
internal abstract class EmitterField : IDisposable
{
    private const int FrameTickMs = 20;
    private const double LogicStepSeconds = 0.1;
    private const long CandidateRefreshMs = 500;
    private const double CandidateMoveRefresh = 2.0;
    private const int MaxCandidatesTested = 64;
    private const int MaxSpawnsPerStep = 3;
    private const float MaxBudget = 3f;
    private const float FastFadeSeconds = 0.4f;
    private const int Octants = 8;
    /// <summary>Blocks per second above which the listener counts as travelling.</summary>
    private const double MovingSpeed = 0.5;
    private const double VelocitySmoothingSeconds = 0.4;
    /// <summary>
    /// Solid blocks in the way that a spot may still be played through. A roof and its rafters
    /// are a few; the depth of a cave is many more. The audio engine muffles what is played.
    /// </summary>
    protected const int BlockedLimit = 4;

    protected readonly ICoreClientAPI capi;
    protected readonly Random random = new();

    private readonly List<Emitter> emitters = new();
    private readonly List<EmitterCandidate> candidates = new();
    private readonly List<EmitterCandidate> rough = new();
    private readonly object emittersLock = new();
    private readonly Vec3d lastPosition = new();
    private readonly Vec3d lastCandidateCentre = new();
    private long tickListenerId;
    private bool hasLastPosition;
    private bool hasCandidateCentre;
    private long lastCandidateRefreshMs;
    private double velocityX;
    private double velocityZ;
    private double logicAccumulator;
    private float budget;
    private int occlusionCursor;

    protected EmitterField(ICoreClientAPI capi)
    {
        this.capi = capi;
        tickListenerId = capi.Event.RegisterGameTickListener(OnFrame, FrameTickMs);
    }

    // ---- what a field is ----

    /// <summary>Whether the field plays now, and how strongly (0..1): the rainfall, the wind.</summary>
    protected abstract bool TryGetIntensity(Entity player, out float intensity);

    protected abstract int TargetCount(float intensity);

    /// <summary>Blocks around the (led) centre in which emitters live.</summary>
    protected abstract double Radius { get; }

    /// <summary>Nothing is placed nearer the listener's head than this (in any direction: a roof overhead counts by its height).</summary>
    protected virtual double MinRadius => 2.0;

    /// <summary>Blocks between emitters.</summary>
    protected abstract double Spacing { get; }

    /// <summary>Seconds an emitter plays before its place moves on (each gets half to one and a half of it).</summary>
    protected abstract double LifetimeSeconds { get; }

    protected virtual float FadeSeconds => 1.0f;

    /// <summary>Seconds of the listener's travel the field is centred ahead by.</summary>
    protected virtual double LeadSeconds => 2.0;

    /// <summary>Drop spots (and live emitters) with more than BlockedLimit solid blocks in the way.</summary>
    protected virtual bool CullOccluded => true;

    /// <summary>A surface further above or below the listener than this is not theirs to hear.</summary>
    protected virtual double MaxVerticalOffset => 20.0;

    /// <summary>Every place the field could play from, within <paramref name="radius"/> of the centre.</summary>
    protected abstract void CollectCandidates(Vec3d centre, double radius, EntityPos playerPos, List<EmitterCandidate> into);

    /// <summary>The sound for a spot: created, not started. <paramref name="variant"/> is the field's note of what it chose.</summary>
    protected abstract ILoadedSound CreateSound(in EmitterCandidate candidate, float intensity, out int variant);

    /// <summary>How loud an emitter should be now.</summary>
    protected abstract float VolumeOf(int variant, int kind, float intensity);

    /// <summary>True when the weather has moved on from what this emitter plays: it is retired as its turn comes.</summary>
    protected virtual bool IsStale(int variant, int kind, float intensity) => false;

    /// <summary>
    /// Emitters never loop: each plays one slice of its recording, as long as its life, then its
    /// place moves on. 1 starts the slice anywhere it fits (so no two play the same stretch);
    /// 0 always starts at the start (a splash has an attack).
    /// </summary>
    protected virtual float RandomStartFraction => 1f;

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
        }
    }

    public List<EmitterVisual> GetSnapshot()
    {
        lock (emittersLock)
        {
            var snapshot = new List<EmitterVisual>(emitters.Count);
            foreach (Emitter emitter in emitters)
            {
                snapshot.Add(new EmitterVisual(new Vec3d(emitter.X, emitter.Y, emitter.Z), emitter.Applied, emitter.Retiring));
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

            budget = 0f;
            hasLastPosition = false;
            return;
        }

        EntityPos playerPos = player.Pos;
        Motion motion = UpdateMotion(player, stepSeconds);
        double radius = Math.Max(MinRadius + 1.0, Radius);
        double lead = Math.Min(motion.Speed * LeadSeconds, radius * 0.75);
        var centre = new Vec3d(playerPos.X + (motion.DirX * lead), playerPos.Y, playerPos.Z + (motion.DirZ * lead));
        Vec3d ears = new(playerPos.X + player.LocalEyePos.X, playerPos.Y + player.LocalEyePos.Y, playerPos.Z + player.LocalEyePos.Z);

        int live = UpdateLive(centre, radius, playerPos, ears, intensity, nowMs);
        RefreshCandidates(centre, radius, playerPos, ears, motion, nowMs);

        int target = Math.Max(0, TargetCount(intensity));
        if (live > target)
        {
            Emitter surplus = Worst(centre, radius, motion, nowMs, expiredFirst: true);
            if (surplus != null)
            {
                Retire(surplus, FadeSeconds);
                live--;
            }
        }

        // The trickle: count / lifetime births a second keeps every emitter about a lifetime old,
        // and travelling pays for more, because the field's ground is changing under it.
        double lifetime = Math.Max(0.5, LifetimeSeconds);
        float rate = (float)(target / lifetime * (1.0 + Math.Min(3.0, motion.Speed / 2.0)));
        budget = Math.Min(MaxBudget, budget + (rate * (float)stepSeconds));

        int spawned = 0;
        var used = new HashSet<int>();
        while (spawned < MaxSpawnsPerStep)
        {
            if (live < target)
            {
                // A free place (first fill, or something was dropped) is filled without waiting.
                if (!Spawn(playerPos, intensity, nowMs, used))
                {
                    break;
                }

                live++;
                spawned++;
                continue;
            }

            if (budget < 1f || target == 0)
            {
                break;
            }

            // Full: a place changes hands. The expired go first, then whoever is worst placed,
            // and only if there is somewhere better to be.
            Emitter leaving = Worst(centre, radius, motion, nowMs, expiredFirst: true);
            if (leaving == null)
            {
                break;
            }

            bool expired = leaving.ExpiresMs <= nowMs || IsStale(leaving.Variant, leaving.Kind, intensity);
            if (!expired && (candidates.Count == 0 || candidates[0].Score < Value(leaving, centre, radius, motion) + 0.2))
            {
                break;
            }

            budget -= 1f;
            if (!Spawn(playerPos, intensity, nowMs, used))
            {
                break;
            }

            Retire(leaving, FadeSeconds);
            spawned++;
        }
    }

    /// <summary>Volumes, and what has left the field, ended, or gone behind rock. Returns the live count.</summary>
    private int UpdateLive(Vec3d centre, double radius, EntityPos playerPos, Vec3d ears, float intensity, long nowMs)
    {
        double keepSq = radius * 1.15 * (radius * 1.15);
        int live = 0;
        int index = 0;
        int checkEvery = CullOccluded ? 3 : int.MaxValue;
        occlusionCursor++;
        foreach (Emitter emitter in emitters)
        {
            index++;
            if (emitter.Retiring)
            {
                continue;
            }

            if (nowMs - emitter.BornMs > 500 && emitter.Sound.HasStopped)
            {
                emitter.Level = 0f;  // a one-shot that played out
                emitter.Retiring = true;
                continue;
            }

            double dx = emitter.X - centre.X;
            double dz = emitter.Z - centre.Z;
            bool outside = (dx * dx) + (dz * dz) > keepSq || Math.Abs(emitter.Y - playerPos.Y) > MaxVerticalOffset;
            // A third of them each step: has rock come between us?
            bool blocked = !outside && (index + occlusionCursor) % checkEvery == 0
                && BlocksBetween(ears, emitter.X, emitter.Y, emitter.Z) > BlockedLimit;
            if (outside || blocked)
            {
                Retire(emitter, FastFadeSeconds);  // left behind, or unheard: its place is wanted ahead
                continue;
            }

            emitter.TargetVolume = GameMath.Clamp(VolumeOf(emitter.Variant, emitter.Kind, intensity), 0f, 1f);
            live++;
        }

        return live;
    }

    private void RefreshCandidates(Vec3d centre, double radius, EntityPos playerPos, Vec3d ears, Motion motion, long nowMs)
    {
        if (hasCandidateCentre && nowMs - lastCandidateRefreshMs < CandidateRefreshMs
            && lastCandidateCentre.SquareDistanceTo(centre.X, centre.Y, centre.Z) < CandidateMoveRefresh * CandidateMoveRefresh)
        {
            return;
        }

        candidates.Clear();
        rough.Clear();
        CollectCandidates(centre, radius, playerPos, rough);

        double minRadiusSq = MinRadius * MinRadius;
        for (int i = rough.Count - 1; i >= 0; i--)
        {
            EmitterCandidate candidate = rough[i];
            double px = candidate.X - ears.X;
            double py = candidate.Y - ears.Y;
            double pz = candidate.Z - ears.Z;
            if ((px * px) + (py * py) + (pz * pz) < minRadiusSq || Math.Abs(candidate.Y - playerPos.Y) > MaxVerticalOffset)
            {
                rough.RemoveAt(i);
                continue;
            }

            rough[i] = candidate with { Score = Geometry(candidate.X, candidate.Z, candidate.Quality, centre, radius, motion) };
        }

        rough.Sort((left, right) => right.Score.CompareTo(left.Score));
        int tested = CullOccluded ? Math.Min(rough.Count, MaxCandidatesTested) : rough.Count;
        for (int i = 0; i < tested; i++)
        {
            EmitterCandidate candidate = rough[i];
            if (!CullOccluded)
            {
                candidates.Add(candidate);
                continue;
            }

            int blocked = BlocksBetween(ears, candidate.X, candidate.Y, candidate.Z);
            if (blocked > BlockedLimit)
            {
                continue;
            }

            // A clear way counts, but nearness counts for more: the roof over your head is the
            // rain you hear most, though it is behind its own boards.
            double openness = 1.0 - (blocked / (double)(BlockedLimit + 1));
            candidates.Add(candidate with { Score = (openness * 0.35) + (candidate.Score * 0.65) });
        }

        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        lastCandidateRefreshMs = nowMs;
        lastCandidateCentre.Set(centre.X, centre.Y, centre.Z);
        hasCandidateCentre = true;
    }

    /// <summary>Near the (led) centre, well judged by its field, and ahead of the way the listener goes.</summary>
    private static double Geometry(double x, double z, double quality, Vec3d centre, double radius, Motion motion)
    {
        double dx = x - centre.X;
        double dz = z - centre.Z;
        double distance = Math.Sqrt((dx * dx) + (dz * dz));
        double nearness = Math.Max(0.0, 1.0 - (distance / radius));
        double ahead = distance < 1e-6 ? 0.5 : ((((dx * motion.DirX) + (dz * motion.DirZ)) / distance) + 1.0) * 0.5;
        double aheadWeight = motion.IsMoving ? 0.3 : 0.12;
        return (nearness * (0.65 - aheadWeight)) + (quality * 0.35) + (ahead * aheadWeight);
    }

    private static double Value(Emitter emitter, Vec3d centre, double radius, Motion motion) =>
        (Geometry(emitter.X, emitter.Z, emitter.Quality, centre, radius, motion) * 0.65) + 0.35;  // it was open when placed

    /// <summary>The emitter whose place should change hands next: an expired one, else the worst placed.</summary>
    private Emitter Worst(Vec3d centre, double radius, Motion motion, long nowMs, bool expiredFirst)
    {
        Emitter worst = null;
        double worstValue = double.MaxValue;
        foreach (Emitter emitter in emitters)
        {
            if (emitter.Retiring)
            {
                continue;
            }

            double value = Value(emitter, centre, radius, motion);
            if (expiredFirst && emitter.ExpiresMs <= nowMs)
            {
                value -= 10.0 + ((nowMs - emitter.ExpiresMs) / 1000.0);  // the longest overdue first
            }

            if (value < worstValue)
            {
                worstValue = value;
                worst = emitter;
            }
        }

        return worst;
    }

    /// <summary>Opens the best free candidate, the emptiest eighth of the compass first.</summary>
    private bool Spawn(EntityPos playerPos, float intensity, long nowMs, HashSet<int> used)
    {
        var perOctant = new int[Octants];
        foreach (Emitter emitter in emitters)
        {
            if (!emitter.Retiring)
            {
                perOctant[Octant(emitter.X - playerPos.X, emitter.Z - playerPos.Z)]++;
            }
        }

        double spacingSq = Math.Max(0.25, Spacing * Spacing);
        int best = -1;
        double bestScore = double.MinValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i))
            {
                continue;
            }

            EmitterCandidate candidate = candidates[i];
            double score = candidate.Score - (0.1 * perOctant[Octant(candidate.X - playerPos.X, candidate.Z - playerPos.Z)]);
            if (score <= bestScore || IsCrowded(candidate, spacingSq))
            {
                continue;
            }

            bestScore = score;
            best = i;
        }

        if (best < 0)
        {
            return false;
        }

        used.Add(best);
        EmitterCandidate chosen = candidates[best];
        ILoadedSound sound = CreateSound(chosen, intensity, out int variant);
        if (sound == null)
        {
            return false;
        }

        // One slice of the recording, never a loop: the life fits inside the sample, and the
        // slice starts anywhere it fits.
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

        emitters.Add(new Emitter(sound, chosen.X, chosen.Y, chosen.Z)
        {
            Quality = chosen.Quality,
            Kind = chosen.Kind,
            Variant = variant,
            TargetVolume = GameMath.Clamp(VolumeOf(variant, chosen.Kind, intensity), 0f, 1f),
            BornMs = nowMs,
            ExpiresMs = nowMs + (long)(lifetime * 1000.0),
        });
        return true;
    }

    private bool IsCrowded(in EmitterCandidate candidate, double spacingSq)
    {
        foreach (Emitter emitter in emitters)
        {
            if (emitter.Retiring)
            {
                continue;
            }

            double dx = emitter.X - candidate.X;
            double dy = emitter.Y - candidate.Y;
            double dz = emitter.Z - candidate.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) < spacingSq)
            {
                return true;
            }
        }

        return false;
    }

    private static void Retire(Emitter emitter, float seconds)
    {
        if (emitter.Retiring)
        {
            emitter.RetireSeconds = Math.Min(emitter.RetireSeconds, seconds);
            return;
        }

        emitter.Retiring = true;
        emitter.RetireSeconds = seconds;
    }

    private static void Release(Emitter emitter)
    {
        if (!emitter.Sound.IsDisposed)
        {
            emitter.Sound.Stop();
            emitter.Sound.Dispose();
        }
    }

    /// <summary>The listener's smoothed velocity; the way they go, or (standing) the way they look.</summary>
    private Motion UpdateMotion(Entity player, double stepSeconds)
    {
        EntityPos pos = player.Pos;
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
        if (speed > MovingSpeed)
        {
            return new Motion(velocityX / speed, velocityZ / speed, speed, true);
        }

        Vec3f view = pos.GetViewVector();
        double length = Math.Sqrt((view.X * view.X) + (view.Z * view.Z));
        return length > 1e-3
            ? new Motion(view.X / length, view.Z / length, 0, false)
            : new Motion(-Math.Sin(pos.Yaw), Math.Cos(pos.Yaw), 0, false);
    }

    private static int Octant(double relX, double relZ)
    {
        double angle = Math.Atan2(relZ, relX) + Math.PI;
        return (int)(angle / (Math.PI * 2.0) * Octants) % Octants;
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

    protected static bool IsSolid(Block block) =>
        block != null
        && block.Id != 0
        && !block.RainPermeable
        && block.Replaceable < 6000
        && block.BlockMaterial != EnumBlockMaterial.Air
        && block.BlockMaterial != EnumBlockMaterial.Water
        && block.BlockMaterial != EnumBlockMaterial.Plant
        && block.BlockMaterial != EnumBlockMaterial.Leaves;

    private readonly record struct Motion(double DirX, double DirZ, double Speed, bool IsMoving);

    private sealed class Emitter(ILoadedSound sound, double x, double y, double z)
    {
        public ILoadedSound Sound { get; } = sound;

        public double X { get; } = x;

        public double Y { get; } = y;

        public double Z { get; } = z;

        public double Quality { get; init; }

        public int Kind { get; init; }

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
