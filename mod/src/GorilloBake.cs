// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The frog's animations (0.8.0, user 2026-11-27: "give it the gorillo's animations"): the Gorillo's own idle, walk and run, read from the
// install's frog_gorilla.skeleton each run (never copied) and skinned on the CPU into a few static frames, each shrunk to frog size like the
// 0.7.0 frog (FrogMesh.Length long when at rest, Flatter in height, centred, its lowest point at y = 0). The frames are debris meshes
// like the birds' poses. Ogre's keyframes are relative to the bind pose: local = scale x (bind orientation x key rotation) x (bind position
// + key translation), world = local x parent's world (row vectors, System.Numerics); skin = inverse bind world x posed world.
using System.Numerics;
using Meitou.Data.Ogre;

namespace PZA.Wildlife.Meitou;

public sealed class GorilloBake
{
    public const string Skeleton = "frog_gorilla.skeleton";
    /// <summary>The clips (0 idle, 1 walk, 2 run): the Gorillo's animation and the frames baked from one cycle of it.</summary>
    public static readonly (string Anim, int Frames)[] Clips = [("idle1", 10), ("walk", 8), ("run", 10)];

    readonly string _meshPath;
    readonly OgreSkeleton _skeleton;
    readonly Dictionary<ushort, OgreBone> _bones;
    readonly Dictionary<ushort, Matrix4x4> _inverseBind = [];
    readonly OgreAnimation[] _anims;
    readonly Vector3 _bindMin, _bindMax;

    GorilloBake(string meshPath, OgreSkeleton skeleton, OgreAnimation[] anims)
    {
        _meshPath = meshPath;
        _skeleton = skeleton;
        _anims = anims;
        _bones = skeleton.Bones.ToDictionary(b => b.Handle);
        foreach (var (handle, world) in World(_ => (Vector3.Zero, Quaternion.Identity, Vector3.One)))
            _inverseBind[handle] = Matrix4x4.Invert(world, out var inv) ? inv : Matrix4x4.Identity;
        var rest = OgreMeshReader.ReadFile(meshPath);
        (_bindMin, _bindMax) = Bounds(rest);
    }

    /// <summary>The bake, or null (with the reason) when the install lacks the mesh, the skeleton or one of the clips.</summary>
    public static GorilloBake? Create(Func<string, string?> find, out string why)
    {
        why = "";
        if (find(FrogMesh.Source) is not { } meshPath) { why = "no " + FrogMesh.Source; return null; }
        if (find(Skeleton) is not { } skeletonPath) { why = "no " + Skeleton; return null; }
        var skeleton = OgreSkeletonReader.Read(File.ReadAllBytes(skeletonPath));
        var anims = new OgreAnimation[Clips.Length];
        for (int c = 0; c < Clips.Length; c++)
        {
            var a = skeleton.Animations.FirstOrDefault(x => x.Name == Clips[c].Anim);
            if (a is null || !(a.Length > 0)) { why = "no '" + Clips[c].Anim + "' in " + Skeleton; return null; }
            anims[c] = a;
        }
        return new GorilloBake(meshPath, skeleton, anims);
    }

    public float Seconds(int clip) => _anims[Math.Clamp(clip, 0, _anims.Length - 1)].Length;

    /// <summary>One frame, built from a fresh read of the install's mesh: skinned, welded as the game welds, shrunk to frog size.</summary>
    public OgreMesh Mesh(int clip, int frame)
    {
        clip = Math.Clamp(clip, 0, Clips.Length - 1);
        frame = Math.Clamp(frame, 0, Clips[clip].Frames - 1);
        var anim = _anims[clip];
        float t = anim.Length * frame / Clips[clip].Frames;
        var tracks = anim.Tracks.GroupBy(tr => tr.Bone).ToDictionary(g => g.Key, g => g.First());
        var posed = World(b => tracks.TryGetValue(b.Handle, out var tr) ? Sample(tr, t) : (Vector3.Zero, Quaternion.Identity, Vector3.One));
        var skin = new Dictionary<ushort, Matrix4x4>();
        foreach (var (h, w) in posed) skin[h] = _inverseBind[h] * w;

        var ogre = OgreMeshReader.ReadFile(_meshPath);
        if (ogre.SharedVertexData is { } shared) Skin(shared, ogre.BoneAssignments, skin);
        foreach (var sm in ogre.SubMeshes)
            if (!sm.UseSharedVertices && sm.VertexData is { } vd) Skin(vd, sm.BoneAssignments, skin);
        if (global::Meitou.Game.Rendering.Frame.MeshWeld.Enabled) OgreMeshWeld.Apply(ogre);
        Shrink(ogre);
        return ogre;
    }

