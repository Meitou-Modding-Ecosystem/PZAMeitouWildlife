// PZA's Wildlife for Meitou - Swamp Fireflies (pure System.Numerics rules, render-side only, never saved).
// Author: PZA. Licence: GPLv3 or later.
using System.Numerics;

namespace PZA.Wildlife.Meitou;

public static class FireflyPreset
{
    public const int MaxCount = 64;
    /// <summary>Spawn/keep radius around the camera focus (Kenshi units: 10 = 1 m, so 350 = 35 m).</summary>
    public const float Radius = 350f;
    public const float MinHeight = 6f, MaxHeight = 32f;
    public const float DriftSpeed = 4.5f;
    /// <summary>Soft billboard diameter (Kenshi units: 0.7..1.6 = 7..16 cm soft halo with bright pinpoint core).</summary>
    public const float MinSize = 0.8f, MaxSize = 2.4f;
    /// <summary>Dusk and dawn hours (24h clock): smooth fade in over FadeHours around dusk, out around dawn.</summary>
    public const double DuskHour = 18.5, DawnHour = 5.5, FadeHours = 1.5;
    /// <summary>Rain strength (0..1) above which fireflies thin out down to RainFloor (30% in heavy swamp rain).</summary>
    public const float RainFadeStart = 0.35f, RainFadeEnd = 0.80f, RainFloor = 0.30f;
    /// <summary>Near-camera fade range (Kenshi units: 10 = 1 m, 30 = 3 m) so motes vanish smoothly before crossing the lens.</summary>
    public const float NearFadeStart = 10f, NearFadeEnd = 30f;
    /// <summary>A blink: the flash lasts this long (s); between flashes the fly keeps this much of its light (an ember, not black).</summary>
    public const float Flash = 0.55f, Ember = 0.06f;
    /// <summary>Real light on the ground: the nearest LightCount flies within LightReach of the eye each cast a point light of LightRadius
    /// (units: 30 = 3 m) whose power is LightPower times the fly's brightness.</summary>
    public const int LightCount = 8;
    public const float LightRadius = 30f, LightPower = 1.2f, LightReach = 600f;
}

/// <summary>One glowing firefly mote for the renderer: position, soft billboard size, and 0..1 pulse brightness.</summary>
public readonly record struct FireflyDraw(long Id, Vector3 Position, float Size, float Brightness);

public sealed class FireflyCore
{
    struct Fly
    {
        public long Id;
        public Vector3 Pos;
        public Vector3 Anchor;
        public float Phase, Period, BobFreq, WanderAngle, HeightOffset;
    }

    readonly object _stateLock = new();
    readonly List<Fly> _flies = new(FireflyPreset.MaxCount);
    readonly object _snapLock = new();
    FireflyDraw[] _snapshot = [];
    ulong _rng = 0x9E37_79B9_7F4A_7C15UL;
    long _nextId = 900_000_000L;
    float _time;

    public bool Enabled { get; set; } = true;
    /// <summary>Water surface height (world units; NegativeInfinity = disabled in unit tests, 100 in Kenshi/Meitou).</summary>
    public float WaterLevel { get; set; } = float.NegativeInfinity;
    public Action<string>? Count;
    public int ActiveCount { get { lock (_stateLock) return _flies.Count; } }

    /// <summary>0 during daylight, 1 at night, smooth ramp over FadeHours at dusk and dawn.</summary>
    public static float NightFactor(double gameHours)
    {
        if (!double.IsFinite(gameHours)) return 0f;
        double h = ((gameHours % 24.0) + 24.0) % 24.0;
        double dusk0 = FireflyPreset.DuskHour - FireflyPreset.FadeHours * 0.5;
        double dusk1 = FireflyPreset.DuskHour + FireflyPreset.FadeHours * 0.5;
        double dawn0 = FireflyPreset.DawnHour - FireflyPreset.FadeHours * 0.5;
        double dawn1 = FireflyPreset.DawnHour + FireflyPreset.FadeHours * 0.5;
        if (h >= dusk1 || h <= dawn0) return 1f;
        if (h > dusk0 && h < dusk1) return Smooth((float)((h - dusk0) / (dusk1 - dusk0)));
        if (h > dawn0 && h < dawn1) return 1f - Smooth((float)((h - dawn0) / (dawn1 - dawn0)));
        return 0f;
    }

    /// <summary>1 in dry/light mist, thins to RainFloor (0.30) in heavy rain so swamp nights stay alive.</summary>
    public static float RainFactor(float rain) =>
        float.IsFinite(rain) ? FireflyPreset.RainFloor + (1f - FireflyPreset.RainFloor) * (1f - Smooth((rain - FireflyPreset.RainFadeStart) / (FireflyPreset.RainFadeEnd - FireflyPreset.RainFadeStart))) : 1f;

    /// <summary>0 within NearFadeStart of the camera eye, smooth ramp to 1 at NearFadeEnd.</summary>
    public static float NearFade(float dist) =>
        float.IsFinite(dist) ? Smooth((dist - FireflyPreset.NearFadeStart) / (FireflyPreset.NearFadeEnd - FireflyPreset.NearFadeStart)) : 0f;

