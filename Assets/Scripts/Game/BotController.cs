using UnityEngine;

/// A bot's body: rides the bus, skydives and glides to a landing spot, then moves with a
/// CharacterController along the brain's NavMesh paths (crouching, swimming across deep water and
/// mantling when it jumps at a ledge, through BodyMoves), turns to its target, aims (spine and chest
/// like the player) and fires through the shared weapon system with an aim error set by its skill.
[DefaultExecutionOrder(-5)]
public class BotController : MonoBehaviour
{
    public CharacterController cc;
    public Animator anim;
    public Combatant me;
    public WeaponHands hands;
    public GameObject glider;

    public enum Mode { Bus, Skydive, Glide, Ground }
    [System.NonSerialized] public Mode mode = Mode.Bus;
    [System.NonSerialized] public BotBrain brain;
    [System.NonSerialized] public float jumpAt;
    [System.NonSerialized] public Vector3 landing;

    Vector3 _vel;
    float _vy, _air, _yaw, _pitch, _onTarget;
    bool _grounded = true;
    string _base;
    Renderer[] _renderers;
    Transform _spine, _chest, _upper;
    Quaternion _corr = Quaternion.identity;
    BodyMoves _moves;
    bool _dead;
    float _wakeAt, _nextPiece, _stepDist;
    bool _emoting;
    bool _ballistic;   // thrown up by a launch pad: rising before the skydive
    const float Walk = 2.1f, Run = 4.6f, Sprint = 6.4f;
    public static float SwimSeconds, CrouchSeconds;   // bot time spent swimming and crouched (demo logs)
    public static int Mantles, PiecesBuilt, Edits;

