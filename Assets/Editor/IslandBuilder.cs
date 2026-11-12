using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// Builds the whole battle royale island from zero: prefabs, textures, terrain, water, zones,
/// loot, spawn points, lighting; saves Assets/Scenes/Island.unity.
/// Run: Unity.exe -batchmode -projectPath <p> -executeMethod IslandBuilder.Build -quit
///
/// World frame: x east, z north, y up, sea level y = 0. The reference map (1254 px) covers
/// 431 x 431 m centred on the origin: world x = (px - 627) * 0.344, z = (627 - py) * 0.344.
public static partial class IslandBuilder
{
    public const string ScenePath = "Assets/Scenes/Island.unity";
    public const string GenDir = "Assets/Generated";
    public const string LevelDataDir = "Assets/LevelData";
    public static string OutDir = @"C:\Users\author\Documents\work\project-files\ai-battle-royale\runs\claude-opus-5-5\test2";

    public const float Size = 600f, BaseY = -20f, HeightRange = 70f;
    public const int HRes = 1025, ARes = 1024;
    const float MapMPerPx = 0.344f, MapCenter = 627f, MapPx = 1254f;

    // ---------------------------------------------------------------- zone anchors (metres, from the map)
    static readonly Vector2 PlazaC = new Vector2(0f, 9f);
    static readonly Vector2 TowerNW = new Vector2(-91f, 165f);
    static readonly Vector2 TowerSE = new Vector2(138.5f, -59.5f);
    static readonly Vector2 TowerS = new Vector2(-1.7f, -149f);
    static readonly Vector2 FarmHouseC = new Vector2(114.6f, 133f);
    static readonly Vector2 Bridge1Guess = new Vector2(45.7f, 69.5f);
    static readonly Vector2 Bridge2Guess = new Vector2(47.5f, -47.5f);
    static readonly Vector2[] WarehouseC = { new Vector2(-116f, -58f), new Vector2(-63f, -85f), new Vector2(-104f, -102.5f) };
    const float WarehouseYaw = 30f;
    static readonly Vector2 FortC = new Vector2(-72f, -50f);
    const float FortYaw = 30f;

    struct Field { public Vector2 c, half; public int kind; }   // kind 0 wheat, 1 soil, 2 green crops
    static readonly Field[] Fields =
    {
        new Field { c = new Vector2(90f, 118f), half = new Vector2(12.5f, 14.5f), kind = 0 },
        new Field { c = new Vector2(111f, 90f), half = new Vector2(17.5f, 13.5f), kind = 1 },
        new Field { c = new Vector2(143f, 113f), half = new Vector2(15.5f, 14.5f), kind = 2 },
    };
    static readonly Vector2 FarmCompoundC = new Vector2(118f, 108f), FarmCompoundHalf = new Vector2(47f, 39f);

    // river: water surface level along z (lake 10 m, falling to 2.6 m at the river end)
    static readonly float[] WZ = { 125f, 110f, 95f, 80f, 60f, 20f, -20f, -60f, -95f, -113f };
    static readonly float[] WL = { 10f, 9.2f, 8.1f, 7.1f, 6.3f, 5.6f, 4.9f, 4.1f, 3.3f, 2.7f };
    // channel from the river end over the rocks into the sea (the waterfall on the map)
    static readonly Vector2[] Channel = { new Vector2(47.5f, -106f), new Vector2(51f, -116f), new Vector2(56.5f, -127f), new Vector2(62f, -138f), new Vector2(67f, -150f) };

