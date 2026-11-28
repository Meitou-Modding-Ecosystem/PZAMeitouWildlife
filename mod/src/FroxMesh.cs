// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The froxlotl's drawing. Model, rig and colour textures by TheIvoryDisaster (made for PZA's mods); the gait and the bake are PZA's
// (mod/tools/froxlotl/bake_froxlotl.py). The bake ships as assets/froxlotl/froxlotl.pzaf: one mesh's texture coordinates and triangles and
// every frame of its rest, walk and run (half-float positions, byte normals), deflated. Each (colour, clip, frame) is one debris mesh, built
// when first asked for by the DebrisRenderer.Load prefix ("pza.frox.mesh#1.7?pza.frox.mango.png"), its texture the PNG beside the bake,
// put in the renderer's texture table under that name before the build (the renderer reads PNGs itself; we only hand it the path). A game
// without any of that draws the frox with the lizard coat instead (logged once). Display only.
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Objects;

namespace PZA.Wildlife.Meitou;

/// <summary>The baked froxlotl (pure data, read once).</summary>
public sealed class FroxAsset
{
    public const string Magic = "PZAFROX1";
    public int Vertices;
    public Vector2[] Uv = [];
    public uint[] Indices = [];
    public float Length;
    public FroxClips Clips = new();
    /// <summary>Per clip (0 rest, 1 walk, 2 run): frames of positions and normals.</summary>
    public (Vector3[] P, Vector3[] N)[][] Frames = [];
    /// <summary>Every clip's length (s), in file order.</summary>
    public float[] Seconds = [];