    /// <summary>True for swamp biome names (e.g. "Swamp", "The Swamp", "Swamplands").</summary>
    public static bool IsSwampBiome(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Contains("swamp", StringComparison.OrdinalIgnoreCase);

    /// <summary>0..1: <see cref="FireflyPreset.Ember"/> between flashes, a smooth flash of <see cref="FireflyPreset.Flash"/> seconds once a
    /// period (rising fast, fading slower).</summary>
    public static float Blink(float time, float period, float phase)
    {
        if (!float.IsFinite(time) || !float.IsFinite(period) || period <= 0 || !float.IsFinite(phase)) return FireflyPreset.Ember;
        float t = time + phase / MathF.Tau * period;
        float into = t - MathF.Floor(t / period) * period;   // seconds into this period, 0..period
        float flash = Math.Min(FireflyPreset.Flash, period);
        if (into >= flash) return FireflyPreset.Ember;
        float u = into / flash;
        float shape = u < 0.3f ? Smooth(u / 0.3f) : 1 - Smooth((u - 0.3f) / 0.7f);
        return FireflyPreset.Ember + (1 - FireflyPreset.Ember) * shape;
    }

    static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    float Next01()
    {
        _rng ^= _rng >> 12;
        _rng ^= _rng << 25;
        _rng ^= _rng >> 27;
        return ((_rng * 0x2545_F491_4F6C_DD1DUL) >> 40) / 16777216f;
    }

    public void Reset()
    {
        lock (_stateLock) _flies.Clear();
        lock (_snapLock) _snapshot = [];
    }

    public FireflyDraw[] Snapshot()
    {
        lock (_snapLock) return _snapshot;
    }

    public void Step(float dt, Vector3 focus, double gameHours, bool inSwamp, Func<float, float, float>? ground, float rain = 0f)
    {
        if (!float.IsFinite(dt) || dt <= 0f) dt = 0f;
        dt = Math.Min(dt, 0.25f);

        lock (_stateLock)
        {
            _time += dt;

            // Rain thins how many flies are out; only dusk and dawn dim each fly (2026-11-27: the rain factor also scaled every fly's
            // brightness, so in swamp rain no fly ever got past 0.3 and none was seen to flash).
            float night = Enabled && inSwamp ? NightFactor(gameHours) : 0f;
            float factor = night * RainFactor(rain);
            if (factor <= 0.02f || !float.IsFinite(focus.X) || !float.IsFinite(focus.Y) || !float.IsFinite(focus.Z))
            {
                if (_flies.Count > 0)
                {
                    _flies.Clear();
                    lock (_snapLock) _snapshot = [];
                }
                return;
            }

            int target = Math.Clamp((int)MathF.Round(FireflyPreset.MaxCount * factor), 1, FireflyPreset.MaxCount);
            float maxDistSq = (FireflyPreset.Radius * 1.25f) * (FireflyPreset.Radius * 1.25f);

            // Respawn flies that drifted too far from the focus, or trim if night/weather is fading
            while (_flies.Count > target) _flies.RemoveAt(_flies.Count - 1);
            while (_flies.Count < target) _flies.Add(Spawn(focus, ground));

            var draw = new FireflyDraw[_flies.Count];
            for (int i = 0; i < _flies.Count; i++)
            {
                var f = _flies[i];
                float dx = f.Pos.X - focus.X, dz = f.Pos.Z - focus.Z;
                if (dx * dx + dz * dz > maxDistSq || !float.IsFinite(f.Pos.X) || !float.IsFinite(f.Pos.Y) || !float.IsFinite(f.Pos.Z))
                {
                    f = Spawn(focus, ground);
                }
                else if (dt > 0f)
                {
                    f.WanderAngle += (Next01() - 0.5f) * 1.6f * dt;
                    float vx = MathF.Cos(f.WanderAngle) * FireflyPreset.DriftSpeed;
                    float vz = MathF.Sin(f.WanderAngle) * FireflyPreset.DriftSpeed;
                    float nx = f.Pos.X + vx * dt;
                    float nz = f.Pos.Z + vz * dt;
                    float g = SafeSurface(ground, nx, nz, focus.Y);
                    float bob = MathF.Sin(_time * f.BobFreq + f.Phase) * 2.5f;
                    float ny = Math.Max(g + 1f, g + f.HeightOffset + bob);
                    f.Pos = new Vector3(nx, ny, nz);
                }
                _flies[i] = f;

                // A firefly's blink: a short flash, then mostly dark (2026-11-27: a sine between 0.15 and 1 read as always-on once
                // tone-mapped). Each fly keeps its own period and phase, so the swamp twinkles out of step.
                float pulse = Blink(_time, f.Period, f.Phase) * night;
                float size = FireflyPreset.MinSize + (FireflyPreset.MaxSize - FireflyPreset.MinSize) * pulse;
                draw[i] = new FireflyDraw(f.Id, f.Pos, size, pulse);
            }

            lock (_snapLock) _snapshot = draw;
        }
        Count?.Invoke("fireflies.step");
    }

    Fly Spawn(Vector3 focus, Func<float, float, float>? ground)
    {
        float ang = Next01() * MathF.Tau;
        float dist = MathF.Sqrt(Next01()) * FireflyPreset.Radius;
        float x = focus.X + MathF.Cos(ang) * dist;
        float z = focus.Z + MathF.Sin(ang) * dist;
        float h = FireflyPreset.MinHeight + Next01() * (FireflyPreset.MaxHeight - FireflyPreset.MinHeight);
        float g = SafeSurface(ground, x, z, focus.Y);
        var pos = new Vector3(x, g + h, z);
        return new Fly
        {
            Id = _nextId++,
            Pos = pos,
            Anchor = pos,
            Phase = Next01() * MathF.Tau,
            Period = 2.4f + Next01() * 2.8f,
            BobFreq = 0.7f + Next01() * 0.9f,
            WanderAngle = Next01() * MathF.Tau,
            HeightOffset = h,
        };
    }

    float SafeSurface(Func<float, float, float>? ground, float x, float z, float fallback)
    {
        float y = fallback;
        if (ground is not null)
        {
            try
            {
                float g = ground(x, z);
                if (float.IsFinite(g)) y = g;
            }
            catch { }
        }
        if (float.IsFinite(WaterLevel) && y < WaterLevel) y = WaterLevel;
        return y;
    }
}
