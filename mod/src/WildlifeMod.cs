// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The code mod's entry (meitou.json "entry"). Patches are applied inside Initialize (MeitouClient docs/modding.md "Harmony"). Anything missing ->
// the mod stays off with a line saying why; the game goes on with its own birds.
using System.Reflection;
using Meitou.Localisation;
using Meitou.Modding;

namespace PZA.Wildlife.Meitou;

public sealed class WildlifeMod : IMod
{
    public void Initialize(ModContext context)
    {
        Log.Sink = context.Log;
        context.AddSettings(Driver.Settings);   // saved values are applied before this returns

        string version = typeof(WildlifeMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        Log.Line($"{version} for Meitou, by PZA: birds at carrion {(Driver.Settings.Carrion ? "ON" : "off")} (preset Carrion: pull "
                 + $"{Preset.FreshPull / 10:0} m fresh / {Preset.OldPull / 10:0} m old, up to {Preset.FreshCap} birds a corpse, flee within {Preset.FleeRadius / 10:0} m)");
        foreach (var line in Strings.Text.Init(context.Folder, Loc.Language?.Code ?? "en", Driver.Settings.Language, "lang/meitou.{code}.txt"))
            Log.Line(line);

        if (!Start(context.Harmony)) return;
        context.AddDebugCommand(Strings.T("debug.toggle"), _ =>
        {
            Driver.Settings.Carrion = !Driver.Settings.Carrion;
            return Driver.Settings.Carrion ? "ON" : "off";
        });
    }

    /// <summary>Resolves the game's members and patches; false (with a line) when anything is missing. Public for the offline check.</summary>
    public static bool Start(HarmonyLib.Harmony harmony)
    {
        var missing = Access.ResolveAll();
        if (missing.Count > 0)
        {
            Log.Line("stays OFF: the game lacks " + string.Join("; ", missing) + " (built for Meitou 0.20.1)");
            return false;
        }
        try
        {
            foreach (var line in Patches.Apply(harmony)) Log.Line(line);
            return true;
        }
        catch (Exception e)
        {
            Log.Line("stays OFF: patching failed (" + e.GetType().Name + ": " + e.Message + ")");
            return false;
        }
    }
}
