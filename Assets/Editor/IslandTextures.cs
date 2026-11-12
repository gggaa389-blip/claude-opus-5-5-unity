using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// Procedural, tileable textures for the terrain layers, the water ripples and the bridge planks.
/// Everything is generated from value noise in code and saved as PNG assets.
public static class IslandTextures
{
    public const string TexDir = "Assets/Generated/Textures";
    public const string LayerDir = "Assets/Generated/TerrainLayers";

    // terrain layer order used by the splat painter
    public const int Grass = 0, Forest = 1, Sand = 2, Dirt = 3, Rock = 4, Paved = 5, Soil = 6, Lush = 7;
    public static readonly string[] LayerNames = { "grass", "forest_floor", "sand", "dirt_road", "rock", "concrete", "farm_soil", "grass_lush" };
    static readonly float[] LayerTile = { 9f, 9f, 7f, 6f, 11f, 8f, 6f, 10f };
    static readonly float[] LayerSmooth = { 0.06f, 0.05f, 0.10f, 0.06f, 0.14f, 0.18f, 0.05f, 0.06f };
    static readonly float[] LayerBump = { 0.5f, 0.6f, 0.35f, 0.7f, 1.0f, 0.45f, 0.8f, 0.5f };

    // ---------------------------------------------------------------- noise
    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    static int Wrap(int i, int p) => ((i % p) + p) % p;

    /// periodic value noise; x,y in lattice units, period in cells
    static float VNoise(float x, float y, int period, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        float a = Hash(Wrap(x0, period), Wrap(y0, period), seed), b = Hash(Wrap(x0 + 1, period), Wrap(y0, period), seed);
        float c = Hash(Wrap(x0, period), Wrap(y0 + 1, period), seed), d = Hash(Wrap(x0 + 1, period), Wrap(y0 + 1, period), seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    /// tileable fBm on u,v in [0,1); returns roughly [0,1]
    static float Fbm(float u, float v, int basePeriod, int octaves, int seed, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        int p = basePeriod;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * VNoise(u * p, v * p, p, seed + o * 31);
            norm += amp; amp *= gain; p *= 2;
        }
        return sum / norm;
    }

    static Color Srgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);

