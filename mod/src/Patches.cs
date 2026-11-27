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
        lines.Add(ApplyFireflies(h));
        if (FirefliesOn) lines.Add(ApplyFireflyLights(h));
        lines.Add(NightSoundsHooks.Apply(h, name => new HarmonyMethod(AccessTools.Method(typeof(NightSoundsHooks), name))));
        return lines;
    }

    /// <summary>Glowing fireflies patch on ParticleRenderer.Prepare, on its own: a missing member leaves birds working.</summary>
    static string ApplyFireflies(Harmony h)
    {
        if (!Access.CanDrawFireflies) return "swamp fireflies OFF: the game lacks " + Access.FireflyMissing;
        try
        {
            h.Patch(Access.ParticlePrepare!, prefix: Hook(nameof(PreparePrefix)), postfix: Hook(nameof(PreparePostfix)));
            FirefliesOn = true;
            return "patched ParticleRenderer.Prepare (glowing swamp fireflies at dusk/night)";
        }
        catch (Exception e)
        {
            h.Unpatch(Access.ParticlePrepare!, HarmonyPatchType.All, h.Id);
            return "swamp fireflies OFF: patching failed (" + e.GetType().Name + ": " + e.Message + ")";
        }
    }

    /// <summary>Real light from the fireflies (CharacterRenderer.CollectLamps postfix), on its own: a miss keeps the glowing motes unlit.</summary>
    static string ApplyFireflyLights(Harmony h)
    {
        string? missing;
        try { missing = FireflyLights.Resolve(); }
        catch (Exception e) { missing = e.GetType().Name + ": " + e.Message; }
        if (missing is not null) return "firefly light OFF: the game lacks " + missing;
        try
        {
            h.Patch(FireflyLights.CollectLamps!, postfix: Hook(nameof(LampsPostfix)));
            FireflyLightsOn = true;
            return $"patched CharacterRenderer.CollectLamps (the {FireflyPreset.LightCount} nearest fireflies light the ground)";
        }
        catch (Exception e)
        {
            h.Unpatch(FireflyLights.CollectLamps!, HarmonyPatchType.All, h.Id);
            return "firefly light OFF: patching failed (" + e.GetType().Name + ": " + e.Message + ")";
        }
    }

    /// <summary>The firefly light patch is on.</summary>
    public static bool FireflyLightsOn { get; private set; }

    static void LampsPostfix(List<global::Meitou.Data.World.WorldLight> into)
    {
        try { FireflyLights.Add(into); }
        catch (Exception e) { Log.Exception("firefly.light", e); }
    }

    /// <summary>Firefly renderer patch is on.</summary>
    public static bool FirefliesOn { get; private set; }

    static void PreparePrefix(global::Meitou.Game.Rendering.Particles.ParticleRenderer __instance, Matrix4x4 view, Vector3 eye, out (int Prior, int Added) __state)
    {
        int prior = __instance.Tracers.Count;
        int added = 0;
        try
        {
            Driver.StepFireflies(eye);
            added = FireflyMeshes.AddTracers(__instance.Tracers, view, eye);
        }
        catch (Exception e) { Log.Exception("firefly.draw", e); }
        __state = (prior, added);
    }

    static void PreparePostfix(global::Meitou.Game.Rendering.Particles.ParticleRenderer __instance, (int Prior, int Added) __state)
    {
        try
        {
            if (__state.Added > 0) FireflyMeshes.SwapToGlowTexture(__instance, __state.Prior);
            if (__instance.Tracers.Count > __state.Prior)
                __instance.Tracers.RemoveRange(__state.Prior, __instance.Tracers.Count - __state.Prior);
        }
        catch (Exception e) { Log.Exception("firefly.cleanup", e); }
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
        try
        {
            if (Driver.Settings.Frogs) FrogMesh.AddPieces(pieces, centreOf);
        }
        catch (Exception e) { Log.Exception("frogs", e); }
        try
        {
            if (Driver.Settings.Froxlotls) FroxMesh.AddPieces(pieces, centreOf);
        }
        catch (Exception e) { Log.Exception("frox", e); }
    }

    /// <summary>A marked bird mesh is made or taken from the cache here; every other name goes to the game's own Load.</summary>
    static bool LoadPrefix(global::Meitou.Game.Rendering.Objects.DebrisRenderer __instance, string file, object? limb, ref object? __result)
    {
        if (limb is null && FrogMesh.Is(file))
        {
            try
            {
                __result = FrogMesh.Get(__instance, file!);
                return false;
            }
            catch (Exception e)
            {
                Log.Exception("frog.load", e);
                return true;
            }
        }
        if (limb is null && FroxMesh.Is(file))
        {
            try
            {
                __result = FroxMesh.Get(__instance, file!);
                return false;
            }
            catch (Exception e)
            {
                Log.Exception("frox.load", e);
                return true;
            }
        }
        if (limb is not null || file is null || !BirdFold.ParsePose(file, out var plain, out var level)) return true;
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
            if ((Driver.On || Driver.Settings.Fireflies) && Access.Session(__instance) is { } session)
                Driver.Feed(session, Access.BiomeName(__instance, session), Access.RainStrength(__instance), Access.TerrainHeight(__instance));
            BiteEffects.Play(Driver.Core);   // host thread, between bird steps
        }
        catch (Exception e) { Log.Exception("feed", e); }
    }

    static void UpdatePrefix(BirdFlocks __instance, float dt, Vector3 eye, Vector3 focus)
    {
        try { Driver.BeforeUpdate(__instance, dt, eye, focus); }
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
