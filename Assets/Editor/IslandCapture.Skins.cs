using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// The outfits side by side (thirteenth run): skins\lineup_*.png show the player prefab and every bot
/// prefab on a plain floor, unarmed in their idle and then holding the assault rifle in its stance;
/// skins\player_closeup.png frames the player in the lobby pose. The skins' materials go to the log.
public static partial class IslandCapture
{
    public static void BuildAndSkins()
    {
        IslandBuilder.Build();
        Skins();
    }

    /// mesh weight of each outfit: vertices, triangles, skinned renderers, bones and materials
    public static void SkinStats()
    {
        foreach (var name in IslandCharacters.BotSkins.Select(s => "Bot_" + s).Prepend("Player"))
        {
            var pf = AssetDatabase.LoadAssetAtPath<GameObject>($"{IslandGame.Dir}/{name}.prefab");
            var smrs = pf.transform.Find("Model").GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int verts = smrs.Sum(r => r.sharedMesh ? r.sharedMesh.vertexCount : 0);
            int tris = smrs.Sum(r => r.sharedMesh ? r.sharedMesh.triangles.Length / 3 : 0);
            int bones = smrs.Sum(r => r.bones.Length), mats = smrs.Sum(r => r.sharedMaterials.Length);
            int blend = smrs.Sum(r => r.sharedMesh ? r.sharedMesh.blendShapeCount : 0);
            Debug.Log($"[SkinStats] {name}: {smrs.Length} skinned renderers, {verts} vertices, {tris} triangles, {bones} bones, {mats} materials, {blend} blend shapes");
        }
    }

    public static void Skins()
    {
        string dir = Path.Combine(Out, "skins");
        Directory.CreateDirectory(dir);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        QualitySettings.SetQualityLevel(5, true);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.7f, 0.85f);
        RenderSettings.ambientEquatorColor = new Color(0.5f, 0.52f, 0.55f);
        RenderSettings.ambientGroundColor = new Color(0.28f, 0.26f, 0.24f);
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.25f;
        sun.color = new Color(1f, 0.95f, 0.86f);
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(38f, 160f, 0f);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.transform.localScale = new Vector3(6f, 1f, 10f);
        floor.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.55f, 0.6f, 0.52f) };

        var names = IslandCharacters.BotSkins.Select(s => "Bot_" + s).Prepend("Player").ToArray();
        var models = new GameObject[names.Length];
        var hands = new WeaponHands[names.Length];
        const float spacing = 1.15f;
        int perRow = 8;
        for (int i = 0; i < names.Length; i++)
        {
            var pf = AssetDatabase.LoadAssetAtPath<GameObject>($"{IslandGame.Dir}/{names[i]}.prefab");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(pf);
            int row = i / perRow, col = i % perRow;
            go.transform.position = new Vector3((col - (perRow - 1) * 0.5f) * spacing, 0f, row * 40f);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            models[i] = go.transform.Find("Model").gameObject;
            hands[i] = go.GetComponent<WeaponHands>();
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            float top = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Max(r => r.bounds.max.y);
            var mats = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct();
            Debug.Log($"[IslandCapture] {names[i]}: top {top:F2} m; materials " + string.Join("; ", mats.Select(m =>
                $"{m.name} mode {(m.HasProperty("_Mode") ? m.GetFloat("_Mode") : -1f)} tex {(m.mainTexture ? m.mainTexture.name : "none")} col {m.color}")));
        }

        var cam = MakeCam();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.55f, 0.68f, 0.86f);
        cam.fieldOfView = 34f;
        int rifle = System.Array.FindIndex(WeaponTable.All, d => d.id == "assault_rifle");
        var idle = IslandCharacters.ClipAssets.TryGetValue("U_Idle", out var u) ? u : AssetDatabase.LoadAllAssetsAtPath($"{IslandCharacters.Root}/Animations/U_Idle.fbx").OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview__"));
        var aim = AssetDatabase.LoadAllAssetsAtPath($"{IslandCharacters.Root}/Animations/R_IdleAim.fbx").OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview__"));
        var lobby = AssetDatabase.LoadAllAssetsAtPath($"{IslandCharacters.Root}/Animations/L_Idle.fbx").OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview__"));
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < names.Length; i++)
            {
                (pass == 0 ? idle : aim).SampleAnimation(models[i], 0.4f);
                if (rifle >= 0 && hands[i] != null) hands[i].held[rifle].SetActive(pass == 1);
            }
            for (int row = 0; row * perRow < names.Length; row++)
            {
                cam.transform.position = new Vector3(0f, 1.15f, row * 40f - 8.2f);
                cam.transform.rotation = Quaternion.Euler(4f, 0f, 0f);
                string path = Path.Combine(dir, $"lineup_{(pass == 0 ? "idle" : "rifle")}_{row + 1}.png");
                Render(cam, 2400, 1100, path);   // the first render after the scene change warms the device up
                Render(cam, 2400, 1100, path);
            }
        }
        // the player up close in the lobby pose
        if (rifle >= 0) hands[0].held[rifle].SetActive(false);
        lobby.SampleAnimation(models[0], 1.2f);
        var p = models[0].transform.root.position;
        cam.fieldOfView = 38f;
        cam.transform.position = p + new Vector3(0f, 1.25f, -3.4f);
        cam.transform.LookAt(p + new Vector3(0f, 1.0f, 0f));
        Render(cam, 1600, 1200, Path.Combine(dir, "player_closeup.png"));
        Debug.Log($"[IslandCapture] outfits rendered to {dir}");
    }
}
