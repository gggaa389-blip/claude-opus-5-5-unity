using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// Mixamo characters and animations (added on the user's request after test 2, see
/// Assets/ThirdParty/Mixamo/SOURCE.txt): Humanoid import settings, one prefab per character, and the
/// natural ground speed of each in-place clip measured from the planted foot.
public static class IslandCharacters
{
    public const string Root = "Assets/ThirdParty/Mixamo";
    public const string PrefabDir = IslandAssets.PrefabRoot + "/mixamo";
    public static readonly string[] Characters = { "YBot", "Remy", "Kate" };
    /// the battle royale's outfits (thirteenth run, on the user's request: Remy looked frightening up
    /// close and the bots included the skinless Y Bot): stylized Mixamo characters, Aj for the player
    /// and fourteen others the bots cycle through
    public const string PlayerSkin = "Aj";
    public static readonly string[] BotSkins = { "Michelle", "Amy", "Jackie", "Claire", "Kaya", "SportyGranny", "Ty", "Timmy",
                                                 "Ortiz", "BigVegas", "Swat", "Vanguard", "Ninja", "Doozy" };
    static IEnumerable<string> AllModels => Characters.Append(PlayerSkin).Concat(BotSkins);

    /// the locker's name for an outfit: its file name in capitals, words split ("SportyGranny" -> "SPORTY GRANNY")
    public static string OutfitName(string skin) =>
        System.Text.RegularExpressions.Regex.Replace(skin, "(?<=[a-z])(?=[A-Z])", " ").ToUpperInvariant();

    /// the locker's rarity colours (0 common .. 4 legendary): plain everyday looks are uncommon, the
    /// costumes epic, the most striking ones legendary
    public static int OutfitRarity(string skin)
    {
        switch (skin)
        {
            case "Ortiz": case "BigVegas": case "Vanguard": return 4;
            case "Jackie": case "SportyGranny": case "Ninja": case "Doozy": return 3;
            case "Michelle": case "Amy": case "Kaya": case "Swat": return 2;
            default: return 1;   // Aj, Claire, Ty, Timmy
        }
    }
    public static readonly string[] Clips = { "Walk_Male", "Walk_Female", "Jog", "Run", "Swim", "TreadWater",
                                              "Rifle_Walk", "Rifle_Run", "Pistol_Walk", "Pistol_Run", "Axe_Walk", "Axe_Run" };
    /// the playable character (fifth run): stance locomotion sets (R_ rifle 8-way, P_ pistol, M_ melee
    /// axe, U_ unarmed), upper-body actions and the crowd's death clips; imported when present
    public static readonly string[] GameClips = {
        "R_Idle", "R_IdleAim", "R_WalkB", "R_WalkL", "R_WalkR", "R_RunB", "R_RunL", "R_RunR", "R_Sprint", "R_JumpLoop", "R_Reload",
        "P_Idle", "P_WalkB", "P_StrafeL", "P_StrafeR", "P_RunB", "P_Reload",
        "M_Idle", "M_WalkB", "M_WalkL", "M_WalkR", "M_RunB", "M_Attack",
        "U_Idle", "U_WalkB", "U_WalkL", "U_WalkR", "U_RunL", "U_RunR", "U_RunB", "U_Throw", "U_Fall",
        "N_Death1", "N_Death2", "S_Dive", "S_Glide", "C_Drink",
        "K_Idle", "K_WalkF", "K_WalkB", "K_WalkL", "K_WalkR", "V_Slide", "V_Mantle",
        "E_Robot", "E_RunningMan", "E_Chicken", "E_Cabbage", "E_Wave", "E_Flex", "E_Victory", "L_Idle" };
    static readonly HashSet<string> OneShot = new HashSet<string> { "R_Reload", "P_Reload", "M_Attack", "U_Throw", "N_Death1", "N_Death2", "C_Drink", "V_Mantle" };
    /// clips whose root motion is thrown away entirely (the game moves the body itself): the mantle
    static bool Detached(string clip) => clip == "V_Mantle";

