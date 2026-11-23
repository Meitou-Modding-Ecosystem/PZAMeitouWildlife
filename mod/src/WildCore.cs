// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The pure rules: which birds a corpse draws, and how a drawn bird circles, spirals down, lands, pecks and flees. No Meitou type is used here
// (System.Numerics only), so the offline check and the fuzz harness run it as it is. 1 world unit = 10 cm (MeitouClient docs, terrain).
// Everything is render-side: the game's birds fly on the display's real time and are not part of the simulation or the saves.
using System.Numerics;

namespace PZA.Wildlife.Meitou;

/// <summary>The one ready-made preset ("Carrion"): every number of the behaviour. No sliders (the user prefers presets).</summary>
public static class Preset
{
    /// <summary>How far a corpse draws birds (units, horizontal): a fresh kill 3 km, an old one 1 km (scavengers spot carrion from far off).</summary>
    public const float FreshPull = 30000, OldPull = 10000;
    /// <summary>The most birds one corpse takes: fresh 6, old 2.</summary>
    public const int FreshCap = 6, OldCap = 2;
    /// <summary>Game seconds until a corpse counts as old (or as soon as it has flies).</summary>
    public const float OldAge = 4 * 3600;
    /// <summary>The chance per second that a bird in reach takes the corpse: fresh, old.</summary>
    public const float FreshRate = 0.6f, OldRate = 0.15f;
    /// <summary>The most birds held at once, over every corpse.</summary>
    public const int MaxBirds = 40;
    /// <summary>The high circle while the bird waits: radius and height over the ground (units), and how long it circles at least (s).</summary>
    public const float GatherRadiusMin = 350, GatherRadiusMax = 550, GatherHeightMin = 250, GatherHeightMax = 450, GatherMin = 4, GatherMax = 12;
    /// <summary>No living character this close to the corpse (units, horizontal), or the birds stay up.</summary>
    public const float ClearRadius = 350;
    /// <summary>The spiral down: its length (s), the low circle's radius and height (units).</summary>
    public const float DescendMin = 9, DescendMax = 15, LowRadius = 60, LowHeight = 35;
    /// <summary>Where a bird lands: 0.7 to 1.4 of its wingspan from the corpse (at least SpotMin units), never closer than one wingspan to
    /// another landed bird; how long it stays (s). Wingspan: the bird mesh's 26 units (birdtype1, docs/game/birds.md) times its scale.</summary>
    public const float Wingspan = 26, SpotNear = 0.7f, SpotFar = 1.4f, SpotMin = 12, StayMin = 25, StayMax = 70;
    /// <summary>A character this close (units, 3D: 20 m) makes a landed or low bird burst up; a descending bird below LowAlert counts as low.</summary>
    public const float FleeRadius = 200, LowAlert = 150;
    /// <summary>The burst: height reached before the game's flight takes over (units), its longest time and startle delay (s), the spread (rad).</summary>
    public const float FleeTopMin = 250, FleeTopMax = 420, FleeLongest = 7, StartleMax = 0.35f, FleeSpread = 0.6f;
    /// <summary>After a scare, seconds before the bird may come back; after a calm take-off (fed), seconds before it takes a corpse again.</summary>
    public const float CooldownMin = 25, CooldownMax = 50, FedMin = 60, FedMax = 120;
    /// <summary>Corpses beyond this (units) from the camera draw no birds.</summary>
    public const float EyeReach = 25000;
    /// <summary>A landed bird's origin sits this many mesh units over the ground times its scale (the mesh is drawn by its origin, #409).</summary>
    public const float LiftPerScale = 2.0f;
    /// <summary>Pecking: head dip (rad), hop length and height (units), turn (rad).</summary>
    public const float DipAngle = 0.45f, HopMin = 1.5f, HopMax = 5, HopHeight = 3, HopSeconds = 0.45f, TurnMin = 0.35f, TurnMax = 1.4f;
}

/// <summary>A corpse as the rules see it: a key, where it lies, its age in game seconds and whether it is old (flies).</summary>
public readonly record struct Corpse(long Key, Vector3 Position, float Age, bool Old);

