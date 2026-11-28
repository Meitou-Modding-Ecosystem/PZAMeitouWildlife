// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The Mire Stilt's drawing. Two bakes in the froxlotl's format (FroxAsset; mod/tools/stilt/bake_stilt.py): assets/stilt/stilt_ground.pzaf (the
// standing mesh, wings folded: idle, wade, hunt, the take-off's head, the landing's tail) and stilt_air.pzaf (the flight mesh: the take-off's
// tail, flap, glide, the landing's head), each with its own texture. Each (mesh, clip, frame) is one debris mesh, built when first asked for
// by the DebrisRenderer.Load prefix ("pza.stilt.mesh#1.2.7?pza.stilt.air.png"). The models are the user's own (Meshy); the rig and clips are
// Mesh2Motion's CC0 bird plus PZA's wade and hunt. Display only.
using System.Numerics;
using Meitou.Game.Rendering.Objects;

namespace PZA.Wildlife.Meitou;

public static class StiltMesh
{
    public const string Name = "pza.stilt.mesh";
    public static readonly string[] Meshes = ["ground", "air"];
    /// <summary>The texture's sheen (alpha = gloss; 255 draws as chrome).</summary>
    public const byte Gloss = 40;
    /// <summary>Half the body's width (units): vertices nearer the centre line than this are the body and legs, not the wings.</summary>
    public const float BodyHalfWidth = 2.5f;
    static FroxAsset?[]? s_assets;
    static long s_lastStep;
    public static string Folder = "";

    public static bool Is(string? file) => file is not null && file.StartsWith(Name, StringComparison.OrdinalIgnoreCase);
    public static string TextureName(int mesh) => "pza.stilt." + Meshes[Math.Clamp(mesh, 0, 1)] + ".png";

    /// <summary>"pza.stilt.mesh#mesh.clip.frame?pza.stilt.ground.png".</summary>
    public static string PieceName(int mesh, int clip, int frame) => $"{Name}#{mesh}.{clip}.{frame}?{TextureName(mesh)}";

