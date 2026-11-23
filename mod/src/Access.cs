// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The game's non-public types and members this mod reaches, looked up ONCE by name (MeitouClient 0.20.1 to the 0.22.2 nightlies). A missing one
// turns the mod off with a line naming it, never a crash later. All of them are the birds' own code (render-side, docs/game/birds.md):
//   Meitou.Game.Effects.BirdFlocks (public): Update(dt, eye, focus, frustum), the private Step(Group, Bird, int, float, Vector3, Vector4[]),
//     the private fields _groups (Group[]) and _ground (terrain height)
//   BirdFlocks.Group (private): Kind (BirdFlockData), Birds (List<Bird>)
//   BirdFlocks.Bird (private): Position, Forward (Vector3), Timer, Scale (float), State (BirdFlocks.BirdState, public), Mate, Attractor
//   Meitou.Game.Effects.Birds (internal): UpdateAttractors(GameDatabase), on the host thread before each bird step; its GameHost (primary
//     constructor field) has the internal field Session (WorldSession, public) whose CurrentSnapshot lists the characters
using System.Collections;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Gameplay;
using Meitou.Game.Effects;
using Meitou.Game.Host;

namespace PZA.Wildlife.Meitou;

public static class Access
{
    public static MethodInfo Update = null!, Step = null!, UpdateAttractors = null!;
    public static Type BirdType = null!, GroupType = null!;

    static FieldInfo s_groups = null!, s_ground = null!, s_mate = null!, s_attractor = null!, s_session = null!;
    static FieldInfo? s_host;
    static AccessTools.FieldRef<object, Vector3> s_position = null!, s_forward = null!;
    static AccessTools.FieldRef<object, float> s_timer = null!, s_scale = null!;
    static AccessTools.FieldRef<object, BirdFlocks.BirdState> s_state = null!;
    static AccessTools.FieldRef<object, BirdFlockData> s_kind = null!;
    static FieldInfo s_birds = null!;

    public static List<string> ResolveAll()
    {
        var missing = new List<string>();
        var flocks = typeof(BirdFlocks);
        BirdType = AccessTools.Inner(flocks, "Bird")!;
        GroupType = AccessTools.Inner(flocks, "Group")!;
        if (BirdType is null) missing.Add("type BirdFlocks.Bird");
        if (GroupType is null) missing.Add("type BirdFlocks.Group");
        if (missing.Count > 0) return missing;

        Update = Need(AccessTools.Method(flocks, "Update", [typeof(float), typeof(Vector3), typeof(Vector3), typeof(Vector4[])]),
            "BirdFlocks.Update(float, Vector3, Vector3, Vector4[])", missing);
        Step = Need(AccessTools.Method(flocks, "Step", [GroupType, BirdType, typeof(int), typeof(float), typeof(Vector3), typeof(Vector4[])]),
            "BirdFlocks.Step(Group, Bird, int, float, Vector3, Vector4[])", missing);
        s_groups = NeedField(flocks, "_groups", missing);
        s_ground = NeedField(flocks, "_ground", missing);
        if (s_ground is not null && s_ground.FieldType != typeof(Func<float, float, float>)) missing.Add("field BirdFlocks._ground (Func<float, float, float>)");

        s_kind = Ref<BirdFlockData>(GroupType!, "Kind", missing);
        s_birds = NeedField(GroupType!, "Birds", missing);
        s_position = Ref<Vector3>(BirdType!, "Position", missing);
        s_forward = Ref<Vector3>(BirdType!, "Forward", missing);
        s_timer = Ref<float>(BirdType!, "Timer", missing);
        s_scale = Ref<float>(BirdType!, "Scale", missing);
        s_state = Ref<BirdFlocks.BirdState>(BirdType!, "State", missing);
        s_mate = NeedField(BirdType!, "Mate", missing);
        s_attractor = NeedField(BirdType!, "Attractor", missing);

        var birds = typeof(BirdFlocks).Assembly.GetType("Meitou.Game.Effects.Birds");
        if (birds is null) missing.Add("type Meitou.Game.Effects.Birds");
        else
        {
            UpdateAttractors = Need(AccessTools.Method(birds, "UpdateAttractors"), "Birds.UpdateAttractors", missing);
            s_host = AccessTools.GetDeclaredFields(birds).FirstOrDefault(f => f.FieldType.FullName == "Meitou.Game.GameHost");
            if (s_host is null) missing.Add("Birds' GameHost field");
            else s_session = NeedField(s_host.FieldType, "Session", missing);
            if (s_session is not null && s_session.FieldType != typeof(WorldSession)) missing.Add("field GameHost.Session (WorldSession)");
        }
        ResolveFold();
        return missing;
    }

