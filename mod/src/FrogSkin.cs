// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The frog's skin (0.9.0, user: "the frogillo just looks very... metallic and its eyes are glossed over with the whole skin"). Made at run
// time from two of the install's textures (never copied or shipped): the lizard coat (coat02-lizard.dds) on the Gorillo's texture layout,
// with the coat's emblem (grey-white and red, not scales) filled from the scales beside it, and the Gorillo's own eyes and dark claws
// (frog_gorilla_diffuse.dds) kept. Meitou's debris light reads the diffuse alpha as gloss, and both textures carry 255 there (the
// chrome look): the skin gets Gloss, the eyes EyeGloss. Pure (pixels in, pixels out) for the check.
using Meitou.Data.Textures;

namespace PZA.Wildlife.Meitou;

public static class FrogSkin
{
    public const string Name = "pza.frog.skin";
    public const string Coat = "coat02-lizard.dds", Gorillo = "frog_gorilla_diffuse.dds";
    public const int Size = 1024;
    /// <summary>The gloss written in the alpha (0..255): skin matte, eyes wet.</summary>
    public const byte Gloss = 51, EyeGloss = 230;
    /// <summary>The Gorillo's eye on its texture (u, v, radius) and the boxes where its claws are (u0, v0, u1, v1).</summary>
    public static readonly (float U, float V, float R) Eye = (0.863f, 0.527f, 0.058f);
    public static readonly (float U0, float V0, float U1, float V1)[] Claws = [(0.15f, 0.30f, 0.25f, 0.44f), (0.61f, 0.41f, 0.80f, 0.56f)];

    /// <summary>The skin at Size x Size from the two textures' largest images (any sizes; sampled nearest).</summary>
    public static RgbaImage Compose(RgbaImage coat, RgbaImage gorillo)
    {
        int n = Size;
        var liz = Resample(coat, n);
        var gor = Resample(gorillo, n);
        // the coat's emblem: grey-white or red pixels, grown by 3, filled from the scales 96 px aside (up to five tries)
        var bad = new bool[n * n];
        for (int i = 0; i < n * n; i++)
        {
            int r = liz[4 * i], g = liz[4 * i + 1], b = liz[4 * i + 2];
            float lum = (0.3f * r + 0.59f * g + 0.11f * b) / 255f, sat = (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255f;
            bad[i] = (sat < 0.10f && lum > 0.40f) || r > g * 1.3f;
        }
        for (int grow = 0; grow < 3; grow++)
        {
            var next = (bool[])bad.Clone();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (bad[y * n + x])
                    {
                        next[y * n + (x + 1) % n] = next[y * n + (x + n - 1) % n] = true;
                        next[((y + 1) % n) * n + x] = next[((y + n - 1) % n) * n + x] = true;
                    }
            bad = next;
        }
        foreach (int shift in new[] { 96, -96, 192, -192, 288 })
        {
            var filled = new List<int>();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x, from = y * n + ((x - shift) % n + n) % n;
                    if (!bad[i] || bad[from]) continue;
                    Buffer.BlockCopy(liz, 4 * from, liz, 4 * i, 4);
                    filled.Add(i);
                }
            foreach (int i in filled) bad[i] = false;
        }
        var outp = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                float d = MathF.Sqrt((u - Eye.U) * (u - Eye.U) + (v - Eye.V) * (v - Eye.V));
                float eye = Math.Clamp((Eye.R - d) / 0.006f, 0, 1);
                int r = gor[4 * i], g = gor[4 * i + 1], b = gor[4 * i + 2];
                float lum = (0.3f * r + 0.59f * g + 0.11f * b) / 255f;
                bool claw = lum < 0.30f && r < 1.4f * g + 5 && Claws.Any(c => u >= c.U0 && u <= c.U1 && v >= c.V0 && v <= c.V1);
                float keep = claw ? 1 : eye;
                for (int k = 0; k < 3; k++) outp[4 * i + k] = (byte)Math.Round(liz[4 * i + k] * (1 - keep) + gor[4 * i + k] * keep);
                outp[4 * i + 3] = (byte)Math.Round(Gloss + (EyeGloss - Gloss) * eye);
            }
        return new RgbaImage(n, n, outp);
    }

    /// <summary>A copy of an image with every alpha set to <paramref name="gloss"/> (Mazal's colours: a wet sheen, not chrome).</summary>
    public static RgbaImage WithGloss(RgbaImage image, byte gloss)
    {
        var p = (byte[])image.Pixels.Clone();
        for (int i = 3; i < p.Length; i += 4) p[i] = gloss;
        return new RgbaImage(image.Width, image.Height, p);
    }

    static byte[] Resample(RgbaImage img, int n)
    {
        var o = new byte[n * n * 4];
        if (img.Width <= 0 || img.Height <= 0 || img.Pixels.Length < img.Width * img.Height * 4) return o;
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(img.Height - 1, (int)((y + 0.5f) * img.Height / n));
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(img.Width - 1, (int)((x + 0.5f) * img.Width / n));
                Buffer.BlockCopy(img.Pixels, 4 * (sy * img.Width + sx), o, 4 * (y * n + x), 4);
            }
        }
        return o;
    }
}
