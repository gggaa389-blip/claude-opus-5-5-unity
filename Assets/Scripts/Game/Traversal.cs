using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// Fortnite traversal (fifth step): launch pads throw you up into a glide, zip lines carry you along a
/// cable, rifts send you into the sky, and supply drops float down under a canopy with better loot.
/// The level gets its pads, zip lines and rifts at the start of a match (places found with ray and
/// height tests); pads and Rift-To-Go are also items.
public static class Traversal
{
    public const float PadLaunch = 36f, RiftHeight = 110f;
    public const float ZipHeight = 4.2f;   // pole tops: the cable is in reach from the ground
    public static int PadLaunches, BotLaunches, RiftTrips, DropsLanded, DropsOpened;   // for the demo logs

    public static void Launch(Combatant c, Vector3 vel)
    {
        PadLaunches++;
        if (!c.isPlayer) BotLaunches++;
        var pc = c.GetComponent<PlayerController>();
        if (pc != null) { pc.Launch(vel); return; }
        var bc = c.GetComponent<BotController>();
        if (bc != null) bc.Launch(vel);
    }

    public static void ToSky(Combatant c)
    {
        RiftTrips++;
        var pc = c.GetComponent<PlayerController>();
        if (pc != null) { pc.ToSky(RiftHeight); return; }
        var bc = c.GetComponent<BotController>();
        if (bc != null) bc.ToSky(RiftHeight);
    }

    public static float Ground(Vector3 p)
    {
        var t = Terrain.activeTerrain;
        return t != null ? t.SampleHeight(p) + t.transform.position.y : 0f;
    }

    public static bool Dry(Vector3 p) => !Water.Surface(p, out var wy) || wy < Ground(p) + 0.1f;

    /// nothing but terrain in a box of this half size standing on the ground at p
    public static bool Free(Vector3 p, float half, float height)
    {
        float g = Ground(p);
        return !Physics.OverlapBox(new Vector3(p.x, g + 0.3f + height * 0.5f, p.z), new Vector3(half, height * 0.5f, half), Quaternion.identity, GameLayers.Env, QueryTriggerInteraction.Ignore)
            .Any(c => !(c is TerrainCollider));
    }

