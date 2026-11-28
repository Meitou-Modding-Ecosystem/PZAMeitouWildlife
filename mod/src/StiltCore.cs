// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The Mire Stilt (the user's own Meshy models; Mesh2Motion's CC0 bird rig and clips plus PZA's wade and hunt): the rules, pure System.Numerics,
// render-side only, never saved. One to three come to the Swamp near the camera (three at dusk and dawn): each flies in from far off, glides
// down and lands in shallow water, then wades, stands and hunts. A strike splashes; now and then it takes a frog or a froxlotl within beak
// reach (that one is gone, as if eaten). Anyone within FleeRadius makes it take off and fly away; after a few hunts it leaves anyway, and a
// new one comes later somewhere else. Two meshes: the flight one in the air (wings spread), the standing one (wings folded) on the ground;
// they swap inside the take-off and the landing, where both clips meet. 1 unit = 10 cm.
using System.Numerics;

namespace PZA.Wildlife.Meitou;

public static class StiltPreset
{
    /// <summary>How many at once: DayCount, DayCount + Twilight at the height of dusk and dawn (FireflyCore.NightFactor's middle).</summary>
    public const int DayCount = 1, Twilight = 2;
    /// <summary>A landing spot: ground StandMin..StandMax under the water line (10-60 cm of water), within SearchRadius of the camera focus,
    /// at least SpawnClear from everyone; one farther than KeepRadius from the focus leaves (units).</summary>
    public const float StandMin = 1, StandMax = 6, SearchRadius = 350, SpawnClear = 250, KeepRadius = 700;
    /// <summary>Anyone within FleeRadius (15 m) makes it take off.</summary>
    public const float FleeRadius = 150;
    /// <summary>Flight: speed (units/s), where it comes from (units away), how high it cruises over the water line, how fast it climbs away
    /// (units/s) and for how long (s), how fast it turns in the air (rad/s).</summary>
    public const float FlySpeed = 90, ArriveFrom = 900, Cruise = 120, Climb = 25, LeaveSeconds = 8, AirTurn = 1.2f;
    /// <summary>In the air its body keeps at least AirClear over the ground under it (units; a hill on the way in or out).</summary>
    public const float AirClear = 5;
    /// <summary>Wing beats on the way in: flapping for the first FlapFirst s, then a burst of FlapBurst s every FlapEvery s (else gliding).</summary>
    public const float FlapFirst = 1.6f, FlapEvery = 5, FlapBurst = 1.1f;
    /// <summary>On the ground: idle IdleMin..IdleMax s, wade WadeMin..WadeMax s, turning at most GroundTurn rad/s.</summary>
    public const float IdleMin = 2, IdleMax = 5, WadeMin = 3, WadeMax = 8, GroundTurn = 1.0f;
    /// <summary>The strike lands StrikeAt s into the hunt clip; its beak reaches BeakAhead units ahead of the feet, BeakReach around it
    /// (units, at scale 1); CatchChance of a strike takes the prey in reach.</summary>
    public const float StrikeAt = 2.9f, BeakAhead = 13, BeakReach = 13, CatchChance = 0.3f;
    /// <summary>Hunts before it leaves on its own; seconds before a new one comes after one left.</summary>
    public const int HuntsMin = 2, HuntsMax = 5;
    public const float ReturnMin = 25, ReturnMax = 60;
    /// <summary>Its scale (2.1 m standing at 1).</summary>
    public const float ScaleMin = 0.92f, ScaleMax = 1.08f;
    public const int SplashQueue = 16;
}

