// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Swamp frogs: the rules (pure System.Numerics, render-side only, never saved). A few frogs sit at the water's edge near the camera in the Swamp,
// more at night; anyone coming near makes each one leap in an arc into the water, splash and vanish; it comes back later somewhere else on the
// shore, away from people. 1 world unit = 10 cm.
using System.Numerics;

namespace PZA.Wildlife.Meitou;

public static class FrogPreset
{
    /// <summary>How many sit out by day and by night (a smooth blend at dusk and dawn, FireflyCore.NightFactor).</summary>
    public const int DayCount = 4, NightCount = 10;
    /// <summary>Shore spots are looked for within SearchRadius of the camera focus; a frog farther than KeepRadius is moved (units).</summary>
    public const float SearchRadius = 300, KeepRadius = 450;
    /// <summary>A shore spot: ground between WaterBelow under and WaterAbove over the water line, with open water (WaterDepth under the line)
    /// within WaterReach in one of 8 directions (units).</summary>
    public const float WaterBelow = 3, WaterAbove = 8, WaterDepth = 4, WaterReach = 30;
    /// <summary>Anyone within FleeRadius (7 m) makes a frog leap; a new frog sits at least SpawnClear (10 m) from everyone.</summary>
    public const float FleeRadius = 70, SpawnClear = 100;
    /// <summary>The leap: its length (s), the apex over the start (units), the startle before it (s); the dive under the water line (s, units).</summary>
    public const float LeapSeconds = 0.5f, LeapApex = 6, StartleMax = 0.15f, DiveSeconds = 0.25f, DiveDepth = 4;
    /// <summary>Seconds before a frog that dived sits out again somewhere else.</summary>
    public const float ReturnMin = 15, ReturnMax = 35;
    /// <summary>The model's scale (the model is 2.2 units long at scale 1: a big swamp frog of about 25 cm at 1.1).</summary>
    public const float ScaleMin = 0.9f, ScaleMax = 1.3f;
    /// <summary>Idle: a small turn or hop every few seconds (s, rad, units).</summary>
    public const float IdleMin = 2, IdleMax = 7, TurnMax = 1.2f, HopLength = 3, HopSeconds = 0.3f, HopApex = 1.5f;
    /// <summary>The most splashes waiting for the host thread.</summary>
    public const int SplashQueue = 16;
    /// <summary>Body poses (0.7.0): Poses steps from tucked (0) to stretched out (Poses - 1); an idle turn eases over TurnSeconds.</summary>
    public const int Poses = 6;
    public const float TurnSeconds = 0.35f;

    /// <summary>The pose nearest a stretch of 0..1 (NaN = tucked).</summary>
    public static int Pose(float stretch) =>
        float.IsFinite(stretch) ? Math.Clamp((int)MathF.Round(Math.Clamp(stretch, 0, 1) * (Poses - 1)), 0, Poses - 1) : 0;

    /// <summary>The Gorillo's idle cycle (s) the sitting frog breathes to.</summary>
    public const float IdleCycle = 1.33f;

    /// <summary>The Gorillo animation and how far through one cycle: sitting (and the startle) idles on its own clock; a hop is one walk
    /// cycle and a leap one run cycle over their length; a dive holds the run's last frame. U is always in 0..1.</summary>
    public static (int Clip, float U) Animation(FrogState state, float clock, float length, float anim)
    {
        float u = length > 0 && float.IsFinite(clock) ? Math.Clamp(clock / length, 0, 1) : 0;
        float idle = float.IsFinite(anim) ? anim / IdleCycle : 0;
        idle -= MathF.Floor(idle);
        return state switch
        {
            FrogState.Hop => (1, Math.Min(u, 0.999f)),
            FrogState.Leap => clock < 0 ? (0, Math.Clamp(idle, 0, 0.999f)) : (2, Math.Min(u, 0.999f)),
            FrogState.Dive => (2, 0.999f),
            _ => (0, Math.Clamp(idle, 0, 0.999f)),
        };
    }

