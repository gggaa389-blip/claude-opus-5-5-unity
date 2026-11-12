using System.Linq;
using UnityEditor;
using UnityEngine;

/// Project settings the level relies on: linear colour space, one fixed quality profile
/// (used by the captures and by the benchmark build) and windowed 1920x1080 player defaults.
public static class IslandSetup
{
    public const int PostLayer = 8;

    public static void Configure()
    {
        // user layer 8 holds the global post-processing volume
        var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tags.FindProperty("layers");
        if (layers.GetArrayElementAtIndex(PostLayer).stringValue != "PostProcessing")
        {
            layers.GetArrayElementAtIndex(PostLayer).stringValue = "PostProcessing";
            tags.ApplyModifiedPropertiesWithoutUndo();
        }

        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // glTFast reads the colour space at import time (vertex colours / material colours)
            foreach (var g in AssetDatabase.FindAssets("t:GameObject", new[] { IslandAssets.SourceRoot }))
                AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(g), ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("[IslandSetup] switched to linear colour space and re-imported the glb files");
        }

        PlayerSettings.productName = "IslandBench";
        PlayerSettings.companyName = "claude-opus-5-5";
        PlayerSettings.defaultScreenWidth = 1920;
        PlayerSettings.defaultScreenHeight = 1080;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.enableFrameTimingStats = true;   // CPU / GPU frame times in the benchmark
        // skinned characters are deformed on the GPU in batches (the new project defaulted to CPU skinning)
        var ps = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        var gpuSkin = ps.FindProperty("gpuSkinning");
        var deform = ps.FindProperty("meshDeformation");
        if (gpuSkin != null) gpuSkin.boolValue = true;
        if (deform != null) deform.intValue = 2;   // 0 CPU, 1 GPU, 2 GPU batched
        ps.ApplyModifiedPropertiesWithoutUndo();

        var qsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset").FirstOrDefault();
        var so = new SerializedObject(qsAsset);
        var levels = so.FindProperty("m_QualitySettings");
        for (int i = 0; i < levels.arraySize; i++)
        {
            var l = levels.GetArrayElementAtIndex(i);
            if (l.FindPropertyRelative("name").stringValue != "Ultra") continue;
            l.FindPropertyRelative("pixelLightCount").intValue = 4;
            l.FindPropertyRelative("shadows").intValue = 2;               // hard and soft
            l.FindPropertyRelative("shadowResolution").intValue = 3;      // very high
            l.FindPropertyRelative("shadowProjection").intValue = 1;      // stable fit
            l.FindPropertyRelative("shadowCascades").intValue = 2;
            l.FindPropertyRelative("shadowDistance").floatValue = 150f;
            l.FindPropertyRelative("antiAliasing").intValue = 4;
            l.FindPropertyRelative("anisotropicTextures").intValue = 2;   // forced on
            l.FindPropertyRelative("vSyncCount").intValue = 0;
            l.FindPropertyRelative("lodBias").floatValue = 2f;
            l.FindPropertyRelative("realtimeReflectionProbes").boolValue = false;
            l.FindPropertyRelative("skinWeights").intValue = 4;
        }
        so.FindProperty("m_CurrentQuality").intValue = 5;
        var perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
        for (int i = 0; i < perPlatform.arraySize; i++)
        {
            var e = perPlatform.GetArrayElementAtIndex(i);
            var first = e.FindPropertyRelative("first");
            if (first != null && first.stringValue == "Standalone") e.FindPropertyRelative("second").intValue = 5;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        QualitySettings.SetQualityLevel(5, true);
        AssetDatabase.SaveAssets();
    }
}
