// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The birds' clips (0.11.0, user: "I want to keep the game's original bird, on that animation rig"): which of Mesh2Motion's bird clips (CC0)
// a bird shows and how far through it. Pure, for the check: the rules (WildBird) and the free birds' beat (BirdFold.FreePose) pick; the
// drawing (BirdRig) turns (clip, u) into a frame of the game's own bird on Mesh2Motion's skeleton.
namespace PZA.Wildlife.Meitou;

public enum RigClip { Glide = 0, Flap = 1, Land = 2, TakeOff = 3, Hop = 4, Idle = 5 }

/// <summary>A clip and how far through it (0..1, loops wrapped).</summary>
public readonly record struct RigFrame(RigClip Clip, float U)
{
    /// <summary>The frame index in a clip of <paramref name="frames"/> frames (0 for bad input).</summary>
    public int Index(int frames) => frames <= 0 || !float.IsFinite(U) ? 0 : Math.Clamp((int)(Math.Clamp(U, 0, 1) * frames), 0, frames - 1);
}

public static class RigPreset
{
    /// <summary>The clips' lengths as baked (s), for the loops' timing; the check holds them to the bake.</summary>
    public const float GlideSeconds = 0.54f, IdleSeconds = 1.17f, TakeOffSeconds = 1.33f;
    /// <summary>A take-off plays its first TakeOffPart in TakeOffPlay seconds (the leap and first beats), then the flap loop takes over;
    /// touch-down plays the land clip from LandFrom to its end over the fold and the settle.</summary>
    public const float TakeOffPart = 0.8f, TakeOffPlay = 0.7f, LandFrom = 0.45f;

    /// <summary>0..1, NaN and infinities to 0.</summary>
    public static float Unit(float u) => float.IsFinite(u) ? Math.Clamp(u, 0, 1) : 0;

    public static float Loop(float t, float seconds)
    {
        if (!float.IsFinite(t) || !(seconds > 0)) return 0;
        double u = t / seconds;
        return (float)(u - Math.Floor(u));
    }

    /// <summary>A held bird's clip from its phase (the same timing as its wing pose).</summary>
    public static RigFrame For(WildBird b)
    {
        switch (b.Phase)
        {
            case Phase.Gather:
                float c = b.Clock % WingPreset.BurstEvery;
                return c < WingPreset.BurstSeconds ? new(RigClip.Flap, Loop(c, 1 / WingPreset.BurstHz)) : new(RigClip.Glide, Loop(b.Clock, GlideSeconds));
            case Phase.Approach:
                if (!(b.Length > 0)) return new(RigClip.Glide, 0);
                float fold = Math.Min(WingPreset.TouchSeconds, 0.45f * b.Length), folding = b.Length - fold;
                if (b.Clock < WingPreset.BrakeFrom * b.Length) return new(RigClip.Glide, Loop(b.Clock, GlideSeconds));
                if (b.Clock < folding) return new(RigClip.Flap, Loop(b.Clock - WingPreset.BrakeFrom * b.Length, 1 / WingPreset.BrakeHz));
                return new(RigClip.Land, LandFrom + (0.85f - LandFrom) * Unit(fold > 0 ? (b.Clock - folding) / fold : 1));
            case Phase.Peck:
                if (b.Clock < WingPreset.Settle) return new(RigClip.Land, 0.85f + 0.149f * Unit(b.Clock / WingPreset.Settle));
                if (b.Action == 2) return new(RigClip.Hop, Math.Min(Unit(b.ActionClock / Preset.HopSeconds), 0.999f));
                return new(RigClip.Idle, Loop(b.Clock, IdleSeconds));
            case Phase.Flee:
                if (b.Clock < b.Delay) return new(RigClip.Idle, Loop(b.Clock, IdleSeconds));
                if (b.Clock < b.Delay + TakeOffPlay) return new(RigClip.TakeOff, TakeOffPart * Unit((b.Clock - b.Delay) / TakeOffPlay));
                return new(RigClip.Flap, Loop(b.Clock - b.Delay - TakeOffPlay, 1 / WingPreset.ClimbHz));
            default:
                return new(RigClip.Glide, Loop(b.Clock, GlideSeconds));
        }
    }

    /// <summary>A free bird: its beat (BirdFold.FreePose: a flap frame or 0 = glide); resting birds sit idle.</summary>
    public static RigFrame Free(int pose, bool resting, long id, double seconds)
    {
        float offset = (float)((ulong)(id * 2654435761L) % 1000) / 1000f;
        if (resting) return new(RigClip.Idle, Loop((float)(seconds % 3600) + offset * IdleSeconds, IdleSeconds));
        if (BirdFold.IsFlap(pose, out int frame)) return new(RigClip.Flap, frame / (float)BirdFold.FlapFrames);
        return new(RigClip.Glide, Loop((float)(seconds % 3600) + offset * GlideSeconds, GlideSeconds));
    }
}
