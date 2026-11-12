using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// A Mixamo character that loops along a closed path (walkers and runners on the roads, swimmers in
/// the river) or stays in place (treading water). Position, heading and the in-place animation are a
/// pure function of time, so the same motion is produced in play mode (WaterClock drives it) and in
/// the offline captures (IslandCapture calls WaterClock.Apply per frame).
/// In the game (play mode only) a character can also run away, die and come back: the motion
/// parameters change piecewise so position stays continuous, and clips cross-fade.
[ExecuteAlways]
[RequireComponent(typeof(Animator))]
public class CrowdAgent : MonoBehaviour
{
    public Vector3[] path;               // closed loop, world positions including height
    public float speed = 1.3f;           // m/s along the path (0 = stays in place)
    public float startDistance;          // where on the loop the agent is at time 0
    public AnimationClip clip;
    public float clipRate = 1f;          // playback rate, matched to the speed so feet do not slide
    public float clipPhase;
    public Vector3 holdPosition;         // used when speed == 0
    public float holdYaw;
    public float bob;                    // vertical bob amplitude (m) for swimmers

    /// ground speed of the plain Run clip (set by the game from the build's measurement)
    public static float RunSpeed = 4.18f;

    static readonly List<CrowdAgent> All = new List<CrowdAgent>();

    float[] _cum;
    float _length;
    PlayableGraph _graph;
    AnimationMixerPlayable _mixer;
    AnimationClipPlayable _cur, _prev;
    AnimationClip _curClip;

