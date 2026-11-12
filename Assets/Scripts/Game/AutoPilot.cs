using System.Collections;
using System.IO;
using UnityEngine;

/// Plays a whole match as the player for the gameplay video (player build with -demo -demoOut <dir>):
/// opens the map in the bus, jumps near a loot spot, glides there, then uses the bot brain for the
/// decisions but drives the player only through input (camera turns, keys, mouse buttons), exactly
/// like a human would. Every frame is written at a fixed 30 fps game clock.
public class AutoPilot : MonoBehaviour, IPlayerInputSource
{
    public PlayerController pc;
    public string outDir;
    float _crouchAt;
    public int fps = 30;
    public float maxSeconds = 420f;

    BotBrain _brain;
    Vector3 _landing;
    float _mapUntil, _endAt = -1f;
    bool _jumped, _done;
    int _frame, _buildStep, _slotBack;

    void Start()
    {
        _brain = new BotBrain(pc.me, 0.92f, 7);
        _brain.canBuildFight = false;   // boxes, ramp rushes and edits are bot-only (the autopilot plays through input)
        Time.captureFramerate = fps;
        if (!string.IsNullOrEmpty(outDir)) { Directory.CreateDirectory(outDir); StartCoroutine(Record()); }
        _landing = GameBootstrap.OpenGround(PickLanding());
        _mapUntil = Time.time + 3.5f;
    }

    /// a treasure chest near the bus route where few bots are landing (no hot drop)
    Vector3 PickLanding()
    {
        var bots = FindObjectsByType<BotController>();
        Vector3 best = Vector3.zero; float bestScore = float.MaxValue;
        foreach (var c in LootContainer.All)
        {
            if (!c.chest) continue;
            var p = c.transform.position;
            float route = DistanceToRoute(p);
            if (route > 150f) continue;
            int crowd = 0;
            foreach (var b in bots) if (Vector3.Distance(new Vector3(b.landing.x, 0, b.landing.z), new Vector3(p.x, 0, p.z)) < 45f) crowd++;
            float score = route * 0.4f + crowd * 70f + Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(0, 0, 10)) * 0.15f;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best == Vector3.zero ? new Vector3(0, 0, 10) : best;
    }

    static float DistanceToRoute(Vector3 p)
    {
        var m = Match.I;
        var a = new Vector2(m.busA.x, m.busA.z); var b = new Vector2(m.busB.x, m.busB.z); var q = new Vector2(p.x, p.z);
        var ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(q, a + ab * t);
    }

    void Look(ref PlayerInputState s, Vector3 target, float rate)
    {
        var cam = pc.cam;
        var dir = target - cam.transform.position;
        float wantYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        float wantPitch = -Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        float k = 1f - Mathf.Exp(-Time.deltaTime * rate);
        s.look = new Vector2(Mathf.DeltaAngle(cam.yaw, wantYaw) * k, -(wantPitch - cam.pitch) * k);
    }

