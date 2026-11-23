// PZA's Wildlife for Meitou: offline check of the built mod (author PZA, GPLv3 or later). Never shipped. It does not start the game.
// 1. Meitou's own manifest reader and rule set (CodeModManifest.Read, CodeModChecks.Check), the entry type, the Settings page's validation.
// 2. The language files staged in lang\: every key, nothing refused.
// 3. The rules (WildCore), case by case with expectations written out by hand: a fresh corpse draws a bird down to land and peck; a person
//    within the flee radius makes it burst up at once (and the whole corpse's flock); one just outside does not; nobody near the corpse is
//    needed before birds come down; caps per corpse (fresh 6, old 2); reach (fresh 3 km, old 1 km); the cooldown after a scare; a gone
//    corpse; never below the ground.
// 4. Start against the real MeitouClient.dll on the release's .NET 11 runtime: every member resolves and the three Harmony patches apply.
// 5. The real BirdFlocks flying over a test terrain with the patches on: spawned flocks are taken by a corpse, land beside it (the game's bird
//    carries the rules' place), scatter from a person, go back to the game's own flight, and all go back when the switch is turned off.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Meitou.Content;
using Meitou.Data.Gameplay;
using Meitou.Game.Effects;
using PZA.Wildlife.Meitou;

static class P
{
    static int s_bad;

    static int Main(string[] a)
    {
        string dir = a[0];
        Log.Sink = s => Console.WriteLine("  log: " + s);
        var m = Manifest(dir);
        Settings(m);
        Languages(dir);
        Rules();
        Patching();
        Folding(a.Length > 1 ? a[1] : @"C:\Program Files (x86)\Steam\steamapps\common\Kenshi");
        Flight();
        Console.WriteLine(s_bad == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({s_bad})");
        return s_bad == 0 ? 0 : 1;
    }

    static void Expect(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) s_bad++;
    }

    #region Manifest, settings, languages

