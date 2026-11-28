// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Night sounds in the game (rules: NightSounds.cs). Two optional patches, looked up by name once; a game without either keeps its own night
// sounds and says so in one log line:
//   AudioSystem.Update(region, weather, hour, sunrise, sunset, ...) prefix, host thread: the audio's sunset moves to dusk. Only the ambience's
//     day/night test reads it (AmbienceDirector: Time_in_Game state, the biome's Enter_<biome>_Night); the sky, the world and the AI keep the
//     game's 23:00.
//   WwisePlayer.SoundInstance constructor postfix: at night, a sound on the ambience object (AudioSystem.MusicObject, where the Enter_
//     events play) that is not a music clip gets its rolled random volume floored and lifted (NightSounds.LiftDb).
using System.Reflection;
using HarmonyLib;

namespace PZA.Wildlife.Meitou;

public static class NightSoundsHooks
{
    public static MethodInfo? AudioUpdate { get; private set; }
    public static ConstructorInfo? SoundCtor { get; private set; }
    static FieldInfo? s_gameObject, s_rolledVolume;
    static PropertyInfo? s_isMusic;
    static ulong s_ambienceObject = 100;
    static volatile bool s_night;
    public static string? Missing { get; private set; } = "not set up";
    public static bool On { get; private set; }

    /// <summary>The night state the last AudioSystem.Update saw (for the check).</summary>
    public static bool Night => s_night;

    public static string? Resolve()
    {
        var audio = AccessTools.TypeByName("Meitou.Game.Audio.AudioSystem");
        AudioUpdate = audio?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Update" && HasDouble(m, "hour") && HasDouble(m, "sunrise") && HasDouble(m, "sunset"));
        if (audio?.GetField("MusicObject", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } mo && mo.GetRawConstantValue() is ulong id)
            s_ambienceObject = id;

        var player = AccessTools.TypeByName("Meitou.Game.Audio.WwisePlayer");
        var sound = player is null ? null : AccessTools.Inner(player, "SoundInstance");
        SoundCtor = sound?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        s_rolledVolume = sound is null ? null : AccessTools.Field(sound, "_rolledVolume");
        s_gameObject = sound is null ? null : AccessTools.Field(sound, "GameObject");
        s_isMusic = sound is null ? null : AccessTools.Property(sound, "IsMusic");

        var lacks = new List<string>();
        if (AudioUpdate is null) lacks.Add("AudioSystem.Update(..., hour, sunrise, sunset, ...)");
        if (SoundCtor is null) lacks.Add("WwisePlayer.SoundInstance's constructor");
        if (s_rolledVolume is null || s_rolledVolume.FieldType != typeof(float)) lacks.Add("SoundInstance._rolledVolume");
        if (s_gameObject is null || s_gameObject.FieldType != typeof(ulong)) lacks.Add("Instance.GameObject");
        if (s_isMusic is null || s_isMusic.PropertyType != typeof(bool)) lacks.Add("SoundInstance.IsMusic");
        Missing = lacks.Count == 0 ? null : string.Join(", ", lacks);
        return Missing;
    }

    static bool HasDouble(MethodInfo m, string name) => m.GetParameters().Any(p => p.Name == name && p.ParameterType == typeof(double));

    public static string Apply(Harmony h, Func<string, HarmonyMethod> hook)
    {
        string? missing;
        try { missing = Resolve(); }
        catch (Exception e) { missing = e.GetType().Name + ": " + e.Message; }
        if (missing is not null) return "night sounds OFF: the game lacks " + missing;
        try
        {
            h.Patch(AudioUpdate!, prefix: hook(nameof(UpdatePrefix)));
            h.Patch(SoundCtor!, postfix: hook(nameof(SoundPostfix)));
            On = true;
            return $"patched AudioSystem.Update and WwisePlayer.SoundInstance (night ambience from {NightSoundsPreset.Dusk:0.0}h, night calls lifted)";
        }
        catch (Exception e)
        {
            h.Unpatch(AudioUpdate!, HarmonyPatchType.All, h.Id);
            h.Unpatch(SoundCtor!, HarmonyPatchType.All, h.Id);
            return "night sounds OFF: patching failed (" + e.GetType().Name + ": " + e.Message + ")";
        }
    }

    /// <summary>Host thread: the audio's sunset at dusk (when the switch is on), and the night state for the postfix.</summary>
    public static void UpdatePrefix(double hour, double sunrise, ref double sunset)
    {
        try
        {
            if (Driver.Settings.NightSounds)
            {
                double moved = NightSounds.Sunset(sunset, sunrise);
                if (moved != sunset) Counters.Add("night.dusk_sunset");
                sunset = moved;
            }
            s_night = NightSounds.IsNight(hour, sunrise, sunset);
        }
        catch (Exception e) { Log.Exception("night.update", e); }
    }

    /// <summary>A new sound: lifted when it is night, on the ambience object, not music, and the switch is on.</summary>
    public static void SoundPostfix(object __instance)
    {
        try
        {
            if (!s_night || !Driver.Settings.NightSounds) return;
            if ((ulong)s_gameObject!.GetValue(__instance)! != s_ambienceObject || (bool)s_isMusic!.GetValue(__instance)!) return;
            float rolled = (float)s_rolledVolume!.GetValue(__instance)!;
            s_rolledVolume.SetValue(__instance, NightSounds.LiftDb(rolled));
            Counters.Add("night.lifted");
        }
        catch (Exception e) { Log.Exception("night.sound", e); }
    }
}
