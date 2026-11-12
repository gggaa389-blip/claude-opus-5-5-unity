using System;
using System.Collections.Generic;
using UnityEngine;

/// Instant replays and the kill cam: the last 12 s of every character (root, every humanoid bone, the
/// weapon in the hands) recorded 30 times a second. Playback freezes the match, poses the recorded
/// characters frame by frame with a camera of its own, and then puts everything back as it was.
[DefaultExecutionOrder(2000)]
public class ReplayRecorder : MonoBehaviour
{
    public static ReplayRecorder I;
    public const float Length = 12f;

    public class Track
    {
        public Combatant c;
        public Transform root;
        public Transform[] bones;
        public Transform hips;
        public GameObject[] held;
        public int boneStart;
    }

    public class Frame
    {
        public float t;
        public Vector3[] pos, hips;
        public Quaternion[] rot, bones;
        public sbyte[] held;
    }

    public struct Sound
    {
        public float t, volume, minDist;
        public string name;
        public Vector3 pos;
    }

    public readonly List<Track> tracks = new List<Track>();
    public readonly List<Sound> sounds = new List<Sound>();
    public readonly Frame[] frames = new Frame[(int)(Length * 30f) + 2];
    public int count, head;
    int _bones;
    float _last = -1f;

    void Awake() => I = this;

    /// every positional sound is kept with its time, so replays play it again
    public static void RecordSound(string name, Vector3 pos, float volume, float minDist)
    {
        if (I == null || ReplayPlayer.Active) return;
        var l = I.sounds;
        if (l.Count > 0 && Time.time - l[0].t > Length) l.RemoveAll(s => Time.time - s.t > Length);
        l.Add(new Sound { t = Time.time, name = name, pos = pos, volume = volume, minDist = minDist });
    }

    void Init()
    {
        foreach (var c in Combatant.All)
        {
            var an = c.anim != null ? c.anim : c.GetComponentInChildren<Animator>();
            if (an == null) continue;
            an.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // off-screen killers must be recorded moving
            var bones = new List<Transform>();
            for (int b = 0; b < (int)HumanBodyBones.LastBone; b++) { var t = an.GetBoneTransform((HumanBodyBones)b); if (t != null) bones.Add(t); }
            tracks.Add(new Track { c = c, root = c.transform, bones = bones.ToArray(), hips = an.GetBoneTransform(HumanBodyBones.Hips), held = c.hands != null ? c.hands.held : new GameObject[0], boneStart = _bones });
            _bones += bones.Count;
        }
    }

    public Frame At(int i) => frames[(head - count + i + frames.Length * 2) % frames.Length];
    public float Oldest => count > 0 ? At(0).t : 0f;
    public float Newest => count > 0 ? At(count - 1).t : 0f;

    void LateUpdate()
    {
        if (ReplayPlayer.Active) return;
        if (tracks.Count == 0) { Init(); if (tracks.Count == 0) return; }
        if (Time.time - _last < 1f / 31f) return;
        _last = Time.time;
        var f = frames[head];
        if (f == null)
        {
            int n = tracks.Count;
            f = frames[head] = new Frame { pos = new Vector3[n], hips = new Vector3[n], rot = new Quaternion[n], bones = new Quaternion[_bones], held = new sbyte[n] };
        }
        f.t = Time.time;
        for (int i = 0; i < tracks.Count; i++)
        {
            var tr = tracks[i];
            if (tr.c == null) continue;
            f.pos[i] = tr.root.position;
            f.rot[i] = tr.root.rotation;
            if (tr.hips != null) f.hips[i] = tr.hips.localPosition;
            for (int b = 0; b < tr.bones.Length; b++) f.bones[tr.boneStart + b] = tr.bones[b].localRotation;
            sbyte h = -1;
            for (int k = 0; k < tr.held.Length; k++) if (tr.held[k] != null && tr.held[k].activeSelf) { h = (sbyte)k; break; }
            f.held[i] = h;
        }
        head = (head + 1) % frames.Length;
        count = Mathf.Min(count + 1, frames.Length);
    }
}

[DefaultExecutionOrder(3000)]
public class ReplayPlayer : MonoBehaviour
{
    public static ReplayPlayer I;
    public static bool Active => I != null && I._on;
    public string title;
    public float t, t0, t1, speed = 1f;
    public bool paused, scripted;
    public float orbitRate = 14f;   // degrees a second the orbit camera circles in a scripted replay
    public Combatant focus;
    bool _on, _shoulder;
    float _soundT;
    Action _done;
    Vector3[] _pos;
    Quaternion[] _rot;
    bool[][] _held;
    float _timeScale = 1f, _yaw, _pitch = 10f, _dist = 4.2f;
    ThirdPersonCamera _tpc;

