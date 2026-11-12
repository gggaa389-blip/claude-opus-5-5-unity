using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// FPS benchmark: run the player with -benchmark [-benchmarkOut file]. First a warm-up pass visits
/// 150 points along the path (one per frame, not recorded) so shaders/pipeline states are compiled;
/// then the camera flies the same path as flythrough.mp4 in real time and every frame time is
/// recorded; the summary is written and the player quits. Without -benchmark it does nothing.
public class Benchmark : MonoBehaviour
{
    public FlyPath path;
    public Camera cam;
    public int warmupFrames = 150;

    readonly List<float> _frames = new List<float>(20000);
    readonly List<double> _cpu = new List<double>(20000), _gpu = new List<double>(20000);
    readonly FrameTiming[] _ft = new FrameTiming[1];
    float _t;
    int _warm;
    bool _active;
    string _out;
    int _passes = 1;

    void Start()
    {
        var args = System.Environment.GetCommandLineArgs();
        _active = args.Contains("-benchmark");
        if (!_active) { enabled = false; return; }
        if (cam == null) cam = Camera.main;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-benchmarkOut") _out = args[i + 1];
            if (args[i] == "-benchmarkPasses") int.TryParse(args[i + 1], out _passes);
            // diagnostic overrides (the reported numbers use the project defaults)
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args[i] == "-msaa" && int.TryParse(args[i + 1], out var aa)) QualitySettings.antiAliasing = aa;
            if (args[i] == "-lodBias" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, inv, out var lb)) QualitySettings.lodBias = lb;
            if (args[i] == "-shadowDistance" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, inv, out var sd)) QualitySettings.shadowDistance = sd;
        }
        // more diagnostic switches: post-processing / AO off, terrain details off or shorter
        if (args.Contains("-noPost") && cam != null && cam.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>() is var ppl && ppl != null) ppl.enabled = false;
        if (args.Contains("-noAO"))
            foreach (var v in FindObjectsByType<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
                if (v.sharedProfile.TryGetSettings<UnityEngine.Rendering.PostProcessing.AmbientOcclusion>(out var ao)) ao.active = false;
        var terrain = Terrain.activeTerrain;
        if (terrain != null)
        {
            if (args.Contains("-noDetails")) terrain.drawTreesAndFoliage = false;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-detailDistance" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dd)) terrain.detailObjectDistance = dd;
        }
        // crowd diagnostics: no crowd at all, crowd frozen (no animation, no movement), crowd without shadows
        if (args.Contains("-noCrowd"))
            foreach (var a in FindObjectsByType<CrowdAgent>()) a.gameObject.SetActive(false);
        if (args.Contains("-crowdStatic"))
            foreach (var a in FindObjectsByType<CrowdAgent>()) a.enabled = false;
        if (args.Contains("-noCrowdShadows"))
            foreach (var r in FindObjectsByType<SkinnedMeshRenderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (args.Contains("-noBushShadows"))
            foreach (var r in FindObjectsByType<MeshRenderer>())
                if (r.name.StartsWith("bush_")) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (string.IsNullOrEmpty(_out)) _out = Path.Combine(Application.persistentDataPath, "benchmark.txt");
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
    }

    void Update()
    {
        if (!_active) return;
        if (_warm < warmupFrames)
        {
            path.Evaluate(_warm / (float)(warmupFrames - 1), out var wp, out var wr);
            cam.transform.SetPositionAndRotation(wp, wr);
            _warm++;
            return;
        }
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _ft) > 0 && _warm > warmupFrames)
        {
            if (_ft[0].cpuMainThreadFrameTime > 0) _cpu.Add(_ft[0].cpuMainThreadFrameTime);
            if (_ft[0].gpuFrameTime > 0) _gpu.Add(_ft[0].gpuFrameTime);
        }
        float dt = Time.unscaledDeltaTime;
        if (_warm == warmupFrames) { _warm++; dt = 0f; }   // first flight frame: start the clock
        else _frames.Add(dt * 1000f);
        _t += dt;
        float total = path.duration * _passes;
        float tt = Mathf.Repeat(_t, path.duration) / path.duration;
        path.Evaluate(tt, out var p, out var r);
        cam.transform.SetPositionAndRotation(p, r);
        if (_t >= total) Finish();
    }

    void Finish()
    {
        _active = false;
        var sorted = _frames.OrderBy(x => x).ToArray();
        float avg = _frames.Average();
        float p50 = sorted[sorted.Length / 2];
        float p99 = sorted[Mathf.Min(sorted.Length - 1, (int)(sorted.Length * 0.99f))];
        float worst1 = sorted.Skip((int)(sorted.Length * 0.99f)).Average();
        var sb = new StringBuilder();
        sb.AppendLine($"frames={_frames.Count}");
        sb.AppendLine($"avg_frame_ms={avg:F3}");
        sb.AppendLine($"avg_fps={1000f / avg:F1}");
        sb.AppendLine($"median_frame_ms={p50:F3}");
        sb.AppendLine($"p99_frame_ms={p99:F3}");
        sb.AppendLine($"one_percent_low_fps={1000f / worst1:F1}");
        sb.AppendLine($"min_frame_ms={sorted[0]:F3}");
        sb.AppendLine($"max_frame_ms={sorted[sorted.Length - 1]:F3}");
        sb.AppendLine($"frame_timing_samples cpu={_cpu.Count} gpu={_gpu.Count}");
        if (_cpu.Count > 0) sb.AppendLine($"avg_cpu_main_thread_ms={_cpu.Average():F3}");
        if (_gpu.Count > 0) sb.AppendLine($"avg_gpu_ms={_gpu.Average():F3} p99_gpu_ms={_gpu.OrderBy(x => x).ElementAt((int)(_gpu.Count * 0.99)):F3}");
        sb.AppendLine($"screen={Screen.width}x{Screen.height} fullscreen={Screen.fullScreenMode}");
        sb.AppendLine($"camera_target={cam.pixelWidth}x{cam.pixelHeight}");
        sb.AppendLine($"gpu={SystemInfo.graphicsDeviceName} api={SystemInfo.graphicsDeviceType}");
        sb.AppendLine($"cpu={SystemInfo.processorType}");
        sb.AppendLine($"quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} vsync={QualitySettings.vSyncCount} msaa={QualitySettings.antiAliasing} shadowDistance={QualitySettings.shadowDistance} lodBias={QualitySettings.lodBias}");
        sb.AppendLine($"flight_seconds={path.duration * _passes}");
        File.WriteAllText(_out, sb.ToString());
        File.WriteAllLines(Path.ChangeExtension(_out, ".frames.csv"), _frames.Select(f => f.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
        Application.Quit();
    }
}