    // play-mode reactions
    struct Motion { public AnimationClip clip; public float speed, rate, phase, start; }
    Motion _orig;
    bool _hasOrig;
    AnimationClip _prevClip;
    float _prevRate, _prevPhase, _fadeStart = -10f;
    float _deadAt = -1f;

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }

    void OnDisable()
    {
        All.Remove(this);
        if (_graph.IsValid()) _graph.Destroy();
    }

    public static void ApplyAll(float time)
    {
        for (int i = 0; i < All.Count; i++) if (All[i] != null) All[i].Apply(time);
    }

    void BuildCum()
    {
        int n = path.Length;
        _cum = new float[n + 1];
        for (int i = 0; i < n; i++) _cum[i + 1] = _cum[i] + Vector3.Distance(path[i], path[(i + 1) % n]);
        _length = _cum[n];
    }

    Vector3 PointAt(float s)
    {
        int n = path.Length;
        s = Mathf.Repeat(s, _length);
        int lo = 0, hi = n;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (_cum[mid] <= s) lo = mid; else hi = mid; }
        float seg = _cum[lo + 1] - _cum[lo];
        return Vector3.Lerp(path[lo], path[(lo + 1) % n], seg > 1e-5f ? (s - _cum[lo]) / seg : 0f);
    }

    void EnsureGraph()
    {
        if (_graph.IsValid() || clip == null) return;
        var animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.CullCompletely;   // no animation work while off screen
        _graph = PlayableGraph.Create(name + "_crowd");
        // evaluated by Unity's animation update (multithreaded, culled); the time is set every frame
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        var output = AnimationPlayableOutput.Create(_graph, "anim", animator);
        _mixer = AnimationMixerPlayable.Create(_graph, 2);
        output.SetSourcePlayable(_mixer);
        _cur = MakeClip(clip);
        _graph.Connect(_cur, 0, _mixer, 0);
        _mixer.SetInputWeight(0, 1f);
        _curClip = clip;
        _graph.Play();
    }

    AnimationClipPlayable MakeClip(AnimationClip c)
    {
        var p = AnimationClipPlayable.Create(_graph, c);
        p.SetApplyFootIK(true);
        p.SetSpeed(0);
        return p;
    }

    /// the clip changed (running away, dying, coming back): cross-fade from the old one
    void SwapClip()
    {
        if (_curClip == clip) return;
        if (_prev.IsValid()) { _graph.Disconnect(_mixer, 1); _prev.Destroy(); }
        _graph.Disconnect(_mixer, 0);
        _prev = _cur;
        _graph.Connect(_prev, 0, _mixer, 1);
        _cur = MakeClip(clip);
        _graph.Connect(_cur, 0, _mixer, 0);
        _curClip = clip;
    }

    void SaveOrig()
    {
        if (_hasOrig) return;
        _orig = new Motion { clip = clip, speed = speed, rate = clipRate, phase = clipPhase, start = startDistance };
        _hasOrig = true;
    }

    void BeginFade(float time)
    {
        _prevClip = clip; _prevRate = clipRate; _prevPhase = clipPhase; _fadeStart = time;
    }

    /// new clip and speed from now on, without a jump in position
    public void SetMotion(AnimationClip c, float newSpeed, float rate, float time)
    {
        if (c == null || path == null || speed <= 0f) return;
        SaveOrig();
        BeginFade(time);
        float s = startDistance + time * speed;
        speed = newSpeed;
        startDistance = s - time * newSpeed;
        clip = c;
        clipRate = rate;
        clipPhase = Mathf.Repeat(-time * rate, c.length);
    }

    public void RestoreMotion(float time)
    {
        if (!_hasOrig) return;
        SetMotion(_orig.clip, _orig.speed, _orig.rate, time);
    }

    /// play a death clip where the character stands, facing yaw (degrees)
    public void Die(AnimationClip death, float time, float yaw)
    {
        if (death == null) return;
        SaveOrig();
        BeginFade(time);
        _deadAt = time;
        clip = death;
        clipRate = 1f;
        clipPhase = 0f;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    /// back on the original route as if nothing happened
    public void Revive(float time)
    {
        if (!_hasOrig) return;
        _deadAt = -1f;
        _fadeStart = -10f;
        clip = _orig.clip; speed = _orig.speed; clipRate = _orig.rate; clipPhase = _orig.phase; startDistance = _orig.start;
        Apply(time);
    }

    public bool IsDead => _deadAt >= 0f;

    public void Apply(float time)
    {
        if (_deadAt < 0f)
        {
            if (speed > 0f && path != null && path.Length > 2)
            {
                if (_cum == null || _cum.Length != path.Length + 1) BuildCum();
                float s = startDistance + time * speed;
                Vector3 p = PointAt(s);
                // heading from a chord around the current point keeps turns smooth
                Vector3 d = PointAt(s + 1.5f) - PointAt(s - 1.5f);
                d.y = 0f;
                if (bob > 0f) p.y += Mathf.Sin(time * 2.1f + startDistance) * bob;
                transform.position = p;
                if (d.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            }
            else
            {
                Vector3 p = holdPosition;
                if (bob > 0f) p.y += Mathf.Sin(time * 1.7f + holdYaw) * bob;
                transform.SetPositionAndRotation(p, Quaternion.Euler(0f, holdYaw + Mathf.Sin(time * 0.3f + holdYaw) * 25f, 0f));
            }
        }
        if (clip == null) return;
        if (!Application.isPlaying)
        {
            // edit mode (offline captures): sample the clip directly
            float len0 = clip.length;
            clip.SampleAnimation(gameObject, len0 > 0f ? (time * clipRate + clipPhase) % len0 : 0f);
            return;
        }
        EnsureGraph();
        if (!_graph.IsValid()) return;
        SwapClip();
        double len = clip.length;
        if (_deadAt >= 0f) _cur.SetTime(Mathf.Min(time - _deadAt, clip.length - 0.02f));
        else _cur.SetTime(len > 0 ? Mathf.Repeat(time * clipRate + clipPhase, (float)len) : 0);
        float w = Mathf.Clamp01((time - _fadeStart) / 0.25f);
        if (_prev.IsValid() && w < 1f && _prevClip != null)
            _prev.SetTime(Mathf.Repeat(time * _prevRate + _prevPhase, _prevClip.length));
        _mixer.SetInputWeight(0, _prev.IsValid() ? w : 1f);
        _mixer.SetInputWeight(1, _prev.IsValid() ? 1f - w : 0f);
    }
}
