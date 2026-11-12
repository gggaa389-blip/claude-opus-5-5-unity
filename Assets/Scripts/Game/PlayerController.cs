using System;
using UnityEngine;

/// The player in a match: rides the bus (camera around the drop vehicle), jumps, skydives and glides, then
/// runs and fights in third person (always facing the camera direction and strafing, like Fortnite
/// with a weapon out), crouches, slides, mantles onto ledges and swims (BodyMoves), picks up loot,
/// builds, and after an elimination spectates whoever got them. Drives the Animator and bends the spine
/// so the weapon points at the crosshair.
[DefaultExecutionOrder(-10)]
public class PlayerController : MonoBehaviour
{
    public Animator anim;
    public CharacterController cc;
    public Combatant me;
    public WeaponHands hands;
    public GameObject glider;
    public float walkSpeed = 2.1f, runSpeed = 4.6f, sprintSpeed = 6.4f, backMul = 0.78f;
    public float jumpHeight = 1.25f, gravity = 24f;

    public enum Mode { Bus, Skydive, Glide, Ground, Dead, Zip }
    [NonSerialized] public Mode mode = Mode.Bus;
    [NonSerialized] public ThirdPersonCamera cam;
    [NonSerialized] public IPlayerInputSource source;
    [NonSerialized] public PlayerInputState input;
    [NonSerialized] public bool aiming, sprinting, showMap;
    [NonSerialized] public Vector3 velocity;
    [NonSerialized] public float stamina = 1f;
    [NonSerialized] public Component nearest;      // WorldItem or LootContainer in reach
    [NonSerialized] public Combatant spectating;
    [NonSerialized] public bool building;
    [NonSerialized] public PieceType piece;
    [NonSerialized] public BuildMat buildMat;
    [NonSerialized] public BuildSystem.Slot buildSlot;
    [NonSerialized] public bool buildOk;
    [NonSerialized] public BodyMoves moves;
    [NonSerialized] public Destructible editing;    // the wall or floor being edited
    [NonSerialized] public int editMask, editHover = -1;
    GameObject _grid;
    Renderer[] _gridTiles;
    bool _dragSet;
    [NonSerialized] public ZipLine nearZip;          // a zip line in reach (E rides it)
    ZipLine _zip;
    float _zipT, _zipSpeed, _nearZipT;
    bool _ballistic;                                 // thrown up by a launch pad: rising before the skydive
    const float ZipSpeed = 15f, HangDrop = 2.1f;     // m/s along the cable; the hands are 2.1 m above the feet
    GameObject[] _ghosts;
    MaterialPropertyBlock _ghostMpb;
    string _lastPlaced;

    float _vy, _air, _deadAt, _wakeAt, _stepDist, _victoryAt = -1f, _victoryYaw;
    bool _killCam;
    Vector3 _lastPos;
    AudioSource _beat;
    [NonSerialized] public bool wheelOpen;          // the emote wheel (B); the mouse picks a slice
    [NonSerialized] public int wheelHover = -1, emoting = -1;
    [NonSerialized] public Vector2 wheel;
    bool _grounded = true;
    string _base;
    Transform _spine, _chest, _upperChest, _head;
    Quaternion _corr = Quaternion.identity;
    Renderer[] _renderers;
    bool _hidden, _faded;
    UnityEngine.Rendering.ShadowCastingMode[] _shadowModes;
    Transform _camTarget;

    public bool Grounded => _grounded;
    public bool Dead => !me.Alive;
    /// 0 standing still .. 1 at run speed, more when sprinting or in the air (weapon spread)
    public float Motion => (_grounded ? 0f : 0.8f) + new Vector2(velocity.x, velocity.z).magnitude / runSpeed;
    public Vector3 AimOrigin => transform.position + Vector3.up * 1.45f;

