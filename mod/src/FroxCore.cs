// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Froxlotls (TheIvoryDisaster's model and colours, PZA's gait): the rules, pure System.Numerics, render-side only, never saved. A few rest at
// the Swamp's water edge near the camera, more at night; now and then one crawls a little way along the bank; anyone coming near makes it
// turn to the water and run in (its run's own ground speed, so the feet don't slide), slip under with a splash and come back later somewhere
// else. Every frox wears one of a few colours picked for the visit (at most PoolSize at once, so few textures are loaded). 1 unit = 10 cm.
using System.Numerics;

namespace PZA.Wildlife.Meitou;

public static class FroxPreset
{
    /// <summary>How many rest out by day and by night (blended at dusk and dawn, FireflyCore.NightFactor).</summary>
    public const int DayCount = 2, NightCount = 4;
    /// <summary>Anyone within FleeRadius (7 m) sends it running; a new one rests at least SpawnClear (10 m) from everyone.</summary>
    public const float FleeRadius = 70, SpawnClear = 100;
    /// <summary>The turn toward the water before the run (s), the longest run (s), the slip under (s, units).</summary>
    public const float TurnSeconds = 0.15f, RunLongest = 3, DiveSeconds = 0.4f, DiveDepth = 4;
    /// <summary>The run plays RunRate times faster than baked and covers ground as fast (0.9.0, user: "they take forever to scamper into the
    /// water"): its feet still don't slide.</summary>
    public const float RunRate = 2.2f;
    /// <summary>Seconds before one that went in rests out again somewhere else.</summary>
    public const float ReturnMin = 20, ReturnMax = 45;
    /// <summary>Its scale (the baked model is 3 units long at 1: 45 to 63 cm; 0.9.0, user: "make froxlotls larger").</summary>
    public const float ScaleMin = 1.5f, ScaleMax = 2.1f;
    /// <summary>Resting: a crawl every RestMin..RestMax s, CrawlMin..CrawlMax s long, along the bank.</summary>
    public const float RestMin = 6, RestMax = 18, CrawlMin = 1.5f, CrawlMax = 4;
    /// <summary>The colours worn at once (from Colours: the wild one and PoolSize - 1 picked at random).</summary>
    public const int PoolSize = 4;
    public const int SplashQueue = 16;
    /// <summary>The wild colour and Mazal's six tropical ones (his six pastels left out, user 2026-11-27: "not a fan of the pastel colors").</summary>
    public static readonly string[] Colours = ["original", "parrot", "sunset", "lagoon", "poison-dart", "mango", "hibiscus"];
}

/// <summary>The baked clips' timing and ground speeds (FroxAsset), with defaults that match the shipped bake for the check.</summary>
public sealed record FroxClips(float RestSeconds = 2.22f, int RestFrames = 12, float WalkSeconds = 1.0f, int WalkFrames = 20, float WalkSpeed = 1.1f,
                               float RunSeconds = 0.42f, int RunFrames = 16, float RunSpeed = 2.77f);

public enum FroxState { Rest = 1, Crawl = 2, Turn = 3, Run = 4, Dive = 5, Away = 6 }

/// <summary>The clip drawn: 0 rest, 1 walk, 2 run.</summary>
public readonly record struct FroxDraw(long Id, Vector3 Position, float Yaw, float Scale, int Colour, int Clip, int Frame);

public sealed class FroxCore
{
    public sealed class Frox
    {
        public long Id;
        public FroxState State;
        public Vector3 Pos, Water;
        public float Yaw, TurnFrom, TurnTo, Scale, Clock, Timer, Anim;
        public int Colour;
    }

    readonly object _lock = new();
    readonly List<Frox> _all = [];
    readonly List<Vector3> _splashes = [];
    FroxDraw[] _snapshot = [];
    readonly Random _random;
    long _nextId = 810_000_000L;
    int[] _pool = [];

    public FroxCore(int seed = 20261127) => _random = new Random(seed);

    public bool Enabled { get; set; } = true;
    public float WaterLevel { get; set; } = 100;
    public FroxClips Clips { get; set; } = new();
    public Action<string>? Count;
    public IReadOnlyList<Frox> All { get { lock (_lock) return [.. _all]; } }
    public int ActiveCount { get { lock (_lock) return _all.Count(f => f.State != FroxState.Away); } }
    public int[] Pool { get { lock (_lock) return [.. _pool]; } }
    public FroxDraw[] Snapshot() { lock (_lock) return _snapshot; }

    public List<Vector3> TakeSplashes()
    {
        lock (_lock)
        {
            var taken = new List<Vector3>(_splashes);
            _splashes.Clear();
            return taken;
        }
    }