/// <summary>What a held bird is doing.</summary>
public enum Phase { Gather = 1, Descend = 2, Approach = 3, Peck = 4, Flee = 5, Done = 6 }

/// <summary>One bird the rules hold: where it is and faces (written back to the game's bird each step), and its phase's state.</summary>
public sealed class WildBird
{
    public Phase Phase;
    public long Corpse;
    public Vector3 Position, Forward;
    /// <summary>The kind's speed (units/s) and the bird's mesh scale.</summary>
    public float Speed, Scale;
    /// <summary>Seconds in the phase, and the phase's length.</summary>
    public float Clock, Length;
    // circling and the spiral
    public Vector3 Centre;
    public float Radius, Height, Theta, R0, H0, Spin = 1;
    // the approach and the landing spot
    public Vector3 From, Spot;
    // pecking
    public float Yaw, Pitch, ActionClock, NextAction, TurnFrom, TurnTo;
    public int Action;
    public Vector3 HopFrom, HopTo;
    // fleeing
    public float Delay, FlySpeed, FleeTop;
    public Vector3 FleeDir;
    public bool Scared;

    /// <summary>How folded the wings are drawn (BirdFold levels): 100 on the ground, 50 for a hop's wing flick, the last of the glide in and
    /// the first moment of a take-off, 0 in flight.</summary>
    public int FoldLevel => Phase switch
    {
        Phase.Peck => Action == 2 && ActionClock > 0.08f && ActionClock < Preset.HopSeconds - 0.08f ? BirdFold.Half : BirdFold.Full,
        Phase.Approach => Length > 0 && Clock / Length >= 0.92f ? BirdFold.Full : Length > 0 && Clock / Length >= 0.7f ? BirdFold.Half : 0,
        Phase.Flee => Clock < Delay ? BirdFold.Full : Clock < Delay + 0.15f ? BirdFold.Half : 0,
        _ => 0,
    };
}

public sealed class WildCore
{
    /// <summary>The terrain's height at a world x, z (the game's bird step gives its own).</summary>
    public Func<float, float, float> Ground = (_, _) => 0;
    /// <summary>Counts a rule each time it fires (the mod's 5 s counter line).</summary>
    public Action<string> Count = _ => { };
    /// <summary>Real seconds the rules have run.</summary>
    public float Now { get; private set; }

    readonly Random _random;
    readonly Dictionary<long, Corpse> _corpses = [];
    readonly List<Vector3> _threats = [];
    readonly Dictionary<long, int> _taken = [];
    readonly Dictionary<object, WildBird> _held = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<object, float> _cooldown = new(ReferenceEqualityComparer.Instance);

    public WildCore(int seed = 20261122) => _random = new Random(seed);

    /// <summary>The birds held now.</summary>
    public int HeldCount => _held.Count;
    public IEnumerable<KeyValuePair<object, WildBird>> Held => _held;
    /// <summary>The birds a corpse holds.</summary>
    public int TakenBy(long corpse) => _taken.GetValueOrDefault(corpse);
    public bool TryGet(object handle, out WildBird bird) => _held.TryGetValue(handle, out bird!);
    public int CorpseCount => _corpses.Count;

    #region The world

    /// <summary>This step's corpses and living characters (non-finite ones are left out).</summary>
    public void SetWorld(IEnumerable<Corpse> corpses, IEnumerable<Vector3> threats)
    {
        _corpses.Clear();
        foreach (var c in corpses)
            if (Finite(c.Position) && float.IsFinite(c.Age)) _corpses[c.Key] = c;

        _threats.Clear();
        foreach (var t in threats)
            if (Finite(t)) _threats.Add(t);
    }

    /// <summary>The real time moves on (prunes old cooldowns now and then).</summary>
    public void Advance(float dt)
    {
        Now += Clean(dt);
        if (_cooldown.Count < 512) return;
        foreach (var k in _cooldown.Where(p => p.Value <= Now).Select(p => p.Key).ToList()) _cooldown.Remove(k);
    }