    /// <summary>Reads a .pzaf; throws InvalidDataException on anything malformed (sizes are checked before every read).</summary>
    public static FroxAsset Read(byte[] file)
    {
        if (file.Length < 8 || System.Text.Encoding.ASCII.GetString(file, 0, 8) != Magic) throw new InvalidDataException("not a froxlotl bake");
        using var inflate = new DeflateStream(new MemoryStream(file, 8, file.Length - 8), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        inflate.CopyTo(buffer);
        if (buffer.Length > 64 << 20) throw new InvalidDataException("bake too large");
        var b = buffer.ToArray();
        int o = 0;
        int Int() { Need(4); int v = BitConverter.ToInt32(b, o); o += 4; return v; }
        float Float() { Need(4); float v = BitConverter.ToSingle(b, o); o += 4; return v; }
        void Need(long n) { if (n < 0 || o + n > b.Length) throw new InvalidDataException("bake cut short"); }
        var a = new FroxAsset();
        a.Vertices = Int();
        int indices = Int();
        a.Length = Float();
        float restSpeed = Float(), walkSpeed = Float(), runSpeed = Float();
        if (a.Vertices is <= 0 or > 65536 || indices <= 0 || indices % 3 != 0 || indices > 600000) throw new InvalidDataException("bad sizes");
        if (!float.IsFinite(a.Length) || !float.IsFinite(walkSpeed) || !float.IsFinite(runSpeed) || walkSpeed < 0 || runSpeed < 0 || walkSpeed > 100 || runSpeed > 100)
            throw new InvalidDataException("bad length or speeds");
        Need((long)a.Vertices * 8);
        a.Uv = new Vector2[a.Vertices];
        for (int i = 0; i < a.Vertices; i++) a.Uv[i] = new(Float(), Float());
        Need((long)indices * 4);
        a.Indices = new uint[indices];
        for (int i = 0; i < indices; i++)
        {
            a.Indices[i] = BitConverter.ToUInt32(b, o);
            o += 4;
            if (a.Indices[i] >= a.Vertices) throw new InvalidDataException("index past the vertices");
        }
        int clips = Int();
        if (clips is < 3 or > 8) throw new InvalidDataException("want 3 to 8 clips");
        a.Frames = new (Vector3[], Vector3[])[Math.Max(4, clips)][];
        var seconds = new float[Math.Max(4, clips)];
        var counts = new int[Math.Max(4, clips)];
        for (int c = 0; c < clips; c++)
        {
            Need(1);
            int len = b[o++];
            Need(len);
            o += len;
            seconds[c] = Float();
            int frames = Int();
            if (!float.IsFinite(seconds[c]) || seconds[c] <= 0 || seconds[c] > 60 || frames is <= 0 or > 240) throw new InvalidDataException("bad clip");
            counts[c] = frames;
            Need((long)frames * a.Vertices * 9);
            a.Frames[c] = new (Vector3[], Vector3[])[frames];
            for (int f = 0; f < frames; f++)
            {
                var p = new Vector3[a.Vertices];
                var n = new Vector3[a.Vertices];
                for (int v = 0; v < a.Vertices; v++)
                {
                    p[v] = new((float)BitConverter.ToHalf(b, o), (float)BitConverter.ToHalf(b, o + 2), (float)BitConverter.ToHalf(b, o + 4));
                    n[v] = new((sbyte)b[o + 6] / 127f, (sbyte)b[o + 7] / 127f, (sbyte)b[o + 8] / 127f);
                    o += 9;
                    if (!WildCore.Finite(p[v])) throw new InvalidDataException("non-finite vertex");
                }
                a.Frames[c][f] = (p, n);
            }
        }
        if (clips == 3)    // an older bake without the swim: it swims with the run
            (a.Frames[3], seconds[3], counts[3]) = (a.Frames[2], seconds[2], counts[2]);
        a.Clips = new FroxClips(seconds[0], counts[0], seconds[1], counts[1], walkSpeed, seconds[2], counts[2], runSpeed, seconds[3], counts[3]);
        a.Seconds = seconds;
        return a;
    }

    /// <summary>One frame as an Ogre mesh in memory (positions, normals, texture coordinates; 32-bit indices).</summary>
    public OgreMesh Mesh(int clip, int frame)
    {
        clip = Math.Clamp(clip, 0, Frames.Length - 1);
        var (p, n) = Frames[clip][Math.Clamp(frame, 0, Frames[clip].Length - 1)];
        const ushort stride = 32;
        var data = new byte[Vertices * stride];
        var span = data.AsSpan();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < Vertices; i++)
        {
            int o = i * stride;
            foreach (var (v, k) in new[] { (p[i].X, 0), (p[i].Y, 4), (p[i].Z, 8), (n[i].X, 12), (n[i].Y, 16), (n[i].Z, 20), (Uv[i].X, 24), (Uv[i].Y, 28) })
                BitConverter.TryWriteBytes(span[(o + k)..], v);
            min = Vector3.Min(min, p[i]);
            max = Vector3.Max(max, p[i]);
        }
        var vd = new OgreVertexData { VertexCount = (uint)Vertices };
        vd.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float3, OgreVertexSemantic.Position, 0, 0));
        vd.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float3, OgreVertexSemantic.Normal, 12, 0));
        vd.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float2, OgreVertexSemantic.TextureCoordinates, 24, 0));
        vd.Buffers[0] = new OgreVertexBuffer(stride, data);
        var mesh = new OgreMesh { Bounds = new OgreBounds(min, max, (max - min).Length() / 2) };
        mesh.SubMeshes.Add(new OgreSubMesh { MaterialName = "pza_frox", UseSharedVertices = false, VertexData = vd, Indices = new OgreIndexBuffer(true, [.. Indices]) });
        return mesh;
    }
}

public static class FroxMesh
{
    public const string Name = "pza.frox.mesh";
    public const string Fallback = "coat02-lizard.dds";
    static FroxAsset? s_asset;
    static bool s_loadTried;
    static long s_lastStep;
    public static string Folder = "";

    public static bool Is(string? file) => file is not null && file.StartsWith(Name, StringComparison.OrdinalIgnoreCase);

    public static string TextureName(int colour) => "pza.frox." + FroxPreset.Colours[Math.Clamp(colour, 0, FroxPreset.Colours.Length - 1)] + ".png";

