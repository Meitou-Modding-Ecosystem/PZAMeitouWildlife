// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Real light from the swamp fireflies: the nearest few flies become small point lights in Meitou's own lamp pass, the way the lanterns
// characters carry do (docs/render-lights.md "Which lights": WorldFrame.UpdateLamps fills WorldLamps.Moving each frame, and
// CharacterRenderer.CollectLamps adds the carried lanterns to it). A postfix on CollectLamps appends ours, so the ground, the reeds and the
// people near a fly glow with its flash. Display only: the lamps are rebuilt every frame and never saved.
// WorldLight's constructor grew over Meitou's releases, so it is called by reflection with its parameters filled by name; a game without
// CollectLamps or a usable constructor keeps the glowing motes and casts no light (one log line says so).
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.World;

namespace PZA.Wildlife.Meitou;

public static class FireflyLights
{
    static ConstructorInfo? s_ctor;
    static ParameterInfo[] s_params = [];
    public static MethodInfo? CollectLamps { get; private set; }
    public static string? Missing { get; private set; } = "not set up";

    /// <summary>Finds CharacterRenderer.CollectLamps(List of WorldLight) and WorldLight's longest constructor (every parameter must be one this
    /// file can fill); returns what is missing, or null.</summary>
    public static string? Resolve()
    {
        var renderer = AccessTools.TypeByName("Meitou.Game.Rendering.Characters.CharacterRenderer");
        CollectLamps = renderer is null ? null : AccessTools.Method(renderer, "CollectLamps", [typeof(List<WorldLight>)]);
        s_ctor = typeof(WorldLight).GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        s_params = s_ctor?.GetParameters() ?? [];
        var unknown = s_params.Where(p => Value(p, Vector3.Zero, 0, 0, 0) is Unfillable).Select(p => p.Name).ToList();
        Missing = CollectLamps is null ? "CharacterRenderer.CollectLamps(List<WorldLight>)"
                : s_ctor is null ? "a WorldLight constructor"
                : unknown.Count > 0 ? "WorldLight parameters " + string.Join(", ", unknown)
                : null;
        return Missing;
    }

    /// <summary>Render thread, once a frame (CollectLamps postfix): the nearest flies within reach of the eye, brightest light at the flash.</summary>
    public static void Add(List<WorldLight> into)
    {
        if (Missing is not null || into is null || !Driver.Settings.Fireflies) return;
        var snap = Driver.Fireflies.Snapshot();
        if (snap.Length == 0) return;

        var eye = Driver.FireflyEye;
        if (!WildCore.Finite(eye)) return;
        float reach2 = FireflyPreset.LightReach * FireflyPreset.LightReach;
        var nearest = snap.Where(f => WildCore.Finite(f.Position) && f.Brightness > 0 && Vector3.DistanceSquared(f.Position, eye) < reach2)
            .OrderBy(f => Vector3.DistanceSquared(f.Position, eye))
            .Take(FireflyPreset.LightCount);

        int added = 0;
        foreach (var f in nearest)
        {
            float power = FireflyPreset.LightPower * f.Brightness;
            if (power < 0.005f) continue;
            var args = s_params.Select(p => Value(p, f.Position, power, FireflyPreset.LightRadius, f.Id)).ToArray();
            into.Add((WorldLight)s_ctor!.Invoke(args));
            added++;
        }
        if (added > 0) Counters.Add("fireflies.lit");
    }

    sealed class Unfillable;
    static readonly Vector3 Colour = new(0.75f, 1.0f, 0.30f);

    /// <summary>One constructor argument by its name (the record's own parameter names), or <see cref="Unfillable"/> for a name unknown here
    /// that has no default.</summary>
    static object? Value(ParameterInfo p, Vector3 at, float power, float radius, long id) => p.Name?.ToLowerInvariant() switch
    {
        "position" => at,
        "colour" or "color" => Colour,
        "intensity" or "power" => power,
        "radius" => radius,
        "type" => Enum.Parse(p.ParameterType, "Point"),
        "direction" => -Vector3.UnitY,
        "innerangle" or "outerangle" => 0f,
        "falloff" => 1f,
        "effect" => Enum.Parse(p.ParameterType, "None"),
        "rule" => Enum.Parse(p.ParameterType, "WhenBuildingOn"),
        "interior" => false,
        "floor" => 0,
        "lightid" => "pza.firefly",
        "buildingid" => "",
        "placementid" => "pza.firefly/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ when p.HasDefaultValue => p.DefaultValue,
        _ => new Unfillable(),
    };
}