    void Start()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
        _moves = new BodyMoves(transform, cc, anim, hands, me);
        _spine = anim.GetBoneTransform(HumanBodyBones.Spine);
        _chest = anim.GetBoneTransform(HumanBodyBones.Chest);
        _upper = anim.GetBoneTransform(HumanBodyBones.UpperChest);
    }

    public void Board()
    {
        mode = Mode.Bus;
        me.inMatch = false;
        cc.enabled = false;
        Show(false);
    }

    /// a launch pad throws the bot up; it glides on towards where it was going
    public void Launch(Vector3 vel)
    {
        if (mode != Mode.Ground || _moves.mantling) return;
        _moves.Reset();
        mode = Mode.Skydive;
        _vel = vel;
        _ballistic = true;
        var flat = new Vector3(brain.moveDir.x, 0f, brain.moveDir.z);
        landing = transform.position + (flat.sqrMagnitude > 0.01f ? flat.normalized : transform.forward) * 45f;
        hands.stowed = true;
        Anim("Skydive", 0.15f);
    }

    /// a rift sends the bot into the sky above it
    public void ToSky(float height)
    {
        if (mode != Mode.Ground) return;
        _moves.Reset();
        cc.enabled = false;
        transform.position += Vector3.up * height;
        cc.enabled = true;
        mode = Mode.Skydive;
        _vel = Vector3.zero;
        _ballistic = false;
        landing = transform.position + Random.insideUnitSphere * 40f;
        hands.stowed = true;
        Anim("Skydive", 0.15f);
    }

    /// the scripted videos: skip the bus and the drop
    public void StartOnGround(Vector3 pos, float yaw)
    {
        mode = Mode.Ground;
        me.inMatch = true;
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        _yaw = yaw;
        cc.enabled = true;
        Show(true);
        Anim(hands.Stance.ToString(), 0f);
    }

    void Show(bool on)
    {
        if (_renderers == null) _renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var r in _renderers) if (r) r.enabled = on;
        if (glider) glider.SetActive(false);
        if (on) hands.Refresh();
    }

    void Jump()
    {
        var m = Match.I;
        transform.position = m.BusPos + Vector3.down * 4f + Random.insideUnitSphere * 2f;
        _vel = m.BusVelocity * 0.4f;
        mode = Mode.Skydive;
        me.inMatch = true;
        Show(true);
        cc.enabled = true;
        Anim("Skydive", 0.2f);
    }

    void Anim(string state, float fade)
    {
        if (state == _base) return;
        anim.CrossFadeInFixedTime(state, _base == null ? 0f : fade, 0);
        _base = state;
    }

    void OnControllerColliderHit(ControllerColliderHit hit) => _moves?.OnHit(hit);

    void Update()
    {
        long t0 = PerfLog.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Step();
        if (PerfLog.On) PerfLog.BotTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    void Step()
    {
        if (!me.Alive) { if (!_dead) { _dead = true; _moves.Reset(); } return; }
        float dt = Time.deltaTime;
        var m = Match.I;
        switch (mode)
        {
            case Mode.Bus:
                transform.position = m.BusPos + Vector3.down * 4f;
                if ((m.DoorsOpen && m.Now >= jumpAt) || m.phase != Match.Phase.Bus) Jump();
                return;
            case Mode.Skydive:
            case Mode.Glide:
                FlightUpdate(dt);
                return;
        }
        GroundUpdate(dt);
    }

    void FlightUpdate(float dt)
    {
        if (_ballistic)
        {
            _vel.y -= 24f * dt;
            cc.Move(_vel * dt);
            if (_vel.y <= 0f) _ballistic = false;
            return;
        }
        var to = landing - transform.position; to.y = 0f;
        float h = Flight.Height(transform.position);
        var wish = to.magnitude > 1f ? to.normalized * Mathf.Clamp01(to.magnitude / 25f) : Vector3.zero;
        if (mode == Mode.Skydive && h < Flight.DeployHeight)
        {
            mode = Mode.Glide;
            if (glider) glider.SetActive(true);
            Anim("Glide", 0.3f);
        }
        _vel = Flight.Step(_vel, wish, mode == Mode.Glide, to.magnitude < 40f, dt);
        var flags = cc.Move(_vel * dt);
        var flat = new Vector3(_vel.x, 0f, _vel.z);
        if (flat.sqrMagnitude > 0.5f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flat), 180f * dt);
        if ((flags & CollisionFlags.Below) != 0 || h < 0.15f)
        {
            mode = Mode.Ground;
            if (glider) glider.SetActive(false);
            hands.stowed = false;
            _vel = flat * 0.3f;
            _yaw = transform.eulerAngles.y;
        }
    }

    Vector3 Eye => transform.position + Vector3.up * 1.55f;

    void GroundUpdate(float dt)
    {
        if (_moves.mantling)
        {
            _moves.MantleStep();
            _vel = Vector3.zero;
            _vy = 0f;
            _grounded = true;
            _moves.ApplyPose(dt);
            Anim("Mantle", 0.08f);
            return;
        }
        float flatSpeed = new Vector2(_vel.x, _vel.z).magnitude;
        _moves.UpdateWater(flatSpeed);
        bool swim = _moves.swimming;
        var aimDir = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;
        long tb = PerfLog.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        brain.Tick(Time.time, Eye, aimDir, dt);
        if (PerfLog.On) PerfLog.BrainTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tb;
        if (brain.select >= 0) { hands.Select(brain.select); brain.select = -1; }
        if (brain.reload) hands.StartReload();
        _moves.SetCrouch(brain.crouch && !swim);
        // build fights: queued pieces go up one every 0.07 s with the material there is most of; edits at once
        if (brain.buildQueue.Count > 0 && Time.time >= _nextPiece && !swim)
        {
            var (type, yaw, pitch, ahead) = brain.buildQueue[0];
            brain.buildQueue.RemoveAt(0);
            _nextPiece = Time.time + 0.07f;
            int mat = 0;
            for (int k = 1; k < 3; k++) if (me.inv.mats[k] > me.inv.mats[mat]) mat = k;
            var f = Quaternion.Euler(0f, Mathf.Round(yaw / 90f) * 90f, 0f) * Vector3.forward;
            var placed = BuildSystem.Place(me, BuildSystem.Compute(type, transform.position + f * BuildRules.Grid * ahead, yaw, pitch), (BuildMat)mat);
            if (placed != null) PiecesBuilt++;
            brain.OnPlaced(type, placed);
        }
        if (brain.editTarget != null) { BuildSystem.Edit(brain.editTarget, brain.editMask); brain.editTarget = null; Edits++; }
        hands.buildMode = brain.state == "rush" || brain.buildQueue.Count > 0;
        if (brain.buildWall && !swim)
        {
            brain.buildWall = false;
            int m = 0;
            for (int k = 1; k < 3; k++) if (me.inv.mats[k] > me.inv.mats[m]) m = k;
            BuildSystem.Place(me, BuildSystem.Compute(PieceType.Wall, transform.position, brain.buildYaw, 0f), (BuildMat)m);
        }
        if (brain.reach is WorldItem wi) Pickup.Take(me, wi, true);
        else if (brain.reach is LootContainer lc) lc.Open();
        brain.reach = null;

        // look: at the target when fighting, else where we go
        float wantYaw = _yaw, wantPitch = 0f;
        if (brain.hasAim)
        {
            var d = brain.aimPoint - Eye;
            wantYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            wantPitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }
        else if (brain.moveDir.sqrMagnitude > 0.05f) wantYaw = Mathf.Atan2(brain.moveDir.x, brain.moveDir.z) * Mathf.Rad2Deg;
        if (swim && brain.moveDir.sqrMagnitude > 0.05f) wantYaw = Mathf.Atan2(brain.moveDir.x, brain.moveDir.z) * Mathf.Rad2Deg;   // swimmers face where they go
        float turn = (brain.hasAim ? 160f + 200f * brain.skill : 400f) * dt;
        _yaw = Mathf.MoveTowardsAngle(_yaw, wantYaw, turn);
        _pitch = Mathf.MoveTowards(_pitch, wantPitch, turn);
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        _onTarget = brain.hasAim && Mathf.Abs(Mathf.DeltaAngle(_yaw, wantYaw)) < 4f ? _onTarget + dt : 0f;

        float speed = swim ? (brain.sprint ? BodyMoves.SwimSprint : BodyMoves.SwimSpeed)
                    : brain.aim ? Walk : _moves.crouched ? BodyMoves.CrouchSpeed : brain.sprint ? Sprint : Run;
        if (!swim && _moves.bedDepth > 0.6f) speed *= 0.75f;   // wading
        var wish = brain.moveDir * speed;
        _vel = Vector3.Lerp(_vel, wish, 1f - Mathf.Exp(-(swim ? 3f : 10f) * dt));
        if (swim)
        {
            _vy = _moves.SwimVy(flatSpeed);
            if (brain.jump && _moves.FindLedge(transform.forward, 0.3f, 2.3f, out var bank)) { _moves.StartMantle(bank); return; }
            if (Time.time > _wakeAt && flatSpeed > 0.8f) { _wakeAt = Time.time + 0.25f; Fx.I.Wake(new Vector3(transform.position.x, _moves.waterY + 0.02f, transform.position.z) + transform.forward * 0.6f); }
        }
        else if (_grounded)
        {
            _vy = -3f;
            if (brain.jump)
            {
                // a jump at a ledge pulls up onto it (the unstick jump uses this too)
                if (_moves.FindLedge(transform.forward, 0.75f, 2.3f, out var top)) { _moves.StartMantle(top); Mantles++; return; }
                _moves.SetCrouch(false);
                _vy = Mathf.Sqrt(2f * 24f * 1.2f); _grounded = false;
            }
        }
        else _vy -= 24f * dt;
        var flags = cc.Move((_vel + _moves.SteepSlide(_grounded) + Vector3.up * _vy) * dt);
        _grounded = (flags & CollisionFlags.Below) != 0 || swim;
        _air = _grounded ? 0f : _air + dt;

        _moves.ApplyPose(dt);
        hands.spreadMul = _moves.SpreadMul;
        if (swim) SwimSeconds += dt;
        if (_moves.crouched) CrouchSeconds += dt;
        var lv = Quaternion.Inverse(transform.rotation) * _vel;
        anim.SetFloat("VelX", lv.x, 0.08f, dt);
        anim.SetFloat("VelZ", lv.z, 0.08f, dt);
        anim.SetFloat("SwimRate", Mathf.Clamp(flatSpeed / 1.6f, 0.8f, 2.4f));
        bool emoting = brain.state == "emote" && brain.emote > 0 && !swim;
        if (emoting != _emoting) { _emoting = emoting; hands.stowed = emoting; }
        if (swim) Anim(flatSpeed > 0.7f ? "Swim" : "Tread", 0.3f);
        else if (emoting) Anim(GameAssets.Emotes[brain.emote - 1], 0.25f);
        else Anim(_air > 0.15f ? (hands.Stance == Stance.Rifle ? "Rifle_Air" : "Air") : hands.Stance.ToString(), 0.18f);
        // footsteps, only for bots near the camera
        _stepDist += flatSpeed * dt;
        if (!swim && _grounded && flatSpeed > 0.6f && _stepDist > 0.8f)
        {
            _stepDist = 0f;
            var cam = Camera.main;
            if (cam != null && (cam.transform.position - transform.position).sqrMagnitude < 40f * 40f)
                Sfx.I?.PlayAt(PlayerController.Footstep(transform.position), transform.position, Mathf.Clamp(flatSpeed / 6f, 0.25f, 0.6f), 3f);
        }

        // fire with an aim error that shrinks while tracking the same target
        float motion = new Vector2(_vel.x, _vel.z).magnitude / Run;
        float err = Mathf.Lerp(5.5f, 1.6f, brain.skill) * (1f + motion * 0.4f) * (_onTarget < 0.8f ? 1.5f : 1f);
        hands.TryFire(brain.firePressed, brain.fireHeld, brain.aim, motion, s =>
        {
            var dir = brain.hasAim ? (brain.aimPoint - Eye).normalized : transform.forward;
            float a = s + err;
            var r = Random.insideUnitCircle * Mathf.Tan(a * Mathf.Deg2Rad);
            var basis = Quaternion.LookRotation(dir);
            return new Ray(Eye, (dir + basis * new Vector3(r.x, r.y, 0f)).normalized);
        }, null);
    }

    void LateUpdate()
    {
        if (!me.Alive || mode != Mode.Ground || _moves.swimming || _moves.mantling) return;
        bool gun = hands.Stance == Stance.Rifle || hands.Stance == Stance.Pistol;
        float total = gun ? 1f : 0.45f;
        var axis = transform.right;
        if (_spine) _spine.rotation = Quaternion.AngleAxis(_pitch * 0.3f * total, axis) * _spine.rotation;
        if (_chest) _chest.rotation = Quaternion.AngleAxis(_pitch * 0.35f * total, axis) * _chest.rotation;
        if (_upper) _upper.rotation = Quaternion.AngleAxis(_pitch * 0.35f * total, axis) * _upper.rotation;
        var muzzle = hands.Muzzle;
        var bone = _upper ? _upper : _chest;
        if (gun && muzzle != null && bone != null && brain.hasAim && !hands.Reloading)
        {
            var want = Quaternion.FromToRotation(muzzle.forward, (brain.aimPoint - muzzle.position).normalized);
            want.ToAngleAxis(out float ang, out var ax);
            if (ang > 180f) ang -= 360f;
            want = Quaternion.AngleAxis(Mathf.Clamp(ang, -30f, 30f), ax);
            _corr = Quaternion.Slerp(_corr, want, 1f - Mathf.Exp(-Time.deltaTime * 12f));
            bone.rotation = _corr * bone.rotation;
        }
        else _corr = Quaternion.Slerp(_corr, Quaternion.identity, 1f - Mathf.Exp(-Time.deltaTime * 8f));
    }
}
