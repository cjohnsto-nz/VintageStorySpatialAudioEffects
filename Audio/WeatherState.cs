using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SpatialAudioEffects;

/// <summary>
/// What the weather is doing, read from the game's client weather system. Reflection: the weather
/// system lives in VSEssentials, which this mod does not reference.
/// </summary>
internal static class WeatherState
{
    /// <summary>Rain falling now (0..1 of the game's rainfall), or false for snow, hail and dry weather.</summary>
    public static bool TryGetRainfall(ICoreClientAPI capi, out float rainfall)
    {
        rainfall = 0f;
        ModSystem weatherSystem = capi?.ModLoader?.GetModSystem("Vintagestory.GameContent.WeatherSystemClient");
        if (weatherSystem == null)
        {
            return false;
        }

        object climate = ReadMember(weatherSystem, "clientClimateCond");
        if (climate == null)
        {
            return false;
        }

        rainfall = ReadFloat(climate, "Rainfall");
        if (rainfall <= 0.02f)
        {
            return false;
        }

        object blended = ReadMember(weatherSystem, "BlendedWeatherData");
        if (blended == null)
        {
            return false;
        }

        string precipitation = ReadMember(blended, "BlendedPrecType")?.ToString() ?? string.Empty;
        if (precipitation.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            precipitation = ReadFloat(climate, "Temperature") < ReadFloat(blended, "snowThresholdTemp") ? "Snow" : "Rain";
        }

        return precipitation.Equals("Rain", StringComparison.OrdinalIgnoreCase);
    }

    private static object ReadMember(object target, string memberName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        PropertyInfo property = type.GetProperty(memberName, flags);
        return property != null ? property.GetValue(target) : type.GetField(memberName, flags)?.GetValue(target);
    }

    private static float ReadFloat(object target, string memberName) => ReadMember(target, memberName) is float value ? value : 0f;
}
