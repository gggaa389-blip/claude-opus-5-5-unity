using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

/// Mixamo crowd: walkers and runners on the roads (A* routes over the road mask, around obstacles and
/// over the bridges), swimmers in the river and two people treading water in the lake.
public static partial class IslandBuilder
{
    const float CrowdCell = 2f;
    const int CrowdN = 300;   // 600 m / 2 m
    static bool[,] _blocked;
    public static readonly List<string> CrowdLog = new List<string>();

    static int CI(float x) => Mathf.Clamp(Mathf.FloorToInt((x + Size / 2f) / CrowdCell), 0, CrowdN - 1);
    static float CX(int i) => -Size / 2f + (i + 0.5f) * CrowdCell;

    static bool OnBridge(Vector2 p, out float deck)
    {
        foreach (var b in new[] { Bridge1, Bridge2 })
        {
            var l = ToLocal(p, b.c, b.yaw);
            if (Mathf.Abs(l.x) < b.width * 0.5f - 0.6f && Mathf.Abs(l.y) < b.length * 0.5f + 1.5f) { deck = b.deckY; return true; }
        }
        deck = 0f;
        return false;
    }

    static bool IsBridgePart(Collider c)
    {
        for (var t = c.transform; t != null; t = t.parent) if (t.name.StartsWith("Bridge_")) return true;
        return false;
    }

    static void BuildObstacleGrid()
    {
        Physics.SyncTransforms();
        _blocked = new bool[CrowdN, CrowdN];
        for (int j = 0; j < CrowdN; j++)
            for (int i = 0; i < CrowdN; i++)
            {
                float x = CX(i), z = CX(j);
                var p = new Vector2(x, z);
                bool bridge = OnBridge(p, out float deck);
                if (!bridge && (CoastSd(x, z) < 1.5f || WaterSd(x, z) < 1.0f || Slope(x, z) > 33f)) { _blocked[i, j] = true; continue; }
                float gy = bridge ? deck : GH(x, z);
                var hits = Physics.OverlapSphere(new Vector3(x, gy + 1.0f, z), 0.7f);
                foreach (var h in hits)
                    if (!(h is TerrainCollider) && !IsBridgePart(h)) { _blocked[i, j] = true; break; }
            }
    }

    /// A* on the 2 m grid; cost(x, z) >= 1 is the price of entering a cell
    static List<Vector2> FindRoute(Vector2 a, Vector2 b, Func<float, float, float> cost)
    {
        int si = CI(a.x), sj = CI(a.y), ti = CI(b.x), tj = CI(b.y);
        // start / goal may sit in a blocked cell: use the nearest free one
        (si, sj) = NearestFree(si, sj); (ti, tj) = NearestFree(ti, tj);
        var g = new float[CrowdN, CrowdN];
        var from = new int[CrowdN, CrowdN];
        var closed = new bool[CrowdN, CrowdN];
        for (int j = 0; j < CrowdN; j++) for (int i = 0; i < CrowdN; i++) { g[i, j] = float.MaxValue; from[i, j] = -1; }
        var open = new SortedSet<(float f, int i, int j)>();
        g[si, sj] = 0f;
        open.Add((Vector2.Distance(new Vector2(si, sj), new Vector2(ti, tj)), si, sj));
        int[] di = { 1, -1, 0, 0, 1, 1, -1, -1 }, dj = { 0, 0, 1, -1, 1, -1, 1, -1 };
        while (open.Count > 0)
        {
            var cur = open.Min; open.Remove(cur);
            if (closed[cur.i, cur.j]) continue;
            closed[cur.i, cur.j] = true;
            if (cur.i == ti && cur.j == tj) break;
            for (int k = 0; k < 8; k++)
            {
                int ni = cur.i + di[k], nj = cur.j + dj[k];
                if (ni < 0 || nj < 0 || ni >= CrowdN || nj >= CrowdN || closed[ni, nj] || _blocked[ni, nj]) continue;
                if (k >= 4 && (_blocked[cur.i + di[k], cur.j] || _blocked[cur.i, cur.j + dj[k]])) continue;   // no corner cutting
                float step = (k < 4 ? 1f : 1.4142f) * cost(CX(ni), CX(nj));
                float ng = g[cur.i, cur.j] + step;
                if (ng < g[ni, nj])
                {
                    g[ni, nj] = ng;
                    from[ni, nj] = cur.i * CrowdN + cur.j;
                    open.Add((ng + Vector2.Distance(new Vector2(ni, nj), new Vector2(ti, tj)), ni, nj));
                }
            }
        }
        if (from[ti, tj] < 0) return null;
        var cells = new List<Vector2>();
        int ci = ti, cj = tj;
        while (true)
        {
            cells.Add(new Vector2(CX(ci), CX(cj)));
            if (ci == si && cj == sj) break;
            int f = from[ci, cj];
            ci = f / CrowdN; cj = f % CrowdN;
        }
        cells.Reverse();
        return cells;
    }