    /// <summary>"pza.frox.mesh#clip.frame?pza.frox.colour.png".</summary>
    public static string PieceName(int colour, int clip, int frame) => $"{Name}#{clip}.{frame}?{TextureName(colour)}";

    /// <summary>Clip, frame and colour from a piece name; false for anything else.</summary>
    public static bool Parse(string file, out int clip, out int frame, out int colour)
    {
        clip = frame = colour = 0;
        int hash = file.IndexOf('#'), dot = file.IndexOf('.', hash + 1), q = file.IndexOf('?');
        if (!Is(file) || hash < 0 || dot < 0 || q < dot) return false;
        if (!int.TryParse(file.AsSpan(hash + 1, dot - hash - 1), out clip) || !int.TryParse(file.AsSpan(dot + 1, q - dot - 1), out frame)) return false;
        string tex = file[(q + 1)..];
        colour = Array.FindIndex(FroxPreset.Colours, c => TextureName(Array.IndexOf(FroxPreset.Colours, c)) == tex);
        return clip is >= 0 and <= 3 && frame >= 0 && colour >= 0;
    }

    public static FroxAsset? Asset
    {
        get
        {
            if (s_loadTried) return s_asset;
            s_loadTried = true;
            string path = Path.Combine(Folder, "assets", "froxlotl", "froxlotl.pzaf");
            try
            {
                s_asset = FroxAsset.Read(File.ReadAllBytes(path));
                Driver.Frox.Clips = s_asset.Clips;
                Log.Line($"froxlotls: bake read ({s_asset.Vertices} vertices, rest/walk/run {s_asset.Frames[0].Length}/{s_asset.Frames[1].Length}/{s_asset.Frames[2].Length} frames, "
                         + $"run {s_asset.Clips.RunSpeed:0.00} units/s); model and colours by TheIvoryDisaster");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Line("froxlotls OFF: " + path + ": " + e.Message);
            }
            return s_asset;
        }
    }