    /// play the last `seconds` around focus (a shoulder view behind it, or an orbit), then call done
    public static void Play(float seconds, Combatant focus, float speed, string title, bool shoulder, Action done = null)
    {
        var r = ReplayRecorder.I;
        if (r == null || r.count < 10 || Active) { done?.Invoke(); return; }
        if (I == null) I = r.gameObject.AddComponent<ReplayPlayer>();
        I.Begin(r, seconds, focus, speed, title, shoulder, done);
    }

    void Begin(ReplayRecorder r, float seconds, Combatant f, float sp, string ttl, bool shoulder, Action done)
    {
        int n = r.tracks.Count;
        _pos = new Vector3[n]; _rot = new Quaternion[n]; _held = new bool[n][];
        for (int i = 0; i < n; i++)
        {
            var tr = r.tracks[i];
            if (tr.c == null) continue;
            _pos[i] = tr.root.position; _rot[i] = tr.root.rotation;
            _held[i] = new bool[tr.held.Length];
            for (int k = 0; k < tr.held.Length; k++) _held[i][k] = tr.held[k] != null && tr.held[k].activeSelf;
            var cc = tr.c.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
        }
        _timeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
        Time.timeScale = 0f;
        _tpc = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
        if (_tpc != null) _tpc.enabled = false;
        t1 = r.Newest; t0 = Mathf.Max(r.Oldest, t1 - seconds);
        int fi = r.tracks.FindIndex(x => x.c == f);
        if (fi >= 0)
            for (int i = r.count - 1; i > 0 && r.At(i).t > t0; i--)
                if ((r.At(i).pos[fi] - r.At(i - 1).pos[fi]).sqrMagnitude > 25f * 25f) { t0 = r.At(i).t; break; }
        t = _soundT = t0;
        focus = f; speed = sp; title = ttl; _shoulder = shoulder; _done = done; paused = false;
        _yaw = Camera.main != null ? Camera.main.transform.eulerAngles.y : 0f;
        _on = true;
    }

    /// where the orbit camera starts: yaw, pitch, distance, and how fast a scripted replay circles
    public void Orbit(float yaw, float pitch, float dist, float rate)
    {
        _yaw = yaw; _pitch = pitch; _dist = dist; orbitRate = rate;
    }

    public void Stop()
    {
        if (!_on) return;
        _on = false;
        var r = ReplayRecorder.I;
        for (int i = 0; i < r.tracks.Count; i++)
        {
            var tr = r.tracks[i];
            if (tr.c == null || _held[i] == null) continue;
            tr.root.SetPositionAndRotation(_pos[i], _rot[i]);
            for (int k = 0; k < tr.held.Length; k++) if (tr.held[k] != null) tr.held[k].SetActive(_held[i][k]);
            var cc = tr.c.GetComponent<CharacterController>();
            if (cc != null && tr.c.Alive) cc.enabled = true;
        }
        Physics.SyncTransforms();
        Time.timeScale = _timeScale;
        if (_tpc != null) _tpc.enabled = true;
        var d = _done; _done = null;
        d?.Invoke();
    }