    /// <summary>How stretched a frog is: a hop half way at its top; a leap pushes off (tucked to stretched in the first 0.15 s), flies
    /// stretched, and draws in a little before the water; a dive stays stretched.</summary>
    public static float Stretch(FrogState state, float clock, float length)
    {
        float u = length > 0 && float.IsFinite(clock) ? Math.Clamp(clock / length, 0, 1) : 0;
        return Math.Clamp(state switch
        {
            FrogState.Hop => 0.6f * MathF.Sin(MathF.PI * u),
            FrogState.Leap => clock < 0 ? 0 : u < 0.3f ? WingPreset.Smooth(u / 0.3f) : u < 0.75f ? 1 : 1 - 0.4f * WingPreset.Smooth((u - 0.75f) / 0.25f),
            FrogState.Dive => 1,
            _ => 0,
        }, 0, 1);
    }
}

public enum FrogState { Sit = 1, Hop = 2, Leap = 3, Dive = 4, Away = 5 }

/// <summary>One frog for the renderer: where it is, which way it faces, its pitch (a leap tips it), its scale.</summary>
/// <summary>Pose: the code-made frog's stretch step; Clip / U: the Gorillo's animation (0 idle, 1 walk, 2 run) and how far through one cycle (0..1).</summary>
public readonly record struct FrogDraw(long Id, Vector3 Position, float Yaw, float Pitch, float Scale, int Pose = 0, int Clip = 0, float U = 0);

public sealed class FrogCore
{
    public sealed class Frog
    {
        public long Id;
        public FrogState State;
        public Vector3 Pos, From, To;
        public float Yaw, Pitch, Scale, Clock, Length, Timer, TurnFrom, TurnTo, TurnClock = FrogPreset.TurnSeconds, Anim;
    }

    readonly object _lock = new();
    readonly List<Frog> _frogs = [];
    readonly List<Vector3> _splashes = [];
    FrogDraw[] _snapshot = [];
    readonly Random _random;
    long _nextId = 800_000_000L;

    public FrogCore(int seed = 20261127) => _random = new Random(seed);

    public bool Enabled { get; set; } = true;
    /// <summary>The water surface height (units; WorldWater.Height in Meitou, 100).</summary>
    public float WaterLevel { get; set; } = 100;
    public Action<string>? Count;
    public int ActiveCount { get { lock (_lock) return _frogs.Count(f => f.State != FrogState.Away); } }
    public IReadOnlyList<Frog> Frogs { get { lock (_lock) return [.. _frogs]; } }

    public FrogDraw[] Snapshot() { lock (_lock) return _snapshot; }

    public List<Vector3> TakeSplashes()
    {
        lock (_lock)
        {
            var taken = new List<Vector3>(_splashes);
            _splashes.Clear();
            return taken;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _frogs.Clear();
            _splashes.Clear();
            _snapshot = [];
        }
    }

    /// <summary>One step: wanted count by the hour, people scare sitting frogs into the water, leaps and dives play out, the dived come back.</summary>
    public void Step(float dt, Vector3 focus, double gameHours, bool inSwamp, Func<float, float, float>? ground, IReadOnlyList<Vector3> people)
    {
        dt = float.IsFinite(dt) && dt > 0 ? Math.Min(dt, 0.25f) : 0;
        lock (_lock)
        {
            if (!Enabled || !inSwamp || !WildCore.Finite(focus) || ground is null)
            {
                if (_frogs.Count > 0) Count?.Invoke("frogs.cleared");
                _frogs.Clear();
                _snapshot = [];
                return;
            }
            float night = FireflyCore.NightFactor(gameHours);
            int want = (int)MathF.Round(FrogPreset.DayCount + (FrogPreset.NightCount - FrogPreset.DayCount) * night);
            while (_frogs.Count > want) _frogs.RemoveAt(_frogs.Count - 1);
            while (_frogs.Count < want) _frogs.Add(new Frog { Id = _nextId++, State = FrogState.Away, Timer = Range(0, 3) });

            foreach (var f in _frogs) StepFrog(f, dt, focus, ground, people);
            _snapshot = [.. _frogs.Where(f => f.State != FrogState.Away).Select(f => { var (clip, u) = FrogPreset.Animation(f.State, f.Clock, f.Length, f.Anim); return new FrogDraw(f.Id, f.Pos, f.Yaw, f.Pitch, f.Scale, FrogPreset.Pose(FrogPreset.Stretch(f.State, f.Clock, f.Length)), clip, u); })];
        }
    }