    public PlayerInputState Read()
    {
        var s = new PlayerInputState { slot = -1, build = -1 };
        var m = Match.I;
        if (pc.Dead || _done)
        {
            if (_endAt < 0f) _endAt = Time.time + 9f;
            return s;
        }
        if (m.phase == Match.Phase.Ended && _endAt < 0f) _endAt = Time.time + 8f;
        switch (pc.mode)
        {
            case PlayerController.Mode.Bus:
            {
                // show the map for a moment, then jump when the bus is closest to the landing spot
                s.map = (Time.time < _mapUntil) != pc.showMap;
                var cam = pc.cam;
                s.look = new Vector2(Time.deltaTime * 8f, -(28f - cam.pitch) * Time.deltaTime * 2f);
                var bus = m.BusPos; bus.y = 0f;
                var land = _landing; land.y = 0f;
                float along = Vector3.Dot(land - bus, m.BusDir);
                if (m.DoorsOpen && Time.time > _mapUntil && along < 30f) s.jump = true;
                return s;
            }
            case PlayerController.Mode.Skydive:
            case PlayerController.Mode.Glide:
            {
                var to = _landing - pc.transform.position;
                var flat = new Vector3(to.x, 0f, to.z);
                Look(ref s, pc.transform.position + flat.normalized * 10f + Vector3.down * (flat.magnitude < 60f ? 14f : 3f), 4f);
                s.move = new Vector2(0f, Mathf.Clamp01(flat.magnitude / 8f));
                return s;
            }
        }
        // on the ground: the bot brain decides, the player's input carries it out
        var eye = pc.AimOrigin;
        // building a wall under fire takes three frames of input: Q, click, back to the weapon
        if (_buildStep == 1) { _buildStep = 2; s.firePressed = true; return s; }
        if (_buildStep == 2) { _buildStep = 0; s.slot = _slotBack; return s; }
        _brain.Tick(Time.time, eye, pc.cam.Forward, Time.deltaTime);
        if (_brain.buildWall)
        {
            _brain.buildWall = false;
            Look(ref s, pc.transform.position + Quaternion.Euler(0f, _brain.buildYaw, 0f) * Vector3.forward * 10f + Vector3.up * 1.5f, 30f);
            s.build = 0;
            _slotBack = pc.me.inv.selected;
            _buildStep = 1;
            return s;
        }
        if (_brain.select >= 0) { s.slot = _brain.select; _brain.select = -1; }
        if (_brain.hasAim) Look(ref s, _brain.aimPoint, 9f);
        else if (_brain.moveDir.sqrMagnitude > 0.05f) Look(ref s, pc.transform.position + _brain.moveDir * 20f + Vector3.up * 1.2f, 4f);
        var local = Quaternion.Inverse(Quaternion.Euler(0f, pc.cam.yaw, 0f)) * _brain.moveDir;
        s.move = Vector2.ClampMagnitude(new Vector2(local.x, local.z), 1f);
        s.sprint = _brain.sprint;
        s.jump = _brain.jump;
        s.aim = _brain.aim;
        s.fireHeld = _brain.fireHeld;
        s.firePressed = _brain.firePressed;
        s.reload = _brain.reload;
        // the brain's crouch is a state; the input is a toggle key
        if (_brain.crouch != pc.moves.crouched && !pc.moves.sliding && Time.time > _crouchAt) { s.crouch = true; _crouchAt = Time.time + 0.4f; }
        s.interact = _brain.reach != null;
        _brain.reach = null;
        return s;
    }

    void Update()
    {
        if (_done) return;
        if ((_endAt > 0f && Time.time >= _endAt) || Time.time > maxSeconds) StartCoroutine(Finish());
    }

    IEnumerator Finish()
    {
        _done = true;
        yield return null;
        Debug.Log($"[Demo] placement {pc.me.placement}, kills {pc.me.kills}, winner {(Match.I.winner != null ? Match.I.winner.displayName : "none")}, {Match.I.Now:F1} s");
        string builds = $"pieces_built_player={BuildSystem.PlacedByPlayer}\npieces_built_bots={BuildSystem.PlacedByBots}\npieces_broken={Destructible.BuiltBroken}\nlevel_parts_broken={Destructible.LevelBroken}\nplayer_mats={pc.me.inv.mats[0]}/{pc.me.inv.mats[1]}/{pc.me.inv.mats[2]}\n";
        Debug.Log("[Demo] " + builds.Replace("\n", " "));
        GameBootstrap.LogStats();
        if (!string.IsNullOrEmpty(outDir))
            File.WriteAllText(Path.Combine(outDir, "demo_log.txt"),
                $"frames={_frame}\nplacement={pc.me.placement}\nkills={pc.me.kills}\nwinner={(Match.I.winner != null ? Match.I.winner.displayName : "none")}\nmatch_seconds={Match.I.Now:F1}\n" + builds);
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