/// <summary>The baked clips' timing (StiltAsset), with defaults that match the shipped bake for the check. Ground (standing mesh): idle,
/// wade, hunt, the take-off's head, the landing's tail. Air (flight mesh): the take-off's tail, flap, glide, the landing's head. The lows
/// are the lowest point of a clip (units over the origin), which make the air meet the ground at the swaps.</summary>
public sealed record StiltClips(
    float IdleSeconds = 1.17f, int IdleFrames = 17, float WadeSeconds = 0.69f, int WadeFrames = 13, float WadeSpeed = 1.43f,
    float HuntSeconds = 5.4f, int HuntFrames = 81, float TakeoffHeadSeconds = 0.16f, int TakeoffHeadFrames = 3,
    float LandTailSeconds = 0.17f, int LandTailFrames = 3,
    float TakeoffTailSeconds = 1.17f, int TakeoffTailFrames = 35, float FlapSeconds = 0.54f, int FlapFrames = 10,
    float GlideSeconds = 0.54f, int GlideFrames = 10, float LandHeadSeconds = 1.5f, int LandHeadFrames = 44,
    float LandLow = 43.5f, float GlideLow = 43.28f, float FlapLow = 43.24f, float TakeoffEndLow = 43.75f)
{
    public float LandSeconds => LandHeadSeconds + LandTailSeconds;
    public float TakeoffSeconds => TakeoffHeadSeconds + TakeoffTailSeconds;
}

public enum StiltState { Away = 1, Arrive = 2, Land = 3, Idle = 4, Wade = 5, Hunt = 6, TakeOff = 7, Leave = 8 }

/// <summary>Mesh 0 = standing (clips 0 idle, 1 wade, 2 hunt, 3 take-off head, 4 landing tail), 1 = flight (0 take-off tail, 1 flap,
/// 2 glide, 3 landing head).</summary>
public readonly record struct StiltDraw(long Id, Vector3 Position, float Yaw, float Scale, int Mesh, int Clip, int Frame);

public sealed class StiltCore
{
    public sealed class Stilt
    {
        public long Id;
        public StiltState State;
        public Vector3 Pos, Spot, From;
        public float Yaw, WantYaw, Scale, Clock, Timer, Anim, Ground;
        public int HuntsLeft;
        public bool Struck;
    }

    readonly object _lock = new();
    readonly List<Stilt> _all = [];
    readonly List<Vector3> _splashes = [];
    StiltDraw[] _snapshot = [];
    readonly Random _random;
    long _nextId = 820_000_000L;

    public StiltCore(int seed = 20261127) => _random = new Random(seed);

    public bool Enabled { get; set; } = true;
    public float WaterLevel { get; set; } = 100;
    public StiltClips Clips { get; set; } = new();
    public Action<string>? Count;
    /// <summary>A strike that catches: takes one frog or froxlotl within the radius of the point (true = one was taken).</summary>
    public Func<Vector3, float, bool>? Prey;
    public IReadOnlyList<Stilt> All { get { lock (_lock) return [.. _all]; } }
    public int ActiveCount { get { lock (_lock) return _all.Count(s => s.State != StiltState.Away); } }
    public StiltDraw[] Snapshot() { lock (_lock) return _snapshot; }

    public List<Vector3> TakeSplashes()
    {
        lock (_lock)
        {
            var taken = new List<Vector3>(_splashes);
            _splashes.Clear();
            return taken;
        }
    }

    /// <summary>How many at this hour: DayCount, up to DayCount + Twilight at dusk and dawn.</summary>
    public static int Wanted(double gameHours)
    {
        float n = FireflyCore.NightFactor(gameHours);
        float twilight = 4 * n * (1 - n);
        return (int)MathF.Round(StiltPreset.DayCount + StiltPreset.Twilight * twilight);
    }

    public void Step(float dt, Vector3 focus, double gameHours, bool inSwamp, Func<float, float, float>? ground, IReadOnlyList<Vector3> people)
    {
        dt = float.IsFinite(dt) && dt > 0 ? Math.Min(dt, 0.25f) : 0;
        lock (_lock)
        {
            if (!Enabled || !inSwamp || !WildCore.Finite(focus) || ground is null)
            {
                if (_all.Count > 0) Count?.Invoke("stilt.cleared");
                _all.Clear();
                _snapshot = [];
                return;
            }
            int want = Wanted(gameHours);
            // only birds that are away leave the list: one in sight finishes its visit
            for (int i = _all.Count - 1; i >= 0 && _all.Count > want; i--)
                if (_all[i].State == StiltState.Away) _all.RemoveAt(i);
            while (_all.Count < want) _all.Add(new Stilt { Id = _nextId++, State = StiltState.Away, Timer = Range(0, 6) });
            foreach (var s in _all) StepOne(s, dt, focus, ground, people);
            _snapshot = [.. _all.Where(s => s.State != StiltState.Away).Select(Draw)];
        }
    }