    public static bool Flat(Vector3 p, float r)
    {
        float g = Ground(p);
        foreach (var o in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
            if (Mathf.Abs(Ground(p + o * r) - g) > 0.5f) return false;
        return true;
    }

    /// the level's launch pads (open flat ground near houses and warehouses), rifts (hilltops) and zip
    /// lines (down from hilltops and across the river); returns what was placed for the log
    public static string SetupLevel(System.Random rng)
    {
        // launch pads: by the houses and warehouses, at least 50 m apart
        var pads = new List<Vector3>();
        var spots = Destructible.Harvestables.Where(d => d != null && (d.name == "front_door" || d.name == "roller_door")).Select(d => d.transform.position).ToList();
        foreach (var s in spots.OrderBy(_ => rng.Next()))
        {
            if (pads.Count >= 5) break;
            for (int k = 0; k < 12; k++)
            {
                float a = k * 30f * Mathf.Deg2Rad;
                var p = s + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 12f;
                if (!Dry(p) || !Flat(p, 2f) || !Free(p, 2f, 12f) || pads.Any(q => Vector3.Distance(q, p) < 50f)) continue;
                p.y = Ground(p);
                pads.Add(p);
                break;
            }
        }
        foreach (var p in pads) LaunchPad.Place(p, rng.Next(360));

        // hilltops: the highest open ground, far apart
        var hills = new List<Vector3>();
        for (float x = -200f; x <= 200f; x += 8f)
            for (float z = -180f; z <= 200f; z += 8f)
            {
                var p = new Vector3(x, 0f, z);
                float g = Ground(p);
                if (g < 10f || !Dry(p)) continue;
                bool top = true;
                foreach (var o in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                    if (Ground(p + o * 8f) > g) top = false;
                if (top) hills.Add(new Vector3(x, g, z));
            }
        hills = hills.OrderByDescending(h => h.y).ToList();
        var rifts = new List<Vector3>();
        foreach (var h in hills)
        {
            if (rifts.Count >= 2) break;
            if (rifts.Any(r => Vector3.Distance(r, h) < 120f) || !Free(h, 3f, 6f)) continue;
            rifts.Add(h);
            Rift.Open(h + Vector3.up * 2.4f);
        }

        // zip lines: from a hilltop down 60 to 100 m, the cable clear of everything; and across the river
        var zips = new List<(Vector3, Vector3)>();
        foreach (var h in hills)
        {
            if (zips.Count >= 3) break;
            if (zips.Any(z => Vector3.Distance(z.Item1, h) < 80f) || rifts.Any(r => Vector3.Distance(r, h) < 25f) || !Free(h, 1.5f, 8f)) continue;
            for (int k = 0; k < 16; k++)
            {
                float a = k * 22.5f * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                bool done = false;
                for (float len = 100f; len >= 50f && !done; len -= 10f)
                {
                    var e = h + dir * len;
                    float ge = Ground(e);
                    if (h.y - ge < 8f || !Dry(e) || !Free(e, 1.5f, 8f)) continue;
                    var top0 = h + Vector3.up * ZipHeight;
                    var top1 = new Vector3(e.x, ge + ZipHeight, e.z);
                    if (!ZipLine.CableClear(top0, top1)) continue;
                    zips.Add((h, new Vector3(e.x, ge, e.z)));
                    done = true;
                }
                if (done) break;
            }
        }
        foreach (var (a, b) in zips) ZipLine.Build(a, b, ZipHeight);
        var river = RiverZip();
        if (river.HasValue) ZipLine.Build(river.Value.Item1, river.Value.Item2, ZipHeight);
        return $"{pads.Count} launch pads, {rifts.Count} rifts, {zips.Count + (river.HasValue ? 1 : 0)} zip lines";
    }

    /// a zip line over the river: two dry banks 40 to 70 m apart with water in between
    static (Vector3, Vector3)? RiverZip()
    {
        for (float z = -120f; z <= 160f; z += 6f)
            for (float x = -40f; x <= 100f; x += 4f)
            {
                var p = new Vector3(x, 0f, z);
                if (!Water.Surface(p, out var wy) || wy < 0.8f || wy - Ground(p) < 1.5f) continue;
                foreach (var d in new[] { Vector3.right, new Vector3(1f, 0f, 1f).normalized, new Vector3(1f, 0f, -1f).normalized })
                    for (float half = 20f; half <= 35f; half += 5f)
                    {
                        var a = p - d * half; var b = p + d * half;
                        if (!Dry(a) || !Dry(b) || !Free(a, 1.5f, 9f) || !Free(b, 1.5f, 9f)) continue;
                        a.y = Ground(a); b.y = Ground(b);
                        if (Mathf.Abs(a.y - b.y) > 3f) continue;
                        if (!ZipLine.CableClear(a + Vector3.up * ZipHeight, b + Vector3.up * ZipHeight)) continue;
                        return (a, b);
                    }
            }
        return null;
    }
}

/// A launch pad: whoever steps on it goes straight up and then glides (cooldown per person).
public class LaunchPad : MonoBehaviour
{
    public static readonly List<LaunchPad> All = new List<LaunchPad>();
    readonly Dictionary<Combatant, float> _cool = new Dictionary<Combatant, float>();

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public static LaunchPad Place(Vector3 at, float yaw)
    {
        var go = Instantiate(GameAssets.I.launchPad, at, Quaternion.Euler(0f, yaw, 0f));
        go.name = "LaunchPad";
        var col = go.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, 0.09f, 0f);
        col.size = new Vector3(2.5f, 0.18f, 2.5f);
        var p = go.AddComponent<LaunchPad>();
        Fx.I?.BuildPop(at + Vector3.up * 0.2f, new Vector3(2.8f, 0.3f, 2.8f));
        return p;
    }

    /// the Launch Pad item: on the ground ahead of whoever uses it
    public static void PlaceAhead(Combatant who)
    {
        var p = who.transform.position + who.transform.forward * 2.6f;
        if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var g, 5f, GameLayers.Env, QueryTriggerInteraction.Ignore)) p = g.point;
        Place(p, who.transform.eulerAngles.y);
        Sfx.I?.PlayAt("build", p, 0.7f);
    }

    void Update()
    {
        var c0 = transform.position;
        foreach (var c in Combatant.All)
        {
            if (!c.Alive || !c.inMatch) continue;
            var d = c.transform.position - c0;
            if (new Vector2(d.x, d.z).magnitude > 1.3f || d.y < -0.4f || d.y > 0.8f) continue;
            if (_cool.TryGetValue(c, out var t) && Time.time < t) continue;
            _cool[c] = Time.time + 1.5f;
            Traversal.Launch(c, Vector3.up * Traversal.PadLaunch + c.transform.forward * 3f);
            Fx.I?.Splash(c0 + Vector3.up * 0.3f, 0.6f);
            Sfx.I?.PlayAt("launch", c0, 0.9f);
        }
    }
}

