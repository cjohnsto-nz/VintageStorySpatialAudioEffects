using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace SurroundWeather;

/// <summary>
/// Takes vanilla's rain-on-glass sound out of the ambient scan while the window emitters play.
/// <para>
/// A block saying it makes no sound (BlockRainAmbient.GetAmbientSoundStrength, which
/// <see cref="RainWindowSuppressor"/> sees to) is the polite way, but it trusts every window to be
/// that class: another mod's glass, or a chiselled one, answers for itself. This is the last gate
/// before the sounds are made, and it holds whatever asked for a rainwindow.
/// </para>
/// <para>
/// Vanilla makes one looping sound for every pane in a 32-block section at once and moves it to
/// whichever pane the listener stands nearest, so leaving it playing sounds like a single window
/// that follows you about - the very thing the emitters are here to replace.
/// </para>
/// </summary>
internal static class VanillaWindowSoundStripper
{
    private const long LogEveryMs = 5000;

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

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(VanillaWindowSoundStripper), nameof(Prefix)));
        logger.Notification("Patched SystemPlayerSounds.OnAmbientSoundScan: rain-on-glass gives way to the window emitters.");
    }

    public static void Prefix(List<AmbientSound> newAmbientSounds)
    {
        if (newAmbientSounds == null || !SurroundWeatherConfigManager.Current.ExperimentalWindowRainEmitters)
        {
            return;
        }

        int stripped = 0;
        int panes = 0;
        for (int i = newAmbientSounds.Count - 1; i >= 0; i--)
        {
            string path = newAmbientSounds[i]?.AssetLoc?.Path;
            if (path?.Contains(RainWindowField.WindowSound, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            panes += newAmbientSounds[i].QuantityNearbyBlocks;
            newAmbientSounds.RemoveAt(i);
            stripped++;
        }

        // Worth knowing in the log: it says whether a window heard now is vanilla's or the field's.
        long nowMs = Environment.TickCount64;
        if (stripped > 0 && nowMs - lastLogMs > LogEveryMs)
        {
            lastLogMs = nowMs;
            log?.Notification(
                "Ambient scan: took out {0} vanilla rain-on-glass sound(s) over {1} pane(s); the window emitters have it.",
                stripped, panes);
        }
    }
}