    #region Drawing

    /// <summary>The frame at <paramref name="time"/> into a clip: looping, or held on its last frame (0 for bad input).</summary>
    public static int Frame(float time, float seconds, int frames, bool loop)
    {
        if (frames <= 0 || !float.IsFinite(time) || !float.IsFinite(seconds) || seconds <= 0) return 0;
        double u = time / seconds;
        if (loop) u -= Math.Floor(u);
        return Math.Clamp((int)(u * frames), 0, frames - 1);
    }

    /// <summary>Flapping or gliding on the way in (by the clock since it set off).</summary>
    public static bool Flapping(float clock) =>
        clock < StiltPreset.FlapFirst || (clock - StiltPreset.FlapFirst) % StiltPreset.FlapEvery < StiltPreset.FlapBurst;

    StiltDraw Draw(Stilt s)
    {
        var c = Clips;
        (int mesh, int clip, int frame) = s.State switch
        {
            StiltState.Arrive => Flapping(s.Clock) ? (1, 1, Frame(s.Anim, c.FlapSeconds, c.FlapFrames, true)) : (1, 2, Frame(s.Anim, c.GlideSeconds, c.GlideFrames, true)),
            StiltState.Leave => (1, 1, Frame(s.Anim, c.FlapSeconds, c.FlapFrames, true)),
            StiltState.Land => s.Clock < c.LandHeadSeconds
                ? (1, 3, Frame(s.Clock, c.LandHeadSeconds, c.LandHeadFrames, false))
                : (0, 4, Frame(s.Clock - c.LandHeadSeconds, c.LandTailSeconds, c.LandTailFrames, false)),
            StiltState.TakeOff => s.Clock < c.TakeoffHeadSeconds
                ? (0, 3, Frame(s.Clock, c.TakeoffHeadSeconds, c.TakeoffHeadFrames, false))
                : (1, 0, Frame(s.Clock - c.TakeoffHeadSeconds, c.TakeoffTailSeconds, c.TakeoffTailFrames, false)),
            StiltState.Wade => (0, 1, Frame(s.Anim, c.WadeSeconds, c.WadeFrames, true)),
            StiltState.Hunt => (0, 2, Frame(s.Clock, c.HuntSeconds, c.HuntFrames, false)),
            _ => (0, 0, Frame(s.Anim, c.IdleSeconds, c.IdleFrames, true)),
        };
        return new StiltDraw(s.Id, s.Pos, s.Yaw, s.Scale, mesh, clip, frame);
    }

    #endregion

    #region The visit

    void StepOne(Stilt s, float dt, Vector3 focus, Func<float, float, float> ground, IReadOnlyList<Vector3> people)
    {
        s.Clock += dt;
        s.Anim += dt;
        if (s.Anim > 3600) s.Anim -= 3600;
        bool onGround = s.State is StiltState.Idle or StiltState.Wade or StiltState.Hunt or StiltState.Land;
        if (onGround && (Near(s.Pos, people, StiltPreset.FleeRadius) || Flat(s.Pos - focus).LengthSquared() > StiltPreset.KeepRadius * StiltPreset.KeepRadius))
        {
            StartTakeOff(s, people);
            return;
        }
        switch (s.State)
        {
            case StiltState.Away:
                s.Timer -= dt;
                if (s.Timer <= 0) TryArrive(s, focus, ground, people);
                return;
            case StiltState.Arrive: ArriveStep(s, dt, ground); return;
            case StiltState.Land: LandStep(s); return;
            case StiltState.Idle: IdleStep(s, dt); return;
            case StiltState.Wade: WadeStep(s, dt, ground); return;
            case StiltState.Hunt: HuntStep(s); return;
            case StiltState.TakeOff: TakeOffStep(s); return;
            case StiltState.Leave: LeaveStep(s, dt, ground); return;
        }
    }

