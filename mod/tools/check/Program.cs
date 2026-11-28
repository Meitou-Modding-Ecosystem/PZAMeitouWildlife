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
        Froxlotls(dir, a.Length > 1 ? a[1] : @"C:\Program Files (x86)\Steam\steamapps\common\Kenshi");
        Stilts(dir);
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
            Expect(g.Settings.Count == 9, "ModSettingsGroup accepts the settings: " + string.Join(", ", g.Settings.Select(x => $"{x.Key}={x.Text}")));
            g.Settings.First(x => x.Key == "Carrion").Set(false);
            Expect(!Driver.On, "the page's switch turns the rules off (Driver.On)");
            g.Settings.First(x => x.Key == "Carrion").Set(true);
            Expect(Driver.On, "and on again");
            // Swamp fireflies rules check
            var ff = new FireflyCore { WaterLevel = 100f };
            Expect(FireflyCore.NightFactor(12.0) == 0f && FireflyCore.NightFactor(23.0) == 1f && FireflyCore.NightFactor(18.5) > 0.4f, "fireflies: night ramp 0 at noon, 1 at night, smooth at dusk");
            Expect(FireflyCore.RainFactor(0f) == 1f && MathF.Abs(FireflyCore.RainFactor(0.95f) - FireflyPreset.RainFloor) < 1e-4f, "fireflies: rain factor 1 in dry, 0.30 floor in heavy rain");
            Expect(FireflyCore.IsSwampBiome("The Swamp") && !FireflyCore.IsSwampBiome("Border Zone"), "fireflies: swamp biome match");
            ff.Step(0.1f, new Vector3(0, 120, 0), 23.0, true, Hills, 0f);
            var snap = ff.Snapshot();
            Expect(ff.ActiveCount == FireflyPreset.MaxCount && snap.Length == FireflyPreset.MaxCount, $"fireflies: spawns {ff.ActiveCount} in swamp at night");
            Expect(snap.All(f => WildCore.Finite(f.Position) && f.Position.Y >= Math.Max(Hills(f.Position.X, f.Position.Z), 100f) + 0.9f && f.Size >= FireflyPreset.MinSize && f.Size <= FireflyPreset.MaxSize + 1e-4f && f.Brightness > 0f),
                "fireflies: finite positions above ground & water with valid size and brightness");
            ff.Step(0.1f, new Vector3(0, 120, 0), 23.0, true, Hills, 0.95f);
            int expectedRainCount = (int)MathF.Round(FireflyPreset.MaxCount * FireflyPreset.RainFloor);
            Expect(ff.ActiveCount == expectedRainCount && ff.Snapshot().Length == expectedRainCount, $"fireflies: thins to {ff.ActiveCount} (30%) in heavy rain");
            float rainPeak = 0;
            for (int i = 0; i < 300; i++)
            {
                ff.Step(0.05f, new Vector3(0, 120, 0), 23.0, true, Hills, 0.95f);
                rainPeak = Math.Max(rainPeak, ff.Snapshot().Max(f => f.Brightness));
            }
            Expect(rainPeak > 0.95f, $"fireflies: in heavy rain fewer flies, but each still flashes full (peak {rainPeak:0.00}; was 0.30 before 2026-11-27)");
            var tracers = new List<Meitou.Game.Rendering.Particles.ParticleRenderer.Tracer>();
            Driver.Fireflies.Reset();
            Driver.Fireflies.Step(0.1f, new Vector3(0, 120, 0), 23.0, true, Hills);
            FireflyMeshes.AddTracers(tracers, Matrix4x4.Identity, new Vector3(0, 120, -200));
            Expect(tracers.Count == FireflyPreset.MaxCount && tracers.All(t => float.IsFinite(t.Colour.X) && t.Colour.Y > 0f && Vector3.Distance(t.Head, t.Tail) >= FireflyPreset.MinSize - 1e-3f),
                $"fireflies: emits {tracers.Count} glowing tracers via ParticleRenderer");
            // the blink (2026-11-27, the user: "not at all pulsing"): mostly an ember, a short flash each period, HDR only at the flash
            var samples = Enumerable.Range(0, 4000).Select(i => FireflyCore.Blink(i * 0.005f, 4f, 1.3f)).ToList();
            float lit = samples.Count(b => b > 0.5f) / (float)samples.Count;
            Expect(samples.Min() >= FireflyPreset.Ember - 1e-4f && samples.Max() > 0.97f && lit > 0.03f && lit < 0.15f,
                $"fireflies: blink from {samples.Min():0.00} to {samples.Max():0.00}, bright {lit:P0} of the time (a 0.55 s flash every 4 s)");
            Expect(FireflyCore.Blink(float.NaN, 4, 0) == FireflyPreset.Ember && FireflyCore.Blink(1, 0, 0) == FireflyPreset.Ember, "fireflies: blink on bad input is an ember");
            Expect(FireflyCore.NearFade(5f) == 0f && FireflyCore.NearFade(20f) > 0.3f && FireflyCore.NearFade(40f) == 1f,
                "fireflies: near-camera fade 0 within 1 m, smooth ramp, 1 beyond 3 m");
            var px = FireflyMeshes.GlowPixels();
            int mid = (FireflyMeshes.GlowSize / 2 * FireflyMeshes.GlowSize + FireflyMeshes.GlowSize / 2) * 4 + 3;
            Expect(px[3] == 0 && px[px.Length - 1] == 0 && px[mid] > 230, "fireflies: radial 32x32 glow texture transparent at corners, bright at center");
            Driver.Fireflies.Reset();
            ff.Step(0.1f, new Vector3(0, 120, 0), 12.0, true, Hills);
            Expect(ff.ActiveCount == 0 && ff.Snapshot().Length == 0, "fireflies: clears in daytime");
            Expect(BirdFold.CanFoldMesh("birdtype1.mesh?birds.dds") && BirdFold.CanFoldMesh("data/meshes/birdtype1.mesh") && !BirdFold.CanFoldMesh("custom_bat.mesh"),
                "fold: only folds birdtype1.mesh, skips custom meshes");
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
        // I: bites while pecking (display only): queued at the beak, capped, none while the switch is off
        var sim6 = new Sim();
        sim6.Corpses = [new Corpse(7, corpseAt, 0, false)];
        for (int i = 0; i < 6; i++) sim6.Add(corpseAt + new Vector3(1200, 400, 150 * i));
        sim6.Run(200, () => false);
        var bites = sim6.Core.TakeBites();
        Expect(Count(sim6, "bite") > 0 && bites.Count > 0 && bites.Count <= Preset.BiteQueue,
            $"I: {Count(sim6, "bite")} bites at {Count(sim6, "peck.dip")} head dips; {bites.Count} waiting (most {Preset.BiteQueue}, {Count(sim6, "bite.dropped")} dropped)");
        Expect(bites.All(p => Vector2.Distance(new(p.X, p.Z), new(corpseAt.X, corpseAt.Z)) < Preset.SpotFar * Preset.Wingspan + Preset.BeakReach + 12
                              && p.Y >= Hills(p.X, p.Z)), "I: every bite beside the body and on or above the ground");
        sim6.Core.Bites = false;
        long before6 = Count(sim6, "bite");
        sim6.Run(60, () => false);
        Expect(Count(sim6, "bite") == before6 && sim6.Core.TakeBites().Count == 0, "I: none while 'Blood while pecking' is off");
        Expect(all.Count >= 3 && sim5.Core.HeldCount == 0 && all.All(p => WildCore.Finite(p.Value.Position) && p.Value.Forward.Y >= -0.21f),
            $"H: ReleaseAll lets {all.Count} birds go with a place and a heading the game can fly on from");
    }

    static long Count(Sim s, string rule) => s.Counts.GetValueOrDefault(rule);

    #endregion

    #region The patches on the real game

    static HarmonyLib.Harmony? s_harmony;

    static void Patching()
    {
        Console.WriteLine("== code-mod effects (blood while pecking)");
        var ctx = (Meitou.Modding.ModContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Meitou.Modding.ModContext));
        string line = BiteEffects.Setup(ctx);
        bool has = typeof(Meitou.Modding.ModContext).Assembly.GetType("Meitou.Modding.EffectAnchor") is not null;
        Expect(has ? BiteEffects.Missing is null : line.Contains("OFF"), "this game " + (has ? "has" : "lacks") + " the effect API: " + line);
        Console.WriteLine("== patching the real MeitouClient.dll");
        var missing = Access.ResolveAll();
        Expect(missing.Count == 0, "every member resolves" + (missing.Count > 0 ? ": missing " + string.Join("; ", missing) : ""));
        s_harmony = new HarmonyLib.Harmony("pza.wildlife.check");
        Expect(WildlifeMod.Start(s_harmony), "WildlifeMod.Start applies the three patches");
        var patched = HarmonyLib.Harmony.GetAllPatchedMethods().Select(x => x.DeclaringType?.Name + "." + x.Name).ToList();
        Expect(patched.Contains("Birds.UpdateAttractors") && patched.Contains("BirdFlocks.Update") && patched.Contains("BirdFlocks.Step"),
            "patched: " + string.Join(", ", patched));
        // Swamp frogs (user 2026-11-27: "add in frogs that will dart into the water when the player gets near them")
        {
            // a shore along x = 0: land rises to the east, water (ground under 100) to the west
            Func<float, float, float> shore = (x, z) => 100 + x * 0.12f;
            var frogs = new FrogCore(3) { WaterLevel = 100 };
            var none = new List<Vector3>();
            for (int i = 0; i < 15; i++) frogs.Step(0.1f, new Vector3(0, 100, 0), 23.0, true, shore, none);   // 1.5 s: none has idled yet
            var fresh = frogs.Frogs.Where(f => f.State == FrogState.Sit).Select(f => (f.Pos, f.Yaw)).ToList();   // copied: the frogs live on
            for (int i = 0; i < 300; i++) frogs.Step(0.1f, new Vector3(0, 100, 0), 23.0, true, shore, none);
            var sitting = frogs.Frogs.Where(f => f.State is FrogState.Sit or FrogState.Hop).ToList();
            Expect(sitting.Count == FrogPreset.NightCount, $"frogs: {sitting.Count} out at night (want {FrogPreset.NightCount})");
            Expect(fresh.Count > 0 && fresh.All(f => f.Pos.Y >= Math.Max(shore(f.Pos.X, f.Pos.Z), 100) - 1e-3f && shore(f.Pos.X, f.Pos.Z) >= 100 - FrogPreset.WaterBelow
                                    && shore(f.Pos.X, f.Pos.Z) <= 100 + FrogPreset.WaterAbove && MathF.Sin(f.Yaw) < 0),
                "frogs: each sits on the bank at the water line, facing the water (west) " + string.Join("; ", fresh.Take(4).Select(f => $"x {f.Pos.X:0} y {f.Pos.Y:0.0} g {shore(f.Pos.X, f.Pos.Z):0.0} yaw {f.Yaw:0.00}")));
            var near = frogs.Frogs.First(f => f.State == FrogState.Sit);
            var person = new List<Vector3> { near.Pos + new Vector3(40, 0, 0) };
            frogs.Step(0.05f, new Vector3(0, 100, 0), 23.0, true, shore, person);
            Expect(frogs.Frogs.First(f => f.Id == near.Id).State == FrogState.Leap, "frogs: someone 4 m away makes a sitting frog leap at once");
            for (int i = 0; i < 30; i++) frogs.Step(0.05f, new Vector3(0, 100, 0), 23.0, true, shore, person);
            var after = frogs.Frogs.First(f => f.Id == near.Id);
            var splashes = frogs.TakeSplashes();
            Expect(after.State == FrogState.Away && splashes.Count >= 1 && splashes.All(sp => shore(sp.X, sp.Z) < 100),
                $"frogs: it leaps into the water, splashes there and is gone ({after.State}, {splashes.Count} splashes)");
            for (int i = 0; i < 500; i++) frogs.Step(0.1f, new Vector3(0, 100, 0), 23.0, true, shore, person);
            Expect(frogs.Frogs.Where(f => f.State == FrogState.Sit).All(f => Vector2.Distance(new(f.Pos.X, f.Pos.Z), new(person[0].X, person[0].Z)) >= FrogPreset.FleeRadius - 1e-3f),
                "frogs: they come back away from the person (none sitting within 7 m)");
            frogs.Step(0.1f, new Vector3(0, 100, 0), 12.0, true, shore, none);
            int day = frogs.Frogs.Count;
            frogs.Step(0.1f, new Vector3(0, 100, 0), 23.0, false, shore, none);
            Expect(day == FrogPreset.DayCount && frogs.Frogs.Count == 0 && frogs.Snapshot().Length == 0, $"frogs: {day} by day, none outside the Swamp");
            var model = FrogMesh.Model();
            var pts = model.SubMeshes[0].VertexData!.ReadPositions()!;
            var size = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max) - pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            Expect(pts.All(WildCore.Finite) && pts.Min(p => p.Y) >= -0.05f && size.Z > 1.8f && size.Z < 2.6f && model.SubMeshes[0].Indices.Indices.All(i => i < pts.Length),
                $"frogs: the model is whole ({pts.Length} vertices, {size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} units, feet at 0)");
        }
        // Night sounds (user 2026-11-27 via mission control: "Yes to the Night sounds for Wildlife")
        Expect(NightSoundsHooks.On && patched.Contains("AudioSystem.Update") && patched.Contains("SoundInstance..ctor"),
            "night sounds on (AudioSystem.Update, WwisePlayer.SoundInstance)" + (NightSoundsHooks.Missing is { } nm ? ": missing " + nm : ""));
        Expect(NightSounds.Sunset(23, 5) == NightSoundsPreset.Dusk && NightSounds.Sunset(18, 5) == 18 && NightSounds.Sunset(23, 20) == 23,
            "night sounds: the audio's sunset moves to 19:30, never later than the game's, never before sunrise");
        Expect(NightSounds.IsNight(20.0, 5, NightSounds.Sunset(23, 5)) && !NightSounds.IsNight(20.0, 5, 23) && !NightSounds.IsNight(12, 5, 19.5),
            "night sounds: 20:00 is night for the ambience now (was day until 23:00); noon is day");
        Expect(NightSounds.LiftDb(-44) == NightSoundsPreset.CallFloor + NightSoundsPreset.Lift && NightSounds.LiftDb(0) == NightSoundsPreset.Lift
               && NightSounds.LiftDb(-5) == -5 + NightSoundsPreset.Lift, "night sounds: a call rolled at -44 dB plays at -10, nothing gets quieter, +2 dB on top");
        double sunset = 23;
        Driver.Settings.NightSounds = true;
        NightSoundsHooks.UpdatePrefix(20.0, 5, ref sunset);
        bool nightOn = NightSoundsHooks.Night;
        double sunset2 = 23;
        Driver.Settings.NightSounds = false;
        NightSoundsHooks.UpdatePrefix(20.0, 5, ref sunset2);
        Expect(sunset == NightSoundsPreset.Dusk && nightOn && sunset2 == 23 && !NightSoundsHooks.Night,
            $"night sounds: the prefix moves sunset to {sunset} at 20:00 (night), and leaves the game's 23:00 with the switch off");
        Driver.Settings.NightSounds = true;
        Expect(Patches.FireflyLightsOn && patched.Contains("CharacterRenderer.CollectLamps"), "firefly light on (CharacterRenderer.CollectLamps)"
               + (FireflyLights.Missing is { } flm ? ": missing " + flm : ""));
        if (Patches.FireflyLightsOn)
        {
            Driver.Fireflies.Reset();
            Driver.Fireflies.Step(0.1f, new Vector3(0, 120, 0), 23.0, true, Hills);
            for (int i = 0; i < 200; i++) Driver.Fireflies.Step(0.05f, new Vector3(0, 120, 0), 23.0, true, Hills);   // let some flash
            Driver.FireflyEye = new Vector3(0, 150, -100);
            var lamps = new List<Meitou.Data.World.WorldLight>();
            FireflyLights.Add(lamps);
            var flies = Driver.Fireflies.Snapshot();
            Expect(lamps.Count > 0 && lamps.Count <= FireflyPreset.LightCount
                   && lamps.All(l => l.Intensity > 0 && l.Intensity <= FireflyPreset.LightPower + 1e-4f && l.Radius == FireflyPreset.LightRadius
                                     && flies.Any(f => Vector3.Distance(f.Position, l.Position) < 1e-3f)),
                $"firefly light: {lamps.Count} point lights at the flies (most {FireflyPreset.LightCount}), power up to {FireflyPreset.LightPower}, reach {FireflyPreset.LightRadius / 10} m");
            Driver.Fireflies.Reset();
        }
        Expect(Patches.FoldOn && patched.Contains("BirdFlocks.AddPieces") && patched.Contains("DebrisRenderer.Load"),
            $"folded wings on (AddPieces, DebrisRenderer.Load){(Access.FoldMissing is { } m ? ": missing " + m : "")}");
        Expect(Patches.FirefliesOn && patched.Contains("ParticleRenderer.Prepare"),
            $"swamp fireflies on (ParticleRenderer.Prepare){(Access.FireflyMissing is { } fm ? ": missing " + fm : "")}");
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
        Wings(kenshi);
    }

    /// <summary>0.8.0 (user 2026-11-27: "Both: frog + froxlotls"): the froxlotl's bake, rules and names; the Gorillo frog's animation frames.</summary>
    static void Froxlotls(string dir, string kenshi)
    {
        Console.WriteLine("== froxlotls and the frog's animations (0.8.0)");
        string bake = Path.Combine(dir, "assets", "froxlotl", "froxlotl.pzaf");
        var asset = FroxAsset.Read(File.ReadAllBytes(bake));
        Expect(asset.Vertices > 1000 && asset.Indices.Length % 3 == 0 && asset.Indices.All(i => i < asset.Vertices) && asset.Frames.Length == 4
               && asset.Frames.All(c => c.Length >= 8) && asset.Clips.RunSpeed > asset.Clips.WalkSpeed && asset.Clips.WalkSpeed > 0.3f,
            $"froxlotl bake: {asset.Vertices} vertices, rest/walk/run {asset.Frames[0].Length}/{asset.Frames[1].Length}/{asset.Frames[2].Length} frames, walk {asset.Clips.WalkSpeed:0.00} run {asset.Clips.RunSpeed:0.00} units/s");
        float lo = float.MaxValue, len = 0;
        bool finite = true;
        foreach (var clip in asset.Frames)
            foreach (var (p, n) in clip)
            {
                lo = Math.Min(lo, p.Min(v => v.Y));
                finite &= p.All(WildCore.Finite) && n.All(WildCore.Finite);
            }
        var rest = asset.Frames[0][0].P;
        len = rest.Max(v => v.Z) - rest.Min(v => v.Z);
        Expect(finite && MathF.Abs(lo) < 0.01f && MathF.Abs(len - asset.Length) < 0.05f, $"froxlotl frames: finite, feet at 0 ({lo:0.000}), {len:0.00} units long at rest");
        var m = asset.Mesh(2, 5);
        Expect(m.SubMeshes[0].VertexData!.ReadPositions()!.Length == asset.Vertices && m.SubMeshes[0].Indices.Indices.Length == asset.Indices.Length, "froxlotl frame mesh whole");
        foreach (var colour in FroxPreset.Colours)
            Expect(File.Exists(Path.Combine(dir, "assets", "froxlotl", "textures", colour + ".png")), "froxlotl colour shipped: " + colour);
        Expect(File.Exists(Path.Combine(dir, "assets", "froxlotl", "CREDITS.md")), "froxlotl credit shipped");
        bool threw = false;
        try { FroxAsset.Read(File.ReadAllBytes(bake)[..2000]); } catch (InvalidDataException) { threw = true; }
        Expect(threw, "a cut-short bake is refused");
        string name = FroxMesh.PieceName(6, 2, 11);
        Expect(FroxMesh.Parse(name, out int pc, out int pf, out int pcol) && pc == 2 && pf == 11 && pcol == 6 && !FroxMesh.Parse("pza.frox.mesh#9.1?x", out _, out _, out _)
               && FroxMesh.Is(name), $"froxlotl piece names round-trip ({name})");
        var missing = FroxMesh.Textures.Missing(typeof(Meitou.Game.Rendering.Objects.DebrisRenderer));
        Expect(missing.Count == 0, "froxlotl colours: the renderer's texture members are there" + (missing.Count > 0 ? " (lacks " + string.Join(", ", missing) + ")" : ""));

        // the birds' rig (0.11.0): Mesh2Motion's CC0 skeleton and clips on the game's own bird
        var rig = BirdRig.Read(File.ReadAllBytes(Path.Combine(dir, "assets", "birdrig", "birdrig.pzab")));
        Expect(rig.Bones.Length >= 15 && rig.Skin.Length >= 6 && rig.Skin.Take(6).All(c => c.Length >= 8)
               && MathF.Abs(rig.Seconds[(int)RigClip.Glide] - RigPreset.GlideSeconds) < 0.02f && MathF.Abs(rig.Seconds[(int)RigClip.Idle] - RigPreset.IdleSeconds) < 0.02f
               && MathF.Abs(rig.Seconds[(int)RigClip.TakeOff] - RigPreset.TakeOffSeconds) < 0.02f && File.Exists(Path.Combine(dir, "assets", "birdrig", "CREDITS.md")),
            $"bird rig: {rig.Bones.Length} bones, {string.Join("/", rig.Skin.Take(6).Select(c => c.Length))} frames, clip lengths as RigPreset; credit shipped");
        var probes = new[] { new Vector3(0, 0, 3), new Vector3(1.5f, 0.3f, -1), new Vector3(12, 1.5f, 0), new Vector3(-25, 7, 3), new Vector3(float.NaN, 0, 0) };
        Expect(probes.All(pt => rig.Weights(pt) is var w && MathF.Abs(w.Sum(x => x.Weight) - 1) < 1e-4f && w.All(x => x.Bone >= 0 && x.Bone < rig.Bones.Length && x.Weight >= -1e-6f))
               && rig.Weights(new Vector3(20, 3, 1)).All(x => rig.Bones[x.Bone].Side == 1) && rig.Weights(new Vector3(-20, 3, 1)).All(x => rig.Bones[x.Bone].Side == 2)
               && rig.Weights(new Vector3(0, 0, 3)).All(x => rig.Bones[x.Bone].Side == 0),
            "bird rig weights: sum to 1, a wing vertex on its own wing's bones, a body vertex on the body's");
        string rn = BirdRig.Name("birdtype1.mesh?birds.dds", new RigFrame(RigClip.Land, 0.5f), rig);
        Expect(BirdRig.Parse(rn, out var rplain, out int rc, out int rf) && rplain == "birdtype1.mesh?birds.dds" && rc == (int)RigClip.Land && rf == rig.Skin[rc].Length / 2
               && !BirdRig.Parse("birdtype1.mesh#pzarigx.1?y", out _, out _, out _) && !BirdFold.ParsePose(rn, out _, out _), $"bird rig names round-trip ({rn}), never taken for a fold");
        string birdMesh = Path.Combine(kenshi, "data", "meshes", "birdtype1.mesh");
        if (File.Exists(birdMesh))
        {
            float Span(int clip, int frame, out float tipLow, out float tipHigh)
            {
                var o = Meitou.Data.Ogre.OgreMeshReader.ReadFile(birdMesh);
                rig.Pose(o, clip, frame);
                var ps = new[] { o.SharedVertexData }.Concat(o.SubMeshes.Select(sm => sm.VertexData)).Where(d => d is not null).Distinct().SelectMany(d => d!.ReadPositions() ?? []).ToArray();
                var wingtip = ps.Where(v => MathF.Abs(v.X) > 0).OrderByDescending(v => v.Length()).First();
                tipLow = ps.Min(v => v.Y); tipHigh = ps.Max(v => v.Y);
                return ps.All(WildCore.Finite) ? ps.Max(v => v.X) - ps.Min(v => v.X) : float.NaN;
            }
            float glideSpan = Span((int)RigClip.Glide, 0, out _, out _);
            float lowest = float.MaxValue, highest = float.MinValue;
            for (int f = 0; f < rig.Skin[(int)RigClip.Flap].Length; f++) { Span((int)RigClip.Flap, f, out var low, out var high); lowest = Math.Min(lowest, low); highest = Math.Max(highest, high); }
            float perchedSpan = Span((int)RigClip.Idle, 0, out _, out _);
            Expect(glideSpan > 40 && glideSpan < 60 && highest - lowest > 20 && perchedSpan < glideSpan * 0.6f,
                $"the game's bird on the rig: {glideSpan:0} units across gliding, its wings sweep {highest - lowest:0} units up and down in the beat, {perchedSpan:0} units across perched (wings folded)");
        }
        else if (Environment.GetEnvironmentVariable("PZA_NO_KENSHI") != "1") Expect(false, "the game's birdtype1.mesh is there: " + birdMesh);
        var life = new List<RigClip>();
        var eb = new WildBird { Phase = Phase.Approach, Length = 3 };
        for (float t = 0; t < 3; t += 1f / 30) { eb.Clock = t; life.Add(RigPreset.For(eb).Clip); }
        eb.Phase = Phase.Peck;
        for (float t = 0; t < 1; t += 1f / 30) { eb.Clock = t; life.Add(RigPreset.For(eb).Clip); }
        eb.Action = 2; eb.ActionClock = 0.2f; life.Add(RigPreset.For(eb).Clip); eb.Action = 0;
        eb.Phase = Phase.Flee; eb.Delay = 0.1f;
        for (float t = 0; t < 2; t += 1f / 30) { eb.Clock = t; life.Add(RigPreset.For(eb).Clip); }
        var order = life.Where((c, i) => i == 0 || life[i - 1] != c).ToList();
        Expect(string.Join(",", order) == "Glide,Flap,Land,Idle,Hop,Idle,TakeOff,Flap",
            $"rig clips over a bird's life: {string.Join(" > ", order)}");
        Expect(RigPreset.Free(0, true, 7, 3).Clip == RigClip.Idle && RigPreset.Free(BirdFold.FlapPose(4), false, 7, 3) == new RigFrame(RigClip.Flap, 0.25f)
               && RigPreset.Free(0, false, 7, 3).Clip == RigClip.Glide && RigPreset.Loop(float.NaN, 1) == 0 && RigPreset.Loop(2.5f, 1) == 0.5f,
            "free birds: resting ones sit, beating ones flap, the rest glide; loops wrap");

        // rules: a shore along x = 0, water to the west
        Func<float, float, float> shore = (x, z) => 100 + x * 0.12f;
        var frox = new FroxCore(4) { WaterLevel = 100, Clips = asset.Clips };
        for (int i = 0; i < 80; i++) frox.Step(0.1f, new Vector3(0, 100, 0), 23.0, true, shore, []);
        var out1 = frox.All.Where(f => f.State is FroxState.Rest or FroxState.Crawl).ToList();
        Expect(out1.Count == FroxPreset.NightCount && frox.Pool.Length == FroxPreset.PoolSize && frox.Pool[0] == 0 && out1.All(f => frox.Pool.Contains(f.Colour)),
            $"froxlotls: {out1.Count} out at night in {string.Join(", ", frox.Pool.Select(c => FroxPreset.Colours[c]))}");
        var one = out1[0];
        var person = new List<Vector3> { one.Pos + new Vector3(50, 0, 0) };
        float fastest = 0, deepest = 0, swam = 0, belowBottom = 0;
        var last = one.Pos;
        int splashes = 0;
        bool swum = false;
        Vector3? entered = null;
        for (int i = 0; i < 900; i++)
        {
            frox.Step(1f / 60, new Vector3(0, 100, 0), 23.0, true, shore, person);
            var f = frox.All.First(x => x.Id == one.Id);
            if (f.State == FroxState.Run) fastest = Math.Max(fastest, new Vector2(f.Pos.X - last.X, f.Pos.Z - last.Z).Length() * 60);
            if (f.State == FroxState.Swim)
            {
                swum = true;
                entered ??= f.Pos;
                deepest = Math.Max(deepest, 100 - f.Pos.Y);
                swam = Math.Max(swam, new Vector2(f.Pos.X - entered.Value.X, f.Pos.Z - entered.Value.Z).Length());
                belowBottom = Math.Max(belowBottom, shore(f.Pos.X, f.Pos.Z) + FroxPreset.Keel - f.Pos.Y);
            }
            last = f.Pos;
            splashes += frox.TakeSplashes().Count;
        }
        var gone = frox.All.First(x => x.Id == one.Id);
        Expect(gone.State == FroxState.Away && splashes >= 1 && fastest > 0 && fastest <= asset.Clips.RunSpeed * FroxPreset.RunRate * one.Scale * 1.01f && fastest > asset.Clips.RunSpeed * one.Scale * 1.5f,
            $"froxlotl: someone 5 m away sends it turning and running into the water ({gone.State}, {splashes} splashes, {fastest:0.00} units/s = its run's own speed {asset.Clips.RunSpeed * FroxPreset.RunRate * one.Scale:0.00}, {one.Scale * asset.Length / 10:0.00} m long)");
        Expect(swum && deepest > 0.5f && deepest <= FroxPreset.SwimDepth + 0.01f && belowBottom <= 1e-3f && swam > 3 * one.Scale && last.X < entered!.Value.X,
            $"froxlotl: it swims away under the surface ({deepest:0.0} units deep, {swam:0.0} units out into deeper water, never in the bottom), then sinks away");
        var draws = frox.Snapshot();
        Expect(draws.All(d => d.Clip is >= 0 and <= 3 && d.Frame >= 0 && d.Frame < asset.Frames[d.Clip].Length), "froxlotl frames in range");
        frox.Step(0.1f, new Vector3(0, 100, 0), 23.0, false, shore, []);
        Expect(frox.All.Count == 0 && frox.Snapshot().Length == 0, "froxlotls: none outside the Swamp");
        Expect(FroxCore.Frame(0.5f, 1, 20) == 10 && FroxCore.Frame(1.0f, 1, 20) == 0 && FroxCore.Frame(float.NaN, 1, 20) == 0 && FroxCore.Frame(3, 0, 20) == 0,
            "froxlotl frame timing loops, bad input = frame 0");

        // the Gorillo frog's animation frames
        Expect(FrogMesh.ParseAnim(FrogMesh.AnimName(2, 0.55f), out int ac, out int af) && ac == 2 && af == 5 && !FrogMesh.ParseAnim(FrogMesh.PoseName(3), out _, out _)
               && FrogMesh.AnimName(9, 7) == FrogMesh.AnimName(2, 0.999f), "frog animation names round-trip, clamped");
        var anims = new List<(int, float)>();
        foreach (var (st, clk, l) in new[] { (FrogState.Sit, 0f, 0f), (FrogState.Hop, 0.15f, 0.3f), (FrogState.Leap, -0.1f, 0.5f), (FrogState.Leap, 0.25f, 0.5f), (FrogState.Dive, 0.1f, 0.25f) })
            anims.Add(FrogPreset.Animation(st, clk, l, 0.7f));
        Expect(anims[0].Item1 == 0 && anims[1] == (1, 0.5f) && anims[2].Item1 == 0 && anims[3] == (2, 0.5f) && anims[4].Item1 == 2,
            "frog animations: idle sitting and in the startle, walk for a hop, run for the leap and the dive");
        string meshes = Path.Combine(kenshi, "data", "animal", "meshes");
        if (!Directory.Exists(meshes))
        {
            if (Environment.GetEnvironmentVariable("PZA_NO_KENSHI") == "1") Console.WriteLine("skip the Gorillo's animations: no Kenshi install here (CI)");
            else Expect(false, "the game's animal meshes are there: " + meshes);
            return;
        }
        // the frog's skin (0.9.0): matte, the Gorillo's own eyes, the coat's emblem gone
        string coatPath = Path.Combine(kenshi, "data", "items", "armour", "coat", FrogSkin.Coat), gorPath = Path.Combine(kenshi, "data", "animal", "textures", FrogSkin.Gorillo);
        var coat = Meitou.Data.Textures.TextureLoader.LoadFile(coatPath, false).Levels[0];
        var gor = Meitou.Data.Textures.TextureLoader.LoadFile(gorPath, false).Levels[0];
        var skin = FrogSkin.Compose(coat, gor);
        int sz = FrogSkin.Size, eyeAt = 4 * ((int)(FrogSkin.Eye.V * sz) * sz + (int)(FrogSkin.Eye.U * sz));
        int shiny = 0, grey = 0;
        for (int i = 0; i < sz * sz; i++)
        {
            byte r = skin.Pixels[4 * i], g = skin.Pixels[4 * i + 1], b = skin.Pixels[4 * i + 2], a = skin.Pixels[4 * i + 3];
            if (a > FrogSkin.Gloss + 2) shiny++;
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 26 && (0.3f * r + 0.59f * g + 0.11f * b) > 102 && a <= FrogSkin.Gloss + 2) grey++;
        }
        float eyeLum = 0.3f * skin.Pixels[eyeAt] + 0.59f * skin.Pixels[eyeAt + 1] + 0.11f * skin.Pixels[eyeAt + 2];
        Expect(skin.Width == sz && coat.Pixels[3] == 255 && shiny < sz * sz / 50 && skin.Pixels[eyeAt + 3] > 200 && eyeLum < 60 && grey < sz * sz / 2000,
            $"frog skin: matte (gloss {FrogSkin.Gloss}/255 on {100f * (sz * sz - shiny) / (sz * sz):0.0}% of it; the coat had 255), the Gorillo's dark wet eye (lum {eyeLum:0}), {grey} emblem pixels left");
        var glossed = FrogSkin.WithGloss(coat, FroxMesh.Textures.FroxGloss);
        Expect(glossed.Pixels.Where((_, i) => i % 4 == 3).All(x => x == FroxMesh.Textures.FroxGloss) && coat.Pixels[3] == 255, "froxlotl colours get a wet sheen, the source untouched");
        Expect(FroxPreset.ScaleMin * asset.Length >= 4 && FroxPreset.RunRate >= 2, $"froxlotls: {FroxPreset.ScaleMin * asset.Length / 10:0.00}-{FroxPreset.ScaleMax * asset.Length / 10:0.00} m long, running {FroxPreset.RunRate}x their baked run");

        var bakeG = GorilloBake.Create(n => File.Exists(Path.Combine(meshes, n)) ? Path.Combine(meshes, n) : null, out var why);
        Expect(bakeG is not null, "the Gorillo's idle, walk and run are there " + why);
        if (bakeG is null) return;
        for (int c = 0; c < GorilloBake.Clips.Length; c++)
        {
            var sizes = new List<Vector3>();
            var firsts = new List<Vector3>();
            bool ok = true;
            for (int f = 0; f < GorilloBake.Clips[c].Frames; f++)
            {
                var g = bakeG.Mesh(c, f);
                var b = Bounds(g);
                ok &= b.Finite && MathF.Abs(b.Min.Y) < 1e-3f && b.Size.Z > 1.2f && b.Size.Z < 3.5f && b.Size.Y < 2.5f;
                sizes.Add(b.Size);
                firsts.Add(g.SubMeshes.Select(s => s.VertexData ?? g.SharedVertexData).First()!.ReadPositions()![100]);
            }
            float moves = firsts.Zip(firsts.Skip(1)).Max(z => Vector3.Distance(z.First, z.Second));
            Expect(ok && moves > 0.005f, $"Gorillo frog '{GorilloBake.Clips[c].Anim}': {GorilloBake.Clips[c].Frames} frames, about {sizes.Average(v => v.Z):0.00} units long, feet at 0, it moves ({moves:0.000} units a frame)");
        }
    }

    /// <summary>0.7.0 (user 2026-11-27: "smoother ... less janky wing flapping", the same for the frogs): wing poses, the beat, the smooth
    /// landing and take-off, free birds' beats, frog poses and the Gorillo frog.</summary>
    static void Stilts(string dir)
    {
        Console.WriteLine("== mire stilts (0.12.0)");
        string at = Path.Combine(dir, "assets", "stilt");
        var ground = FroxAsset.Read(File.ReadAllBytes(Path.Combine(at, "stilt_ground.pzaf")));
        var air = FroxAsset.Read(File.ReadAllBytes(Path.Combine(at, "stilt_air.pzaf")));
        var c = StiltMesh.ClipsOf(ground, air);
        Expect(ground.Frames.Length == 5 && air.Frames.Length == 4 && ground.Frames.All(f => f.Length >= 2) && air.Frames.All(f => f.Length >= 8)
               && MathF.Abs(c.HuntSeconds - StiltPreset.StrikeAt - 2.5f) < 0.2f && c.WadeSpeed is > 0.5f and < 6,
            $"stilt bakes: standing {ground.Vertices} vertices (idle/wade/hunt/take-off head/landing tail {string.Join("/", ground.Frames.Select(f => f.Length))} frames), "
            + $"flight {air.Vertices} (take-off tail/flap/glide/landing head {string.Join("/", air.Frames.Select(f => f.Length))}); wade {c.WadeSpeed:0.00} units/s");
        bool finite = ground.Frames.Concat(air.Frames).All(cl => cl.All(f => f.P.All(WildCore.Finite) && f.N.All(WildCore.Finite)));
        var idle = ground.Frames[0][0].P;
        float height = idle.Max(v => v.Y) - idle.Min(v => v.Y);
        float feet = ground.Frames.Take(3).SelectMany(cl => cl).Max(f => MathF.Abs(f.P.Min(v => v.Y)));
        Expect(finite && height is > 19 and < 23 && feet < 0.3f, $"stilt frames finite; {height / 10:0.00} m standing; feet on the ground in idle, wade and hunt (off by at most {feet:0.00} units)");
        // the body's (and legs') lowest point: vertices within 2.5 units of the centre line (the spread wings' tips dip far below the body on a
        // down-stroke; that is the wings moving, not the bird)
        static float Low((Vector3[] P, Vector3[] N) f) => f.P.Where(v => MathF.Abs(v.X) < StiltMesh.BodyHalfWidth).DefaultIfEmpty(Vector3.Zero).Min(v => v.Y);
        float up = MathF.Abs(Low(ground.Frames[3][^1]) - Low(air.Frames[0][0])), down = MathF.Abs(Low(air.Frames[3][^1]) - Low(ground.Frames[4][0]));
        Expect(up < 1.5f && down < 1.5f, $"the two meshes meet at the swaps: take-off {up:0.00} units apart, landing {down:0.00}");
        var mesh = ground.Mesh(2, 40);
        Expect(mesh.SubMeshes[0].VertexData!.ReadPositions()!.Length == ground.Vertices && air.Mesh(1, 3).SubMeshes[0].Indices.Indices.Length == air.Indices.Length, "stilt frame meshes whole");
        Expect(File.Exists(Path.Combine(at, "textures", "ground.png")) && File.Exists(Path.Combine(at, "textures", "air.png")) && File.Exists(Path.Combine(at, "CREDITS.md")),
            "stilt textures and credit shipped");
        string n = StiltMesh.PieceName(1, 3, 12);
        Expect(StiltMesh.Parse(n, out int pm, out int pc, out int pf) && pm == 1 && pc == 3 && pf == 12 && StiltMesh.Is(n)
               && !StiltMesh.Parse("pza.stilt.mesh#2.0.0?pza.stilt.air.png", out _, out _, out _) && !StiltMesh.Parse("pza.stilt.mesh#0.0.0?pza.stilt.air.png", out _, out _, out _)
               && !StiltMesh.Parse("pza.stilt.mesh#0.1?pza.stilt.ground.png", out _, out _, out _) && !FroxMesh.Is(n),
            $"stilt piece names round-trip ({n}); a wrong mesh, texture or part count is refused");
        Expect(StiltCore.Wanted(12) == StiltPreset.DayCount && StiltCore.Wanted(FireflyPreset.DuskHour) == StiltPreset.DayCount + StiltPreset.Twilight && StiltCore.Wanted(double.NaN) == StiltPreset.DayCount,
            $"stilts: {StiltCore.Wanted(12)} by day, {StiltCore.Wanted(FireflyPreset.DuskHour)} at dusk");

        // rules: a lagoon. x < -400 deep water, -400..0 shallows (30 cm of water), land rising from x = 0
        Func<float, float, float> lagoon = (x, z) => x < -400 ? 80 : x < 0 ? 97 : 100 + x * 0.1f;
        var counts = new Dictionary<string, int>();
        var preyAsked = new List<float>();
        var core = new StiltCore(9) { WaterLevel = 100, Clips = c, Count = k => counts[k] = counts.GetValueOrDefault(k) + 1, Prey = (p, r) => { preyAsked.Add(r); return true; } };
        var focus = new Vector3(-150, 100, 0);
        float jump = 0, wrongGround = 0, belowInAir = 0;
        var lastLow = new Dictionary<long, float>();
        var lastDraw = new Dictionary<long, StiltDraw>();
        string worstAt = "";
        var seen = new HashSet<StiltState>();
        for (int i = 0; i < 30 * 300; i++)
        {
            core.Step(1f / 30, focus, 12, true, lagoon, []);
            foreach (var s in core.All)
            {
                seen.Add(s.State);
                if (s.State is StiltState.Idle or StiltState.Wade or StiltState.Hunt)
                    wrongGround = Math.Max(wrongGround, MathF.Abs(s.Pos.Y - lagoon(s.Pos.X, s.Pos.Z)) + (core.Shallow(lagoon(s.Pos.X, s.Pos.Z)) ? 0 : 1000));
                if (s.State is StiltState.Arrive or StiltState.Leave) belowInAir = Math.Max(belowInAir, lagoon(s.Pos.X, s.Pos.Z) - s.Pos.Y);
            }
            foreach (var d in core.Snapshot())
            {
                var asset = d.Mesh == 0 ? ground : air;
                float low = d.Position.Y + Low(asset.Frames[d.Clip][d.Frame]) * d.Scale;
                if (lastLow.TryGetValue(d.Id, out var before) && MathF.Abs(low - before) > jump)
                    (jump, worstAt) = (MathF.Abs(low - before), $"{lastDraw[d.Id].Mesh}.{lastDraw[d.Id].Clip}.{lastDraw[d.Id].Frame} to {d.Mesh}.{d.Clip}.{d.Frame}");
                lastLow[d.Id] = low;
                lastDraw[d.Id] = d;
            }
            foreach (var s in core.All) if (s.State == StiltState.Away) lastLow.Remove(s.Id);
        }
        Expect(seen.IsSupersetOf([StiltState.Arrive, StiltState.Land, StiltState.Idle, StiltState.Wade, StiltState.Hunt, StiltState.TakeOff, StiltState.Leave]),
            "stilts live a whole visit alone: " + string.Join(" ", seen.Order()));
        Expect(wrongGround < 0.01f, $"stilts on the ground stand on the shallows' floor, never on land or in deep water (worst {wrongGround:0.000})");
        Expect(belowInAir <= 0, $"stilts in the air never dip under the ground (worst {belowInAir:0.0} units)");
        Expect(jump < 4, $"the drawn bird's body never jumps between frames at 30 fps, swaps and clip changes included (largest step {jump:0.00} units, {worstAt})");
        Expect(counts.GetValueOrDefault("stilt.strike") > 0 && counts.GetValueOrDefault("stilt.catch") > 0 && preyAsked.All(r => r > 10 && r < 16)
               && counts.GetValueOrDefault("stilt.strike") >= counts.GetValueOrDefault("stilt.catch") * 2,
            $"strikes {counts.GetValueOrDefault("stilt.strike")}, catches {counts.GetValueOrDefault("stilt.catch")} (prey asked within {preyAsked.DefaultIfEmpty().Max():0.0} units)");

        // someone walks up to a landed stilt: it takes off that step and climbs away from them
        var c2 = new StiltCore(5) { WaterLevel = 100, Clips = c };
        StiltCore.Stilt? landed = null;
        for (int i = 0; i < 30 * 120 && landed is null; i++)
        {
            c2.Step(1f / 30, focus, 12, true, lagoon, []);
            landed = c2.All.FirstOrDefault(s => s.State is StiltState.Idle or StiltState.Wade or StiltState.Hunt);
        }
        Expect(landed is not null, "a stilt lands within two minutes");
        if (landed is null) return;
        var walker = new List<Vector3> { landed.Pos + new Vector3(100, 0, 0) };
        c2.Step(1f / 30, focus, 12, true, lagoon, walker);
        var flown = c2.All.First(s => s.Id == landed.Id);
        Expect(flown.State == StiltState.TakeOff, $"someone 10 m off: it takes off at once ({flown.State})");
        var start = flown.Pos;
        for (int i = 0; i < 30 * 6; i++) c2.Step(1f / 30, focus, 12, true, lagoon, walker);
        flown = c2.All.First(s => s.Id == landed.Id);
        Expect(flown.State == StiltState.Leave && flown.Pos.Y > start.Y + 50 && (flown.Pos.X - walker[0].X) < (start.X - walker[0].X) - 100,
            $"then flies off, up {flown.Pos.Y - start.Y:0} units and away from them ({flown.State})");
        c2.Step(1f / 30, focus, 12, false, lagoon, walker);
        Expect(c2.ActiveCount == 0 && c2.Snapshot().Length == 0, "leaving the Swamp clears them");
        var frogs = new FrogCore(3) { WaterLevel = 100 };
        Expect(!frogs.Take(new Vector3(float.NaN, 0, 0), 10) && !frogs.Take(Vector3.Zero, -1) && !new FroxCore(2).Take(Vector3.Zero, 30), "taking prey from nothing takes nothing");
    }

    static void Wings(string kenshi)
    {
        Console.WriteLine("== wing poses and frog poses (0.7.0)");
        // names
        for (int f = 0; f < BirdFold.FlapFrames; f++)
        {
            string marked = BirdFold.PoseName("birdtype1.mesh?birds.dds", BirdFold.FlapPose(f));
            Expect(BirdFold.ParsePose(marked, out var plain, out var pose) && plain == "birdtype1.mesh?birds.dds" && pose == BirdFold.FlapPose(f) && marked.EndsWith("?birds.dds"),
                $"flap frame {f}: {marked} round-trips");
        }
        Expect(BirdFold.ParsePose(BirdFold.PoseName("birdtype1.mesh", 35), out _, out int p35) && p35 == 35 && BirdFold.PoseName("x.mesh", 0) == "x.mesh"
               && !BirdFold.ParsePose("x#pzaflap16", out _, out _) && !BirdFold.ParsePose("x#pzaflap", out _, out _),
            "fold levels round-trip through ParsePose; a frame past the beat or none is refused");
        Expect(BirdFold.Level(0) == 0 && BirdFold.Level(0.01f) == 0 && BirdFold.Level(0.5f) == 50 && BirdFold.Level(0.52f) == 50 && BirdFold.Level(0.53f) == 55
               && BirdFold.Level(7) == 100 && BirdFold.Level(float.NaN) == 0, "fold levels come in steps of 5, clamped");

        // the beat: a turn about the shoulder, the body kept, the tips up at the top of the beat and down at the bottom
        var tip = new Vector3(24, 3, -1);
        var body = new Vector3(1.5f, 2, 4);
        int top = Enumerable.Range(0, BirdFold.FlapFrames).MaxBy(BirdFold.FlapAngle), bottom = Enumerable.Range(0, BirdFold.FlapFrames).MinBy(BirdFold.FlapAngle);
        var up = BirdFold.Flap(tip, BirdFold.FlapAngle(top));
        var down = BirdFold.Flap(tip, BirdFold.FlapAngle(bottom));
        Expect(BirdFold.Flap(body, 0.6f) == body && BirdFold.Flap(tip, 0) == tip && up.Y > tip.Y + 8 && down.Y < tip.Y - 5 && up.X < tip.X && down.X < tip.X
               && BirdFold.Flap(new Vector3(-24, 3, -1), 0.5f).Y == BirdFold.Flap(tip, 0.5f).Y,
            $"flap: the tip goes up {up.Y - tip.Y:0.0} and down {tip.Y - down.Y:0.0} units, the body stays, both wings alike");
        float stepMax = 0;
        for (int f = 0; f < BirdFold.FlapFrames; f++)
            stepMax = Math.Max(stepMax, Vector3.Distance(BirdFold.Flap(tip, BirdFold.FlapAngle(f)), BirdFold.Flap(tip, BirdFold.FlapAngle(f + 1))));
        Expect(stepMax < 6.5f, $"flap: the tip moves at most {stepMax:0.0} units between frames (16 frames, no jumps)");
        Expect(BirdFold.FlapAt(0, 3) == BirdFold.FlapPose(0) && BirdFold.FlapAt(1f / 3 - 1e-3f, 3) == BirdFold.FlapPose(BirdFold.FlapFrames - 1)
               && BirdFold.FlapAt(float.NaN, 3) == 0 && BirdFold.FlapAt(1, 0) == 0, "flap timing: one beat runs through every frame; bad input glides");

        // a held bird's poses over its life: no snap bigger than 2 fold steps from frame to frame while it lands, settles, hops and takes off
        var b = new WildBird { Phase = Phase.Approach, Length = 3 };
        var seq = new List<int>();
        for (float t = 0; t < 3; t += 1f / 60) { b.Clock = t; seq.Add(b.WingPose); }
        Expect(seq.Take(60).All(x => x == 0) && seq.Any(x => x > 100) && seq[^1] >= 45 && seq[^1] <= 50,
            $"approach: glides, then beats to brake, then folds half way as it touches down (ends at {seq[^1]})");
        b.Phase = Phase.Peck; b.Action = 0;
        var settle = new List<int>();
        for (float t = 0; t < 0.6f; t += 1f / 60) { b.Clock = t; settle.Add(b.WingPose); }
        Expect(settle.Zip(settle.Skip(1)).All(z => z.Second >= z.First && z.Second - z.First <= 10) && settle[0] <= 55 && settle[^1] == 100,
            $"landing: the fold finishes on the ground in steps ({string.Join(",", settle.Distinct())})");
        b.Clock = 5; b.Action = 2;
        var hop = new List<int>();
        for (float t = 0; t <= Preset.HopSeconds; t += 1f / 60) { b.ActionClock = t; hop.Add(b.WingPose); }
        Expect(hop.Min() == 50 && hop[0] == 100 && hop.Zip(hop.Skip(1)).All(z => Math.Abs(z.Second - z.First) <= 10),
            $"hop: the wings open half way and close again smoothly ({string.Join(",", hop.Distinct())})");
        b.Phase = Phase.Flee; b.Delay = 0.1f;
        var off = new List<int>();
        for (float t = 0; t < 1.2f; t += 1f / 60) { b.Clock = t; off.Add(b.WingPose); }
        var opening = off.Where(x => x is > 0 and <= 100).ToList();
        Expect(off[0] == 100 && opening.Zip(opening.Skip(1)).All(z => z.Second <= z.First) && opening.Distinct().Count() >= 3 && off.Skip(30).All(x => x > 100),
            $"take-off: the wings open over {opening.Distinct().Count()} steps, then it beats as it climbs");
        b.Phase = Phase.Gather;
        int beats = 0;
        for (float t = 0; t < 12; t += 1f / 30) { b.Clock = t; if (b.WingPose > 100) beats++; }
        Expect(beats > 0 && beats < 360 / 2, $"high circle: beats in bursts ({beats} of 360 frames), glides the rest");

        // free birds
        int climbing = 0, level = 0;
        for (double t = 0; t < 30; t += 1.0 / 30)
        {
            if (BirdFold.FreePose(77, t, 0.3f) > 100) climbing++;
            if (BirdFold.FreePose(77, t, 0) > 100) level++;
        }
        Expect(climbing == 900 && level > 30 && level < 300 && BirdFold.FreePose(77, double.NaN, 0) == 0 && BirdFold.FreePose(77, 5, float.NaN) == 0,
            $"free birds: always beat while climbing, in bursts on the level ({level} of 900 frames)");
        int together = 0;
        for (double t = 0; t < 30; t += 1.0 / 30) if ((BirdFold.FreePose(1, t, 0) > 100) && (BirdFold.FreePose(2, t, 0) > 100)) together++;
        Expect(together < 60, $"free birds: two birds rarely beat together ({together} frames)");

        // frogs
        Expect(FrogPreset.Pose(0) == 0 && FrogPreset.Pose(1) == FrogPreset.Poses - 1 && FrogPreset.Pose(float.NaN) == 0 && FrogPreset.Pose(9) == FrogPreset.Poses - 1
               && FrogPreset.Stretch(FrogState.Sit, 3, 0) == 0 && FrogPreset.Stretch(FrogState.Leap, -0.1f, 0.5f) == 0 && FrogPreset.Stretch(FrogState.Leap, 0.25f, 0.5f) == 1
               && FrogPreset.Stretch(FrogState.Dive, 0, 0.25f) == 1, "frog poses: tucked sitting and in the startle, stretched in the air and the dive");
        var leap = new List<int>();
        for (float t = 0; t <= 0.5f; t += 1f / 60) leap.Add(FrogPreset.Pose(FrogPreset.Stretch(FrogState.Leap, t, 0.5f)));
        Expect(leap.Zip(leap.Skip(1)).All(z => Math.Abs(z.Second - z.First) <= 1) && leap.Max() == FrogPreset.Poses - 1,
            $"frog leap: one pose step a frame at most ({string.Join(",", leap.Distinct())})");
        var box = (min: new Vector3(-1, 0, -1), max: new Vector3(1, 1, 1));
        var heel = new Vector3(0.5f, 0.1f, -0.9f);
        var crown = new Vector3(0, 1, 0.9f);
        var heelOut = FrogMesh.Shape(heel, 1, box.min, box.max);
        Expect(FrogMesh.Shape(heel, 0, box.min, box.max) == heel && heelOut.Z < heel.Z - 0.6f && FrogMesh.Shape(crown, 1, box.min, box.max).Y < crown.Y
               && FrogMesh.Shape(new Vector3(float.NaN, 0, 0), 1, box.min, box.max) is var nan && float.IsNaN(nan.X),
            $"frog shape: the hind legs reach back {heel.Z - heelOut.Z:0.00} of the box when stretched, the body flattens, tucked moves nothing");
        Expect(FrogMesh.PoseOf(FrogMesh.PoseName(3)) == 3 && FrogMesh.PoseOf(FrogMesh.PoseName(99)) == FrogPreset.Poses - 1 && FrogMesh.PoseOf("pza.frog.mesh#x?y") == 0
               && FrogMesh.Is(FrogMesh.PoseName(2)), "frog pose names round-trip");
        var frogs = new FrogCore(5) { WaterLevel = 100 };
        Func<float, float, float> shore = (x, z) => 100 + x * 0.12f;
        for (int i = 0; i < 400; i++) frogs.Step(0.1f, new Vector3(0, 100, 0), 23.0, true, shore, []);
        float maxTurn = 0;
        var yaw = frogs.Frogs.ToDictionary(f => f.Id, f => f.Yaw);
        for (int i = 0; i < 1200; i++)
        {
            frogs.Step(1f / 60, new Vector3(0, 100, 0), 23.0, true, shore, []);
            foreach (var f in frogs.Frogs)
            {
                if (f.State == FrogState.Sit && yaw.TryGetValue(f.Id, out var y0)) maxTurn = Math.Max(maxTurn, MathF.Abs(f.Yaw - y0));
                yaw[f.Id] = f.Yaw;
            }
        }
        Expect(maxTurn < 0.15f, $"frog turns ease (at most {maxTurn:0.000} rad a frame, a 1.2 rad turn would snap in one)");

        // the Gorillo as the frog
        string gorillo = Path.Combine(kenshi, "data", "animal", "meshes", FrogMesh.Source);
        if (!File.Exists(gorillo))
        {
            if (Environment.GetEnvironmentVariable("PZA_NO_KENSHI") == "1") Console.WriteLine("skip the Gorillo frog: no Kenshi install here (CI)");
            else Expect(false, "the game's Gorillo is there to shrink: " + gorillo);
            return;
        }
        Vector3 tucked = default;
        for (int pose = 0; pose < FrogPreset.Poses; pose++)
        {
            var ogre = Meitou.Data.Ogre.OgreMeshReader.ReadFile(gorillo);
            var a = Bounds(ogre);
            int moved = FrogMesh.FromGorillo(ogre, pose / (float)(FrogPreset.Poses - 1));
            var g = Bounds(ogre);
            if (pose == 0) tucked = g.Size;
            Console.WriteLine($"  Gorillo frog pose {pose}: {moved} vertices, {g.Size.X:0.00} x {g.Size.Y:0.00} x {g.Size.Z:0.00} units");
            Expect(moved > 0 && g.Finite && g.Count == a.Count && MathF.Abs(g.Min.Y) < 1e-3f && g.Size.Y < tucked.Y + 1e-3f
                   && (pose == 0 ? MathF.Abs(g.Size.Z - FrogMesh.Length) < 1e-3f : g.Size.Z > tucked.Z),
                $"Gorillo frog pose {pose}: every vertex finite, feet at 0, {(pose == 0 ? "2.1 units long" : "longer than tucked")}");
        }
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

    sealed record Box(Vector3 Size, bool Finite, int Count, Vector3 Min = default);

    static Box Bounds(Meitou.Data.Ogre.OgreMesh m)
    {
        var ps = new[] { m.SharedVertexData }.Concat(m.SubMeshes.Select(s => s.VertexData)).Where(d => d is not null).Distinct()
            .SelectMany(d => d!.ReadPositions() ?? []).ToList();
        var min = ps.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = ps.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        return new Box(max - min, ps.All(WildCore.Finite), ps.Count, min);
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
        float savedWater = Driver.Core.WaterLevel;
        Driver.Core.WaterLevel = float.NegativeInfinity;
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
        Driver.Core.WaterLevel = savedWater;
        Driver.Core.Reset();
        s_harmony.UnpatchAll(s_harmony.Id);
    }

    #endregion
}
