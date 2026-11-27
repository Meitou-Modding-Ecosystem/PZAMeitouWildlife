// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The link between the game's birds and the rules (WildCore):
//   host thread, Birds.UpdateAttractors postfix (just before each bird step starts): the snapshot's corpses and living characters near the
//     camera are copied into a fresh input (handed over by reference; the step starts after this, BirdThread's semaphore orders the two)
//   bird thread, BirdFlocks.Update prefix: the rules take the input, free birds of the game's flight may be taken by a corpse
//   bird thread, BirdFlocks.Step prefix: a held bird is moved by the rules instead of the game's step (false = the game's step is skipped)
// Nothing here touches the simulation, the AI or the saves: the birds are render-side only (docs/game/birds.md "Meitou").
using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Simulation;

namespace PZA.Wildlife.Meitou;

public static class Driver
{
    public static readonly WildSettings Settings = new();
    public static readonly WildCore Core = new() { Count = Counters.Add, WaterLevel = global::Meitou.Data.World.WorldWater.Height };
    public static readonly FireflyCore Fireflies = new() { Count = Counters.Add, WaterLevel = global::Meitou.Data.World.WorldWater.Height };
    public static readonly FrogCore Frogs = new() { Count = Counters.Add, WaterLevel = global::Meitou.Data.World.WorldWater.Height };
    /// <summary>The conscious living characters within 100 m of the camera pivot (what scares the frogs), taken on the host thread.</summary>
    static volatile List<Vector3> s_people = [];
    static long s_lastFrogNote;
    /// <summary>Froxlotls at the Swamp's water edges (FroxCore; drawn by FroxMesh).</summary>
    public static readonly FroxCore Frox = new() { Count = Counters.Add, WaterLevel = global::Meitou.Data.World.WorldWater.Height };
    static long s_lastFroxNote;

    sealed record EnvState(double GameHours, bool InSwamp, float Rain, bool Paused, Vector3 Focus, Func<float, float, float>? Ground);
    static volatile EnvState s_env = new(12.0, false, 0f, false, new Vector3(float.NaN), null);
    static long s_lastFireflyTick;

    /// <summary>One step's view of the world, made on the host thread.</summary>
    public sealed record Input(List<Corpse> Corpses, List<Vector3> Threats);

    static volatile Input? s_input;

    /// <summary>Hands the rules an input directly (the offline check and the fuzz harness, which have no game host).</summary>
    public static void SetInput(Input input) => s_input = input;
    static volatile bool s_on = true;
    public static bool On { get => s_on; set => s_on = value; }

    #region Host thread: the corpses and the people

    static readonly Dictionary<CharacterId, (long Key, long Tick)> s_seen = [];
    static long s_nextKey = 1;
    static object? s_world;

    /// <summary>From the snapshot: every dead character (animal or not) within reach of the camera eye is a corpse, every conscious living one a
    /// threat. A corpse's age counts from the tick it was first seen dead.</summary>
    public static void Feed(WorldSession session, string? biomeName = null, float rain = 0f, Func<float, float, float>? terrain = null)
    {
        if (!ReferenceEquals(s_world, session.World))
        {
            s_world = session.World;
            s_seen.Clear();
            Core.Reset();
            FoldedMeshes.Reset();
            Fireflies.Reset();
            Frogs.Reset();
            Frox.Step(0, new System.Numerics.Vector3(float.NaN), 0, false, null, []);
            s_lastFireflyTick = 0;
            Counters.Add("feed.new_world");
        }
        var prev = s_env;
        var pivot = session.Camera.Current.Target;
        s_env = new EnvState(
            session.World?.GameHours ?? 12.0,
            FireflyCore.IsSwampBiome(biomeName),
            rain,
            session.TimeScale <= 0,
            WildCore.Finite(pivot) ? pivot : prev.Focus,
            terrain ?? prev.Ground);
        var snapshot = session.CurrentSnapshot;
        var people = new List<Vector3>();
        var at = s_env.Focus;
        if (WildCore.Finite(at))
            foreach (var c in snapshot.Characters)
                if (c.Body is not { Dead: true } && c.Body is not { Unconscious: true }
                    && (c.Position.X - at.X) * (c.Position.X - at.X) + (c.Position.Z - at.Z) * (c.Position.Z - at.Z) < 1000f * 1000f)
                    people.Add(c.Position);
        s_people = people;
        float tickSeconds = session.World?.TickSeconds is > 0 and var ts ? ts : 1 / 30f;
        s_input = Read(snapshot.Characters, snapshot.Tick, tickSeconds);
    }

