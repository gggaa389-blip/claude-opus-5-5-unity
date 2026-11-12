using System;
using System.Collections.Generic;
using UnityEngine;

/// Skydiving and gliding shared by the player and the bots (Fortnite style: free fall, then a glider
/// that opens on its own near the ground).
public static class Flight
{
    public const float FallSpeed = 22f, DiveSpeed = 32f, SkySteer = 13f, GlideFall = 5.5f, GlideSpeed = 11f, DeployHeight = 42f;

    /// height above whatever is below (ground, roofs)
    public static float Height(Vector3 p)
    {
        return Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var h, 600f, GameLayers.Env, QueryTriggerInteraction.Ignore) ? h.distance - 0.5f : 999f;
    }

    /// velocity for this frame from a wanted horizontal direction (world, length 0..1)
    public static Vector3 Step(Vector3 vel, Vector3 wish, bool gliding, bool dive, float dt)
    {
        float speed = gliding ? GlideSpeed : SkySteer;
        var h = Vector3.Lerp(new Vector3(vel.x, 0f, vel.z), wish * speed, 1f - Mathf.Exp(-(gliding ? 2.2f : 1.6f) * dt));
        float vy = gliding ? -GlideFall : -(dive ? DiveSpeed : FallSpeed);
        float y = Mathf.Lerp(vel.y, vy, 1f - Mathf.Exp(-3f * dt));
        return new Vector3(h.x, y, h.z);
    }
}

/// The storm: a safe circle that shrinks in phases towards a random point on land; outside it you lose
/// health (not shield) every second, more in later phases.
[Serializable]
public class Storm
{
    struct Phase { public float wait, shrink, radius, dps; }
    readonly Phase[] _phases;
    public Vector2 center, nextCenter;
    public float radius = 420f, nextRadius = 420f;
    public int phase;               // 0 before the first circle is shown
    public bool shrinking;
    public float firstAt;
    float _t0, _fromR;
    Vector2 _fromC;
    readonly System.Random _rng;
    readonly Func<Vector2, bool> _onLand;

    public Storm(float scale, int seed, Func<Vector2, bool> onLand)
    {
        _rng = new System.Random(seed);
        _onLand = onLand;
        firstAt = 52f * scale;
        _phases = new[]
        {
            new Phase { wait = 30f * scale, shrink = 30f * scale, radius = 150f, dps = 1f },
            new Phase { wait = 25f * scale, shrink = 25f * scale, radius = 80f, dps = 2f },
            new Phase { wait = 20f * scale, shrink = 20f * scale, radius = 40f, dps = 5f },
            new Phase { wait = 15f * scale, shrink = 15f * scale, radius = 16f, dps = 8f },
            new Phase { wait = 10f * scale, shrink = 25f * scale, radius = 0f, dps = 10f },
        };
        center = nextCenter = new Vector2(0f, 10f);
    }

    public float Dps => phase <= 0 ? 0f : _phases[Mathf.Min(phase, _phases.Length) - 1].dps;
    public bool Inside(Vector3 p) => new Vector2(p.x, p.z).Distance(center) <= radius;
    public bool InsideNext(Vector3 p, float margin = 0f) => new Vector2(p.x, p.z).Distance(nextCenter) <= nextRadius - margin;
    public bool Final => phase >= _phases.Length && !shrinking && radius <= 0.01f;

    /// what the HUD says and the seconds to the next change
    public string Status(float now, out float seconds)
    {
        if (phase == 0) { seconds = firstAt - now; return "STORM EYE FORMING"; }
        if (phase > _phases.Length) { seconds = 0f; return ""; }
        var p = _phases[phase - 1];
        if (shrinking) { seconds = _t0 + p.shrink - now; return "STORM EYE SHRINKING"; }
        seconds = _t0 + p.wait - now;
        return "STORM EYE SHRINKS IN";
    }

    public void Update(float now)
    {
        if (phase == 0)
        {
            if (now < firstAt) return;
            StartPhase(1, now);
            return;
        }
        if (phase > _phases.Length) return;
        var p = _phases[phase - 1];
        if (!shrinking)
        {
            if (now >= _t0 + p.wait) { shrinking = true; _t0 = now; _fromC = center; _fromR = radius; }
            return;
        }
        float k = Mathf.Clamp01((now - _t0) / Mathf.Max(0.01f, p.shrink));
        center = Vector2.Lerp(_fromC, nextCenter, k);
        radius = Mathf.Lerp(_fromR, nextRadius, k);
        if (k >= 1f)
        {
            shrinking = false;
            if (phase < _phases.Length) StartPhase(phase + 1, now);
            else phase = _phases.Length + 1;
        }
    }

    void StartPhase(int n, float now)
    {
        phase = n;
        shrinking = false;
        _t0 = now;
        var p = _phases[n - 1];
        // next circle: inside the current one, its centre on land
        float room = Mathf.Max(0f, Mathf.Min(radius, n == 1 ? 140f : radius) - p.radius);
        for (int k = 0; k < 60; k++)
        {
            float a = (float)_rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Sqrt((float)_rng.NextDouble()) * room;
            var c = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            if (_onLand == null || _onLand(c) || k == 59) { nextCenter = c; break; }
        }
        nextRadius = p.radius;
    }
}

