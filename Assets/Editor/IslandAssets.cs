using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// Turns every test 1 .glb (imported by glTFast) into one prefab: materials are converted to the
/// Island/VertexColorLit shaders (glTF baseColorFactor x COLOR_0), colliders, static flags, culling
/// LODs and opened doors are added.
public static class IslandAssets
{
    public const string SourceRoot = "Assets/SourceModels";
    public const string PrefabRoot = "Assets/Prefabs";
    public const string MaterialRoot = "Assets/Generated/Materials";

    public enum Kind { Building, Tree, Rock, Bush, Prop, Loot, Blimp, Drop }

    public static Kind KindOf(string name)
    {
        if (name == "house" || name == "warehouse" || name == "lookout_tower") return Kind.Building;
        if (name.StartsWith("wall_") || name.StartsWith("floor_") || name.StartsWith("ramp_") || name.StartsWith("pyramid_roof_")) return Kind.Building;
        if (name.StartsWith("tree_")) return Kind.Tree;
        if (name.StartsWith("rock_")) return Kind.Rock;
        if (name.StartsWith("bush_")) return Kind.Bush;
        if (name == "cargo_blimp" || name == "drop_vehicle") return Kind.Blimp;   // the aircraft (the drop vehicle is the match's bus)
        if (name.StartsWith("supply_drop")) return Kind.Drop;
        if (name == "barrel" || name == "wood_crate" || name == "fence_segment") return Kind.Prop;
        return Kind.Loot; // weapons, ammo box, med kit, bandage, shield potion, grenade, treasure chest
    }

    static readonly string[] DoubleSidedHints = { "leaf", "leaves", "moss", "canopy", "glass" };
    static readonly string[] FoliageHints = { "leaf", "leaves", "needles" };

    /// leaf / needle material -> two tinted variants (darker and cooler, warmer and yellower); the level
    /// builder gives every tree and bush one of the three so forests are not a single flat green
    public static readonly Dictionary<Material, Material[]> FoliageVariants = new Dictionary<Material, Material[]>();

    public static void EnsureFolder(string path)
    {
        path = path.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    public static Dictionary<string, GameObject> BuildAll()
    {
        AssetDatabase.DeleteAsset(PrefabRoot);
        AssetDatabase.DeleteAsset(MaterialRoot);
        FoliageVariants.Clear();
        EnsureFolder(PrefabRoot);
        EnsureFolder(MaterialRoot);
        var result = new Dictionary<string, GameObject>();
        var guids = AssetDatabase.FindAssets("t:GameObject", new[] { SourceRoot });
        foreach (var g in guids.OrderBy(x => AssetDatabase.GUIDToAssetPath(x)))
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            if (!path.EndsWith(".glb")) continue;
            string assetId = Path.GetFileName(Path.GetDirectoryName(path));
            string name = Path.GetFileNameWithoutExtension(path);
            var prefab = BuildPrefab(path, assetId, name);
            result[name] = prefab;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[IslandAssets] {result.Count} prefabs built");
        return result;
    }

    static GameObject BuildPrefab(string glbPath, string assetId, string name)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
        var go = Object.Instantiate(src);
        go.name = name;
        var kind = KindOf(name);
        string matDir = $"{MaterialRoot}/{assetId}";
        EnsureFolder(matDir);

        // materials
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = ConvertMaterial(mats[i], matDir);
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            if (kind == Kind.Loot) r.lightProbeUsage = LightProbeUsage.Off;
        }

        OpenDoors(go, name);
        AddColliders(go, name, kind);