    static (int, int) NearestFree(int i, int j)
    {
        for (int r = 0; r < 20; r++)
            for (int a = -r; a <= r; a++)
                for (int b = -r; b <= r; b++)
                {
                    int x = i + a, y = j + b;
                    if (x >= 0 && y >= 0 && x < CrowdN && y < CrowdN && !_blocked[x, y]) return (x, y);
                }
        return (i, j);
    }

    static List<Vector2> Chaikin(List<Vector2> p, int iterations, bool closed)
    {
        for (int it = 0; it < iterations; it++)
        {
            var q = new List<Vector2>();
            int n = p.Count;
            if (!closed) q.Add(p[0]);
            for (int k = 0; k < (closed ? n : n - 1); k++)
            {
                Vector2 a = p[k], b = p[(k + 1) % n];
                q.Add(Vector2.Lerp(a, b, 0.25f)); q.Add(Vector2.Lerp(a, b, 0.75f));
            }
            if (!closed) q.Add(p[n - 1]);
            p = q;
        }
        return p;
    }

    static List<Vector2> Resample(List<Vector2> p, float step)
    {
        var outp = new List<Vector2> { p[0] };
        float carry = 0f;
        for (int k = 0; k < p.Count - 1; k++)
        {
            Vector2 a = p[k], b = p[k + 1];
            float len = Vector2.Distance(a, b);
            float s = step - carry;
            while (s <= len) { outp.Add(Vector2.Lerp(a, b, s / len)); s += step; }
            carry = len - (s - step);
        }
        return outp;
    }

    /// there-and-back loop: out on the right-hand side of the route, back on the other side
    static List<Vector2> OutAndBack(List<Vector2> route, float side)
    {
        List<Vector2> Offset(List<Vector2> r)
        {
            var o = new List<Vector2>();
            for (int k = 0; k < r.Count; k++)
            {
                Vector2 d = r[Mathf.Min(k + 1, r.Count - 1)] - r[Mathf.Max(k - 1, 0)];
                Vector2 right = new Vector2(d.y, -d.x).normalized;
                o.Add(r[k] + right * side);
            }
            return o;
        }
        var back = new List<Vector2>(route); back.Reverse();
        var loop = Offset(route);
        loop.AddRange(Offset(back));
        return loop;
    }

    /// path points over or next to a bridge are pulled onto the deck (at most 1.3 m off its axis, deck height)
    static Vector3[] Ground(List<Vector2> loop)
    {
        var pts = new Vector3[loop.Count];
        for (int k = 0; k < loop.Count; k++)
        {
            var p = loop[k];
            float y = GH(p.x, p.y);
            foreach (var b in new[] { Bridge1, Bridge2 })
            {
                var l = ToLocal(p, b.c, b.yaw);
                if (Mathf.Abs(l.x) < b.width * 0.5f + 0.8f && Mathf.Abs(l.y) < b.length * 0.5f + 1.0f)
                {
                    l.x = Mathf.Clamp(l.x, -1.3f, 1.3f);
                    p = ToWorld(l, b.c, b.yaw);
                    y = b.deckY;
                }
            }
            pts[k] = new Vector3(p.x, y, p.y);
        }
        return pts;
    }

    static float PathLength(Vector3[] pts)
    {
        float L = 0f;
        for (int k = 0; k < pts.Length; k++) L += Vector3.Distance(pts[k], pts[(k + 1) % pts.Length]);
        return L;
    }

    static int _agentIndex;

