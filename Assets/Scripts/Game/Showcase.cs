using System.Collections;
using System.IO;
using UnityEngine;

/// A scripted video in the game build: a coroutine drives the player only through input (move, look
/// towards a point, keys), and with -demoOut every frame is written at a fixed 30 fps clock, the sound
/// into audio.wav. Waits count frames, so they also run while the game is paused.
public abstract class Showcase : MonoBehaviour, IPlayerInputSource
{
    public PlayerController pc;
    public string outDir;

    protected Vector3? _look;
    protected float _lookRate = 6f;
    protected Vector2 _move;
    protected bool _tap, _hold, _aim, _aimTap, _jump, _sprint, _crouch, _edit, _interact, _done;
    protected int _slot = -1, _build = -1, _emote;
    protected Vector2 _lookDelta;   // raw look added this frame (the emote wheel)
    int _frame;

    protected int Frames => _frame;
    protected Vector3 Feet => pc.transform.position;

    void Start()
    {
        Time.captureFramerate = 30;
        if (!string.IsNullOrEmpty(outDir)) { Directory.CreateDirectory(outDir); StartCoroutine(Record()); }
        StartCoroutine(Script());
    }

    protected abstract IEnumerator Script();

    public PlayerInputState Read()
    {
        var s = new PlayerInputState { slot = _slot, build = _build, move = _move, fireHeld = _tap || _hold, firePressed = _tap, aim = _aim, aimPressed = _aimTap,
                                       jump = _jump, sprint = _sprint, crouch = _crouch, edit = _edit, interact = _interact, emote = _emote };
        _slot = -1; _build = -1; _emote = 0; _tap = false; _aimTap = false; _jump = false; _crouch = false; _edit = false; _interact = false;
        if (_look.HasValue)
        {
            var cam = pc.cam;
            var dir = _look.Value - cam.transform.position;
            float wantYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float wantPitch = -Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            float k = 1f - Mathf.Exp(-Time.deltaTime * _lookRate);
            s.look = new Vector2(Mathf.DeltaAngle(cam.yaw, wantYaw) * k, -(wantPitch - cam.pitch) * k);
        }
        s.look += _lookDelta;
        _lookDelta = Vector2.zero;
        return s;
    }

    protected static float Ground(Vector3 p)
    {
        var t = Terrain.activeTerrain;
        return t != null ? t.SampleHeight(p) + t.transform.position.y : 0f;
    }

    protected IEnumerator Wait(float t) { for (int n = Mathf.RoundToInt(t * 30f); n > 0; n--) yield return null; }

    protected IEnumerator Until(System.Func<bool> done, float maxTime)
    {
        for (int n = Mathf.RoundToInt(maxTime * 30f); n > 0 && !done(); n--) yield return null;
    }

    /// a cut to another place: the player on the ground there, facing dir
    protected void TeleportTo(Vector3 p, Vector3 dir)
    {
        p.y = Ground(p) + 0.1f;
        pc.Teleport(p, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
        _look = p + dir * 30f + Vector3.up * 1.2f;
        _move = Vector2.zero;
        _sprint = false;
    }

    protected IEnumerator WalkTo(Vector3 p, float stopAt, float maxTime)
    {
        float until = Time.time + maxTime;
        while (Time.time < until)
        {
            var d = p - Feet; d.y = 0f;
            if (d.magnitude < stopAt) break;
            var local = Quaternion.Inverse(Quaternion.Euler(0f, pc.cam.yaw, 0f)) * d.normalized;
            _move = new Vector2(local.x, local.z) * Mathf.Clamp01(d.magnitude / 1.2f + 0.25f);
            yield return null;
        }
        _move = Vector2.zero;
    }

    /// stop recording, log, quit
    protected IEnumerator Finish(string log)
    {
        _done = true;
        yield return null;
        Debug.Log(log);
        Application.Quit();
    }

    IEnumerator Record()
    {
        var wait = new WaitForEndOfFrame();
        var audio = new AudioCapture(Path.Combine(outDir, "audio.wav"));
        while (!_done)
        {
            yield return wait;
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(outDir, $"f_{_frame++:00000}.jpg"), tex.EncodeToJPG(90));
            Destroy(tex);
            audio.Frame();
        }
        audio.Close();
    }
}
