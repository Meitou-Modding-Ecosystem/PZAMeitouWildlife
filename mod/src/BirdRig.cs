// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Animated birds (0.11.0, user: "I want to keep the game's original bird, on that animation rig"). The GAME's own bird (birdtype1.mesh,
// read from the install each run, never copied) moved by Mesh2Motion's bird skeleton and clips (CC0 1.0, https://mesh2motion.org),
// baked by mod/tools/birds/bake_bird_rig.py into assets/birdrig/birdrig.pzab: per clip and frame, a 3x4 skin matrix for each of 21 bones
// (body: tail to head; each wing: root to tip), fitted to the game bird by a uniform scale (wingtips at +-26 units) so every bone stays rigid.
// The weights are made here from the installed mesh: a wing vertex (|x| past the shoulder) follows its wing's bones by span, a body vertex
// the body's by z, blended over the shoulder; each vertex takes the two neighbouring bones of its chain. Each (clip, frame) is one debris
// mesh ("birdtype1.mesh#pzarig1.4?texture"), the game's own texture kept. A game or bake that lacks anything: the folded-wing poses.
using System.IO.Compression;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Objects;

namespace PZA.Wildlife.Meitou;

/// <summary>The baked rig (pure data) and the skinning (pure maths), for the drawing and the check.</summary>
public sealed class BirdRig
{
    public const string Magic = "PZABIRD1", Marker = "#pzarig";
    /// <summary>A wing vertex is past the shoulder (|x| over Shoulder) and fully on its wing past Shoulder + Blend.</summary>
    public const float Shoulder = 1.0f, Blend = 2.5f;

    public readonly record struct Bone(int Side, float Key, Vector3 Bind);

    public Bone[] Bones = [];
    public float[] Seconds = [];
    /// <summary>Per clip, per frame, per bone: the skin matrix (System.Numerics row-vector form: p' = Transform(p, M)).</summary>
    public Matrix4x4[][][] Skin = [];

