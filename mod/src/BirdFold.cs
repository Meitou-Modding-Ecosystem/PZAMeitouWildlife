// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Folded wings for landed birds. The game's bird mesh is a gliding bird (birdtype1.mesh: head +z, tail -z, a body under 2 units wide, wings
// spread along x to +-26 units, tips curved up), and it has no skeleton or animation. A landed bird is drawn with a variant of the same mesh
// whose wing vertices are moved in memory when the mesh is loaded: swept back along the body and laid over the back (Fold), or half way
// (a hop's wing flick). Nothing of the game's is copied or written: the variant is made from the installed file each run.
// Wing poses (0.7.0, user 2026-11-27: "smoother ... less janky wing flapping"): fold levels in steps of 5 (so a landing folds and a take-off
// opens over several frames, not in one snap) and a 16-frame wing beat (Flap: the span turned up and down about the shoulder, the tips lagging),
// each its own variant made the same way. A pose is one int: 0 = the game's own mesh, 1..100 = a fold level, 101..116 = a flap frame.
using System.Numerics;

namespace PZA.Wildlife.Meitou;

public static class BirdFold
{
    /// <summary>The marker in a mesh name that asks for a folded variant: "birdtype1.mesh#pzafold100?texture.dds" (100 = fully folded).</summary>
    public const string Marker = "#pzafold";
    /// <summary>The fold levels used: full (landed) and half (a hop's wing flick).</summary>
    public const int Full = 100, Half = 50;
    /// <summary>Half the body's width (units): vertices inside it stay; the wing beyond is folded. The shoulder blend runs over the next 3 units.</summary>
    public const float Shoulder = 2, Blend = 3;

