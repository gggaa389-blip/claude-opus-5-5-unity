using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

/// Water surfaces, bridges, lighting, camera path and level statistics.
public static partial class IslandBuilder
{
    // ---------------------------------------------------------------- water
    static Material _waterMat;

    static Color Lin(float r, float g, float b, float a)
    {
        var c = new Color(r, g, b, a).linear;
        c.a = a;
        return c;
    }

    static Color OceanColor(float depth)
    {
        Color foam = Lin(0.92f, 0.98f, 0.98f, 0.9f);
        Color shallow = Lin(0.20f, 0.78f, 0.86f, 0.66f);
        Color mid = Lin(0.02f, 0.55f, 0.80f, 0.85f);
        Color deep = Lin(0.0f, 0.30f, 0.62f, 0.96f);
        if (depth < 0.3f) return Color.Lerp(foam, shallow, Mathf.Clamp01(depth / 0.3f));
        if (depth < 3.5f) return Color.Lerp(shallow, mid, (depth - 0.3f) / 3.2f);
        return Color.Lerp(mid, deep, Smooth(3.5f, 13f, depth));
    }

    static Mesh SaveMesh(Mesh m)
    {
        AssetDatabase.CreateAsset(m, $"{GenDir}/Meshes/{m.name}.asset");
        return m;
    }

