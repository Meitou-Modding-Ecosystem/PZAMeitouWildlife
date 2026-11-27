// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Blood where a landed bird bites at a body: the game's own hit mist (`Bloodsplash01`) and a small upward spray (`HackBlood-system`)
// between the beak and the body. Display
// only, through Meitou's code-mod effect API (ModContext.PlayEffect with an EffectAnchor, docs/modding.md "Visual effects"; nothing is
// simulated or saved). That API arrived in Meitou 0.22.2-nightly.20261123, after the releases this mod is built against, so it is found by
// reflection once: a game without it keeps the birds pecking with no blood (one log line says so). The rules queue bites on the bird thread;
// the host thread plays them (the API refuses calls off it), at most BurstsPerStep at a time.
using System.Numerics;
using System.Reflection;
using Meitou.Modding;

namespace PZA.Wildlife.Meitou;

public static class BiteEffects
{
    /// <summary>The game's blood mist (an EFFECT / Particle Universe system the blood bursts use), its size against a hit's (the mist of a
    /// sword hit is far bigger than a beak), and how long it emits (s).</summary>
    public const string Effect = "Bloodsplash01";
    public const float Stretch = 1.0f, Seconds = 0.4f;
    /// <summary>The game's hit spray (it emits along +Z; turned to spray up and out of the body), smaller than a sword hit's.</summary>
    public const string Spray = "HackBlood-system";
    public const float SprayStretch = 0.35f, SpraySeconds = 0.2f;
    static readonly Quaternion SprayTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -1.2f);
    /// <summary>The most bursts started in one bird step (the API starts at most 16 a frame for every mod together).</summary>
    public const int BurstsPerStep = 3;

    static ModContext? s_context;
    static MethodInfo? s_play, s_has, s_at, s_scaled;
    public static string? Missing { get; private set; } = "not set up";

    /// <summary>Finds PlayEffect(string, EffectAnchor, float, Vector3?), HasEffect(string), EffectAnchor.At(Vector3, Quaternion?) and
    /// EffectAnchor.Scaled(float); returns the log line.</summary>
    public static string Setup(ModContext context)
    {
        s_context = context;
        var anchor = typeof(ModContext).Assembly.GetType("Meitou.Modding.EffectAnchor");
        if (anchor is null)
        {
            Missing = "this game has no code-mod effects (Meitou 0.22.2-nightly.20261123 or newer has them)";
            return "blood while pecking OFF: " + Missing;
        }
        s_play = typeof(ModContext).GetMethod("PlayEffect", [typeof(string), anchor, typeof(float), typeof(Vector3?)]);
        s_has = typeof(ModContext).GetMethod("HasEffect", [typeof(string)]);
        s_at = anchor.GetMethod("At", BindingFlags.Public | BindingFlags.Static, [typeof(Vector3), typeof(Quaternion?)]);
        s_scaled = anchor.GetMethod("Scaled", [typeof(float)]);
        var lacks = new[] { (s_play, "ModContext.PlayEffect(string, EffectAnchor, float, Vector3?)"), (s_has, "ModContext.HasEffect"),
                            (s_at, "EffectAnchor.At"), (s_scaled, "EffectAnchor.Scaled") }.Where(x => x.Item1 is null).Select(x => x.Item2).ToList();
        Missing = lacks.Count == 0 ? null : string.Join(", ", lacks);
        return Missing is null ? $"blood while pecking: {Effect} at {Stretch}x through ModContext.PlayEffect"
                               : "blood while pecking OFF: the game lacks " + Missing;
    }

    /// <summary>The splash where a frog lands in the water: the game's foot splash (Meitou's water effects), at most a few a frame.</summary>
    public const string SplashEffect = "Footsplashes01";

    public static void PlaySplashes(List<Vector3> splashes)
    {
        if (splashes.Count == 0 || Missing is not null || s_context is null) return;
        if (!(bool)s_has!.Invoke(s_context, [SplashEffect])!)
        {
            Counters.Add("frogs.no_splash_effect");
            return;
        }
        foreach (var p in splashes.Take(BurstsPerStep))
        {
            var anchor = s_at!.Invoke(null, [p, null]);
            bool ok = (bool)s_play!.Invoke(s_context, [SplashEffect, anchor, 0.4f, null])!;
            Counters.Add(ok ? "frogs.splash_drawn" : "frogs.splash_refused");
        }
    }

    /// <summary>Host thread, once a bird step: plays the queued bites (the oldest first, at most <see cref="BurstsPerStep"/>; the rest dropped).</summary>
    public static void Play(WildCore core)
    {
        var bites = core.TakeBites();
        if (bites.Count == 0) return;
        if (Missing is not null || s_context is null || !Driver.Settings.Blood)
        {
            Counters.Add("bite.not_drawn");
            return;
        }
        if (!(bool)s_has!.Invoke(s_context, [Effect])!)
        {
            Counters.Add("bite.no_particles");   // particles not loaded yet, or turned off
            return;
        }
        bool sprays = (bool)s_has.Invoke(s_context, [Spray])!;
        for (int i = 0; i < bites.Count; i++)
        {
            if (i >= BurstsPerStep)
            {
                Counters.Add("bite.capped");
                continue;
            }
            var anchor = s_scaled!.Invoke(s_at!.Invoke(null, [bites[i], null]), [Stretch]);
            bool ok = (bool)s_play!.Invoke(s_context, [Effect, anchor, Seconds, null])!;
            if (sprays)
            {
                var spray = s_scaled.Invoke(s_at.Invoke(null, [bites[i], (Quaternion?)SprayTurn]), [SprayStretch]);
                ok &= (bool)s_play.Invoke(s_context, [Spray, spray, SpraySeconds, null])!;
            }
            Counters.Add(ok ? "bite.drawn" : "bite.refused");
        }
    }
}
