using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// Offline captures rendered by Unity (batch mode with graphics):
/// topdown.png (orthographic, framed like the reference map), shots\*.png and flythrough.mp4.
public static partial class IslandCapture
{
    static string Out => IslandBuilder.OutDir;
    const float MapHalf = 215.7f;   // half of the 1254 px map in metres (0.344 m/px)

    static WaterClock Clock => UnityEngine.Object.FindAnyObjectByType<WaterClock>();
    static Terrain Terr => UnityEngine.Object.FindAnyObjectByType<Terrain>();

    static void Open()
    {
        EditorSceneManager.OpenScene(IslandBuilder.ScenePath, OpenSceneMode.Single);
        QualitySettings.SetQualityLevel(5, true);
        // all frames of a capture are rendered inside one editor frame; without this the skinned
        // characters keep the pose of their first render (only rigid parts such as Remy's shoes move)
        foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>())
            smr.forceMatrixRecalculationPerRender = true;
    }

    static Camera MakeCam()
    {
        var go = new GameObject("CaptureCam");
        var cam = go.AddComponent<Camera>();
        cam.allowHDR = true;
        cam.allowMSAA = true;
        IslandBuilder.AddPostFx(cam);
        cam.nearClipPlane = 0.2f;
        cam.farClipPlane = 2500f;
        cam.clearFlags = CameraClearFlags.Skybox;
        return cam;
    }

    static RenderTexture _msaa, _resolved;
    static Texture2D _read;

    static void Render(Camera cam, int w, int h, string path, bool jpg = false)
    {
        if (_msaa == null || _msaa.width != w || _msaa.height != h)
        {
            if (_msaa) { _msaa.Release(); _resolved.Release(); }
            _msaa = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            _resolved = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _read = new Texture2D(w, h, TextureFormat.RGB24, false);
        }
        cam.targetTexture = _msaa;
        cam.Render();
        Graphics.Blit(_msaa, _resolved);
        var prev = RenderTexture.active;
        RenderTexture.active = _resolved;
        _read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        _read.Apply();
        RenderTexture.active = prev;
        cam.targetTexture = null;
        File.WriteAllBytes(path, jpg ? _read.EncodeToJPG(94) : _read.EncodeToPNG());
    }

    public static void TopDown()
    {
        Open();
        var cam = MakeCam();
        cam.orthographic = true;
        cam.orthographicSize = MapHalf;
        cam.transform.position = new Vector3(0f, 160f, 0f);
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // north (+z) up, east (+x) right, like the map
        cam.farClipPlane = 400f;
        bool fog = RenderSettings.fog;
        RenderSettings.fog = false;
        QualitySettings.shadowDistance = 450f;
        QualitySettings.shadowCascades = 1;
        QualitySettings.lodBias = 60f;
        var t = Terr;
        t.heightmapPixelError = 1f;
        t.basemapDistance = 1000f;
        Clock?.Apply(3f);
        Render(cam, 2048, 2048, Path.Combine(Out, "topdown.png"));
        RenderSettings.fog = fog;
        UnityEngine.Object.DestroyImmediate(cam.gameObject);
        Debug.Log("[IslandCapture] topdown.png written");
    }

    struct Shot { public string name; public Vector3 pos, target; public float fov; }

    static Vector3 G(float x, float z, float above)
    {
        float h = Terr.SampleHeight(new Vector3(x, 0, z)) + Terr.transform.position.y;
        return new Vector3(x, Mathf.Max(h, 0f) + above, z);
    }

    public static void Shots()
    {
        Open();
        Directory.CreateDirectory(Path.Combine(Out, "shots"));
        var cam = MakeCam();
        var shots = ShotList();
        foreach (var s in shots)
        {
            cam.fieldOfView = s.fov;
            cam.nearClipPlane = s.name.Contains("eye") ? 0.1f : 0.3f;
            cam.transform.position = s.pos;
            cam.transform.rotation = Quaternion.LookRotation(s.target - s.pos);
            Clock?.Apply(5f);
            Render(cam, 1920, 1080, Path.Combine(Out, "shots", s.name + ".png"));
            Debug.Log($"[IslandCapture] shot {s.name}");
        }
        // close-ups of the Mixamo crowd: the camera is placed relative to an agent, at the first time
        // from t = 5 s on where it has a clear view (same test as the character videos)
        CollectTrees();
        foreach (var (shot, label, side, back, up) in new[] {
            ("08_mixamo_walkers_plaza", "Remy_walk_", 3.5f, -6.5f, 1.4f),
            ("09_mixamo_runner_road", "YBot_run_", 3.0f, -7.0f, 1.8f),
            ("10_mixamo_swimmers_river", "Remy_swim_", 3.0f, -6.0f, 2.2f) })
        {
            var agent = UnityEngine.Object.FindObjectsByType<CrowdAgent>().Where(a => a.name.Contains(label)).OrderBy(a => a.name).FirstOrDefault();
            if (agent == null) { Debug.LogWarning($"[IslandCapture] no agent for {shot}"); continue; }
            Physics.SyncTransforms();
            bool found = false;
            float shotTime = 5f;
            for (float time = 5f; time < 60f && !found; time += 1.5f)
            {
                Clock?.Apply(time);
                var t = agent.transform;
                var target = t.position + Vector3.up * (label.Contains("_swim_") ? 0.9f : 1.0f);
                foreach (float sgn in new[] { 1f, -1f })
                {
                    var pos = t.position + t.right * side * sgn - t.forward * back + Vector3.up * up;
                    // free camera, clear line of sight, no canopy in the way and nothing big beside the agent
                    if (!CameraClear(pos, target)) continue;
                    cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos));
                    found = true; shotTime = time;
                    break;
                }
            }
            if (!found) { Debug.LogWarning($"[IslandCapture] no free camera position for {shot}"); continue; }
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.1f;
            Render(cam, 1920, 1080, Path.Combine(Out, "shots", shot + ".png"));
            Debug.Log($"[IslandCapture] shot {shot} ({agent.name} at t = {shotTime:F1} s)");
        }
        UnityEngine.Object.DestroyImmediate(cam.gameObject);
    }

    static Shot[] ShotList()
    {
        // village eye shot from the main camera (placed at 1.7 m by the builder)
        var main = Camera.main;
        return new[]
        {
            new Shot { name = "01_village", pos = G(42f, -30f, 26f), target = G(0f, 10f, 2f), fov = 55f },
            new Shot { name = "02_farm", pos = G(68f, 62f, 28f), target = G(116f, 112f, 2f), fov = 55f },
            new Shot { name = "03_industrial_yard", pos = G(-28f, -30f, 30f), target = G(-88f, -78f, 2f), fov = 55f },
            new Shot { name = "04_east_beach", pos = G(150f, -40f, 12f), target = G(185f, 40f, 2f), fov = 55f },
            new Shot { name = "05_north_forest_lake", pos = G(45f, 95f, 38f), target = G(-8f, 145f, 0f), fov = 55f },
            new Shot { name = "06_south_cliff_tower", pos = new Vector3(55f, 16f, -225f), target = G(-2f, -149f, 4f), fov = 50f },
            new Shot { name = "07_village_eye_level_1.7m", pos = main.transform.position, target = main.transform.position + main.transform.forward * 10f, fov = 60f },
        };
    }

    public static void Flythrough()
    {
        Open();
        var path = UnityEngine.Object.FindAnyObjectByType<FlyPath>();
        string frames = Path.Combine(Path.GetTempPath(), "island_frames_opus55");
        if (Directory.Exists(frames)) Directory.Delete(frames, true);
        Directory.CreateDirectory(frames);
        var cam = MakeCam();
        cam.fieldOfView = 60f;
        int fps = 30;
        int count = Mathf.RoundToInt(path.duration * fps);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            path.Evaluate(i / (float)(count - 1), out var p, out var r);
            cam.transform.SetPositionAndRotation(p, r);
            Clock?.Apply(i / (float)fps);
            Render(cam, 1920, 1080, Path.Combine(frames, $"f_{i:0000}.jpg"), jpg: true);
        }
        Debug.Log($"[IslandCapture] {count} frames in {sw.Elapsed.TotalSeconds:F1} s ({sw.Elapsed.TotalMilliseconds / count:F1} ms/frame incl. readback+encode)");
        UnityEngine.Object.DestroyImmediate(cam.gameObject);
        string mp4 = Path.Combine(Out, "flythrough.mp4");
        var psi = new ProcessStartInfo("ffmpeg", $"-y -framerate {fps} -i \"{Path.Combine(frames, "f_%04d.jpg")}\" -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart \"{mp4}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using (var proc = Process.Start(psi))
        {
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Debug.Log($"[IslandCapture] ffmpeg exit {proc.ExitCode}\n{err.Substring(Math.Max(0, err.Length - 1500))}");
        }
        Directory.Delete(frames, true);
    }

    public static void All()
    {
        TopDown();
        Shots();
        Flythrough();
    }

    public static void BuildAndCapture()
    {
        IslandBuilder.Build();
        All();
    }
}
