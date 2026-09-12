using System;

namespace SurroundSoundLab;

internal static class WeatherBedSpatialPolicy
{
    internal const float Height = 10f;

    internal static float Elevation(string domain, string path)
    {
        if (domain == "game")
            return path switch
            {
                "sounds/weather/tracks/rain-leafless.ogg" or "sounds/weather/tracks/rain-leafy.ogg" => 45f,
                "sounds/weather/wind-leafless.ogg" or "sounds/weather/wind-leafy.ogg" => 30f,
                "sounds/weather/tracks/hail.ogg" or "sounds/weather/tracks/lowtremble.ogg"
                    or "sounds/weather/tracks/verylowtremble.ogg" or "sounds/weather/lowgrumble.ogg"
                    or "sounds/weather/lightning-distant.ogg" => 45f,
                _ => 0f
            };
        if (domain == "vintagestorysurroundsound")
            return path switch
            {
                "sounds/weather/tracks/rain-surround-new.ogg" or "sounds/weather/tracks/rain-surround-quiet.ogg"
                    or "sounds/weather/tracks/rain-surround-loud.ogg" or "sounds/weather/tracks/rain-surround-canopy.ogg"
                    or "sounds/weather/rumble-low.ogg" or "sounds/weather/lightning-distant.ogg"
                    or "sounds/weather/hail.wav" => 45f,
                "sounds/weather/wind-surround-leafy2.ogg" or "sounds/weather/wind-surround-leafless2.ogg" => 30f,
                _ => 0f
            };
        return 0f;
    }

    // OpenAL warps each multichannel speaker direction toward the source by
    // a = distance / (2 * radius), when radius >= distance. A source overhead
    // gives elevation atan(a / (1-a)), retaining each channel's azimuth.
    internal static float Radius(float elevationDegrees)
    {
        float tangent = MathF.Tan(Math.Clamp(elevationDegrees, 1f, 45f) * MathF.PI / 180f);
        float amount = tangent / (1f + tangent);
        return Height / (2f * amount);
    }
}
