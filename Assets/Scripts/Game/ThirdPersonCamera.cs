using UnityEngine;

/// Over-the-right-shoulder camera: orbits the player with the mouse, zooms in when aiming down sights,
/// slides in front of walls instead of clipping through them, and takes recoil kicks and shake.
[DefaultExecutionOrder(100)]
public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;
    public Camera cam;
    public float yaw, pitch;                       // pitch > 0 looks down
    public float pivotHeight = 1.58f;
    // a little further right and higher than at first: the stylized outfits' big heads (Aj's cap) hid
    // too much of the view beside the crosshair
    public Vector3 hipOffset = new Vector3(0.74f, 0.2f, -2.9f);
    public Vector3 adsOffset = new Vector3(0.8f, 0.14f, -1.85f);
    public float hipFov = 72f;

    [System.NonSerialized] public bool aiming;
    [System.NonSerialized] public float adsFov = 55f;
    [System.NonSerialized] public bool scoped;     // sniper scope: the camera sits at the eye
    [System.NonSerialized] public Vector3? overrideOffset;   // bus, skydive, spectating: a fixed orbit offset
    [System.NonSerialized] public bool collide = true;        // off in the bus (the drop vehicle has colliders)
    // _dist starts far so the first frame snaps to the wanted distance: easing out from 2.75 m began the
    // match with the camera inside the bus for half a second
    float _blend, _kickP, _kickY, _shake, _dist = 1000f;
    bool _close;

    /// pressed against the character's head by walls (a doorway, under a bridge): the player then keeps
    /// only its shadow, as Fortnite fades the character out (some slack so it does not flicker)
    public bool Close => _close;

    public Quaternion Rotation => Quaternion.Euler(pitch + _kickP, yaw + _kickY, 0f);
    public Vector3 Forward => transform.forward;

    public void AddLook(Vector2 d)
    {
        yaw += d.x;
        pitch = Mathf.Clamp(pitch - d.y, -70f, 75f);
    }

    /// recoil: 40 % stays (the aim climbs), 60 % springs back
    public void Kick(float up, float side)
    {
        pitch = Mathf.Clamp(pitch - up * 0.4f, -70f, 75f);
        yaw += side * 0.4f;
        _kickP -= up * 0.6f;
        _kickY += side * 0.6f;
    }

    public void Shake(float amount) => _shake = Mathf.Max(_shake, amount);

    void LateUpdate()
    {
        if (target == null) return;
        float dt = Time.deltaTime;
        _blend = Mathf.MoveTowards(_blend, aiming ? 1f : 0f, dt * 7f);
        float b = Mathf.SmoothStep(0f, 1f, _blend);
        _kickP = Mathf.Lerp(_kickP, 0f, 1f - Mathf.Exp(-dt * 12f));
        _kickY = Mathf.Lerp(_kickY, 0f, 1f - Mathf.Exp(-dt * 12f));
        _shake = Mathf.MoveTowards(_shake, 0f, dt * 2.5f);

        var rot = Rotation;
        var off = overrideOffset ?? Vector3.Lerp(hipOffset, adsOffset, b);
        if (overrideOffset == null && scoped && b > 0.95f) off = new Vector3(0.3f, 0.02f, 0.2f);
        var pivot = target.position + Vector3.up * pivotHeight;
        var shoulder = pivot + rot * new Vector3(off.x, off.y, 0f);
        // keep the shoulder point out of walls, then pull the camera in front of anything behind it
        if (collide && Physics.SphereCast(pivot, 0.18f, (shoulder - pivot).normalized, out var h1, off.x, GameLayers.Env, QueryTriggerInteraction.Ignore))
            shoulder = pivot + (shoulder - pivot).normalized * Mathf.Max(0f, h1.distance - 0.05f);
        float want = -off.z;
        float dist = want;
        var back = rot * Vector3.back;
        if (collide && want > 0f && Physics.SphereCast(shoulder, 0.2f, back, out var h2, want, GameLayers.Env, QueryTriggerInteraction.Ignore))
            dist = Mathf.Max(0.3f, h2.distance - 0.05f);
        // pull in fast, ease back out
        _dist = dist < _dist ? dist : Mathf.Lerp(_dist, dist, 1f - Mathf.Exp(-dt * 6f));
        var pos = shoulder + back * _dist;
        if (want <= 0f) pos = shoulder + rot * Vector3.forward * off.z;
        // measured from the head, not along the camera arm: a door frame beside the player can squeeze
        // the shoulder point onto the head while the wall behind is still far
        float headDist = Vector3.Distance(pos, pivot);
        _close = overrideOffset == null && !scoped && want > 0f && (_close ? headDist < 1.25f : headDist < 1.0f);

        if (_shake > 0f)
        {
            float t = Time.time * 25f;
            pos += rot * new Vector3(Mathf.PerlinNoise(t, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, t) - 0.5f, 0f) * _shake * 0.35f;
        }
        transform.SetPositionAndRotation(pos, rot);
        cam.fieldOfView = overrideOffset != null ? hipFov : Mathf.Lerp(hipFov, adsFov, b);
    }

    /// ray through the crosshair, started next to the player so walls behind the player are ignored
    public Ray AimRay(float spreadDeg)
    {
        var rot = Rotation;
        var dir = rot * Vector3.forward;
        if (spreadDeg > 0f)
        {
            var r = Random.insideUnitCircle * Mathf.Tan(spreadDeg * Mathf.Deg2Rad);
            dir = (dir + rot * new Vector3(r.x, r.y, 0f)).normalized;
        }
        var origin = transform.position;
        if (target != null)
        {
            // start at the point of the camera ray closest to the player's shoulders
            var pivot = target.position + Vector3.up * pivotHeight;
            float along = Mathf.Max(0f, Vector3.Dot(pivot - origin, dir));
            origin += dir * along;
        }
        return new Ray(origin, dir);
    }
}
