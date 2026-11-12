using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// Pooled visual effects built in code: bullet tracers, muzzle flash, impact sparks and dust, hit
/// puffs on characters, bullet holes, rocket smoke trails and explosions.
public class Fx : MonoBehaviour
{
    public static Fx I;

    ParticleSystem _spark, _dust, _puff, _fire, _smoke, _trail;
    readonly List<LineRenderer> _tracers = new List<LineRenderer>();
    readonly List<float> _tracerT = new List<float>();
    const float TracerLife = 0.07f;
    readonly Transform[] _decals = new Transform[120];
    int _decalNext;
    Mesh _quad;
    // muzzle flashes: a small pool, everybody fires (the player and the bots)
    readonly Transform[] _flashes = new Transform[8];
    readonly float[] _flashUntil = new float[8];
    int _flashNext;
    Light _boomLight;
    float _boomT = 9f;

    void Awake()
    {
        I = this;
        var a = GameAssets.I;
        _quad = MakeQuad();
        _spark = MakeSystem("Sparks", a.additive, 1.2f, 400, Grad(new Color(1f, 0.85f, 0.45f), new Color(1f, 0.45f, 0.1f)), stretch: true, grow: 0.5f);
        _dust = MakeSystem("Dust", a.smoke, -0.05f, 400, Grad(new Color(0.75f, 0.7f, 0.62f, 0.7f), new Color(0.7f, 0.66f, 0.6f, 0f)), grow: 2.5f);
        _puff = MakeSystem("HitPuff", a.additive, 0f, 200, Grad(new Color(1f, 1f, 1f, 0.9f), new Color(0.6f, 0.85f, 1f, 0f)), grow: 1.8f);
        _fire = MakeSystem("Fire", a.additive, -0.15f, 300, Grad(new Color(1f, 0.9f, 0.55f), new Color(1f, 0.35f, 0.05f, 0f)), grow: 2.2f);
        _smoke = MakeSystem("Smoke", a.smoke, -0.12f, 600, Grad(new Color(0.32f, 0.3f, 0.29f, 0.85f), new Color(0.45f, 0.44f, 0.43f, 0f)), grow: 3f);
        _trail = MakeSystem("RocketTrail", a.smoke, -0.02f, 1500, Grad(new Color(0.85f, 0.84f, 0.82f, 0.6f), new Color(0.8f, 0.8f, 0.8f, 0f)), grow: 4f);

        for (int i = 0; i < _flashes.Length; i++)
        {
            var f = new GameObject("MuzzleFlash");
            f.transform.SetParent(transform, false);
            f.AddComponent<MeshFilter>().sharedMesh = _quad;
            var fr = f.AddComponent<MeshRenderer>();
            fr.sharedMaterial = a.flash;
            fr.shadowCastingMode = ShadowCastingMode.Off;
            var fl = f.AddComponent<Light>();
            fl.type = LightType.Point;
            fl.color = new Color(1f, 0.75f, 0.4f);
            fl.range = 5f;
            fl.intensity = 3f;
            fl.shadows = LightShadows.None;
            f.SetActive(false);
            _flashes[i] = f.transform;
        }

        var b = new GameObject("ExplosionLight");
        b.transform.SetParent(transform, false);
        _boomLight = b.AddComponent<Light>();
        _boomLight.type = LightType.Point;
        _boomLight.color = new Color(1f, 0.6f, 0.25f);
        _boomLight.range = 18f;
        _boomLight.shadows = LightShadows.None;
        _boomLight.enabled = false;
    }

