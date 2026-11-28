// PZA's Wildlife for Meitou: the fuzz harness. Author: PZA. Licence: GPLv3 or later. Never shipped; it never starts the game.
//   settings.keep / settings.roundtrip / strings.load   the shared PZA's Meitou UI targets on WildSettings and Strings.Table
//   core.flight   WildCore over random worlds: steep or cliffed terrain, 0-10 corpses (fresh, old, moved, gone, nasty places and ages), 0-60
//                 birds (nasty places, headings, speeds, scales), people walking in and out, nasty step lengths (NaN, negative, 0, huge).
//                 Invariants: no NaN place or heading, a unit heading, never below the ground, a landed (or gliding-in) bird with a person
//                 within the flee radius is fleeing after its step, a corpse never holds more than its cap at a claim, never more than
//                 MaxBirds held, a non-finite bird is never taken, the rules' clock stays finite, a bird let go has a finite place and a heading the game can fly on from (not steeply down).
//   fold          BirdFold (folded wings): finite, never wider, still where it must be; marked names round-trip, Parse never throws.
//   wings         0.7.0 wing and frog poses: every pose the drawing knows, the beat never swings a wing through the body, a landing, settle, hop
//                 and take-off change the fold by at most 500 levels a second (plus one step), frog poses in range and finite shapes, pose names round-trip.
//   frox          0.8.0 FroxCore over random shores, hours, people and steps: finite, never more than the night count, colours from the pool, a
//                 run never faster than its run's ground speed, frames in range, splashes capped; FroxAsset.Read on cut and flipped bakes only
//                 refuses (InvalidDataException) and never returns an index past its vertices; frog animation names stay in range.
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
                                         "flee.calm", "release", "release.corpse_gone", "descend.aborted", "bite", "bite.dropped" })
                fz.Expect(fz.Counted("rule." + rule) > 0, $"rule '{rule}' never fired in {fz.Case + 1} cases (a rule that never fires is a failed test)");
        }),
        new("driver.near", 5000, Near),
        new("fold", 20000, FoldTarget),
        new("wings", 20000, WingsTarget),
        new("frox", 2000, FroxTarget, fz => fz.Expect(fz.Counted("rule.frox.flee") > 0 && fz.Counted("rule.frox.rest") > 0 && fz.Counted("rule.frox.splash") > 0 && fz.Counted("rule.frox.swim_away") > 0, "froxlotls never rested, fled, splashed or swam away")),
        new("firefly.blink", 20000, BlinkTarget),
        new("night.sounds", 20000, NightTarget),
        new("frogs", 3000, FrogTarget, fz => fz.Expect(fz.Counted("rule.frogs.leap") > 0 && fz.Counted("rule.frogs.sit") > 0, "frogs never sat or leapt")),
        new("fireflies", 5000, FireflyTarget, fz =>
        {
            fz.Expect(fz.Counted("rule.fireflies.step") > 0, "rule 'fireflies.step' never fired");
        }),
    ]);

    static void FireflyTarget(PzaFuzz fz)
    {
        var ground = Terrain(fz);
        float water = fz.Chance(0.5) ? 100f : float.NegativeInfinity;
        var core = new FireflyCore { WaterLevel = water, Count = r => fz.Count("rule." + r) };
        var focus = new Vector3((float)fz.Range(-5e4, 5e4), (float)fz.Range(-100, 1000), (float)fz.Range(-5e4, 5e4));
        for (int step = fz.Int(5, 30); step > 0; step--)
        {
            float dt = Dt(fz);
            double hour = fz.Chance(0.1) ? fz.Float() : fz.Range(-48, 72);
            bool inSwamp = fz.Chance(0.6);
            float rain = fz.Chance(0.2) ? (float)fz.Range(0.85, 1.0) : (float)fz.Range(0.0, 0.3);
            core.Enabled = fz.Chance(0.85);
            var f = fz.Chance(0.05) ? new Vector3(fz.Float(), fz.Float(), fz.Float()) : focus + new Vector3((float)fz.Range(-500, 500), 0, (float)fz.Range(-500, 500));
            float dist = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-10, 100);
            float nf = FireflyCore.NearFade(dist);
            fz.Expect(float.IsFinite(nf) && nf >= 0f && nf <= 1f, $"NearFade({dist})={nf} out of 0..1");
            if (float.IsFinite(dist) && dist <= FireflyPreset.NearFadeStart) fz.Expect(nf == 0f, $"NearFade({dist})={nf} should be 0 within {FireflyPreset.NearFadeStart}");
            if (float.IsFinite(dist) && dist >= FireflyPreset.NearFadeEnd) fz.Expect(nf == 1f, $"NearFade({dist})={nf} should be 1 beyond {FireflyPreset.NearFadeEnd}");
            core.Step(dt, f, hour, inSwamp, ground, rain);
            var snap = core.Snapshot();
            fz.Expect(core.ActiveCount == snap.Length && snap.Length <= FireflyPreset.MaxCount, $"fireflies count {core.ActiveCount}/{snap.Length} exceeds {FireflyPreset.MaxCount}");
            double h = double.IsFinite(hour) ? ((hour % 24.0) + 24.0) % 24.0 : double.NaN;
            bool midday = double.IsFinite(h) && h >= FireflyPreset.DawnHour + FireflyPreset.FadeHours * 0.5 && h <= FireflyPreset.DuskHour - FireflyPreset.FadeHours * 0.5;
            if (midday) fz.Expect(snap.Length == 0, $"fireflies active ({snap.Length}) in daytime at hour={h:0.00}");
            float nightNow = core.Enabled && inSwamp ? FireflyCore.NightFactor(hour) : 0f;
            foreach (var fly in snap)   // rain thins the flies, never dims one: each shines between its ember and its full flash at this hour
                fz.Expect(fly.Brightness >= FireflyPreset.Ember * nightNow - 1e-4f && fly.Brightness <= nightNow + 1e-4f,
                    $"fly brightness {fly.Brightness} outside {FireflyPreset.Ember * nightNow}..{nightNow} (night {nightNow}, rain {rain})");
            int rainCap = (int)MathF.Round(FireflyPreset.MaxCount * FireflyPreset.RainFloor);
            if (rain >= FireflyPreset.RainFadeEnd) fz.Expect(snap.Length <= rainCap, $"fireflies active ({snap.Length}) exceeds rain floor cap ({rainCap}) in heavy rain={rain:0.00}");
            float factor = core.Enabled && inSwamp ? FireflyCore.NightFactor(hour) * FireflyCore.RainFactor(rain) : 0f;
            if (factor <= 0.02f || !WildCore.Finite(f))
            {
                fz.Expect(snap.Length == 0, $"fireflies active ({snap.Length}) when factor={factor} or non-finite focus");
                continue;
            }
            foreach (var d in snap)
            {
                fz.Expect(WildCore.Finite(d.Position) && float.IsFinite(d.Size) && float.IsFinite(d.Brightness), $"non-finite firefly {d}");
                float g = ground(d.Position.X, d.Position.Z);
                float surf = float.IsFinite(water) && g < water ? water : g;
                fz.Expect(d.Position.Y >= surf + 0.9f, $"firefly {d.Position.Y - surf:0.###} too close/below surface");
                fz.Expect(d.Size >= FireflyPreset.MinSize - 1e-4f && d.Size <= FireflyPreset.MaxSize + 1e-4f, $"firefly size {d.Size} out of range");
                fz.Expect(d.Brightness >= 0f && d.Brightness <= 1.001f, $"firefly brightness {d.Brightness} out of range");
            }
        }
    }

    /// <summary>FrogCore over random shores, hours, people and step lengths: finite, never more than the night count, a sitting frog never
    /// under the ground or the water line, a sitting frog with someone within the flee radius is leaping after the step, splashes capped.</summary>
    static void FrogTarget(PzaFuzz fz)
    {
        var focus = new Vector3((float)fz.Range(-5e4, 5e4), 0, (float)fz.Range(-5e4, 5e4));
        var ground = Terrain(fz);
        if (fz.Chance(0.7))   // a shoreline through the focus at a random angle, with ripples: where frogs actually sit
        {
            float a = (float)fz.Range(0, MathF.Tau), slope = (float)fz.Range(0.02, 0.5), ripple = (float)fz.Range(0, 6), fx = focus.X, fz0 = focus.Z;
            ground = (x, z) => 100 + slope * ((x - fx) * MathF.Cos(a) + (z - fz0) * MathF.Sin(a)) + ripple * MathF.Sin(x * 0.05f) * MathF.Cos(z * 0.07f);
        }
        var core = new FrogCore((int)fz.Below(1 << 30)) { WaterLevel = fz.Chance(0.9) ? 100 : (float)fz.Range(-500, 500), Count = r => fz.Count("rule." + r) };
        var people = new List<Vector3>();
        bool drainOften = fz.Chance(0.5);   // else the splashes pile up until the end (the queue's cap must hold)
        float move = drainOften ? 0.05f : 0.3f;
        for (int step = fz.Int(20, drainOften ? 300 : 1200); step > 0; step--)
        {
            if (fz.Chance(move))
            {
                people.Clear();
                for (int n = fz.Int(0, 4); n > 0; n--)
                    people.Add(fz.Chance(0.05) ? new Vector3(fz.Float(), fz.Float(), fz.Float()) : focus + new Vector3((float)fz.Range(-300, 300), 0, (float)fz.Range(-300, 300)));
            }
            if (!drainOften && fz.Chance(0.3))   // someone walks right up to the frogs: they keep diving while the splashes pile up
            {
                people.Clear();
                foreach (var f in core.Frogs.Where(f => f.State == FrogState.Sit)) people.Add(f.Pos + new Vector3(20, 0, 0));
            }
            var before = core.Frogs.Where(f => f.State is FrogState.Sit or FrogState.Hop).Select(f => (f.Id, Scared: people.Any(p => WildCore.Finite(p)
                && new Vector2(p.X - f.Pos.X, p.Z - f.Pos.Z).LengthSquared() < FrogPreset.FleeRadius * FrogPreset.FleeRadius))).ToList();
            double hour = !drainOften ? 23.0 : fz.Chance(0.1) ? fz.Double() : fz.Range(-24, 48);
            var at = fz.Chance(0.03) ? new Vector3(fz.Float(), 0, fz.Float()) : focus;
            core.Step(Dt(fz), at, hour, fz.Chance(0.9), ground, people);
            var frogs = core.Frogs;
            fz.Expect(frogs.Count <= FrogPreset.NightCount, $"{frogs.Count} frogs, most {FrogPreset.NightCount}");
            foreach (var f in frogs.Where(f => f.State != FrogState.Away))
            {
                fz.Expect(WildCore.Finite(f.Pos) && float.IsFinite(f.Yaw) && float.IsFinite(f.Pitch), $"non-finite frog {f.Pos} {f.Yaw} in {f.State}");
                if (f.State == FrogState.Sit)
                {
                    float g = ground(f.Pos.X, f.Pos.Z);
                    fz.Expect(f.Pos.Y >= Math.Max(g, core.WaterLevel) - 1e-3f, $"a sitting frog {f.Pos.Y - Math.Max(g, core.WaterLevel)} under the ground or water");
                }
            }
            foreach (var (id, scared) in before.Where(b => b.Scared))
                if (frogs.FirstOrDefault(f => f.Id == id) is { } f)
                    fz.Expect(f.State is FrogState.Leap or FrogState.Dive or FrogState.Away, $"a frog with someone within {FrogPreset.FleeRadius} is {f.State}");
            if (drainOften && fz.Chance(0.05)) fz.Expect(core.TakeSplashes().Count <= FrogPreset.SplashQueue, "splash queue over its cap");
        }
        fz.Expect(core.TakeSplashes().Count <= FrogPreset.SplashQueue, "splash queue over its cap");
    }

    /// <summary>NightSounds on nasty hours and volumes: the audio's sunset is never later than the game's and stays after sunrise; a lifted
    /// sound is never quieter than rolled, and at most CallFloor - rolled + Lift louder.</summary>
    static void NightTarget(PzaFuzz fz)
    {
        double sunrise = fz.Chance(0.1) ? fz.Double() : fz.Range(0, 12), sunset = fz.Chance(0.1) ? fz.Double() : fz.Range(0, 30);
        double s = NightSounds.Sunset(sunset, sunrise);
        if (double.IsFinite(sunset) && double.IsFinite(sunrise))
        {
            fz.Expect(s <= sunset, $"Sunset({sunset}, {sunrise}) = {s}: later than the game's");
            if (sunset > sunrise) fz.Expect(s > sunrise, $"Sunset({sunset}, {sunrise}) = {s}: not after sunrise (no day left)");
        }
        float rolled = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-96, 6);
        float lifted = NightSounds.LiftDb(rolled);
        if (float.IsFinite(rolled))
        {
            fz.Expect(float.IsFinite(lifted) && lifted >= rolled, $"LiftDb({rolled}) = {lifted}: quieter or not finite");
            fz.Expect(lifted <= Math.Max(rolled, NightSoundsPreset.CallFloor) + NightSoundsPreset.Lift + 1e-4f, $"LiftDb({rolled}) = {lifted}: lifted too far");
        }
    }

    /// <summary>FireflyCore.Blink on nasty times, periods and phases: always finite, never below the ember, never above 1.</summary>
    static void BlinkTarget(PzaFuzz fz)
    {
        float t = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(0, 1e5), period = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(0.01, 10);
        float phase = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-10, 10);
        float b = FireflyCore.Blink(t, period, phase);
        fz.Expect(float.IsFinite(b) && b >= FireflyPreset.Ember - 1e-5f && b <= 1 + 1e-5f, $"Blink({t}, {period}, {phase}) = {b}");
    }

    static float SafeGround(Func<float, float, float> ground, float x, float z)
    {
        try { return ground(x, z); }
        catch { return float.NaN; }
    }

    static byte[]? s_bake;

    /// <summary>The shipped bake (read once from the repo), for the reader's target.</summary>
    static byte[] Bake([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        if (s_bake is not null) return s_bake;
        // above the harness's own folder, then above this source file (the mutation check builds a copy elsewhere)
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "", Environment.CurrentDirectory })
            for (var d = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "mod", "assets", "froxlotl", "froxlotl.pzaf");
                if (File.Exists(p)) return s_bake = File.ReadAllBytes(p);
            }
        throw new FileNotFoundException("froxlotl.pzaf not found above " + AppContext.BaseDirectory + " or " + source);
    }

    /// <summary>0.8.0 froxlotls (see the header).</summary>
    /// <summary>A lagoon shallower than the swim depth: a bank rising east of x = 0, deep holes just off it (so froxlotls sit and dive in),
    /// and water only <paramref name="depth"/> deep beyond: a swimmer has to keep off the bottom.</summary>
    static Func<float, float, float> Shallows(float depth) => (x, z) =>
    {
        if (x > 0) return 99 + 0.2f * x;
        float dz = ((z % 20) + 20) % 20 - 10, dx = x + 4;
        return dx * dx + dz * dz < 9 ? 94 : 100 - depth;
    };

    static void FroxTarget(PzaFuzz fz)
    {
        bool lagoon = fz.Chance(0.3);
        var ground = lagoon ? Shallows((float)fz.Range(0.35, 1.7)) : Terrain(fz);
        var core = new FroxCore((int)fz.Below(1 << 30)) { WaterLevel = lagoon || fz.Chance(0.8) ? 100 : (float)fz.Range(-500, 500), Count = r => fz.Count("rule." + r) };
        var clips = core.Clips;
        var focus = lagoon ? new Vector3((float)fz.Range(-20, 40), 100, (float)fz.Range(-200, 200)) : new Vector3((float)fz.Range(-5e4, 5e4), 0, (float)fz.Range(-5e4, 5e4));
        var people = new List<Vector3>();
        var last = new Dictionary<long, Vector3>();
        var lastState = new Dictionary<long, FroxState>();
        bool swamp = true;
        for (int step = fz.Int(20, 400); step > 0; step--)
        {
            if (fz.Chance(0.05))
            {
                people.Clear();
                for (int n = fz.Int(0, 4); n > 0; n--)
                    people.Add(fz.Chance(0.05) ? new Vector3(fz.Float(), fz.Float(), fz.Float()) : focus + new Vector3((float)fz.Range(-300, 300), 0, (float)fz.Range(-300, 300)));
            }
            if (fz.Chance(0.01)) swamp = !swamp;
            float dt = Dt(fz);
            double hour = fz.Chance(0.1) ? fz.Double() : fz.Range(-24, 48);
            core.Step(dt, fz.Chance(0.02) ? new Vector3(fz.Float(), 0, fz.Float()) : focus, hour, swamp, ground, people);
            var all = core.All;
            var pool = core.Pool;
            fz.Expect(all.Count <= FroxPreset.NightCount && pool.Length <= FroxPreset.PoolSize, $"{all.Count} froxlotls, pool {pool.Length}");
            float used = float.IsFinite(dt) && dt > 0 ? Math.Min(dt, 0.25f) : 0;
            foreach (var f in all.Where(f => f.State != FroxState.Away))
            {
                fz.Expect(WildCore.Finite(f.Pos) && float.IsFinite(f.Yaw), $"non-finite froxlotl {f.Pos} {f.Yaw} in {f.State}");
                fz.Expect(pool.Contains(f.Colour), $"colour {f.Colour} not in the pool");
                if (f.State == FroxState.Swim && core.WaterLevel is var wl && float.IsFinite(wl))
                {
                    float floor = SafeGround(ground, f.Pos.X, f.Pos.Z);
                    fz.Expect(f.Pos.Y <= wl + 1e-3f, $"a swimming froxlotl above the water ({f.Pos.Y} over {wl})");
                    if (float.IsFinite(floor) && floor + FroxPreset.Keel < wl)
                        fz.Expect(f.Pos.Y >= floor + FroxPreset.Keel - 1e-2f, $"a swimming froxlotl in the bottom ({f.Pos.Y}, bottom {floor})");
                    if (lastState.GetValueOrDefault(f.Id) == FroxState.Swim && last.TryGetValue(f.Id, out var was) && WildCore.Finite(was))
                    {
                        float swum = new Vector2(f.Pos.X - was.X, f.Pos.Z - was.Z).Length();
                        float res = 2.5e-7f * Math.Max(Math.Max(MathF.Abs(f.Pos.X), MathF.Abs(f.Pos.Z)), 1);
                        fz.Expect(swum <= FroxPreset.SwimSpeed * f.Scale * used + 1e-3f + 4 * res, $"a froxlotl swam {swum} in {used} s");
                    }
                }
                if (f.State == FroxState.Run && last.TryGetValue(f.Id, out var before) && WildCore.Finite(before))
                {
                    float moved = new Vector2(f.Pos.X - before.X, f.Pos.Z - before.Z).Length();
                    float ulp = 2.5e-7f * Math.Max(Math.Max(MathF.Abs(f.Pos.X), MathF.Abs(f.Pos.Z)), 1);   // a float's resolution where it stands
                    fz.Expect(moved <= clips.RunSpeed * FroxPreset.RunRate * f.Scale * used + 1e-3f + 4 * ulp, $"a froxlotl ran {moved} in {used} s (its run: {clips.RunSpeed * FroxPreset.RunRate * f.Scale} units/s)");
                }
            }
            last = all.ToDictionary(f => f.Id, f => f.Pos);
            lastState = all.ToDictionary(f => f.Id, f => f.State);
            foreach (var d in core.Snapshot())
            {
                int frames = d.Clip switch { 0 => clips.RestFrames, 1 => clips.WalkFrames, 2 => clips.RunFrames, _ => clips.SwimFrames };
                fz.Expect(d.Clip is >= 0 and <= 3 && d.Frame >= 0 && d.Frame < frames, $"frame {d.Clip}.{d.Frame} out of range");
            }
            if (fz.Chance(0.05)) fz.Expect(core.TakeSplashes().Count <= FroxPreset.SplashQueue, "froxlotl splashes over the cap");
        }

        // the reader on cut and flipped bakes
        var bake = Bake();
        var bad = (byte[])bake.Clone();
        if (fz.Chance(0.5)) bad = bad[..fz.Int(0, bad.Length)];
        else
        {
            // decompress, flip, recompress: the checks behind the deflate are what matter
            using var inflate = new System.IO.Compression.DeflateStream(new MemoryStream(bake, 8, bake.Length - 8), System.IO.Compression.CompressionMode.Decompress);
            using var plain = new MemoryStream();
            inflate.CopyTo(plain);
            var body = plain.ToArray();
            for (int n = fz.Int(1, 6); n > 0; n--)
            {
                int at = fz.Chance(0.5) ? fz.Int(0, Math.Min(64, body.Length)) : fz.Int(0, body.Length);
                body[at] = (byte)fz.Int(0, 256);
            }
            if (fz.Chance(0.2)) body = body[..fz.Int(0, body.Length)];
            using var packed = new MemoryStream();
            packed.Write(System.Text.Encoding.ASCII.GetBytes(FroxAsset.Magic));
            using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(body);
            bad = packed.ToArray();
        }
        try
        {
            var a = FroxAsset.Read(bad);
            fz.Expect(a.Indices.All(i => i < a.Vertices) && a.Frames.Length == 4 && a.Frames.All(c => c.Length > 0) && a.Frames.All(c => c.All(f => f.P.Length == a.Vertices)),
                "a flipped bake read back with an index past its vertices or a bad frame");
        }
        catch (InvalidDataException) { }
        catch (Exception e) { fz.Expect(false, $"FroxAsset.Read threw {e.GetType().Name}: {e.Message}"); }

        // the frog's skin on random small images: never throws, the right size, the gloss never past the eye's
        int w1 = fz.Int(1, 40), h1 = fz.Int(1, 40), w2 = fz.Int(1, 40), h2 = fz.Int(1, 40);
        var img1 = new global::Meitou.Data.Textures.RgbaImage(w1, h1, Enumerable.Range(0, w1 * h1 * 4).Select(_ => (byte)fz.Int(0, 256)).ToArray());
        var img2 = new global::Meitou.Data.Textures.RgbaImage(w2, h2, Enumerable.Range(0, w2 * h2 * 4).Select(_ => (byte)fz.Int(0, 256)).ToArray());
        if (fz.Chance(0.05))
        {
            try
            {
                var skin = FrogSkin.Compose(img1, img2);
                fz.Expect(skin.Width == FrogSkin.Size && skin.Pixels.Where((_, i) => i % 4 == 3).All(a => a >= FrogSkin.Gloss && a <= FrogSkin.EyeGloss),
                    "frog skin: wrong size or a gloss outside skin..eye");
            }
            catch (Exception e) { fz.Expect(false, $"FrogSkin.Compose threw {e.GetType().Name}"); }
        }
        byte want = (byte)fz.Int(0, 256);
        fz.Expect(FrogSkin.WithGloss(img1, want).Pixels.Where((_, i) => i % 4 == 3).All(a => a == want), "WithGloss left an alpha");

        // frog animation names
        string name = FrogMesh.AnimName(fz.Int(-5, 10), fz.Chance(0.2) ? fz.Float() : (float)fz.Range(-2, 3));
        fz.Expect(FrogMesh.ParseAnim(name, out int c2, out int f2) && f2 < GorilloBake.Clips[c2].Frames, $"frog animation name {name} out of range");
    }

    /// <summary>0.7.0 wing and frog poses (see the header).</summary>
    static void WingsTarget(PzaFuzz fz)
    {
        // the beat on any vertex and any angle: finite, a thin wing (within 1 unit of the shoulder line in height) stays on its side
        var p = new Vector3(fz.Float(-100, 100), fz.Chance(0.5) ? (float)fz.Range(BirdFold.Pivot - 1, BirdFold.Pivot + 1) : fz.Float(-100, 100), fz.Float(-100, 100));
        float angle = fz.Chance(0.2) ? fz.Float() : (float)fz.Range(-10, 10);
        var f = BirdFold.Flap(p, angle);
        if (WildCore.Finite(p))
        {
            fz.Expect(WildCore.Finite(f), $"Flap({p}, {angle}) = {f}");
            if (MathF.Abs(p.X) > BirdFold.Shoulder && MathF.Abs(p.Y - BirdFold.Pivot) <= 1)
                fz.Expect(MathF.Sign(f.X) == MathF.Sign(p.X) && MathF.Abs(f.X) >= BirdFold.Shoulder - 1 - 1e-3f, $"Flap({p}, {angle}) swung the wing through the body: {f}");
        }
        float amount = fz.Chance(0.2) ? fz.Float() : (float)fz.Range(-2, 3);
        int level = BirdFold.Level(amount);
        fz.Expect(BirdFold.ValidPose(level) && level % BirdFold.FoldStep == 0, $"Level({amount}) = {level}");
        float seconds = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-1e4, 1e4), hz = fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-1, 10);
        fz.Expect(BirdFold.ValidPose(BirdFold.FlapAt(seconds, hz)), $"FlapAt({seconds}, {hz})");
        fz.Expect(BirdFold.ValidPose(BirdFold.FreePose(fz.Int(-1000000, 1000000), fz.Chance(0.1) ? fz.Double() : fz.Range(0, 1e7), fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-1, 1))), "FreePose out of range");
        int pose = fz.Int(-5, 130);
        string mesh = fz.Word(12) + ".mesh" + (fz.Chance(0.5) ? "?" + fz.Word(8) : "");
        try
        {
            if (BirdFold.ValidPose(pose) && pose > 0)
                fz.Expect(BirdFold.ParsePose(BirdFold.PoseName(mesh, pose), out var plain, out var back) && plain == mesh && back == pose, $"pose {pose} on {mesh} does not round-trip");
            string text = fz.Text(40);
            if (BirdFold.ParsePose(text, out _, out var got)) fz.Expect(BirdFold.ValidPose(got) && got > 0, $"ParsePose({PzaFuzzUi.Short(text)}) = {got}");
        }
        catch (Exception e) { fz.Expect(false, $"PoseName/ParsePose threw: {e.GetType().Name}"); }

        // a bird's life at a frame rate: the fold never moves faster than 500 levels a second (plus one rounding step) while it lands, settles, hops and takes off
        float dt = (float)fz.Range(1.0 / 240, 1.0 / 30);
        var b = new WildBird { Phase = Phase.Approach, Length = (float)fz.Range(0.5, 8), Delay = (float)fz.Range(0, 1), Action = 0 };
        int last = -1;
        void Look(string where)
        {
            int now = b.WingPose;
            fz.Expect(BirdFold.ValidPose(now), $"{where}: pose {now}");
            if (last is > 0 and <= 100 && now is > 0 and <= 100)
                fz.Expect(Math.Abs(now - last) <= BirdFold.FoldStep + 500 * dt, $"{where}: fold {last} -> {now} in one frame of {dt:0.000} s");
            last = now;
        }
        for (b.Clock = 0; b.Clock <= b.Length; b.Clock += dt) Look("approach");
        b.Clock = b.Length; Look("approach end");
        b.Phase = Phase.Peck;
        for (b.Clock = 0; b.Clock < 1; b.Clock += dt) Look("settle");
        b.Action = 2;
        for (b.ActionClock = 0; b.ActionClock <= Preset.HopSeconds; b.ActionClock += dt) Look("hop");
        b.Action = 0; Look("hop end");
        b.Phase = Phase.Flee;
        for (b.Clock = 0; b.Clock < b.Delay + 1; b.Clock += dt) Look("take-off");

        // frogs
        float stretch = fz.Chance(0.2) ? fz.Float() : (float)fz.Range(-3, 3);
        int fp = FrogPreset.Pose(stretch);
        fz.Expect(fp >= 0 && fp < FrogPreset.Poses, $"frog Pose({stretch}) = {fp}");
        var state = (FrogState)fz.Int(0, 6);
        float st = FrogPreset.Stretch(state, fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-1, 2), fz.Chance(0.1) ? fz.Float() : (float)fz.Range(-1, 2));
        fz.Expect(float.IsFinite(st) && st >= 0 && st <= 1, $"frog Stretch({state}) = {st}");
        var min = new Vector3(fz.Float(-50, 50), fz.Float(-50, 50), fz.Float(-50, 50));
        var max = min + new Vector3(fz.Float(0, 60), fz.Float(0, 60), fz.Float(0, 60));
        var q = new Vector3(fz.Float(-100, 100), fz.Float(-100, 100), fz.Float(-100, 100));
        var shaped = FrogMesh.Shape(q, stretch, min, max);
        if (WildCore.Finite(q) && WildCore.Finite(min) && WildCore.Finite(max)) fz.Expect(WildCore.Finite(shaped), $"frog Shape({q}, {stretch}) = {shaped}");
        string fname = fz.Chance(0.5) ? FrogMesh.PoseName(fz.Int(-3, 10)) : fz.Text(30);
        try
        {
            int got = FrogMesh.PoseOf(fname);
            fz.Expect(got >= 0 && got < FrogPreset.Poses, $"frog PoseOf({PzaFuzzUi.Short(fname)}) = {got}");
        }
        catch (Exception e) { fz.Expect(false, $"frog PoseOf threw: {e.GetType().Name}"); }
    }

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

        double drain = fz.Pick(new[] { 0.3, 0.02, 0.0 });   // how often the host thread takes the bites (0: only at the end)
        for (int step = fz.Int(50, 700); step > 0; step--)
        {
            Mutate(fz, corpses, threats, ground, origin);
            core.SetWorld(corpses, threats);
            float dt = Dt(fz);
            core.Advance(dt);
            fz.Expect(float.IsFinite(core.Now), $"the rules' clock is {core.Now} after a step of {dt} s (cooldowns would never end)");
            if (fz.Chance(0.05)) core.Bites = !core.Bites;
            if (fz.Chance(drain)) CheckBites(fz, core, ground);
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
        CheckBites(fz, core, ground);
    }

    /// <summary>The bites the host thread takes: never more than the queue holds, finite, never under the ground.</summary>
    static void CheckBites(PzaFuzz fz, WildCore core, Func<float, float, float> ground)
    {
        var bites = core.TakeBites();
        fz.Expect(bites.Count <= Preset.BiteQueue, $"{bites.Count} bites waiting, most {Preset.BiteQueue}");
        foreach (var p in bites)
            fz.Expect(WildCore.Finite(p) && p.Y >= ground(p.X, p.Z) - 1e-3f, $"bite at {p} (ground {ground(p.X, p.Z)})");
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