    static GameObject SpawnAgent(Transform g, string character, string clip, Vector3[] path, float startFrac, Random rng, string label)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(IslandCharacters.Prefabs[character], g.gameObject.scene);
        go.transform.SetParent(g, false);
        go.name = $"Mixamo_{character}_{label}_{++_agentIndex:00}";
        var agent = go.GetComponent<CrowdAgent>();
        float vary = 0.92f + 0.16f * (float)rng.NextDouble();
        agent.clip = IslandCharacters.ClipAssets[clip];
        agent.clipRate = vary;
        agent.clipPhase = (float)rng.NextDouble() * agent.clip.length;
        agent.speed = IslandCharacters.ClipSpeed[clip] * vary;
        agent.path = path;
        agent.startDistance = path != null ? startFrac * PathLength(path) : 0f;
        agent.Apply(0f);
        Counts["mixamo " + character] = Counts.TryGetValue("mixamo " + character, out var c) ? c + 1 : 1;
        return go;
    }

    static void PlaceCrowd(Transform root)
    {
        var g = Group(root, "Crowd_Mixamo");
        CrowdLog.Clear();
        _agentIndex = 0;
        var rng = new Random(4242);
        BuildObstacleGrid();

        Func<float, float, float> roadCost = (x, z) =>
        {
            if (OnBridge(new Vector2(x, z), out _)) return 1f;
            return 1f + 30f * (1f - Smooth(0.15f, 0.55f, Road(x, z)));
        };
        Func<float, float, float> beachCost = (x, z) => 1f + 30f * (1f - Smooth(0.2f, 0.6f, SandNear(x, z))) + 20f * Smooth(1f, 4f, -CoastSd(x, z) + 6f);

        Vector3[] Route(string label, Vector2 a, Vector2 b, Func<float, float, float> cost, float side = 0.9f)
        {
            var r = FindRoute(a, b, cost);
            if (r == null || r.Count < 4) { CrowdLog.Add($"route {label}: NOT FOUND"); return null; }
            var smooth = Resample(Chaikin(r, 3, false), 1f);
            var loop = OutAndBack(smooth, side);
            var pts = Ground(loop);
            float onRoad = smooth.Count(p => Road(p.x, p.y) > 0.3f || OnBridge(p, out _)) / (float)smooth.Count;
            CrowdLog.Add($"route {label}: {Resample(r, 1f).Count} m one way, {onRoad:P0} on road/bridge");
            return pts;
        }

        // 1. the plaza ring: four walkers, both directions, on the radius where the dirt plaza is
        float bestR = 14f, bestRoad = -1f;
        for (float r = 8f; r <= 24f; r += 0.5f)
        {
            float s = 0f;
            for (int k = 0; k < 72; k++) { float a = k * Mathf.PI * 2f / 72f; s += Road(PlazaC.x + Mathf.Cos(a) * r, PlazaC.y + Mathf.Sin(a) * r); }
            if (s > bestRoad) { bestRoad = s; bestR = r; }
        }
        var ring = new List<Vector2>();
        for (int k = 0; k < 96; k++) { float a = k * Mathf.PI * 2f / 96f; ring.Add(PlazaC + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (bestR - 0.7f)); }
        var ringCcw = Ground(ring);
        var ringCw = Ground(Enumerable.Reverse(ring).Select(p => PlazaC + (p - PlazaC) * ((bestR + 0.7f) / (bestR - 0.7f))).ToList());
        SpawnAgent(g, "YBot", "Walk_Male", ringCcw, 0.0f, rng, "walk");
        SpawnAgent(g, "Kate", "Walk_Female", ringCcw, 0.5f, rng, "walk");
        SpawnAgent(g, "Remy", "Walk_Male", ringCw, 0.25f, rng, "walk");
        SpawnAgent(g, "Kate", "Walk_Female", ringCw, 0.75f, rng, "walk");
        CrowdLog.Add($"plaza ring: radius {bestR - 0.7f:F1} m and {bestR + 0.7f:F1} m (dirt plaza, mean road mask {bestRoad / 72f:F2}), 4 walkers");

        // 2. walking routes along the roads
        var north = Route("plaza -> north bridge -> farmhouse", new Vector2(8f, 26f), new Vector2(114.6f, 124f), roadCost);
        if (north != null) { SpawnAgent(g, "Remy", "Walk_Male", north, 0.1f, rng, "walk"); SpawnAgent(g, "Kate", "Walk_Female", north, 0.6f, rng, "walk"); }
        var yard = Route("plaza -> industrial yard", new Vector2(-14f, -2f), new Vector2(-82f, -68f), roadCost);
        if (yard != null) { SpawnAgent(g, "YBot", "Walk_Male", yard, 0.3f, rng, "walk"); SpawnAgent(g, "Remy", "Walk_Male", yard, 0.8f, rng, "walk"); }
        var farmBeach = Route("farm -> east beach", new Vector2(132f, 100f), new Vector2(182f, 92f), roadCost);
        if (farmBeach != null) SpawnAgent(g, "Kate", "Walk_Female", farmBeach, 0.2f, rng, "walk");

        // 3. joggers and runners on the longer routes
        var southEast = Route("plaza -> south bridge -> SE hill", new Vector2(10f, -10f), new Vector2(130f, -52f), roadCost);
        if (southEast != null) { SpawnAgent(g, "YBot", "Jog", southEast, 0.15f, rng, "jog"); SpawnAgent(g, "Kate", "Jog", southEast, 0.65f, rng, "jog"); }
        var cliff = Route("industrial yard -> south cliff tower", new Vector2(-60f, -100f), new Vector2(-6f, -140f), roadCost);
        if (cliff != null) SpawnAgent(g, "Remy", "Jog", cliff, 0.4f, rng, "jog");
        var nw = Route("plaza -> north-west tower", new Vector2(-10f, 25f), new Vector2(-88f, 158f), roadCost);
        if (nw != null) { SpawnAgent(g, "Remy", "Run", nw, 0.05f, rng, "run"); SpawnAgent(g, "YBot", "Run", nw, 0.55f, rng, "run"); }
        var beach = Route("east beach (sand)", new Vector2(178f, 125f), new Vector2(168f, -110f), beachCost, 1.5f);
        if (beach != null) SpawnAgent(g, "Kate", "Run", beach, 0.3f, rng, "run");

        // 3b. armed: every test 1 weapon carried by one walker and one runner, with the weapon locomotion
        // clips (rifle set for the long guns and the rocket launcher, pistol set, axe set for the
        // pickaxe, the plain walk / run for the grenade)
        var armed = Group(root, "Crowd_Mixamo_Armed");
        var routes = new[] { north, yard, southEast, cliff, nw, beach, farmBeach }.Where(r => r != null).ToArray();
        var armory = new (string weapon, string walk, string run)[]
        {
            ("assault_rifle", "Rifle_Walk", "Rifle_Run"), ("shotgun", "Rifle_Walk", "Rifle_Run"),
            ("sniper_rifle", "Rifle_Walk", "Rifle_Run"), ("rocket_launcher", "Rifle_Walk", "Rifle_Run"),
            ("pistol", "Pistol_Walk", "Pistol_Run"), ("pickaxe", "Axe_Walk", "Axe_Run"), ("grenade", "Walk_Male", "Run"),
        };
        string[] cast = { "Remy", "YBot", "Kate" };
        int armedCount = 0;
        foreach (var (weapon, walk, run) in armory)
            foreach (var (clip, mode) in new[] { (walk, "walk"), (run, "run") })
            {
                string ch = cast[armedCount % cast.Length];
                string use = clip == "Walk_Male" && ch == "Kate" ? "Walk_Female" : clip;
                var route = routes[armedCount % routes.Length];
                var a = SpawnAgent(armed, ch, use, route, (armedCount * 0.37f + 0.11f) % 1f, rng, $"{weapon}_{mode}");
                IslandWeapons.Attach(a, weapon, IslandCharacters.ClipAssets[use], Prefabs[weapon]);
                a.GetComponent<CrowdAgent>().Apply(0f);
                armedCount++;
            }
        CrowdLog.Add($"armed: {armedCount} characters, every test 1 weapon carried walking and running");

        // 4. swimmers: down the river on one side, back up on the other
        var centre = new List<Vector2>();
        float cx = 25f;
        for (float z = 118f; z >= -96f; z -= 2f)
        {
            float s = 0f; int n = 0;
            for (float x = cx - 30f; x <= cx + 30f; x += 0.5f) if (WaterSd(x, z) < -0.5f) { s += x; n++; }
            if (n == 0) continue;
            cx = s / n;
            centre.Add(new Vector2(cx, z));
        }
        var river = OutAndBack(Resample(Chaikin(centre, 3, false), 1f), 1.6f);
        var swimH = IslandCharacters.ClipHeights["Swim"];
        float swimDrop = swimH.x - 0.02f;   // mean head height just at the surface
        var swimPath = river.Select(p => new Vector3(p.x, WaterLevel(p.y) - swimDrop, p.y)).ToArray();
        foreach (var (ch, frac) in new[] { ("YBot", 0.05f), ("Remy", 0.38f), ("Kate", 0.7f) })
        {
            var sw = SpawnAgent(g, ch, "Swim", swimPath, frac, rng, "swim");
            sw.GetComponent<CrowdAgent>().bob = 0.03f;
        }
        CrowdLog.Add($"river swim loop: {PathLength(swimPath):F0} m, 3 swimmers, root {swimDrop:F2} m below the surface");

        // 5. treading water in the lake
        var treadH = IslandCharacters.ClipHeights["TreadWater"];
        foreach (var (ch, p, yaw) in new[] { ("Kate", new Vector2(-12f, 150f), 160f), ("YBot", new Vector2(3f, 141f), 300f) })
        {
            var tw = SpawnAgent(g, ch, "TreadWater", null, 0f, rng, "tread");
            var a = tw.GetComponent<CrowdAgent>();
            a.speed = 0f;
            a.holdPosition = new Vector3(p.x, WaterLevel(p.y) - (treadH.x - 0.3f), p.y);
            a.holdYaw = yaw;
            a.bob = 0.05f;
            a.Apply(0f);
        }
        CrowdLog.Add("lake: 2 people treading water");
        foreach (var l in CrowdLog) Debug.Log("[IslandBuilder] crowd " + l);
    }
}
