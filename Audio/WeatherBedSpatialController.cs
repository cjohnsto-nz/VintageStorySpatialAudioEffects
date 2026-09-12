using System;
using System.Collections.Generic;
using HarmonyLib;
using OpenTK.Audio.OpenAL;
using OpenTK.Mathematics;
using Vintagestory.Client;

namespace SurroundSoundLab;

internal static class WeatherBedSpatialController
{
    private const int SourceSpatialize = 0x1214;
    private const int SourceRadius = 0x1031;
    private const int DirectChannels = 0x1033;
    private static readonly object Sync = new();
    private static readonly List<Bed> Beds = new();

    internal static void OnSourceCreated(LoadedSoundNative sound)
    {
        Forget(sound);
        if (sound.IsDisposed || !AudioOpenAlInitContextPatch.SpatialBedRequested
            || SurroundSoundLabConfigManager.Current.OutputMode != SurroundOutputMode.WindowsSpatialAudio)
            return;
        var location = sound.Params?.Location;
        float elevation = WeatherBedSpatialPolicy.Elevation(location?.Domain, location?.Path);
        int channels = LoadedSoundNativeChannelMaskPatch.SampleRef(sound)?.Channels ?? 0;
        var originalPosition = sound.Params?.Position;
        // Don't relocate a named weather asset when another caller uses it as
        // an actual world-positioned effect rather than an ambient bed.
        bool positionedEffect = sound.Params?.RelativePosition == false && originalPosition != null
            && (originalPosition.X != 0f || originalPosition.Y != 0f || originalPosition.Z != 0f);
        if (elevation == 0f || channels < 1 || positionedEffect
            || !AL.IsExtensionPresent("AL_SOFT_source_spatialize")
            || !AL.IsExtensionPresent("AL_EXT_SOURCE_RADIUS")) return;
        int source = LoadedSoundNativeChannelMaskPatch.SourceIdRef(sound);
        if (source == 0) return;

        // Enable positional rendering for these multichannel beds. Radius keeps
        // their channels spread around the listener instead of collapsing to mono.
        AL.Source(source, (ALSourcei)DirectChannels, 0);
        AL.Source(source, (ALSourcei)SourceSpatialize, 1);
        AL.Source(source, (ALSourcef)SourceRadius, channels == 1
            ? WeatherBedSpatialPolicy.Height * 0.5f : WeatherBedSpatialPolicy.Radius(elevation));
        AL.Source(source, ALSourcef.RolloffFactor, 0f);
        AL.Source(source, ALSourceb.SourceRelative, false);
        SetPosition(source, AL.GetListener(ALListener3f.Position));
        lock (Sync)
            Beds.Add(new Bed(new WeakReference<LoadedSoundNative>(sound), source, AudioOpenAlInitContextPatch.ContextGeneration));
    }

    internal static void OnListenerUpdated(Vector3 position)
    {
        lock (Sync)
        {
            for (int i = Beds.Count - 1; i >= 0; i--)
            {
                Bed bed = Beds[i];
                // Context recreation can reuse native source IDs. Never touch an
                // ID from an old generation, even if it appears valid again.
                if (bed.Generation != AudioOpenAlInitContextPatch.ContextGeneration
                    || !bed.Sound.TryGetTarget(out var sound) || sound.IsDisposed
                    || LoadedSoundNativeChannelMaskPatch.SourceIdRef(sound) != bed.Source)
                {
                    Beds.RemoveAt(i);
                    continue;
                }
                SetPosition(bed.Source, position);
            }
        }
    }

    internal static void Forget(LoadedSoundNative sound)
    {
        lock (Sync)
            Beds.RemoveAll(bed => !bed.Sound.TryGetTarget(out var target) || ReferenceEquals(target, sound));
    }

    private static void SetPosition(int source, Vector3 listener) =>
        AL.Source(source, ALSource3f.Position, listener.X, listener.Y + WeatherBedSpatialPolicy.Height, listener.Z);

    private sealed record Bed(WeakReference<LoadedSoundNative> Sound, int Source, long Generation);
}

[HarmonyPatch(typeof(LoadedSoundNative), nameof(LoadedSoundNative.Dispose))]
internal static class WeatherBedSpatialDisposePatch
{
    public static void Prefix(LoadedSoundNative __instance) => WeatherBedSpatialController.Forget(__instance);
}