    void Start()
    {
        _spine = anim.GetBoneTransform(HumanBodyBones.Spine);
        _chest = anim.GetBoneTransform(HumanBodyBones.Chest);
        _upperChest = anim.GetBoneTransform(HumanBodyBones.UpperChest);
        _head = anim.GetBoneTransform(HumanBodyBones.Head);
        _renderers = GetComponentsInChildren<Renderer>(true);
        _shadowModes = System.Array.ConvertAll(_renderers, r => r.shadowCastingMode);
        moves = new BodyMoves(transform, cc, anim, hands, me);
        _camTarget = new GameObject("CameraTarget").transform;
        cam.target = _camTarget;
        cam.yaw = mode == Mode.Bus && Match.I != null ? Mathf.Atan2(Match.I.BusDir.x, Match.I.BusDir.z) * Mathf.Rad2Deg + 60f : transform.eulerAngles.y;
        cam.pitch = mode == Mode.Bus ? 28f : 8f;
        cam.hipFov = Settings.Fov;
        _lastPos = transform.position;
        if (mode == Mode.Bus) Board(); else hands.Refresh();
        // the build preview: one see-through copy of each piece
        var a = GameAssets.I;
        _ghosts = new GameObject[4];
        _ghostMpb = new MaterialPropertyBlock();
        for (int t = 0; t < 4; t++)
        {
            var g = new GameObject("Ghost_" + (PieceType)t) { layer = 2 };
            var m = Instantiate(a.buildPrefabs[t * 3], g.transform, false);
            m.transform.localRotation = Quaternion.Euler(0f, a.buildYaw[t * 3], 0f);
            m.transform.localPosition = a.buildOffset[t * 3];
            foreach (var c in m.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var l in m.GetComponentsInChildren<LODGroup>(true)) Destroy(l);
            foreach (var r in m.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int k = 0; k < mats.Length; k++) mats[k] = a.ghostMat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.gameObject.layer = 2;
            }
            g.SetActive(false);
            _ghosts[t] = g;
        }
    }

    /// skip the bus and the drop (the building video starts on the ground)
    public void StartOnGround()
    {
        mode = Mode.Ground;
        me.inMatch = true;
    }

    /// the scripted videos cut between places
    public void Teleport(Vector3 pos, float yaw)
    {
        moves.Reset();
        cc.enabled = false;
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        cc.enabled = true;
        velocity = Vector3.zero;
        _vy = 0f;
        cam.yaw = yaw;
        cam.pitch = 8f;
        _camTarget.position = pos;
    }

    void Board()
    {
        mode = Mode.Bus;
        me.inMatch = false;
        cc.enabled = false;
        SetVisible(false);
    }

    void SetVisible(bool on)
    {
        _hidden = !on;
        foreach (var r in _renderers) if (r) r.enabled = on;
        if (glider) glider.SetActive(false);
        if (on) hands.Refresh();
    }

    void Anim(string state, float fade)
    {
        if (state == _base) return;
        anim.CrossFadeInFixedTime(state, _base == null ? 0f : fade, 0);
        _base = state;
    }

    void OnControllerColliderHit(ControllerColliderHit hit) => moves?.OnHit(hit);

    void Update()
    {
        float dt = Time.deltaTime;
        input = source != null ? source.Read() : new PlayerInputState { slot = -1, build = -1 };
        if (input.map) showMap = !showMap;
        if (source is HumanInput h) h.aimScale = aiming ? Settings.AdsSensitivity / 0.7f * Mathf.Max(0.25f, hands.Def.adsFov / cam.hipFov) : 1f;
        // the emote wheel takes the mouse while it is open
        if (wheelOpen)
        {
            wheel = Vector2.ClampMagnitude(wheel + new Vector2(input.look.x, -input.look.y) * 4f, 120f);
            input.look = Vector2.zero;
            wheelHover = wheel.magnitude > 30f ? Mathf.FloorToInt(Mathf.Repeat(Mathf.Atan2(wheel.x, -wheel.y) * Mathf.Rad2Deg + 30f, 360f) / 60f) % 6 : -1;
        }
        cam.AddLook(input.look);
        var m = Match.I;
        // distance for the match summary, the wind of falling for the sound
        var step = transform.position - _lastPos; step.y = 0f;
        if (me.Alive && me.inMatch && mode != Mode.Bus && step.magnitude < 20f) MatchStats.distance += step.magnitude;
        _lastPos = transform.position;
        Sfx.windLevel = mode == Mode.Skydive ? 1f : mode == Mode.Glide ? 0.45f : 0f;

        if (Dead)
        {
            if (mode != Mode.Dead) { mode = Mode.Dead; _deadAt = Time.time; moves.Reset(); StopEmote(); wheelOpen = false; MatchStats.survived = Match.I != null ? Match.I.Now : 0f; }
            // the kill cam: the last seconds again, slowed down, over the shoulder of whoever got us
            if (!_killCam && Time.time - _deadAt > 1.2f)
            {
                _killCam = true;
                if (me.lastAttacker != null && me.lastAttacker != me) ReplayPlayer.Play(5f, me.lastAttacker, 0.5f, "KILL CAM", true, () => Hud.SummaryAt = UiClock.Now + 1.5f);
                else Hud.SummaryAt = UiClock.Now + 1.5f;
            }
            // after a moment, watch whoever got us (Fortnite spectating)
            var killer = me.lastAttacker;
            if (Time.time - _deadAt > 3f && killer != null && killer.Alive) spectating = killer;
            if (spectating != null && !spectating.Alive) spectating = null;
            var follow = spectating != null ? spectating.transform : transform;
            SetCamera(follow.position + Vector3.up * 0.1f, new Vector3(0.5f, 0.3f, -3.6f), false);
            return;
        }

        if (m != null && m.PlayerWon && mode == Mode.Ground) { VictoryUpdate(dt); return; }

        switch (mode)
        {
            case Mode.Bus:
                transform.position = m.BusPos + Vector3.down * 4f;
                // round the middle of the drop vehicle (wheels 6 m above the route, balloon top 12.6 m above them)
                SetCamera(m.BusPos + Vector3.up * 12f, new Vector3(0f, 3f, -26f), false);
                if ((input.jump && m.DoorsOpen) || m.phase != Match.Phase.Bus) LeaveBus();
                return;
            case Mode.Skydive:
            case Mode.Glide:
                FlightUpdate(dt);
                return;
            case Mode.Zip:
                ZipUpdate(dt);
                return;
        }
        GroundUpdate(dt);
    }

    /// Victory Royale: the celebration emote, the camera slowly swinging round in front
    void VictoryUpdate(float dt)
    {
        if (_victoryAt < 0f)
        {
            _victoryAt = Time.time;
            _victoryYaw = transform.eulerAngles.y;
            MatchStats.survived = Match.I.Now;
            Hud.SummaryAt = UiClock.Now + 7f;
            StopEdit(); building = false; hands.buildMode = false; moves.Reset(); StopEmote(); wheelOpen = false;
            hands.stowed = true;
            _beat = Sfx.I?.Loop("beat", transform, 0.5f, false);
        }
        float t = Time.time - _victoryAt;
        cam.yaw = _victoryYaw + 180f + Mathf.Sin(t * 0.35f) * 35f;
        cam.pitch = 3f;
        cam.aiming = false;
        SetCamera(transform.position + Vector3.up * 2.0f, new Vector3(0f, 0.2f, -4.0f), false);
        cc.Move(Vector3.down * 3f * dt);
        Anim("E_Victory", 0.3f);
    }

    void StartEmote(int k)
    {
        emoting = k;
        hands.CancelAction();
        hands.stowed = true;
        moves.SetCrouch(false);
        if (_beat == null) _beat = Sfx.I?.Loop("beat", transform, 0.45f, false);
    }

    void StopEmote()
    {
        if (emoting < 0) return;
        emoting = -1;
        if (!moves.swimming && !moves.mantling) hands.stowed = false;
        if (_beat != null) Destroy(_beat.gameObject);
        _beat = null;
    }

    void SetCamera(Vector3 pivot, Vector3 offset, bool ground)
    {
        _camTarget.position = pivot;
        cam.pivotHeight = ground ? 1.58f : 0f;
        cam.overrideOffset = ground ? (Vector3?)null : offset;
        cam.collide = mode != Mode.Bus;
    }

    void LeaveBus()
    {
        var m = Match.I;
        transform.position = m.BusPos + Vector3.down * 4f;
        velocity = m.BusVelocity * 0.4f;
        mode = Mode.Skydive;
        me.inMatch = true;
        SetVisible(true);
        cc.enabled = true;
        Anim("Skydive", 0.2f);
        Sfx.I?.Play2D("throw", 0.6f);
    }

    /// a launch pad: straight up (steering a little), the skydive and the glider follow at the top
    public void Launch(Vector3 vel)
    {
        if (mode != Mode.Ground && mode != Mode.Zip) return;
        LeaveSpecial();
        mode = Mode.Skydive;
        velocity = vel;
        _ballistic = true;
        Anim("Skydive", 0.15f);
    }

    /// a rift: high above, skydiving
    public void ToSky(float height)
    {
        if (mode != Mode.Ground && mode != Mode.Zip) return;
        LeaveSpecial();
        cc.enabled = false;
        transform.position += Vector3.up * height;
        cc.enabled = true;
        mode = Mode.Skydive;
        velocity = Vector3.zero;
        _ballistic = false;
        Anim("Skydive", 0.15f);
    }

    /// out of building, editing, crouching or a zip line, weapons away for the flight
    void LeaveSpecial()
    {
        StopEdit();
        building = false;
        hands.buildMode = false;
        moves.Reset();
        _zip = null;
        cc.enabled = true;
        hands.stowed = true;
        hands.CancelAction();
    }

    void StartZip(ZipLine z, float t)
    {
        LeaveSpecial();
        _zip = z;
        _zipT = Mathf.Clamp(t, 0.02f, 0.98f);
        _zipSpeed = 0f;
        mode = Mode.Zip;
        cc.enabled = false;
        Sfx.I?.PlayAt("zip", transform.position + Vector3.up * 2f, 0.6f);
    }

    /// hanging from the cable, riding the way the camera faces; Space lets go
    void ZipUpdate(float dt)
    {
        var z = _zip;
        var along = z.b - z.a; along.y = 0f; along.Normalize();
        var look = Vector3.ProjectOnPlane(cam.Forward, Vector3.up).normalized;
        float want = (Vector3.Dot(look, along) >= 0f ? 1f : -1f) * ZipSpeed;
        _zipSpeed = Mathf.MoveTowards(_zipSpeed, want, 24f * dt);
        _zipT += _zipSpeed * dt / z.Length;
        var p = z.Point(Mathf.Clamp01(_zipT));
        transform.position = p - Vector3.up * HangDrop;
        if (Mathf.Abs(_zipSpeed) > 0.5f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(along * Mathf.Sign(_zipSpeed)), 400f * dt);
        velocity = along * _zipSpeed;
        SetCamera(transform.position, Vector3.zero, true);
        Anim("Glide", 0.15f);
        bool end = _zipT <= 0f || _zipT >= 1f;
        if (input.jump || end)
        {
            mode = Mode.Ground;
            _zip = null;
            cc.enabled = true;
            hands.stowed = false;
            _vy = input.jump ? 7f : 2f;
            _grounded = false;
            _air = 0.2f;
            _base = null;
        }
    }

    void FlightUpdate(float dt)
    {
        if (_ballistic)
        {
            velocity.y -= gravity * dt;
            var wishB = Quaternion.Euler(0f, cam.yaw, 0f) * new Vector3(input.move.x, 0f, input.move.y);
            var hv = Vector3.Lerp(new Vector3(velocity.x, 0f, velocity.z), Vector3.ClampMagnitude(wishB, 1f) * Flight.SkySteer * 0.6f, 1f - Mathf.Exp(-1.2f * dt));
            velocity = new Vector3(hv.x, velocity.y, hv.z);
            cc.Move(velocity * dt);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, cam.yaw, 0f), 200f * dt);
            SetCamera(transform.position + Vector3.up * 1.2f, new Vector3(0f, 0.6f, -5.5f), false);
            if (velocity.y <= 0f) _ballistic = false;
            return;
        }
        float h = Flight.Height(transform.position);
        if (mode == Mode.Skydive && (h < Flight.DeployHeight || (input.jump && h < 120f)))
        {
            mode = Mode.Glide;
            if (glider) glider.SetActive(true);
            Anim("Glide", 0.3f);
        }
        var face = Quaternion.Euler(0f, cam.yaw, 0f);
        var wish = face * new Vector3(input.move.x, 0f, input.move.y);
        bool dive = input.move.y > 0.5f && cam.pitch > 40f;
        velocity = Flight.Step(velocity, Vector3.ClampMagnitude(wish, 1f), mode == Mode.Glide, dive, dt);
        var flags = cc.Move(velocity * dt);
        var flat = new Vector3(velocity.x, 0f, velocity.z);
        var turn = flat.sqrMagnitude > 1f ? Quaternion.LookRotation(flat) : face;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, turn, 200f * dt);
        SetCamera(transform.position + Vector3.up * 1.2f, new Vector3(0f, 0.6f, mode == Mode.Glide ? -6.5f : -5.5f), false);
        if ((flags & CollisionFlags.Below) != 0 || h < 0.15f)
        {
            mode = Mode.Ground;
            hands.stowed = false;
            if (glider) glider.SetActive(false);
            velocity = flat * 0.3f;
            _vy = -3f;
            _base = null;
        }
    }

    void GroundUpdate(float dt)
    {
        SetCamera(transform.position, Vector3.zero, true);
        // pulling up onto a ledge: nothing else until it is done
        if (moves.mantling)
        {
            moves.MantleStep();
            velocity = Vector3.zero;
            _vy = 0f;
            _grounded = true;
            moves.ApplyPose(dt);
            cam.pivotHeight = moves.Pivot;
            Anim("Mantle", 0.08f);
            return;
        }
        var def = hands.Def;
        float flatSpeed = new Vector2(velocity.x, velocity.z).magnitude;
        moves.UpdateWater(flatSpeed);
        bool swim = moves.swimming;
        // build mode: Q Z C V pick a piece, a weapon slot or the wheel goes back to the weapons
        if (input.build >= 0 && !swim) { building = true; piece = (PieceType)input.build; hands.CancelAction(); }
        if (building && (input.slot >= 0 || input.scroll != 0 || swim)) building = false;
        EditUpdate();
        hands.buildMode = building || editing != null;
        aiming = !building && editing == null && !swim && input.aim && def.CanAim && !hands.Reloading && _grounded;
        cam.aiming = aiming;
        cam.adsFov = def.adsFov;
        cam.scoped = def.scope;

        if (input.slot >= 0) hands.Select(input.slot);
        if (input.scroll != 0) hands.Cycle(input.scroll);
        if (input.reload && !building) hands.StartReload();
        Interact();
        BuildUpdate();
        // a zip line in reach: E rides it (items in reach come first)
        nearZip = null;
        if (!swim && editing == null)
            foreach (var z in ZipLine.All)
            {
                float t = z.Closest(AimOrigin + Vector3.up * 0.5f, out float d);
                if (d < 3f) { nearZip = z; _nearZipT = t; break; }
            }
        if (nearZip != null && input.interact && nearest == null) { StartZip(nearZip, _nearZipT); return; }

        var mv = Vector2.ClampMagnitude(input.move, 1f);
        // emotes: B opens the wheel, a click (or B again) on a slice plays one; moving, jumping, firing,
        // building or a weapon key stops it
        if (input.emote == 9 && editing == null && !building && !swim && _grounded)
        {
            if (!wheelOpen) { wheelOpen = true; wheel = Vector2.zero; wheelHover = -1; }
            else { wheelOpen = false; if (wheelHover >= 0) StartEmote(wheelHover); }
        }
        if (wheelOpen && (input.firePressed || input.aimPressed))
        {
            wheelOpen = false;
            if (input.firePressed && wheelHover >= 0) StartEmote(wheelHover);
            input.firePressed = input.fireHeld = input.aimPressed = false;
        }
        if (input.emote >= 1 && input.emote <= 6 && _grounded && !swim) StartEmote(input.emote - 1);
        if (emoting >= 0 && (mv.sqrMagnitude > 0.04f || input.jump || input.firePressed || input.slot >= 0 || input.build >= 0 || input.crouch || swim))
            StopEmote();
        bool wantSprint = input.sprint && mv.y > 0.3f;
        // Left Ctrl: crouch or stand up; out of a sprint it slides; sprinting stands you up
        if (input.crouch && !swim)
        {
            if (moves.sliding) moves.EndSlide(false);
            else if (wantSprint && moves.CanSlide(velocity, _grounded || _air < 0.3f)) moves.StartSlide(velocity);   // running downhill leaves the ground for moments
            else moves.SetCrouch(!moves.crouched);
        }
        if (moves.crouched && wantSprint && !input.crouch) moves.SetCrouch(false);
        sprinting = wantSprint && !aiming && !hands.BusyFiring && _grounded && stamina > 0.02f && !hands.Using && !moves.Low;
        stamina = Mathf.Clamp01(stamina + (sprinting ? -dt / 9f : dt / 5f));
        float speed = swim ? (input.sprint ? BodyMoves.SwimSprint : BodyMoves.SwimSpeed)
                    : aiming || hands.Using ? walkSpeed : moves.crouched ? BodyMoves.CrouchSpeed : sprinting ? sprintSpeed : runSpeed;
        if (!swim && moves.bedDepth > 0.6f) speed *= 0.75f;   // wading
        var local = new Vector3(mv.x, 0f, mv.y) * speed;
        if (local.z < 0f && !swim) local *= backMul;

        var face = Quaternion.Euler(0f, cam.yaw, 0f);
        var wish = face * local;
        // swimming turns the body where it goes; on land it always faces the camera direction
        if (swim) { if (wish.sqrMagnitude > 0.1f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(wish), 360f * dt); }
        else if (emoting < 0) transform.rotation = Quaternion.RotateTowards(transform.rotation, face, 1080f * dt);
        if (moves.sliding) velocity = moves.SlideStep(dt, mv.x, _grounded);
        else velocity = Vector3.Lerp(velocity, wish, 1f - Mathf.Exp(-(swim ? 3f : _grounded ? 12f : 2.5f) * dt));

        // jumping, mantling (Space at a ledge, or pressing forward against one in the air), floating
        var fwd = face * Vector3.forward;
        if (swim)
        {
            _vy = moves.SwimVy(flatSpeed);
            if (input.jump && moves.FindLedge(wish.sqrMagnitude > 0.1f ? wish : transform.forward, 0.3f, 2.3f, out var bank)) { moves.StartMantle(bank); return; }
            if (Time.time > _wakeAt && flatSpeed > 0.8f) { _wakeAt = Time.time + 0.18f; Fx.I.Wake(new Vector3(transform.position.x, moves.waterY + 0.02f, transform.position.z) + transform.forward * 0.6f); }
        }
        else
        {
            bool forward = mv.y > 0.4f;
            if (input.jump && forward && (_grounded || _air < 0.3f) && moves.FindLedge(fwd, 0.75f, 2.3f, out var top)) { moves.StartMantle(top); return; }
            if (!_grounded && forward && _air > 0.12f && moves.FindLedge(fwd, 0.25f, 1.5f, out top)) { moves.StartMantle(top); return; }
            if (_grounded)
            {
                _vy = -3f;
                if (input.jump)
                {
                    moves.EndSlide(false);
                    moves.SetCrouch(false);
                    if (!moves.crouched) { _vy = Mathf.Sqrt(2f * gravity * jumpHeight); _grounded = false; _air = 0.2f; }
                }
            }
            else _vy -= gravity * dt;
        }
        var flags = cc.Move((velocity + moves.SteepSlide(_grounded) + Vector3.up * _vy) * dt);
        bool wasGrounded = _grounded;
        _grounded = (flags & CollisionFlags.Below) != 0 || swim;
        if ((flags & CollisionFlags.Above) != 0 && _vy > 0f) _vy = 0f;
        _air = _grounded ? 0f : _air + dt;
        // falls hurt, except into water
        bool wet = Water.Surface(transform.position, out var wy) && wy > transform.position.y + 0.5f;
        if (!wasGrounded && _grounded && _vy < -14f && !wet && !swim)
            me.TakeDamage(new DamageEvent { amount = (-_vy - 14f) * 6f, fall = true, point = transform.position, source = me });
        if (transform.position.y < -8f && !wet) me.TakeDamage(new DamageEvent { amount = 1000f, fall = true, source = me });
        if (!_grounded && moves.sliding && _air > 0.35f) moves.EndSlide(false);

        if (!building && editing == null && emoting < 0 && !wheelOpen) hands.TryFire(input.firePressed, input.fireHeld, aiming, Motion, s => cam.AimRay(s), d =>
        {
            MatchStats.shots++;
            cam.Kick(d.recoil * (aiming ? 0.6f : 1f), UnityEngine.Random.Range(-0.35f, 0.35f) * d.recoil);
            if (d.kind == FireKind.Rocket) cam.Shake(0.3f);
        });

        moves.ApplyPose(dt);
        cam.pivotHeight = moves.Pivot;
        hands.spreadMul = moves.SpreadMul;
        var lv = Quaternion.Inverse(transform.rotation) * velocity;
        anim.SetFloat("VelX", lv.x, 0.08f, dt);
        anim.SetFloat("VelZ", lv.z, 0.08f, dt);
        anim.SetFloat("SwimRate", Mathf.Clamp(flatSpeed / 1.6f, 0.8f, 2.4f));
        if (swim) Anim(flatSpeed > 0.7f ? "Swim" : "Tread", 0.3f);
        else if (emoting >= 0) Anim(GameAssets.Emotes[emoting], 0.25f);
        else Anim(_air > 0.15f ? (hands.Stance == Stance.Rifle ? "Rifle_Air" : "Air") : hands.Stance.ToString(), 0.18f);
        // footsteps by the surface underfoot; strokes when swimming
        _stepDist += flatSpeed * dt;
        if (swim && _stepDist > 2.2f) { _stepDist = 0f; Sfx.I?.PlayAt("stroke", transform.position + Vector3.up, 0.35f); }
        else if (!swim && _grounded && flatSpeed > 0.6f && _stepDist > (sprinting ? 0.95f : 0.75f))
        {
            _stepDist = 0f;
            Sfx.I?.PlayAt(Footstep(transform.position), transform.position, Mathf.Clamp(flatSpeed / 6f, 0.25f, 0.6f), 3f);
        }

        // hide the character while looking through the sniper scope (the camera sits at the eye)
        bool hide = aiming && def.scope && cam.aiming;
        if (hide != _hidden)
        {
            _hidden = hide;
            foreach (var r in _renderers) if (r) r.enabled = !hide;
            if (!hide) hands.Refresh();
        }
        // and while the camera is pressed against the head: only the shadow stays
        bool fade = !hide && cam.Close;
        if (fade != _faded)
        {
            _faded = fade;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (!r) continue;
                // parts that never cast a shadow (effects) just stop drawing
                if (_shadowModes[i] == UnityEngine.Rendering.ShadowCastingMode.Off) r.forceRenderingOff = fade;
                else r.shadowCastingMode = fade ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : _shadowModes[i];
            }
        }
    }

    /// ammo and items for a free slot are picked up by walking over them (Fortnite's auto pick-up);
    /// E swaps with the selected slot or searches a chest / ammo box
    void Interact()
    {
        var p = transform.position + Vector3.up * 0.5f;
        nearest = null;
        float best = 2.4f;
        var fwd = Vector3.ProjectOnPlane(cam.Forward, Vector3.up).normalized;
        for (int i = WorldItem.All.Count - 1; i >= 0; i--)
        {
            var w = WorldItem.All[i];
            if (w == null || !w.Settled) continue;
            var d = w.transform.position - p;
            float dist = d.magnitude;
            if (dist < 1.6f && (w.IsAmmo || (!me.inv.Full || (w.item.Stacks && CanStack(w.item)))))
            {
                if (Pickup.Take(me, w, false)) { Sfx.I?.Play2D("pickup", 0.5f); GameEvents.RaiseFeed("+ " + w.Label + (w.IsMats ? $" x{w.matCount}" : w.IsAmmo ? $" x{w.ammoCount}" : "")); continue; }
            }
            float score = dist - Mathf.Max(0f, Vector3.Dot(d.normalized, fwd)) * 0.6f;
            if (dist < 2.4f && score < best) { best = score; nearest = w; }
        }
        foreach (var c in LootContainer.All)
        {
            if (c.opened) continue;
            float dist = Vector3.Distance(c.transform.position, transform.position);
            if (dist < 2.6f && dist < best + 0.5f) { best = dist; nearest = c; }
        }
        if (!input.interact || nearest == null) return;
        if (nearest is LootContainer lc) lc.Open();
        else if (nearest is WorldItem wi && Pickup.Take(me, wi, true)) Sfx.I?.Play2D("pickup", 0.7f);
        hands.Refresh();
    }

    void BuildUpdate()
    {
        var inv = me.inv;
        if (building)
        {
            if (input.aimPressed) buildMat = (BuildMat)(((int)buildMat + 1) % 3);
            // out of the chosen material: use whichever there is enough of
            if (inv.mats[(int)buildMat] < BuildRules.Cost)
                for (int k = 0; k < 3; k++) if (inv.mats[k] >= BuildRules.Cost) { buildMat = (BuildMat)k; break; }
            buildSlot = BuildSystem.Compute(piece, transform.position, cam.yaw, cam.pitch);
            buildOk = buildSlot.free && inv.mats[(int)buildMat] >= BuildRules.Cost;
        }
        // the preview: the chosen piece where it would go, nothing over a spot that is already built
        for (int t = 0; t < 4; t++)
        {
            bool show = building && t == (int)piece && buildSlot.free;
            if (_ghosts[t].activeSelf != show) _ghosts[t].SetActive(show);
        }
        if (!building) return;
        var g = _ghosts[(int)piece];
        g.transform.SetPositionAndRotation(buildSlot.pos, Quaternion.Euler(0f, buildSlot.yaw, 0f));
        _ghostMpb.SetColor("_Color", buildOk ? new Color(0.35f, 0.7f, 1f, 0.38f) : new Color(1f, 0.3f, 0.25f, 0.32f));
        foreach (var r in g.GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(_ghostMpb);
        // click to place; holding the button keeps placing as the slot changes (turbo build)
        if (buildOk && (input.firePressed || (input.fireHeld && buildSlot.key != _lastPlaced)))
        {
            if (BuildSystem.Place(me, buildSlot, buildMat) != null) _lastPlaced = buildSlot.key;
        }
        if (!input.fireHeld) _lastPlaced = null;
    }

    /// G on a wall or floor in reach: its tiles show as a blue grid; LMB (or dragging) picks the tiles to cut
    /// out, G applies the edit, RMB makes the piece whole again (Fortnite's PC defaults)
    void EditUpdate()
    {
        if (editing == null)
        {
            if (!input.edit || moves.swimming) return;
            if (!Physics.Raycast(cam.AimRay(0f), out var h, 40f, GameLayers.Env, QueryTriggerInteraction.Ignore) || Vector3.Distance(h.point, AimOrigin) > 5.5f) return;
            var d = h.collider.GetComponentInParent<Destructible>();
            if (d == null || !d.built || !d.Alive || !PieceEdit.Editable(d.piece)) return;   // editable while it still builds, as in Fortnite
            editing = d;
            editMask = d.editMask;
            building = false;
            hands.CancelAction();
            ShowGrid();
            return;
        }
        if (!editing || !editing.Alive || Vector3.Distance(editing.bounds.center, transform.position) > 7f || input.slot >= 0 || input.build >= 0 || moves.swimming) { StopEdit(); return; }
        if (input.edit) { BuildSystem.Edit(editing, editMask); StopEdit(); return; }
        if (input.aimPressed) { BuildSystem.Edit(editing, 0); StopEdit(); return; }
        // the tile under the crosshair: the aim ray against the piece's middle plane
        var r = cam.AimRay(0f);
        var root = editing.transform;
        var o = root.InverseTransformPoint(r.origin);
        var dir = root.InverseTransformDirection(r.direction);
        var src = GameAssets.I.buildMeshes[editing.meshIdx];
        bool wall = editing.piece == PieceType.Wall;
        float plane = wall ? 0f : src.bounds.size.y * 0.5f, along = wall ? dir.z : dir.y, from = wall ? o.z : o.y;
        editHover = -1;
        if (Mathf.Abs(along) > 1e-4f)
        {
            float t = (plane - from) / along;
            if (t > 0f && t < 12f) editHover = PieceEdit.TileAt(editing.piece, o + dir * t);
        }
        if (editHover >= 0)
        {
            bool cut = (editMask & (1 << editHover)) != 0;
            if (input.firePressed) _dragSet = !cut;
            if ((input.firePressed || input.fireHeld) && cut != _dragSet)
            {
                int m = _dragSet ? editMask | (1 << editHover) : editMask & ~(1 << editHover);
                if (m != PieceEdit.Full(editing.piece)) editMask = m;
            }
        }
        for (int i = 0; i < _gridTiles.Length; i++)
        {
            bool cut = (editMask & (1 << i)) != 0;
            var c = cut ? new Color(0.9f, 0.95f, 1f, 0.1f) : new Color(0.15f, 0.5f, 1f, 0.45f);
            if (i == editHover) c.a += 0.18f;
            _ghostMpb.SetColor("_Color", c);
            _gridTiles[i].SetPropertyBlock(_ghostMpb);
        }
    }

    void ShowGrid()
    {
        _grid = new GameObject("EditGrid") { layer = 2 };
        _grid.transform.SetParent(editing.transform, false);
        var src = GameAssets.I.buildMeshes[editing.meshIdx];
        bool wall = editing.piece == PieceType.Wall;
        float thick = (wall ? src.bounds.size.z : src.bounds.size.y) + 0.1f;
        int n = PieceEdit.Tiles(editing.piece);
        _gridTiles = new Renderer[n];
        for (int i = 0; i < n; i++)
        {
            var tb = PieceEdit.Tile(editing.piece, i, thick);
            var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(q.GetComponent<Collider>());
            q.layer = 2;
            q.transform.SetParent(_grid.transform, false);
            if (!wall) tb.center = new Vector3(tb.center.x, tb.center.y - 0.05f, tb.center.z);
            q.transform.localPosition = tb.center;
            q.transform.localScale = wall ? new Vector3(tb.size.x - 0.12f, tb.size.y - 0.12f, tb.size.z) : new Vector3(tb.size.x - 0.12f, tb.size.y, tb.size.z - 0.12f);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = GameAssets.I.ghostMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _gridTiles[i] = r;
        }
    }

    void StopEdit()
    {
        editing = null;
        editHover = -1;
        if (_grid != null) Destroy(_grid);
        _grid = null;
    }

    /// the footstep sound for the ground under p: soft on the island's ground, a knock on wooden builds,
    /// hard on everything else
    public static string Footstep(Vector3 p)
    {
        if (!Physics.Raycast(p + Vector3.up * 0.3f, Vector3.down, out var g, 0.8f, GameLayers.Env, QueryTriggerInteraction.Ignore)) return "step_soft";
        if (g.collider is TerrainCollider) return "step_soft";
        var d = g.collider.GetComponentInParent<Destructible>();
        return d != null && d.mat == BuildMat.Wood ? "step_wood" : "step_hard";
    }

    bool CanStack(Item it)
    {
        foreach (var s in me.inv.slots) if (s != null && s.Id == it.Id && s.mag < s.MaxStack) return true;
        return false;
    }

    /// after the Animator: bend the spine towards the camera pitch and turn the chest so the weapon
    /// barrel points at what the crosshair is on
    void LateUpdate()
    {
        if (Dead || mode != Mode.Ground || moves.swimming || moves.mantling) return;
        var def = hands.Def;
        float pitch = cam.pitch;
        bool gun = def.stance == Stance.Rifle || def.stance == Stance.Pistol;
        float total = gun ? 1f : 0.45f;
        var axis = transform.right;
        if (_spine) _spine.rotation = Quaternion.AngleAxis(pitch * 0.3f * total, axis) * _spine.rotation;
        if (_chest) _chest.rotation = Quaternion.AngleAxis(pitch * 0.35f * total, axis) * _chest.rotation;
        if (_upperChest) _upperChest.rotation = Quaternion.AngleAxis(pitch * 0.35f * total, axis) * _upperChest.rotation;
        else if (_chest) _chest.rotation = Quaternion.AngleAxis(pitch * 0.35f * total, axis) * _chest.rotation;

        var muzzle = hands.Muzzle;
        var bone = _upperChest ? _upperChest : _chest;
        if (gun && muzzle != null && bone != null && !hands.Reloading)
        {
            var ray = cam.AimRay(0f);
            var aimPoint = GameLayers.Cast(ray.origin, ray.direction, 1000f, out var ah, me) ? ah.point : ray.origin + ray.direction * 1000f;
            var want = Quaternion.FromToRotation(muzzle.forward, (aimPoint - muzzle.position).normalized);
            want.ToAngleAxis(out float ang, out var ax);
            if (ang > 180f) ang -= 360f;
            want = Quaternion.AngleAxis(Mathf.Clamp(ang, -35f, 35f), ax);
            _corr = Quaternion.Slerp(_corr, want, 1f - Mathf.Exp(-Time.deltaTime * 18f));
            bone.rotation = _corr * bone.rotation;
        }
        else _corr = Quaternion.Slerp(_corr, Quaternion.identity, 1f - Mathf.Exp(-Time.deltaTime * 10f));
        if (_head && !gun) _head.rotation = Quaternion.AngleAxis(pitch * 0.4f, axis) * _head.rotation;
    }
}
