// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The three Harmony patches, all on the birds' own code (render-side). Each body is wrapped: a throw is logged (at most once a second) and never
// reaches the game; a throw in the step prefix hands the bird back to the game's own step.
using System.Numerics;
using HarmonyLib;
using Meitou.Game.Effects;

namespace PZA.Wildlife.Meitou;

public static class Patches
{
    /// <summary>Applies all of them or none; returns one line per patch.</summary>
    public static List<string> Apply(Harmony h)
    {
        var lines = new List<string>();
        try
        {
            h.Patch(Access.UpdateAttractors, postfix: Hook(nameof(AttractorsPostfix)));
            lines.Add("patched Birds.UpdateAttractors postfix (the corpses and people, host thread)");
            h.Patch(Access.Update, prefix: Hook(nameof(UpdatePrefix)));
            lines.Add("patched BirdFlocks.Update prefix (birds taken by a corpse, bird thread)");
            h.Patch(Access.Step, prefix: Hook(nameof(StepPrefix)));
            lines.Add("patched BirdFlocks.Step prefix (a held bird is moved by the rules)");
        }
        catch
        {
            h.UnpatchAll(h.Id);
            throw;
        }
        lines.Add(ApplyFold(h));
        return lines;
    }

    /// <summary>The folded-wing patches, on their own: a game without the members, or a failed patch, leaves only the wings spread.</summary>
    static string ApplyFold(Harmony h)
    {
        if (!Access.CanFold) return "folded wings OFF: the game lacks " + Access.FoldMissing;
        try
        {
            h.Patch(Access.AddPieces!, prefix: Hook(nameof(PiecesPrefix)), postfix: Hook(nameof(PiecesPostfix)));
            h.Patch(Access.LoadMesh!, prefix: Hook(nameof(LoadPrefix)));
            FoldOn = true;
            return "patched BirdFlocks.AddPieces and DebrisRenderer.Load (folded wings for landed birds)";
        }
        catch (Exception e)
        {
            h.Unpatch(Access.AddPieces!, HarmonyPatchType.All, h.Id);
            h.Unpatch(Access.LoadMesh!, HarmonyPatchType.All, h.Id);
            return "folded wings OFF: patching failed (" + e.GetType().Name + ": " + e.Message + ")";
        }
    }

    /// <summary>Both folded-wing patches are on.</summary>
    public static bool FoldOn { get; private set; }

    static void PiecesPrefix(List<global::Meitou.Game.Rendering.Objects.DebrisPiece> pieces, out int __state) => __state = pieces?.Count ?? 0;

    static void PiecesPostfix(List<global::Meitou.Game.Rendering.Objects.DebrisPiece> pieces, Func<string, Vector3?> centreOf, int __state)
    {
        try { FoldedMeshes.SwapPieces(pieces, __state, centreOf); }
        catch (Exception e) { Log.Exception("pieces", e); }
    }

    /// <summary>A marked bird mesh is made (or taken from the cache) here; every other name goes to the game's own Load.</summary>
    static bool LoadPrefix(global::Meitou.Game.Rendering.Objects.DebrisRenderer __instance, string file, object? limb, ref object? __result)
    {
        if (limb is not null || file is null || !BirdFold.Parse(file, out var plain, out var level)) return true;
        try
        {
            __result = FoldedMeshes.Get(__instance, file, plain, level);
            return false;
        }
        catch (Exception e)
        {
            Log.Exception("load", e);
            return true;
        }
    }

    static HarmonyMethod Hook(string name) => new(AccessTools.Method(typeof(Patches), name));

    static void AttractorsPostfix(object __instance)
    {
        try
        {
            if (Driver.On && Access.Session(__instance) is { } session) Driver.Feed(session);
        }
        catch (Exception e) { Log.Exception("feed", e); }
    }

    static void UpdatePrefix(BirdFlocks __instance, float dt, Vector3 eye)
    {
        try { Driver.BeforeUpdate(__instance, dt, eye); }
        catch (Exception e) { Log.Exception("update", e); }
    }

    static bool StepPrefix(object b, float dt)
    {
        try { return Driver.StepBird(b, dt); }
        catch (Exception e)
        {
            Log.Exception("step", e);
            return true;
        }
    }
}
