using UnityEngine;

/// Water for swimming: the lake, river and sea meshes carry colliders on Unity's Water layer (added by
/// the editor) that nothing collides with and only these queries see.
public static class Water
{
    public const int Layer = 4;
    public static int Mask => 1 << Layer;

    public static void Setup()
    {
        for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(Layer, i, true);
    }

    /// the highest water surface at p (false where there is none)
    public static bool Surface(Vector3 p, out float y)
    {
        if (Physics.Raycast(new Vector3(p.x, p.y + 30f, p.z), Vector3.down, out var h, 60f, Mask, QueryTriggerInteraction.Collide)) { y = h.point.y; return true; }
        y = float.MinValue;
        return false;
    }
}

/// Fortnite's movement extras, shared by the player and the bots: crouching (smaller and steadier,
/// slower), sliding out of a sprint (faster downhill), mantling onto ledges up to 2.3 m, swimming in
/// deep water (weapons away, no fall damage). The controllers feed it input and their velocity; it owns
/// the capsule height, the hitbox, the crouch animation layer and the special base states.
public class BodyMoves
{
    public const float StandH = 1.8f, CrouchH = 1.2f, SlideH = 1.0f;
    public const float CrouchSpeed = 2.3f, SwimSpeed = 3.0f, SwimSprint = 4.3f;
    const float SwimIn = 1.35f, SwimOut = 1.15f;          // water above the bed to start / stop swimming
    const float SlideMin = 7.4f, SlideStop = 2.6f, SlideFriction = 4.4f, SlideMax = 12.5f;
    const float Gravity = 24f;

    readonly Transform _tr;
    readonly CharacterController _cc;
    readonly Animator _anim;
    readonly WeaponHands _hands;
    readonly Combatant _me;
    readonly int _crouchLayer;

    public bool crouched, sliding, swimming, mantling;
    public float waterY = float.MinValue, bedDepth;     // surface, and how deep the water is where we stand
    public bool justEnteredWater;
    Vector3 _slide;
    Vector3 _mFrom, _mTo;
    float _mT0, _mDur, _height = StandH, _crouchW, _pivot = 1.58f;
    string _crouchState;

    public BodyMoves(Transform tr, CharacterController cc, Animator anim, WeaponHands hands, Combatant me)
    {
        _tr = tr; _cc = cc; _anim = anim; _hands = hands; _me = me;
        _crouchLayer = anim.GetLayerIndex("Crouch");
    }

    // ---------------------------------------------------------------- steep ground

    Vector3 _floorNormal = Vector3.up;

    /// the controllers pass on their CharacterController contacts; the last one under the feet is the floor
    public void OnHit(ControllerColliderHit hit)
    {
        if (hit.point.y < _tr.position.y + _cc.radius && hit.normal.y > 0.05f) _floorNormal = hit.normal;
    }

    /// A CharacterController neither climbs nor slides down a surface steeper than its slope limit: on a
    /// roof's ridge it stands for good (the autopilot spent a whole match on one, landing on the loot
    /// spot inside a house). Standing on such a surface gives a push down the slope, added to the move.
    public Vector3 SteepSlide(bool grounded)
    {
        var n = _floorNormal;
        _floorNormal = Vector3.up;
        if (!grounded || swimming || mantling || Vector3.Angle(n, Vector3.up) <= _cc.slopeLimit + 2f) return Vector3.zero;
        var down = new Vector3(n.x, 0f, n.z);
        return down.sqrMagnitude > 1e-4f ? down.normalized * 4.5f : Vector3.zero;
    }

    /// camera pivot above the feet: lower when crouching, sliding or swimming
    public float Pivot => _pivot;
    public bool Low => crouched || sliding;
    public float SpreadMul => sliding ? 1.1f : crouched ? 0.72f : 1f;
    public float SwimDrop(float speed) => Mathf.Lerp(GameAssets.I.treadDrop, GameAssets.I.swimDrop, Mathf.Clamp01(speed / SwimSpeed));

    // ---------------------------------------------------------------- water

