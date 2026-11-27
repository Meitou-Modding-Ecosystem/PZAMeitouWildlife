// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The folded-wing bird meshes (BirdFold) in the game's debris renderer, which draws the birds (docs/game/birds.md "Meitou"):
//   BirdFlocks.AddPieces (host thread, between bird steps): prefix notes where this frame's birds start in the piece list; the postfix gives
//     each landed bird the folded variant's name ("birdtype1.mesh#pzafold100?texture") and places it by that mesh's own middle (#409)
//   DebrisRenderer.Load (private, host thread): a marked name is made here once: the installed mesh file read with Meitou's own reader,
//     welded as the game welds, its wing vertices folded, then built by the renderer's own Build; it is cached under the marked name
// Anything missing in a game build (members resolved once in Access) turns the folding off: the birds land with their wings spread, as in
// 0.1.0. A failure while making a variant gives the unfolded mesh, never an invisible bird.
using System.Collections;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Objects;

namespace PZA.Wildlife.Meitou;

public static class FoldedMeshes
{
    /// <summary>The fold level per bird id for this frame's draw, filled by the bird step (locked for thread safety).</summary>
    static readonly object s_lock = new();
    static readonly Dictionary<long, int> s_levels = [];

    public static void BeginStep() { lock (s_lock) s_levels.Clear(); }
    public static void Reset() { lock (s_lock) s_levels.Clear(); }

    public static void Note(long id, int level)
    {
        if (level <= 0) return;
        lock (s_lock) s_levels[id] = level;
    }

    public static int LevelOf(long id) { lock (s_lock) return s_levels.GetValueOrDefault(id); }

    #region The pieces

    /// <summary>The postfix's step: every piece of a landed bird gets the folded mesh, placed by the folded mesh's middle.</summary>
    public static void SwapPieces(List<DebrisPiece> pieces, int start, Func<string, Vector3?> centreOf)
    {
        if (!Driver.On) return;
        lock (s_lock)
        {
            if (s_levels.Count == 0) return;
        }
        for (int i = Math.Max(0, start); i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (!BirdFold.CanFoldMesh(piece.Mesh)) continue;
            int level = LevelOf(piece.Id);
            if (level <= 0 || !BirdFold.ValidPose(level) || centreOf(piece.Mesh) is not { } centre) continue;
            string name = BirdFold.PoseName(piece.Mesh, level);
            if (centreOf(name) is not { } folded) continue;
            var origin = piece.Position - Vector3.Transform(centre * piece.Scale, piece.Rotation);
            pieces[i] = piece with { Mesh = name, Position = origin + Vector3.Transform(folded * piece.Scale, piece.Rotation) };
            Counters.Add(level == BirdFold.Full ? "draw.folded" : level > 100 ? "draw.flap" : "draw.part_folded");
        }
    }

    #endregion

    #region Making a folded mesh

    /// <summary>The Load prefix's step: the cached variant, or one made now. Null result = the unfolded mesh is drawn instead.</summary>
    public static object? Get(DebrisRenderer renderer, string marked, string plain, int level)
    {
        var meshes = Access.Meshes(renderer);
        if (meshes.Contains(marked)) return meshes[marked];

        object? mesh = null;
        try
        {
            mesh = Make(renderer, plain, level);
            Log.Line(mesh is null ? $"folded wings: {plain} not found, the bird keeps its wings spread" : $"folded wings: made {marked}");
        }
        catch (Exception e)
        {
            Log.Exception("fold", e);
        }

        mesh ??= Access.Load(renderer, plain);   // the unfolded one (Load on the plain name: the game's own path)
        meshes[marked] = mesh;
        return mesh;
    }

    /// <summary>Reads the installed mesh, welds it as the game does, folds its wings and builds it with the renderer's own Build.</summary>
    static object? Make(DebrisRenderer renderer, string plain, int level)
    {
        string file = plain;
        string? diffuse = null;
        int mark = plain.IndexOf('?');
        if (mark >= 0) (file, diffuse) = (plain[..mark], plain[(mark + 1)..]);
        if (Access.Find(renderer)(Path.GetFileName(file.Replace('\\', '/'))) is not { } path) return null;

        var ogre = OgreMeshReader.ReadFile(path);
        Weld(ogre);
        int moved = PoseVertices(ogre, level);
        Counters.Add(level > 100 ? "flap.made" : "fold.made");
        if (moved == 0) return null;
        return Access.Build(renderer, file, ogre, diffuse);
    }

    /// <summary>The game's own weld when it is on (its own method: a build without it leaves the mesh as read).</summary>
    static void Weld(OgreMesh ogre)
    {
        if (global::Meitou.Game.Rendering.Frame.MeshWeld.Enabled) OgreMeshWeld.Apply(ogre);
    }

    /// <summary>Folds every Float3 position of the mesh's vertex data in place; returns how many vertices moved.</summary>
    public static int FoldVertices(OgreMesh ogre, float amount) => MoveVertices(ogre, p => BirdFold.Fold(p, amount));

    /// <summary>Puts the mesh in a wing pose (fold level or flap frame) in place; returns how many vertices moved.</summary>
    public static int PoseVertices(OgreMesh ogre, int pose) => MoveVertices(ogre, p => BirdFold.Pose(p, pose));

    /// <summary>Moves every Float3 position of the mesh's vertex data in place; returns how many vertices moved.</summary>
    public static int MoveVertices(OgreMesh ogre, Func<Vector3, Vector3> move)
    {
        var done = new HashSet<OgreVertexData>(ReferenceEqualityComparer.Instance);
        int moved = 0;
        foreach (var data in new[] { ogre.SharedVertexData }.Concat(ogre.SubMeshes.Select(s => s.VertexData)))
        {
            if (data is null || !done.Add(data)) continue;
            if (data.Find(OgreVertexSemantic.Position) is not { Type: OgreVertexElementType.Float3 } e) continue;
            if (!data.Buffers.TryGetValue(e.Source, out var buffer)) continue;
            var bytes = buffer.Data.AsSpan();
            for (int v = 0; v < data.VertexCount; v++)
            {
                int at = v * buffer.VertexSize + e.Offset;
                if (at + 12 > bytes.Length) break;
                var p = new Vector3(BitConverter.ToSingle(bytes[at..]), BitConverter.ToSingle(bytes[(at + 4)..]), BitConverter.ToSingle(bytes[(at + 8)..]));
                var f = move(p);
                if (f == p) continue;
                BitConverter.TryWriteBytes(bytes[at..], f.X);
                BitConverter.TryWriteBytes(bytes[(at + 4)..], f.Y);
                BitConverter.TryWriteBytes(bytes[(at + 8)..], f.Z);
                moved++;
            }
        }
        return moved;
    }

    #endregion
}
