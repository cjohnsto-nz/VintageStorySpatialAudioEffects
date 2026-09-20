using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace SpatialAudioEffects;

/// <summary>
/// Silences vanilla's one rain-on-glass ambient sound while the window emitters play instead.
/// <para>
/// It is not an asset swap but the block's own say in whether it sounds at all
/// (BlockRainAmbient.GetAmbientSoundStrength, which vanilla asks on every scan), so turning the
/// emitters on or off takes hold within a scan, without reloading the world.
/// </para>
/// </summary>
internal static class RainWindowSuppressor
{
    private const string BlockClass = "Vintagestory.GameContent.BlockRainAmbient";

    /// <summary>Patches the survival mod's rain-ambient block, if this world has it.</summary>
    internal static void TryPatch(Harmony harmony, ILogger logger)
    {
        Type type = AccessTools.TypeByName(BlockClass);
        MethodInfo method = type == null ? null : AccessTools.Method(type, "GetAmbientSoundStrength");
        if (method == null)
        {
            logger.Notification("{0}.GetAmbientSoundStrength not found; vanilla's rain-on-glass sound is left alone.", BlockClass);
            return;
        }

        harmony.Patch(method, postfix: new HarmonyMethod(typeof(RainWindowSuppressor), nameof(Postfix)));
        logger.Notification("Patched {0}.GetAmbientSoundStrength: vanilla's rain-on-glass sound gives way to the window emitters.", BlockClass);
    }

    public static void Postfix(ref float __result)
    {
        if (SpatialAudioEffectsConfigManager.Current.ExperimentalWindowRainEmitters)
        {
            __result = 0f;  // the window emitters are the rain on the glass now
        }
    }
}
