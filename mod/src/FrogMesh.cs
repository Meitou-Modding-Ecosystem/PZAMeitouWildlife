// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The frog's model and its drawing. 0.7.0 (user 2026-11-27, "Left is best"): the frog is the game's own Gorillo (frog_gorilla.mesh, read
// from the install each run, never copied) shrunk to frog size and a little flatter, with its own texture layout on the lizard coat
// (coat02-lizard.dds); its pose (Shape: tucked to stretched, FrogPreset.Pose) is one variant per step, made the same way. A game without
// the Gorillo gets the code-made frog below in the same poses.
// Before 0.7.0: Kenshi has no frog, so the model is made in code (never a file): a simple frog of ellipsoids (body,
// head, two eyes, folded hind legs, front legs) facing +z with its feet at y = 0, about 2.2 units long, UV-mapped onto the green scaly patch of
// the game's own lizard-coat texture (coat02-lizard.dds; a lighter patch for the belly). It is built by the debris renderer's own Build when its
// marked name ("pza.frog.mesh?coat02-lizard.dds") is first asked for (the DebrisRenderer.Load prefix, as the folded birds), and drawn as a
// debris piece beside the birds (the BirdFlocks.AddPieces postfix). Display only.
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Objects;

namespace PZA.Wildlife.Meitou;

public static class FrogMesh
{
    public const string Name = "pza.frog.mesh";
    public const string Texture = "coat02-lizard.dds";
    /// <summary>The game's mesh the frog is made from, its length at scale 1 (units, nose to heels when tucked) and how much flatter it is drawn.</summary>
    public const string Source = "frog_gorilla.mesh";
    public const float Length = 2.1f, Flatter = 0.8f;
    public static string FullName => PoseName(0);
    /// <summary>The marked name of a pose: "pza.frog.mesh#3?coat02-lizard.dds".</summary>
    public static string PoseName(int pose) => Name + "#" + Math.Clamp(pose, 0, FrogPreset.Poses - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "?" + Texture;

    /// <summary>The pose in a marked name (0 when it has none or a bad one).</summary>
    public static int PoseOf(string file)
    {
        int at = file.IndexOf('#'), end = file.IndexOf('?');
        if (at < 0) return 0;
        if (end < at) end = file.Length;
        return int.TryParse(file.AsSpan(at + 1, end - at - 1), out int pose) && pose >= 0 && pose < FrogPreset.Poses ? pose : 0;
    }
    static long s_lastStep;

    public static bool Is(string? file) => file is not null && file.StartsWith(Name, StringComparison.OrdinalIgnoreCase);

    static GorilloBake? s_bake;
    static bool s_bakeTried, s_bakeFailed;

    /// <summary>The Gorillo animation frame's name: "pza.frog.mesh#g1.5?coat02-lizard.dds" (clip 1, frame 5 of its GorilloBake.Clips frames).</summary>
    public static string AnimName(int clip, float u)
    {
        clip = Math.Clamp(clip, 0, GorilloBake.Clips.Length - 1);
        int frames = GorilloBake.Clips[clip].Frames;
        int frame = float.IsFinite(u) ? Math.Clamp((int)(u * frames), 0, frames - 1) : 0;
        return $"{Name}#g{clip}.{frame}?{Texture}";
    }

    /// <summary>Clip and frame from an animation frame's name; false for a stretch pose's name or anything else.</summary>
    public static bool ParseAnim(string file, out int clip, out int frame)
    {
        clip = frame = 0;
        int at = file.IndexOf("#g", StringComparison.Ordinal), dot = at < 0 ? -1 : file.IndexOf('.', at), end = file.IndexOf('?');
        if (at < 0 || dot < 0) return false;
        if (end < dot) end = file.Length;
        return int.TryParse(file.AsSpan(at + 2, dot - at - 2), out clip) && int.TryParse(file.AsSpan(dot + 1, end - dot - 1), out frame)
               && clip >= 0 && clip < GorilloBake.Clips.Length && frame >= 0 && frame < GorilloBake.Clips[clip].Frames;
    }

    #region Drawing

    /// <summary>Host thread, once a frame (AddPieces postfix): steps the frogs and adds one piece per frog out, placed by the model's middle.</summary>
    public static void AddPieces(List<DebrisPiece> pieces, Func<string, Vector3?> centreOf)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = s_lastStep == 0 ? 0 : (float)System.Diagnostics.Stopwatch.GetElapsedTime(s_lastStep, now).TotalSeconds;
        s_lastStep = now;
        Driver.StepFrogs(dt);
        BiteEffects.PlaySplashes(Driver.Frogs.TakeSplashes());

        var frogs = Driver.Frogs.Snapshot();
        if (frogs.Length == 0) return;
        foreach (var f in frogs)
        {
            string name = s_bakeFailed ? PoseName(f.Pose) : AnimName(f.Clip, f.U);
            if (centreOf(name) is not { } centre) continue;
            var rotation = Quaternion.CreateFromYawPitchRoll(f.Yaw, f.Pitch, 0);
            pieces.Add(new DebrisPiece(name, f.Scale, rotation, f.Position + Vector3.Transform(centre * f.Scale, rotation), Id: f.Id));
        }
        Counters.Add("frogs.drawn");
    }