    /// where we stand: the water surface and its depth over the bed; starts and stops swimming
    public void UpdateWater(float horizSpeed)
    {
        justEnteredWater = false;
        var p = _tr.position;
        if (!Water.Surface(p, out waterY)) { bedDepth = 0f; if (swimming) StopSwim(); return; }
        float bed = Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out var g, 8f, GameLayers.Env, QueryTriggerInteraction.Ignore) ? g.point.y : p.y - 8f;
        bedDepth = waterY - bed;
        if (!swimming && bedDepth > SwimIn && p.y < waterY - 0.6f)
        {
            swimming = true;
            justEnteredWater = true;
            sliding = false;
            SetCrouch(false, true);
            _hands.stowed = true;
            _hands.CancelAction();
            Fx.I?.Splash(new Vector3(p.x, waterY, p.z), 1f);
            Sfx.I?.PlayAt("splash", p, 0.8f);
        }
        else if (swimming && bedDepth < SwimOut) StopSwim();
    }

    void StopSwim()
    {
        swimming = false;
        if (!mantling) _hands.stowed = false;
    }

    /// vertical speed that keeps the head at the surface (treading water still, lower when swimming)
    public float SwimVy(float horizSpeed)
    {
        float want = waterY - SwimDrop(horizSpeed) + Mathf.Sin(Time.time * 2.2f) * 0.03f;
        return Mathf.Clamp((want - _tr.position.y) * 5f, -3f, 3f);
    }

    // ---------------------------------------------------------------- crouch and slide

    public bool Fits(Vector3 pos, float h)
    {
        float r = _cc.radius * 0.92f;
        return !Physics.CheckCapsule(pos + Vector3.up * (r + 0.08f), pos + Vector3.up * (h - r), r, GameLayers.Env, QueryTriggerInteraction.Ignore);
    }

    /// crouch or stand; standing up needs head room
    public void SetCrouch(bool on, bool force = false)
    {
        if (!on && crouched && !force && !Fits(_tr.position, StandH)) return;
        crouched = on && !swimming;
    }

    public bool CanSlide(Vector3 vel, bool grounded) => grounded && !swimming && !mantling && new Vector2(vel.x, vel.z).magnitude > 5.2f;

    public void StartSlide(Vector3 vel)
    {
        var flat = new Vector3(vel.x, 0f, vel.z);
        _slide = flat.normalized * Mathf.Max(flat.magnitude * 1.12f, SlideMin);
        sliding = true;
        crouched = false;
        Sfx.I?.PlayAt("slide", _tr.position, 0.6f);
    }

    /// ends a slide; it settles into a crouch (as in Fortnite) unless asked to stand
    public void EndSlide(bool crouch)
    {
        if (!sliding) return;
        sliding = false;
        crouched = crouch || !Fits(_tr.position, StandH);
    }

    /// one slide step: friction on the flat, gravity along slopes, a little steering
    public Vector3 SlideStep(float dt, float steer, bool grounded)
    {
        float speed = _slide.magnitude;
        var dir = speed > 0.01f ? _slide / speed : _tr.forward;
        dir = Quaternion.Euler(0f, steer * 45f * dt, 0f) * dir;
        float accel = -SlideFriction;
        if (grounded && Physics.Raycast(_tr.position + Vector3.up * 0.3f, Vector3.down, out var g, 1.2f, GameLayers.Env, QueryTriggerInteraction.Ignore))
        {
            var downhill = Vector3.ProjectOnPlane(Vector3.down, g.normal);
            downhill.y = 0f;
            accel += Gravity * Vector3.Dot(downhill, dir) * 0.85f;
        }
        speed = Mathf.Clamp(speed + accel * dt, 0f, SlideMax);
        _slide = dir * speed;
        if (speed < SlideStop) EndSlide(true);
        return _slide;
    }

    // ---------------------------------------------------------------- mantle

    /// a ledge ahead to pull up onto: a wall at waist height or above whose top is between minH and maxH
    /// above the feet, flat, with room to stand
    public bool FindLedge(Vector3 fwd, float minH, float maxH, out Vector3 top)
    {
        top = default;
        var feet = _tr.position;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) return false;
        fwd.Normalize();
        float r = _cc.radius;
        if (!Physics.SphereCast(feet + Vector3.up * Mathf.Max(0.5f, minH - 0.15f), 0.22f, fwd, out var wall, r + 0.75f, GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;
        if (wall.normal.y > 0.6f) return false;
        var probe = wall.point + fwd * (r + 0.2f);
        probe.y = feet.y + maxH + 0.35f;
        if (Physics.CheckSphere(probe, 0.2f, GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;   // taller than we reach
        if (!Physics.SphereCast(probe, 0.2f, Vector3.down, out var t, maxH + 0.35f - 0.25f, GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;
        if (t.normal.y < 0.7f) return false;
        float h = t.point.y - feet.y;
        if (h < minH || h > maxH) return false;
        var stand = new Vector3(probe.x, t.point.y + 0.02f, probe.z);
        if (!Fits(stand, StandH)) return false;
        // head room on the way up and over the edge
        if (Physics.Linecast(feet + Vector3.up * 1.6f, new Vector3(feet.x, stand.y + 1.6f, feet.z), GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;
        top = stand;
        return true;
    }

    public void StartMantle(Vector3 top)
    {
        mantling = true;
        sliding = false;
        crouched = false;
        _mFrom = _tr.position;
        _mTo = top;
        _mT0 = Time.time;
        _mDur = 0.38f + 0.2f * Mathf.Max(0f, top.y - _mFrom.y);
        _cc.enabled = false;
        _hands.stowed = true;
        _hands.CancelAction();
        _anim.SetFloat("MantleRate", GameAssets.I.mantleLength / _mDur);
        Sfx.I?.PlayAt("mantle", _tr.position + Vector3.up, 0.6f);
    }

    /// up first, then over the edge; true while it lasts
    public bool MantleStep()
    {
        float t = Mathf.Clamp01((Time.time - _mT0) / _mDur);
        float up = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.62f));
        float over = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.42f) / 0.58f));
        var p = Vector3.Lerp(new Vector3(_mFrom.x, 0f, _mFrom.z), new Vector3(_mTo.x, 0f, _mTo.z), over);
        p.y = Mathf.Lerp(_mFrom.y, _mTo.y + 0.03f, up);
        _tr.position = p;
        if (t < 1f) return true;
        mantling = false;
        _cc.enabled = true;
        if (!swimming) _hands.stowed = false;
        return false;
    }

    /// eliminated: standing pose for the death clip, full capsule, nothing special going on
    public void Reset()
    {
        crouched = sliding = swimming = false;
        if (mantling) { mantling = false; _cc.enabled = true; }
        _hands.stowed = false;
        _crouchW = 0f;
        if (_crouchLayer >= 0) _anim.SetLayerWeight(_crouchLayer, 0f);
    }

    // ---------------------------------------------------------------- pose

    /// capsule and hitbox height, the camera pivot, the crouch layer and its slide state
    public void ApplyPose(float dt)
    {
        float want = sliding ? SlideH : crouched ? CrouchH : StandH;
        if (!Mathf.Approximately(want, _height))
        {
            _height = Mathf.MoveTowards(_height, want, dt * 5f);
            if (_cc.enabled)
            {
                _cc.height = _height;
                _cc.center = new Vector3(0f, _height * 0.5f + 0.02f, 0f);
            }
            _me.SetHitboxHeight(_height);
        }
        float pivot = swimming ? 1.58f : sliding ? 1.0f : crouched ? 1.15f : 1.58f;
        _pivot = Mathf.MoveTowards(_pivot, pivot, dt * 3.5f);
        if (_crouchLayer < 0) return;
        float w = (crouched || sliding) && !swimming && !mantling ? 1f : 0f;
        _crouchW = Mathf.MoveTowards(_crouchW, w, dt * 7f);
        _anim.SetLayerWeight(_crouchLayer, _crouchW);
        string st = sliding ? "Slide" : "Crouch";
        if (st != _crouchState)
        {
            _anim.CrossFadeInFixedTime(st, _crouchState == null ? 0f : 0.15f, _crouchLayer);
            _crouchState = st;
        }
    }
}
