using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

/// The playable battle royale (fifth and sixth run, on the user's request): effect textures and
/// materials, HUD art, the characters' Animator Controller (one 8-direction blend tree per weapon
/// stance, skydive, glide and death states, an upper-body action layer), the player prefab (Aj) and
/// one bot prefab per outfit in IslandCharacters.BotSkins, each with all test 1 weapons and consumables
/// fitted to the right hand and a glider, the island's navigation mesh, and the GameBootstrap object in
/// the scene. (Remy, Kate and Y Bot played these parts until the thirteenth run.) Called by
/// IslandBuilder.Build.
public static class IslandGame
{
    public const string Dir = IslandBuilder.GenDir + "/Game";
    public const float Walk = 2.1f, Run = 4.6f, Sprint = 6.4f, BackMul = 0.78f;

    public static void Setup(Transform root, Dictionary<string, GameObject> prefabs)
    {
        EnsureLayers();
        IslandAssets.EnsureFolder(Dir);
        var trav = BuildTraversalArt(prefabs);
        var assets = BuildAssets(prefabs);
        (assets.launchPad, assets.riftMat, assets.zipPoleMat, assets.zipCapMat, assets.zipCableMat) = trav;
        assets.podiumMat = AssetDatabase.LoadAssetAtPath<Material>($"{Dir}/lobby_podium.mat");
        assets.podiumRingMat = AssetDatabase.LoadAssetAtPath<Material>($"{Dir}/lobby_ring.mat");
        assets.supplyFalling = prefabs.TryGetValue("supply_drop_falling", out var sf) ? sf : AssetDatabase.LoadAssetAtPath<GameObject>(IslandAssets.PrefabRoot + "/supply_drop/supply_drop_falling.prefab");
        assets.supplyLanded = prefabs.TryGetValue("supply_drop_landed", out var sl) ? sl : AssetDatabase.LoadAssetAtPath<GameObject>(IslandAssets.PrefabRoot + "/supply_drop/supply_drop_landed.prefab");
        var controller = BuildController();
        var glider = BuildGliderMesh();
        var player = BuildCharacter(IslandCharacters.PlayerSkin, "Player", true, controller, prefabs, glider);
        assets.botPrefabs = IslandCharacters.BotSkins.Select(s => BuildCharacter(s, "Bot_" + s, false, controller, prefabs, glider)).ToArray();
        // the locker's outfits: the player in each of them, with a name, a rarity and a portrait for its tile
        var outfits = IslandCharacters.BotSkins.Prepend(IslandCharacters.PlayerSkin).ToArray();
        assets.playerPrefabs = outfits.Select((s, i) => i == 0 ? player : BuildCharacter(s, "Player_" + s, true, controller, prefabs, glider)).ToArray();
        assets.outfitNames = outfits.Select(IslandCharacters.OutfitName).ToArray();
        assets.outfitRarity = outfits.Select(IslandCharacters.OutfitRarity).ToArray();
        assets.outfitIcons = BuildOutfitIcons(assets.playerPrefabs, outfits);
        assets.itemPrefabs = WeaponTable.All.Select(d => prefabs[d.id]).Concat(WeaponTable.Consumables.Select(c => prefabs[c.id])).Append(prefabs["ammo_box"]).ToArray();
        assets.navMesh = BuildNavMesh();
        BuildPieces(assets, prefabs);
        BuildWaterColliders(root);
        if (IslandCharacters.ClipHeights.TryGetValue("Swim", out var sh)) assets.swimDrop = sh.x - 0.02f;          // head at the surface
        if (IslandCharacters.ClipHeights.TryGetValue("TreadWater", out var th)) assets.treadDrop = th.x - 0.3f;   // head 0.3 m out
        if (C("V_Mantle") != null) assets.mantleLength = C("V_Mantle").length;
        EditorUtility.SetDirty(assets);
        AssetDatabase.SaveAssets();

        var sys = root.GetComponentsInChildren<Transform>(true).First(t => t.name == "Systems");
        var boot = sys.gameObject.AddComponent<GameBootstrap>();
        boot.playerPrefab = player;
        boot.assets = assets;
        boot.spawnPoints = root.Find("SpawnPoints");
        boot.islandRoot = root;
        Debug.Log($"[IslandGame] player and {assets.botPrefabs.Length} bot prefabs, animator ({controller.layers.Length} layers), nav mesh, game bootstrap ready");
    }

    public static void EnsureLayers()
    {
        var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tags.FindProperty("layers");
        bool dirty = false;
        foreach (var (i, n) in new[] { (GameLayers.Player, "Player"), (GameLayers.Hitbox, "Hitbox") })
            if (layers.GetArrayElementAtIndex(i).stringValue != n) { layers.GetArrayElementAtIndex(i).stringValue = n; dirty = true; }
        if (dirty) tags.ApplyModifiedPropertiesWithoutUndo();
    }

    // ---------------------------------------------------------------- traversal art (fifth step)

    static Material LitMat(string name, Color c, float metal, float smooth, Color emission = default)
    {
        var m = new Material(Shader.Find("Standard")) { name = name, color = c };
        m.SetFloat("_Metallic", metal);
        m.SetFloat("_Glossiness", smooth);
        if (emission.maxColorComponent > 0f)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        AssetDatabase.CreateAsset(m, $"{Dir}/{name}.mat");
        return m;
    }

    static GameObject Prim(PrimitiveType t, Transform parent, Vector3 pos, Vector3 scale, Material m, float yaw = 0f)
    {
        var g = GameObject.CreatePrimitive(t);
        Object.DestroyImmediate(g.GetComponent<Collider>());
        g.transform.SetParent(parent, false);
        g.transform.localPosition = pos;
        g.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        g.transform.localScale = scale;
        g.GetComponent<Renderer>().sharedMaterial = m;
        return g;
    }