    public static Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
    public static Dictionary<string, AnimationClip> ClipAssets = new Dictionary<string, AnimationClip>();
    public static Dictionary<string, float> ClipSpeed = new Dictionary<string, float>();   // m/s at rate 1
    public static Dictionary<string, Vector2> ClipHeights = new Dictionary<string, Vector2>(); // mean head, hips height above root
    public static Dictionary<string, Vector2> ClipVelocity = new Dictionary<string, Vector2>(); // ground velocity (x right, y forward) m/s

    static bool Swimmy(string clip) => clip == "Swim" || clip == "TreadWater";
    /// clips whose root height is kept as authored (swimming, falling, dying) instead of feet on the ground
    static bool KeepHeight(string clip) => Swimmy(clip) || clip.StartsWith("N_Death") || clip == "R_JumpLoop" || clip == "U_Fall" || clip.StartsWith("S_");

    public static void Prepare()
    {
        Prefabs.Clear(); ClipAssets.Clear(); ClipSpeed.Clear(); ClipHeights.Clear(); ClipVelocity.Clear();

        // characters: Humanoid, own avatar, Standard materials written out next to the model; Unity unpacks
        // the embedded textures into <name>.fbm, which are then capped at 2K (1K for the bots' outfits)
        // with the normal maps flagged
        foreach (var c in AllModels)
        {
            int maxTex = BotSkins.Contains(c) ? 1024 : 2048;
            string path = $"{Root}/Characters/{c}.fbx";
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            bool dirty = imp.animationType != ModelImporterAnimationType.Human || imp.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel
                         || imp.importAnimation || imp.materialImportMode != ModelImporterMaterialImportMode.ImportStandard
                         || imp.materialLocation != ModelImporterMaterialLocation.External || imp.materialSearch != ModelImporterMaterialSearch.Local
                         || imp.importCameras || imp.importLights;
            // some outfit files carry a scene camera (Aj's did): imported, it rendered over the game's
            // camera from under the character's feet
            imp.importCameras = false;
            imp.importLights = false;
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.importAnimation = false;
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.materialLocation = ModelImporterMaterialLocation.External;
            imp.materialName = ModelImporterMaterialName.BasedOnModelNameAndMaterialName;
            imp.materialSearch = ModelImporterMaterialSearch.Local;
            if (dirty) imp.SaveAndReimport();
            string texDir = $"{Root}/Characters/{c}.fbm";
            if (!AssetDatabase.IsValidFolder(texDir)) continue;   // Y Bot has no textures
            foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { texDir }))
            {
                string tp = AssetDatabase.GUIDToAssetPath(g);
                var ti = (TextureImporter)AssetImporter.GetAtPath(tp);
                string lower = Path.GetFileNameWithoutExtension(tp).ToLowerInvariant();
                bool normal = lower.Contains("normal");
                bool linear = normal || lower.Contains("gloss") || lower.Contains("specular") || lower.Contains("opacity");
                if (ti.maxTextureSize != maxTex || (normal && ti.textureType != TextureImporterType.NormalMap) || ti.sRGBTexture == linear)
                {
                    ti.maxTextureSize = maxTex;
                    ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    ti.sRGBTexture = !linear;
                    ti.SaveAndReimport();
                }
            }
        }
        FixMaterials();
        var ybotAvatar = AssetDatabase.LoadAllAssetsAtPath($"{Root}/Characters/YBot.fbx").OfType<Avatar>().First();

        // animations: Humanoid on the Y Bot avatar, looping, in place, height from the feet (swimming keeps
        // the authored height)
        foreach (var name in Clips.Concat(GameClips))
        {
            string path = $"{Root}/Animations/{name}.fbx";
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            if (imp == null) { Debug.LogWarning($"[IslandCharacters] clip {name} not found"); continue; }
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            imp.sourceAvatar = ybotAvatar;
            imp.importAnimation = true;
            imp.materialImportMode = ModelImporterMaterialImportMode.None;
            var clips = imp.defaultClipAnimations;
            foreach (var c in clips)
            {
                c.name = name;
                c.loopTime = !OneShot.Contains(name);
                c.loopPose = !OneShot.Contains(name);
                c.lockRootRotation = true;
                c.keepOriginalOrientation = true;
                c.lockRootHeightY = !Detached(name);
                c.heightFromFeet = !KeepHeight(name);
                c.keepOriginalPositionY = KeepHeight(name);
                c.lockRootPositionXZ = !Detached(name);
                c.keepOriginalPositionXZ = true;
            }
            imp.clipAnimations = clips;
            imp.SaveAndReimport();
            ClipAssets[name] = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview__"));
        }

        // one prefab per character
        IslandAssets.EnsureFolder(PrefabDir);
        foreach (var c in Characters)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/Characters/{c}.fbx");
            var go = Object.Instantiate(model);
            go.name = "Mixamo_" + c;
            var an = go.GetComponent<Animator>();
            an.applyRootMotion = false;
            an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // real-world size: the Mixamo files do not agree on units (Remy comes in about 2x too big),
            // so every character is scaled to a head height between 1.40 and 1.70 m in the bind pose
            float headY = go.transform.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Head).position).y;
            float scale = Mathf.Clamp(headY, 1.40f, 1.70f) / headY;
            if (Mathf.Abs(scale - 1f) > 0.01f) go.transform.localScale = Vector3.one * scale;
            Debug.Log($"[IslandCharacters] {c}: head height {headY:F2} m in the bind pose, scale {scale:F3}");
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                r.updateWhenOffscreen = false;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            var lod = go.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.015f, go.GetComponentsInChildren<Renderer>(true)) });
            lod.RecalculateBounds();
            go.AddComponent<CrowdAgent>();
            Prefabs[c] = PrefabUtility.SaveAsPrefabAsset(go, $"{PrefabDir}/{go.name}.prefab");
            Object.DestroyImmediate(go);
        }

        Measure();
    }

    /// Scale that brings a Mixamo model (instantiated at scale 1) to real-world size: the head bone between
    /// 1.40 and 1.70 m in the bind pose, and the top of the mesh no higher than 1.95 m, since the stylized
    /// outfits carry much bigger heads than their bodies would suggest
    public static float FitScale(GameObject model, string label)
    {
        var an = model.GetComponent<Animator>();
        float headY = model.transform.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Head).position).y;
        float s = Mathf.Clamp(headY, 1.40f, 1.70f) / headY;
        float top = 0f;
        foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            top = Mathf.Max(top, model.transform.InverseTransformPoint(r.bounds.max).y);
        if (top * s > 1.95f) s = 1.95f / top;
        Debug.Log($"[IslandCharacters] {label}: head bone {headY:F2} m, top {top:F2} m in the bind pose, scale {s:F3} (top {top * s:F2} m)");
        return s;
    }

    /// The FBX materials come in as Standard with hair set to alpha blend and, for Kate, no hair texture
    /// at all. Hair and eyelashes become alpha-tested cards (Remy's separate opacity map is merged into the
    /// diffuse alpha), skin and cloth get a matte smoothness.
    static void FixMaterials()
    {
        string matDir = $"{Root}/Characters/Materials";
        foreach (var g in AssetDatabase.FindAssets("t:Material", new[] { matDir }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
            string n = m.name.ToLowerInvariant();
            if (n.StartsWith("ybot")) { m.SetFloat("_Glossiness", 0.45f); continue; }
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", 0.22f);
            if (n.Contains("hair") || n.Contains("eyelash"))
            {
                Texture2D main = m.mainTexture as Texture2D;
                if (n.StartsWith("kate"))
                {
                    main = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Characters/Kate.fbm/Ch21_1002_Diffuse.png");
                    var bump = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Characters/Kate.fbm/Ch21_1002_Normal.png");
                    if (bump) { m.SetTexture("_BumpMap", bump); m.EnableKeyword("_NORMALMAP"); }
                }
                else if (main != null)
                {
                    string mp = AssetDatabase.GetAssetPath(main);
                    string op = mp.Replace("_Diffuse", "_Opacity");
                    if (op != mp && File.Exists(op)) main = MergeAlpha(mp, op);
                }
                if (main) m.mainTexture = main;
                m.SetColor("_Color", Color.white);
                m.SetFloat("_Mode", 1f);
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                m.SetInt("_ZWrite", 1);
                m.EnableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                m.SetFloat("_Cutoff", 0.4f);
            }
            EditorUtility.SetDirty(m);
        }
        AssetDatabase.SaveAssets();
    }

    /// diffuse RGB + opacity (red channel) -> one RGBA texture next to the source
    static Texture2D MergeAlpha(string diffusePath, string opacityPath)
    {
        string outPath = diffusePath.Replace("_Diffuse", "_DiffuseAlpha");
        if (!File.Exists(outPath))
        {
            var d = new Texture2D(2, 2, TextureFormat.RGBA32, false); d.LoadImage(File.ReadAllBytes(diffusePath));
            var o = new Texture2D(2, 2, TextureFormat.RGBA32, false); o.LoadImage(File.ReadAllBytes(opacityPath));
            if (o.width != d.width || o.height != d.height)
            {
                var rt = RenderTexture.GetTemporary(d.width, d.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(o, rt);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                o = new Texture2D(d.width, d.height, TextureFormat.RGBA32, false);
                o.ReadPixels(new Rect(0, 0, d.width, d.height), 0, 0); o.Apply();
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            }
            var dp = d.GetPixels32(); var opx = o.GetPixels32();
            for (int i = 0; i < dp.Length; i++) dp[i].a = opx[i].r;
            var r = new Texture2D(d.width, d.height, TextureFormat.RGBA32, false);
            r.SetPixels32(dp); r.Apply();
            File.WriteAllBytes(outPath, r.EncodeToPNG());
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(outPath);
            ti.alphaIsTransparency = true;
            ti.maxTextureSize = 2048;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
    }

    /// ground speed of each walking/running clip: while a foot is planted it moves backwards under the
    /// body at the body's speed; also the mean head / hips height (used to float the swimmers)
    static void Measure()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/Characters/YBot.fbx");
        var go = Object.Instantiate(model);
        var an = go.GetComponent<Animator>();
        an.applyRootMotion = false;
        foreach (var kv in ClipAssets)
        {
            var clip = kv.Value;
            int n = 120;
            float dt = clip.length / n;
            var foot = new Vector3[2, n + 1];
            float head = 0f, hips = 0f;
            for (int i = 0; i <= n; i++)
            {
                clip.SampleAnimation(go, i * dt);   // edit-mode sampling (a PlayableGraph does not pose the rig outside play mode)
                foot[0, i] = go.transform.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.LeftFoot).position);
                foot[1, i] = go.transform.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.RightFoot).position);
                head += go.transform.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Head).position).y;
                hips += go.transform.InverseTransformPoint(an.GetBoneTransform(HumanBodyBones.Hips).position).y;
            }
            ClipHeights[kv.Key] = new Vector2(head / (n + 1), hips / (n + 1));
            if (KeepHeight(kv.Key) || OneShot.Contains(kv.Key)) { ClipSpeed[kv.Key] = kv.Key == "Swim" ? 0.9f : 0f; ClipVelocity[kv.Key] = Vector2.zero; continue; }
            var v = new List<float>();
            var vx = new List<float>();
            for (int f = 0; f < 2; f++)
            {
                float minY = float.MaxValue;
                for (int i = 0; i <= n; i++) minY = Mathf.Min(minY, foot[f, i].y);
                for (int i = 1; i < n; i++)
                    if (foot[f, i].y < minY + 0.025f)
                    {
                        v.Add(-(foot[f, i + 1].z - foot[f, i - 1].z) / (2f * dt));
                        vx.Add(-(foot[f, i + 1].x - foot[f, i - 1].x) / (2f * dt));
                    }
            }
            v.Sort();
            vx.Sort();
            float speed = v.Count > 0 ? v[v.Count / 2] : 1.3f;
            ClipSpeed[kv.Key] = Mathf.Clamp(speed, 0.6f, 7f);
            ClipVelocity[kv.Key] = v.Count > 0 ? new Vector2(vx[vx.Count / 2], speed) : Vector2.zero;
        }
        Object.DestroyImmediate(go);
        foreach (var kv in ClipSpeed)
            Debug.Log($"[IslandCharacters] clip {kv.Key}: length {ClipAssets[kv.Key].length:F2} s, ground speed {kv.Value:F2} m/s, velocity {ClipVelocity[kv.Key]}, head {ClipHeights[kv.Key].x:F2} m, hips {ClipHeights[kv.Key].y:F2} m");
    }
}