    /// <summary>Host/render thread, once a frame inside ParticleRenderer.Prepare: steps fireflies independently of BirdFlocks.</summary>
    /// <summary>The camera eye the fireflies were last stepped for (the lights pick the flies nearest to it).</summary>
    public static Vector3 FireflyEye = new(float.NaN);

    /// <summary>Host thread, once a frame (the frogs' drawing): the frogs by the hour and the swamp, scared by the people near the pivot.</summary>
    public static void StepFrogs(float dt)
    {
        var env = s_env;
        if (env.Paused) dt = 0f;
        Frogs.Step(dt, env.Focus, env.GameHours, env.InSwamp, env.Ground, s_people);
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(s_lastFrogNote, now).TotalSeconds < 10) return;
        s_lastFrogNote = now;
        if (env.InSwamp) Log.Line($"frogs: {Frogs.ActiveCount} sitting out near the camera, {s_people.Count} people near");
    }

    /// <summary>Host thread, once a frame (the froxlotls' drawing): like the frogs.</summary>
    public static void StepFrox(float dt)
    {
        var env = s_env;
        if (env.Paused) dt = 0f;
        Frox.Step(dt, env.Focus, env.GameHours, env.InSwamp, env.Ground, s_people);
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(s_lastFroxNote, now).TotalSeconds < 10) return;
        s_lastFroxNote = now;
        if (env.InSwamp) Log.Line($"froxlotls: {Frox.ActiveCount} out near the camera in {string.Join(", ", Frox.Pool.Select(c => FroxPreset.Colours[c]))}");
    }

    public static void StepFireflies(Vector3 eye)
    {
        FireflyEye = eye;
        long now = Stopwatch.GetTimestamp();
        float dt = s_lastFireflyTick == 0 ? 0f : (float)Stopwatch.GetElapsedTime(s_lastFireflyTick, now).TotalSeconds;
        s_lastFireflyTick = now;
        var env = s_env;
        if (env.Paused) dt = 0f;
        var focus = WildCore.Finite(env.Focus) ? env.Focus : eye;
        Fireflies.Step(dt, focus, env.GameHours, env.InSwamp, env.Ground, env.Rain);
    }

    /// <summary>The rules' input from a character list (public for the offline check).</summary>
    public static Input Read(IReadOnlyList<CharacterSnapshot> characters, long tick, float tickSeconds)
    {
        var corpses = new List<Corpse>();
        var living = new List<Vector3>();
        HashSet<CharacterId>? aliveDead = s_seen.Count > 4096 ? [] : null;
        foreach (var c in characters)
        {
            if (c.Body is { Dead: true })
            {
                if (!s_seen.TryGetValue(c.Id, out var seen)) s_seen[c.Id] = seen = (s_nextKey++, tick);
                aliveDead?.Add(c.Id);
                corpses.Add(new Corpse(seen.Key, c.Position, Math.Max(0, tick - seen.Tick) * tickSeconds, c.HasFlies));
            }
            else if (c.Body is not { Unconscious: true }) living.Add(c.Position);
        }
        if (corpses.Count == 0) living.Clear();   // nobody to scare off
        if (aliveDead is not null)
        {
            foreach (var k in s_seen.Keys.Where(id => !aliveDead.Contains(id)).ToList()) s_seen.Remove(k);
        }
        return new Input(corpses, living);
    }

    #endregion

    #region Bird thread: taking and moving birds

    static float s_lastDt;

    /// <summary>Before the game's step: the rules take this step's input; free birds may be taken. Off: every held bird is let go.</summary>
    public static void BeforeUpdate(BirdFlocks flocks, float dt, Vector3 eye, Vector3 focus)
    {
        var core = Core;
        var ground = Access.Ground(flocks);
        core.Ground = ground;
        var env = s_env;
        if (env.Ground is null && ground is not null) s_env = env with { Ground = ground };
        if (Patches.FoldOn) FoldedMeshes.BeginStep();
        if (!On)
        {
            if (core.HeldCount > 0) LetAllGo();
            Log.Tick(core);
            return;
        }
        if (s_input is { } input)
        {
            var (corpses, threats) = Near(input, eye);
            core.SetWorld(corpses, threats);
        }
        core.Advance(dt);
        s_lastDt = dt;
        if (core.CorpseCount == 0 && core.HeldCount == 0)
        {
            Log.Tick(core);
            return;
        }

        foreach (var group in Access.Groups(flocks))
        {
            var kind = Access.Kind(group!);
            var birds = Access.Birds(group!);
            foreach (var bird in birds)
            {
                if (bird is null || core.TryGet(bird, out _) || !Free(bird)) continue;
                if (core.TryClaim(bird, Access.Position(bird), Access.Forward(bird), kind.Speed, Access.Scale(bird), dt) is null) continue;
                Take(bird, birds);
            }
        }
        Log.Tick(core);
    }

    static readonly List<Corpse> s_near = [];
    static readonly List<Vector3> s_nearThreats = [];

    /// <summary>The corpses within <see cref="Preset.EyeReach"/> of the camera eye (the nearest 32), and the living characters within 800 units
    /// of one of them (the only ones that can scare a low bird). Public for the offline check.</summary>
    public static (List<Corpse>, List<Vector3>) Near(Input input, Vector3 eye)
    {
        s_near.Clear();
        s_nearThreats.Clear();
        float reach2 = Preset.EyeReach * Preset.EyeReach;
        foreach (var c in input.Corpses)
            if (Flat2(c.Position - eye) <= reach2) s_near.Add(c);
        if (s_near.Count > 32)
        {
            s_near.Sort((a, b) => Flat2(a.Position - eye).CompareTo(Flat2(b.Position - eye)));
            s_near.RemoveRange(32, s_near.Count - 32);
        }
        foreach (var t in input.Threats)
            foreach (var c in s_near)
                if (Flat2(t - c.Position) < 800 * 800)
                {
                    s_nearThreats.Add(t);
                    break;
                }
        return (s_near, s_nearThreats);
    }

    static float Flat2(Vector3 v) => v.X * v.X + v.Z * v.Z;

    /// <summary>The game's step for one bird: false = the rules moved it (the game's step is skipped).</summary>
    public static bool StepBird(object bird, float dt)
    {
        var core = Core;
        if (!core.TryGet(bird, out var held))
        {
            if (On && Patches.FoldOn) NoteFree(bird);
            return true;
        }
        bool keep = core.Step(bird, held, dt);
        Access.Position(bird) = held.Position;
        Access.Forward(bird) = held.Forward;
        if (!keep) GiveBack(bird);
        else if (Patches.FoldOn) FoldedMeshes.Note(Access.Id(bird), held.WingPose);
        return false;
    }

    static readonly System.Diagnostics.Stopwatch s_wingClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>A free bird in flight beats its wings while climbing and in bursts (BirdFold.FreePose); resting ones keep the game's mesh.</summary>
    static void NoteFree(object bird)
    {
        if (Access.State(bird) is not (BirdFlocks.BirdState.Wander or BirdFlocks.BirdState.Follow or BirdFlocks.BirdState.Circle)) return;
        var forward = Access.Forward(bird);
        float climb = forward.Length() > 1e-4f ? forward.Y / forward.Length() : 0;
        FoldedMeshes.Note(Access.Id(bird), BirdFold.FreePose(Access.Id(bird), s_wingClock.Elapsed.TotalSeconds, climb));
    }

    /// <summary>A bird the game flies freely (wandering, following, circling, resting) and that holds none of the game's attractors.</summary>
    static bool Free(object bird) =>
        Access.State(bird) is BirdFlocks.BirdState.Wander or BirdFlocks.BirdState.Follow or BirdFlocks.BirdState.Circle or BirdFlocks.BirdState.Rest
        && !Access.HasAttractor(bird);

    /// <summary>A taken bird is marked as circling an attractor (the game's mate search skips those), drops its mate, and its followers drop it.</summary>
    static void Take(object bird, System.Collections.IList flock)
    {
        Access.State(bird) = BirdFlocks.BirdState.Attracted;
        Access.ClearMate(bird);
        foreach (var other in flock)
        {
            if (other is null || !ReferenceEquals(Access.Mate(other), bird)) continue;
            Access.ClearMate(other);
            if (Access.State(other) == BirdFlocks.BirdState.Follow) Access.State(other) = BirdFlocks.BirdState.Wander;
        }
    }

    /// <summary>Back to the game's flight: wandering, a fresh timer.</summary>
    static void GiveBack(object bird)
    {
        Access.State(bird) = BirdFlocks.BirdState.Wander;
        Access.Timer(bird) = 10 + Random.Shared.NextSingle() * 30;
        Access.ClearMate(bird);
    }

    static void LetAllGo()
    {
        foreach (var (bird, held) in Core.ReleaseAll())
        {
            Access.Position(bird) = held.Position;
            Access.Forward(bird) = held.Forward;
            GiveBack(bird);
        }
        Counters.Add("off.released");
    }

    #endregion
}