    static GameObject MeshObject(string name, Transform parent, Mesh mesh, Material mat, bool shadows = false)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        r.receiveShadows = shadows;
        return go;
    }

    static void BuildWater(Transform parent)
    {
        var g = Group(parent, "Water");
        _waterMat = new Material(Shader.Find("Island/Water")) { name = "Water" };
        _waterMat.SetTexture("_NormalTex", IslandTextures.WaterNormal);
        AssetDatabase.CreateAsset(_waterMat, $"{GenDir}/Materials/Water.mat");

        // ocean: 2 m grid over the 600 x 600 m area, colour = depth tint (foam line, turquoise shallows, deep blue)
        const int n = 301;
        var v = new Vector3[n * n]; var c = new Color[n * n]; var nr = new Vector3[n * n]; var tg = new Vector4[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = -300f + i * 2f, z = -300f + j * 2f;
                int k = j * n + i;
                v[k] = new Vector3(x, 0f, z);
                c[k] = OceanColor(-GridHeight(x, z));
                nr[k] = Vector3.up; tg[k] = new Vector4(1f, 0f, 0f, -1f);
            }
        var tris = new List<int>();
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = j * n + i;
                // skip quads that are fully under high ground (inland)
                float h = Mathf.Min(Mathf.Min(GridHeight(v[a].x, v[a].z), GridHeight(v[a + 1].x, v[a + 1].z)),
                                    Mathf.Min(GridHeight(v[a + n].x, v[a + n].z), GridHeight(v[a + n + 1].x, v[a + n + 1].z)));
                if (h > 1.5f) continue;
                tris.AddRange(new[] { a, a + n, a + 1, a + 1, a + n, a + n + 1 });
            }
        var ocean = new Mesh { name = "ocean_near", indexFormat = IndexFormat.UInt32 };
        ocean.vertices = v; ocean.colors = c; ocean.normals = nr; ocean.tangents = tg;
        ocean.SetTriangles(tris, 0);
        ocean.RecalculateBounds();
        MeshObject("Ocean", g, SaveMesh(ocean), _waterMat);

        // far ocean ring out to the horizon
        // (overlaps the near grid by 6 m and sits 3 cm lower, so there is no seam at the 300 m border)
        float[] e = { -6000f, -294f, 294f, 6000f };
        var fv = new List<Vector3>(); var fc = new List<Color>(); var ft = new List<int>();
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) { fv.Add(new Vector3(e[i], -0.03f, e[j])); fc.Add(OceanColor(30f)); }
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                if (i == 1 && j == 1) continue;
                int a = j * 4 + i;
                ft.AddRange(new[] { a, a + 4, a + 1, a + 1, a + 4, a + 5 });
            }
        var far = new Mesh { name = "ocean_far" };
        far.SetVertices(fv); far.SetColors(fc); far.SetTriangles(ft, 0);
        far.normals = fv.Select(_ => Vector3.up).ToArray();
        far.tangents = fv.Select(_ => new Vector4(1f, 0f, 0f, -1f)).ToArray();
        far.RecalculateBounds();
        MeshObject("OceanFar", g, SaveMesh(far), _waterMat);

        // lake and river: 1 m grid wherever the water mask is, surface at the river level for that z
        float x0 = -60f, x1 = 85f, z0 = -125f, z1 = 180f;
        int nx = Mathf.RoundToInt(x1 - x0) + 1, nz = Mathf.RoundToInt(z1 - z0) + 1;
        var rv = new List<Vector3>(); var rc = new List<Color>(); var rt = new List<int>();
        var index = new int[nx * nz];
        var wsd = new float[nx * nz];
        for (int j = 0; j < nz; j++) for (int i = 0; i < nx; i++) wsd[j * nx + i] = WaterSd(x0 + i, z0 + j);
        for (int k = 0; k < index.Length; k++) index[k] = -1;
        int V(int i, int j)
        {
            int k = j * nx + i;
            if (index[k] >= 0) return index[k];
            float x = x0 + i, z = z0 + j;
            index[k] = rv.Count;
            rv.Add(new Vector3(x, WaterLevel(z), z));
            float edge = Smooth(-0.5f, -4f, wsd[k]);
            rc.Add(Color.Lerp(Lin(0.26f, 0.66f, 0.72f, 0.6f), Lin(0.04f, 0.46f, 0.70f, 0.88f), edge));
            return index[k];
        }
        for (int j = 0; j < nz - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                float m = Mathf.Min(Mathf.Min(wsd[j * nx + i], wsd[j * nx + i + 1]), Mathf.Min(wsd[(j + 1) * nx + i], wsd[(j + 1) * nx + i + 1]));
                if (m > 1.2f) continue;
                if (z0 + j < -112f && wsd[j * nx + i] > -1f) continue; // the channel ribbon takes over below the river end
                int a = V(i, j), b = V(i + 1, j), cc = V(i, j + 1), d = V(i + 1, j + 1);
                rt.AddRange(new[] { a, cc, b, b, cc, d });
            }
        var river = new Mesh { name = "lake_river", indexFormat = IndexFormat.UInt32 };
        river.SetVertices(rv); river.SetColors(rc); river.SetTriangles(rt, 0);
        river.normals = rv.Select(_ => Vector3.up).ToArray();
        river.tangents = rv.Select(_ => new Vector4(1f, 0f, 0f, -1f)).ToArray();
        river.RecalculateBounds();
        // inland water: calmer, finer ripples and fewer sun glints than the sea
        var inland = new Material(_waterMat) { name = "WaterInland" };
        inland.SetFloat("_NormalStrength", 0.3f);
        inland.SetFloat("_Tiling", 9f);
        inland.SetFloat("_Glossiness", 0.84f);
        AssetDatabase.CreateAsset(inland, $"{GenDir}/Materials/WaterInland.mat");
        MeshObject("LakeAndRiver", g, SaveMesh(river), inland);

        // channel and waterfall from the river end to the sea
        float sStart = 0f;
        for (float s = 0f; s < ChannelLength; s += 0.5f) { var p = ChannelPoint(s); if (WaterSd(p.x, p.y) > -1.5f) { sStart = s; break; } }
        var cv = new List<Vector3>(); var cc2 = new List<Color>(); var ct = new List<int>();
        int segs = 0;
        for (float s = sStart; s <= ChannelLength; s += 0.75f, segs++)
        {
            var p = ChannelPoint(s);
            if (CoastSd(p.x, p.y) < -1.5f) break;   // the falls end in the sea
            var p2 = ChannelPoint(Mathf.Min(s + 0.5f, ChannelLength));
            var dir = (p2 - p).sqrMagnitude > 1e-6f ? (p2 - p).normalized : Vector2.up;
            var side = new Vector2(dir.y, -dir.x);
            float wl = ChannelLevel(s);
            float steep = Mathf.Abs(ChannelLevel(s + 0.75f) - wl) / 0.75f;
            Color col = Color.Lerp(Lin(0.20f, 0.66f, 0.82f, 0.7f), Lin(0.92f, 0.97f, 1f, 0.9f), Smooth(0.08f, 0.35f, steep));
            for (int k = -1; k <= 1; k += 2)
            {
                var q = p + side * k * 4.2f;
                cv.Add(new Vector3(q.x, wl, q.y)); cc2.Add(col);
            }
            if (segs > 0)
            {
                int a = (segs - 1) * 2;
                ct.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
            }
        }
        var channel = new Mesh { name = "waterfall_channel" };
        channel.SetVertices(cv); channel.SetColors(cc2); channel.SetTriangles(ct, 0);
        channel.RecalculateNormals();
        channel.tangents = cv.Select(_ => new Vector4(1f, 0f, 0f, -1f)).ToArray();
        // two-sided winding check: make sure normals point up
        var nn = channel.normals;
        if (nn.Length > 0 && nn[0].y < 0f) { ct.Reverse(); channel.SetTriangles(ct, 0); channel.RecalculateNormals(); }
        channel.RecalculateBounds();
        MeshObject("RiverMouthWaterfall", g, SaveMesh(channel), inland);
    }

    // ---------------------------------------------------------------- bridges
    static Material _wood, _woodDark;

    static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat, bool collider = true)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (!collider) Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        return go;
    }

    static void BuildBridge(string name, BridgeSpec b, Transform parent)
    {
        if (_wood == null || !AssetDatabase.Contains(_wood))
        {
            _wood = new Material(Shader.Find("Standard")) { name = "bridge_wood", enableInstancing = true };
            _wood.mainTexture = IslandTextures.WoodTex;
            _wood.SetFloat("_Glossiness", 0.15f);
            AssetDatabase.CreateAsset(_wood, $"{GenDir}/Materials/bridge_wood.mat");
            _woodDark = new Material(_wood) { name = "bridge_wood_dark" };
            _woodDark.color = new Color(0.72f, 0.66f, 0.6f);
            AssetDatabase.CreateAsset(_woodDark, $"{GenDir}/Materials/bridge_wood_dark.mat");
        }
        var root = new GameObject(name).transform;
        root.SetParent(parent, false);
        root.position = new Vector3(b.c.x, 0f, b.c.y);
        root.rotation = Quaternion.Euler(0f, b.yaw, 0f);
        float L = b.length, W = b.width, top = b.deckY;
        // deck planks across the bridge
        int n = Mathf.RoundToInt(L / 0.5f);
        float pitch = L / n;
        for (int k = 0; k < n; k++)
        {
            float z = -L / 2f + (k + 0.5f) * pitch;
            float jitter = ((k * 7919) % 11) / 11f * 0.04f;
            Box("Plank", root, new Vector3(0f, top - 0.07f + jitter * 0.3f, z), new Vector3(W, 0.12f, pitch - 0.05f), k % 3 == 0 ? _woodDark : _wood);
        }
        // stringers, cross beams, piers
        foreach (float x in new[] { -W / 2f + 0.5f, W / 2f - 0.5f })
            Box("Stringer", root, new Vector3(x, top - 0.33f, 0f), new Vector3(0.32f, 0.4f, L + 0.4f), _woodDark, false);
        for (float z = -L / 2f + 0.6f; z <= L / 2f - 0.5f; z += 2.4f)
            Box("CrossBeam", root, new Vector3(0f, top - 0.6f, z), new Vector3(W + 0.4f, 0.22f, 0.26f), _woodDark, false);
        float half = (L - 11f) / 2f;
        var pierZ = new List<float> { -half - 0.3f, half + 0.3f };
        if (L - 11f > 10f) pierZ.Add(0f);
        foreach (float z in pierZ)
            foreach (float x in new[] { -W / 2f + 0.5f, W / 2f - 0.5f })
            {
                float bottom = b.water - 3.2f, ptop = top - 0.5f;
                Box("Pier", root, new Vector3(x, (bottom + ptop) / 2f, z), new Vector3(0.42f, ptop - bottom, 0.42f), _woodDark);
            }
        // railings
        foreach (float x in new[] { -W / 2f + 0.1f, W / 2f - 0.1f })
        {
            for (float z = -L / 2f + 0.2f; z <= L / 2f - 0.1f; z += 2.4f)
                Box("RailPost", root, new Vector3(x, top + 0.55f, z), new Vector3(0.18f, 1.1f, 0.18f), _woodDark);
            Box("TopRail", root, new Vector3(x, top + 1.05f, 0f), new Vector3(0.14f, 0.14f, L), _wood);
            Box("MidRail", root, new Vector3(x, top + 0.58f, 0f), new Vector3(0.1f, 0.1f, L), _wood);
        }
        Counts["bridge (procedural)"] = Counts.TryGetValue("bridge (procedural)", out var cnt) ? cnt + 1 : 1;
    }

    // ---------------------------------------------------------------- lighting, sky, cameras
    static void SetupEnvironment(Transform root)
    {
        var env = Group(root, "Lighting_Cameras");
        var sunGo = new GameObject("Sun");
        sunGo.transform.SetParent(env, false);
        sunGo.transform.rotation = Quaternion.Euler(47f, 135f, 0f);   // from the north-west, shadows fall south-east like on the map
        var sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.93f, 0.82f);
        sun.intensity = 1.35f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.85f;
        sun.shadowBias = 0.04f;
        sun.shadowNormalBias = 0.35f;

        // procedural sky with drifting clouds (Assets/Shaders/SkyClouds.shader)
        var sky = new Material(Shader.Find("Island/SkyClouds")) { name = "IslandSky" };
        AssetDatabase.CreateAsset(sky, $"{GenDir}/Materials/IslandSky.mat");
        RenderSettings.skybox = sky;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.60f, 0.70f, 0.86f);
        RenderSettings.ambientEquatorColor = new Color(0.55f, 0.58f, 0.52f);
        RenderSettings.ambientGroundColor = new Color(0.33f, 0.30f, 0.24f);
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = new Color(0.70f, 0.82f, 0.93f);
        RenderSettings.fogDensity = 0.0012f;

        // sky-only reflection cubemap for metals and water
        var cubeCamGo = new GameObject("CubeCam");
        var cubeCam = cubeCamGo.AddComponent<Camera>();
        cubeCam.cullingMask = 0;
        cubeCam.clearFlags = CameraClearFlags.Skybox;
        var cube = new Cubemap(256, TextureFormat.RGBA32, true) { name = "SkyReflection" };
        bool ok = cubeCam.RenderToCubemap(cube);
        Object.DestroyImmediate(cubeCamGo);
        if (ok)
        {
            AssetDatabase.CreateAsset(cube, $"{GenDir}/SkyReflection.cubemap");
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = cube;
            RenderSettings.reflectionIntensity = 0.9f;
        }
        else Debug.LogWarning("[IslandBuilder] RenderToCubemap failed, no custom reflections");

        // post-processing: one global volume on its own layer
        var volGo = new GameObject("PostFX_GlobalVolume") { layer = IslandSetup.PostLayer };
        volGo.transform.SetParent(env, false);
        var vol = volGo.AddComponent<PostProcessVolume>();
        vol.isGlobal = true;
        vol.priority = 0f;
        vol.sharedProfile = BuildPostProfile();

        // main camera: player eye height (1.7 m) at the village spawn, looking at the plaza
        Vector3 eye = new Vector3(PlazaC.x + 14f, 0f, PlazaC.y - 12f);
        eye.y = GH(eye.x, eye.z) + 1.7f;
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        camGo.transform.SetParent(env, false);
        camGo.transform.position = eye;
        camGo.transform.rotation = Quaternion.LookRotation(new Vector3(PlazaC.x, eye.y, PlazaC.y) - eye);
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 60f;
        cam.nearClipPlane = 0.2f;
        cam.farClipPlane = 2500f;
        cam.allowHDR = true;
        cam.allowMSAA = true;
        AddPostFx(cam);
        camGo.AddComponent<AudioListener>();

        var sys = new GameObject("Systems");
        sys.transform.SetParent(env, false);
        var path = sys.AddComponent<FlyPath>();
        BuildFlyPath(path);
        var bench = sys.AddComponent<Benchmark>();
        bench.path = path;
        bench.cam = cam;
        var clock = sys.AddComponent<WaterClock>();
        var bus = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Match.BusObject);
        if (bus != null) clock.propellers = bus.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("propeller_") && !t.name.Contains("mount")).ToArray();
    }

    public const string PostProfilePath = GenDir + "/PostFX.asset";
    const string PostResourcesPath = "Packages/com.unity.postprocessing/PostProcessing/PostProcessResources.asset";

    /// ambient occlusion for contact shading, mild bloom, neutral tone mapping with a warm, slightly
    /// punchier grade, and a light vignette
    static PostProcessProfile BuildPostProfile()
    {
        var profile = ScriptableObject.CreateInstance<PostProcessProfile>();
        var ao = profile.AddSettings<AmbientOcclusion>();
        ao.enabled.Override(true);
        ao.mode.Override(AmbientOcclusionMode.MultiScaleVolumetricObscurance);
        ao.intensity.Override(0.7f);
        ao.thicknessModifier.Override(1.6f);
        ao.color.Override(new Color(0.08f, 0.1f, 0.12f));
        var bloom = profile.AddSettings<Bloom>();
        bloom.enabled.Override(true);
        bloom.intensity.Override(0.45f);
        bloom.threshold.Override(1.1f);
        bloom.softKnee.Override(0.6f);
        bloom.diffusion.Override(7f);
        var cg = profile.AddSettings<ColorGrading>();
        cg.enabled.Override(true);
        cg.gradingMode.Override(GradingMode.HighDefinitionRange);
        cg.tonemapper.Override(Tonemapper.Neutral);
        cg.postExposure.Override(0.15f);
        cg.temperature.Override(4f);
        cg.saturation.Override(12f);
        cg.contrast.Override(12f);
        var vig = profile.AddSettings<Vignette>();
        vig.enabled.Override(true);
        vig.intensity.Override(0.18f);
        vig.smoothness.Override(0.45f);
        AssetDatabase.CreateAsset(profile, PostProfilePath);
        foreach (var setting in profile.settings) AssetDatabase.AddObjectToAsset(setting, profile);
        AssetDatabase.SaveAssets();
        return profile;
    }

    /// post-process layer for a camera (main camera, capture cameras)
    public static PostProcessLayer AddPostFx(Camera cam)
    {
        var layer = cam.gameObject.GetComponent<PostProcessLayer>();
        if (layer == null) layer = cam.gameObject.AddComponent<PostProcessLayer>();
        layer.Init(AssetDatabase.LoadAssetAtPath<PostProcessResources>(PostResourcesPath));
        layer.volumeLayer = 1 << IslandSetup.PostLayer;
        layer.volumeTrigger = cam.transform;
        layer.antialiasingMode = PostProcessLayer.Antialiasing.None;   // MSAA x4 from the quality settings
        return layer;
    }

    static Vector3 Air(float x, float z, float above) => new Vector3(x, Mathf.Max(GH(x, z), 0f) + above, z);
    static Vector3 Gnd(float x, float z, float above = 0f) => new Vector3(x, Mathf.Max(GH(x, z), 0f) + above, z);

    /// 40 s flight over all zones: south cliff -> industrial yard -> oak forest -> north tower,
    /// lake and pines -> farm -> east beach -> south-east hill -> south bridge -> village.
    /// Waypoints are (x, height above ground, z); the spline is then checked for clearance.
    static void BuildFlyPath(FlyPath path)
    {
        path.duration = 40f;
        Vector3[] wp =
        {
            new Vector3(30f, 22f, -250f), new Vector3(-15f, 22f, -192f), new Vector3(-50f, 26f, -138f), new Vector3(-92f, 24f, -100f),
            new Vector3(-150f, 26f, -42f), new Vector3(-168f, 26f, 28f), new Vector3(-138f, 26f, 100f), new Vector3(-80f, 24f, 140f),
            new Vector3(-28f, 26f, 182f), new Vector3(28f, 24f, 165f), new Vector3(72f, 24f, 150f), new Vector3(116f, 24f, 160f),
            new Vector3(182f, 20f, 118f), new Vector3(198f, 16f, 30f), new Vector3(188f, 18f, -52f), new Vector3(152f, 22f, -100f),
            new Vector3(92f, 20f, -72f), new Vector3(54f, 16f, -32f), new Vector3(30f, 17f, -8f), new Vector3(10f, 26f, 22f), new Vector3(-42f, 44f, 72f),
        };
        path.points = wp.Select(p => Air(p.x, p.z, p.y)).ToArray();
        path.targets = new[]
        {
            Gnd(-2f, -149f, 8f), Gnd(-2f, -149f, 6f), Gnd(-80f, -75f), Gnd(-100f, -60f),
            Gnd(-130f, 0f), Gnd(-120f, 60f), Gnd(-91f, 165f, 4f), Gnd(-10f, 145f),
            Gnd(10f, 140f), Gnd(60f, 120f), Gnd(112f, 115f), Gnd(140f, 100f),
            Gnd(180f, 40f), Gnd(180f, -40f), Gnd(138f, -60f, 5f), Gnd(100f, -60f),
            Gnd(48f, -48f), Gnd(10f, 0f), Gnd(0f, 9f, 2f), Gnd(-8f, 22f), Gnd(2f, 4f),
        };
        // clearance check: lowest distance between the camera and anything below it along the flight
        Physics.SyncTransforms();
        float minClear = float.MaxValue; Vector3 at = Vector3.zero;
        for (int i = 0; i <= 2000; i++)
        {
            path.Evaluate(i / 2000f, out var p, out _);
            float clear = p.y - Mathf.Max(GH(p.x, p.z), 0f);
            if (Physics.SphereCast(p + Vector3.up * 2f, 1.5f, Vector3.down, out var hit, 200f)) clear = Mathf.Min(clear, hit.distance - 0.5f);
            if (clear < minClear) { minClear = clear; at = p; }
        }
        Debug.Log($"[IslandBuilder] flythrough length {path.Length:F0} m, min clearance {minClear:F1} m at {at}");
    }

    // ---------------------------------------------------------------- statistics for the report
    static void WriteStats(Transform root)
    {
        long tris = 0;
        int renderers = 0;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var m = mf.sharedMesh;
            if (m == null) continue;
            renderers++;
            for (int s = 0; s < m.subMeshCount; s++) tris += m.GetIndexCount(s) / 3;
        }
        long waterTris = 0;
        foreach (var mf in root.Find("Terrain_Water/Water").GetComponentsInChildren<MeshFilter>(true))
            for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) waterTris += mf.sharedMesh.GetIndexCount(s) / 3;
        var sb = new StringBuilder();
        sb.AppendLine("# Level statistics (written by IslandBuilder.Build)");
        sb.AppendLine($"terrain_size_m: {Size} x {Size}, height range {BaseY}..{BaseY + HeightRange} m");
        sb.AppendLine($"heightmap_resolution: {HRes} x {HRes} ({Size / (HRes - 1):F3} m per sample), splatmap {ARes} x {ARes}, layers {IslandTextures.LayerNames.Length}");
        sb.AppendLine($"terrain_full_res_triangles: {2L * (HRes - 1) * (HRes - 1)} (rendered with terrain LOD, pixel error 4, base map beyond 150 m)");
        sb.AppendLine($"mesh_triangles_in_scene (all MeshFilters, excluding terrain): {tris}");
        sb.AppendLine($"  of which water meshes: {waterTris}");
        sb.AppendLine($"mesh_renderers: {renderers}");
        long skinTris = 0; int skinned = 0;
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null) continue;
            skinned++;
            for (int s = 0; s < smr.sharedMesh.subMeshCount; s++) skinTris += smr.sharedMesh.GetIndexCount(s) / 3;
        }
        sb.AppendLine($"skinned_mesh_renderers (Mixamo crowd): {skinned}, triangles {skinTris}");
        foreach (var kv in IslandCharacters.ClipSpeed) sb.AppendLine($"mixamo_clip {kv.Key}: ground speed {kv.Value:F2} m/s, length {IslandCharacters.ClipAssets[kv.Key].length:F2} s");
        foreach (var l in CrowdLog) sb.AppendLine("crowd " + l);
        sb.AppendLine("placed_objects_per_asset:");
        foreach (var kv in Counts.OrderBy(k => k.Key)) sb.AppendLine($"  {kv.Key}: {kv.Value}");
        sb.AppendLine($"placed_objects_total: {Counts.Values.Sum()}");
        sb.AppendLine("pads:");
        foreach (var p in Pads) sb.AppendLine($"  {p.name} at ({p.c.x:F1}, {p.c.y:F1}) h={p.h:F2}");
        sb.AppendLine($"bridge_north: centre ({Bridge1.c.x:F1}, {Bridge1.c.y:F1}) yaw {Bridge1.yaw:F1} length {Bridge1.length:F1} deck {Bridge1.deckY:F2} water {Bridge1.water:F2}");
        sb.AppendLine($"bridge_south: centre ({Bridge2.c.x:F1}, {Bridge2.c.y:F1}) yaw {Bridge2.yaw:F1} length {Bridge2.length:F1} deck {Bridge2.deckY:F2} water {Bridge2.water:F2}");
        sb.AppendLine("spawn_points:");
        foreach (Transform t in root.Find("SpawnPoints")) sb.AppendLine($"  {t.name}: ({t.position.x:F1}, {t.position.y:F2}, {t.position.z:F1})");
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "logs", "level_stats.txt"), sb.ToString());
        Debug.Log(sb.ToString());
    }
}