    static Color Shade(Color c, float k) => new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f);

    // ---------------------------------------------------------------- generators (512 x 512, tileable)
    delegate Color Pixel(float u, float v);

    static Texture2D Make(int size, Pixel f, float smoothness = 1f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = f(x / (float)size, y / (float)size);
                c.a = smoothness;   // the built-in terrain shader reads smoothness from the diffuse alpha
                px[y * size + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Color GrassPx(float u, float v)
    {
        float low = Fbm(u, v, 4, 4, 11);
        float mid = Fbm(u, v, 16, 3, 12);
        float blade = Fbm(u, v, 128, 2, 13);
        Color a = Srgb(92, 150, 44), b = Srgb(140, 182, 62);
        Color c = Color.Lerp(a, b, Mathf.SmoothStep(0.25f, 0.75f, low));
        c = Shade(c, 0.9f + 0.2f * mid);
        c = Shade(c, 0.88f + 0.24f * blade);
        float flower = Hash((int)(u * 256), (int)(v * 256), 14);
        if (flower > 0.996f) c = Color.Lerp(c, Srgb(240, 236, 170), 0.6f);
        return c;
    }

    static Color ForestPx(float u, float v)
    {
        float low = Fbm(u, v, 4, 4, 21);
        float patch = Fbm(u, v, 8, 3, 22);
        float fine = Fbm(u, v, 128, 2, 23);
        Color moss = Srgb(78, 124, 40), litter = Srgb(116, 98, 58);
        Color c = Color.Lerp(moss, litter, Mathf.SmoothStep(0.5f, 0.75f, patch));
        c = Shade(c, 0.85f + 0.3f * low);
        c = Shade(c, 0.85f + 0.3f * fine);
        return c;
    }

    static Color SandPx(float u, float v)
    {
        float low = Fbm(u, v, 4, 3, 31);
        float grain = Hash((int)(u * 512), (int)(v * 512), 32);
        float ripple = Mathf.Sin((v * 9f + Fbm(u, v, 4, 2, 33) * 2.5f) * Mathf.PI * 2f) * 0.5f + 0.5f;
        Color c = Color.Lerp(Srgb(226, 202, 148), Srgb(242, 224, 172), low);
        c = Shade(c, 0.96f + 0.06f * ripple);
        c = Shade(c, 0.95f + 0.1f * grain);
        return c;
    }

    static Color DirtPx(float u, float v)
    {
        float low = Fbm(u, v, 4, 4, 41);
        float fine = Fbm(u, v, 64, 3, 42);
        Color c = Color.Lerp(Srgb(190, 140, 84), Srgb(222, 176, 112), low);
        c = Shade(c, 0.9f + 0.2f * fine);
        // round pebbles: one candidate per cell, random centre and radius
        float gx = u * 96f, gy = v * 96f;
        int cx = Mathf.FloorToInt(gx), cy = Mathf.FloorToInt(gy);
        float peb = Hash(cx, cy, 43);
        if (peb > 0.86f)
        {
            float ox = 0.3f + 0.4f * Hash(cx, cy, 44), oy = 0.3f + 0.4f * Hash(cx, cy, 45);
            float d = Vector2.Distance(new Vector2(gx - cx, gy - cy), new Vector2(ox, oy));
            float r = 0.12f + 0.16f * Hash(cx, cy, 46);
            if (d < r) c = Shade(c, peb > 0.93f ? 1.12f + 0.1f * (1f - d / r) : 0.82f);
        }
        return c;
    }

    static Color RockPx(float u, float v)
    {
        float low = Fbm(u, v, 3, 5, 51);
        float warp = Fbm(u, v, 4, 3, 53);
        float strata = Mathf.Sin((v * 5f + warp * 2.2f) * Mathf.PI * 2f) * 0.5f + 0.5f;
        float fine = Fbm(u, v, 48, 3, 54);
        float crack = 1f - Mathf.Abs(Fbm(u + warp * 0.15f, v, 6, 3, 52) * 2f - 1f);
        Color c = Color.Lerp(Srgb(112, 106, 96), Srgb(198, 190, 172), Mathf.SmoothStep(0.25f, 0.75f, low));
        c = Shade(c, 0.8f + 0.26f * Mathf.SmoothStep(0.3f, 0.7f, strata));
        c = Shade(c, 0.92f + 0.14f * fine);
        c = Shade(c, Mathf.Lerp(1f, 0.72f, Mathf.SmoothStep(0.9f, 0.97f, crack)));
        float lichen = Fbm(u, v, 8, 3, 55);
        if (lichen > 0.66f) c = Color.Lerp(c, Srgb(140, 146, 104), Mathf.SmoothStep(0.66f, 0.78f, lichen) * 0.25f);
        return c;
    }

    static Color LushPx(float u, float v)
    {
        float low = Fbm(u, v, 4, 4, 101);
        float clump = Fbm(u, v, 24, 3, 102);
        float blade = Fbm(u, v, 128, 2, 103);
        Color c = Color.Lerp(Srgb(66, 122, 38), Srgb(98, 150, 46), Mathf.SmoothStep(0.3f, 0.7f, low));
        c = Shade(c, 0.85f + 0.3f * clump);
        c = Shade(c, 0.9f + 0.2f * blade);
        return c;
    }

    static Color ConcretePx(float u, float v)
    {
        float low = Fbm(u, v, 4, 4, 61);
        float fine = Hash((int)(u * 512), (int)(v * 512), 62);
        Color c = Color.Lerp(Srgb(140, 140, 138), Srgb(170, 170, 166), low);
        c = Shade(c, 0.96f + 0.08f * fine);
        float gu = Mathf.Abs(Mathf.Repeat(u * 4f, 1f) - 0.5f), gv = Mathf.Abs(Mathf.Repeat(v * 4f, 1f) - 0.5f);
        if (gu > 0.488f || gv > 0.488f) c = Shade(c, 0.72f);
        float stain = Fbm(u, v, 8, 3, 63);
        if (stain > 0.68f) c = Shade(c, 0.9f);
        return c;
    }

    static Color SoilPx(float u, float v)
    {
        float low = Fbm(u, v, 4, 3, 71);
        float fine = Fbm(u, v, 64, 2, 72);
        float furrow = Mathf.Sin(u * 6f * Mathf.PI * 2f) * 0.5f + 0.5f;   // rows along v (world z)
        Color c = Color.Lerp(Srgb(112, 74, 42), Srgb(150, 104, 60), furrow);
        c = Shade(c, 0.9f + 0.15f * low);
        c = Shade(c, 0.9f + 0.2f * fine);
        return c;
    }

    static Color WoodPx(float u, float v)
    {
        float grain = Fbm(u * 0.25f, v, 2, 4, 81);
        float streak = Fbm(u, v * 0.1f, 32, 3, 82);
        Color c = Color.Lerp(Srgb(150, 98, 52), Srgb(196, 138, 76), grain);
        c = Shade(c, 0.85f + 0.3f * streak);
        return c;
    }

    /// tangent-space normal map from the luminance of a tileable texture (wraps at the edges)
    static Texture2D NormalFromLuma(Texture2D src, float strength)
    {
        int n = src.width;
        var px = src.GetPixels();
        var h = new float[n * n];
        for (int i = 0; i < px.Length; i++) h[i] = px[i].r * 0.3f + px[i].g * 0.59f + px[i].b * 0.11f;
        var outPx = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (h[y * n + (x + 1) % n] - h[y * n + (x + n - 1) % n]) * strength;
                float dy = (h[((y + 1) % n) * n + x] - h[((y + n - 1) % n) * n + x]) * strength;
                var nn = new Vector3(-dx, -dy, 1f).normalized;
                outPx[y * n + x] = new Color(nn.x * 0.5f + 0.5f, nn.y * 0.5f + 0.5f, nn.z * 0.5f + 0.5f, 1f);
            }
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        t.SetPixels(outPx);
        t.Apply();
        return t;
    }

    // ---------------------------------------------------------------- saving
    static T SavePng<T>(Texture2D tex, string name, bool normalMap = false, bool linear = false) where T : UnityEngine.Object
    {
        string path = $"{TexDir}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
        imp.sRGBTexture = !(normalMap || linear);
        imp.wrapMode = TextureWrapMode.Repeat;
        imp.mipmapEnabled = true;
        imp.anisoLevel = 8;
        imp.filterMode = FilterMode.Trilinear;
        imp.textureCompression = TextureImporterCompression.CompressedHQ;
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<T>(path);
    }

    public static Texture2D WaterNormal;
    public static Texture2D WoodTex;
    public static TerrainLayer[] Layers;

    public static void BuildAll()
    {
        AssetDatabase.DeleteAsset(TexDir);
        AssetDatabase.DeleteAsset(LayerDir);
        IslandAssets.EnsureFolder(TexDir);
        IslandAssets.EnsureFolder(LayerDir);

        Pixel[] gens = { GrassPx, ForestPx, SandPx, DirtPx, RockPx, ConcretePx, SoilPx, LushPx };
        Layers = new TerrainLayer[gens.Length];
        for (int i = 0; i < gens.Length; i++)
        {
            var src = Make(512, gens[i], LayerSmooth[i]);
            var normal = SavePng<Texture2D>(NormalFromLuma(src, LayerBump[i] * 4f), "terrain_" + LayerNames[i] + "_normal", normalMap: true);
            var tex = SavePng<Texture2D>(src, "terrain_" + LayerNames[i]);
            var layer = new TerrainLayer
            {
                diffuseTexture = tex,
                normalMapTexture = normal,
                normalScale = 1f,
                tileSize = new Vector2(LayerTile[i], LayerTile[i]),
                tileOffset = Vector2.zero,
                metallic = 0f,
                smoothness = i == Paved ? 0.15f : 0.05f,
                name = LayerNames[i]
            };
            AssetDatabase.CreateAsset(layer, $"{LayerDir}/{LayerNames[i]}.terrainlayer");
            Layers[i] = layer;
        }

        // ripple normal map from a tileable height field
        const int n = 256;
        var h = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = x / (float)n, v = y / (float)n;
                h[x, y] = Fbm(u, v, 6, 4, 91, 0.55f) + 0.35f * (1f - Mathf.Abs(Fbm(u, v, 3, 3, 92) * 2f - 1f));
            }
        var nt = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var npx = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (h[(x + 1) % n, y] - h[(x + n - 1) % n, y]) * 6f;
                float dy = (h[x, (y + 1) % n] - h[x, (y + n - 1) % n]) * 6f;
                var nn = new Vector3(-dx, -dy, 1f).normalized;
                npx[y * n + x] = new Color(nn.x * 0.5f + 0.5f, nn.y * 0.5f + 0.5f, nn.z * 0.5f + 0.5f, 1f);
            }
        nt.SetPixels(npx);
        nt.Apply();
        WaterNormal = SavePng<Texture2D>(nt, "water_ripples_normal", normalMap: true);
        WoodTex = SavePng<Texture2D>(Make(256, WoodPx), "bridge_wood");
        AssetDatabase.SaveAssets();
    }
}
