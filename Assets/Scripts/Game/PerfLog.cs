using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

/// The frame rate of a real match (-perf: the autopilot plays in real time, nothing is recorded, VSync
/// and the frame limit off): every 10 s the mean fps, the 1% low and the worst frame with the players
/// still in, the time the bots' code takes per frame (all of it, and their brains alone) and the draw
/// counts; the whole match when the game quits. The loading screen and the first frames are left out.
public class PerfLog : MonoBehaviour
{
    public static bool On;
    public static long BotTicks, BrainTicks;   // Stopwatch ticks, added up by BotController
    static int _fontRebuilds;                  // a dynamic font's glyph texture rebuilt (too many sizes or glyphs)

    readonly List<float> _all = new List<float>(16384), _win = new List<float>(2048);
    float _next = -1f;
    long _bot0, _brain0;
    double _batches, _setPass, _tris, _main, _render, _gpu;
    int _nft;
    readonly FrameTiming[] _ft = new FrameTiming[1];
    // -perfMarkers in a development build: every timed main-thread profiler marker, the costliest logged
    readonly List<(string name, ProfilerRecorder rec)> _markers = new List<(string, ProfilerRecorder)>();
    double[] _mWin, _mAll;
    int _mFrames, _mFramesAll, _spikes;
    static readonly HashSet<string> Totals = new HashSet<string> { "CPU Total Frame Time", "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "Main Thread", "PlayerLoop", "GPU Frame Time" };
    ProfilerRecorder _rBatches, _rSetPass, _rTris;

    void OnEnable()
    {
        On = true;
        Font.textureRebuilt += _ => _fontRebuilds++;
        _rBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _rSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _rTris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-perfMarkers") >= 0 && Debug.isDebugBuild) StartMarkers();
    }

    void StartMarkers()
    {
        var handles = new List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        foreach (var h in handles)
        {
            var d = ProfilerRecorderHandle.GetDescription(h);
            if (d.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds) continue;
            _markers.Add((d.Name, ProfilerRecorder.StartNew(d.Category, d.Name, 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.CollectOnlyOnCurrentThread)));
        }
        _mWin = new double[_markers.Count];
        _mAll = new double[_markers.Count];
        Debug.Log($"[Perf] recording {_markers.Count} profiler markers");
    }

    string Top(double[] sums, int frames, int n)
    {
        var order = new List<int>();
        for (int i = 0; i < sums.Length; i++) if (sums[i] > 0) order.Add(i);
        order.Sort((a, b) => sums[b].CompareTo(sums[a]));
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int k = 0; k < Mathf.Min(n, order.Count); k++)
            sb.Append(string.Format(inv, "{0}{1} {2:F2}", k > 0 ? "; " : "", _markers[order[k]].name, sums[order[k]] / frames / 1e6));
        return sb.ToString();
    }

    void OnDisable()
    {
        _rBatches.Dispose(); _rSetPass.Dispose(); _rTris.Dispose();
        foreach (var m in _markers) m.rec.Dispose();
    }

    void Update()
    {
        if (Time.timeScale <= 0f || Hud.Loading) { Restart(); return; }
        if (_next < 0f) { _next = Time.unscaledTime + 10f; Restart(); return; }   // the first frame after loading
        float dt = Time.unscaledDeltaTime;
        _all.Add(dt);
        _win.Add(dt);
        _batches += _rBatches.LastValue; _setPass += _rSetPass.LastValue; _tris += _rTris.LastValue;
        // where the frame goes (needs Frame Timing Stats in the player settings): main thread, render thread, GPU
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _ft) == 1)
        {
            _main += _ft[0].cpuMainThreadFrameTime; _render += _ft[0].cpuRenderThreadFrameTime; _gpu += _ft[0].gpuFrameTime; _nft++;
        }
        if (_markers.Count > 0)
        {
            for (int i = 0; i < _markers.Count; i++) { double v = _markers[i].rec.LastValue; _mWin[i] += v; _mAll[i] += v; }
            _mFrames++; _mFramesAll++;
            // a hitch: what took that frame's time (the totals left out)
            if (dt > 0.06f && _spikes < 40)
            {
                _spikes++;
                var one = new double[_markers.Count];
                for (int i = 0; i < _markers.Count; i++) if (!Totals.Contains(_markers[i].name)) one[i] = _markers[i].rec.LastValue;
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[Perf] hitch {0:F0} ms at match {1:F1} s: ", dt * 1000f, Match.I != null ? Match.I.Now : 0f) + Top(one, 1, 10));
            }
        }
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 10f;
        int n = _win.Count;
        double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / n;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int k = Mathf.Max(1, _nft);
        Debug.Log(string.Format(inv, "[Perf] {0}, match {1:F0} s, {2} alive; main thread {8:F1} ms, render thread {9:F1} ms, GPU {10:F1} ms; bots {3:F2} ms/frame (brains {4:F2}), {6:F0} set-pass, {7:F2} M triangles",
            Line(_win), Match.I != null ? Match.I.Now : 0f, Match.I != null ? Match.I.Alive : 0,
            (BotTicks - _bot0) * ms, (BrainTicks - _brain0) * ms, _batches / n, _setPass / n, _tris / n / 1e6,
            _main / k, _render / k, _gpu / k));
        if (_fontRebuilds > 0) { Debug.Log($"[Perf] font textures rebuilt {_fontRebuilds} times in these 10 s"); _fontRebuilds = 0; }
        if (_markers.Count > 0 && _mFrames > 0)
        {
            Debug.Log("[Perf] costliest markers, ms per frame: " + Top(_mWin, _mFrames, 14));
            System.Array.Clear(_mWin, 0, _mWin.Length);
            _mFrames = 0;
        }
        Restart();
    }

    void Restart()
    {
        _win.Clear();
        _bot0 = BotTicks; _brain0 = BrainTicks;
        _batches = _setPass = _tris = 0;
        _main = _render = _gpu = 0; _nft = 0;
    }

    static string Line(List<float> frames)
    {
        if (frames.Count == 0) return "no frames";
        var sorted = new List<float>(frames);
        sorted.Sort();
        float sum = 0f; foreach (var f in frames) sum += f;
        int n1 = Mathf.Max(1, sorted.Count / 100);
        float worst = 0f; for (int i = sorted.Count - n1; i < sorted.Count; i++) worst += sorted[i];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(inv, "{0:F0} fps mean, {1:F0} fps 1% low, worst frame {2:F1} ms, {3} frames",
                             frames.Count / sum, n1 / worst, sorted[sorted.Count - 1] * 1000f, frames.Count);
    }

    void OnApplicationQuit()
    {
        Debug.Log($"[Perf] whole match: {Line(_all)}; {SystemInfo.graphicsDeviceName}, {Screen.width}x{Screen.height}, quality {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
        if (_markers.Count > 0 && _mFramesAll > 0) Debug.Log("[Perf] costliest markers over the match, ms per frame: " + Top(_mAll, _mFramesAll, 40));
    }
}