/// A rift: a swirling purple orb; whoever touches it reappears high in the sky to skydive and glide.
/// Rift-To-Go opens a short-lived one right where it is used.
public class Rift : MonoBehaviour
{
    public static readonly List<Rift> All = new List<Rift>();
    Transform _a, _b;
    float _until = -1f, _born;
    readonly Dictionary<Combatant, float> _cool = new Dictionary<Combatant, float>();

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public static Rift Open(Vector3 at, float life = -1f)
    {
        var go = new GameObject("Rift");
        go.transform.position = at;
        var r = go.AddComponent<Rift>();
        r._a = Orb(go.transform, 3.4f);
        r._b = Orb(go.transform, 2.5f);
        var l = go.AddComponent<Light>();
        l.color = new Color(0.65f, 0.4f, 1f);
        l.range = 14f;
        l.intensity = 2.2f;
        r._until = life > 0f ? Time.time + life : -1f;
        r._born = Time.time;
        Sfx.I?.PlayAt("rift", at, 0.9f);
        return r;
    }

    static Transform Orb(Transform parent, float size)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(g.GetComponent<Collider>());
        g.layer = 2;
        g.transform.SetParent(parent, false);
        g.transform.localScale = Vector3.one * size;
        var rr = g.GetComponent<Renderer>();
        rr.sharedMaterial = GameAssets.I.riftMat;
        rr.material.SetColor("_TintColor", new Color(0.8f, 0.5f, 1f, 0.85f));   // stronger than the asset's
        rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return g.transform;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        _a.Rotate(20f * dt, 110f * dt, 0f, Space.Self);
        _b.Rotate(-35f * dt, -150f * dt, 0f, Space.Self);
        float grow = Mathf.Clamp01((Time.time - _born) / 0.4f);
        float shrink = _until > 0f ? Mathf.Clamp01((_until - Time.time) / 0.4f) : 1f;
        transform.localScale = Vector3.one * Mathf.Min(grow, shrink) * (1f + 0.05f * Mathf.Sin(Time.time * 5f));
        if (_until > 0f && Time.time > _until) { Destroy(gameObject); return; }
        foreach (var c in Combatant.All)
        {
            if (!c.Alive || !c.inMatch || Vector3.Distance(c.Chest, transform.position) > 2f) continue;
            if (_cool.TryGetValue(c, out var t) && Time.time < t) continue;
            _cool[c] = Time.time + 2f;
            Fx.I?.Splash(c.Chest, 0.8f);
            Traversal.ToSky(c);
        }
    }
}

/// A zip line: a sagging cable between two pole tops; E rides it, the way the camera faces.
public class ZipLine : MonoBehaviour
{
    public static readonly List<ZipLine> All = new List<ZipLine>();
    public Vector3 a, b;
    public float sag;

    public float Length => Vector3.Distance(a, b);
    public Vector3 Point(float t) => Vector3.Lerp(a, b, t) - Vector3.up * sag * 4f * t * (1f - t);

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    static List<Bounds> _canopies;

    /// the cable, sagging 3 % of its length, touches nothing, tree canopies included (they have no
    /// colliders), with room for someone hanging 2.1 m below it
    public static bool CableClear(Vector3 a, Vector3 b)
    {
        float sag = Vector3.Distance(a, b) * 0.03f;
        Vector3 P(float t) => Vector3.Lerp(a, b, t) - Vector3.up * sag * 4f * t * (1f - t);
        for (int i = 0; i < 20; i++)
            if (Physics.Linecast(P(i / 20f) + (i == 0 ? (b - a).normalized * 0.5f : Vector3.zero), P((i + 1) / 20f), GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;
        if (_canopies == null)
            _canopies = Destructible.Harvestables.Where(d => d != null && d.name.StartsWith("tree_"))
                .Select(d => { var rs = d.GetComponentsInChildren<Renderer>(); var bb = rs[0].bounds; foreach (var r in rs) bb.Encapsulate(r.bounds); return bb; }).ToList();
        var flat = new Vector2(b.x - a.x, b.z - a.z);
        float len2 = Mathf.Max(0.01f, flat.sqrMagnitude);
        foreach (var c in _canopies)
        {
            float t = Mathf.Clamp01(Vector2.Dot(new Vector2(c.center.x - a.x, c.center.z - a.z), flat) / len2);
            var q = P(t);
            if (new Vector2(c.center.x - q.x, c.center.z - q.z).magnitude > Mathf.Max(c.extents.x, c.extents.z) * 0.75f + 0.5f) continue;   // canopies are round, their bounds square
            if (q.y - 2.4f < c.max.y + 0.3f && q.y > c.min.y) return false;
        }
        return true;
    }

    public static ZipLine Build(Vector3 groundA, Vector3 groundB, float height)
    {
        var go = new GameObject("ZipLine");
        var z = go.AddComponent<ZipLine>();
        z.a = groundA + Vector3.up * height;
        z.b = groundB + Vector3.up * height;
        z.sag = z.Length * 0.03f;
        var A = GameAssets.I;
        foreach (var g in new[] { groundA, groundB })
        {
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "ZipPole";
            pole.transform.SetParent(go.transform, false);
            pole.transform.position = g + Vector3.up * (height * 0.5f - 0.3f);
            pole.transform.localScale = new Vector3(0.24f, height * 0.5f + 0.3f, 0.24f);
            pole.GetComponent<Renderer>().sharedMaterial = A.zipPoleMat;
            var cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(cap.GetComponent<Collider>());
            cap.transform.SetParent(go.transform, false);
            cap.transform.position = g + Vector3.up * (height + 0.05f);
            cap.transform.localScale = new Vector3(0.5f, 0.2f, 0.5f);
            cap.GetComponent<Renderer>().sharedMaterial = A.zipCapMat;
        }
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = A.zipCableMat;
        lr.widthMultiplier = 0.06f;
        lr.positionCount = 32;
        for (int i = 0; i < 32; i++) lr.SetPosition(i, z.Point(i / 31f));
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return z;
    }

    /// the point of the cable nearest p (as 0..1 along it) and how far it is
    public float Closest(Vector3 p, out float dist)
    {
        float best = 0f; dist = float.MaxValue;
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f, d = Vector3.Distance(Point(t), p);
            if (d < dist) { dist = d; best = t; }
        }
        return best;
    }
}

/// Supply drops: one floats down at the start of each of the first three storm phases, somewhere on dry
/// land inside the next safe circle; the scripted video can call one in anywhere.
public class SupplyDrops : MonoBehaviour
{
    int _lastPhase;
    System.Random _rng;

