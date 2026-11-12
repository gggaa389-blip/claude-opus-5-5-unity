using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// Character videos rendered by Unity:
///   characters_demo.mp4: one scene per crowd animation (walk, female walk, jog over a bridge, run, beach
///   run, swim, treading water) and a wide shot of the plaza;
///   weapons_demo.mp4: every test 1 weapon carried walking and running.
/// Every scene searches a start time and a camera side where the camera stays free of trunks, rocks,
/// walls and tree canopies and keeps a clear line of sight to the character for the whole scene.
public static partial class IslandCapture
{
    class Scene
    {
        public string agent, caption;  // agent: exact name or name prefix
        public float duration = 6f, dist = 4.5f, height = 1.7f, aim = 1.1f, orbitDeg = 20f, fov = 45f;
        public string nearObject;      // start the scene when the agent passes this object (e.g. a bridge)
        public bool wide;              // static wide shot of the plaza instead of a tracking shot
        // chosen by the search
        public float start;
        public Vector3 dir0;
    }

    // ffmpeg expressions need a decimal point whatever the system locale is
    static string Inv(float v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    static Vector3 AgentTarget(CrowdAgent a, Scene s) => a.transform.position + Vector3.up * s.aim;

    // tree canopies have no colliders (only the trunks do), so the camera test checks them by position
    static List<Vector4> _trees;   // x, z, base y, scale

    static void CollectTrees()
    {
        _trees = new List<Vector4>();
        foreach (var t in GameObject.Find("Island").GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("tree_"))
                _trees.Add(new Vector4(t.position.x, t.position.z, t.position.y, t.localScale.x));
    }

    static bool InCanopy(Vector3 p, float margin)
    {
        foreach (var tr in _trees)
        {
            float dx = p.x - tr.x, dz = p.z - tr.y;
            float r = 2.6f * tr.w + margin;
            if (dx * dx + dz * dz < r * r && p.y > tr.z + 1.8f * tr.w && p.y < tr.z + 9.5f * tr.w) return true;
        }
        return false;
    }

    static bool CameraClear(Vector3 cam, Vector3 target)
    {
        float ground = Terr.SampleHeight(cam) + Terr.transform.position.y;
        if (cam.y < Mathf.Max(ground, 0f) + 0.7f) return false;
        if (Physics.CheckSphere(cam, 1.2f)) return false;              // nothing right in front of the lens
        if (Physics.Linecast(cam, target)) return false;
        // the whole body must be visible, not only the chest: head and feet too
        if (Physics.Linecast(cam, target + Vector3.up * 0.6f)) return false;
        if (Physics.Linecast(cam, target - Vector3.up * (target.y - Terr.SampleHeight(target) - Terr.transform.position.y - 0.15f))) return false;
        if (InCanopy(cam, 0.8f)) return false;
        for (int i = 1; i < 10; i++)                                   // leaves between camera and character
            if (InCanopy(Vector3.Lerp(cam, target, i / 10f), 0f)) return false;
        // a clean frame around the character: nothing big between the camera and points beside it
        var view = (target - cam).normalized;
        var side = Vector3.Cross(Vector3.up, view).normalized;
        float d = Vector3.Distance(cam, target);
        foreach (var off in new[] { side * 1.3f, -side * 1.3f, Vector3.up * 0.9f })
            if (Physics.Raycast(cam, (target + off - cam).normalized, d * 0.85f)) return false;
        return true;
    }

    static Vector3 CamPos(CrowdAgent a, Scene s, float local)
    {
        float orbit = s.orbitDeg * (local / s.duration - 0.5f);
        var dir = Quaternion.Euler(0f, orbit, 0f) * s.dir0;
        return AgentTarget(a, s) + dir * s.dist + Vector3.up * (s.height - s.aim);
    }

    /// pick start time, side and distance so the whole scene is unobstructed
    static bool PlanScene(CrowdAgent a, Scene s)
    {
        var starts = new List<float>();
        if (s.nearObject != null)
        {
            var obj = GameObject.Find(s.nearObject);
            var c = obj != null ? obj.transform.position : Vector3.zero;
            for (float t = 0f; t < 600f; t += 0.25f)
            {
                a.Apply(t);
                var p = a.transform.position;
                if (new Vector2(p.x - c.x, p.z - c.z).magnitude < 3f) starts.Add(t - s.duration * 0.5f);
            }
        }
        for (float t = 3f; t < 240f; t += 2.5f) starts.Add(t);
        float baseDist = s.dist;
        foreach (float ts in starts)
        {
            if (ts < 0f) continue;
            a.Apply(ts + s.duration * 0.5f);
            Vector3 fwd = a.transform.forward, right = a.transform.right;
            foreach (float sign in new[] { 1f, -1f })
                foreach (float k in new[] { 1f, 0.8f, 1.25f })
                {
                    // three-quarter front view: mostly from the side, a little ahead so the face shows
                    s.dir0 = (right * sign * 0.8f + fwd * 0.6f).normalized;
                    s.dist = baseDist * k;
                    s.start = ts;
                    bool ok = true;
                    for (int i = 0; i <= 8 && ok; i++)
                    {
                        float local = s.duration * i / 8f;
                        a.Apply(ts + local);
                        ok = CameraClear(CamPos(a, s, local), AgentTarget(a, s));
                    }
                    if (ok) return true;
                }
        }
        s.dist = baseDist;
        return false;
    }

    public static void CrowdVideo()
    {
        Open();
        Physics.SyncTransforms();
        CollectTrees();
        var scenes = new List<Scene>
        {
            new Scene { agent = "Mixamo_Remy_walk_03", caption = "Walking (Male Standard Walk) | Remy" },
            new Scene { agent = "Mixamo_Kate_walk_02", caption = "Walking (Female Normal Walk) | Kate" },
            new Scene { agent = "Mixamo_YBot_jog_10", caption = "Jogging over the south bridge | Y Bot", nearObject = "Bridge_South", dist = 6f, height = 2.6f, duration = 7f },
            new Scene { agent = "Mixamo_Remy_run_13", caption = "Running (Running With Intention) | Remy", dist = 6f, height = 2.0f, duration = 5f },
            new Scene { agent = "Mixamo_Kate_run_15", caption = "Running on the east beach | Kate", dist = 6.5f, height = 2.2f, duration = 5f },
            new Scene { agent = "Mixamo_Remy_swim_", caption = "Swimming down the river | Remy", aim = 0.85f, dist = 4.5f, height = 2.4f, duration = 6f },
            new Scene { agent = "Mixamo_Kate_swim_", caption = "Swimming | Kate", aim = 0.85f, dist = 4.2f, height = 2.2f, duration = 5f },
            new Scene { agent = "Mixamo_Kate_tread_", caption = "Treading water in the lake | Kate", aim = 1.3f, dist = 4f, height = 1.9f, orbitDeg = 70f, duration = 6f },
            new Scene { agent = "Mixamo_YBot_walk_01", caption = "The village plaza", wide = true, duration = 6f, fov = 50f },
        };
        RenderScenes(scenes, "characters_demo.mp4", "Mixamo crowd on the test 2 island | claude-opus-5-5");
    }

    static readonly (string id, string name)[] WeaponNames =
    {
        ("assault_rifle", "Assault rifle"), ("shotgun", "Shotgun"), ("sniper_rifle", "Sniper rifle"),
        ("rocket_launcher", "Rocket launcher"), ("pistol", "Pistol"), ("pickaxe", "Pickaxe"), ("grenade", "Grenade"),
    };

    /// weapons_demo.mp4: every test 1 weapon, carried walking and running (two scenes per weapon)
    public static void WeaponsVideo()
    {
        Open();
        Physics.SyncTransforms();
        CollectTrees();
        var all = UnityEngine.Object.FindObjectsByType<CrowdAgent>();
        var scenes = new List<Scene>();
        foreach (var (id, label) in WeaponNames)
            foreach (var mode in new[] { "walk", "run" })
            {
                var a = all.Where(x => x.name.Contains("_" + id + "_" + mode + "_")).OrderBy(x => x.name).FirstOrDefault();
                if (a == null) { Debug.LogWarning($"[IslandCapture] weapons video: no agent for {id} {mode}"); continue; }
                string who = a.name.Split('_')[1];
                if (who == "YBot") who = "Y Bot";
                bool run = mode == "run";
                bool small = id == "grenade" || id == "pistol";   // small weapons need a closer camera
                scenes.Add(new Scene
                {
                    agent = a.name,
                    caption = $"{label} | {(run ? "running" : "walking")} | {who}",
                    duration = 3.5f, dist = (run ? 5.0f : 3.6f) * (small ? 0.75f : 1f), height = run ? 1.9f : 1.6f,
                    aim = small ? 1.05f : 1.15f, orbitDeg = 25f, fov = 42f,
                });
            }
        RenderScenes(scenes, "weapons_demo.mp4", "Test 1 weapons carried by Mixamo characters | claude-opus-5-5");
    }

    /// debug: side and front close-ups of every armed agent's weapon hand (logs/hands_*.png)
    public static void HandCloseups()
    {
        Open();
        var cam = MakeCam();
        cam.fieldOfView = 35f;
        cam.nearClipPlane = 0.05f;
        foreach (var a in UnityEngine.Object.FindObjectsByType<CrowdAgent>().Where(x => x.transform.parent != null && x.transform.parent.name == "Crowd_Mixamo_Armed"))
        {
            foreach (float time in new[] { 2f, 2.4f, 2.2f })
            {
                Clock?.Apply(time);
                var hand = a.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightHand).position;
                var look = hand + Vector3.up * 0.3f;
                var pos = time == 2.2f
                    ? look + a.transform.right * 1.5f + a.transform.forward * 2.2f + Vector3.up * 0.2f   // front three-quarter
                    : look + a.transform.right * 2.6f + a.transform.forward * 0.5f + Vector3.up * 0.1f;  // side view
                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos));
                Render(cam, 640, 480, Path.Combine(Out, "logs", $"hands_{a.name}_{time:F1}.png"));
            }
        }
        UnityEngine.Object.DestroyImmediate(cam.gameObject);
    }

    static CrowdAgent FindAgent(string key)
    {
        var all = UnityEngine.Object.FindObjectsByType<CrowdAgent>();
        return all.FirstOrDefault(a => a.name == key) ?? all.Where(a => a.name.StartsWith(key)).OrderBy(a => a.name).FirstOrDefault();
    }

    static string Esc(string text) => text.Replace(":", "\\:").Replace("'", "");

    static void RenderScenes(List<Scene> scenes, string fileName, string title)
    {
        var agents = new Dictionary<Scene, CrowdAgent>();
        foreach (var s in scenes)
        {
            var a = FindAgent(s.agent);
            if (a == null) { Debug.LogWarning($"[IslandCapture] video {fileName}: no agent {s.agent}"); continue; }
            agents[s] = a;
            if (s.wide) { s.start = 12f; continue; }
            bool ok = PlanScene(a, s);
            Debug.Log($"[IslandCapture] video {fileName} scene '{s.caption}' ({a.name}): start {s.start:F1} s, distance {s.dist:F1} m, clear {ok}");
        }

        string frames = Path.Combine(Path.GetTempPath(), "island_crowd_frames_opus55");
        if (Directory.Exists(frames)) Directory.Delete(frames, true);
        Directory.CreateDirectory(frames);
        var cam = MakeCam();
        cam.nearClipPlane = 0.1f;
        const int fps = 30;
        int frame = 0;
        var captions = new StringBuilder();
        var sw = Stopwatch.StartNew();
        // wide plaza camera: high on the south-east side, looking over the plaza
        var plaza = new Vector3(0f, 0f, 9f);
        plaza.y = Terr.SampleHeight(plaza) + Terr.transform.position.y;
        foreach (var s in scenes)
        {
            if (!agents.TryGetValue(s, out var a)) continue;
            int n = Mathf.RoundToInt(s.duration * fps);
            float t0 = frame / (float)fps;
            for (int i = 0; i < n; i++)
            {
                float local = i / (float)fps;
                Clock?.Apply(s.start + local);
                cam.fieldOfView = s.fov;
                if (s.wide)
                {
                    float sweep = Mathf.Lerp(-12f, 12f, local / s.duration);
                    var pos = plaza + Quaternion.Euler(0f, 135f + sweep, 0f) * Vector3.forward * 26f + Vector3.up * 11f;
                    cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(plaza + Vector3.up * 1f - pos));
                }
                else
                {
                    var target = AgentTarget(a, s);
                    var pos = CamPos(a, s, local);
                    cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos));
                }
                Render(cam, 1920, 1080, Path.Combine(frames, $"f_{frame:0000}.jpg"), jpg: true);
                frame++;
            }
            float t1 = frame / (float)fps;
            captions.Append($"drawtext=fontfile='C\\:/Windows/Fonts/segoeuib.ttf':text='{Esc(s.caption)}':fontsize=40:fontcolor=white:" +
                            $"box=1:boxcolor=black@0.45:boxborderw=14:x=48:y=h-th-60:enable='between(t,{Inv(t0)},{Inv(t1 - 0.02f)})',");
        }
        captions.Append($"drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='{Esc(title)}':" +
                        "fontsize=26:fontcolor=white@0.85:box=1:boxcolor=black@0.35:boxborderw=10:x=48:y=40");
        UnityEngine.Object.DestroyImmediate(cam.gameObject);
        Debug.Log($"[IslandCapture] video {fileName}: {frame} frames ({frame / (float)fps:F1} s) in {sw.Elapsed.TotalSeconds:F1} s");

        string filter = Path.Combine(frames, "captions.txt");
        File.WriteAllText(filter, captions.ToString());
        string mp4 = Path.Combine(Out, fileName);
        var psi = new ProcessStartInfo("ffmpeg", $"-y -framerate {fps} -i \"{Path.Combine(frames, "f_%04d.jpg")}\" -filter_script:v \"{filter}\" " +
                                                 $"-c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart \"{mp4}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using (var proc = Process.Start(psi))
        {
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Debug.Log($"[IslandCapture] video {fileName} ffmpeg exit {proc.ExitCode}\n{err.Substring(Math.Max(0, err.Length - 1500))}");
            if (proc.ExitCode != 0) return;   // keep the frames for a manual re-encode
        }
        Directory.Delete(frames, true);
    }
}