    /// <summary>0 (old) .. 1 (just killed).</summary>
    public static float Freshness(Corpse c) => c.Old ? 0 : 1 - Math.Clamp(c.Age / Preset.OldAge, 0, 1);
    public static float Pull(Corpse c) => Lerp(Preset.OldPull, Preset.FreshPull, Freshness(c));
    public static int Cap(Corpse c) => (int)MathF.Round(Lerp(Preset.OldCap, Preset.FreshCap, Freshness(c)));
    static float Rate(Corpse c) => Lerp(Preset.OldRate, Preset.FreshRate, Freshness(c));

    /// <summary>Whether a living character is within <see cref="Preset.ClearRadius"/> of the corpse (horizontal): the birds stay up.</summary>
    public bool Hot(Vector3 at)
    {
        foreach (var t in _threats)
            if (Flat(t - at).LengthSquared() < Preset.ClearRadius * Preset.ClearRadius) return true;
        return false;
    }

    /// <summary>The nearest living character within <see cref="Preset.FleeRadius"/> (3D); null for none.</summary>
    public Vector3? ThreatNear(Vector3 at)
    {
        Vector3? best = null;
        float bestD = Preset.FleeRadius * Preset.FleeRadius;
        foreach (var t in _threats)
        {
            float d = Vector3.DistanceSquared(t, at);
            if (d < bestD) (best, bestD) = (t, d);
        }
        return best;
    }

    #endregion

    #region Taking a bird

    /// <summary>A free bird of the game's flight, this step: it may take the corpse that pulls it hardest (not full, in reach, not on cooldown).
    /// Returns the held bird, or null when it stays with the game.</summary>
    public WildBird? TryClaim(object handle, Vector3 position, Vector3 forward, float speed, float scale, float dt)
    {
        dt = Clean(dt);
        if (dt <= 0 || !Finite(position) || !Finite(forward) || _held.ContainsKey(handle)) return null;
        if (_held.Count >= Preset.MaxBirds) return null;
        if (_cooldown.TryGetValue(handle, out var until) && until > Now) return null;

        Corpse? best = null;
        float score = 1;
        foreach (var c in _corpses.Values)
        {
            if (_taken.GetValueOrDefault(c.Key) >= Cap(c)) continue;
            float pull = Pull(c), d2 = Flat(c.Position - position).LengthSquared();
            float s = pull * pull / Math.Max(d2, 1);
            if (s > score) (best, score) = (c, s);
        }
        if (best is not { } corpse) return null;
        if (_random.NextDouble() >= 1 - Math.Exp(-Rate(corpse) * dt)) return null;

        var b = new WildBird
        {
            Phase = Phase.Gather,
            Corpse = corpse.Key,
            Position = position,
            Forward = Unit(forward, Vector3.UnitZ),
            Speed = Math.Clamp(float.IsFinite(speed) ? speed : 300, 120, 700),
            Scale = Math.Clamp(float.IsFinite(scale) ? scale : 1, 0.05f, 20),
            Centre = corpse.Position,
            Radius = Range(Preset.GatherRadiusMin, Preset.GatherRadiusMax),
            Height = Range(Preset.GatherHeightMin, Preset.GatherHeightMax),
            Length = Range(Preset.GatherMin, Preset.GatherMax),
            Spin = _random.Next(2) == 0 ? 1 : -1,
        };
        _held[handle] = b;
        _taken[corpse.Key] = _taken.GetValueOrDefault(corpse.Key) + 1;
        Count(corpse.Old || Freshness(corpse) < 0.5f ? "claim.old" : "claim.fresh");
        return b;
    }

    /// <summary>Lets every held bird go (the switch turned off); returns them so the game's birds get their place back.</summary>
    public List<KeyValuePair<object, WildBird>> ReleaseAll()
    {
        var all = _held.ToList();
        foreach (var (_, b) in all) Finish(b, 0);
        _held.Clear();
        _taken.Clear();
        return all;
    }

    #endregion

    #region The step