    /// <summary>A shallow spot near the camera, clear of people; it sets off from ArriveFrom away, high over the water.</summary>
    void TryArrive(Stilt s, Vector3 focus, Func<float, float, float> ground, IReadOnlyList<Vector3> people)
    {
        for (int tries = 0; tries < 24; tries++)
        {
            float a = Range(0, MathF.Tau), d = MathF.Sqrt(Range(0, 1)) * StiltPreset.SearchRadius;
            var p = new Vector3(focus.X + MathF.Cos(a) * d, 0, focus.Z + MathF.Sin(a) * d);
            float g = Safe(ground, p.X, p.Z);
            if (!Shallow(g)) continue;
            p.Y = g;
            if (Near(p, people, StiltPreset.SpawnClear)) continue;
            float from = Range(0, MathF.Tau);
            s.Spot = p;
            s.Ground = g;
            s.Scale = Range(StiltPreset.ScaleMin, StiltPreset.ScaleMax);
            s.From = new Vector3(p.X + MathF.Sin(from) * StiltPreset.ArriveFrom, WaterLevel + StiltPreset.Cruise, p.Z + MathF.Cos(from) * StiltPreset.ArriveFrom);
            s.Pos = s.From;
            s.Yaw = MathF.Atan2(p.X - s.From.X, p.Z - s.From.Z);
            s.WantYaw = s.Yaw;
            s.State = StiltState.Arrive;
            s.Clock = 0;
            s.Anim = Range(0, 5);
            s.HuntsLeft = _random.Next(StiltPreset.HuntsMin, StiltPreset.HuntsMax + 1);
            ClearGround(s, ground);     // it sets off over whatever is there (a hill higher than the cruise)
            Count?.Invoke("stilt.arrive");
            return;
        }
        s.Timer = Range(2, 5);
        Count?.Invoke("stilt.no_shallows");
    }

    /// <summary>Where the landing starts: LandReach short of the spot along the way in (it covers that in the landing, slowing to a stop).</summary>
    float LandReach(Stilt s) => StiltPreset.FlySpeed * 0.5f * Clips.LandSeconds * s.Scale;

    /// <summary>Straight in at FlySpeed, coming down from the cruise to the landing's own start height (the clips meet there).</summary>
    void ArriveStep(Stilt s, float dt, Func<float, float, float> ground)
    {
        var flat = Flat(s.Spot - s.Pos);
        float left = flat.Length() - LandReach(s);
        float total = MathF.Max(1, Flat(s.Spot - s.From).Length() - LandReach(s));
        if (left <= 0)
        {
            s.From = s.Pos;
            s.State = StiltState.Land;
            s.Clock = 0;
            Count?.Invoke("stilt.land");
            return;
        }
        var dir = flat / MathF.Max(flat.Length(), 1e-3f);
        s.Yaw = MathF.Atan2(dir.X, dir.Z);
        float step = MathF.Min(StiltPreset.FlySpeed * s.Scale * dt, left);
        s.Pos += dir * step;
        float u = Math.Clamp(1 - (left - step) / total, 0, 1);
        float high = WaterLevel + StiltPreset.Cruise;
        float meet = s.Ground + (Clips.LandLow - Clips.GlideLow) * s.Scale;     // the landing's first frame sits where the glide's would
        s.Pos.Y = high + (meet - high) * WingPreset.Smooth(u);
        ClearGround(s, ground);
    }

    /// <summary>The body's lowest point (the origin plus the flight clips' lowest body point) at least AirClear over the ground under it.</summary>
    void ClearGround(Stilt s, Func<float, float, float> ground)
    {
        float g = Safe(ground, s.Pos.X, s.Pos.Z);
        if (!float.IsFinite(g)) return;
        float floor = g + StiltPreset.AirClear - AirLow * s.Scale;
        if (s.Pos.Y < floor)
        {
            s.Pos.Y = floor;
            Count?.Invoke("stilt.air_clear");
        }
    }

