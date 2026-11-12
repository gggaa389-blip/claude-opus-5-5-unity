using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Picking Mixamo clips: renders a side-view strip of a clip on the Y Bot (eight moments) and logs the
/// hips and hands height over time, so the right part of a clip can be chosen and trimmed.
/// Usage: -executeMethod IslandClipProbe.Run -probeClips V_SlideRun,V_PullUp -probeOut <dir>
public static class IslandClipProbe
{
    /// fresh candidates: Humanoid on the Y Bot avatar, default clip settings (-probeKeep leaves a clip as the game imports it)
    static void ReimportForProbe(ModelImporter imp, Avatar avatar)
    {
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        imp.sourceAvatar = avatar;
        imp.importAnimation = true;
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        imp.SaveAndReimport();
    }

    public static void Run()
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string k) { int i = System.Array.IndexOf(args, k); return i >= 0 && i < args.Length - 1 ? args[i + 1] : null; }
        var names = (Arg("-probeClips") ?? "").Split(',').Where(s => s.Length > 0).ToArray();
        string outDir = Arg("-probeOut") ?? "Temp/probe";
        Directory.CreateDirectory(outDir);
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
        var ybotPath = $"{IslandCharacters.Root}/Characters/YBot.fbx";
        var avatar = AssetDatabase.LoadAllAssetsAtPath(ybotPath).OfType<Avatar>().First();
        var model = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ybotPath));
        var an = model.GetComponent<Animator>();
        an.applyRootMotion = false;
        var cam = new GameObject("ProbeCam").AddComponent<Camera>();
        cam.backgroundColor = new Color(0.82f, 0.85f, 0.9f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.fieldOfView = 40f;
        var rt = new RenderTexture(320, 400, 24);
        cam.targetTexture = rt;
        foreach (var name in names)
        {
            string path = $"{IslandCharacters.Root}/Animations/{name}.fbx";
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            if (System.Array.IndexOf(args, "-probeKeep") < 0) ReimportForProbe(imp, avatar);
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview__"));
            var strip = new Texture2D(320 * 8, 400, TextureFormat.RGB24, false);
            var hips = an.GetBoneTransform(HumanBodyBones.Hips);
            var lh = an.GetBoneTransform(HumanBodyBones.LeftHand);
            var lf = an.GetBoneTransform(HumanBodyBones.LeftFoot);
            string log = "";
            for (int k = 0; k <= 20; k++)
            {
                float t = clip.length * k / 20f;
                clip.SampleAnimation(model, t);
                log += $" t{t:0.00}:hips({hips.position.x:0.00},{hips.position.y:0.00},{hips.position.z:0.00}) hand {lh.position.y:0.00} foot {lf.position.y:0.00}\n";
            }
            for (int k = 0; k < 8; k++)
            {
                float t = clip.length * k / 7f;
                clip.SampleAnimation(model, t);
                var c = hips.position;
                cam.transform.position = new Vector3(c.x + 4.2f, 1.1f, c.z);
                cam.transform.LookAt(new Vector3(c.x, 1.0f, c.z));
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(320, 400, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 320, 400), 0, 0);
                tex.Apply();
                strip.SetPixels(320 * k, 0, 320, 400, tex.GetPixels());
                Object.DestroyImmediate(tex);
            }
            File.WriteAllBytes(Path.Combine(outDir, name + ".png"), strip.EncodeToPNG());
            Debug.Log($"[Probe] {name}: length {clip.length:0.00} s, frames {clip.length * clip.frameRate:0}\n{log}");
        }
    }
}