    #endregion

    #region The model

    /// <summary>The Load prefix's step: the built frog (cached under its name), or null when the renderer can't build it.</summary>
    public static object? Get(DebrisRenderer renderer, string file)
    {
        var meshes = Access.Meshes(renderer);
        if (meshes.Contains(file)) return meshes[file];
        object? mesh = null;
        if (ParseAnim(file, out int clip, out int frame))
        {
            try
            {
                if (!s_bakeTried)
                {
                    s_bakeTried = true;
                    s_bake = GorilloBake.Create(Access.Find(renderer), out var why);
                    Log.Line(s_bake is null ? $"frogs: no Gorillo animations ({why}), the frogs keep their stretch poses" : "frogs: the Gorillo's idle, walk and run baked as the frog's frames");
                    s_bakeFailed = s_bake is null;
                }
                if (s_bake is not null)
                {
                    mesh = Access.Build(renderer, file[..file.IndexOf('?')], s_bake.Mesh(clip, frame), Skin(renderer, true));
                    Counters.Add("frogs.anim_frame_made");
                }
            }
            catch (Exception e)
            {
                Log.Exception("frog.anim", e);
                s_bakeFailed = true;
            }
            if (mesh is null) s_bakeFailed = true;
            meshes[file] = mesh;
            return mesh;
        }
        int pose = PoseOf(file);
        float stretch = pose / (float)(FrogPreset.Poses - 1);
        try
        {
            if (Access.Find(renderer)(Source) is { } path)
            {
                var ogre = OgreMeshReader.ReadFile(path);
                if (global::Meitou.Game.Rendering.Frame.MeshWeld.Enabled) OgreMeshWeld.Apply(ogre);
                if (FromGorillo(ogre, stretch) > 0) mesh = Access.Build(renderer, Name + "#" + pose, ogre, Skin(renderer, true));
                Log.Line(mesh is null ? "frogs: the Gorillo would not build, the code-made frog is drawn" : $"frogs: frog pose {pose} made from {Source}");
            }
            else Log.Line($"frogs: no {Source} in this game, the code-made frog is drawn");
            if (mesh is null)
            {
                var model = Model();
                FoldedMeshes.MoveVertices(model, p => Shape(p, stretch, ModelMin, ModelMax));
                mesh = Access.Build(renderer, Name + "#" + pose, model, Skin(renderer, false));
            }
            Counters.Add("frogs.pose_made");
        }
        catch (Exception e) { Log.Exception("frog.mesh", e); }
        meshes[file] = mesh;
        return mesh;
    }

    /// <summary>The frog's texture: on the Gorillo's layout the made skin (FrogSkin: matte, its own eyes and claws), else the coat with a
    /// matte gloss; the plain coat (shiny) when this game can't take a made texture.</summary>
    static string Skin(DebrisRenderer renderer, bool gorillo)
    {
        var find = Access.Find(renderer);
        if (gorillo)
            return FroxMesh.Textures.RegisterImage(renderer, FrogSkin.Name, () =>
                find(FrogSkin.Coat) is { } c && find(FrogSkin.Gorillo) is { } g ? FrogSkin.Compose(FroxMesh.Textures.Load(c), FroxMesh.Textures.Load(g)) : null)
                ? FrogSkin.Name : Texture;
        return FroxMesh.Textures.RegisterImage(renderer, "pza.frog.coat", () =>
            find(FrogSkin.Coat) is { } c ? FrogSkin.WithGloss(FroxMesh.Textures.Load(c), FrogSkin.Gloss) : null) ? "pza.frog.coat" : Texture;
    }