    public void Step(float dt, Vector3 focus, double gameHours, bool inSwamp, Func<float, float, float>? ground, IReadOnlyList<Vector3> people)
    {
        dt = float.IsFinite(dt) && dt > 0 ? Math.Min(dt, 0.25f) : 0;
        lock (_lock)
        {
            if (!Enabled || !inSwamp || !WildCore.Finite(focus) || ground is null)
            {
                if (_all.Count > 0) Count?.Invoke("frox.cleared");
                _all.Clear();
                _pool = [];
                _snapshot = [];
                return;
            }
            if (_pool.Length == 0) PickPool();
            float night = FireflyCore.NightFactor(gameHours);
            int want = (int)MathF.Round(FroxPreset.DayCount + (FroxPreset.NightCount - FroxPreset.DayCount) * night);
            while (_all.Count > want) _all.RemoveAt(_all.Count - 1);
            while (_all.Count < want) _all.Add(new Frox { Id = _nextId++, State = FroxState.Away, Timer = Range(0, 4) });
            foreach (var f in _all) StepOne(f, dt, focus, ground, people);
            _snapshot = [.. _all.Where(f => f.State != FroxState.Away).Select(Draw)];
        }
    }

    /// <summary>The wild colour and PoolSize - 1 others, picked once a visit.</summary>
    void PickPool()
    {
        var rest = Enumerable.Range(1, FroxPreset.Colours.Length - 1).OrderBy(_ => _random.Next()).Take(FroxPreset.PoolSize - 1);
        _pool = [0, .. rest];
        Count?.Invoke("frox.pool");
    }

    FroxDraw Draw(Frox f)
    {
        var c = Clips;
        (int clip, float seconds, int frames) = f.State switch
        {
            FroxState.Crawl => (1, c.WalkSeconds, c.WalkFrames),
            FroxState.Turn or FroxState.Run or FroxState.Dive => (2, c.RunSeconds, c.RunFrames),
            _ => (0, c.RestSeconds, c.RestFrames),
        };
        return new FroxDraw(f.Id, f.Pos, f.Yaw, f.Scale, f.Colour, clip, Frame(clip == 2 ? f.Anim * FroxPreset.RunRate : f.Anim, seconds, frames));
    }

    /// <summary>The frame at <paramref name="time"/> into a looping clip (0 for bad input).</summary>
    public static int Frame(float time, float seconds, int frames)
    {
        if (frames <= 0 || !float.IsFinite(time) || !float.IsFinite(seconds) || seconds <= 0) return 0;
        double u = time / seconds;
        u -= Math.Floor(u);
        return Math.Clamp((int)(u * frames), 0, frames - 1);
    }

    void StepOne(Frox f, float dt, Vector3 focus, Func<float, float, float> ground, IReadOnlyList<Vector3> people)
    {
        f.Clock += dt;
        f.Anim += dt;
        if (f.Anim > 3600) f.Anim -= 3600;
        switch (f.State)
        {
            case FroxState.Away:
                f.Timer -= dt;
                if (f.Timer <= 0) TryRest(f, focus, ground, people);
                return;
            case FroxState.Rest or FroxState.Crawl:
                if (Flat(f.Pos - focus).LengthSquared() > 450f * 450f)
                {
                    GoAway(f, 0.5f);
                    return;
                }
                if (Near(f.Pos, people, FroxPreset.FleeRadius))
                {
                    StartFlee(f, ground);
                    return;
                }
                if (f.State == FroxState.Crawl) CrawlStep(f, dt, ground);
                else RestStep(f, dt, ground);
                return;
            case FroxState.Turn:
                f.Yaw = Lerp(f.TurnFrom, f.TurnTo, WingPreset.Smooth(f.Clock / FroxPreset.TurnSeconds));
                if (f.Clock >= FroxPreset.TurnSeconds) (f.State, f.Clock) = (FroxState.Run, 0);
                return;
            case FroxState.Run:
                RunStep(f, dt, ground);
                return;
            case FroxState.Dive:
                float u = Math.Clamp(f.Clock / FroxPreset.DiveSeconds, 0, 1);
                f.Pos.Y = WaterLevel - FroxPreset.DiveDepth * WingPreset.Smooth(u);
                if (u >= 1) GoAway(f, Range(FroxPreset.ReturnMin, FroxPreset.ReturnMax));
                return;
        }
    }

