// PZA's Wildlife for Meitou: the fuzz harness. Author: PZA. Licence: GPLv3 or later. Never shipped; it never starts the game.
//   settings.keep / settings.roundtrip / strings.load   the shared PZA's Meitou UI targets on WildSettings and Strings.Table
//   core.flight   WildCore over random worlds: steep or cliffed terrain, 0-10 corpses (fresh, old, moved, gone, nasty places and ages), 0-60
//                 birds (nasty places, headings, speeds, scales), people walking in and out, nasty step lengths (NaN, negative, 0, huge).
//                 Invariants: no NaN place or heading, a unit heading, never below the ground, a landed (or gliding-in) bird with a person
//                 within the flee radius is fleeing after its step, a corpse never holds more than its cap at a claim, never more than
//                 MaxBirds held, a non-finite bird is never taken, the rules' clock stays finite, a bird let go has a finite place and a heading the game can fly on from (not steeply down).
//   fold          BirdFold (folded wings): finite, never wider, still where it must be; marked names round-trip, Parse never throws.
//   driver.near   Driver.Near: at most 32 corpses, all within reach of the eye and the nearest ones, every person kept is near a kept corpse.
using System.Numerics;
using PZA.MeitouUi.Fuzz;
using PZA.Wildlife.Meitou;

static class Program
{
    static int Main(string[] a) => PzaFuzz.Run("PZA's Wildlife for Meitou", a,
    [
        .. PzaFuzzUi.Settings("pza.wildlife", () => new WildSettings()),
        PzaFuzzUi.Strings(Strings.Table),
        new("core.flight", 1500, Flight, fz =>
        {
            foreach (var rule in new[] { "claim.fresh", "descend", "approach", "land", "peck.dip", "peck.hop", "peck.turn", "scatter", "flee.scared",
                                         "flee.calm", "release", "release.corpse_gone", "descend.aborted" })
                fz.Expect(fz.Counted("rule." + rule) > 0, $"rule '{rule}' never fired in {fz.Case + 1} cases (a rule that never fires is a failed test)");
        }),
        new("driver.near", 5000, Near),
        new("fold", 20000, FoldTarget),
    ]);

    /// <summary>BirdFold: a folded vertex is finite and never wider than it was; nothing moves at amount 0 or inside the shoulder; a marked
    /// name round-trips with its level and the texture mark after it; Parse never throws on any text and refuses levels outside 0..100.</summary>
    static void FoldTarget(PzaFuzz fz)
    {
        var p = new Vector3(fz.Float(-100, 100), fz.Float(-100, 100), fz.Float(-100, 100));
        float amount = fz.Chance(0.2) ? fz.Float() : (float)fz.Range(0, 1);
        var f = BirdFold.Fold(p, amount);
        if (WildCore.Finite(p))
        {
            fz.Expect(WildCore.Finite(f), $"Fold({p}, {amount}) = {f}");
            fz.Expect(MathF.Abs(f.X) <= MathF.Abs(p.X) + 1e-3f * Math.Max(1, MathF.Abs(p.X)), $"Fold({p}, {amount}) widened the bird: {f}");
            if (amount <= 0 || MathF.Abs(p.X) <= BirdFold.Shoulder) fz.Expect(f == p, $"Fold({p}, {amount}) moved a vertex it must keep: {f}");
        }
        string name = fz.Chance(0.5) ? fz.Word(20) + ".mesh" + (fz.Chance(0.5) ? "?" + fz.Word(12) : "") : fz.Text(60);
        try
        {
            int level = fz.Int(-5, 120);
            bool ok = BirdFold.Parse(name, out var plain, out var got);
            if (!name.Contains(BirdFold.Marker))
            {
                fz.Expect(!ok, $"Parse({name}) found a fold without the marker");
                string marked = BirdFold.Name(name, level);
                bool back = BirdFold.Parse(marked, out var plain2, out var got2);
                fz.Expect(back == (level is >= 0 and <= 100) && (!back || (plain2 == name && got2 == level)),
                    $"Name/Parse({name}, {level}) -> {marked} -> {back} {plain2} {got2}");
            }
            else if (ok) fz.Expect(got is >= 0 and <= 100 && !plain.Contains(BirdFold.Marker + got), $"Parse({name}) -> {plain} {got}");
        }
        catch (Exception e)
        {
            fz.Expect(false, $"Parse/Name threw on '{PzaFuzzUi.Short(name)}': {e.GetType().Name}");
        }
    }