    void StepFrog(Frog f, float dt, Vector3 focus, Func<float, float, float> ground, IReadOnlyList<Vector3> people)
    {
        f.Clock += dt;
        f.Anim += dt;
        if (f.Anim > 3600) f.Anim -= 3600;
        switch (f.State)
        {
            case FrogState.Away:
                f.Timer -= dt;
                if (f.Timer <= 0) TrySit(f, focus, ground, people);
                return;
            case FrogState.Sit or FrogState.Hop:
                if (Flat(f.Pos - focus).LengthSquared() > FrogPreset.KeepRadius * FrogPreset.KeepRadius)
                {
                    GoAway(f, 0.5f);
                    return;
                }
                if (Near(f.Pos, people, FrogPreset.FleeRadius))
                {
                    StartLeap(f, ground);
                    return;
                }
                if (f.State == FrogState.Hop) HopStep(f, ground);
                else Idle(f, dt, ground);
                return;
            case FrogState.Leap:
                LeapStep(f, ground);
                return;
            case FrogState.Dive:
                DiveStep(f);
                return;
        }
    }

    #region Sitting

    /// <summary>A shore spot near the focus, away from everyone; the frog faces the water. Stays away (tries again soon) when none is found.</summary>
    void TrySit(Frog f, Vector3 focus, Func<float, float, float> ground, IReadOnlyList<Vector3> people)
    {
        for (int tries = 0; tries < 24; tries++)
        {
            float a = Range(0, MathF.Tau), d = MathF.Sqrt(Range(0, 1)) * FrogPreset.SearchRadius;
            var p = new Vector3(focus.X + MathF.Cos(a) * d, 0, focus.Z + MathF.Sin(a) * d);
            float g = Safe(ground, p.X, p.Z);
            if (!float.IsFinite(g) || g < WaterLevel - FrogPreset.WaterBelow || g > WaterLevel + FrogPreset.WaterAbove) continue;
            if (WaterNear(p, ground) is not { } water) continue;
            p.Y = Math.Max(g, WaterLevel);
            if (Near(p, people, FrogPreset.SpawnClear)) continue;
            f.Pos = p;
            f.Yaw = MathF.Atan2(water.X - p.X, water.Z - p.Z);
            f.Pitch = 0;
            f.TurnClock = FrogPreset.TurnSeconds;
            f.Scale = Range(FrogPreset.ScaleMin, FrogPreset.ScaleMax);
            f.State = FrogState.Sit;
            f.Clock = 0;
            f.Timer = Range(FrogPreset.IdleMin, FrogPreset.IdleMax);
            Count?.Invoke("frogs.sit");
            return;
        }
        f.Timer = Range(1, 3);
        Count?.Invoke("frogs.no_shore");
    }