        // Environment pieces (buildings, trees, rocks, bushes, fences, crates, barrels) are static and
        // static-batched; measured faster here than GPU instancing (117.8 vs 106.6 fps, see report).
        // Loot, the supply drop and the blimp stay dynamic.
        bool isStatic = kind == Kind.Building || kind == Kind.Tree || kind == Kind.Rock || kind == Kind.Bush || kind == Kind.Prop;
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, isStatic ? (StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic) : 0);

        AddCullLod(go, name, kind);

        string dir = $"{PrefabRoot}/{assetId}";
        EnsureFolder(dir);
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{dir}/{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    static Material ConvertMaterial(Material src, string dir)
    {
        if (src == null) return null;
        string path = $"{dir}/{src.name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        bool transparent = src.IsKeywordEnabled("_ALPHABLEND_ON") || src.renderQueue >= 3000;
        var m = new Material(Shader.Find(transparent ? "Island/VertexColorTransparent" : "Island/VertexColorLit"));
        m.name = src.name;
        Color baseColor = src.HasProperty("baseColorFactor") ? src.GetColor("baseColorFactor") : Color.white;
        float metallic = src.HasProperty("metallicFactor") ? src.GetFloat("metallicFactor") : 0f;
        float roughness = src.HasProperty("roughnessFactor") ? src.GetFloat("roughnessFactor") : 0.7f;
        m.SetColor("_Color", baseColor);
        m.SetFloat("_Metallic", metallic);
        m.SetFloat("_Glossiness", Mathf.Clamp01(1f - roughness));
        Color emission = Color.black;
        if (src.IsKeywordEnabled("_EMISSION") && src.HasProperty("emissiveFactor")) emission = src.GetColor("emissiveFactor");
        m.SetColor("_EmissionColor", emission);
        string lower = src.name.ToLowerInvariant();
        bool doubleSided = DoubleSidedHints.Any(h => lower.Contains(h));
        m.SetFloat("_Cull", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
        m.enableInstancing = true;
        bool foliage = FoliageHints.Any(h => lower.Contains(h));
        if (foliage && m.HasProperty("_Wind"))
            m.SetFloat("_Wind", lower.Contains("palm") ? 0.07f : lower.Contains("needles") ? 0.03f : 0.045f);
        AssetDatabase.CreateAsset(m, path);
        if (foliage)
        {
            var v1 = new Material(m) { name = m.name + "_shade" };
            v1.SetColor("_Color", Mul(baseColor, 0.80f, 0.88f, 0.84f));
            var v2 = new Material(m) { name = m.name + "_sun" };
            v2.SetColor("_Color", Mul(baseColor, 1.04f, 1.0f, 0.82f));
            AssetDatabase.CreateAsset(v1, $"{dir}/{v1.name}.mat");
            AssetDatabase.CreateAsset(v2, $"{dir}/{v2.name}.mat");
            FoliageVariants[m] = new[] { v1, v2 };
        }
        return m;
    }

    static Color Mul(Color c, float r, float g, float b) => new Color(Mathf.Clamp01(c.r * r), Mathf.Clamp01(c.g * g), Mathf.Clamp01(c.b * b), c.a);

    static Transform Find(GameObject go, string child) =>
        go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == child);

    static void OpenDoors(GameObject go, string name)
    {
        if (name == "house")
        {
            var door = Find(go, "front_door");   // hinge pivot; swing 90 deg into the hall
            if (door) door.localRotation = Quaternion.Euler(0f, -95f, 0f) * door.localRotation;
        }
        else if (name == "warehouse")
        {
            var side = Find(go, "side_door");    // hinge pivot; swing outwards
            if (side) side.localRotation = Quaternion.Euler(0f, -100f, 0f) * side.localRotation;
            var roller = Find(go, "roller_door"); // pivot on the roll drum: squash the slats up into a rolled bundle
            if (roller) roller.localScale = new Vector3(1f, 0.1f, 1f);
        }
    }

    static void AddColliders(GameObject go, string name, Kind kind)
    {
        switch (kind)
        {
            case Kind.Building:
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.name == "glass") continue; // window panes sit inside the frames that already block
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
                break;
            case Kind.Tree:
            {
                var cc = go.AddComponent<CapsuleCollider>();
                float r = name == "tree_oak" ? 0.5f : name == "tree_pine" ? 0.38f : 0.3f;
                cc.radius = r;
                cc.height = 4.0f;
                cc.center = new Vector3(0f, 2.0f, 0f);
                break;
            }
            case Kind.Rock:
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.name.EndsWith("_moss")) continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = true;
                }
                break;
            case Kind.Bush:
                break; // walk-through foliage
            case Kind.Blimp:
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.name != "cabin" && mf.name != "jump_platform" && mf.name != "envelope" && mf.name != "bus_body" && mf.name != "balloon_envelope") continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
                break;
            case Kind.Drop:
            {
                var b = LocalBounds(go, n => n.StartsWith("crate"));
                var bc = go.AddComponent<BoxCollider>();
                bc.center = b.center; bc.size = b.size;
                break;
            }
            default:
            {
                var b = LocalBounds(go, null);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = b.center; bc.size = b.size;
                break;
            }
        }
    }

    static void AddCullLod(GameObject go, string name, Kind kind)
    {
        float cull;
        switch (kind)
        {
            case Kind.Bush: cull = 0.012f; break;
            case Kind.Rock: cull = name == "rock_03" ? 0.003f : 0.008f; break;
            case Kind.Prop: cull = name == "fence_segment" ? 0.004f : 0.008f; break;
            case Kind.Loot: cull = name == "treasure_chest" ? 0.008f : 0.015f; break;
            default: return;
        }
        var lod = go.AddComponent<LODGroup>();
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        lod.SetLODs(new[] { new LOD(cull, renderers) });
        lod.RecalculateBounds();
    }

    /// Bounds of the renderers in the root's local space (root assumed at identity).
    public static Bounds LocalBounds(GameObject go, System.Func<string, bool> filter)
    {
        var root = go.transform;
        bool any = false;
        var b = new Bounds();
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            if (filter != null && !filter(mf.name)) continue;
            var mb = mf.sharedMesh.bounds;
            var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(c);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
        }
        return b;
    }
}