    public static BirdRig Read(byte[] file)
    {
        if (file.Length < 8 || System.Text.Encoding.ASCII.GetString(file, 0, 8) != Magic) throw new InvalidDataException("not a bird rig");
        using var inflate = new DeflateStream(new MemoryStream(file, 8, file.Length - 8), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        inflate.CopyTo(buffer);
        if (buffer.Length > 16 << 20) throw new InvalidDataException("rig too large");
        var b = buffer.ToArray();
        int o = 0;
        void Need(long n) { if (n < 0 || o + n > b.Length) throw new InvalidDataException("rig cut short"); }
        int Int() { Need(4); int v = BitConverter.ToInt32(b, o); o += 4; return v; }
        float Float() { Need(4); float v = BitConverter.ToSingle(b, o); o += 4; return v; }
        var r = new BirdRig();
        int bones = Int();
        if (bones is <= 0 or > 128) throw new InvalidDataException("bad bone count");
        r.Bones = new Bone[bones];
        for (int i = 0; i < bones; i++)
        {
            Need(1);
            int side = b[o++];
            float key = Float(), x = Float(), y = Float(), z = Float();
            if (side > 2 || !float.IsFinite(key) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw new InvalidDataException("bad bone");
            r.Bones[i] = new Bone(side, key, new Vector3(x, y, z));
        }
        if (!Enumerable.Range(0, 3).All(s => r.Bones.Any(bn => bn.Side == s))) throw new InvalidDataException("want a body and two wings");
        int clips = Int();
        if (clips is < 6 or > 16) throw new InvalidDataException("want glide, flap, land, take-off, hop, idle");
        r.Seconds = new float[clips];
        r.Skin = new Matrix4x4[clips][][];
        for (int c = 0; c < clips; c++)
        {
            Need(1);
            int len = b[o++];
            Need(len);
            o += len;
            r.Seconds[c] = Float();
            int frames = Int();
            if (!float.IsFinite(r.Seconds[c]) || r.Seconds[c] <= 0 || r.Seconds[c] > 60 || frames is <= 0 or > 240) throw new InvalidDataException("bad clip");
            Need((long)frames * bones * 48);
            r.Skin[c] = new Matrix4x4[frames][];
            for (int f = 0; f < frames; f++)
            {
                r.Skin[c][f] = new Matrix4x4[bones];
                for (int i = 0; i < bones; i++)
                {
                    var m = new float[12];
                    for (int k = 0; k < 12; k++) m[k] = Float();
                    if (!m.All(float.IsFinite)) throw new InvalidDataException("non-finite skin");
                    // rows [R | t] (column-vector form) -> System.Numerics row-vector form
                    r.Skin[c][f][i] = new Matrix4x4(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1);
                }
            }
        }
        return r;
    }

    /// <summary>A vertex's bones and weights (two per chain, at most four in all; they sum to 1).</summary>
    public (int Bone, float Weight)[] Weights(Vector3 p)
    {
        var body = Chain(0, p.Z);
        float ax = MathF.Abs(p.X);
        if (!float.IsFinite(ax) || ax <= Shoulder) return body;
        var wing = Chain(p.X > 0 ? 1 : 2, ax);
        float k = WingPreset.Smooth((ax - Shoulder) / Blend);
        (int Bone, float Weight)[] all = [.. body.Select(w => (w.Bone, w.Weight * (1 - k))), .. wing.Select(w => (w.Bone, w.Weight * k))];
        return [.. all.Where(w => w.Weight > 0)];
    }

    (int Bone, float Weight)[] Chain(int side, float value)
    {
        var chain = Enumerable.Range(0, Bones.Length).Where(i => Bones[i].Side == side).OrderBy(i => Bones[i].Key).ToArray();
        if (!float.IsFinite(value) || value <= Bones[chain[0]].Key) return [(chain[0], 1)];
        if (value >= Bones[chain[^1]].Key) return [(chain[^1], 1)];
        for (int j = 0; j + 1 < chain.Length; j++)
        {
            float a = Bones[chain[j]].Key, b = Bones[chain[j + 1]].Key;
            if (value <= b)
            {
                float f = b > a ? (value - a) / (b - a) : 0;
                return [(chain[j], 1 - f), (chain[j + 1], f)];
            }
        }
        return [(chain[^1], 1)];
    }

    /// <summary>Skins a mesh's positions and normals in place to clip / frame (the weights from each vertex's own bind position).</summary>
    public int Pose(OgreMesh ogre, int clip, int frame)
    {
        clip = Math.Clamp(clip, 0, Skin.Length - 1);
        var mats = Skin[clip][Math.Clamp(frame, 0, Skin[clip].Length - 1)];
        int moved = 0;
        var done = new HashSet<OgreVertexData>(ReferenceEqualityComparer.Instance);
        foreach (var data in new[] { ogre.SharedVertexData }.Concat(ogre.SubMeshes.Select(s => s.VertexData)))
        {
            if (data is null || !done.Add(data)) continue;
            if (data.Find(OgreVertexSemantic.Position) is not { Type: OgreVertexElementType.Float3 } pe || !data.Buffers.TryGetValue(pe.Source, out var pb)) continue;
            var ne = data.Find(OgreVertexSemantic.Normal) is { Type: OgreVertexElementType.Float3 } n0 && data.Buffers.TryGetValue(n0.Source, out var nb0) ? (n0, nb0) : ((OgreVertexElement, OgreVertexBuffer)?)null;
            for (int v = 0; v < data.VertexCount; v++)
            {
                int at = v * pb.VertexSize + pe.Offset;
                if (at + 12 > pb.Data.Length) break;
                var p = new Vector3(BitConverter.ToSingle(pb.Data, at), BitConverter.ToSingle(pb.Data, at + 4), BitConverter.ToSingle(pb.Data, at + 8));
                if (!WildCore.Finite(p)) continue;
                var w = Weights(p);
                var q = Vector3.Zero;
                foreach (var (bone, weight) in w) q += Vector3.Transform(p, mats[bone]) * weight;
                Write(pb.Data, at, q);
                if (ne is { } nn)
                {
                    int an = v * nn.Item2.VertexSize + nn.Item1.Offset;
                    if (an + 12 <= nn.Item2.Data.Length)
                    {
                        var n = new Vector3(BitConverter.ToSingle(nn.Item2.Data, an), BitConverter.ToSingle(nn.Item2.Data, an + 4), BitConverter.ToSingle(nn.Item2.Data, an + 8));
                        var r = Vector3.Zero;
                        foreach (var (bone, weight) in w) r += Vector3.TransformNormal(n, mats[bone]) * weight;
                        if (WildCore.Finite(r) && r.LengthSquared() > 1e-12f) Write(nn.Item2.Data, an, Vector3.Normalize(r));
                    }
                }
                moved++;
            }
        }
        return moved;
    }

    static void Write(byte[] d, int at, Vector3 v)
    {
        BitConverter.TryWriteBytes(d.AsSpan(at), v.X);
        BitConverter.TryWriteBytes(d.AsSpan(at + 4), v.Y);
        BitConverter.TryWriteBytes(d.AsSpan(at + 8), v.Z);
    }

    #region The drawing

    static BirdRig? s_rig;
    static bool s_tried;
    public static string Folder = "";

    public static BirdRig? Loaded
    {
        get
        {
            if (s_tried) return s_rig;
            s_tried = true;
            string path = Path.Combine(Folder, "assets", "birdrig", "birdrig.pzab");
            try
            {
                s_rig = Read(File.ReadAllBytes(path));
                Log.Line($"animated birds: rig read ({s_rig.Bones.Length} bones, {string.Join("/", s_rig.Skin.Select(c => c.Length))} frames); skeleton and clips by Mesh2Motion (CC0)");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Line("animated birds OFF: " + path + ": " + e.Message);
            }
            return s_rig;
        }
    }

    /// <summary>True when the birds are drawn on the rig: the switch is on and the bake is there.</summary>
    public static bool On => Driver.Settings.AnimatedBirds && Loaded is not null;

    /// <summary>The game mesh's name with the rig marker before the texture mark.</summary>
    public static string Name(string mesh, RigFrame f, BirdRig rig)
    {
        int clip = Math.Clamp((int)f.Clip, 0, rig.Skin.Length - 1);
        string tag = $"{Marker}{clip}.{f.Index(rig.Skin[clip].Length)}";
        int mark = mesh.IndexOf('?');
        return mark < 0 ? mesh + tag : mesh[..mark] + tag + mesh[mark..];
    }

    public static bool Parse(string name, out string plain, out int clip, out int frame)
    {
        plain = name;
        clip = frame = 0;
        int at = name.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return false;
        int dot = name.IndexOf('.', at), end = name.IndexOf('?', at);
        if (end < 0) end = name.Length;
        if (dot < 0 || dot > end) return false;
        if (!int.TryParse(name.AsSpan(at + Marker.Length, dot - at - Marker.Length), out clip) || !int.TryParse(name.AsSpan(dot + 1, end - dot - 1), out frame)) return false;
        if (clip < 0 || frame < 0) return false;
        plain = name[..at] + name[end..];
        return true;
    }

    /// <summary>The Load prefix's step: the installed bird read, welded as the game welds, skinned to (clip, frame), built.</summary>
    public static object? Get(DebrisRenderer renderer, string marked)
    {
        var meshes = Access.Meshes(renderer);
        if (meshes.Contains(marked)) return meshes[marked];
        object? mesh = null;
        try
        {
            if (Loaded is { } rig && Parse(marked, out var plain, out int clip, out int frame) && clip < rig.Skin.Length && frame < rig.Skin[clip].Length)
            {
                string file = plain, diffuse = "";
                int q = plain.IndexOf('?');
                if (q >= 0) (file, diffuse) = (plain[..q], plain[(q + 1)..]);
                if (Access.Find(renderer)(Path.GetFileName(file.Replace('\\', '/'))) is { } path)
                {
                    var ogre = OgreMeshReader.ReadFile(path);
                    if (global::Meitou.Game.Rendering.Frame.MeshWeld.Enabled) OgreMeshWeld.Apply(ogre);
                    if (rig.Pose(ogre, clip, frame) > 0) mesh = Access.Build(renderer, file, ogre, diffuse.Length > 0 ? diffuse : null);
                    Counters.Add("rig.frame_made");
                }
            }
        }
        catch (Exception e) { Log.Exception("rig.mesh", e); }
        mesh ??= Access.Load(renderer, marked[..marked.IndexOf(Marker, StringComparison.Ordinal)] + (marked.IndexOf('?') is var qq && qq >= 0 ? marked[qq..] : ""));
        meshes[marked] = mesh;
        return mesh;
    }

    #endregion
}