    /// <summary>The flight clips' lowest body point over the origin (the lower of the flap's and the glide's).</summary>
    public float AirLow => MathF.Min(Clips.GlideLow, Clips.FlapLow);

    /// <summary>The landing clip plays (its own fall from the air); the bird slows to a stop on the spot; the standing mesh takes over for
    /// the clip's last part (Draw).</summary>
    void LandStep(Stilt s)
    {
        float u = Math.Clamp(s.Clock / Clips.LandSeconds, 0, 1);
        float ease = 1 - (1 - u) * (1 - u);
        s.Pos = new Vector3(s.From.X + (s.Spot.X - s.From.X) * ease, s.Ground, s.From.Z + (s.Spot.Z - s.From.Z) * ease);
        if (u < 1) return;
        ToIdle(s);
    }

    void ToIdle(Stilt s)
    {
        s.State = StiltState.Idle;
        s.Clock = 0;
        s.Timer = Range(StiltPreset.IdleMin, StiltPreset.IdleMax);
    }

    /// <summary>Stands; then wades, or hunts (half and half), or leaves once its hunts are done.</summary>
    void IdleStep(Stilt s, float dt)
    {
        s.Timer -= dt;
        if (s.Timer > 0) return;
        if (s.HuntsLeft <= 0)
        {
            StartTakeOff(s, []);
            Count?.Invoke("stilt.done");
            return;
        }
        s.Clock = 0;
        s.Struck = false;
        if (_random.NextDouble() < 0.5)
        {
            s.State = StiltState.Wade;
            s.Timer = Range(StiltPreset.WadeMin, StiltPreset.WadeMax);
            s.WantYaw = s.Yaw + Range(-1.2f, 1.2f);
            Count?.Invoke("stilt.wade");
        }
        else
        {
            s.State = StiltState.Hunt;
            s.HuntsLeft--;
            Count?.Invoke("stilt.hunt");
        }
    }

    /// <summary>Wades at the wade's own ground speed (its feet don't slide), easing round; it never steps out of the shallows: at the edge
    /// it turns instead.</summary>
    void WadeStep(Stilt s, float dt, Func<float, float, float> ground)
    {
        s.Yaw += Math.Clamp(Wrap(s.WantYaw - s.Yaw), -StiltPreset.GroundTurn * dt, StiltPreset.GroundTurn * dt);
        var dir = new Vector3(MathF.Sin(s.Yaw), 0, MathF.Cos(s.Yaw));
        var next = s.Pos + dir * Clips.WadeSpeed * s.Scale * dt;
        float g = Safe(ground, next.X, next.Z);
        if (Shallow(g)) s.Pos = new Vector3(next.X, g, next.Z);
        else
        {
            s.WantYaw = s.Yaw + MathF.PI * 0.6f;
            Count?.Invoke("stilt.wade_edge");
        }
        s.Ground = s.Pos.Y;
        s.Timer -= dt;
        if (s.Timer <= 0) ToIdle(s);
    }

    /// <summary>The hunt clip once through; at StrikeAt the beak hits the water ahead (a splash) and CatchChance of the time takes the prey
    /// within reach.</summary>
    void HuntStep(Stilt s)
    {
        if (!s.Struck && s.Clock >= StiltPreset.StrikeAt)
        {
            s.Struck = true;
            var beak = Beak(s);
            if (_splashes.Count < StiltPreset.SplashQueue) _splashes.Add(new Vector3(beak.X, WaterLevel, beak.Z));
            else Count?.Invoke("stilt.splash_dropped");
            Count?.Invoke("stilt.strike");
            if (_random.NextDouble() < StiltPreset.CatchChance && Prey is { } prey && prey(beak, StiltPreset.BeakReach * s.Scale))
                Count?.Invoke("stilt.catch");
        }
        if (s.Clock >= Clips.HuntSeconds) ToIdle(s);
    }

