// PZA's Wildlife for Meitou - glowing swamp fireflies via ParticleRenderer (round radial HDR additive glow + near-camera fade, zero external assets).
// Author: PZA. Licence: GPLv3 or later.
using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Meitou.Game.Rendering.Particles;

namespace PZA.Wildlife.Meitou;

public static class FireflyMeshes
{
    public const int GlowSize = 32;
    static bool s_loggedActive;

    /// <summary>Appends active fireflies as camera-facing HDR glowing orbs into ParticleRenderer.Tracers for one frame. Returns how many were added.</summary>
    public static int AddTracers(List<ParticleRenderer.Tracer> tracers, Matrix4x4 view, Vector3 eye)
    {
        if (!Driver.Settings.Fireflies) return 0;
        var snap = Driver.Fireflies.Snapshot();
        if (snap.Length == 0) return 0;

        // Orient each billboard along the camera's Up axis so it stays a round glowing orb from any angle (including top-down RTS).
        var up = new Vector3(view.M12, view.M22, view.M32);
        up = up.LengthSquared() > 1e-6f ? Vector3.Normalize(up) : Vector3.UnitY;
        bool hasEye = WildCore.Finite(eye);
        int added = 0;

        for (int i = 0; i < snap.Length; i++)
        {
            var f = snap[i];
            if (!WildCore.Finite(f.Position) || f.Size <= 0f || f.Brightness <= 0f) continue;
            float near = hasEye ? FireflyCore.NearFade(Vector3.Distance(f.Position, eye)) : 1f;
            if (near <= 0.01f) continue;

            var half = up * (f.Size * 0.5f);
            // Warm yellow-green: a faint ember between flashes, HDR (> 1) only at the flash so Meitou's bloom flares with it.
            float glow = (0.25f + 3.5f * f.Brightness * f.Brightness) * near;
            float alpha = Math.Clamp((0.2f + 0.8f * f.Brightness) * near, 0f, 1f);
            var colour = new Vector4(0.85f * glow, 1.00f * glow, 0.28f * glow, alpha);
            tracers.Add(new ParticleRenderer.Tracer(f.Position + half, f.Position - half, f.Size, colour));
            added++;
        }

        Report(snap, eye, added);
        if (added > 0)
        {
            if (!s_loggedActive)
            {
                s_loggedActive = true;
                Log.Line($"swamp fireflies: drawing {added} round glowing motes via ParticleRenderer");
            }
            Counters.Add("fireflies.drawn");
        }
        return added;
    }

    static long s_lastReport;

    /// <summary>Every 10 s while flies are out: how many, how near the nearest is to the eye, the brightest now, and the lights given
    /// (so a log shows whether the motes are where the camera looks and whether they blink).</summary>
    static void Report(FireflyDraw[] snap, Vector3 eye, int drawn)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(s_lastReport, now).TotalSeconds < 10) return;
        s_lastReport = now;
        float nearest = snap.Length == 0 || !WildCore.Finite(eye) ? float.NaN : snap.Min(f => Vector3.Distance(f.Position, eye));
        float bright = snap.Length == 0 ? 0 : snap.Max(f => f.Brightness);
        int lit = snap.Count(f => f.Brightness > 0.5f);
        Log.Line($"fireflies: {snap.Length} out, {drawn} drawn, nearest {nearest / 10:0.0} m from the eye, brightest {bright:0.00}, {lit} flashing now; "
                 + $"lights {Counters.Get("fireflies.lit")} frames so far");
    }

    /// <summary>32x32 radial firefly glow RGBA8 pixels: hot bright center core + soft circular halo, 0 alpha at and outside r=1.</summary>
    public static byte[] GlowPixels(int size = GlowSize)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float a = 0f;
                if (r < 1f)
                {
                    float edge = 1f - r;
                    float halo = edge * edge;
                    float core = MathF.Exp(-r * r * 14f);
                    float taper = Math.Clamp(edge / 0.15f, 0f, 1f);
                    a = Math.Clamp((0.75f * core + 0.55f * halo) * taper, 0f, 1f);
                }
                int i = (y * size + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
                rgba[i + 3] = (byte)MathF.Round(a * 255f);
            }
        return rgba;
    }

    // ---- optional round additive glow swap on ParticleRenderer._draws (falls back to _tracerTexture if anything is missing)
    static bool s_glowResolved;
    static FieldInfo? s_drawsField, s_gpuField, s_itemTracer, s_itemTexture, s_itemMaterial;
    static MethodInfo? s_rgba8;
    static object? s_additiveMaterial;
    static readonly ConditionalWeakTable<ParticleRenderer, object> s_glowTextures = new();

    static void ResolveGlow()
    {
        if (s_glowResolved) return;
        s_glowResolved = true;
        var pr = typeof(ParticleRenderer);
        s_drawsField = AccessTools.Field(pr, "_draws");
        s_gpuField = AccessTools.Field(pr, "_gpu");
        var drawItem = AccessTools.Inner(pr, "DrawItem");
        if (drawItem is not null)
        {
            s_itemTracer = AccessTools.Field(drawItem, "Tracer");
            s_itemTexture = AccessTools.Field(drawItem, "Texture");
            s_itemMaterial = AccessTools.Field(drawItem, "Material");
        }
        var sampledImage = s_itemTexture?.FieldType ?? pr.Assembly.GetType("Meitou.Game.Rendering.Sky.SampledImage");
        if (sampledImage is not null)
            s_rgba8 = sampledImage.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Rgba8" && m.GetParameters().Length == 7);
        var matType = s_itemMaterial?.FieldType;
        var blendType = pr.Assembly.GetType("Meitou.Data.Particles.ParticleBlend");
        if (matType is not null && blendType is not null)
        {
            try
            {
                var additive = Enum.Parse(blendType, "Additive");
                s_additiveMaterial = Activator.CreateInstance(matType, "pza firefly", null, additive, true, false, false, Vector4.One, true);
            }
            catch { s_additiveMaterial = null; }
        }
    }

    /// <summary>When this frame's tracer draw contains only our fireflies (no bolts), swaps its texture to our round radial glow + additive blend.</summary>
    public static void SwapToGlowTexture(ParticleRenderer renderer, int priorTracers)
    {
        if (priorTracers != 0) return;
        ResolveGlow();
        if (s_drawsField is null || s_gpuField is null || s_itemTracer is null || s_itemTexture is null || s_rgba8 is null) return;
        if (s_drawsField.GetValue(renderer) is not IList draws || draws.Count == 0) return;

        var glow = GetOrCreateGlow(renderer);
        if (glow is null) return;

        for (int i = draws.Count - 1; i >= 0; i--)
        {
            var item = draws[i];
            if (item is null || s_itemTracer.GetValue(item) is not true) continue;
            s_itemTexture.SetValue(item, glow);
            if (s_itemMaterial is not null && s_additiveMaterial is not null)
                s_itemMaterial.SetValue(item, s_additiveMaterial);
            draws[i] = item;
            break;
        }
    }

    static object? GetOrCreateGlow(ParticleRenderer renderer)
    {
        if (s_glowTextures.TryGetValue(renderer, out var cached)) return cached;
        var gpu = s_gpuField?.GetValue(renderer);
        if (gpu is null || s_rgba8 is null) return null;
        try
        {
            var tex = s_rgba8.Invoke(null, [gpu, GlowSize, GlowSize, GlowPixels(), false, true, "pza firefly glow"]);
            if (tex is not null) s_glowTextures.AddOrUpdate(renderer, tex);
            return tex;
        }
        catch (Exception e)
        {
            Log.Exception("firefly.tex", e);
            return null;
        }
    }
}