    void TryRest(Frox f, Vector3 focus, Func<float, float, float> ground, IReadOnlyList<Vector3> people)
    {
        for (int tries = 0; tries < 24; tries++)
        {
            float a = Range(0, MathF.Tau), d = MathF.Sqrt(Range(0, 1)) * 300;
            var p = new Vector3(focus.X + MathF.Cos(a) * d, 0, focus.Z + MathF.Sin(a) * d);
            float g = Safe(ground, p.X, p.Z);
            if (!float.IsFinite(g) || g < WaterLevel - FrogPreset.WaterBelow || g > WaterLevel + FrogPreset.WaterAbove) continue;
            if (WaterNear(p, ground) is not { } water) continue;
            p.Y = Math.Max(g, WaterLevel);
            if (Near(p, people, FroxPreset.SpawnClear)) continue;
            f.Pos = p;
            f.Water = water;
            // lies along the bank, its head turned a little away from the water
            float toWater = MathF.Atan2(water.X - p.X, water.Z - p.Z);
            f.Yaw = toWater + MathF.PI * (0.5f + Range(-0.3f, 0.3f)) * (_random.NextDouble() < 0.5 ? 1 : -1);
            f.Scale = Range(FroxPreset.ScaleMin, FroxPreset.ScaleMax);
            f.Colour = _pool.Length > 0 ? _pool[_random.Next(_pool.Length)] : 0;
            f.State = FroxState.Rest;
            f.Clock = 0;
            f.Anim = Range(0, 10);
            f.Timer = Range(FroxPreset.RestMin, FroxPreset.RestMax);
            Count?.Invoke("frox.rest");
            return;
        }
        f.Timer = Range(1, 3);
        Count?.Invoke("frox.no_shore");
    }

    void RestStep(Frox f, float dt, Func<float, float, float> ground)
    {
        f.Pos.Y = Surface(ground, f.Pos);
        f.Timer -= dt;
        if (f.Timer > 0) return;
        f.State = FroxState.Crawl;
        f.Clock = 0;
        f.Timer = Range(FroxPreset.CrawlMin, FroxPreset.CrawlMax);
        f.TurnFrom = f.Yaw;
        f.TurnTo = f.Yaw + Range(-0.8f, 0.8f);
        Count?.Invoke("frox.crawl");
    }

    /// <summary>Crawls at its walk's ground speed, easing round to a new heading; stops at the end of the bank band (never into the water:
    /// that is the run's) or when its time is up.</summary>
    void CrawlStep(Frox f, float dt, Func<float, float, float> ground)
    {
        f.Yaw = Lerp(f.TurnFrom, f.TurnTo, WingPreset.Smooth(f.Clock / 1.0f));
        var dir = new Vector3(MathF.Sin(f.Yaw), 0, MathF.Cos(f.Yaw));
        var next = f.Pos + dir * Clips.WalkSpeed * f.Scale * dt;
        float g = Safe(ground, next.X, next.Z);
        f.Timer -= dt;
        if (!float.IsFinite(g) || g < WaterLevel - FrogPreset.WaterBelow || g > WaterLevel + FrogPreset.WaterAbove || f.Timer <= 0)
        {
            f.State = FroxState.Rest;
            f.Timer = Range(FroxPreset.RestMin, FroxPreset.RestMax);
            return;
        }
        f.Pos = new Vector3(next.X, Math.Max(g, WaterLevel), next.Z);
    }

    void StartFlee(Frox f, Func<float, float, float> ground)
    {
        var water = WaterNear(f.Pos, ground) ?? f.Water;
        f.Water = new Vector3(water.X, WaterLevel, water.Z);
        f.TurnFrom = f.Yaw;
        float to = MathF.Atan2(f.Water.X - f.Pos.X, f.Water.Z - f.Pos.Z);
        f.TurnTo = f.Yaw + Wrap(to - f.Yaw);
        f.State = FroxState.Turn;
        f.Clock = 0;
        Count?.Invoke("frox.flee");
    }

    /// <summary>Runs at its run's ground speed toward the water; once over open water (or after RunLongest) it slips under with a splash.</summary>
    void RunStep(Frox f, float dt, Func<float, float, float> ground)
    {
        var dir = Flat(f.Water - f.Pos);
        float left = dir.Length();
        if (left > 1e-3f) f.Yaw = MathF.Atan2(dir.X, dir.Z);
        float step = Clips.RunSpeed * FroxPreset.RunRate * f.Scale * dt;
        if (left > 1e-3f) f.Pos += dir / left * Math.Min(step, left);
        float g = Safe(ground, f.Pos.X, f.Pos.Z);
        bool wet = float.IsFinite(g) && g < WaterLevel - FrogPreset.WaterDepth;   // open water (the bank band reaches 3 under the line)
        f.Pos.Y = float.IsFinite(g) ? Math.Max(g, WaterLevel) : f.Pos.Y;
        if (!wet && left > 0.5f && f.Clock < FroxPreset.RunLongest) return;
        if (_splashes.Count < FroxPreset.SplashQueue) _splashes.Add(new Vector3(f.Pos.X, WaterLevel, f.Pos.Z));
        else Count?.Invoke("frox.splash_dropped");
        f.State = FroxState.Dive;
        f.Clock = 0;
        Count?.Invoke("frox.splash");
    }

    void GoAway(Frox f, float seconds)
    {
        f.State = FroxState.Away;
        f.Timer = seconds;
        f.Clock = 0;
        Count?.Invoke("frox.away");
    }

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

    static float Wrap(float a)
    {
        if (!float.IsFinite(a)) return 0;
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
    }

    static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);
}