    static CodeModManifest Manifest(string dir)
    {
        Console.WriteLine("== manifest");
        var m = CodeModManifest.Read(Path.Combine(dir, "meitou.json"));
        Console.WriteLine($"manifest: id={m.Id} api={m.ApiVersion} version={m.Version} entry={m.Entry} assemblies={string.Join(",", m.Assemblies)} gameVersion={m.GameVersion}");
        Expect(CodeModManifest.IsValidId(m.Id), "manifest id is valid");
        foreach (var (ver, expectOk) in new[] { ("0.20.1", true), ("0.21.0", true), ("0.22.1", true), ("0.22.2", true), ("0.30.0", true), ("0.20.0", false) })
        {
            CodeModVersion.TryParse(ver, out var g);
            var problems = CodeModChecks.Check(new List<CodeModEntry> { new("PZAWildlife", m, true) }, g);
            Expect((problems.Count == 0) == expectOk, $"CodeModChecks.Check against game {ver}: {(problems.Count == 0 ? "no problems" : string.Join("; ", problems.SelectMany(p => p.Value)))}");
        }
        foreach (var dll in m.Assemblies) Expect(File.Exists(Path.Combine(dir, dll)), $"staged {dll} present");
        Expect(File.Exists(Path.Combine(dir, "PZAWildlife.mod")), "staged PZAWildlife.mod (the data mod Meitou lists the folder by) present");

        var asm = typeof(WildlifeMod).Assembly;
        bool same = File.ReadAllBytes(asm.Location).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, m.Assemblies[0])));
        Expect(same, "checked assembly is the staged one");
        var entry = asm.GetType(m.Entry);
        Expect(entry is not null && entry.IsPublic && typeof(Meitou.Modding.IMod).IsAssignableFrom(entry) && entry.GetConstructor(Type.EmptyTypes) is not null,
            $"entry {m.Entry} is a public IMod with a public parameterless constructor");
        Console.WriteLine("references: " + string.Join(", ", asm.GetReferencedAssemblies().Select(r => $"{r.Name} {r.Version}")));
        return m;
    }

    static void Settings(CodeModManifest m)
    {
        Console.WriteLine("== settings");
        var groupType = typeof(Meitou.Modding.IMod).Assembly.GetType("Meitou.Modding.ModSettingsGroup")!;
        var ctor = groupType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).First(c => c.GetParameters().Length == 3);
        var s = new WildSettings();
        try
        {
            var g = (Meitou.Modding.ModSettingsGroup)ctor.Invoke(new object[] { m.Id, "PZAWildlife", s });
            Expect(g.Settings.Count == 2, "ModSettingsGroup accepts the settings: " + string.Join(", ", g.Settings.Select(x => $"{x.Key}={x.Text}")));
            g.Settings.First(x => x.Key == "Carrion").Set(false);
            Expect(!Driver.On, "the page's switch turns the rules off (Driver.On)");
            g.Settings.First(x => x.Key == "Carrion").Set(true);
            Expect(Driver.On, "and on again");
        }
        catch (TargetInvocationException e)
        {
            Expect(false, "ModSettingsGroup refused the settings: " + e.InnerException?.Message);
        }
    }

    static void Languages(string dir)
    {
        Console.WriteLine("== languages");
        foreach (var code in new[] { "ru", "zh", "ja" })
        {
            string file = Path.Combine(dir, "lang", $"meitou.{code}.txt");
            if (!File.Exists(file))
            {
                Expect(false, $"lang\\meitou.{code}.txt staged");
                continue;
            }
            var into = new Dictionary<string, string>(StringComparer.Ordinal);
            var (used, refused) = Strings.Text.Load(File.ReadAllBytes(file), into);
            var missing = Strings.Table.Where(t => !into.ContainsKey(t.Key)).Select(t => t.Key).ToList();
            Expect(refused.Count == 0 && missing.Count == 0, $"meitou.{code}.txt: {used} texts, refused [{string.Join(", ", refused)}], missing [{string.Join(", ", missing)}]");
            Expect(File.ReadAllText(file).Contains("MACHINE-DRAFTED"), $"meitou.{code}.txt says it is machine-drafted");
        }
    }

    #endregion

    #region The rules, case by case

    const float Dt = 1 / 30f;
    static float Hills(float x, float z) => 60 * MathF.Sin(x / 700) + 40 * MathF.Cos(z / 900) + 15 * MathF.Sin((x + z) / 130);

    sealed class Sim
    {
        public readonly WildCore Core = new(7) { Ground = Hills };
        public readonly List<object> Birds = [];
        public readonly Dictionary<object, (Vector3 P, Vector3 F)> Free = new(ReferenceEqualityComparer.Instance);
        public List<Corpse> Corpses = [];
        public List<Vector3> Threats = [];
        public float BelowGround, BadForward;
        public HashSet<Phase> Seen = [];
        public readonly Dictionary<string, long> Counts = [];

        public Sim() => Core.Count = r => Counts[r] = Counts.GetValueOrDefault(r) + 1;

        public object Add(Vector3 p)
        {
            var h = new object();
            Birds.Add(h);
            Free[h] = (p, Vector3.UnitX);
            return h;
        }

        /// <summary>One step: free birds stay where they are (or may be claimed), held ones step.</summary>
        public void Step()
        {
            Core.SetWorld(Corpses, Threats);
            Core.Advance(Dt);
            foreach (var h in Birds)
            {
                if (Core.TryGet(h, out var b))
                {
                    if (!Core.Step(h, b, Dt)) Free[h] = (b.Position, b.Forward);
                    else
                    {
                        Seen.Add(b.Phase);
                        float g = Hills(b.Position.X, b.Position.Z);
                        BelowGround = Math.Max(BelowGround, g - b.Position.Y);
                        BadForward = Math.Max(BadForward, MathF.Abs(b.Forward.Length() - 1));
                    }
                }
                else if (Core.TryClaim(h, Free[h].P, Free[h].F, 300, 1, Dt) is not null) Free.Remove(h);
            }
        }

        public float Run(float seconds, Func<bool> until)
        {
            for (float t = 0; t < seconds; t += Dt)
            {
                Step();
                if (until()) return t;
            }
            return float.PositiveInfinity;
        }

        public Phase? PhaseOf(object h) => Core.TryGet(h, out var b) ? b.Phase : null;
    }

    static Vector3 OnGround(float x, float z) => new(x, Hills(x, z), z);

    static void Rules()
    {
        Console.WriteLine("== rules (WildCore, preset Carrion)");
        var corpseAt = OnGround(1000, 1000);
        Expect(WildCore.Cap(new Corpse(1, corpseAt, 0, false)) == 6 && WildCore.Cap(new Corpse(1, corpseAt, 0, true)) == 2
               && WildCore.Cap(new Corpse(1, corpseAt, Preset.OldAge * 2, false)) == 2, "caps: fresh 6, old (flies or 4 game hours) 2");
        Expect(WildCore.Pull(new Corpse(1, corpseAt, 0, false)) == 30000 && WildCore.Pull(new Corpse(1, corpseAt, 0, true)) == 10000, "reach: fresh 3 km, old 1 km");

        // A: a fresh corpse draws a bird 300 m off, it circles, spirals down, lands and pecks
        var sim = new Sim();
        sim.Corpses = [new Corpse(1, corpseAt, 0, false)];
        var bird = sim.Add(corpseAt + new Vector3(3000, 400, 0));
        float claimed = sim.Run(30, () => sim.Core.TryGet(bird, out _));
        Expect(claimed < 30, $"A: a bird 300 m from a fresh corpse is taken ({claimed:0.0} s)");
        float landed = sim.Run(150, () => sim.PhaseOf(bird) == Phase.Peck);
        Expect(landed < 150, $"A: it circles ({sim.Seen.Contains(Phase.Gather)}), spirals down ({sim.Seen.Contains(Phase.Descend)}), glides in ({sim.Seen.Contains(Phase.Approach)}) and lands ({landed:0.0} s later)");
        sim.Core.TryGet(bird, out var held);
        float off = held is null ? -1 : new Vector2(held.Position.X - corpseAt.X, held.Position.Z - corpseAt.Z).Length();
        Expect(held is not null && off >= Preset.SpotNear * Preset.Wingspan - 0.5f && off <= Preset.SpotFar * Preset.Wingspan + 12, $"A: it lands beside the corpse ({off:0.0} units = {off / 10:0.0} m)");
        float landedY = held is null ? 0 : held.Position.Y - Hills(held.Position.X, held.Position.Z);
        Expect(held is not null && landedY >= 0 && landedY <= Preset.LiftPerScale + Preset.HopHeight + 0.01f, $"A: on the ground ({landedY:0.00} units over it)");
        long dips = Count(sim, "peck.dip");
        sim.Run(15, () => false);
        Expect(sim.PhaseOf(bird) == Phase.Peck && Count(sim, "peck.dip") > dips, $"A: it stays and pecks (head dips {Count(sim, "peck.dip")}, hops {Count(sim, "peck.hop")}, turns {Count(sim, "peck.turn")})");
        Expect(sim.BelowGround <= 0.001f && sim.BadForward < 1e-3f, $"A: never below the ground (worst {sim.BelowGround:0.000}), heading always a unit ({sim.BadForward:0.0000})");

        // C: someone 30 m off (outside the flee radius, inside the clear radius): the landed bird stays
        sim.Threats = [corpseAt + new Vector3(300, 0, 0)];
        sim.Run(5, () => false);
        Expect(sim.PhaseOf(bird) == Phase.Peck, "C: a person 30 m away does not scare a landed bird (flee radius 20 m)");

        // B: someone 12 m off: it bursts up in the same step, goes back to the game's flight, and is not taken again during the cooldown
        sim.Threats = [OnGround(corpseAt.X + 120, corpseAt.Z)];
        sim.Step();
        Expect(sim.PhaseOf(bird) == Phase.Flee, "B: a person 12 m away makes the landed bird flee at once");
        float freed = sim.Run(12, () => !sim.Core.TryGet(bird, out _));
        Expect(freed < 12, $"B: it climbs away and goes back to the game's flight ({freed:0.0} s)");
        var at = sim.Free.TryGetValue(bird, out var fr) ? fr.P : default;
        Expect(at.Y - Hills(at.X, at.Z) >= Preset.FleeTopMin - 1 || freed >= Preset.FleeLongest, $"B: high when let go ({at.Y - Hills(at.X, at.Z):0} units over the ground)");
        Expect(Vector2.Distance(new(at.X, at.Z), new(corpseAt.X + 120, corpseAt.Z)) > 150, "B: away from the person");
        sim.Threats = [];
        float again = sim.Run(Preset.CooldownMin - 1, () => sim.Core.TryGet(bird, out _));
        Expect(float.IsInfinity(again), "B: not taken again within the cooldown");
        again = sim.Run(150, () => sim.Core.TryGet(bird, out _));
        Expect(again < 150, $"B: taken again after it ({again:0.0} s more) while the corpse lies there");

        // D: with someone standing by the corpse the birds circle and do not come down; once they leave, they do
        var sim2 = new Sim();
        sim2.Corpses = [new Corpse(5, corpseAt, 0, false)];
        sim2.Threats = [OnGround(corpseAt.X + 100, corpseAt.Z)];
        var waits = sim2.Add(corpseAt + new Vector3(0, 450, 2000));
        sim2.Run(60, () => false);
        Expect(sim2.PhaseOf(waits) == Phase.Gather && !sim2.Seen.Contains(Phase.Descend), "D: a person by the corpse keeps the birds circling high");
        sim2.Threats = [];
        Expect(sim2.Run(150, () => sim2.PhaseOf(waits) == Phase.Peck) < 150, "D: when they leave, the birds come down");

        // E: the whole flock scatters
        var sim3 = new Sim();
        sim3.Corpses = [new Corpse(9, corpseAt, 0, false)];
        var flock = Enumerable.Range(0, 4).Select(i => sim3.Add(corpseAt + new Vector3(1500 + 40 * i, 400, 300 * i))).ToList();
        sim3.Run(240, () => flock.All(h => sim3.PhaseOf(h) == Phase.Peck));
        Expect(flock.All(h => sim3.PhaseOf(h) == Phase.Peck), "E: four birds landed at one corpse");
        var spots = flock.Select(h => sim3.Core.TryGet(h, out var x) ? new Vector2(x.Position.X, x.Position.Z) : default).ToList();
        float closest = spots.SelectMany((p, i) => spots.Skip(i + 1).Select(q => Vector2.Distance(p, q))).DefaultIfEmpty(0).Min();
        Expect(closest >= 0.7f * Preset.Wingspan, $"E: they land apart (closest two {closest:0} units, wingspan {Preset.Wingspan})");
        sim3.Threats = [OnGround(corpseAt.X - 150, corpseAt.Z)];
        sim3.Step();
        Expect(flock.All(h => sim3.PhaseOf(h) == Phase.Flee), "E: one person near: all four burst up in that step");
        Expect(Count(sim3, "scatter") >= 1 && Count(sim3, "flee.scared") >= 4, $"E: rules logged (scatter {Count(sim3, "scatter")}, flee.scared {Count(sim3, "flee.scared")})");

        // F: caps and reach
        foreach (var (old, cap) in new[] { (false, 6), (true, 2) })
        {
            var s = new Sim();
            s.Corpses = [new Corpse(2, corpseAt, 0, old)];
            for (int i = 0; i < 12; i++) s.Add(corpseAt + new Vector3(2000 + 30 * i, 400, 100 * i));
            int most = 0;
            s.Run(60, () =>
            {
                most = Math.Max(most, s.Core.TakenBy(2));
                return false;
            });
            Expect(most == cap, $"F: 12 birds near a{(old ? "n old" : " fresh")} corpse: {most} taken (cap {cap})");
        }
        foreach (var (old, want) in new[] { (false, true), (true, false) })
        {
            var s = new Sim();
            s.Corpses = [new Corpse(3, corpseAt, 0, old)];
            var far = s.Add(corpseAt + new Vector3(12000, 400, 0));
            bool got = s.Run(60, () => s.Core.TryGet(far, out _)) < 60;
            Expect(got == want, $"F: a bird 1.2 km from a{(old ? "n old" : " fresh")} corpse is {(got ? "" : "not ")}taken");
        }

        // G: the corpse goes (eaten, carried, the area unloaded): a landed bird takes off, a circling one is let go at once
        var sim4 = new Sim();
        sim4.Corpses = [new Corpse(4, corpseAt, 0, false)];
        var a1 = sim4.Add(corpseAt + new Vector3(1500, 400, 0));
        sim4.Run(150, () => sim4.PhaseOf(a1) == Phase.Peck);
        var a2 = sim4.Add(corpseAt + new Vector3(-2500, 400, 0));
        sim4.Run(30, () => sim4.PhaseOf(a2) == Phase.Gather);
        sim4.Corpses = [];
        sim4.Step();
        Expect(sim4.PhaseOf(a1) == Phase.Flee && sim4.PhaseOf(a2) is null, $"G: corpse gone: the landed bird takes off ({sim4.PhaseOf(a1)}), the circling one is let go ({sim4.PhaseOf(a2)?.ToString() ?? "free"})");
        Expect(sim4.Run(10, () => sim4.PhaseOf(a1) is null) < 10, "G: and the landed one is let go after its climb");

        // H: off
        var sim5 = new Sim();
        sim5.Corpses = [new Corpse(6, corpseAt, 0, false)];
        for (int i = 0; i < 5; i++) sim5.Add(corpseAt + new Vector3(1000, 400, 200 * i));
        sim5.Run(30, () => sim5.Core.HeldCount >= 3);
        var all = sim5.Core.ReleaseAll();
        Expect(all.Count >= 3 && sim5.Core.HeldCount == 0 && all.All(p => WildCore.Finite(p.Value.Position) && p.Value.Forward.Y >= -0.21f),
            $"H: ReleaseAll lets {all.Count} birds go with a place and a heading the game can fly on from");
    }

    static long Count(Sim s, string rule) => s.Counts.GetValueOrDefault(rule);

    #endregion

    #region The patches on the real game

    static HarmonyLib.Harmony? s_harmony;

    static void Patching()
    {
        Console.WriteLine("== patching the real MeitouClient.dll");
        var missing = Access.ResolveAll();
        Expect(missing.Count == 0, "every member resolves" + (missing.Count > 0 ? ": missing " + string.Join("; ", missing) : ""));
        s_harmony = new HarmonyLib.Harmony("pza.wildlife.check");
        Expect(WildlifeMod.Start(s_harmony), "WildlifeMod.Start applies the three patches");
        var patched = HarmonyLib.Harmony.GetAllPatchedMethods().Select(x => x.DeclaringType?.Name + "." + x.Name).ToList();
        Expect(patched.Contains("Birds.UpdateAttractors") && patched.Contains("BirdFlocks.Update") && patched.Contains("BirdFlocks.Step"),
            "patched: " + string.Join(", ", patched));
        Expect(Patches.FoldOn && patched.Contains("BirdFlocks.AddPieces") && patched.Contains("DebrisRenderer.Load"),
            $"folded wings on (AddPieces, DebrisRenderer.Load){(Access.FoldMissing is { } m ? ": missing " + m : "")}");
    }

    static void Folding(string kenshi)
    {
        Console.WriteLine("== folded wings");
        foreach (var (name, level) in new[] { ("birdtype1.mesh?birds.dds", 100), ("birdtype1.mesh", 50), ("a/b.mesh?x?y", 100) })
        {
            string marked = BirdFold.Name(name, level);
            Expect(BirdFold.Parse(marked, out var plain, out var got) && plain == name && got == level && marked.IndexOf('?') == name.IndexOf('?') + (name.Contains('?') ? marked.Length - name.Length : 0),
                $"name {marked} -> {plain} level {got}; the texture mark stays after the marker");
        }
        Expect(!BirdFold.Parse("birdtype1.mesh?birds.dds", out _, out _) && !BirdFold.Parse("x#pzafold", out _, out _) && !BirdFold.Parse("x#pzafold101", out _, out _),
            "unmarked, empty and out-of-range levels are not folds");

        string mesh = Path.Combine(kenshi, "data", "meshes", "birdtype1.mesh");
        if (!File.Exists(mesh) && Environment.GetEnvironmentVariable("PZA_NO_KENSHI") == "1")
            Console.WriteLine("skip the fold on the game's own mesh: no Kenshi install here (CI); the rest runs");
        else if (!File.Exists(mesh)) Expect(false, "the game's birdtype1.mesh is there to fold: " + mesh);
        else FoldRealMesh(mesh);
        LoadHook();
    }

    /// <summary>The fold on the installed birdtype1.mesh (read only): sizes, every vertex finite, none lost.</summary>
    static void FoldRealMesh(string mesh)
    {
        var before = Meitou.Data.Ogre.OgreMeshReader.ReadFile(mesh);
        var a = Bounds(before);
        foreach (var level in new[] { BirdFold.Half, BirdFold.Full })
        {
            var ogre = Meitou.Data.Ogre.OgreMeshReader.ReadFile(mesh);
            int moved = FoldedMeshes.FoldVertices(ogre, level / 100f);
            var b = Bounds(ogre);
            Console.WriteLine($"  birdtype1.mesh at {level}: {moved} vertices moved; size {a.Size.X:0.0} x {a.Size.Y:0.0} x {a.Size.Z:0.0} -> {b.Size.X:0.0} x {b.Size.Y:0.0} x {b.Size.Z:0.0}");
            Expect(moved > 0 && b.Finite && b.Count == a.Count, $"fold {level}: every vertex finite, none lost");
            if (level == BirdFold.Full)
                Expect(b.Size.X <= 10 && b.Size.Z <= 22 && b.Size.Y <= a.Size.Y, $"fold {level}: a perched bird's size (wingspan {a.Size.X:0} -> {b.Size.X:0.0} units)");
            else Expect(b.Size.X < a.Size.X && b.Size.X > 10, $"fold {level}: half open ({b.Size.X:0.0} units across)");
        }
    }

    /// <summary>The DebrisRenderer.Load hook on an uninitialised renderer (no GPU).</summary>
    static void LoadHook()
    {
        // the Load hook: a marked name never reaches the game's own loader (which would look for a file by that name and draw nothing)
        var renderer = (Meitou.Game.Rendering.Objects.DebrisRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Meitou.Game.Rendering.Objects.DebrisRenderer));
        var meshesField = typeof(Meitou.Game.Rendering.Objects.DebrisRenderer).GetField("_meshes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(meshesField.FieldType, StringComparer.OrdinalIgnoreCase)!;
        meshesField.SetValue(renderer, dict);
        int finds = 0;
        typeof(Meitou.Game.Rendering.Objects.DebrisRenderer).GetField("_find", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(renderer, new Func<string, string?>(_ => { finds++; return null; }));
        string marked2 = BirdFold.Name("birdtype1.mesh?birds.dds", BirdFold.Full);
        dict[marked2] = null;
        var got2 = Access.Load(renderer, marked2);
        Expect(got2 is null && finds == 0, $"DebrisRenderer.Load: a cached folded mesh is answered by the hook (the game's loader not asked: {finds} finds)");
        dict.Remove(marked2);
        Access.Load(renderer, marked2);
        Expect(finds >= 1 && dict.Contains(marked2), $"a missing mesh file: the hook falls back to the plain mesh and caches the answer ({finds} finds)");
    }

    sealed record Box(Vector3 Size, bool Finite, int Count);

    static Box Bounds(Meitou.Data.Ogre.OgreMesh m)
    {
        var ps = new[] { m.SharedVertexData }.Concat(m.SubMeshes.Select(s => s.VertexData)).Where(d => d is not null).Distinct()
            .SelectMany(d => d!.ReadPositions() ?? []).ToList();
        var min = ps.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = ps.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        return new Box(max - min, ps.All(WildCore.Finite), ps.Count);
    }

    static void Flight()
    {
        Console.WriteLine("== the real BirdFlocks with the patches (test terrain, one kind, no game host)");
        if (s_harmony is null) return;
        var kind = new BirdFlockData
        {
            Id = "pza.check.birds", Mesh = "birdtype1.mesh", Texture = "birds.dds", Speed = 300, SpawnCount = 10, Height = 400, GroundHeight = 0, ContourStrength = 1, Distance = 3000,
            Flocking = true, Circling = true, SpawnHeightMin = -100000, SpawnHeightMax = 100000, Amounts = new Dictionary<string, int> { ["g"] = 20 },
        };
        var flocks = new BirdFlocks([kind], Hills, (_, _) => "g", 7);
        var corpseAt = OnGround(0, 0);
        Driver.On = true;
        Driver.SetInput(new Driver.Input([new Corpse(1, corpseAt, 0, false)], []));
        var eye = new Vector3(0, 600, -800);

        int mostHeld = 0, landedMost = 0;
        float worstBelow = 0;
        bool carried = true;
        for (int i = 0; i < 30 * 150; i++)
        {
            flocks.Update(Dt, eye, Vector3.Zero, null);
            int landed = 0;
            foreach (var (h, b) in Driver.Core.Held)
            {
                carried &= Access.Position(h) == b.Position && Access.State(h) == BirdFlocks.BirdState.Attracted;
                worstBelow = Math.Max(worstBelow, Hills(b.Position.X, b.Position.Z) - b.Position.Y);
                if (b.Phase == Phase.Peck) landed++;
            }
            mostHeld = Math.Max(mostHeld, Driver.Core.HeldCount);
            landedMost = Math.Max(landedMost, landed);
        }
        Expect(flocks.BirdCount > 0, $"the game spawned its flocks ({flocks.BirdCount} birds)");
        Expect(mostHeld > 0 && mostHeld <= Preset.FreshCap, $"a fresh corpse took {mostHeld} of them (cap {Preset.FreshCap})");
        Expect(landedMost > 0, $"{landedMost} landed beside it within 150 s");
        Expect(carried, "every held bird: the game's bird carries the rules' place and is marked as circling an attractor (the mate search skips it)");
        Expect(worstBelow <= 0.001f, $"never below the ground ({worstBelow:0.000})");
        Expect(flocks.CountIn(BirdFlocks.BirdState.Attracted) == Driver.Core.HeldCount, $"no other bird of the game is marked ({flocks.CountIn(BirdFlocks.BirdState.Attracted)} marked, {Driver.Core.HeldCount} held)");

        // the frame's pieces: landed birds drawn with the folded mesh, placed by its own middle; flying ones as the game draws them
        var pieces = new List<Meitou.Game.Rendering.Objects.DebrisPiece>();
        Vector3? CentreOf(string m) => BirdFold.Parse(m, out _, out var lv) ? new Vector3(0, lv / 100f, 1) : Vector3.Zero;
        flocks.AddPieces(pieces, CentreOf);
        var landedIds = Driver.Core.Held.Where(p => p.Value.Phase == Phase.Peck).Select(p => (Access.Id(p.Key), p.Value)).ToList();
        bool swapped = landedIds.Count > 0 && landedIds.All(x =>
        {
            var piece = pieces.FirstOrDefault(q => q.Id == x.Item1);
            if (!BirdFold.Parse(piece.Mesh ?? "", out _, out var lv)) return false;
            var want = x.Value.Position + Vector3.Transform(new Vector3(0, lv / 100f, 1) * piece.Scale, piece.Rotation);
            return Vector3.Distance(piece.Position, want) < 1e-3f;
        });
        int flyingFolded = pieces.Count(q => BirdFold.Parse(q.Mesh, out _, out _)) - landedIds.Count(x => true);
        Expect(swapped, $"{landedIds.Count} landed birds drawn folded, placed by the folded mesh's middle");
        Expect(pieces.Count == flocks.BirdCount && flyingFolded <= Driver.Core.HeldCount - landedIds.Count, $"{pieces.Count} pieces; only held birds near the ground are folded");

        var landedNow = Driver.Core.Held.Where(p => p.Value.Phase == Phase.Peck).Select(p => p.Key).ToList();
        Driver.SetInput(new Driver.Input([new Corpse(1, corpseAt, 0, false)], [OnGround(80, 0)]));
        flocks.Update(Dt, eye, Vector3.Zero, null);
        Expect(landedNow.Count > 0 && landedNow.All(h => Driver.Core.TryGet(h, out var b) && b.Phase == Phase.Flee), $"a person 8 m away: all {landedNow.Count} landed birds flee in that step");
        for (int i = 0; i < 30 * 10; i++) flocks.Update(Dt, eye, Vector3.Zero, null);
        var states = landedNow.Select(h => Access.State(h)).ToList();
        Expect(landedNow.All(h => !Driver.Core.TryGet(h, out _)) && states.All(x => x is BirdFlocks.BirdState.Wander or BirdFlocks.BirdState.Follow
               or BirdFlocks.BirdState.Circle), "10 s later they fly with the game's own flight again (" + string.Join(", ", states) + ")");

        for (int i = 0; i < 30 * 30; i++) flocks.Update(Dt, eye, Vector3.Zero, null);
        int before = Driver.Core.HeldCount;
        Driver.On = false;
        flocks.Update(Dt, eye, Vector3.Zero, null);
        Expect(Driver.Core.HeldCount == 0 && flocks.CountIn(BirdFlocks.BirdState.Attracted) == 0, $"switched off: all {before} held birds go back to the game");
        for (int i = 0; i < 30 * 10; i++) flocks.Update(Dt, eye, Vector3.Zero, null);
        Expect(Driver.Core.HeldCount == 0, "and none is taken while off");
        Driver.On = true;
        s_harmony.UnpatchAll(s_harmony.Id);
    }

    #endregion
}
