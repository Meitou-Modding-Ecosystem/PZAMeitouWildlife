// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Night sounds: the rules (pure). The game plays a biome's night ambience (the swamp's frogs and insect chorus, the canyons' crickets) only
// while the hour is not strictly between the GLOBAL CONSTANTS sunrise and sunset (5 and 23; MeitouClient docs/audio.md "Biome ambience"),
// and its random calls roll an extra volume down to -44 dB, so most nights were nearly silent until 23:00. Here the audio's sunset moves to
// dusk (the fireflies' time), and the night ambience's random part is floored and lifted a little. Sound only: nothing simulated or saved.
namespace PZA.Wildlife.Meitou;

/// <summary>The one ready-made preset (user 2026-11-27: "the night ambience starts at dusk instead of 23:00; the night calls are lifted a
/// little"). No sliders.</summary>
public static class NightSoundsPreset
{
    /// <summary>The hour the night ambience starts (the fireflies come out from 17:45 to 19:15).</summary>
    public const double Dusk = 19.5;
    /// <summary>A night sound's rolled random volume is kept at or above this (dB; the game rolls calls down to -44), then lifted by Lift (dB).</summary>
    public const float CallFloor = -12f, Lift = 2f;
}

public static class NightSounds
{
    /// <summary>The audio's sunset: the game's, moved earlier to <see cref="NightSoundsPreset.Dusk"/> (never later than the game's, and kept
    /// after sunrise so day still exists). Non-finite input is returned as it is.</summary>
    public static double Sunset(double gameSunset, double sunrise)
    {
        if (!double.IsFinite(gameSunset) || !double.IsFinite(sunrise)) return gameSunset;
        double dusk = Math.Min(gameSunset, NightSoundsPreset.Dusk);
        return dusk > sunrise ? dusk : gameSunset;
    }

    /// <summary>Night for the ambience: not strictly between sunrise and sunset (the game's own test, AmbienceDirector).</summary>
    public static bool IsNight(double hour, double sunrise, double sunset) =>
        double.IsFinite(hour) && !(hour > sunrise && hour < sunset);

    /// <summary>A night ambience sound's rolled random volume (dB): floored at <see cref="NightSoundsPreset.CallFloor"/>, plus
    /// <see cref="NightSoundsPreset.Lift"/>. Never quieter than it was; a non-finite roll is left as it is.</summary>
    public static float LiftDb(float rolled) =>
        float.IsFinite(rolled) ? Math.Max(rolled, NightSoundsPreset.CallFloor) + NightSoundsPreset.Lift : rolled;
}