    void LateUpdate()
    {
        if (!_on) return;
        var r = ReplayRecorder.I;
        float dt = UiClock.Delta;
        if (!scripted) Controls(r, dt);
        if (!_on) return;
        if (!paused) t += dt * speed;
        if (t >= t1) { t = t1; if (_done != null || scripted) { Stop(); return; } paused = true; }
        // the recorded sounds, a little lower when slowed down
        if (t > _soundT)
        {
            foreach (var s in r.sounds)
                if (s.t > _soundT && s.t <= t) Sfx.I?.PlayAt(s.name, s.pos, s.volume, s.minDist, Mathf.Lerp(1f, speed, 0.5f));
        }
        _soundT = t;
        // the two frames around t
        int lo = 0, hi = r.count - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (r.At(mid).t <= t) lo = mid; else hi = mid; }
        var a = r.At(lo); var b = r.At(hi);
        float k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 0f;
        Vector3 fpos = Vector3.zero; Quaternion frot = Quaternion.identity;
        for (int i = 0; i < r.tracks.Count; i++)
        {
            var tr = r.tracks[i];
            if (tr.c == null) continue;
            var p = Vector3.Lerp(a.pos[i], b.pos[i], k);
            var q = Quaternion.Slerp(a.rot[i], b.rot[i], k);
            tr.root.SetPositionAndRotation(p, q);
            if (tr.hips != null) tr.hips.localPosition = Vector3.Lerp(a.hips[i], b.hips[i], k);
            for (int j = 0; j < tr.bones.Length; j++) tr.bones[j].localRotation = Quaternion.Slerp(a.bones[tr.boneStart + j], b.bones[tr.boneStart + j], k);
            int h = (k < 0.5f ? a : b).held[i];
            for (int j = 0; j < tr.held.Length; j++) if (tr.held[j] != null && tr.held[j].activeSelf != (j == h)) tr.held[j].SetActive(j == h);
            if (tr.c == focus) { fpos = p; frot = q; }
        }
        var cam = Camera.main;
        if (cam == null) return;
        var target = fpos + Vector3.up * 1.3f;
        if (_shoulder)
        {
            var back = frot * new Vector3(0.75f, 0.45f, -2.8f);
            var want = target + back;
            cam.transform.SetPositionAndRotation(Vector3.Lerp(cam.transform.position, want, 0.25f), Quaternion.Slerp(cam.transform.rotation, Quaternion.LookRotation(frot * Vector3.forward * 12f + Vector3.down * 1f), 0.25f));
        }
        else
        {
            if (scripted) _yaw += orbitRate * dt;
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            cam.transform.SetPositionAndRotation(target - rot * Vector3.forward * _dist, rot);
        }
    }

    /// Space pauses, the arrow keys skip 2 s, 1 2 3 set quarter, half and full speed, Tab follows the next
    /// player, the mouse orbits, the wheel zooms, Esc ends the replay
    void Controls(ReplayRecorder r, float dt)
    {
        if (Input.GetKeyDown(KeyCode.Escape)) { Stop(); return; }
        if (Input.GetKeyDown(KeyCode.Space)) paused = !paused;
        if (Input.GetKeyDown(KeyCode.LeftArrow)) t = Mathf.Max(t0, t - 2f);
        if (Input.GetKeyDown(KeyCode.RightArrow)) t = Mathf.Min(t1, t + 2f);
        if (Input.GetKeyDown(KeyCode.Alpha1)) speed = 0.25f;
        if (Input.GetKeyDown(KeyCode.Alpha2)) speed = 0.5f;
        if (Input.GetKeyDown(KeyCode.Alpha3)) speed = 1f;
        if (Input.GetKeyDown(KeyCode.Tab) && r.tracks.Count > 0)
        {
            int i = r.tracks.FindIndex(x => x.c == focus);
            for (int k = 1; k <= r.tracks.Count; k++) { var c = r.tracks[(i + k) % r.tracks.Count].c; if (c != null && c.inMatch) { focus = c; break; } }
            _shoulder = false;
        }
        _yaw += Input.GetAxisRaw("Mouse X") * 3f;
        _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * 3f, -20f, 70f);
        _dist = Mathf.Clamp(_dist - Input.GetAxisRaw("Mouse ScrollWheel") * 8f, 1.5f, 20f);
    }
}

/// Filming tools: F7 a free camera (WASD, Q / E down and up, the mouse, Shift faster), F8 hides the HUD,
/// F9 replays the last 12 s, P pauses the match, [ and ] slow it down and speed it up again.
public class CameraTools : MonoBehaviour
{
    public static bool Free;
    public PlayerController pc;
    float _yaw, _pitch;

    public static void SetFree(bool on)
    {
        Free = on;
        var cam = Camera.main;
        if (cam == null) return;
        var tpc = cam.GetComponent<ThirdPersonCamera>();
        if (tpc != null) tpc.enabled = !on;
        var tools = cam.GetComponent<CameraTools>();
        if (tools != null && on) { var e = cam.transform.eulerAngles; tools._yaw = e.y; tools._pitch = e.x > 180f ? e.x - 360f : e.x; }
    }

    /// moves the free camera: move in its own frame (x right, y up, z forward), look in degrees
    public void Fly(Vector3 move, Vector2 look, float dt)
    {
        _yaw += look.x;
        _pitch = Mathf.Clamp(_pitch - look.y, -85f, 85f);
        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.SetPositionAndRotation(transform.position + rot * move * dt, rot);
    }

    void Update()
    {
        if (pc == null || Hud.MenuOpen || ReplayPlayer.Active) return;
        if (Input.GetKeyDown(KeyCode.F7)) SetFree(!Free);
        if (Input.GetKeyDown(KeyCode.F8)) Hud.Hidden = !Hud.Hidden;
        if (Input.GetKeyDown(KeyCode.F9)) ReplayPlayer.Play(ReplayRecorder.Length, pc.me, 1f, "REPLAY", false);
        if (Input.GetKeyDown(KeyCode.P)) Time.timeScale = Time.timeScale > 0f ? 0f : 1f;
        if (Input.GetKeyDown(KeyCode.LeftBracket) && Time.timeScale > 0f) Time.timeScale = Mathf.Max(0.125f, Time.timeScale * 0.5f);
        if (Input.GetKeyDown(KeyCode.RightBracket) && Time.timeScale > 0f) Time.timeScale = Mathf.Min(1f, Time.timeScale * 2f);
        if (!Free) return;
        float sp = Input.GetKey(KeyCode.LeftShift) ? 30f : 8f;
        var m = new Vector3((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0), (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0),
                            (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
        Fly(m * sp, new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 2f, UiClock.Delta);
    }
}