    /// <summary>Where the beak strikes: BeakAhead in front of the feet, at the water line.</summary>
    public static Vector3 Beak(Stilt s) =>
        new(s.Pos.X + MathF.Sin(s.Yaw) * StiltPreset.BeakAhead * s.Scale, s.Pos.Y, s.Pos.Z + MathF.Cos(s.Yaw) * StiltPreset.BeakAhead * s.Scale);

    void StartTakeOff(Stilt s, IReadOnlyList<Vector3> people)
    {
        // it climbs away from the nearest person (or on along its heading)
        float best = float.PositiveInfinity;
        s.WantYaw = s.Yaw;
        foreach (var q in people)
        {
            if (!WildCore.Finite(q)) continue;
            float d = Flat(q - s.Pos).LengthSquared();
            if (d < best && d > 1e-4f) (best, s.WantYaw) = (d, MathF.Atan2(s.Pos.X - q.X, s.Pos.Z - q.Z));
        }
        s.From = s.Pos;
        s.State = StiltState.TakeOff;
        s.Clock = 0;
        Count?.Invoke("stilt.takeoff");
    }

    /// <summary>The take-off clip plays (its own rise); the bird gathers speed along its heading.</summary>
    void TakeOffStep(Stilt s)
    {
        float t = s.Clock, total = Clips.TakeoffSeconds;
        float v = StiltPreset.FlySpeed * 0.6f * s.Scale / MathF.Max(total, 1e-3f);    // accelerating to 60% of its flight speed
        float d = 0.5f * v * MathF.Min(t, total) * MathF.Min(t, total);
        s.Pos = new Vector3(s.From.X + MathF.Sin(s.Yaw) * d, s.From.Y, s.From.Z + MathF.Cos(s.Yaw) * d);
        if (t < total) return;
        s.Pos.Y = s.From.Y + (Clips.TakeoffEndLow - Clips.FlapLow) * s.Scale;     // the flap's frame sits where the take-off ended
        s.State = StiltState.Leave;
        s.Clock = 0;
    }

    /// <summary>Flapping away, climbing, turning to its heading; out of sight it is away for a while.</summary>
    void LeaveStep(Stilt s, float dt, Func<float, float, float> ground)
    {
        s.Yaw += Math.Clamp(Wrap(s.WantYaw - s.Yaw), -StiltPreset.AirTurn * dt, StiltPreset.AirTurn * dt);
        float speed = StiltPreset.FlySpeed * s.Scale * MathF.Min(1, 0.6f + s.Clock / 2);
        s.Pos += new Vector3(MathF.Sin(s.Yaw) * speed * dt, StiltPreset.Climb * dt, MathF.Cos(s.Yaw) * speed * dt);
        ClearGround(s, ground);
        if (s.Clock < StiltPreset.LeaveSeconds) return;
        s.State = StiltState.Away;
        s.Timer = Range(StiltPreset.ReturnMin, StiltPreset.ReturnMax);
        s.Clock = 0;
        Count?.Invoke("stilt.away");
    }

    #endregion

    #region Maths

    /// <summary>Ground under the water line by StandMin..StandMax (finite).</summary>
    public bool Shallow(float g) => float.IsFinite(g) && g <= WaterLevel - StiltPreset.StandMin && g >= WaterLevel - StiltPreset.StandMax;

    static bool Near(Vector3 p, IReadOnlyList<Vector3> people, float radius)
    {
        float r2 = radius * radius;
        foreach (var q in people)
            if (WildCore.Finite(q) && Flat(q - p).LengthSquared() < r2) return true;
        return false;
    }

    static float Safe(Func<float, float, float> ground, float x, float z)
    {
        try { return ground(x, z); }
        catch { return float.NaN; }
    }

    static float Wrap(float a)
    {
        if (!float.IsFinite(a)) return 0;
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
    }

    static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    #endregion
}