    static Mesh MakeQuad()
    {
        var m = new Mesh { name = "fx_quad" };
        m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    static Gradient Grad(Color a, Color b)
    {
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.4f), new GradientAlphaKey(0f, 1f) });
        // tint is carried by the start colour; the gradient only fades
        return g;
    }

    ParticleSystem MakeSystem(string name, Material mat, float gravity, int max, Gradient fade, bool stretch = false, float grow = 1f)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = max;
        main.gravityModifier = gravity;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = false;
        var col = ps.colorOverLifetime; col.enabled = true; col.color = fade;
        var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f / grow, 1f, 1f));
        if (grow < 1f) sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, grow));
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.03f; r.lengthScale = 1.2f; }
        ps.Play();
        return ps;
    }

    static void Burst(ParticleSystem ps, Vector3 pos, Vector3 normal, int n, float spread, Vector2 speed, Vector2 size, Vector2 life, Color color, float jitter = 0f)
    {
        var ep = new ParticleSystem.EmitParams();
        for (int i = 0; i < n; i++)
        {
            var dir = Vector3.Slerp(normal, Random.onUnitSphere, spread);
            if (Vector3.Dot(dir, normal) < 0f) dir = Vector3.Reflect(dir, normal);
            ep.position = pos + Random.insideUnitSphere * jitter;
            ep.velocity = dir.normalized * Random.Range(speed.x, speed.y);
            ep.startSize = Random.Range(size.x, size.y);
            ep.startLifetime = Random.Range(life.x, life.y);
            ep.startColor = color;
            ep.rotation = Random.Range(0f, 360f);
            ep.angularVelocity = Random.Range(-90f, 90f);
            ps.Emit(ep, 1);
        }
    }

    public void Tracer(Vector3 from, Vector3 to, float width = 0.03f)
    {
        LineRenderer lr = null;
        for (int i = 0; i < _tracers.Count; i++)
            if (!_tracers[i].enabled) { lr = _tracers[i]; _tracerT[i] = 0f; break; }
        if (lr == null)
        {
            var go = new GameObject("Tracer");
            go.transform.SetParent(transform, false);
            lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = GameAssets.I.tracer;
            lr.positionCount = 2;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.textureMode = LineTextureMode.Stretch;
            _tracers.Add(lr);
            _tracerT.Add(0f);
        }
        // a short bright streak, not the whole path: from the muzzle up to 18 m towards the hit
        var d = to - from;
        float len = d.magnitude;
        var start = from + d.normalized * Mathf.Min(0.6f, len * 0.5f);
        lr.SetPosition(0, start);
        lr.SetPosition(1, to);
        lr.startWidth = width;
        lr.endWidth = width * 0.6f;
        lr.startColor = new Color(1f, 0.9f, 0.6f, 0.9f);
        lr.endColor = new Color(1f, 0.95f, 0.8f, 0.6f);
        lr.enabled = true;
    }

    public void MuzzleFlash(Transform muzzle, float size)
    {
        if (muzzle == null) return;
        int i = _flashNext; _flashNext = (_flashNext + 1) % _flashes.Length;
        var fl = _flashes[i];
        fl.gameObject.SetActive(true);
        fl.position = muzzle.position + muzzle.forward * size * 0.35f;
        var cam = Camera.main;
        fl.rotation = Quaternion.LookRotation(cam ? fl.position - cam.transform.position : muzzle.forward) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
        fl.localScale = Vector3.one * size * Random.Range(0.8f, 1.2f);
        _flashUntil[i] = Time.time + 0.045f;
        Burst(_spark, muzzle.position, muzzle.forward, 4, 0.25f, new Vector2(6f, 12f), new Vector2(0.015f, 0.03f), new Vector2(0.05f, 0.12f), new Color(1f, 0.8f, 0.4f));
    }

    /// bullet impact on the world: sparks on hard surfaces, dust on soft ones, and a bullet hole
    public void Impact(RaycastHit hit, bool hole = true)
    {
        bool soft = hit.collider is TerrainCollider;
        var n = hit.normal;
        if (soft)
            Burst(_dust, hit.point, n, 6, 0.35f, new Vector2(0.4f, 1.6f), new Vector2(0.18f, 0.35f), new Vector2(0.4f, 0.9f), new Color(0.72f, 0.64f, 0.5f, 0.75f));
        else
        {
            Burst(_spark, hit.point, n, 10, 0.5f, new Vector2(3f, 9f), new Vector2(0.02f, 0.04f), new Vector2(0.12f, 0.3f), new Color(1f, 0.8f, 0.45f));
            Burst(_dust, hit.point, n, 3, 0.3f, new Vector2(0.3f, 1f), new Vector2(0.12f, 0.22f), new Vector2(0.3f, 0.6f), new Color(0.6f, 0.58f, 0.55f, 0.6f));
        }
        if (hole) Decal(hit.point, n, Random.Range(0.07f, 0.1f), hit.collider.transform);
    }

    /// hit on a character: a bright puff, no gore (Fortnite style)
    public void HitPuff(Vector3 p, Vector3 dir, bool head)
    {
        Burst(_puff, p, -dir, head ? 12 : 7, 0.6f, new Vector2(1f, 3.5f), new Vector2(0.08f, 0.18f), new Vector2(0.15f, 0.35f),
              head ? new Color(1f, 0.9f, 0.35f, 1f) : new Color(0.75f, 0.9f, 1f, 1f));
    }

    public void Decal(Vector3 p, Vector3 n, float size, Transform parent)
    {
        var t = _decals[_decalNext];
        if (t == null)
        {
            var go = new GameObject("BulletHole");
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = GameAssets.I.decal;
            r.shadowCastingMode = ShadowCastingMode.Off;
            t = _decals[_decalNext] = go.transform;
        }
        _decalNext = (_decalNext + 1) % _decals.Length;
        t.SetParent(parent != null && parent.lossyScale.x > 0.01f ? parent : transform, true);
        t.position = p + n * 0.012f;
        t.rotation = Quaternion.LookRotation(-n) * Quaternion.Euler(0, 0, Random.Range(0f, 360f));
        var ps = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(size / Mathf.Max(0.01f, ps.x), size / Mathf.Max(0.01f, ps.y), 1f / Mathf.Max(0.01f, ps.z));
        t.gameObject.SetActive(true);
    }

    /// chips, dust or sparks from a hit or a break, by material
    public void Debris(Vector3 p, Vector3 n, BuildMat m, bool big)
    {
        var chip = m == BuildMat.Wood ? new Color(0.62f, 0.42f, 0.24f, 0.9f) : m == BuildMat.Brick ? new Color(0.62f, 0.6f, 0.58f, 0.9f) : new Color(0.7f, 0.74f, 0.8f, 0.9f);
        int k = big ? 4 : 1;
        Burst(_dust, p, n, 6 * k, 0.6f, new Vector2(1f, 4f) * (big ? 1.5f : 1f), new Vector2(0.2f, 0.5f) * (big ? 2.5f : 1f), new Vector2(0.4f, 1.1f), chip, big ? 1f : 0.1f);
        if (m == BuildMat.Metal) Burst(_spark, p, n, 10 * k, 0.6f, new Vector2(3f, 9f), new Vector2(0.02f, 0.04f), new Vector2(0.15f, 0.35f), new Color(1f, 0.85f, 0.5f));
        else Burst(_spark, p, n, 6 * k, 0.7f, new Vector2(2f, 6f), new Vector2(0.05f, 0.09f), new Vector2(0.3f, 0.7f), chip);
    }

    /// jumping or falling into water: a white splash and a ring of spray
    public void Splash(Vector3 p, float size)
    {
        var foam = new Color(0.92f, 0.96f, 1f, 0.85f);
        Burst(_dust, p, Vector3.up, Mathf.RoundToInt(14 * size), 0.35f, new Vector2(2f, 6f) * size, new Vector2(0.35f, 0.8f) * size, new Vector2(0.5f, 1.0f), foam, 0.4f * size);
        Burst(_puff, p, Vector3.up, Mathf.RoundToInt(10 * size), 1f, new Vector2(1.5f, 3.5f), new Vector2(0.25f, 0.5f), new Vector2(0.4f, 0.8f), foam, 0.6f * size);
    }

    /// a supply drop's coloured smoke trail
    public void Smoke(Vector3 p, Color c) => Burst(_smoke, p, Vector3.up, 1, 0.3f, new Vector2(0.3f, 1f), new Vector2(0.9f, 1.7f), new Vector2(2.2f, 3.6f), c, 0.15f);

    /// swimming: a little foam at the surface now and then
    public void Wake(Vector3 p)
    {
        Burst(_puff, p, Vector3.up, 2, 0.8f, new Vector2(0.2f, 0.6f), new Vector2(0.2f, 0.4f), new Vector2(0.5f, 0.9f), new Color(0.92f, 0.96f, 1f, 0.6f), 0.3f);
    }

    /// a build piece breaking: chunks of it tumble out and fall (on layer 2, so nothing aims at them)
    public void Chunks(Bounds b, Material mat, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Chunk";
            go.layer = 2;
            var p = b.center + Vector3.Scale(Random.insideUnitSphere * 0.5f, b.size);
            go.transform.SetPositionAndRotation(p, Random.rotation);
            go.transform.localScale = new Vector3(Random.Range(0.3f, 0.85f), Random.Range(0.12f, 0.4f), Random.Range(0.3f, 0.85f));
            var r = go.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 2f;
            rb.linearVelocity = (p - b.center).normalized * Random.Range(1.5f, 4f) + Vector3.up * Random.Range(1f, 4f);
            rb.angularVelocity = Random.insideUnitSphere * 7f;
            Destroy(go, Random.Range(1.4f, 2.2f));
        }
    }

    /// a new build piece appearing: a blue shimmer over its shape
    public void BuildPop(Vector3 c, Vector3 size)
    {
        var ep = new ParticleSystem.EmitParams();
        for (int i = 0; i < 26; i++)
        {
            ep.position = c + Vector3.Scale(new Vector3(Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f)), size);
            ep.velocity = Vector3.up * Random.Range(0.3f, 1.2f);
            ep.startSize = Random.Range(0.15f, 0.35f);
            ep.startLifetime = Random.Range(0.3f, 0.6f);
            ep.startColor = new Color(0.4f, 0.75f, 1f, 1f);
            _puff.Emit(ep, 1);
        }
    }

    public void Trail(Vector3 p, Vector3 back)
    {
        Burst(_trail, p, back, 2, 0.2f, new Vector2(0.2f, 0.8f), new Vector2(0.25f, 0.45f), new Vector2(0.9f, 1.6f), new Color(0.9f, 0.88f, 0.85f, 0.55f), 0.05f);
        Burst(_fire, p, back, 1, 0.1f, new Vector2(1f, 3f), new Vector2(0.15f, 0.25f), new Vector2(0.05f, 0.1f), new Color(1f, 0.7f, 0.3f));
    }

    public void Explosion(Vector3 p, float radius)
    {
        float s = radius / 5f;
        Burst(_fire, p, Vector3.up, 40, 1f, new Vector2(2f, 9f) * s, new Vector2(1.2f, 2.6f) * s, new Vector2(0.25f, 0.6f), new Color(1f, 0.75f, 0.4f), 0.5f * s);
        Burst(_smoke, p + Vector3.up * 0.5f, Vector3.up, 34, 0.9f, new Vector2(1f, 4.5f) * s, new Vector2(1.6f, 3.2f) * s, new Vector2(1.6f, 3.4f), new Color(0.3f, 0.29f, 0.28f, 0.8f), 0.8f * s);
        Burst(_spark, p, Vector3.up, 60, 1f, new Vector2(8f, 24f) * s, new Vector2(0.04f, 0.08f), new Vector2(0.3f, 0.9f), new Color(1f, 0.75f, 0.35f));
        Burst(_dust, p, Vector3.up, 20, 0.7f, new Vector2(2f, 6f) * s, new Vector2(1f, 2f) * s, new Vector2(0.8f, 1.6f), new Color(0.6f, 0.55f, 0.45f, 0.7f), 0.5f * s);
        if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var g, 2.5f, GameLayers.Env, QueryTriggerInteraction.Ignore))
            Decal(g.point, g.normal, radius * 0.7f, null);
        _boomLight.transform.position = p + Vector3.up * 1f;
        _boomT = 0f;
        _boomLight.enabled = true;
    }

    void LateUpdate()
    {
        for (int i = 0; i < _flashes.Length; i++)
            if (_flashes[i].gameObject.activeSelf && Time.time > _flashUntil[i]) _flashes[i].gameObject.SetActive(false);
        for (int i = 0; i < _tracers.Count; i++)
        {
            if (!_tracers[i].enabled) continue;
            _tracerT[i] += Time.deltaTime;
            float k = 1f - _tracerT[i] / TracerLife;
            if (k <= 0f) { _tracers[i].enabled = false; continue; }
            _tracers[i].startColor = new Color(1f, 0.9f, 0.6f, 0.9f * k);
            _tracers[i].endColor = new Color(1f, 0.95f, 0.8f, 0.6f * k);
        }
        if (_boomLight.enabled)
        {
            _boomT += Time.deltaTime;
            _boomLight.intensity = 8f * Mathf.Exp(-_boomT * 9f);
            if (_boomT > 0.5f) _boomLight.enabled = false;
        }
    }
}