    /// no models exist for these in test 1: a launch pad (dark disc, glowing blue ring, arrow), a
    /// Rift-To-Go orb with a swirling purple texture, zip line poles and cable materials
    static (GameObject, Material, Material, Material, Material) BuildTraversalArt(Dictionary<string, GameObject> prefabs)
    {
        var padBase = LitMat("pad_base", new Color(0.16f, 0.18f, 0.24f), 0.6f, 0.65f);
        var padTop = LitMat("pad_top", new Color(0.08f, 0.2f, 0.42f), 0.2f, 0.7f, new Color(0.05f, 0.25f, 0.6f));
        var padGlow = LitMat("pad_glow", new Color(0.3f, 0.75f, 1f), 0f, 0.5f, new Color(0.4f, 1.3f, 2.6f));
        var padArrow = LitMat("pad_arrow", new Color(1f, 0.95f, 0.6f), 0f, 0.5f, new Color(2.2f, 1.9f, 0.9f));
        GameObject Pad(string name, float d)
        {
            var root = new GameObject(name);
            Prim(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 0.07f, 0f), new Vector3(d, 0.07f, d), padBase);
            Prim(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 0.15f, 0f), new Vector3(d * 0.86f, 0.012f, d * 0.86f), padGlow);
            Prim(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 0.165f, 0f), new Vector3(d * 0.72f, 0.012f, d * 0.72f), padTop);
            for (int k = 0; k < 2; k++)   // two chevrons pointing up the pad
                foreach (var s in new[] { -1f, 1f })
                    Prim(PrimitiveType.Cube, root.transform, new Vector3(s * d * 0.09f, 0.18f, -d * 0.12f + k * d * 0.2f), new Vector3(d * 0.06f, 0.015f, d * 0.26f), padArrow, s * -38f);
            string path = $"{Dir}/{name}.prefab";
            var pf = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return pf;
        }
        prefabs["launch_pad"] = Pad("launch_pad", 0.9f);
        var world = Pad("LaunchPadWorld", 2.8f);

        // a swirl: spiral arms in purple and blue fading out at the rim
        var swirl = SaveTex("rift_swirl", 256, 256, (u, v) =>
        {
            float x = u * 2f - 1f, y = v * 2f - 1f, r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
            float arms = 0.5f + 0.5f * Mathf.Sin(a * 3f + r * 11f);
            float fade = Mathf.Clamp01(1f - r) * Mathf.Clamp01(r * 3f + 0.3f);
            var c = Color.Lerp(new Color(0.55f, 0.25f, 1f), new Color(0.35f, 0.8f, 1f), arms);
            c = Color.Lerp(c, Color.white, Mathf.Clamp01(0.35f - r) * 2f);
            return new Color(c.r, c.g, c.b, Mathf.Clamp01(fade * (0.35f + 0.65f * arms)));
        });
        var rift = new Material(Shader.Find("Legacy Shaders/Particles/Additive")) { name = "rift" };
        rift.mainTexture = swirl;
        rift.SetColor("_TintColor", new Color(0.8f, 0.5f, 1f, 0.85f));
        AssetDatabase.CreateAsset(rift, $"{Dir}/rift.mat");
        {
            var root = new GameObject("rift_to_go");
            Prim(PrimitiveType.Sphere, root.transform, new Vector3(0f, 0.16f, 0f), Vector3.one * 0.3f, LitMat("rift_orb", new Color(0.25f, 0.1f, 0.45f), 0.3f, 0.9f, new Color(0.9f, 0.45f, 2.2f)));
            Prim(PrimitiveType.Sphere, root.transform, new Vector3(0f, 0.16f, 0f), Vector3.one * 0.38f, rift);
            prefabs["rift_to_go"] = PrefabUtility.SaveAsPrefabAsset(root, $"{Dir}/rift_to_go.prefab");
            Object.DestroyImmediate(root);
        }
        var pole = LitMat("zip_pole", new Color(0.32f, 0.34f, 0.38f), 0.7f, 0.5f);
        var cap = LitMat("zip_cap", new Color(1f, 0.78f, 0.15f), 0.2f, 0.6f, new Color(0.5f, 0.35f, 0.05f));
        var cable = LitMat("zip_cable", new Color(0.08f, 0.08f, 0.09f), 0.6f, 0.4f);
        LitMat("lobby_podium", new Color(0.82f, 0.86f, 0.94f), 0.15f, 0.55f);
        LitMat("lobby_ring", new Color(0.35f, 0.75f, 1f), 0f, 0.5f, new Color(0.6f, 1.6f, 3.2f));
        Debug.Log("[IslandGame] traversal art: launch pads, rift, zip line materials");
        return (world, rift, pole, cap, cable);
    }

    // ---------------------------------------------------------------- effect textures and materials

    static Texture2D SaveTex(string name, int w, int h, System.Func<float, float, Color> f)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = f((x + 0.5f) / w, (y + 0.5f) / h);
        t.SetPixels(px);
        t.Apply();
        string path = $"{Dir}/{name}.png";
        File.WriteAllBytes(path, t.EncodeToPNG());
        Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = true;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.mipmapEnabled = name != "scope";
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Material SaveMat(string name, string shader, Texture tex, Color tint)
    {
        var m = new Material(Shader.Find(shader)) { name = name };
        m.mainTexture = tex;
        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", tint);
        m.renderQueue = 3100;
        AssetDatabase.CreateAsset(m, $"{Dir}/{name}.mat");
        return m;
    }

    static GameAssets BuildAssets(Dictionary<string, GameObject> prefabs)
    {
        float Soft(float u, float v) { float r = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f; return Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r); }
        var soft = SaveTex("soft_dot", 64, 64, (u, v) => new Color(1, 1, 1, Soft(u, v)));
        var smokeTex = SaveTex("smoke", 128, 128, (u, v) =>
        {
            float n = Mathf.PerlinNoise(u * 5f, v * 5f) * 0.6f + Mathf.PerlinNoise(u * 11f + 3f, v * 11f) * 0.4f;
            return new Color(1, 1, 1, Mathf.Clamp01(Soft(u, v) * 1.6f * (0.55f + n)));
        });
        var flashTex = SaveTex("muzzle_flash", 128, 128, (u, v) =>
        {
            var d = new Vector2(u - 0.5f, v - 0.5f);
            float r = d.magnitude * 2f, a = Mathf.Atan2(d.y, d.x);
            float star = Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 3f)), 8f) * Mathf.Clamp01(1f - r * 0.9f);
            float core = Mathf.Clamp01(1f - r * 2.2f);
            float i = Mathf.Clamp01(core + star);
            return new Color(1f, 0.85f + 0.15f * core, 0.55f + 0.45f * core, i);
        });
        var hole = SaveTex("bullet_hole", 64, 64, (u, v) =>
        {
            float r = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f;
            float a = r < 0.35f ? 0.95f : Mathf.Clamp01((1f - r) / 0.65f) * 0.55f;
            return new Color(0.05f, 0.045f, 0.04f, a);
        });
        var tracerTex = SaveTex("tracer", 64, 16, (u, v) => new Color(1, 1, 1, Mathf.Pow(Mathf.Sin(v * Mathf.PI), 3f)));
        var scope = SaveTex("scope", 512, 512, (u, v) =>
        {
            float r = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f;
            float a = Mathf.Clamp01((r - 0.93f) / 0.05f);
            return new Color(0, 0, 0, a);
        });

        var a = ScriptableObject.CreateInstance<GameAssets>();
        a.additive = SaveMat("fx_additive", "Legacy Shaders/Particles/Additive", soft, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        a.smoke = SaveMat("fx_smoke", "Legacy Shaders/Particles/Alpha Blended", smokeTex, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        a.decal = SaveMat("fx_bullet_hole", "Legacy Shaders/Particles/Alpha Blended", hole, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        a.decal.renderQueue = 2460;   // before other transparents, after opaque geometry
        a.tracer = SaveMat("fx_tracer", "Legacy Shaders/Particles/Additive", tracerTex, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        a.flash = SaveMat("fx_muzzle_flash", "Legacy Shaders/Particles/Additive", flashTex, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        a.softDot = soft;
        a.scope = scope;
        BuildHudArt(a, prefabs);
        a.stormMat = new Material(Shader.Find("Island/StormWall")) { name = "storm_wall" };
        AssetDatabase.CreateAsset(a.stormMat, $"{Dir}/storm_wall.mat");
        AssetDatabase.CreateAsset(a, $"{Dir}/GameAssets.asset");
        return a;
    }

    // ---------------------------------------------------------------- HUD art

    static Color Mask(float d, float edge = 0.04f) => new Color(1, 1, 1, Mathf.Clamp01(0.5f - d / edge));

    /// dark figure on a disc (the round counter icons under the minimap)
    static Color OnDisc(float x, float y, float figure, Color disc, Color ink)
    {
        float a1 = Mathf.Clamp01(0.5f - (new Vector2(x, y).magnitude - 0.98f) / 0.06f);
        float f = Mathf.Clamp01(0.5f - figure / 0.06f);
        var c = Color.Lerp(disc, ink, f);
        c.a = a1;
        return c;
    }

    static void BuildHudArt(GameAssets a, Dictionary<string, GameObject> prefabs)
    {
        // icons as signed distances on a -1..1 square (negative inside)
        a.iconShield = SaveTex("icon_shield", 64, 64, (u, v) =>
        {
            float x = (u - 0.5f) * 2f, y = (v - 0.5f) * 2f;
            float half = y > 0.1f ? 0.72f : 0.72f * Mathf.Clamp01((y + 0.95f) / 1.05f);
            float d = Mathf.Max(Mathf.Max(Mathf.Abs(x) - half, y - 0.82f), -0.95f - y);
            return Mask(d * 0.5f);
        });
        a.iconHealth = SaveTex("icon_health", 64, 64, (u, v) =>
        {
            float x = Mathf.Abs(u - 0.5f) * 2f, y = Mathf.Abs(v - 0.5f) * 2f;
            float d = Mathf.Min(Mathf.Max(x - 0.26f, y - 0.8f), Mathf.Max(x - 0.8f, y - 0.26f));
            return Mask(d * 0.5f);
        });
        a.iconSprint = SaveTex("icon_sprint", 64, 64, (u, v) =>
        {
            float x = (u - 0.5f) * 2f, y = Mathf.Abs(v - 0.5f) * 2f;
            float c1 = Mathf.Abs(x + 0.35f - (1f - y) * 0.6f) - 0.16f;
            float c2 = Mathf.Abs(x - 0.3f - (1f - y) * 0.6f) - 0.16f;
            return Mask(Mathf.Max(Mathf.Min(c1, c2), y - 0.85f) * 0.5f);
        });
        var white = Color.white;
        var ink = new Color(0.08f, 0.1f, 0.18f, 1f);
        a.iconPlayers = SaveTex("icon_players", 64, 64, (u, v) =>
        {
            float x = (u - 0.5f) * 2f, y = (v - 0.5f) * 2f;
            float head = new Vector2(x, y - 0.3f).magnitude - 0.26f;
            float body = Mathf.Max(new Vector2(x / 1.2f, y + 0.72f).magnitude - 0.6f, y + 0.12f);
            return OnDisc(x, y, Mathf.Min(head, body), white, ink);
        });
        a.iconElims = SaveTex("icon_elims", 64, 64, (u, v) =>
        {
            float x = (u - 0.5f) * 2f, y = (v - 0.5f) * 2f;
            float r = new Vector2(x, y).magnitude;
            float ring = Mathf.Abs(r - 0.45f) - 0.08f;
            float ticks = Mathf.Min(Mathf.Max(Mathf.Abs(x) - 0.07f, Mathf.Abs(Mathf.Abs(y) - 0.6f) - 0.18f),
                                    Mathf.Max(Mathf.Abs(y) - 0.07f, Mathf.Abs(Mathf.Abs(x) - 0.6f) - 0.18f));
            return OnDisc(x, y, Mathf.Min(ring, ticks), white, ink);
        });
        a.iconClock = SaveTex("icon_clock", 64, 64, (u, v) =>
        {
            float x = (u - 0.5f) * 2f, y = (v - 0.5f) * 2f;
            float r = new Vector2(x, y).magnitude;
            float ring = Mathf.Abs(r - 0.56f) - 0.08f;
            float h1 = Mathf.Max(Mathf.Abs(x) - 0.07f, Mathf.Max(-y - 0.02f, y - 0.42f));
            float h2 = Mathf.Max(Mathf.Abs(y) - 0.07f, Mathf.Max(-x - 0.02f, x - 0.3f));
            return OnDisc(x, y, Mathf.Min(ring, Mathf.Min(h1, h2)), new Color(0.86f, 0.36f, 0.95f, 1f), white);
        });
        a.mapArrow = SaveTex("map_arrow", 64, 64, (u, v) =>
        {
            float x = (u - 0.5f) * 2f, y = (v - 0.5f) * 2f;
            float d = Mathf.Max(Mathf.Abs(x) * 1.9f + y - 0.85f, -y - 0.75f + Mathf.Abs(x) * 0.9f);
            float a1 = Mathf.Clamp01(0.5f - (d - 0.14f) / 0.06f);
            float w = Mathf.Clamp01(0.5f - d / 0.06f);
            var c = Color.Lerp(new Color(0.05f, 0.05f, 0.08f, 1f), Color.white, w);
            c.a = a1;
            return c;
        });
        a.slotShine = SaveTex("slot_shine", 64, 64, (u, v) =>
        {
            // rarity slot shading: lighter at the top, a soft diagonal sheen, darker at the bottom
            float k = Mathf.Lerp(-0.28f, 0.26f, v) + Mathf.Exp(-Mathf.Pow((u + v - 1.25f) * 3.2f, 2f)) * 0.16f;
            return k >= 0f ? new Color(1, 1, 1, k) : new Color(0, 0, 0, -k);
        });

        // minimap: the top-down render of the level (IslandCapture.TopDown), if it exists
        string top = Path.Combine(IslandBuilder.OutDir, "topdown.png");
        if (File.Exists(top))
        {
            string mp = $"{Dir}/minimap.png";
            File.Copy(top, mp, true);
            AssetDatabase.ImportAsset(mp, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(mp);
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 2048;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
            a.minimap = AssetDatabase.LoadAssetAtPath<Texture2D>(mp);
        }
        a.weaponIcons = WeaponTable.All.Select(d => RenderIcon(d.id, prefabs[d.id])).ToArray();
        a.consumableIcons = WeaponTable.Consumables.Select(c => RenderIcon(c.id, prefabs[c.id])).ToArray();
        a.ammoIcon = RenderIcon("ammo_box", prefabs["ammo_box"]);
    }

    /// hotbar icon: the test 1 model rendered side-on, barrel to the right and tilted up, on transparency
    static Texture2D RenderIcon(string id, GameObject prefab)
    {
        const int Layer = 31, Size = 256;
        var rig = new GameObject("IconRig");
        rig.transform.position = new Vector3(0f, 4000f, 0f);
        var w = Object.Instantiate(prefab, rig.transform, false);
        foreach (var l in w.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(l);
        foreach (var t in w.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
        bool gun = WeaponTable.Get(id) != null && id != "grenade" && id != "pickaxe";
        w.transform.localRotation = id == "pickaxe" ? Quaternion.Euler(0f, 0f, -38f) * Quaternion.Euler(0f, 90f, 0f)
                                  : gun ? Quaternion.Euler(0f, 0f, 18f) * Quaternion.Euler(0f, 90f, 0f)
                                  : Quaternion.Euler(-12f, 35f, 0f);
        var rs = w.GetComponentsInChildren<Renderer>(true);
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);

        var camGo = new GameObject("IconCam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(b.extents.y, b.extents.x) * 1.12f;
        cam.transform.SetPositionAndRotation(b.center - Vector3.forward * 10f, Quaternion.identity);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        cam.cullingMask = 1 << Layer;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 30f;
        cam.allowHDR = false;
        var keyGo = new GameObject("IconKey");
        var key = keyGo.AddComponent<Light>();
        key.type = LightType.Directional;
        key.cullingMask = 1 << Layer;
        key.intensity = 1.3f;
        key.transform.rotation = Quaternion.Euler(35f, 25f, 0f);
        var fillGo = new GameObject("IconFill");
        var fill = fillGo.AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.cullingMask = 1 << Layer;
        fill.intensity = 0.55f;
        fill.transform.rotation = Quaternion.Euler(-20f, -150f, 0f);

        bool fog = RenderSettings.fog;
        RenderSettings.fog = false;
        var msaa = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
        var res = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        cam.targetTexture = msaa;
        cam.Render();
        Graphics.Blit(msaa, res);
        var prev = RenderTexture.active;
        RenderTexture.active = res;
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderSettings.fog = fog;
        cam.targetTexture = null;
        string path = $"{Dir}/icon_{id}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        msaa.Release(); res.Release();
        Object.DestroyImmediate(rig); Object.DestroyImmediate(camGo); Object.DestroyImmediate(keyGo); Object.DestroyImmediate(fillGo);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = true;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ---------------------------------------------------------------- animator

    static AnimationClip C(string name, string fallback = null)
    {
        var c = IslandCharacters.ClipAssets;
        if (c.TryGetValue(name, out var clip)) return clip;
        if (fallback != null && c.TryGetValue(fallback, out clip)) return clip;
        return null;
    }

    /// a stance's 8-direction tree: idle, walk and run in four directions and a sprint; each clip sits at
    /// the velocity the game moves at, and its playback is scaled by target / measured speed so the feet
    /// keep their grip (a missing clip is replaced by a slower or faster use of a neighbour)
    static void Tree(AnimatorController ac, string state, string idle, string wf, string wb, string wl, string wr,
                     string rf, string rb, string rl, string rr, string sprint)
    {
        var st = ac.CreateBlendTreeInController(state, out var tree, 0);
        tree.blendType = BlendTreeType.FreeformDirectional2D;
        tree.blendParameter = "VelX";
        tree.blendParameterY = "VelZ";
        tree.useAutomaticThresholds = false;
        void Add(string clip, Vector2 pos)
        {
            var c = C(clip);
            if (c == null) { Debug.LogWarning($"[IslandGame] {state}: clip {clip} missing"); return; }
            tree.AddChild(c, pos);
            float scale = 1f;
            if (pos.sqrMagnitude > 0.01f && IslandCharacters.ClipVelocity.TryGetValue(clip, out var v))
            {
                float along = Vector2.Dot(v, pos.normalized);
                if (along > 0.3f) scale = Mathf.Clamp(pos.magnitude / along, 0.55f, 1.9f);
            }
            var ch = tree.children;
            ch[ch.Length - 1].timeScale = scale;
            tree.children = ch;
        }
        Add(idle, Vector2.zero);
        Add(wf, new Vector2(0f, Walk));
        Add(wb, new Vector2(0f, -Walk * BackMul));
        Add(wl, new Vector2(-Walk, 0f));
        Add(wr, new Vector2(Walk, 0f));
        Add(rf, new Vector2(0f, Run));
        Add(rb, new Vector2(0f, -Run * BackMul));
        Add(rl, new Vector2(-Run, 0f));
        Add(rr, new Vector2(Run, 0f));
        Add(sprint, new Vector2(0f, Sprint));
        st.writeDefaultValues = true;
    }

    static AnimatorController BuildController()
    {
        string path = $"{Dir}/PlayerAnimator.controller";
        var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
        ac.AddParameter("VelX", AnimatorControllerParameterType.Float);
        ac.AddParameter("VelZ", AnimatorControllerParameterType.Float);
        ac.AddParameter(new AnimatorControllerParameter { name = "ActionSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        ac.AddParameter(new AnimatorControllerParameter { name = "SwimRate", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        ac.AddParameter(new AnimatorControllerParameter { name = "MantleRate", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });

        string I(string n, string fb) => C(n) != null ? n : fb;
        Tree(ac, "Rifle", I("R_IdleAim", I("R_Idle", "Rifle_Walk")), "Rifle_Walk", I("R_WalkB", "Rifle_Walk"), I("R_WalkL", "Rifle_Walk"), I("R_WalkR", "Rifle_Walk"),
             "Rifle_Run", I("R_RunB", "Rifle_Run"), I("R_RunL", "Rifle_Run"), I("R_RunR", "Rifle_Run"), I("R_Sprint", "Rifle_Run"));
        Tree(ac, "Pistol", I("P_Idle", "Pistol_Walk"), "Pistol_Walk", I("P_WalkB", "Pistol_Walk"), I("P_StrafeL", "Pistol_Walk"), I("P_StrafeR", "Pistol_Walk"),
             "Pistol_Run", I("P_RunB", "Pistol_Run"), I("P_StrafeL", "Pistol_Run"), I("P_StrafeR", "Pistol_Run"), "Pistol_Run");
        Tree(ac, "Melee", I("M_Idle", "Axe_Walk"), "Axe_Walk", I("M_WalkB", "Axe_Walk"), I("M_WalkL", "Axe_Walk"), I("M_WalkR", "Axe_Walk"),
             "Axe_Run", I("M_RunB", "Axe_Run"), I("M_WalkL", "Axe_Run"), I("M_WalkR", "Axe_Run"), "Axe_Run");
        Tree(ac, "Unarmed", I("U_Idle", "Walk_Male"), "Walk_Male", I("U_WalkB", "Walk_Male"), I("U_WalkL", "Walk_Male"), I("U_WalkR", "Walk_Male"),
             "Run", I("U_RunB", "Run"), I("U_RunL", "Run"), I("U_RunR", "Run"), "Run");
        var sm = ac.layers[0].stateMachine;
        var rifleAir = sm.AddState("Rifle_Air");
        rifleAir.motion = C("R_JumpLoop", "Rifle_Run");
        var air = sm.AddState("Air");
        air.motion = C("U_Fall", "Run");
        foreach (var (state, clip, fb) in new[] { ("Skydive", "S_Dive", "U_Fall"), ("Glide", "S_Glide", "U_Fall"), ("DeathA", "N_Death1", "U_Fall"), ("DeathB", "N_Death2", "N_Death1"),
                                                  ("Swim", "Swim", "U_Fall"), ("Tread", "TreadWater", "U_Fall"), ("Mantle", "V_Mantle", "U_Fall"),
                                                  ("E_Robot", "E_Robot", "U_Idle"), ("E_RunningMan", "E_RunningMan", "U_Idle"), ("E_Chicken", "E_Chicken", "U_Idle"),
                                                  ("E_Cabbage", "E_Cabbage", "U_Idle"), ("E_Wave", "E_Wave", "U_Idle"), ("E_Flex", "E_Flex", "U_Idle"),
                                                  ("E_Victory", "E_Victory", "U_Idle"), ("LobbyIdle", "L_Idle", "U_Idle") })
        {
            var st = sm.AddState(state);
            st.motion = C(clip, fb);
            if (state == "Swim") { st.speedParameterActive = true; st.speedParameter = "SwimRate"; }
            if (state == "Mantle") { st.speedParameterActive = true; st.speedParameter = "MantleRate"; }
        }
        sm.defaultState = sm.states.First(s => s.state.name == "Rifle").state;

        // upper-body actions: reloads, pickaxe swing, grenade throw
        var mask = new AvatarMask();
        foreach (AvatarMaskBodyPart p in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
            if (p != AvatarMaskBodyPart.LastBodyPart) mask.SetHumanoidBodyPartActive(p, false);
        foreach (var p in new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm,
                                  AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers, AvatarMaskBodyPart.LeftHandIK, AvatarMaskBodyPart.RightHandIK })
            mask.SetHumanoidBodyPartActive(p, true);
        AssetDatabase.CreateAsset(mask, $"{Dir}/UpperBody.mask");
        ac.AddLayer("Actions");
        var layers = ac.layers;
        layers[0].iKPass = true;   // left hand on the fore-grip (PlayerHandIK)
        layers[1].avatarMask = mask;
        layers[1].defaultWeight = 0f;
        layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
        ac.layers = layers;
        var asm = ac.layers[1].stateMachine;
        var empty = asm.AddState("Empty");
        asm.defaultState = empty;
        foreach (var (state, clip, fb) in new[] { ("Reload_Rifle", "R_Reload", "Rifle_Walk"), ("Reload_Pistol", "P_Reload", "R_Reload"),
                                                  ("Melee", "M_Attack", "Axe_Walk"), ("Throw", "U_Throw", "Walk_Male"), ("Drink", "C_Drink", "U_Throw") })
        {
            var s = asm.AddState(state);
            s.motion = C(clip, fb);
            s.speedParameterActive = true;
            s.speedParameter = "ActionSpeed";
            var tr = s.AddTransition(empty);
            tr.hasExitTime = true;
            tr.exitTime = 0.97f;
            tr.duration = 0.1f;
        }

        // crouching and sliding (third layer): hips, legs and the spine's lean from the crouch clips while
        // the arms keep the stance's pose, so every weapon is held the same way crouched
        var lower = new AvatarMask();
        foreach (AvatarMaskBodyPart p in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
            if (p != AvatarMaskBodyPart.LastBodyPart)
                lower.SetHumanoidBodyPartActive(p, p == AvatarMaskBodyPart.Root || p == AvatarMaskBodyPart.Body || p == AvatarMaskBodyPart.LeftLeg
                                                   || p == AvatarMaskBodyPart.RightLeg || p == AvatarMaskBodyPart.LeftFootIK || p == AvatarMaskBodyPart.RightFootIK);
        AssetDatabase.CreateAsset(lower, $"{Dir}/LowerBody.mask");
        ac.AddLayer("Crouch");
        layers = ac.layers;
        layers[2].avatarMask = lower;
        layers[2].defaultWeight = 0f;
        layers[2].blendingMode = AnimatorLayerBlendingMode.Override;
        ac.layers = layers;
        float cs = BodyMoves.CrouchSpeed;
        LayerTree(ac, 2, "Crouch", (I("K_Idle", "U_Idle"), Vector2.zero), (I("K_WalkF", "Walk_Male"), new Vector2(0f, cs)), (I("K_WalkB", "U_WalkB"), new Vector2(0f, -cs * BackMul)),
                  (I("K_WalkL", "U_WalkL"), new Vector2(-cs, 0f)), (I("K_WalkR", "U_WalkR"), new Vector2(cs, 0f)));
        var slide = ac.layers[2].stateMachine.AddState("Slide");
        slide.motion = C("V_Slide", "K_Idle") ?? C("U_Fall");
        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();
        return ac;
    }

    /// a 2D velocity blend tree on any layer, each clip sped up or slowed to its ground speed
    static void LayerTree(AnimatorController ac, int layer, string state, params (string clip, Vector2 pos)[] items)
    {
        var st = ac.CreateBlendTreeInController(state, out var tree, layer);
        tree.blendType = BlendTreeType.FreeformDirectional2D;
        tree.blendParameter = "VelX";
        tree.blendParameterY = "VelZ";
        tree.useAutomaticThresholds = false;
        foreach (var (clip, pos) in items)
        {
            var c = C(clip);
            if (c == null) { Debug.LogWarning($"[IslandGame] {state}: clip {clip} missing"); continue; }
            tree.AddChild(c, pos);
            float scale = 1f;
            if (pos.sqrMagnitude > 0.01f && IslandCharacters.ClipVelocity.TryGetValue(clip, out var v))
            {
                float along = Vector2.Dot(v, pos.normalized);
                if (along > 0.3f) scale = Mathf.Clamp(pos.magnitude / along, 0.55f, 1.9f);
            }
            var ch = tree.children;
            ch[ch.Length - 1].timeScale = scale;
            tree.children = ch;
        }
        st.writeDefaultValues = true;
    }

    // ---------------------------------------------------------------- characters

    /// the player or a bot: CharacterController, the Mixamo model with the shared controller, every
    /// weapon and consumable fitted to the right hand, muzzles, fore-grips, a glider, a Combatant and the
    /// controller for who drives it
    static GameObject BuildCharacter(string character, string name, bool player, AnimatorController controller,
                                     Dictionary<string, GameObject> prefabs, Mesh gliderMesh)
    {
        var root = new GameObject(name);
        var cc = root.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.3f;
        cc.center = new Vector3(0f, 0.92f, 0f);
        cc.stepOffset = 0.45f;
        cc.slopeLimit = 50f;
        cc.skinWidth = 0.04f;
        cc.minMoveDistance = 0f;

        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{IslandCharacters.Root}/Characters/{character}.fbx");
        var model = Object.Instantiate(fbx, root.transform, false);
        model.name = "Model";
        var an = model.GetComponent<Animator>();
        model.transform.localScale = Vector3.one * IslandCharacters.FitScale(model, name);
        // nothing but the mesh and its skeleton: no camera or light that came with the file
        foreach (var c in model.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(c);
        foreach (var l in model.GetComponentsInChildren<Light>(true)) Object.DestroyImmediate(l);
        an.runtimeAnimatorController = controller;
        an.applyRootMotion = false;
        an.cullingMode = player ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullUpdateTransforms;
        foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = player;

        // the seven weapons, each fitted to the stance pose it is held in, and the three consumables
        var defs = WeaponTable.All;
        var held = new GameObject[defs.Length];
        var muzzles = new Transform[defs.Length];
        var grips = new Transform[defs.Length];
        GameObject rocketTip = null;
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            string fit = d.stance == Stance.Rifle ? (C("R_IdleAim") != null ? "R_IdleAim" : "R_Idle") : d.stance == Stance.Pistol ? "P_Idle" : d.stance == Stance.Melee ? "M_Idle" : "U_Idle";
            string fb = d.stance == Stance.Rifle ? "Rifle_Walk" : d.stance == Stance.Pistol ? "Pistol_Walk" : d.stance == Stance.Melee ? "Axe_Walk" : "Walk_Male";
            var w = IslandWeapons.Attach(model, d.id, C(fit, fb), prefabs[d.id]);
            held[i] = w;
            if (d.kind == FireKind.Hitscan || d.kind == FireKind.Rocket) muzzles[i] = AddMuzzle(w);
            if (d.stance == Stance.Rifle) grips[i] = AddLeftGrip(w, muzzles[i]);
            if (d.kind == FireKind.Rocket)
            {
                var tip = w.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "rocket_tip");
                if (tip != null) rocketTip = tip.gameObject;
            }
            w.SetActive(false);
        }
        var cons = WeaponTable.Consumables.Select(c =>
        {
            var w = IslandWeapons.Attach(model, c.id, C("U_Idle", "Walk_Male"), prefabs[c.id]);
            w.SetActive(false);
            return w;
        }).ToArray();

        // the glider, above the hands of the hanging pose
        var gl = new GameObject("Glider");
        gl.transform.SetParent(root.transform, false);
        var gclip = C("S_Glide");
        float handY = 2.05f;
        if (gclip != null)
        {
            gclip.SampleAnimation(model, gclip.length * 0.5f);
            var lh = an.GetBoneTransform(HumanBodyBones.LeftHand).position; var rh = an.GetBoneTransform(HumanBodyBones.RightHand).position;
            handY = root.transform.InverseTransformPoint((lh + rh) * 0.5f).y;
        }
        gl.transform.localPosition = new Vector3(0f, handY, 0f);
        gl.AddComponent<MeshFilter>().sharedMesh = gliderMesh;
        gl.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{Dir}/glider.mat");
        gl.SetActive(false);

        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = GameLayers.Player;

        var me = root.AddComponent<Combatant>();
        me.anim = an;
        var hands = root.AddComponent<WeaponHands>();
        hands.anim = an;
        hands.held = held;
        hands.heldConsumables = cons;
        hands.muzzles = muzzles;
        hands.leftGrips = grips;
        hands.rightHand = an.GetBoneTransform(HumanBodyBones.RightHand);
        hands.rocketTip = rocketTip;
        me.hands = hands;
        var ik = model.AddComponent<PlayerHandIK>();
        ik.weapons = hands;
        if (player)
        {
            var pc = root.AddComponent<PlayerController>();
            pc.anim = an; pc.cc = cc; pc.me = me; pc.hands = hands; pc.glider = gl;
            pc.walkSpeed = Walk; pc.runSpeed = Run; pc.sprintSpeed = Sprint; pc.backMul = BackMul;
        }
        else
        {
            var bc = root.AddComponent<BotController>();
            bc.anim = an; bc.cc = cc; bc.me = me; bc.hands = hands; bc.glider = gl;
        }
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{Dir}/{name}.prefab");
        Object.DestroyImmediate(root);
        return prefab;
    }

    /// Head-and-shoulders portraits of the outfits for the locker's tiles (256 x 256 with alpha). Each is
    /// rendered twice, over black and over white, far above the island with a camera that sees only the
    /// characters' layer; the difference gives the true coverage, so edges and the see-through face parts
    /// come out right over the tile's colours.
    static Texture2D[] BuildOutfitIcons(GameObject[] players, string[] names)
    {
        string dir = $"{Dir}/outfits";
        IslandAssets.EnsureFolder(dir);
        var lobby = C("L_Idle", "U_Idle");
        var camGo = new GameObject("OutfitCam");
        var cam = camGo.AddComponent<Camera>();
        cam.cullingMask = 1 << GameLayers.Player;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.fieldOfView = 24f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 20f;
        cam.allowHDR = false;
        var key = new GameObject("OutfitKey").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.1f;
        key.color = new Color(1f, 0.95f, 0.88f);
        key.cullingMask = 1 << GameLayers.Player;
        key.transform.rotation = Quaternion.LookRotation(new Vector3(-0.35f, -0.45f, -1f));
        const int S = 256;
        var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
        var read = new Texture2D(S, S, TextureFormat.RGBA32, false);
        var icons = new Texture2D[players.Length];
        var at = new Vector3(0f, 600f, 0f);
        for (int i = 0; i < players.Length; i++)
        {
            var model = Object.Instantiate(players[i].transform.Find("Model").gameObject, at, Quaternion.identity);
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            if (lobby != null) lobby.SampleAnimation(model, 1.0f);
            var head = model.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head).position;
            // the top of the hair or hat in this pose, from the skinned vertices (renderer bounds are padded)
            float top = head.y + 0.2f;
            var baked = new Mesh();
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.gameObject.activeInHierarchy || smr.sharedMesh == null) continue;
                smr.BakeMesh(baked);   // already scaled (the model's scale too); only placed and turned here
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (var v in baked.vertices) top = Mathf.Max(top, m.MultiplyPoint3x4(v).y);
            }
            Object.DestroyImmediate(baked);
            Debug.Log($"[IslandGame] outfit {names[i]}: head bone {head.y - at.y:F2} m, top {top - at.y:F2} m in the lobby pose");
            float lo = head.y - 0.34f, hi = top + 0.06f, cy = (lo + hi) * 0.5f;
            float d = (hi - lo) * 0.5f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.04f;
            var look = new Vector3(head.x, cy, head.z);
            cam.transform.position = look + new Vector3(0.12f * d, 0.03f, d);
            cam.transform.LookAt(look);
            cam.targetTexture = rt;
            var px = new Color[2][];
            for (int pass = 0; pass < 2; pass++)
            {
                cam.backgroundColor = pass == 0 ? new Color(0f, 0f, 0f, 1f) : new Color(1f, 1f, 1f, 1f);
                cam.Render();
                cam.Render();   // the first render after a change can come out without the skinning applied
                var prev = RenderTexture.active; RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, S, S), 0, 0); read.Apply();
                RenderTexture.active = prev;
                px[pass] = read.GetPixels();
            }
            cam.targetTexture = null;
            var outPx = new Color[S * S];
            for (int k = 0; k < outPx.Length; k++)
            {
                var b = px[0][k]; var w = px[1][k];
                float a = Mathf.Clamp01(1f - ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f);
                outPx[k] = a > 0.004f ? new Color(Mathf.Clamp01(b.r / a), Mathf.Clamp01(b.g / a), Mathf.Clamp01(b.b / a), a) : new Color(0f, 0f, 0f, 0f);
            }
            var icon = new Texture2D(S, S, TextureFormat.RGBA32, false);
            icon.SetPixels(outPx); icon.Apply();
            string path = $"{dir}/{names[i]}.png";
            File.WriteAllBytes(path, icon.EncodeToPNG());
            Object.DestroyImmediate(icon);
            Object.DestroyImmediate(model);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 256;
            ti.SaveAndReimport();
            icons[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        rt.Release();
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(read);
        Object.DestroyImmediate(camGo);
        Object.DestroyImmediate(key.gameObject);
        Debug.Log($"[IslandGame] {icons.Length} outfit portraits in {dir}");
        return icons;
    }

    /// a hang glider: an arched wing in two colours over a bar (vertex colours, the island's lit shader)
    static Mesh BuildGliderMesh()
    {
        var v = new List<Vector3>(); var c = new List<Color>(); var tri = new List<int>();
        const int nx = 24, nz = 6;
        const float span = 3.2f, chord = 1.4f, arch = 0.55f, lift = 0.75f;
        var a = new Color(0.12f, 0.62f, 0.95f); var b = new Color(1f, 0.82f, 0.22f);
        for (int side = 0; side < 2; side++)   // top and bottom faces
        {
            int baseIdx = v.Count;
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= nz; j++)
                {
                    float u = (float)i / nx, w = (float)j / nz;
                    float x = (u - 0.5f) * span;
                    float y = lift + arch * (1f - Mathf.Pow(2f * u - 1f, 2f)) + 0.08f * Mathf.Sin(w * Mathf.PI) - (side == 1 ? 0.03f : 0f);
                    float z = (w - 0.45f) * chord * (1f - 0.25f * Mathf.Abs(2f * u - 1f));
                    v.Add(new Vector3(x, y, z));
                    c.Add(((int)(u * 6f) % 2 == 0) ? a : b);
                }
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    int q = baseIdx + i * (nz + 1) + j;
                    if (side == 0) tri.AddRange(new[] { q, q + 1, q + nz + 1, q + 1, q + nz + 2, q + nz + 1 });
                    else tri.AddRange(new[] { q, q + nz + 1, q + 1, q + 1, q + nz + 1, q + nz + 2 });
                }
        }
        // the bar the hands hold and two struts up to the wing
        void Box(Vector3 p0, Vector3 p1, float t, Color col)
        {
            var d = (p1 - p0); var f = d.normalized;
            var r = Vector3.Cross(f, Mathf.Abs(f.y) > 0.9f ? Vector3.right : Vector3.up).normalized * t;
            var u = Vector3.Cross(r, f).normalized * t;
            int i0 = v.Count;
            foreach (var e in new[] { p0, p1 })
                foreach (var k in new[] { -r - u, r - u, r + u, -r + u }) { v.Add(e + k); c.Add(col); }
            int[] quads = { 0, 1, 5, 4, 1, 2, 6, 5, 2, 3, 7, 6, 3, 0, 4, 7 };
            for (int q = 0; q < 16; q += 4)
                tri.AddRange(new[] { i0 + quads[q], i0 + quads[q + 1], i0 + quads[q + 2], i0 + quads[q], i0 + quads[q + 2], i0 + quads[q + 3] });
        }
        var dark = new Color(0.15f, 0.15f, 0.18f);
        Box(new Vector3(-0.45f, 0f, 0f), new Vector3(0.45f, 0f, 0f), 0.025f, dark);
        Box(new Vector3(-0.4f, 0f, 0f), new Vector3(-0.7f, lift + 0.2f, 0f), 0.02f, dark);
        Box(new Vector3(0.4f, 0f, 0f), new Vector3(0.7f, lift + 0.2f, 0f), 0.02f, dark);
        var m = new Mesh { name = "glider" };
        m.SetVertices(v); m.SetColors(c); m.SetTriangles(tri, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        AssetDatabase.CreateAsset(m, $"{Dir}/glider_mesh.asset");
        var mat = new Material(Shader.Find("Island/VertexColorLit")) { name = "glider" };
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
        AssetDatabase.CreateAsset(mat, $"{Dir}/glider.mat");
        return m;
    }

    /// building: the twelve modular kit pieces, turned and shifted into the slot frame the game builds
    /// with (walls along local X centred on the cell edge, floors and cones centred on the cell, stairs
    /// rising towards +Z), the ghost material and the HUD icons
    static void BuildPieces(GameAssets a, Dictionary<string, GameObject> prefabs)
    {
        a.buildPrefabs = new GameObject[12];
        a.buildYaw = new float[12];
        a.buildOffset = new Vector3[12];
        a.buildMeshes = new Mesh[12];
        for (int t = 0; t < 4; t++)
            for (int m = 0; m < 3; m++)
            {
                int idx = t * 3 + m;
                var pf = prefabs[BuildRules.PrefabId((PieceType)t, (BuildMat)m)];
                a.buildPrefabs[idx] = pf;
                var root = new GameObject("PieceProbe");
                var inst = Object.Instantiate(pf, root.transform, false);
                var b = RootBounds(root);
                float yaw = 0f;
                if (t == (int)PieceType.Wall && b.size.x < b.size.z) yaw = 90f;
                if (t == (int)PieceType.Stairs) yaw = RampYaw(root);
                inst.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                b = RootBounds(root);
                a.buildYaw[idx] = yaw;
                a.buildOffset[idx] = new Vector3(-b.center.x, -b.min.y, -b.center.z);
                inst.transform.localPosition = a.buildOffset[idx];
                a.buildMeshes[idx] = BakeSlotMesh(inst, $"{Dir}/piece_{pf.name}.asset");
                Object.DestroyImmediate(root);
                Debug.Log($"[IslandGame] piece {pf.name}: size {b.size}, yaw {yaw}, offset {a.buildOffset[idx]}");
            }
        a.ghostMat = new Material(Shader.Find("Island/Ghost")) { name = "build_ghost" };
        AssetDatabase.CreateAsset(a.ghostMat, $"{Dir}/build_ghost.mat");
        a.matIcons = new[] { "wall_wood", "wall_brick", "wall_metal" }.Select(id => RenderIcon(id, prefabs[id])).ToArray();
        a.pieceIcons = new[] { "wall_wood", "floor_wood", "ramp_wood", "pyramid_roof_wood" }.Select(id => RenderIcon(id, prefabs[id])).ToArray();
        EditorUtility.SetDirty(a);
    }

    /// the piece's mesh moved into the slot frame and saved readable (the glTF meshes are not), for
    /// cutting windows and doors out of it at run time
    static Mesh BakeSlotMesh(GameObject inst, string path)
    {
        var mf = inst.GetComponentInChildren<MeshFilter>();
        var src = mf.sharedMesh;
        var m = mf.transform.localToWorldMatrix;   // the probe root sits at the origin
        var mesh = new Mesh { name = src.name + "_slot" };
        mesh.vertices = src.vertices.Select(v => m.MultiplyPoint3x4(v)).ToArray();
        mesh.normals = src.normals.Select(n => m.MultiplyVector(n).normalized).ToArray();
        mesh.uv = src.uv;
        mesh.subMeshCount = src.subMeshCount;
        for (int k = 0; k < src.subMeshCount; k++) mesh.SetTriangles(src.GetTriangles(k), k);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    static Bounds RootBounds(GameObject root)
    {
        var rs = root.GetComponentsInChildren<Renderer>(true);
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    /// which way the ramp climbs: compare the mean height of its vertices on each side
    static float RampYaw(GameObject root)
    {
        var pts = new List<Vector3>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            foreach (var v in mf.sharedMesh.vertices) pts.Add(mf.transform.localToWorldMatrix.MultiplyPoint3x4(v));
        var c = Vector3.zero;
        foreach (var p in pts) c += p;
        c /= Mathf.Max(1, pts.Count);
        float Side(System.Func<Vector3, bool> f) { float sum = 0f; int n = 0; foreach (var p in pts) if (f(p)) { sum += p.y; n++; } return n > 0 ? sum / n : 0f; }
        float zp = Side(p => p.z > c.z), zn = Side(p => p.z < c.z), xp = Side(p => p.x > c.x), xn = Side(p => p.x < c.x);
        if (Mathf.Abs(zp - zn) >= Mathf.Abs(xp - xn)) return zp > zn ? 0f : 180f;
        return xp > xn ? -90f : 90f;
    }

    /// swimming: invisible copies of the lake, river and sea surfaces with colliders on the Water layer;
    /// nothing collides with it, Water.Surface finds the surface with a ray
    static void BuildWaterColliders(Transform root)
    {
        var water = root.Find("Terrain_Water/Water");
        if (water == null) { Debug.LogWarning("[IslandGame] no water group"); return; }
        var g = new GameObject("WaterSurfaces");
        g.transform.SetParent(water.parent, false);
        int n = 0;
        foreach (var mf in water.GetComponentsInChildren<MeshFilter>(true))
        {
            var go = new GameObject(mf.name + "_Surface") { layer = Water.Layer };
            go.transform.SetParent(g.transform, false);
            go.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
            go.transform.localScale = mf.transform.lossyScale;
            go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            n++;
        }
        Debug.Log($"[IslandGame] water: {n} surfaces on the Water layer");
    }

    /// walkable surfaces of the island for the bots, baked from the colliders (terrain, buildings,
    /// rocks, trees, props)
    static NavMeshData BuildNavMesh()
    {
        var t0 = System.DateTime.Now;
        Physics.SyncTransforms();
        var bounds = new Bounds(new Vector3(0f, 20f, 10f), new Vector3(470f, 140f, 470f));
        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(bounds, GameLayers.Env, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);
        var settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 0.35f;
        settings.agentHeight = 1.8f;
        settings.agentClimb = 0.5f;
        settings.agentSlope = 45f;
        var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        data.name = "IslandNavMesh";
        AssetDatabase.CreateAsset(data, $"{Dir}/IslandNavMesh.asset");
        Debug.Log($"[IslandGame] nav mesh from {sources.Count} sources in {(System.DateTime.Now - t0).TotalSeconds:F1} s");
        return data;
    }

    /// left-hand point: the pump or fore-grip part when the model has one, else halfway down the barrel;
    /// palm facing up under the barrel
    static Transform AddLeftGrip(GameObject weapon, Transform muzzle)
    {
        var wt = weapon.transform;
        var part = weapon.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name.Contains("pump") || r.name == "front_grip");
        Vector3 local;
        if (part != null) local = wt.InverseTransformPoint(part.bounds.center) + new Vector3(0f, -0.03f, 0f);
        else
        {
            var m = muzzle != null ? muzzle.localPosition : new Vector3(0f, 0.05f, 0.5f);
            local = new Vector3(m.x, m.y - 0.05f, m.z * 0.5f);
        }
        var go = new GameObject("LeftGrip");
        go.transform.SetParent(wt, false);
        go.transform.localPosition = local;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
        return go.transform;
    }

    /// muzzle point: the front end of the barrel part (barrel along +Z in the weapon's space)
    static Transform AddMuzzle(GameObject weapon)
    {
        var wt = weapon.transform;
        var parts = weapon.GetComponentsInChildren<MeshFilter>(true);
        var barrel = parts.Where(m => m.name.Contains("barrel") || m.name == "tube" || m.name.Contains("slide")).ToArray();
        if (barrel.Length == 0) barrel = parts;
        bool any = false;
        var b = new Bounds();
        foreach (var mf in barrel)
        {
            if (mf.sharedMesh == null) continue;
            var mb = mf.sharedMesh.bounds;
            var m = wt.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(c);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
        }
        var go = new GameObject("Muzzle");
        go.transform.SetParent(wt, false);
        go.transform.localPosition = new Vector3(b.center.x, b.center.y, b.max.z);
        go.transform.localRotation = Quaternion.identity;
        return go.transform;
    }
}