    // ---------------------------------------------------------------- masks from the map
    class Mask
    {
        public int W, H;
        public float[] V;
        public static Mask Load(string assetPath)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(assetPath));
            var px = tex.GetPixels32();
            var m = new Mask { W = tex.width, H = tex.height, V = new float[px.Length] };
            for (int i = 0; i < px.Length; i++) m.V[i] = px[i].r / 255f;
            Object.DestroyImmediate(tex);
            return m;
        }
        /// bilinear sample in world metres; outside the map image the edge texels are repeated
        public float Sample(float x, float z, float unused = 0f)
        {
            float u = (MapCenter + x / MapMPerPx) / MapPx * W - 0.5f;
            float v = (MapCenter + z / MapMPerPx) / MapPx * H - 0.5f;   // texture row 0 = south edge
            u = Mathf.Clamp(u, 0f, W - 1.001f); v = Mathf.Clamp(v, 0f, H - 1.001f);   // clamp to the edge texels
            int x0 = (int)u, y0 = (int)v;
            int x1 = Mathf.Min(x0 + 1, W - 1), y1 = Mathf.Min(y0 + 1, H - 1);
            float fx = u - x0, fy = v - y0;
            float a = V[y0 * W + x0], b = V[y0 * W + x1], c = V[y1 * W + x0], d = V[y1 * W + x1];
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }
    }

    static Mask coastM, waterM, waterSdfM, roadM, pavedM, rockM, sandM;

    static void LoadMasks()
    {
        coastM = Mask.Load($"{LevelDataDir}/coast_sdf.png");
        waterM = Mask.Load($"{LevelDataDir}/water.png");
        waterSdfM = Mask.Load($"{LevelDataDir}/water_sdf.png");
        roadM = Mask.Load($"{LevelDataDir}/road.png");
        pavedM = Mask.Load($"{LevelDataDir}/paved.png");
        rockM = Mask.Load($"{LevelDataDir}/rock.png");
        sandM = Mask.Load($"{LevelDataDir}/sand.png");
    }

    /// signed distance to the coastline in metres, positive inland
    static float CoastSd(float x, float z)
    {
        // beyond the map image keep falling away from the coast instead of jumping to the clamp value
        const float half = MapPx * 0.5f * MapMPerPx;
        float outside = Mathf.Max(Mathf.Max(Mathf.Abs(x) - half, Mathf.Abs(z) - half), 0f);
        return (coastM.Sample(x, z, 0f) - 0.5f) * 80f - outside;
    }
    /// signed distance to lake/river water in metres, positive outside the water
    static float WaterSd(float x, float z) => (waterSdfM.Sample(x, z, 1f) - 0.5f) * 50f;
    static float Road(float x, float z) => roadM.Sample(x, z, 0f);
    static float Paved(float x, float z) => pavedM.Sample(x, z, 0f);
    static float RockMap(float x, float z) => rockM.Sample(x, z, 0f);
    static float SandMap(float x, float z) => sandM.Sample(x, z, 0f);
    /// beach mask dilated by about 7 m (beaches run down to the waterline)
    static float SandNear(float x, float z)
    {
        float m = 0f;
        for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
                m = Mathf.Max(m, SandMap(x + di * 7f, z + dj * 7f));
        return m;
    }

    // ---------------------------------------------------------------- math helpers
    static float Smooth(float e0, float e1, float x)
    {
        float t = Mathf.Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    static float Bell(float x, float z, Vector2 c, float r)
    {
        float d = Vector2.Distance(new Vector2(x, z), c);
        return d >= r ? 0f : 0.5f * (1f + Mathf.Cos(Mathf.PI * d / r));
    }

    static float Perlin(float x, float z) => Mathf.PerlinNoise(x + 523.17f, z + 811.43f) * 2f - 1f;

    static float Fbm(float x, float z, int oct)
    {
        float s = 0f, a = 1f, n = 0f;
        for (int i = 0; i < oct; i++) { s += a * Perlin(x, z); n += a; a *= 0.5f; x = x * 2.03f + 17.1f; z = z * 2.03f - 9.3f; }
        return s / n;
    }

    /// local coords of p in a frame rotated by yaw (Unity Euler y, degrees) around c
    static Vector2 ToLocal(Vector2 p, Vector2 c, float yawDeg)
    {
        float a = yawDeg * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
        Vector2 d = p - c;
        return new Vector2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);
    }

    static Vector2 ToWorld(Vector2 local, Vector2 c, float yawDeg)
    {
        float a = yawDeg * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
        return c + new Vector2(local.x * cs + local.y * sn, -local.x * sn + local.y * cs);
    }

    static float RectDist(Vector2 p, Vector2 c, Vector2 half, float yaw = 0f)
    {
        var l = ToLocal(p, c, yaw);
        float dx = Mathf.Max(Mathf.Abs(l.x) - half.x, 0f), dz = Mathf.Max(Mathf.Abs(l.y) - half.y, 0f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static float WaterLevel(float z)
    {
        if (z >= WZ[0]) return WL[0];
        for (int i = 0; i < WZ.Length - 1; i++)
            if (z >= WZ[i + 1]) return Mathf.Lerp(WL[i + 1], WL[i], (z - WZ[i + 1]) / (WZ[i] - WZ[i + 1]));
        return WL[WL.Length - 1];
    }

    // ---------------------------------------------------------------- zones
    static float FarmMask(float x, float z) => 1f - Smooth(0f, 12f, RectDist(new Vector2(x, z), FarmCompoundC, FarmCompoundHalf));

    static int FieldAt(float x, float z, float margin = 0f)
    {
        var p = new Vector2(x, z);
        for (int i = 0; i < Fields.Length; i++)
            if (RectDist(p, Fields[i].c, Fields[i].half) <= margin) return i;
        return -1;
    }

    static float PineZone(float x, float z) => Smooth(56f, 72f, z) * (1f - Smooth(68f, 84f, x)) * Smooth(-172f, -150f, x);
    static float OakZone(float x, float z) => (1f - Smooth(-82f, -66f, x)) * Smooth(-46f, -30f, z) * (1f - Smooth(112f, 132f, z));
    static readonly Vector3[] OakClearings = { new Vector3(-152f, 42f, 13f), new Vector3(-112f, 98f, 11f), new Vector3(-128f, 2f, 12f), new Vector3(-100f, 52f, 9f) };

    static float YardMask(float x, float z)
    {
        float s = 0f;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
                s += Paved(x + i * 7f, z + j * 7f);
        return Smooth(0.15f, 0.6f, s / 9f);
    }

    // ---------------------------------------------------------------- height
    static float RawHeight(float x, float z)
    {
        float sd = CoastSd(x, z);
        float n1 = Fbm(x * 0.011f, z * 0.011f, 4);
        float n2 = Fbm(x * 0.045f + 37f, z * 0.045f + 11f, 3);
        float land = 6.6f + 1.5f * n1 + 0.5f * n2;

        // north: rolling hills around the lake and through the pine forest
        float north = Smooth(52f, 140f, z) * (1f - Smooth(72f, 115f, x));
        float ridge = 1f - Mathf.Abs(Fbm(x * 0.022f + 5f, z * 0.022f - 9f, 3));
        land += north * (3.5f + 7.5f * ridge);

        // high ground under the three towers
        land += 11f * Bell(x, z, TowerNW, 32f);
        land += 10f * Bell(x, z, TowerSE, 34f);
        land += 14f * Bell(x, z, TowerS, 42f);

        // the east side sinks toward the beach
        float east = Smooth(118f, 172f, x) * (1f - Bell(x, z, TowerSE, 40f));
        land = Mathf.Lerp(land, 3.0f + 0.4f * n2, east);

        // flat village, gentle farm, flat industrial yard
        float village = 1f - Smooth(44f, 64f, Vector2.Distance(new Vector2(x, z), PlazaC));
        land = Mathf.Lerp(land, 7.0f + 0.15f * n2, village);
        land = Mathf.Lerp(land, 6.2f + 0.7f * n1, FarmMask(x, z) * 0.85f);
        land = Mathf.Lerp(land, 6.0f, YardMask(x, z));

        // shoreline: wide gentle beaches, short steep rock cliffs on the south point and the west coast
        float cliff = Mathf.Clamp01(Mathf.Max(1.3f * Bell(x, z, TowerS, 78f), 0.75f * RockMap(x, z) * (1f - Smooth(0.2f, 0.6f, SandMap(x, z)))));
        float ramp = Mathf.Lerp(26f, 4.5f, cliff);
        float h;
        if (sd >= 0f) h = Mathf.Lerp(0.45f, land, Smooth(0f, ramp, sd));
        else h = 0.45f + sd * Mathf.Lerp(0.28f, 1.1f, cliff);
        h = Mathf.Max(h, -15f);

        // lake basin and river channel
        float wsd = WaterSd(x, z);
        if (wsd < 22f)   // the water distance field saturates at 25 m, stay below that
        {
            // banks: high ground is eased down to a gentle slope toward the water, low ground is
            // lifted so the banks always stay above the water level
            float wl = WaterLevel(z);
            float slopeUp = wl + 1.2f + 0.2f * Mathf.Max(wsd - 3.5f, 0f);
            float bank = Mathf.Lerp(slopeUp, h, Smooth(4f, 22f, wsd));
            bank = Mathf.Max(bank, Mathf.Lerp(wl + 1.2f, -60f, Smooth(6f, 16f, wsd)));
            if (wsd >= 3.5f) h = bank;
            else if (wsd >= 0f) h = Mathf.Lerp(wl - 0.45f, bank, Smooth(0f, 3.5f, wsd));
            else h = wl - 0.45f - Mathf.Min(2.7f, -wsd * 0.55f);
        }

        // waterfall channel from the river end down to the sea
        float s, dc = ChannelDist(new Vector2(x, z), out s);
        if (dc < 9f)
        {
            float wc = ChannelLevel(s);
            float bed = wc - 0.8f;
            float k = 1f - Smooth(3.2f, 9f, dc);
            if (h > bed) h = Mathf.Lerp(h, bed, k);
        }
        return h;
    }

    static float[] _channelCum;
    static float ChannelDist(Vector2 p, out float s)
    {
        if (_channelCum == null)
        {
            _channelCum = new float[Channel.Length];
            for (int i = 1; i < Channel.Length; i++) _channelCum[i] = _channelCum[i - 1] + Vector2.Distance(Channel[i - 1], Channel[i]);
        }
        float best = float.MaxValue; s = 0f;
        for (int i = 0; i < Channel.Length - 1; i++)
        {
            Vector2 a = Channel[i], b = Channel[i + 1], ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            float d = Vector2.Distance(p, a + ab * t);
            if (d < best) { best = d; s = _channelCum[i] + t * ab.magnitude; }
        }
        return best;
    }

    static float ChannelLength => _channelCum[_channelCum.Length - 1];

    /// water surface along the channel: river level, then rapids and a waterfall over the coastal rocks
    static float ChannelLevel(float s)
    {
        float wl = WL[WL.Length - 1];
        Vector2 p = ChannelPoint(s);
        float sd = CoastSd(p.x, p.y);
        return Mathf.Lerp(-0.05f, wl, Smooth(1f, 13f, sd));
    }

    static Vector2 ChannelPoint(float s)
    {
        if (_channelCum == null) ChannelDist(Vector2.zero, out _);
        for (int i = 0; i < Channel.Length - 1; i++)
            if (s <= _channelCum[i + 1] || i == Channel.Length - 2)
                return Vector2.Lerp(Channel[i], Channel[i + 1], Mathf.Clamp01((s - _channelCum[i]) / (_channelCum[i + 1] - _channelCum[i])));
        return Channel[Channel.Length - 1];
    }

    // ---------------------------------------------------------------- pads (flattened ground under buildings / bridge ends)
    class Pad { public Vector2 c, half; public float yaw, h, margin; public string name; }
    static readonly List<Pad> Pads = new List<Pad>();

    static Pad AddPad(string name, Vector2 c, Vector2 half, float yaw, float margin, float? height = null)
    {
        float h;
        if (height.HasValue) h = height.Value;
        else
        {
            float sum = 0f; int n = 0;
            for (float u = -1f; u <= 1.001f; u += 0.25f)
                for (float v = -1f; v <= 1.001f; v += 0.25f)
                {
                    var w = ToWorld(new Vector2(u * half.x, v * half.y), c, yaw);
                    sum += RawHeight(w.x, w.y); n++;
                }
            h = sum / n;
        }
        var p = new Pad { name = name, c = c, half = half, yaw = yaw, h = h, margin = margin };
        Pads.Add(p);
        return p;
    }

    static float[,] Heights;   // [zIndex, xIndex] in metres

    static float GridX(int i) => -Size / 2f + i * Size / (HRes - 1);

    static void ComputeHeights()
    {
        Heights = new float[HRes, HRes];
        for (int j = 0; j < HRes; j++)
            for (int i = 0; i < HRes; i++)
                Heights[j, i] = RawHeight(GridX(i), GridX(j));

        // smooth the roads: blend toward a 5 m box blur where the road mask is set
        var blur = BoxBlur(Heights, 4);
        for (int j = 0; j < HRes; j++)
            for (int i = 0; i < HRes; i++)
            {
                float x = GridX(i), z = GridX(j);
                float r = Smooth(0.15f, 0.6f, Road(x, z)) * (WaterSd(x, z) > 2f ? 1f : 0f);
                if (r > 0f) Heights[j, i] = Mathf.Lerp(Heights[j, i], blur[j, i], r * 0.9f);
            }

        // building and bridge pads
        foreach (var pad in Pads)
        {
            float reach = pad.half.magnitude + pad.margin + 1f;
            int i0 = Mathf.Max(0, Idx(pad.c.x - reach)), i1 = Mathf.Min(HRes - 1, Idx(pad.c.x + reach) + 1);
            int j0 = Mathf.Max(0, Idx(pad.c.y - reach)), j1 = Mathf.Min(HRes - 1, Idx(pad.c.y + reach) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float d = RectDist(new Vector2(GridX(i), GridX(j)), pad.c, pad.half, pad.yaw);
                    float w = 1f - Smooth(0f, pad.margin, d);
                    if (w > 0f) Heights[j, i] = Mathf.Lerp(Heights[j, i], pad.h, w);
                }
        }
    }

    static int Idx(float x) => Mathf.FloorToInt((x + Size / 2f) / Size * (HRes - 1));

    static float[,] BoxBlur(float[,] src, int r)
    {
        int n = src.GetLength(0);
        var tmp = new float[n, n];
        var dst = new float[n, n];
        for (int j = 0; j < n; j++)
        {
            float s = 0f; int c = 0;
            for (int i = -r; i <= r; i++) if (i >= 0 && i < n) { s += src[j, i]; c++; }
            for (int i = 0; i < n; i++)
            {
                tmp[j, i] = s / c;
                int a = i - r, b = i + r + 1;
                if (a >= 0) { s -= src[j, a]; c--; }
                if (b < n) { s += src[j, b]; c++; }
            }
        }
        for (int i = 0; i < n; i++)
        {
            float s = 0f; int c = 0;
            for (int j = -r; j <= r; j++) if (j >= 0 && j < n) { s += tmp[j, i]; c++; }
            for (int j = 0; j < n; j++)
            {
                dst[j, i] = s / c;
                int a = j - r, b = j + r + 1;
                if (a >= 0) { s -= tmp[a, i]; c--; }
                if (b < n) { s += tmp[b, i]; c++; }
            }
        }
        return dst;
    }

    /// bilinear height from the computed grid (valid before the terrain exists)
    static float GridHeight(float x, float z)
    {
        float u = (x + Size / 2f) / Size * (HRes - 1), v = (z + Size / 2f) / Size * (HRes - 1);
        u = Mathf.Clamp(u, 0, HRes - 1.001f); v = Mathf.Clamp(v, 0, HRes - 1.001f);
        int i = (int)u, j = (int)v;
        float fx = u - i, fy = v - j;
        return Mathf.Lerp(Mathf.Lerp(Heights[j, i], Heights[j, i + 1], fx), Mathf.Lerp(Heights[j + 1, i], Heights[j + 1, i + 1], fx), fy);
    }

    // ---------------------------------------------------------------- terrain
    static Terrain terrain;

    static Terrain CreateTerrain(Transform parent)
    {
        string dir = $"{GenDir}/Terrain";
        IslandAssets.EnsureFolder(dir);
        var td = new TerrainData();
        td.heightmapResolution = HRes;
        td.size = new Vector3(Size, HeightRange, Size);
        td.alphamapResolution = ARes;
        td.baseMapResolution = 1024;
        td.SetDetailResolution(ARes, 32);
        var norm = new float[HRes, HRes];
        for (int j = 0; j < HRes; j++)
            for (int i = 0; i < HRes; i++)
                norm[j, i] = Mathf.Clamp01((Heights[j, i] - BaseY) / HeightRange);
        // the asset must exist before heights / splat are written, otherwise the alphamaps are not persisted
        AssetDatabase.CreateAsset(td, $"{dir}/IslandTerrain.asset");
        td.SetHeights(0, 0, norm);
        td.terrainLayers = IslandTextures.Layers;
        td.SetAlphamaps(0, 0, PaintSplat());
        td.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
        td.detailPrototypes = IslandDetails.Prototypes();
        td.SetDetailLayer(0, 0, 0, GrassDetail);
        td.SetDetailLayer(0, 0, 1, FlowerDetailA);
        td.SetDetailLayer(0, 0, 2, FlowerDetailB);
        EditorUtility.SetDirty(td);
        AssetDatabase.SaveAssets();

        var go = Terrain.CreateTerrainGameObject(td);
        go.name = "Terrain";
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(-Size / 2f, BaseY, -Size / 2f);
        var t = go.GetComponent<Terrain>();
        var mat = new Material(Shader.Find("Nature/Terrain/Standard")) { name = "IslandTerrain" };
        AssetDatabase.CreateAsset(mat, $"{dir}/IslandTerrain.mat");
        t.materialTemplate = mat;
        t.heightmapPixelError = 4f;
        t.basemapDistance = 150f;   // far terrain uses the single-pass base map (GPU-bound scene, see report)
        t.drawInstanced = true;
        t.shadowCastingMode = ShadowCastingMode.On;
        t.drawTreesAndFoliage = true;      // needed for the grass / flower details (no terrain trees are used)
        t.detailObjectDistance = 60f;
        t.detailObjectDensity = 1f;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        terrain = t;
        return t;
    }

    static float[,,] PaintSplat()
    {
        int L = IslandTextures.LayerNames.Length;
        var a = new float[ARes, ARes, L];
        GrassDetail = new int[ARes, ARes]; FlowerDetailA = new int[ARes, ARes]; FlowerDetailB = new int[ARes, ARes];
        var w = new float[L];
        float cell = Size / ARes;
        for (int j = 0; j < ARes; j++)
            for (int i = 0; i < ARes; i++)
            {
                float x = -Size / 2f + (i + 0.5f) * cell, z = -Size / 2f + (j + 0.5f) * cell;
                float h = GridHeight(x, z);
                float hx = GridHeight(x + 1f, z) - GridHeight(x - 1f, z), hz = GridHeight(x, z + 1f) - GridHeight(x, z - 1f);
                float slope = Mathf.Atan(Mathf.Sqrt(hx * hx + hz * hz) / 2f) * Mathf.Rad2Deg;
                float sd = CoastSd(x, z), wsd = WaterSd(x, z);
                float noise = Fbm(x * 0.08f, z * 0.08f, 2);

                Array.Clear(w, 0, L);
                w[IslandTextures.Grass] = 1f;
                float forest = Mathf.Max(PineZone(x, z), OakZone(x, z));
                // macro variation: patches of darker lush grass, also along the forest edges
                float macro = Fbm(x * 0.018f + 40f, z * 0.018f - 20f, 3);
                float edge = Smooth(0.05f, 0.4f, forest) * (1f - Smooth(0.6f, 0.9f, forest));
                Over(w, IslandTextures.Lush, Mathf.Clamp01(Smooth(0.0f, 0.42f, macro) * 0.8f + edge * 0.6f));
                Over(w, IslandTextures.Forest, Mathf.Clamp01(forest * (0.75f + 0.35f * noise)));
                int fi = FieldAt(x, z);
                if (fi >= 0)
                {
                    Over(w, IslandTextures.Soil, 1f);
                    if (Fields[fi].kind == 0) Over(w, IslandTextures.Dirt, 0.85f);          // straw-coloured ground under the wheat
                    else if (Fields[fi].kind == 2) Over(w, IslandTextures.Lush, 0.5f);      // greenish ground under the crop rows
                }
                else Over(w, IslandTextures.Soil, 0.8f * (1f - Smooth(0f, 0.9f, FieldDist(x, z))));
                // beaches reach down to the waterline: near the coast use the sand mask dilated by ~7 m
                float sandNear = sd < 12f ? SandNear(x, z) : 0f;
                float sand = Mathf.Max(Smooth(0.25f, 0.6f, SandMap(x, z)), Smooth(1.6f, 0.1f, sd + 0.6f * noise));
                sand = Mathf.Max(sand, Smooth(0.3f, 0.6f, sandNear) * (1f - Smooth(5f, 11f, sd + 2f * noise)));
                if (h < 0.4f) sand = Mathf.Max(sand, Mathf.Lerp(0.45f, 1f, Smooth(0.2f, 0.5f, sandNear)));   // seabed: sand off the beaches, mixed with rock elsewhere
                if (wsd < 0.6f) sand = Mathf.Max(sand, Smooth(0.6f, -0.8f, wsd));
                Over(w, IslandTextures.Sand, sand);
                float rock = Mathf.Max(Smooth(29f, 37f, slope + 3f * noise), 0.9f * Smooth(0.4f, 0.62f, RockMap(x, z) + 0.15f * noise) * (h > 0.6f ? 1f : 0.4f));
                if (h < 0.3f) rock = Mathf.Max(rock, 0.55f * (1f - Smooth(0.2f, 0.5f, sandNear)) + (h < -4f ? 0.35f : 0f));
                if (z > 112f && wsd > -0.5f && wsd < 3.5f) rock = Mathf.Max(rock, 0.85f * (1f - Smooth(2f, 3.5f, wsd + noise)));   // rocky lake rim
                Over(w, IslandTextures.Rock, rock * (1f - Smooth(0.3f, 0.6f, Road(x, z))));
                Over(w, IslandTextures.Dirt, Smooth(0.28f, 0.6f, Road(x, z) + 0.12f * noise) * (wsd > 0.5f ? 1f : 0f));
                Over(w, IslandTextures.Paved, Smooth(0.3f, 0.6f, Paved(x, z)));
                for (int k = 0; k < L; k++) a[j, i, k] = w[k];

                // grass tufts and flowers where the ground is grassy, flat enough, dry and not built on
                float grassy = w[IslandTextures.Grass] + w[IslandTextures.Lush] + 0.4f * w[IslandTextures.Forest];
                bool blocked = slope > 32f || h < 1.0f || wsd < 1.5f || FieldAt(x, z, 0.8f) >= 0 || InBuildingPad(x, z);
                float r1 = CellHash(i, j, 11), r2 = CellHash(i, j, 12);
                int g = 0;
                if (!blocked && grassy > 0.55f && r1 < grassy * 0.8f) g = r1 < 0.12f * grassy ? 2 : 1;
                GrassDetail[j, i] = g;
                float bloom = Smooth(0.15f, 0.55f, Fbm(x * 0.03f - 70f, z * 0.03f + 15f, 2));
                bool flower = !blocked && grassy > 0.8f && forest < 0.5f && r2 < 0.012f + 0.07f * bloom;
                FlowerDetailA[j, i] = flower && CellHash(i, j, 13) < 0.55f ? 1 : 0;
                FlowerDetailB[j, i] = flower && FlowerDetailA[j, i] == 0 ? 1 : 0;
            }
        return a;
    }

    static int[,] GrassDetail, FlowerDetailA, FlowerDetailB;

    static float CellHash(int i, int j, int seed)
    {
        unchecked
        {
            uint hsh = (uint)(i * 73856093) ^ (uint)(j * 19349663) ^ (uint)(seed * 83492791);
            hsh = (hsh ^ (hsh >> 13)) * 1274126177u;
            hsh ^= hsh >> 16;
            return (hsh & 0xFFFFFF) / 16777215f;
        }
    }

    static bool InBuildingPad(float x, float z)
    {
        var p = new Vector2(x, z);
        foreach (var pad in Pads)
            if (pad.name != "bridge_end" && RectDist(p, pad.c, pad.half, pad.yaw) < 0.8f) return true;
        return false;
    }

    static float FieldDist(float x, float z)
    {
        float best = float.MaxValue;
        foreach (var f in Fields) best = Mathf.Min(best, RectDist(new Vector2(x, z), f.c, f.half));
        return best;
    }

    static void Over(float[] w, int layer, float alpha)
    {
        alpha = Mathf.Clamp01(alpha);
        if (alpha <= 0f) return;
        for (int k = 0; k < w.Length; k++) w[k] *= 1f - alpha;
        w[layer] += alpha;
    }

    // ---------------------------------------------------------------- entry point
    public static void Build()
    {
        var t0 = DateTime.Now;
        IslandSetup.Configure();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AssetDatabase.DeleteAsset(ScenePath);
        AssetDatabase.DeleteAsset(GenDir);
        IslandAssets.EnsureFolder(GenDir);
        IslandAssets.EnsureFolder("Assets/Scenes");
        _cropMat = null;

        var prefabs = IslandAssets.BuildAll();
        IslandCharacters.Prepare();
        IslandTextures.BuildAll();
        IslandAssets.EnsureFolder($"{GenDir}/Meshes");
        LoadMasks();
        Counts.Clear();
        Pads.Clear();
        _channelCum = null;
        Prefabs = prefabs;
        BoundsCache.Clear();
        BottomCache.Clear();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root = new GameObject("Island").transform;

        PlanLayout();            // building positions + pads (before the terrain is written)
        ComputeHeights();
        CreateTerrain(Group(root, "Terrain_Water"));
        BuildWater(Group(root, "Terrain_Water"));
        Physics.SyncTransforms();
        PopulateZones(root);
        Physics.SyncTransforms();
        PlaceCrowd(root);
        Physics.SyncTransforms();
        PlaceSpawnPoints(Group(root, "SpawnPoints"));
        SetupEnvironment(root);
        IslandGame.Setup(root, prefabs);   // the playable third-person shooter (fifth run)

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        WriteStats(root);
        Debug.Log($"[IslandBuilder] done in {(DateTime.Now - t0).TotalSeconds:F1} s");
    }

    static Transform Group(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) return t;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}

