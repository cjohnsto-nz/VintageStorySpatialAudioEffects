using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SurroundWeather;

/// <summary>
/// Experimental: rain as a ring of looping emitters on the surfaces it lands on, instead of a bed
/// at the listener. Each emitter sits on the top of a column's rain-blocking block, which is the
/// ground outside, the roof over a building, and the canopy under a tree.
/// <para>
/// There is no room muffling here. Where you are decides nothing; where the rain lands decides
/// everything. Indoors the emitters are on the roof above you and on the ground past the doorway,
/// and the audio engine muffles them through the wall in between.
/// </para>
/// </summary>
internal sealed class RainSurfaceEmitterSystem : IDisposable
{
    private const int TickMs = 250;
    private const float FadeSeconds = 1.2f;
    private const float StopFadeSeconds = 0.6f;
    /// <summary>Emitters keep this far apart, so rain is spread rather than stacked in one spot.</summary>
    private const double MinSpacing = 4.0;
    private const double MinRadius = 2.5;
    /// <summary>A surface further above or below than this is another world (a cave roof, a cliff).</summary>
    private const double MaxVerticalOffset = 20.0;
    private const int PlacementAttempts = 24;
    /// <summary>Vanilla's range for a weather track: the engine hears it fully within a few metres, then 1/r.</summary>
    private const float Range = 16f;
    private const float BaseVolume = 0.55f;

    private static readonly AssetLocation[] RainAliases =
    {
        CustomSoundRegistry.RainOneAlias,
        CustomSoundRegistry.RainTwoAlias,
        CustomSoundRegistry.RainThreeAlias,
        CustomSoundRegistry.RainFourAlias
    };

    private readonly ICoreClientAPI capi;
    private readonly Random random = new();
    private readonly List<Emitter> emitters = new();
    private readonly List<Emitter> fading = new();
    private readonly object snapshotLock = new();
    private long tickListenerId;

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

        float intensity = GameMath.Clamp(rainfall * 2f, 0f, 1f);
        float volume = GameMath.Clamp(BaseVolume * intensity * Math.Max(0f, config.RainSurfaceEmitterVolume), 0.01f, 1f);
        int wanted = (int)Math.Round(Math.Max(2, config.RainSurfaceEmitterCount) * (0.45f + (0.55f * intensity)));

        CullOutOfRange(player.Pos, config, nowMs);
        lock (snapshotLock)
        {
            for (int i = emitters.Count - 1; i >= 0; i--)
            {
                Emitter emitter = emitters[i];
                if (emitter.Sound.IsDisposed)
                {
                    emitters.RemoveAt(i);
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

        int missing = wanted - ActiveCount;
        for (int i = 0; i < missing; i++)
        {
            if (!TryPlace(player.Pos, volume, config))
            {
                break;  // nowhere left that rain reaches: try again next tick
            }
        }
    }

    private bool TryPlace(EntityPos playerPos, float volume, SurroundWeatherConfig config)
    {
        IBlockAccessor blocks = capi.World?.BlockAccessor;
        if (blocks == null)
        {
            return false;
        }

        double maxRadius = Math.Max(MinRadius + 1.0, config.RainSurfaceEmitterRadius);
        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            double angle = random.NextDouble() * Math.PI * 2.0;
            double radius = MinRadius + ((maxRadius - MinRadius) * Math.Sqrt(random.NextDouble()));
            int x = (int)Math.Floor(playerPos.X + (Math.Cos(angle) * radius));
            int z = (int)Math.Floor(playerPos.Z + (Math.Sin(angle) * radius));

            // The top of this column's rain-blocking block: the ground, a roof, or a canopy.
            int surfaceY = blocks.GetRainMapHeightAt(x, z);
            double ex = x + 0.5;
            double ey = surfaceY + 1.0;
            double ez = z + 0.5;
            if (Math.Abs(ey - playerPos.Y) > MaxVerticalOffset)
            {
                continue;
            }

            var above = new BlockPos(x, surfaceY + 1, z, playerPos.Dimension);
            Block block = blocks.GetBlock(above);
            if (block != null && block.SideSolid[BlockFacing.UP.Index] && block.Id != 0)
            {
                continue;  // not open to the sky after all: no rain lands here
            }

            if (IsTooClose(ex, ey, ez))
            {
                continue;
            }

            Block surface = blocks.GetBlock(new BlockPos(x, surfaceY, z, playerPos.Dimension));
            return Play(ex, ey, ez, surface, volume);
        }

        return false;
    }

    private bool Play(double x, double y, double z, Block surface, float volume)
    {
        // Rain on water is brighter, on leaves softer; otherwise a little random spread keeps the
        // emitters from ringing as one voice.
        float pitch = 0.94f + ((float)random.NextDouble() * 0.12f);
        if (surface?.BlockMaterial == EnumBlockMaterial.Water)
        {
            pitch += 0.06f;
        }
        else if (surface?.BlockMaterial == EnumBlockMaterial.Leaves)
        {
            pitch -= 0.05f;
        }

        ILoadedSound sound = capi.World.LoadSound(new SoundParams
        {
            Location = RainAliases[random.Next(RainAliases.Length)],
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
            emitters.Add(new Emitter(sound, x, y, z) { Volume = volume });
        }

        return true;
    }

    private void CullOutOfRange(EntityPos playerPos, SurroundWeatherConfig config, long nowMs)
    {
        double keep = Math.Max(MinRadius + 1.0, config.RainSurfaceEmitterRadius) * 1.6;
        double keepSq = keep * keep;
        lock (snapshotLock)
        {
            for (int i = emitters.Count - 1; i >= 0; i--)
            {
                Emitter emitter = emitters[i];
                double dx = emitter.X - playerPos.X;
                double dz = emitter.Z - playerPos.Z;
                if ((dx * dx) + (dz * dz) <= keepSq && Math.Abs(emitter.Y - playerPos.Y) <= MaxVerticalOffset)
                {
                    continue;
                }

                BeginFade(emitter, nowMs, FadeSeconds);
                emitters.RemoveAt(i);
            }
        }
    }

    private bool IsTooClose(double x, double y, double z)
    {
        lock (snapshotLock)
        {
            foreach (Emitter emitter in emitters)
            {
                double dx = emitter.X - x;
                double dy = emitter.Y - y;
                double dz = emitter.Z - z;
                if ((dx * dx) + (dy * dy) + (dz * dz) < MinSpacing * MinSpacing)
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

    private sealed class Emitter(ILoadedSound sound, double x, double y, double z)
    {
        public ILoadedSound Sound { get; } = sound;

        public double X { get; } = x;

        public double Y { get; } = y;

        public double Z { get; } = z;

        public float Volume { get; set; }

        public long DisposeAtMs { get; set; }
    }
}

internal readonly record struct RainSurfaceEmitterVisual(Vec3d Position, float Volume, bool FadingOut);