    /// <summary>The code-made frog's box (its tucked pose), for Shape.</summary>
    static readonly Vector3 ModelMin = new(-0.85f, 0, -0.85f), ModelMax = new(0.85f, 0.95f, 1.1f);

    /// <summary>A pose of a body in its own box (nose at +z): tucked at 0; stretched out at 1: the back part (hind legs) pulled back and a little
    /// down, the front legs drawn back under the chest, the body flattened (a leaping frog's long line). Finite in = finite out; at 0 nothing moves.</summary>
    public static Vector3 Shape(Vector3 p, float stretch, Vector3 min, Vector3 max)
    {
        stretch = float.IsFinite(stretch) ? Math.Clamp(stretch, 0, 1) : 0;
        var size = max - min;
        if (stretch == 0 || !WildCore.Finite(p) || !WildCore.Finite(size) || !(size.Y > 0) || !(size.Z > 0)) return p;
        float zn = (p.Z - min.Z) / size.Z, yn = (p.Y - min.Y) / size.Y;
        float low = WingPreset.Smooth(1 - yn / 0.38f);
        float rear = WingPreset.Smooth((0.45f - zn) / 0.45f) * low;
        float front = WingPreset.Smooth((zn - 0.6f) / 0.3f) * WingPreset.Smooth(1 - yn / 0.45f);
        var q = p;
        q.Z -= stretch * size.Z * (0.45f * rear + 0.18f * front);
        q.Y = min.Y + (p.Y - min.Y) * (1 - 0.15f * stretch) - stretch * size.Y * 0.06f * rear;
        return q;
    }