    /// <summary>Host thread, once a frame (AddPieces postfix): steps them, plays their splashes, adds one piece each.</summary>
    public static void AddPieces(List<DebrisPiece> pieces, Func<string, Vector3?> centreOf)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = s_lastStep == 0 ? 0 : (float)System.Diagnostics.Stopwatch.GetElapsedTime(s_lastStep, now).TotalSeconds;
        s_lastStep = now;
        if (Asset is null) return;
        Driver.StepFrox(dt);
        BiteEffects.PlaySplashes(Driver.Frox.TakeSplashes());
        foreach (var f in Driver.Frox.Snapshot())
        {
            string name = PieceName(f.Colour, f.Clip, f.Frame);
            if (centreOf(name) is not { } centre) continue;
            var rotation = Quaternion.CreateFromYawPitchRoll(f.Yaw, 0, 0);
            pieces.Add(new DebrisPiece(name, f.Scale, rotation, f.Position + Vector3.Transform(centre * f.Scale, rotation), Id: f.Id));
        }
        Counters.Add("frox.drawn");
    }

    /// <summary>The Load prefix's step: the cached frame, or one built now with its colour (or the lizard coat when the colour can't be loaded).</summary>
    public static object? Get(DebrisRenderer renderer, string file)
    {
        var meshes = Access.Meshes(renderer);
        if (meshes.Contains(file)) return meshes[file];
        object? mesh = null;
        try
        {
            if (Asset is { } asset && Parse(file, out int clip, out int frame, out int colour))
            {
                string tex = TextureName(colour);
                string diffuse = Textures.Register(renderer, tex, Path.Combine(Folder, "assets", "froxlotl", "textures", FroxPreset.Colours[colour] + ".png")) ? tex : Fallback;
                mesh = Access.Build(renderer, file[..file.IndexOf('?')], asset.Mesh(clip, frame), diffuse);
                Counters.Add("frox.frame_made");
            }
        }
        catch (Exception e) { Log.Exception("frox.mesh", e); }
        meshes[file] = mesh;
        return mesh;
    }

    /// <summary>Puts a PNG of ours in the debris renderer's texture table (its own PNG path: TextureLoader, SampledImage.Rgba8, DebrisTexture),
    /// found by name once; false (logged once) when this game lacks a member.</summary>
    public static class Textures
    {
        static bool s_resolved, s_warned;
        static FieldInfo? s_table, s_gpu;
        static ConstructorInfo? s_debrisTexture;
        static MethodInfo? s_load, s_rgba8;

        static void Resolve(Type renderer)
        {
            s_resolved = true;
            s_table = AccessTools.Field(renderer, "_textures");
            s_gpu = AccessTools.Field(renderer, "_gpu");
            var nested = AccessTools.Inner(renderer, "DebrisTexture");
            var sampled = AccessTools.TypeByName("Meitou.Game.Rendering.Sky.SampledImage") ?? AccessTools.TypeByName("SampledImage");
            s_debrisTexture = nested?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters() is { Length: 2 } ps && sampled is not null && ps[1].ParameterType == sampled);
            s_load = AccessTools.TypeByName("Meitou.Data.Textures.TextureLoader")?.GetMethod("LoadFile", BindingFlags.Public | BindingFlags.Static);
            s_rgba8 = sampled?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Rgba8" && m.GetParameters() is { Length: 5 } ps && ps[2].ParameterType == typeof(bool));
        }

        /// <summary>The members this game lacks for our colours (empty: all there). For the offline check.</summary>
        public static List<string> Missing(Type renderer)
        {
            Resolve(renderer);
            var lacks = new List<string>();
            if (s_table is null) lacks.Add("DebrisRenderer._textures");
            if (s_gpu is null) lacks.Add("DebrisRenderer._gpu");
            if (s_debrisTexture is null) lacks.Add("DebrisTexture(gpu, SampledImage)");
            if (s_load is null) lacks.Add("TextureLoader.LoadFile");
            if (s_rgba8 is null) lacks.Add("SampledImage.Rgba8(gpu, image, repeat, mipmaps, name)");
            return lacks;
        }

        /// <summary>Mazal's colour PNG with a wet sheen (alpha = gloss, FrogSkin.WithGloss): the PNG's 255 drew as chrome.</summary>
        public static bool Register(object renderer, string name, string path) =>
            RegisterImage(renderer, name, () => File.Exists(path) ? FrogSkin.WithGloss(Load(path), FroxGloss) : null);

        public const byte FroxGloss = 77;

        /// <summary>The largest image of a texture file the game can read (PNG or DDS), through its own loader.</summary>
        public static global::Meitou.Data.Textures.RgbaImage Load(string path)
        {
            object loaded = s_load!.Invoke(null, s_load.GetParameters().Length == 2 ? [path, false] : [path])!;
            return (global::Meitou.Data.Textures.RgbaImage)((System.Collections.IList)loaded.GetType().GetProperty("Levels")!.GetValue(loaded)!)[0]!;
        }

        /// <summary>Puts an image we make in the renderer's texture table under <paramref name="name"/> (once); false when this game can't.</summary>
        public static bool RegisterImage(object renderer, string name, Func<global::Meitou.Data.Textures.RgbaImage?> make)
        {
            if (!s_resolved) Resolve(renderer.GetType());
            if (s_table?.GetValue(renderer) is not System.Collections.IDictionary table || s_gpu is null || s_debrisTexture is null || s_load is null || s_rgba8 is null)
            {
                if (!s_warned) Log.Line("wildlife: this game's debris renderer can't take our textures; the froxlotls wear the lizard coat and the frogs keep its shine");
                s_warned = true;
                return false;
            }
            if (table.Contains(name)) return table[name] is not null;
            object? texture = null;
            try
            {
                if (make() is { } rgba)
                {
                    object gpu = s_gpu.GetValue(renderer)!;
                    object image = s_rgba8.Invoke(null, [gpu, rgba, true, true, "debris " + name])!;
                    texture = s_debrisTexture.Invoke([gpu, image]);
                }
            }
            catch (Exception e) { Log.Exception("texture " + name, e); }
            table[name] = texture;
            return texture is not null;
        }
    }
}
