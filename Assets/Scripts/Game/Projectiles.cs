using System.Collections.Generic;
using UnityEngine;

/// Rocket from the launcher: flies straight with a little drop, leaves a smoke trail, explodes on contact.
public class Rocket : MonoBehaviour
{
    WeaponDef _def;
    Combatant _owner;
    Vector3 _vel;
    float _life, _mult;

    public static void Launch(Vector3 pos, Vector3 dir, WeaponDef def, Combatant owner, float mult, GameObject tipTemplate)
    {
        var go = new GameObject("Rocket");
        go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir));
        // the visual: a copy of the loaded rocket on the launcher model
        if (tipTemplate != null)
        {
            var v = Instantiate(tipTemplate, go.transform, false);
            v.SetActive(true);
            v.transform.localPosition = Vector3.zero;
            v.transform.localRotation = Quaternion.identity;
            v.transform.localScale = tipTemplate.transform.lossyScale;
            foreach (var c in v.GetComponentsInChildren<Collider>()) Destroy(c);
            foreach (var t in v.GetComponentsInChildren<Transform>()) t.gameObject.layer = 0;
            // the model's node origin is the launcher grip, not the rocket: centre the mesh on the projectile
            var rend = v.GetComponentInChildren<Renderer>();
            if (rend != null) v.transform.position += go.transform.position - rend.bounds.center;
        }
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.6f, 0.3f);
        light.range = 4f;
        light.intensity = 2f;
        light.shadows = LightShadows.None;
        var r = go.AddComponent<Rocket>();
        r._def = def;
        r._owner = owner;
        r._mult = mult;
        r._vel = dir * def.speed;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        _life += dt;
        _vel += Vector3.down * 1.2f * dt;
        var step = _vel * dt;
        if (GameLayers.Cast(transform.position, step.normalized, step.magnitude + 0.05f, out var hit, _owner) || _life > 8f)
        {
            var at = _life > 8f ? transform.position : hit.point + hit.normal * 0.2f;
            Explosion.Detonate(at, _def, _owner, _life > 8f ? null : GameLayers.Target(hit.collider), _mult);
            Destroy(gameObject);
            return;
        }
        transform.position += step;
        transform.rotation = Quaternion.LookRotation(_vel);
        Fx.I.Trail(transform.position - transform.forward * 0.4f, -transform.forward);
    }
}

/// Thrown grenade: a physics body that bounces and explodes after its fuse.
public class Grenade : MonoBehaviour
{
    WeaponDef _def;
    Combatant _owner;
    float _fuse = 2.4f, _lastBounce, _mult;

    public static void Throw(Vector3 pos, Vector3 velocity, WeaponDef def, Combatant owner, float mult, GameObject template)
    {
        GameObject go;
        if (template != null)
        {
            go = Instantiate(template);
            go.SetActive(true);
            go.transform.localScale = template.transform.lossyScale;
        }
        else go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "ThrownGrenade";
        go.transform.position = pos;
        foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.layer = 0;
        foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
        var col = go.AddComponent<SphereCollider>();
        col.radius = 0.07f / Mathf.Max(0.01f, go.transform.lossyScale.x);
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.4f;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.5f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.linearVelocity = velocity;
        rb.angularVelocity = Random.insideUnitSphere * 10f;
        col.sharedMaterial = new PhysicsMaterial { bounciness = 0.35f, dynamicFriction = 0.6f, staticFriction = 0.6f };
        var occ = owner != null ? owner.GetComponent<CharacterController>() : null;
        if (occ != null) Physics.IgnoreCollision(col, occ);
        var g = go.AddComponent<Grenade>();
        g._def = def;
        g._owner = owner;
        g._mult = mult;
    }

    void Update()
    {
        _fuse -= Time.deltaTime;
        if (_fuse > 0f) return;
        Explosion.Detonate(transform.position + Vector3.up * 0.15f, _def, _owner, null, _mult);
        Destroy(gameObject);
    }

    void OnCollisionEnter(Collision c)
    {
        if (Time.time - _lastBounce < 0.15f || c.relativeVelocity.magnitude < 1.5f) return;
        _lastBounce = Time.time;
        Sfx.I?.PlayAt("bounce", transform.position, 0.5f);
    }
}

/// Explosion damage: falls off with distance, blocked by walls; the thrower's own blast hurts them too
/// (half damage), like rocket and grenade splash in Fortnite.
public static class Explosion
{
    public static void Detonate(Vector3 pos, WeaponDef def, Combatant owner, IDamageable direct, float mult)
    {
        Fx.I.Explosion(pos, def.radius);
        Sfx.I?.PlayAt("explosion", pos, 1f, 12f);
        Physics.SyncTransforms();
        var done = new HashSet<IDamageable>();
        bool hitAny = false, killAny = false;
        foreach (var c in Physics.OverlapSphere(pos, def.radius, GameLayers.Hitboxes, QueryTriggerInteraction.Collide))
        {
            var t = GameLayers.Target(c);
            if (t == null || !t.Alive || !done.Add(t)) continue;
            var center = c.bounds.center;
            float d = Vector3.Distance(pos, c.ClosestPoint(pos));
            if (t != direct && Physics.Linecast(pos, center, GameLayers.Env, QueryTriggerInteraction.Ignore)) continue;   // behind a wall
            float k = t == direct ? 1f : Mathf.Clamp01(1f - d / def.radius) * 0.75f + 0.25f;
            if (ReferenceEquals(t, owner)) k *= 0.5f;
            var e = new DamageEvent { amount = def.damage * mult * k, point = center, dir = (center - pos).normalized, explosion = true, weapon = def, source = owner };
            float dealt = t.TakeDamage(e);
            if (owner != null && owner.isPlayer && !ReferenceEquals(t, owner))
            {
                GameEvents.RaiseHit(e, t, dealt, !t.Alive);
                hitAny = true; killAny |= !t.Alive;
            }
        }
        if (hitAny) Sfx.I?.Play2D(killAny ? "kill" : "hitmarker");
        // builds and level parts in the blast (explosives are strong against structures, as in Fortnite)
        var broken = new HashSet<Destructible>();
        foreach (var c in Physics.OverlapSphere(pos, def.radius, GameLayers.Env, QueryTriggerInteraction.Ignore))
        {
            var st = c.GetComponentInParent<Destructible>();
            if (st == null || !st.Alive || !broken.Add(st)) continue;
            var cp = Nearest(c, pos);
            float k = Mathf.Clamp01(1f - Vector3.Distance(pos, cp) / def.radius) * 0.6f + 0.4f;
            st.Hit(def.damage * mult * 2.5f * k, cp, (cp - pos).normalized, owner, false);
        }
        var cam = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
        if (cam != null && cam.target != null) cam.Shake(Mathf.Clamp01(1.2f - Vector3.Distance(pos, cam.target.position) / 30f));
    }

    /// the collider's point nearest the blast (ClosestPoint does not work on concave mesh colliders:
    /// there, the surface towards the collider's centre)
    static Vector3 Nearest(Collider c, Vector3 p)
    {
        if (!(c is MeshCollider mc) || mc.convex) return c.ClosestPoint(p);
        var b = c.bounds;
        if (b.Contains(p)) return p;
        var to = b.center - p;
        return c.Raycast(new Ray(p, to.normalized), out var h, to.magnitude + 1f) ? h.point : b.ClosestPoint(p);
    }
}