    /// <summary>Corpse keys are unique, as the game's character ids are.</summary>
    static long s_key;

    /// <summary>A random terrain: smooth, steep (short waves, big heights) or with cliffs; always finite.</summary>
    static Func<float, float, float> Terrain(PzaFuzz fz)
    {
        float a1 = (float)fz.Range(0, fz.Chance(0.3) ? 3000 : 200), a2 = (float)fz.Range(0, 400), w1 = (float)fz.Range(20, 3000), w2 = (float)fz.Range(20, 3000);
        float baseY = (float)fz.Range(-5000, 5000), cliff = fz.Chance(0.25) ? (float)fz.Range(50, 800) : 0, cliffAt = (float)fz.Range(-500, 500);
        return (x, z) =>
        {
            float g = baseY + a1 * MathF.Sin(x / w1) + a2 * MathF.Cos(z / w2) + (x > cliffAt ? cliff : 0);
            return float.IsFinite(g) ? g : baseY;
        };
    }

    static Vector3 Near(PzaFuzz fz, Vector3 around, float spread) =>
        fz.Chance(0.03) ? new Vector3(fz.Float(), fz.Float(), fz.Float()) : around + new Vector3((float)fz.Range(-spread, spread), (float)fz.Range(-200, 900), (float)fz.Range(-spread, spread));

    static float Dt(PzaFuzz fz) => fz.Below(20) switch
    {
        0 => float.NaN,
        1 => -(float)fz.Range(0, 1),
        2 => 0,
        3 => (float)fz.Range(1, 1e6),
        4 => float.PositiveInfinity,
        _ => (float)fz.Range(1 / 144.0, 0.25),
    };

    static void Flight(PzaFuzz fz)
    {
        var ground = Terrain(fz);
        var core = new WildCore((int)fz.Below(1 << 30)) { Ground = ground, Count = r => fz.Count("rule." + r) };
        var origin = new Vector3((float)fz.Range(-1e5, 1e5), 0, (float)fz.Range(-1e5, 1e5));

        var corpses = new List<Corpse>();
        for (int n = fz.Int(0, 10); n > 0; n--)
        {
            var p = Near(fz, origin, 3000);
            if (WildCore.Finite(p)) p.Y = ground(p.X, p.Z);
            corpses.Add(new Corpse(++s_key, p, fz.Chance(0.1) ? fz.Float() : (float)fz.Range(0, Preset.OldAge * 2), fz.Chance(0.3)));
        }
        var birds = new List<object>();
        var free = new Dictionary<object, (Vector3 P, Vector3 F)>(ReferenceEqualityComparer.Instance);
        for (int n = fz.Int(0, 60); n > 0; n--)
        {
            var h = new object();
            birds.Add(h);
            var f = fz.Chance(0.05) ? new Vector3(fz.Float(), fz.Float(), fz.Float()) : new Vector3((float)fz.Range(-1, 1), (float)fz.Range(-1, 1), (float)fz.Range(-1, 1));
            free[h] = (Near(fz, origin, 8000), f);
        }
        var threats = new List<Vector3>();
        float speed = fz.Chance(0.05) ? fz.Float() : (float)fz.Range(150, 600), scale = fz.Chance(0.05) ? fz.Float() : (float)fz.Range(0.2, 3);

        for (int step = fz.Int(50, 700); step > 0; step--)
        {
            Mutate(fz, corpses, threats, ground, origin);
            core.SetWorld(corpses, threats);
            float dt = Dt(fz);
            core.Advance(dt);
            fz.Expect(float.IsFinite(core.Now), $"the rules' clock is {core.Now} after a step of {dt} s (cooldowns would never end)");
            foreach (var h in birds)
            {
                if (!core.TryGet(h, out var b))
                {
                    var (p, f) = free[h];
                    if (core.TryClaim(h, p, f, speed, scale, dt) is not { } got) continue;
                    fz.Expect(WildCore.Finite(p) && WildCore.Finite(f), $"a bird with a non-finite place or heading was taken ({p}, {f})");
                    var c = corpses.First(k => k.Key == got.Corpse);
                    fz.Expect(core.TakenBy(got.Corpse) <= WildCore.Cap(c) && core.TakenBy(got.Corpse) <= Preset.FreshCap,
                        $"corpse {got.Corpse} holds {core.TakenBy(got.Corpse)} birds, cap {WildCore.Cap(c)}");
                    fz.Expect(core.HeldCount <= Preset.MaxBirds, $"{core.HeldCount} birds held, most {Preset.MaxBirds}");
                    continue;
                }
                bool mustFlee = b.Phase is Phase.Peck or Phase.Approach && core.ThreatNear(b.Position) is not null;
                bool kept = core.Step(h, b, dt);
                CheckBird(fz, b, ground, kept);
                if (mustFlee) fz.Expect(!kept || b.Phase == Phase.Flee, $"a landed bird with a person within {Preset.FleeRadius} is {b.Phase} after its step");
                if (!kept) free[h] = (b.Position, b.Forward);
            }
        }
    }