    /// <summary>Frog size: FrogMesh.Length long at rest, Flatter in height, centred on the rest pose's x / z, this frame's lowest point at y = 0.</summary>
    void Shrink(OgreMesh ogre)
    {
        var size = _bindMax - _bindMin;
        float k = size.Z > 0 ? FrogMesh.Length / size.Z : 1;
        var scale = new Vector3(k, k * FrogMesh.Flatter, k);
        float floor = float.MaxValue;
        FoldedMeshes.MoveVertices(ogre, p => { if (WildCore.Finite(p)) floor = Math.Min(floor, p.Y); return p; });
        if (!float.IsFinite(floor)) floor = _bindMin.Y;
        var shift = new Vector3((_bindMin.X + _bindMax.X) / 2, floor, (_bindMin.Z + _bindMax.Z) / 2);
        FoldedMeshes.MoveVertices(ogre, p => WildCore.Finite(p) ? (p - shift) * scale : p);
        FrogMesh.TurnNormals(ogre, scale);
    }

    static (Vector3, Vector3) Bounds(OgreMesh ogre)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        FoldedMeshes.MoveVertices(ogre, p => { if (WildCore.Finite(p)) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); } return p; });
        return (min, max);
    }

    Dictionary<ushort, Matrix4x4> World(Func<OgreBone, (Vector3 T, Quaternion R, Vector3 S)> delta)
    {
        var world = new Dictionary<ushort, Matrix4x4>();
        Matrix4x4 Get(OgreBone b, int depth)
        {
            if (world.TryGetValue(b.Handle, out var m)) return m;
            var (dt, dr, ds) = delta(b);
            var local = Matrix4x4.CreateScale(b.Scale * ds) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(b.Orientation * dr))
                        * Matrix4x4.CreateTranslation(b.Position + dt);
            m = depth < 256 && b.Parent is { } p && _bones.TryGetValue(p, out var parent) ? local * Get(parent, depth + 1) : local;
            world[b.Handle] = m;
            return m;
        }
        foreach (var b in _skeleton.Bones) Get(b, 0);
        return world;
    }

    /// <summary>A track at time t: linear between keys (slerp for the rotation), held before the first and after the last.</summary>
    public static (Vector3, Quaternion, Vector3) Sample(OgreAnimationTrack track, float t)
    {
        var k = track.KeyFrames;
        if (k.Count == 0) return (Vector3.Zero, Quaternion.Identity, Vector3.One);
        int j = k.FindIndex(x => x.Time > t);
        if (j <= 0)
        {
            var x = j == 0 ? k[0] : k[^1];
            return (x.Translation, x.Rotation, x.HasScale ? x.Scale : Vector3.One);
        }
        var a = k[j - 1];
        var b = k[j];
        float u = b.Time > a.Time ? Math.Clamp((t - a.Time) / (b.Time - a.Time), 0, 1) : 0;
        return (Vector3.Lerp(a.Translation, b.Translation, u), Quaternion.Slerp(a.Rotation, b.Rotation, u),
                Vector3.Lerp(a.HasScale ? a.Scale : Vector3.One, b.HasScale ? b.Scale : Vector3.One, u));
    }

    /// <summary>Linear-blend skinning of one vertex data's positions and normals in place.</summary>
    static void Skin(OgreVertexData vd, IList<OgreBoneAssignment> assignments, Dictionary<ushort, Matrix4x4> skin)
    {
        var byVertex = new List<(ushort Bone, float Weight)>[vd.VertexCount];
        foreach (var a in assignments)
            if (a.VertexIndex < vd.VertexCount && a.Weight > 0 && skin.ContainsKey(a.BoneIndex))
                (byVertex[a.VertexIndex] ??= []).Add((a.BoneIndex, a.Weight));
        foreach (var (semantic, isNormal) in new[] { (OgreVertexSemantic.Position, false), (OgreVertexSemantic.Normal, true) })
        {
            if (vd.Find(semantic) is not { Type: OgreVertexElementType.Float3 } e || !vd.Buffers.TryGetValue(e.Source, out var buffer)) continue;
            var bytes = buffer.Data.AsSpan();
            for (int v = 0; v < vd.VertexCount; v++)
            {
                int at = v * buffer.VertexSize + e.Offset;
                if (at + 12 > bytes.Length || byVertex[v] is not { } list) continue;
                var p = new Vector3(BitConverter.ToSingle(bytes[at..]), BitConverter.ToSingle(bytes[(at + 4)..]), BitConverter.ToSingle(bytes[(at + 8)..]));
                var sum = Vector3.Zero;
                float total = 0;
                foreach (var (bone, w) in list)
                {
                    sum += (isNormal ? Vector3.TransformNormal(p, skin[bone]) : Vector3.Transform(p, skin[bone])) * w;
                    total += w;
                }
                if (!(total > 0)) continue;
                var q = isNormal ? Vector3.Normalize(sum) : sum / total;
                if (!WildCore.Finite(q)) continue;
                BitConverter.TryWriteBytes(bytes[at..], q.X);
                BitConverter.TryWriteBytes(bytes[(at + 4)..], q.Y);
                BitConverter.TryWriteBytes(bytes[(at + 8)..], q.Z);
            }
        }
    }
}
