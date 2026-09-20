using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace SurroundWeather;

/// <summary>
/// Takes the ambient sounds the emitter fields have replaced out of the scan that makes them.
/// <para>
/// Vanilla makes one looping sound for every block of a kind in a 32-block section, merges the
/// sections, and moves the one sound to whichever block of it the listener stands nearest. Rain on
/// the windows is then a single window that follows you about the room, and a lake is a single
/// point a stride away wherever you walk. That is what the fields are here to replace, so while
/// one plays, its sound does not.
/// </para>
/// <para>
/// A block saying it makes no sound (BlockRainAmbient.GetAmbientSoundStrength, which
/// <see cref="RainWindowSuppressor"/> sees to) is the polite way, but it trusts every window to be
/// that class: another mod's glass, or a chiselled one, answers for itself. This is the last gate
/// before the sounds are made, and it holds whatever asked for one.
/// </para>
/// </summary>
internal static class VanillaAmbientStripper
{
    private const long LogEveryMs = 5000;

    /// <summary>The sounds a field has taken over, and what says that field is running.</summary>
    private static readonly (string Path, System.Func<SurroundWeatherConfig, bool> Replaced)[] TakenOver =
    {
        (RainWindowField.WindowSound, config => config.ExperimentalWindowRainEmitters),
        (WaterField.WaveSound, config => config.ExperimentalWaterWaveEmitters),
        (WaterField.CreekSound, config => config.ExperimentalFlowingWaterEmitters),
        (WaterField.RapidsSound, config => config.ExperimentalFlowingWaterEmitters),
        (WaterField.FallSound, config => config.ExperimentalFlowingWaterEmitters),
    };

    private static ILogger log;
    private static long lastLogMs;

    internal static void TryPatch(Harmony harmony, ILogger logger)
    {
        log = logger;
        MethodInfo method = AccessTools.Method(typeof(SystemPlayerSounds), "OnAmbientSoundScan");
        if (method == null)
        {
            logger.Notification("SystemPlayerSounds.OnAmbientSoundScan not found; vanilla's rain-on-glass sound is left alone.");
            return;
        }

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(VanillaAmbientStripper), nameof(Prefix)));
        logger.Notification("Patched SystemPlayerSounds.OnAmbientSoundScan: the sounds the emitter fields play give way to them.");
    }

    public static void Prefix(List<AmbientSound> newAmbientSounds)
    {
        if (newAmbientSounds == null)
        {
            return;
        }

        SurroundWeatherConfig config = SurroundWeatherConfigManager.Current;
        int stripped = 0;
        int blocks = 0;
        for (int i = newAmbientSounds.Count - 1; i >= 0; i--)
        {
            string path = newAmbientSounds[i]?.AssetLoc?.Path;
            if (path == null || !IsTakenOver(path, config))
            {
                continue;
            }

            blocks += newAmbientSounds[i].QuantityNearbyBlocks;
            newAmbientSounds.RemoveAt(i);
            stripped++;
        }

        // Worth knowing in the log: it says whether what is heard now is vanilla's or a field's.
        long nowMs = Environment.TickCount64;
        if (stripped > 0 && nowMs - lastLogMs > LogEveryMs)
        {
            lastLogMs = nowMs;
            log?.Notification(
                "Ambient scan: took out {0} vanilla sound(s) over {1} block(s); the emitter fields have them.",
                stripped, blocks);
        }
    }

    private static bool IsTakenOver(string path, SurroundWeatherConfig config)
    {
        foreach ((string sound, System.Func<SurroundWeatherConfig, bool> replaced) in TakenOver)
        {
            if (path.Contains(sound, StringComparison.OrdinalIgnoreCase) && replaced(config))
            {
                return true;
            }
        }

        return false;
    }
}