    /// <summary>The world changes under the birds: people come and go, corpses age, move or disappear, new ones fall.</summary>
    static void Mutate(PzaFuzz fz, List<Corpse> corpses, List<Vector3> threats, Func<float, float, float> ground, Vector3 origin)
    {
        if (fz.Chance(0.02) && corpses.Count > 0) corpses.RemoveAt(fz.Below(corpses.Count));
        if (fz.Chance(0.01))
        {
            var p = Near(fz, origin, 3000);
            if (WildCore.Finite(p)) p.Y = ground(p.X, p.Z);
            corpses.Add(new Corpse(++s_key, p, 0, false));
        }
        if (fz.Chance(0.01) && corpses.Count > 0)
        {
            int i = fz.Below(corpses.Count);
            corpses[i] = corpses[i] with { Position = corpses[i].Position + new Vector3((float)fz.Range(-100, 100), 0, (float)fz.Range(-100, 100)) };
        }
        if (fz.Chance(0.03))
        {
            threats.Clear();
            if (corpses.Count > 0 && fz.Chance(0.7))
                for (int n = fz.Int(1, 4); n > 0; n--)
                {
                    var c = fz.Pick(corpses).Position;
                    threats.Add(fz.Chance(0.05) ? new Vector3(fz.Float(), fz.Float(), fz.Float()) : c + new Vector3((float)fz.Range(-400, 400), 0, (float)fz.Range(-400, 400)));
                }
        }
    }

    static void CheckBird(PzaFuzz fz, WildBird b, Func<float, float, float> ground, bool kept)
    {
        fz.Expect(WildCore.Finite(b.Position) && WildCore.Finite(b.Forward), $"non-finite bird {b.Position} {b.Forward} in {b.Phase}");
        fz.Expect(MathF.Abs(b.Forward.Length() - 1) < 1e-3f, $"heading {b.Forward} is not a unit ({b.Phase})");
        float g = ground(b.Position.X, b.Position.Z);
        fz.Expect(b.Position.Y >= g - Math.Max(1e-3f, MathF.Abs(g) * 1e-6f), $"bird {b.Position.Y - g:0.###} units below the ground in {b.Phase}");
        if (!kept) fz.Expect(b.Forward.Y >= -0.21f, $"let go heading down steeply ({b.Forward})");
    }

    static void Near(PzaFuzz fz)
    {
        var eye = new Vector3((float)fz.Range(-1e5, 1e5), (float)fz.Range(-1e3, 1e4), (float)fz.Range(-1e5, 1e5));
        var corpses = new List<Corpse>();
        for (int n = fz.Int(0, 80); n > 0; n--) corpses.Add(new Corpse(n, Near(fz, eye, 40000), 0, false));
        var threats = new List<Vector3>();
        for (int n = fz.Int(0, 200); n > 0; n--) threats.Add(Near(fz, eye, 40000));
        var (near, people) = Driver.Near(new Driver.Input(corpses, threats), eye);

        static float Flat2(Vector3 v) => v.X * v.X + v.Z * v.Z;
        fz.Expect(near.Count <= 32, $"{near.Count} corpses kept");
        fz.Expect(near.All(c => Flat2(c.Position - eye) <= Preset.EyeReach * Preset.EyeReach), "a kept corpse beyond the eye's reach");
        var inReach = corpses.Where(c => Flat2(c.Position - eye) <= Preset.EyeReach * Preset.EyeReach).ToList();
        fz.Expect(near.Count == Math.Min(32, inReach.Count), $"{near.Count} kept of {inReach.Count} in reach");
        if (near.Count > 0 && inReach.Count > near.Count)
        {
            float worstKept = near.Max(c => Flat2(c.Position - eye));
            fz.Expect(inReach.Except(near).All(c => Flat2(c.Position - eye) >= worstKept), "a nearer corpse was dropped for a farther one");
        }
        fz.Expect(people.All(t => near.Any(c => Flat2(t - c.Position) < 800 * 800)), "a person kept far from every kept corpse");
    }
}