    /// <summary>Mesh, clip and frame from a piece name; false for anything else (a bad mesh index or texture too).</summary>
    public static bool Parse(string file, out int mesh, out int clip, out int frame)
    {
        mesh = clip = frame = 0;
        int hash = file.IndexOf('#'), q = file.IndexOf('?');
        if (!Is(file) || hash < 0 || q < hash) return false;
        var parts = file[(hash + 1)..q].Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out mesh) || !int.TryParse(parts[1], out clip) || !int.TryParse(parts[2], out frame)) return false;
        return mesh is 0 or 1 && clip is >= 0 and <= 4 && frame >= 0 && file[(q + 1)..] == TextureName(mesh);
    }

    /// <summary>The clips' timing and where they meet, from the two bakes (pure; the check reads the shipped bakes through it).</summary>
    public static StiltClips ClipsOf(FroxAsset ground, FroxAsset air)
    {
        // the body's (and legs') lowest point, within BodyHalfWidth of the centre line: the spread wings' tips dip far below it on a down-stroke
        static float Low((Vector3[] P, Vector3[] N) f) => f.P.Where(p => MathF.Abs(p.X) < BodyHalfWidth).DefaultIfEmpty(Vector3.Zero).Min(p => p.Y);
        static float MeanLow((Vector3[] P, Vector3[] N)[] fs) => fs.Average(f => Low(f));
        if (ground.Seconds.Length < 5 || air.Seconds.Length < 4 || ground.Frames.Length < 5 || ground.Frames[4] is null || air.Frames[3] is null)
            throw new InvalidDataException("want five ground clips and four air clips");
        return new StiltClips(
            ground.Seconds[0], ground.Frames[0].Length, ground.Seconds[1], ground.Frames[1].Length, ground.Clips.WalkSpeed,
            ground.Seconds[2], ground.Frames[2].Length, ground.Seconds[3], ground.Frames[3].Length, ground.Seconds[4], ground.Frames[4].Length,
            air.Seconds[0], air.Frames[0].Length, air.Seconds[1], air.Frames[1].Length, air.Seconds[2], air.Frames[2].Length,
            air.Seconds[3], air.Frames[3].Length,
            LandLow: Low(air.Frames[3][0]), GlideLow: MeanLow(air.Frames[2]), FlapLow: MeanLow(air.Frames[1]), TakeoffEndLow: Low(air.Frames[0][^1]));
    }

    /// <summary>Both bakes, read once (null when either is missing or malformed: the stilts stay off, logged once).</summary>
    public static FroxAsset?[]? Assets
    {
        get
        {
            if (s_assets is not null) return s_assets[0] is null ? null : s_assets;
            s_assets = new FroxAsset?[2];
            string dir = Path.Combine(Folder, "assets", "stilt");
            try
            {
                var ground = FroxAsset.Read(File.ReadAllBytes(Path.Combine(dir, "stilt_ground.pzaf")));
                var air = FroxAsset.Read(File.ReadAllBytes(Path.Combine(dir, "stilt_air.pzaf")));
                Driver.Stilts.Clips = ClipsOf(ground, air);
                (s_assets[0], s_assets[1]) = (ground, air);
                Log.Line($"mire stilts: bakes read (standing {ground.Vertices} vertices, flight {air.Vertices}; hunt {ground.Frames[2].Length} frames, "
                         + $"wade {Driver.Stilts.Clips.WadeSpeed:0.00} units/s); the user's own Meshy models on Mesh2Motion's bird rig");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Line("mire stilts OFF: " + dir + ": " + e.Message);
                s_assets[0] = null;
                return null;
            }
            return s_assets;
        }
    }

    /// <summary>Host thread, once a frame (AddPieces postfix): steps them, plays their splashes, adds one piece each.</summary>
    public static void AddPieces(List<DebrisPiece> pieces, Func<string, Vector3?> centreOf)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = s_lastStep == 0 ? 0 : (float)System.Diagnostics.Stopwatch.GetElapsedTime(s_lastStep, now).TotalSeconds;
        s_lastStep = now;
        if (Assets is null) return;
        Driver.StepStilts(dt);
        BiteEffects.PlaySplashes(Driver.Stilts.TakeSplashes());
        foreach (var s in Driver.Stilts.Snapshot())
        {
            string name = PieceName(s.Mesh, s.Clip, s.Frame);
            if (centreOf(name) is not { } centre) continue;
            var rotation = Quaternion.CreateFromYawPitchRoll(s.Yaw, 0, 0);
            pieces.Add(new DebrisPiece(name, s.Scale, rotation, s.Position + Vector3.Transform(centre * s.Scale, rotation), Id: s.Id));
        }
        Counters.Add("stilt.drawn");
    }

    /// <summary>The Load prefix's step: the cached frame, or one built now with its own texture (the lizard coat when that can't load).</summary>
    public static object? Get(DebrisRenderer renderer, string file)
    {
        var meshes = Access.Meshes(renderer);
        if (meshes.Contains(file)) return meshes[file];
        object? mesh = null;
        try
        {
            if (Assets is { } assets && Parse(file, out int m, out int clip, out int frame) && assets[m] is { } asset && clip < asset.Frames.Length && asset.Frames[clip] is not null)
            {
                string tex = TextureName(m);
                string path = Path.Combine(Folder, "assets", "stilt", "textures", Meshes[m] + ".png");
                bool ok = FroxMesh.Textures.RegisterImage(renderer, tex, () => File.Exists(path) ? FrogSkin.WithGloss(FroxMesh.Textures.Load(path), Gloss) : null);
                mesh = Access.Build(renderer, file[..file.IndexOf('?')], asset.Mesh(clip, frame), ok ? tex : FroxMesh.Fallback);
                Counters.Add("stilt.frame_made");
            }
        }
        catch (Exception e) { Log.Exception("stilt.mesh", e); }
        meshes[file] = mesh;
        return mesh;
    }
}