    /// <summary>The nearest open water within reach (8 directions, 3 distances); null when there is none.</summary>
    Vector3? WaterNear(Vector3 p, Func<float, float, float> ground)
    {
        for (int step = 1; step <= 3; step++)
        {
            float r = FrogPreset.WaterReach * step / 3;
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.Tau / 8;
                var q = new Vector3(p.X + MathF.Cos(a) * r, 0, p.Z + MathF.Sin(a) * r);
                float g = Safe(ground, q.X, q.Z);
                if (float.IsFinite(g) && g < WaterLevel - FrogPreset.WaterDepth) return new Vector3(q.X, WaterLevel, q.Z);
            }
        }
        return null;
    }

    /// <summary>Now and then a small turn or a short hop along the ground.</summary>
    void Idle(Frog f, float dt, Func<float, float, float> ground)
    {
        f.Timer -= dt;
        f.Pos.Y = Surface(ground, f.Pos);
        if (f.TurnClock < FrogPreset.TurnSeconds)
        {
            f.TurnClock = Math.Min(FrogPreset.TurnSeconds, f.TurnClock + dt);
            f.Yaw = Lerp(f.TurnFrom, f.TurnTo, WingPreset.Smooth(f.TurnClock / FrogPreset.TurnSeconds));
        }
        if (f.Timer > 0) return;
        f.Timer = Range(FrogPreset.IdleMin, FrogPreset.IdleMax);
        if (_random.NextDouble() < 0.6)
        {
            (f.TurnFrom, f.TurnTo, f.TurnClock) = (f.Yaw, f.Yaw + Range(-FrogPreset.TurnMax, FrogPreset.TurnMax), 0);
            Count?.Invoke("frogs.turn");
            return;
        }
        f.TurnClock = FrogPreset.TurnSeconds;
        var dir = new Vector3(MathF.Sin(f.Yaw), 0, MathF.Cos(f.Yaw));
        var to = f.Pos + dir * FrogPreset.HopLength;
        float g = Safe(ground, to.X, to.Z);
        if (!float.IsFinite(g) || g < WaterLevel - FrogPreset.WaterBelow) return;   // a hop never ends in the water: that is the leap's
        (f.From, f.To, f.State, f.Clock, f.Length) = (f.Pos, new Vector3(to.X, Math.Max(g, WaterLevel), to.Z), FrogState.Hop, 0, FrogPreset.HopSeconds);
        Count?.Invoke("frogs.hop");
    }

    void HopStep(Frog f, Func<float, float, float> ground)
    {
        float u = Math.Clamp(f.Clock / f.Length, 0, 1);
        f.Pos = Arc(f.From, f.To, u, FrogPreset.HopApex);
        f.Pos.Y = Math.Max(f.Pos.Y, Surface(ground, f.Pos));
        f.Pitch = -0.25f * MathF.Sin(MathF.Tau * u);   // nose up on the way up, down on the way down (negative = up, as the leap)
        if (u < 1) return;
        f.Pitch = 0;
        f.State = FrogState.Sit;
        f.Timer = Range(FrogPreset.IdleMin, FrogPreset.IdleMax);
    }

    #endregion

    #region Into the water

    void StartLeap(Frog f, Func<float, float, float> ground)
    {
        var water = WaterNear(f.Pos, ground) ?? f.Pos + new Vector3(MathF.Sin(f.Yaw), 0, MathF.Cos(f.Yaw)) * FrogPreset.WaterReach;
        water.Y = WaterLevel;
        f.From = f.Pos;
        f.To = water;
        f.Yaw = MathF.Atan2(water.X - f.Pos.X, water.Z - f.Pos.Z);
        f.State = FrogState.Leap;
        f.TurnClock = FrogPreset.TurnSeconds;
        f.Clock = -Range(0, FrogPreset.StartleMax);   // a startle: it sits for a split second first
        f.Length = FrogPreset.LeapSeconds;
        Count?.Invoke("frogs.leap");
    }

    void LeapStep(Frog f, Func<float, float, float> ground)
    {
        if (f.Clock < 0) return;
        float u = Math.Clamp(f.Clock / f.Length, 0, 1);
        f.Pos = Arc(f.From, f.To, u, FrogPreset.LeapApex);
        f.Pos.Y = Math.Max(f.Pos.Y, Safe(ground, f.Pos.X, f.Pos.Z) is var g && float.IsFinite(g) ? g : f.Pos.Y);
        f.Pitch = Lerp(-0.6f, 0.6f, u);   // nose up off the bank, nose down into the water
        if (u < 1) return;
        lock (_splashes)
        {
            if (_splashes.Count < FrogPreset.SplashQueue) _splashes.Add(f.To);
            else Count?.Invoke("frogs.splash_dropped");
        }
        f.State = FrogState.Dive;
        f.Clock = 0;
        f.Length = FrogPreset.DiveSeconds;
        Count?.Invoke("frogs.splash");
    }

    void DiveStep(Frog f)
    {
        float u = Math.Clamp(f.Clock / f.Length, 0, 1);
        f.Pos = new Vector3(f.To.X, WaterLevel - FrogPreset.DiveDepth * u, f.To.Z);
        if (u >= 1) GoAway(f, Range(FrogPreset.ReturnMin, FrogPreset.ReturnMax));
    }

    void GoAway(Frog f, float seconds)
    {
        f.State = FrogState.Away;
        f.Timer = seconds;
        f.Clock = 0;
        Count?.Invoke("frogs.away");
    }

    #endregion

    #region Maths

    static Vector3 Arc(Vector3 a, Vector3 b, float u, float apex) =>
        Vector3.Lerp(a, b, u) + Vector3.UnitY * (apex * 4 * u * (1 - u));

    static bool Near(Vector3 p, IReadOnlyList<Vector3> people, float radius)
    {
        float r2 = radius * radius;
        foreach (var q in people)
            if (WildCore.Finite(q) && Flat(q - p).LengthSquared() < r2) return true;
        return false;
    }

    float Surface(Func<float, float, float> ground, Vector3 p)
    {
        float g = Safe(ground, p.X, p.Z);
        return float.IsFinite(g) ? Math.Max(g, WaterLevel) : p.Y;
    }

    static float Safe(Func<float, float, float> ground, float x, float z)
    {
        try { return ground(x, z); }
        catch { return float.NaN; }
    }

    static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);

    #endregion
}