    public void Init(int seed) => _rng = new System.Random(seed * 13 + 5);

    void Update()
    {
        var m = Match.I;
        if (m == null || _rng == null) return;
        var st = m.storm;
        if (st.phase == _lastPhase) return;
        _lastPhase = st.phase;
        if (st.phase < 1 || st.phase > 3) return;
        for (int k = 0; k < 30; k++)
        {
            float a = (float)_rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Sqrt((float)_rng.NextDouble()) * st.nextRadius * 0.8f;
            var p = new Vector3(st.nextCenter.x + Mathf.Cos(a) * r, 0f, st.nextCenter.y + Mathf.Sin(a) * r);
            if (Traversal.Ground(p) < 2f || (Water.Surface(p, out var wy) && wy > Traversal.Ground(p))) continue;
            SupplyDrop.Spawn(p, 140f);
            break;
        }
    }
}

public class SupplyDrop : MonoBehaviour
{
    public static readonly List<SupplyDrop> All = new List<SupplyDrop>();
    public const float FallSpeed = 7f;
    public bool landed;
    Vector3 _ground;
    float _smokeAt, _phase;
    GameObject _model;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public static SupplyDrop Spawn(Vector3 target, float height)
    {
        var g = target;
        if (Physics.Raycast(new Vector3(target.x, 400f, target.z), Vector3.down, out var h, 800f, GameLayers.Env, QueryTriggerInteraction.Ignore)) g = h.point;
        var go = new GameObject("SupplyDrop");
        go.transform.position = g + Vector3.up * height;
        var d = go.AddComponent<SupplyDrop>();
        d._ground = g;
        d._phase = Random.value * 6f;
        d._model = Instantiate(GameAssets.I.supplyFalling, go.transform, false);
        foreach (var c in d._model.GetComponentsInChildren<Collider>(true)) Destroy(c);
        Debug.Log($"[Game] supply drop on its way to {g}");
        return d;
    }

    void Update()
    {
        if (landed) return;
        float dt = Time.deltaTime;
        var p = transform.position;
        p.y = Mathf.Max(_ground.y, p.y - FallSpeed * dt);
        transform.position = p;
        _model.transform.localRotation = Quaternion.Euler(Mathf.Sin(Time.time * 0.9f + _phase) * 4f, Time.time * 12f, Mathf.Cos(Time.time * 0.7f + _phase) * 4f);
        if (Time.time > _smokeAt)
        {
            _smokeAt = Time.time + 0.08f;
            Fx.I?.Smoke(p + Vector3.up * 0.3f, new Color(0.45f, 0.75f, 1f, 0.75f));
        }
        if (p.y <= _ground.y + 0.01f) Land();
    }

    void Land()
    {
        landed = true;
        Traversal.DropsLanded++;
        Destroy(_model);
        var m = Instantiate(GameAssets.I.supplyLanded, transform.position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform);
        if (m.GetComponentInChildren<Collider>() == null) m.AddComponent<BoxCollider>();
        var lc = m.AddComponent<LootContainer>();
        lc.supply = true;
        Fx.I?.Debris(transform.position + Vector3.up * 0.5f, Vector3.up, BuildMat.Metal, true);
        Sfx.I?.PlayAt("break", transform.position, 0.6f);
    }
}