    /// <summary>Moves a held bird by <paramref name="dt"/> real seconds. False: the bird is let go this step (its Position and Forward are where
    /// the game's flight goes on from).</summary>
    public bool Step(object handle, WildBird b, float dt)
    {
        dt = Math.Min(Clean(dt), 0.25f);
        var (keepP, keepF) = (b.Position, b.Forward);

        if (!React(b)) return Let(handle, b, b.Scared ? Range(Preset.CooldownMin, Preset.CooldownMax) : 0);
        bool done = Move(b, dt);
        Settle(b, keepP, keepF);
        if (!done && b.Phase != Phase.Done) return true;
        return Let(handle, b, b.Scared ? Range(Preset.CooldownMin, Preset.CooldownMax) : Range(Preset.FedMin, Preset.FedMax));
    }

    /// <summary>The corpse and the people around: a gone corpse lets a flying bird go (a landed one takes off), a person near a landed or low
    /// bird scatters the corpse's birds, a person near the corpse sends a descending bird back up. False: let it go now.</summary>
    bool React(WildBird b)
    {
        bool landed = b.Phase is Phase.Peck or Phase.Approach;
        if (!_corpses.TryGetValue(b.Corpse, out var corpse))
        {
            if (landed) StartFlee(b, null);
            else if (b.Phase != Phase.Flee)
            {
                Count("release.corpse_gone");
                return false;
            }
            return true;
        }

        if (b.Phase is Phase.Gather or Phase.Descend) b.Centre = corpse.Position;
        else if (landed && Flat(corpse.Position - b.Centre).LengthSquared() > 40 * 40)
        {
            Count("takeoff.corpse_moved");
            StartFlee(b, null);
            return true;
        }

        bool low = landed || b.Phase == Phase.Descend && b.Position.Y - Ground(b.Position.X, b.Position.Z) < Preset.LowAlert;
        if (low && ThreatNear(b.Position) is { } threat)
        {
            Scatter(b.Corpse, threat);
            return true;
        }
        if (b.Phase == Phase.Descend && Hot(b.Centre))
        {
            Count("descend.aborted");
            BeginGather(b);
        }
        return true;
    }

    /// <summary>Every landed or low bird of the corpse bursts up, each its own way (the flock scatters).</summary>
    void Scatter(long corpse, Vector3 threat)
    {
        Count("scatter");
        foreach (var o in _held.Values)
        {
            if (o.Corpse != corpse || o.Phase is Phase.Flee or Phase.Done or Phase.Gather) continue;
            if (o.Phase == Phase.Descend && o.Position.Y - Ground(o.Position.X, o.Position.Z) >= Preset.LowAlert) continue;
            StartFlee(o, threat);
        }
    }

    /// <summary>The phase's motion; true when the bird is done (to be let go).</summary>
    bool Move(WildBird b, float dt)
    {
        b.Clock += dt;
        switch (b.Phase)
        {
            case Phase.Gather: Gather(b, dt); return false;
            case Phase.Descend: Descend(b, dt); return false;
            case Phase.Approach: Approach(b); return false;
            case Phase.Peck: Peck(b, dt); return false;
            case Phase.Flee: return Flee(b, dt);
            default: return true;
        }
    }

    /// <summary>Never below the ground; a non-finite result puts the last place back and ends the bird.</summary>
    void Settle(WildBird b, Vector3 keepP, Vector3 keepF)
    {
        if (!Finite(b.Position) || !Finite(b.Forward))
        {
            Count("error.nonfinite");
            (b.Position, b.Forward) = (keepP, keepF);
            b.Phase = Phase.Done;
        }
        float g = Ground(b.Position.X, b.Position.Z);
        if (float.IsFinite(g) && b.Position.Y < g) b.Position.Y = g;
        b.Forward = Unit(b.Forward, Vector3.UnitZ);
    }

    /// <summary>Lets the bird go: back to the game's flight, a cooldown before it may be taken again.</summary>
    bool Let(object handle, WildBird b, float cooldown)
    {
        Finish(b, cooldown);
        if (_held.Remove(handle))
        {
            int n = _taken.GetValueOrDefault(b.Corpse) - 1;
            if (n > 0) _taken[b.Corpse] = n;
            else _taken.Remove(b.Corpse);
        }
        if (cooldown > 0) _cooldown[handle] = Now + cooldown;
        Count("release");
        return false;
    }