    /// <summary>True only for meshes whose geometry matches birdtype1.mesh (so modded or future flying species are not distorted).</summary>
    public static bool CanFoldMesh(string? mesh)
    {
        if (string.IsNullOrWhiteSpace(mesh)) return false;
        int mark = mesh.IndexOf('?');
        string file = mark >= 0 ? mesh[..mark] : mesh;
        int slash = file.LastIndexOfAny(['/', '\\']);
        string name = slash >= 0 ? file[(slash + 1)..] : file;
        return name.Equals("birdtype1.mesh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A mesh name with the fold marker (before the texture mark, so the texture stays the bird's own).</summary>
    public static string Name(string mesh, int level)
    {
        int mark = mesh.IndexOf('?');
        string tag = Marker + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return mark < 0 ? mesh + tag : mesh[..mark] + tag + mesh[mark..];
    }

    /// <summary>Splits a marked name into the game's own name and the level; false for a name without the marker or a bad level.</summary>
    public static bool Parse(string name, out string plain, out int level)
    {
        plain = name;
        level = 0;
        int at = name.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return false;
        int end = at + Marker.Length, stop = end;
        while (stop < name.Length && char.IsAsciiDigit(name[stop])) stop++;
        if (stop == end || stop - end > 3 || !int.TryParse(name.AsSpan(end, stop - end), out level) || level < 0 || level > 100) return false;
        plain = name[..at] + name[stop..];
        return true;
    }

    /// <summary>One vertex folded by <paramref name="amount"/> (0 = as drawn in flight, 1 = folded): beyond the shoulder the span is swept
    /// back along the body (tips just past the tail), narrowed, and laid flat along the back (2026-11-22 look: a raised fold read as a V); blended in over
    /// the first 3 units so the shoulder does not tear. A non-finite vertex is returned as it is.</summary>
    public static Vector3 Fold(Vector3 p, float amount)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return p;
        amount = Math.Clamp(float.IsFinite(amount) ? amount : 0, 0, 1);
        float d = MathF.Abs(p.X) - Shoulder;
        if (d <= 0 || amount == 0) return p;
        float t = Math.Min(1, d / Blend);
        t = t * t * (3 - 2 * t) * amount;
        float side = MathF.Sign(p.X);
        var folded = new Vector3(
            side * (Shoulder + 0.07f * d),
            0.7f + 0.012f * d + 0.2f * (p.Y - 0.011f * p.X * p.X) + 0.06f * p.Z,
            0.30f * p.Z + 1.0f - 0.36f * d);
        return Vector3.Lerp(p, folded, t);
    }

    /// <summary>The marker of a wing-beat frame: "birdtype1.mesh#pzaflap3?texture.dds".</summary>
    public const string FlapMarker = "#pzaflap";
    /// <summary>Frames in one wing beat; fold levels come in steps of FoldStep.</summary>
    public const int FlapFrames = 16, FoldStep = 5, FlapBase = 100;
    /// <summary>The beat's angles (rad, up positive): it swings Swing either side of Up (Up + Swing = 30 degrees up, Up - Swing = 25 down; the mesh's own tips already curve up);
    /// the tips turn up to TipLag more than the shoulder (a wing bends in the beat); angles are clamped to MaxAngle (with the tip lag under a right angle, so a wing never swings through the body).</summary>
    public const float Up = 0.05f, Swing = 0.48f, TipLag = 0.2f, MaxAngle = 1.0f, Pivot = 0.5f;

    /// <summary>The fold level nearest <paramref name="amount"/> (0..1) in steps of FoldStep; 0 for none (or a bad amount).</summary>
    public static int Level(float amount)
    {
        if (!float.IsFinite(amount) || amount <= 0) return 0;
        int level = (int)MathF.Round(Math.Min(amount, 1) * 100 / FoldStep) * FoldStep;
        return Math.Clamp(level, 0, 100);
    }

    /// <summary>The pose of flap frame <paramref name="frame"/> (wrapped into 0..FlapFrames-1).</summary>
    public static int FlapPose(int frame) => FlapBase + 1 + ((frame % FlapFrames) + FlapFrames) % FlapFrames;

    /// <summary>The flap pose <paramref name="seconds"/> into a beat of <paramref name="hertz"/> beats a second (frame 0 = wings near level,
    /// going up); 0 for bad input.</summary>
    public static int FlapAt(float seconds, float hertz)
    {
        if (!float.IsFinite(seconds) || !float.IsFinite(hertz) || hertz <= 0) return 0;
        double turns = (double)seconds * hertz;
        double frac = turns - Math.Floor(turns);
        return FlapPose(Math.Min(FlapFrames - 1, (int)(frac * FlapFrames)));
    }

    /// <summary>True for a flap pose, with its frame.</summary>
    public static bool IsFlap(int pose, out int frame)
    {
        frame = pose - FlapBase - 1;
        return frame >= 0 && frame < FlapFrames;
    }

    /// <summary>A pose the drawing knows: 0, a fold level 1..100, or a flap frame.</summary>
    public static bool ValidPose(int pose) => pose is >= 0 and <= 100 || IsFlap(pose, out _);

    /// <summary>The beat's angle at frame <paramref name="frame"/>: Up + Swing sin(2 pi frame / FlapFrames).</summary>
    public static float FlapAngle(int frame) => Up + Swing * MathF.Sin(MathF.Tau * (((frame % FlapFrames) + FlapFrames) % FlapFrames) / FlapFrames);

    /// <summary>A mesh name for any pose (0 = the name as it is).</summary>
    public static string PoseName(string mesh, int pose)
    {
        if (!IsFlap(pose, out int frame)) return pose <= 0 ? mesh : Name(mesh, pose);
        int mark = mesh.IndexOf('?');
        string tag = FlapMarker + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return mark < 0 ? mesh + tag : mesh[..mark] + tag + mesh[mark..];
    }

    /// <summary>Splits a fold- or flap-marked name into the game's own name and the pose; false for an unmarked or bad name.</summary>
    public static bool ParsePose(string name, out string plain, out int pose)
    {
        if (Parse(name, out plain, out pose)) return true;
        plain = name;
        pose = 0;
        int at = name.IndexOf(FlapMarker, StringComparison.Ordinal);
        if (at < 0) return false;
        int end = at + FlapMarker.Length, stop = end;
        while (stop < name.Length && char.IsAsciiDigit(name[stop])) stop++;
        if (stop == end || stop - end > 2 || !int.TryParse(name.AsSpan(end, stop - end), out int frame) || frame < 0 || frame >= FlapFrames) return false;
        plain = name[..at] + name[stop..];
        pose = FlapPose(frame);
        return true;
    }

    /// <summary>One vertex of a wing beat: beyond the shoulder the span is turned by <paramref name="angle"/> (up positive) about the shoulder
    /// line (x = +-Shoulder, y = Pivot), the tips a little more; blended in over the first 3 units like the fold. Non-finite in = out as it is.</summary>
    public static Vector3 Flap(Vector3 p, float angle)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return p;
        angle = Math.Clamp(float.IsFinite(angle) ? angle : 0, -MaxAngle, MaxAngle);
        float d = MathF.Abs(p.X) - Shoulder;
        if (d <= 0 || angle == 0) return p;
        float t = Math.Min(1, d / Blend);
        t = t * t * (3 - 2 * t);
        float a = angle * t * (1 + TipLag * Math.Min(1, d / 20));
        float h = p.Y - Pivot, c = MathF.Cos(a), s = MathF.Sin(a);
        float side = MathF.Sign(p.X);
        return new Vector3(side * (Shoulder + d * c - h * s), Pivot + d * s + h * c, p.Z);
    }

    /// <summary>One vertex in a pose (0 = as it is).</summary>
    public static Vector3 Pose(Vector3 p, int pose) =>
        IsFlap(pose, out int frame) ? Flap(p, FlapAngle(frame)) : pose > 0 ? Fold(p, Math.Min(pose, 100) / 100f) : p;

    /// <summary>A free bird the game flies (not one of ours): it beats its wings while it climbs, and now and then on the level (a burst of
    /// beats every 7 to 13 s, by its id so birds don't beat together); 0 = glide. Pure, for the check.</summary>
    public static int FreePose(long id, double seconds, float climb)
    {
        if (!double.IsFinite(seconds) || !float.IsFinite(climb)) return 0;
        float offset = (float)((ulong)(id * 2654435761L) % 1000) / 1000f;
        if (climb > 0.12f) return FlapAt((float)(seconds % 3600) + offset, 3.2f);
        double period = 7 + 6 * offset;
        double at = (seconds + offset * period) % period;
        return at < 1.1 ? FlapAt((float)at, 2.7f) : 0;
    }
}
