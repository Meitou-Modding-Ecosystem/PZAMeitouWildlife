// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The link between the game's birds and the rules (WildCore):
//   host thread, Birds.UpdateAttractors postfix (just before each bird step starts): the snapshot's corpses and living characters near the
//     camera are copied into a fresh input (handed over by reference; the step starts after this, BirdThread's semaphore orders the two)
//   bird thread, BirdFlocks.Update prefix: the rules take the input, free birds of the game's flight may be taken by a corpse
//   bird thread, BirdFlocks.Step prefix: a held bird is moved by the rules instead of the game's step (false = the game's step is skipped)
// Nothing here touches the simulation, the AI or the saves: the birds are render-side only (docs/game/birds.md "Meitou").
using System.Numerics;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Simulation;

namespace PZA.Wildlife.Meitou;

public static class Driver
{
    public static readonly WildSettings Settings = new();
    public static readonly WildCore Core = new() { Count = Counters.Add };

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
    public static void Feed(WorldSession session)
    {
        if (!ReferenceEquals(s_world, session.World))
        {
            s_world = session.World;
            s_seen.Clear();
            Counters.Add("feed.new_world");
        }
        var snapshot = session.CurrentSnapshot;
        float tickSeconds = session.World?.TickSeconds is > 0 and var ts ? ts : 1 / 30f;
        s_input = Read(snapshot.Characters, snapshot.Tick, tickSeconds);
    }

    /// <summary>The rules' input from a character list (public for the offline check).</summary>
    public static Input Read(IReadOnlyList<CharacterSnapshot> characters, long tick, float tickSeconds)
    {
        var corpses = new List<Corpse>();
        var living = new List<Vector3>();
        foreach (var c in characters)
        {
            if (c.Body is { Dead: true })
            {
                if (!s_seen.TryGetValue(c.Id, out var seen)) s_seen[c.Id] = seen = (s_nextKey++, tick);
                corpses.Add(new Corpse(seen.Key, c.Position, Math.Max(0, tick - seen.Tick) * tickSeconds, c.HasFlies));
            }
            else if (c.Body is not { Unconscious: true }) living.Add(c.Position);
        }
        if (corpses.Count == 0) living.Clear();   // nobody to scare off
        if (s_seen.Count > 4096) s_seen.Clear();
        return new Input(corpses, living);
    }

    #endregion

    #region Bird thread: taking and moving birds

    static float s_lastDt;

    /// <summary>Before the game's step: the rules take this step's input; free birds may be taken. Off: every held bird is let go.</summary>
    public static void BeforeUpdate(BirdFlocks flocks, float dt, Vector3 eye)
    {
        var core = Core;
        core.Ground = Access.Ground(flocks);
        if (Patches.FoldOn) FoldedMeshes.BeginStep();
        if (!On)
        {
            if (core.HeldCount > 0) LetAllGo();
            return;
        }
        if (s_input is { } input)
        {
            var (corpses, threats) = Near(input, eye);
            core.SetWorld(corpses, threats);
        }
        core.Advance(dt);
        s_lastDt = dt;
        if (core.CorpseCount == 0 && core.HeldCount == 0) return;

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
        if (!core.TryGet(bird, out var held)) return true;
        bool keep = core.Step(bird, held, dt);
        Access.Position(bird) = held.Position;
        Access.Forward(bird) = held.Forward;
        if (!keep) GiveBack(bird);
        else if (Patches.FoldOn) FoldedMeshes.Note(Access.Id(bird), held.FoldLevel);
        return false;
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