    /// <summary>A place and heading the game's flight can go on from: finite, a unit heading not pointing down.</summary>
    void Finish(WildBird b, float _)
    {
        b.Phase = Phase.Done;
        if (!Finite(b.Position)) b.Position = b.Centre;
        float g = Ground(b.Position.X, b.Position.Z);
        if (float.IsFinite(g) && b.Position.Y < g) b.Position.Y = g;
        var f = Unit(b.Forward, Vector3.UnitZ);
        if (f.Y < -0.2f) f = Unit(new Vector3(f.X, 0.2f, f.Z), Vector3.UnitZ);
        b.Forward = f;
    }

    #endregion

    #region The phases

    /// <summary>Circles high round the corpse (steered, about 1.2 rad/s), and spirals down once it has circled long enough and nobody is near.</summary>
    void Gather(WildBird b, float dt)
    {
        var rel = Flat(b.Position - b.Centre);
        float theta = MathF.Atan2(rel.Z, rel.X) + b.Spin * 0.35f;
        var aim = b.Centre + new Vector3(MathF.Cos(theta), 0, MathF.Sin(theta)) * b.Radius;
        aim.Y = Math.Max(Ground(aim.X, aim.Z), Ground(b.Centre.X, b.Centre.Z)) + b.Height;
        b.Forward = Turn(b.Forward, aim - b.Position, 1.2f * dt);
        b.Position += b.Forward * (b.Speed * dt);

        float r = rel.Length();
        bool onCircle = MathF.Abs(r - b.Radius) < 0.35f * b.Radius;
        if (b.Clock >= b.Length && onCircle && !Hot(b.Centre)) BeginDescend(b, rel);
    }

    void BeginGather(WildBird b)
    {
        b.Phase = Phase.Gather;
        b.Clock = 0;
        b.Length = Range(Preset.GatherMin, Preset.GatherMax);
    }

    void BeginDescend(WildBird b, Vector3 rel)
    {
        b.Phase = Phase.Descend;
        b.Clock = 0;
        b.Length = Range(Preset.DescendMin, Preset.DescendMax);
        b.R0 = Math.Max(rel.Length(), Preset.LowRadius);
        b.H0 = Math.Max(b.Position.Y - Ground(b.Position.X, b.Position.Z), Preset.LowHeight);
        b.Theta = MathF.Atan2(rel.Z, rel.X);
        float turn = rel.X * b.Forward.Z - rel.Z * b.Forward.X;
        b.Spin = turn >= 0 ? 1 : -1;
        Count("descend");
    }

    /// <summary>A scripted spiral: radius and height ease to the low circle while the bird slows to half speed.</summary>
    void Descend(WildBird b, float dt)
    {
        float e = Smooth(b.Clock / b.Length);
        float r = Lerp(b.R0, Preset.LowRadius, e), h = Lerp(b.H0, Preset.LowHeight, e), speed = Lerp(b.Speed, 0.45f * b.Speed, e);
        b.Theta += b.Spin * speed / Math.Max(r, 1) * dt;
        var next = b.Centre + new Vector3(MathF.Cos(b.Theta), 0, MathF.Sin(b.Theta)) * r;
        next.Y = Ground(next.X, next.Z) + h;
        b.Forward = Unit(next - b.Position, b.Forward);
        b.Position = next;
        if (b.Clock >= b.Length) BeginApproach(b);
    }

    void BeginApproach(WildBird b)
    {
        b.Spot = FreeSpot(b);
        b.From = b.Position;
        b.Phase = Phase.Approach;
        b.Clock = 0;
        b.Length = Math.Clamp(Vector3.Distance(b.From, b.Spot) / (0.35f * b.Speed), 1.5f, 5);
        Count("approach");
    }