    /// <summary>The Gorillo made a frog in place: posed in its own box, then shrunk so its tucked length is Length, Flatter in height, centred
    /// on x and z, its lowest point at y = 0 (normals turned for the squash). Returns how many vertices it moved (0 = nothing to move).</summary>
    public static int FromGorillo(OgreMesh ogre, float stretch)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        FoldedMeshes.MoveVertices(ogre, p => { if (WildCore.Finite(p)) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); } return p; });
        var size = max - min;
        if (!WildCore.Finite(size) || !(size.Z > 0) || !(size.Y > 0)) return 0;
        float k = Length / size.Z;
        var scale = new Vector3(k, k * Flatter, k);
        float floor = float.MaxValue;
        FoldedMeshes.MoveVertices(ogre, p => { if (WildCore.Finite(p)) floor = Math.Min(floor, Shape(p, stretch, min, max).Y); return p; });
        if (!float.IsFinite(floor)) floor = min.Y;
        var shift = new Vector3((min.X + max.X) / 2, floor, (min.Z + max.Z) / 2);
        int moved = FoldedMeshes.MoveVertices(ogre, p => WildCore.Finite(p) ? (Shape(p, stretch, min, max) - shift) * scale : p);
        TurnNormals(ogre, scale);
        return moved;
    }

    /// <summary>Normals under a non-uniform scale: n / scale, normalised.</summary>
    public static void TurnNormals(OgreMesh ogre, Vector3 scale)
    {
        var done = new HashSet<OgreVertexData>(ReferenceEqualityComparer.Instance);
        foreach (var data in new[] { ogre.SharedVertexData }.Concat(ogre.SubMeshes.Select(s => s.VertexData)))
        {
            if (data is null || !done.Add(data)) continue;
            if (data.Find(OgreVertexSemantic.Normal) is not { Type: OgreVertexElementType.Float3 } e) continue;
            if (!data.Buffers.TryGetValue(e.Source, out var buffer)) continue;
            var bytes = buffer.Data.AsSpan();
            for (int v = 0; v < data.VertexCount; v++)
            {
                int at = v * buffer.VertexSize + e.Offset;
                if (at + 12 > bytes.Length) break;
                var n = new Vector3(BitConverter.ToSingle(bytes[at..]), BitConverter.ToSingle(bytes[(at + 4)..]), BitConverter.ToSingle(bytes[(at + 8)..])) / scale;
                if (!WildCore.Finite(n) || n.LengthSquared() < 1e-12f) continue;
                n = Vector3.Normalize(n);
                BitConverter.TryWriteBytes(bytes[at..], n.X);
                BitConverter.TryWriteBytes(bytes[(at + 4)..], n.Y);
                BitConverter.TryWriteBytes(bytes[(at + 8)..], n.Z);
            }
        }
    }

    /// <summary>The frog as an Ogre mesh in memory: one sub-mesh, positions, normals and texture coordinates in one buffer, 32-bit indices.</summary>
    public static OgreMesh Model()
    {
        var verts = new List<(Vector3 P, Vector3 N, Vector2 T)>();
        var idx = new List<uint>();
        Ellipsoid(verts, idx, new(0, 0.42f, -0.05f), new(0.55f, 0.38f, 0.8f), 0);          // body
        Ellipsoid(verts, idx, new(0, 0.55f, 0.65f), new(0.48f, 0.3f, 0.45f), 0);           // head
        for (int side = -1; side <= 1; side += 2)
        {
            Ellipsoid(verts, idx, new(side * 0.26f, 0.82f, 0.78f), new(0.13f, 0.13f, 0.13f), 0);   // eye
            Ellipsoid(verts, idx, new(side * 0.5f, 0.28f, -0.5f), new(0.22f, 0.2f, 0.5f), side * 0.35f);   // folded hind leg
            Ellipsoid(verts, idx, new(side * 0.62f, 0.08f, -0.2f), new(0.16f, 0.06f, 0.32f), side * 0.6f);   // hind foot
            Ellipsoid(verts, idx, new(side * 0.36f, 0.18f, 0.52f), new(0.09f, 0.18f, 0.09f), 0);     // front leg
        }

        const ushort stride = 32;
        var data = new byte[verts.Count * stride];
        var span = data.AsSpan();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < verts.Count; i++)
        {
            var (p, n, t) = verts[i];
            int o = i * stride;
            foreach (var (v, k) in new[] { (p.X, 0), (p.Y, 4), (p.Z, 8), (n.X, 12), (n.Y, 16), (n.Z, 20), (t.X, 24), (t.Y, 28) })
                BitConverter.TryWriteBytes(span[(o + k)..], v);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var vd = new OgreVertexData { VertexCount = (uint)verts.Count };
        vd.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float3, OgreVertexSemantic.Position, 0, 0));
        vd.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float3, OgreVertexSemantic.Normal, 12, 0));
        vd.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float2, OgreVertexSemantic.TextureCoordinates, 24, 0));
        vd.Buffers[0] = new OgreVertexBuffer(stride, data);
        var mesh = new OgreMesh { Bounds = new OgreBounds(min, max, (max - min).Length() / 2) };
        mesh.SubMeshes.Add(new OgreSubMesh { MaterialName = "pza_frog", UseSharedVertices = false, VertexData = vd, Indices = new OgreIndexBuffer(true, [.. idx]) });
        return mesh;
    }

    /// <summary>An ellipsoid of 8 rings by 12 segments at <paramref name="c"/> with radii <paramref name="r"/>, turned <paramref name="yaw"/>
    /// about y; the back maps to the coat's green scales, the belly (normals facing down) to its lighter cloth.</summary>
    static void Ellipsoid(List<(Vector3, Vector3, Vector2)> verts, List<uint> idx, Vector3 c, Vector3 r, float yaw)
    {
        const int rings = 8, segs = 12;
        uint first = (uint)verts.Count;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        for (int i = 0; i <= rings; i++)
        {
            float lat = MathF.PI * i / rings - MathF.PI / 2;
            for (int j = 0; j <= segs; j++)
            {
                float lon = MathF.Tau * j / segs;
                var unit = new Vector3(MathF.Cos(lat) * MathF.Sin(lon), MathF.Sin(lat), MathF.Cos(lat) * MathF.Cos(lon));
                var p = c + Vector3.Transform(unit * r, turn);
                var n = Vector3.Normalize(Vector3.Transform(unit / r, turn));
                var t = n.Y < -0.25f
                    ? new Vector2(0.40f + 0.20f * j / segs, 0.88f + 0.06f * i / rings)    // belly: the coat's lighter lining
                    : new Vector2(0.32f + 0.36f * j / segs, 0.20f + 0.45f * i / rings);   // back and sides: the green scales
                verts.Add((p, n, t));
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                uint a = first + (uint)(i * (segs + 1) + j), b = a + segs + 1;
                idx.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
    }

    #endregion
}
