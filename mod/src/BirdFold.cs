// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Folded wings for landed birds. The game's bird mesh is a gliding bird (birdtype1.mesh: head +z, tail -z, a body under 2 units wide, wings
// spread along x to +-26 units, tips curved up), and it has no skeleton or animation. A landed bird is drawn with a variant of the same mesh
// whose wing vertices are moved in memory when the mesh is loaded: swept back along the body and laid over the back (Fold), or half way
// (a hop's wing flick). Nothing of the game's is copied or written: the variant is made from the installed file each run.
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
}