static class V2Ext
{
    public static float Distance(this Vector2 a, Vector2 b) => Vector2.Distance(a, b);
}

/// One battle royale match: the bus (the test 1 drop vehicle, a bus under a hot-air balloon) crosses the
/// island on a random line, everybody drops from it, the storm closes in, the last one standing wins.
public class Match : MonoBehaviour
{
    public static Match I;
    public enum Phase { Bus, Playing, Ended }

    /// the bus model in the level: hangs over the island until a match flies it along the route
    public const string BusObject = "DropVehicle_80mAboveIsland";

    public Phase phase = Phase.Bus;
    public float startTime;
    public Transform busModel;
    public Vector3 busA, busB;
    public float busTime = 40f, busAlt = 150f;
    public Storm storm;
    public Combatant player;
    public Combatant winner;
    public float endTime = -1f;
    public readonly List<Combatant> combatants = new List<Combatant>();
    float _tick;
    StormWall _wall;
    Terrain _terrain;

    public float Now => Time.time - startTime;
    public float BusProgress => Mathf.Clamp01(Now / busTime);
    public Vector3 BusPos => Vector3.Lerp(busA, busB, BusProgress);
    public Vector3 BusDir => (busB - busA).normalized;
    public Vector3 BusVelocity => phase == Phase.Bus ? (busB - busA) / busTime : Vector3.zero;
    public bool DoorsOpen => Now > 2.5f;
    public int Alive { get { int n = 0; foreach (var c in combatants) if (c.Alive) n++; return n; } }

    public void Begin(int seed, float stormScale, Transform bus)
    {
        I = this;
        startTime = Time.time;
        busModel = bus;
        _terrain = Terrain.activeTerrain;
        var rng = new System.Random(seed);
        float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
        var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
        var perp = new Vector3(-dir.z, 0f, dir.x);
        var c = new Vector3(0f, busAlt, 10f) + perp * ((float)rng.NextDouble() * 120f - 60f);
        busA = c - dir * 300f;
        busB = c + dir * 300f;
        storm = new Storm(stormScale, seed, OnLand);
        _wall = StormWall.Create(GameAssets.I.stormMat);
    }

    bool OnLand(Vector2 p) => _terrain != null && _terrain.SampleHeight(new Vector3(p.x, 0f, p.y)) + _terrain.transform.position.y > 2f;

    void Update()
    {
        if (busModel != null)
        {
            // the bus keeps flying past the end of the route and leaves
            float t = Now / busTime;
            busModel.position = Vector3.LerpUnclamped(busA, busB, t) + Vector3.up * 6f;
            busModel.rotation = Quaternion.LookRotation(BusDir);
            if (t > 1.6f) { busModel.gameObject.SetActive(false); busModel = null; }
        }
        if (phase == Phase.Bus && Now >= busTime) phase = Phase.Playing;
        storm.Update(Now);
        _wall.Set(storm.center, storm.radius);

        // storm damage once a second to everyone outside the eye
        _tick += Time.deltaTime;
        if (_tick >= 1f)
        {
            _tick -= 1f;
            if (storm.Dps > 0f)
                foreach (var c in combatants)
                    if (c.Alive && c.inMatch && !storm.Inside(c.transform.position))
                        c.TakeDamage(new DamageEvent { amount = storm.Dps, storm = true, point = c.Chest });
        }
    }

    public void Register(Combatant c)
    {
        combatants.Add(c);
        c.Died += OnDied;
    }

    void OnDied(Combatant c, DamageEvent e)
    {
        c.placement = Alive + 1;
        if (phase == Phase.Ended) return;
        if (Alive <= 1 || (c == player))
        {
            if (Alive <= 1)
            {
                foreach (var o in combatants) if (o.Alive) { winner = o; o.placement = 1; }
                phase = Phase.Ended;
                endTime = Time.time;
            }
        }
    }

    public bool PlayerWon => winner != null && winner == player;
    public bool PlayerOut => player != null && !player.Alive;
}

/// The storm wall: a tall open cylinder on the edge of the eye with an animated purple shader.
public class StormWall : MonoBehaviour
{
    Transform _t;

    public static StormWall Create(Material mat)
    {
        var go = new GameObject("StormWall");
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = Cylinder(128);
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var w = go.AddComponent<StormWall>();
        w._t = go.transform;
        return w;
    }

    public void Set(Vector2 c, float r)
    {
        _t.position = new Vector3(c.x, -40f, c.y);
        _t.localScale = new Vector3(Mathf.Max(0.05f, r), 420f, Mathf.Max(0.05f, r));
        gameObject.SetActive(r < 400f);
    }

    static Mesh Cylinder(int seg)
    {
        var v = new List<Vector3>(); var uv = new List<Vector2>(); var n = new List<Vector3>(); var tri = new List<int>();
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v.Add(d); v.Add(d + Vector3.up);
            uv.Add(new Vector2((float)i / seg, 0f)); uv.Add(new Vector2((float)i / seg, 1f));
            n.Add(d); n.Add(d);
            if (i == seg) break;
            int b = i * 2;
            tri.AddRange(new[] { b, b + 1, b + 2, b + 1, b + 3, b + 2 });
        }
        var m = new Mesh { name = "storm_wall" };
        m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(n); m.SetTriangles(tri, 0);
        m.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(2.2f, 1.2f, 2.2f));
        return m;
    }
}