    /// <summary>A landing spot beside the corpse, a wingspan clear of the other birds there (the best of 12 tries).</summary>
    Vector3 FreeSpot(WildBird b)
    {
        float span = Span(b);
        var best = b.Centre;
        float bestGap = -1;
        for (int i = 0; i < 12; i++)
        {
            float angle = Range(0, MathF.Tau), dist = Math.Max(Preset.SpotMin, Range(Preset.SpotNear, Preset.SpotFar) * span);
            var spot = b.Centre + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * dist;
            float gap = float.MaxValue;
            foreach (var o in _held.Values)
                if (o != b && o.Corpse == b.Corpse && o.Phase is Phase.Approach or Phase.Peck)
                    gap = Math.Min(gap, Flat(o.Spot - spot).Length() / Math.Max(Math.Max(span, Span(o)), 1));
            if (gap > bestGap) (best, bestGap) = (spot, gap);
            if (gap >= 1) break;
        }
        return best;
    }

    static float Span(WildBird b) => Preset.Wingspan * b.Scale;

    /// <summary>The glide to the spot: eased along the ground, the height falling away, a flare at the end; then it lands.</summary>
    void Approach(WildBird b)
    {
        float u = Math.Clamp(b.Clock / b.Length, 0, 1), s = Smooth(u);
        var flat = Vector3.Lerp(Flat(b.From), Flat(b.Spot), s);
        float h0 = b.From.Y - Ground(b.From.X, b.From.Z);
        var next = new Vector3(flat.X, Ground(flat.X, flat.Z) + h0 * (1 - u) * (1 - u) + Lift(b) * u, flat.Z);
        var step = next - b.Position;
        if (step.LengthSquared() > 1e-4f) b.Forward = Unit(new Vector3(step.X, step.Y * 0.5f, step.Z), b.Forward);
        b.Position = next;
        if (u < 1) return;

        b.Phase = Phase.Peck;
        b.Clock = 0;
        b.Length = Range(Preset.StayMin, Preset.StayMax);
        b.Yaw = MathF.Atan2(b.Forward.X, b.Forward.Z);
        b.Pitch = 0;
        b.Action = 0;
        b.ActionClock = 0;
        b.NextAction = Range(0.4f, 1.2f);
        b.Spot = new Vector3(flat.X, 0, flat.Z);
        Count("land");
    }

    /// <summary>On the ground: head dips, small hops and turns from the bird's transform (the mesh has no animation); takes off when fed.</summary>
    void Peck(WildBird b, float dt)
    {
        if (b.Clock >= b.Length)
        {
            Count("takeoff.fed");
            StartFlee(b, null);
            return;
        }

        b.ActionClock += dt;
        float bump = 0;
        switch (b.Action)
        {
            case 0:
                if (b.ActionClock >= b.NextAction) PickAction(b);
                break;
            case 1:   // head dip
                b.Pitch = -Preset.DipAngle * MathF.Sin(MathF.PI * Math.Clamp(b.ActionClock / 0.45f, 0, 1));
                if (b.ActionClock >= 0.45f) EndAction(b);
                break;
            case 2:   // hop
                {
                    float u = Math.Clamp(b.ActionClock / Preset.HopSeconds, 0, 1);
                    b.Spot = Vector3.Lerp(b.HopFrom, b.HopTo, u);
                    bump = Preset.HopHeight * MathF.Sin(MathF.PI * u);
                    if (u >= 1) EndAction(b);
                    break;
                }
            case 3:   // turn
                b.Yaw = Lerp(b.TurnFrom, b.TurnTo, Smooth(b.ActionClock / 0.35f));
                if (b.ActionClock >= 0.35f) EndAction(b);
                break;
        }

        b.Position = new Vector3(b.Spot.X, Ground(b.Spot.X, b.Spot.Z) + Lift(b) + bump, b.Spot.Z);
        float cp = MathF.Cos(b.Pitch);
        b.Forward = new Vector3(MathF.Sin(b.Yaw) * cp, MathF.Sin(b.Pitch), MathF.Cos(b.Yaw) * cp);
    }