    // ---- folded wings (optional: a miss leaves the birds' wings spread, the rest works)
    public static MethodInfo? AddPieces, LoadMesh;
    static MethodInfo? s_build;
    static FieldInfo? s_meshes, s_find;
    static AccessTools.FieldRef<object, long>? s_id;
    public static string? FoldMissing { get; private set; }
    public static bool CanFold => FoldMissing is null;

    /// <summary>BirdFlocks.AddPieces(List of DebrisPiece, Func of string to Vector3?), Bird.Id; DebrisRenderer's private Load(string, string?,
    /// LimbLook?), Build(string, string, OgreMesh, LimbLook?, string?), _meshes (Dictionary of string to Mesh?) and _find.</summary>
    static void ResolveFold()
    {
        var r = typeof(global::Meitou.Game.Rendering.Objects.DebrisRenderer);
        var limb = typeof(global::Meitou.Game.Rendering.Objects.LimbLook);
        AddPieces = AccessTools.Method(typeof(BirdFlocks), "AddPieces",
            [typeof(List<global::Meitou.Game.Rendering.Objects.DebrisPiece>), typeof(Func<string, Vector3?>)]);
        LoadMesh = AccessTools.Method(r, "Load", [typeof(string), typeof(string), limb]);
        s_build = AccessTools.Method(r, "Build", [typeof(string), typeof(string), typeof(global::Meitou.Data.Ogre.OgreMesh), limb, typeof(string)]);
        s_meshes = AccessTools.Field(r, "_meshes");
        s_find = AccessTools.Field(r, "_find");
        var id = AccessTools.Field(BirdType, "Id");
        if (id is not null && id.FieldType == typeof(long)) s_id = AccessTools.FieldRefAccess<object, long>(id);
        var lacks = new List<string>();
        if (AddPieces is null) lacks.Add("BirdFlocks.AddPieces");
        if (LoadMesh is null) lacks.Add("DebrisRenderer.Load");
        if (s_build is null) lacks.Add("DebrisRenderer.Build");
        if (s_meshes is null || !typeof(System.Collections.IDictionary).IsAssignableFrom(s_meshes.FieldType)) lacks.Add("DebrisRenderer._meshes");
        if (s_find is null || s_find.FieldType != typeof(Func<string, string?>)) lacks.Add("DebrisRenderer._find");
        if (s_id is null) lacks.Add("Bird.Id");
        FoldMissing = lacks.Count == 0 ? null : string.Join(", ", lacks);
    }

    public static long Id(object bird) => s_id!(bird);
    public static System.Collections.IDictionary Meshes(object renderer) => (System.Collections.IDictionary)s_meshes!.GetValue(renderer)!;
    public static Func<string, string?> Find(object renderer) => (Func<string, string?>)s_find!.GetValue(renderer)!;
    public static object? Build(object renderer, string file, global::Meitou.Data.Ogre.OgreMesh ogre, string? diffuse) =>
        s_build!.Invoke(renderer, [file, file, ogre, null, diffuse]);
    public static object? Load(object renderer, string mesh) => LoadMesh!.Invoke(renderer, [mesh, null, null]);

    static MethodInfo Need(MethodInfo? m, string name, List<string> missing)
    {
        if (m is null) missing.Add("method " + name);
        return m!;
    }

    static FieldInfo NeedField(Type t, string name, List<string> missing)
    {
        var f = AccessTools.Field(t, name);
        if (f is null) missing.Add($"field {t.Name}.{name}");
        return f!;
    }

    static AccessTools.FieldRef<object, T> Ref<T>(Type t, string name, List<string> missing)
    {
        var f = AccessTools.Field(t, name);
        if (f is null || f.FieldType != typeof(T))
        {
            missing.Add($"field {t.Name}.{name} ({typeof(T).Name})");
            return null!;
        }
        return AccessTools.FieldRefAccess<object, T>(f);
    }

    // ---- the flocks (bird thread)
    public static Func<float, float, float> Ground(BirdFlocks f) => (Func<float, float, float>)s_ground.GetValue(f)!;
    public static Array Groups(BirdFlocks f) => (Array)s_groups.GetValue(f)!;
    public static BirdFlockData Kind(object group) => s_kind(group);
    public static IList Birds(object group) => (IList)s_birds.GetValue(group)!;
    public static ref Vector3 Position(object bird) => ref s_position(bird);
    public static ref Vector3 Forward(object bird) => ref s_forward(bird);
    public static ref float Timer(object bird) => ref s_timer(bird);
    public static float Scale(object bird) => s_scale(bird);
    public static ref BirdFlocks.BirdState State(object bird) => ref s_state(bird);
    public static object? Mate(object bird) => s_mate.GetValue(bird);
    public static void ClearMate(object bird) => s_mate.SetValue(bird, null);
    public static bool HasAttractor(object bird) => s_attractor.GetValue(bird) is not null;

    // ---- the host (host thread)
    public static WorldSession? Session(object birdsHost) => s_host?.GetValue(birdsHost) is { } host ? s_session.GetValue(host) as WorldSession : null;
}