    void PickAction(WildBird b)
    {
        b.ActionClock = 0;
        double roll = _random.NextDouble();
        if (roll < 0.55)
        {
            b.Action = 1;
            Count("peck.dip");
        }
        else if (roll < 0.8)
        {
            b.Action = 3;
            b.TurnFrom = b.Yaw;
            b.TurnTo = b.Yaw + (_random.Next(2) == 0 ? 1 : -1) * Range(Preset.TurnMin, Preset.TurnMax);
            Count("peck.turn");
        }
        else
        {
            b.Action = 2;
            b.HopFrom = b.Spot;
            float yaw = b.Yaw + Range(-0.8f, 0.8f);
            var dir = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
            var to = b.Spot + dir * Range(Preset.HopMin, Preset.HopMax);
            if (Flat(to - b.Centre).Length() > Math.Max(Preset.SpotMin, Preset.SpotFar * Span(b)) + 10) to = b.Spot + Unit(Flat(b.Centre - b.Spot), dir) * Preset.HopMin;
            b.HopTo = new Vector3(to.X, 0, to.Z);
            Count("peck.hop");
        }
    }

    void EndAction(WildBird b)
    {
        b.Action = 0;
        b.Pitch = 0;
        b.ActionClock = 0;
        b.NextAction = Range(0.5f, 2.2f);
    }

    /// <summary>A burst up: from a person, a steep climb straight away from them (a short startle first); a calm take-off climbs gently.</summary>
    void StartFlee(WildBird b, Vector3? threat)
    {
        bool scared = threat is not null;
        var away = scared ? Flat(b.Position - threat!.Value) : Flat(b.Forward);
        if (away.LengthSquared() < 1e-6f) away = new Vector3(MathF.Cos(Range(0, MathF.Tau)), 0, MathF.Sin(Range(0, MathF.Tau)));
        away = Unit(away, Vector3.UnitZ);
        float spread = Range(-Preset.FleeSpread, Preset.FleeSpread), c = MathF.Cos(spread), s = MathF.Sin(spread);
        away = new Vector3(away.X * c - away.Z * s, 0, away.X * s + away.Z * c);

        b.Phase = Phase.Flee;
        b.Clock = 0;
        b.Scared |= scared;
        b.Delay = scared ? Range(0, Preset.StartleMax) : Range(0.2f, 1.0f);
        b.FleeDir = Unit(away + Vector3.UnitY * (scared ? 1.4f : 0.6f), Vector3.UnitY);
        b.FlySpeed = 0;
        b.FleeTop = Range(Preset.FleeTopMin, Preset.FleeTopMax);
        Count(scared ? "flee.scared" : "flee.calm");
    }

    /// <summary>The burst itself; true once the bird is high enough (or after <see cref="Preset.FleeLongest"/>): the game's flight takes over.</summary>
    bool Flee(WildBird b, float dt)
    {
        if (b.Clock < b.Delay) return false;
        float top = b.Speed * 1.3f;
        b.FlySpeed = Math.Min(top, b.FlySpeed + top / 0.8f * dt);
        b.Forward = Turn(b.Forward, b.FleeDir, 6 * dt);
        b.Position += b.Forward * (b.FlySpeed * dt);
        float height = b.Position.Y - Ground(b.Position.X, b.Position.Z);
        return height >= b.FleeTop || b.Clock - b.Delay > Preset.FleeLongest;
    }

    #endregion

    #region Maths

    static float Lift(WildBird b) => Preset.LiftPerScale * b.Scale;

    /// <summary>Turns a unit heading towards <paramref name="want"/> by at most <paramref name="maxAngle"/> radians.</summary>
    public static Vector3 Turn(Vector3 from, Vector3 want, float maxAngle)
    {
        from = Unit(from, Vector3.UnitZ);
        want = Unit(want, from);
        float cos = Math.Clamp(Vector3.Dot(from, want), -1, 1), angle = MathF.Acos(cos);
        if (angle <= maxAngle || angle < 1e-4f) return want;
        var axis = Vector3.Cross(from, want);
        if (axis.LengthSquared() < 1e-10f) axis = Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        return Unit(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), maxAngle)), want);
    }

    public static Vector3 Unit(Vector3 v, Vector3 fallback) =>
        Finite(v) && v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : (Finite(fallback) && fallback.LengthSquared() > 1e-10f ? Vector3.Normalize(fallback) : Vector3.UnitZ);

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static float Smooth(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
    static float Clean(float dt) => float.IsFinite(dt) && dt > 0 ? dt : 0;
    float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    #endregion
}
