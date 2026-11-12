using System.Collections;
using System.Linq;
using UnityEngine;

/// The movement video (-moveDemo -demoOut <dir>): three places the game finds in the level, with a cut
/// between them. A sprint down an open hillside into a slide that speeds up downhill, then a
/// crouch-walk and a few crouched shots; a run at a stack of crates and a jump that mantles onto it,
/// then a hop down the far side; a run and a jump into the river, swimming across (treading water
/// halfway to look around) and walking out on the far bank. The player is driven only through input.
public class MoveShowcase : Showcase
{
    static Vector3 _hillTop, _hillDir, _ledgeFrom, _ledgeDir, _riverFrom, _riverDir, _riverMid, _riverOut;
    static Destructible _ledge;
    static bool _hasHill, _hasLedge, _hasRiver;

    // ------------------------------------------------------------------ choosing the places

    static readonly Vector3[] Dirs = Enumerable.Range(0, 8).Select(k => Quaternion.Euler(0f, k * 45f, 0f) * Vector3.forward).ToArray();

    /// nothing at body height along a path that follows the ground
    static bool Clear(Vector3 a, Vector3 b, float radius = 0.45f)
    {
        int n = Mathf.CeilToInt(Vector3.Distance(a, b) / 4f);
        var prev = new Vector3(a.x, Ground(a) + 1.1f, a.z);
        for (int i = 1; i <= n; i++)
        {
            var q = Vector3.Lerp(a, b, i / (float)n);
            q.y = Ground(q) + 1.1f;
            if (Physics.SphereCast(prev, radius, (q - prev).normalized, out _, Vector3.Distance(prev, q), GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;
            prev = q;
        }
        return true;
    }

    static bool Dry(Vector3 p) => !Water.Surface(p, out var wy) || wy < Ground(p) + 0.1f;

    /// open grass falling 6 m or more over 32 m after a 9 m run-up, smooth enough to slide down
    static void FindHill()
    {
        float best = 0f;
        for (float x = -200f; x <= 200f; x += 6f)
            for (float z = -180f; z <= 200f; z += 6f)
            {
                var p = new Vector3(x, 0f, z);
                float g0 = Ground(p);
                if (g0 < 4f || !Dry(p)) continue;
                foreach (var d in Dirs)
                {
                    float prev = g0, drop = 0f, lastStep = -1f;
                    bool ok = true;
                    for (int i = 1; i <= 10 && ok; i++)
                    {
                        var q = p + d * (4f * i);
                        float g = Ground(q);
                        if (!Dry(q) || g < 1.5f) ok = false;
                        float step = prev - g;
                        // a flat run-up, then a smooth grassy fall (5 to 22 degrees, no bumps or cliffs)
                        if (i <= 2 ? Mathf.Abs(step) > 1.0f : step < 0.35f || step > 1.6f || (lastStep >= 0f && Mathf.Abs(step - lastStep) > 0.5f)) ok = false;
                        if (i > 2) { drop += step; lastStep = step; }
                        prev = g;
                    }
                    if (!ok || drop < 6f || drop <= best) continue;
                    if (!Clear(p, p + d * 52f, 0.6f) || !Dry(p + d * 52f)) continue;
                    best = drop; _hillTop = p; _hillDir = d; _hasHill = true;
                }
            }
        if (_hasHill) { _hillTop.y = Ground(_hillTop); Debug.Log($"[MoveDemo] hill at {_hillTop} falling {best:0.0} m towards {_hillDir}"); }
    }

    /// a stack of crates (or another flat-topped part) 1.2 to 2.2 m high with a clear 10 m run-up
    static void FindLedge()
    {
        float best = float.MaxValue;
        foreach (var d in Destructible.Harvestables)
        {
            if (d == null || !d.Alive) continue;
            var r = d.GetComponentInChildren<Renderer>();
            if (r == null) continue;
            var b = r.bounds;
            if (b.size.x < 0.8f || b.size.z < 0.8f || b.size.x > 4f || b.size.z > 4f) continue;
            float h = b.max.y - Ground(b.center);
            if (h < 1.2f || h > 2.2f) continue;
            if (!Physics.Raycast(new Vector3(b.center.x, b.max.y + 1f, b.center.z), Vector3.down, out var top, 2f, GameLayers.Env, QueryTriggerInteraction.Ignore) || top.normal.y < 0.85f) continue;
            // standing free: nothing else around it above its top
            bool Own(Collider c)
            {
                var o = c.GetComponentInParent<Destructible>();
                return o == d || (o != null && o.name == d.name && new Vector2(o.transform.position.x - d.transform.position.x, o.transform.position.z - d.transform.position.z).magnitude < 0.6f);
            }
            if (Physics.OverlapSphere(new Vector3(b.center.x, b.max.y + 1.3f, b.center.z), 3.6f, GameLayers.Env, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider) && !Own(c))) continue;
            bool stacked = Destructible.Harvestables.Any(o => o != d && o != null && o.name == d.name && (o.transform.position - d.transform.position).y < -0.4f
                                                             && new Vector2(o.transform.position.x - d.transform.position.x, o.transform.position.z - d.transform.position.z).magnitude < 0.6f);
            foreach (var dir in Dirs.Where((_, k) => k % 2 == 0))
            {
                var from = b.center - dir * (Mathf.Max(b.extents.x, b.extents.z) + 9f);
                from.y = Ground(from);
                if (!Dry(from) || Mathf.Abs(from.y - Ground(b.center)) > 0.6f) continue;
                if (!Clear(from, b.center - dir * (Mathf.Max(b.extents.x, b.extents.z) + 0.8f))) continue;
                if (!Clear(b.center + dir * (Mathf.Max(b.extents.x, b.extents.z) + 0.5f), b.center + dir * (Mathf.Max(b.extents.x, b.extents.z) + 5f))) continue;
                float score = Mathf.Abs(h - 1.7f) + (stacked ? 0f : 0.5f);
                if (score >= best) continue;
                best = score; _ledge = d; _ledgeFrom = from; _ledgeDir = dir; _hasLedge = true;
            }
        }
        if (_hasLedge) Debug.Log($"[MoveDemo] ledge: {_ledge.name} at {_ledge.Center}, approached along {_ledgeDir}");
    }

    /// the river 10 to 26 m wide with deep water in the middle, a clear dry run-up and a clear far bank
    static void FindRiver()
    {
        float best = float.MaxValue;
        for (float x = -120f; x <= 160f; x += 4f)
            for (float z = -160f; z <= 190f; z += 4f)
            {
                var p = new Vector3(x, 0f, z);
                if (!Water.Surface(p, out var wy) || wy < 0.8f || wy - Ground(p) < 1.9f) continue;   // inland water only, deep here
                foreach (var d in Dirs.Take(4))
                {
                    float Edge(Vector3 dir) { for (float s = 1f; s < 20f; s += 0.5f) { var q = p + dir * s; if (!Water.Surface(q, out var y) || y - Ground(q) < 0.25f) return s; } return 99f; }
                    float a = Edge(-d), b = Edge(d);
                    if (a + b < 10f || a + b > 26f) continue;
                    var from = p - d * (a + 9f);
                    var outAt = p + d * (b + 5f);
                    if (!Dry(from) || !Dry(outAt) || Ground(from) - wy > 3f || Ground(outAt) - wy > 3f) continue;
                    if (!Clear(from, p - d * (a - 0.5f)) || !Clear(p + d * (b - 1f), outAt)) continue;
                    float score = Mathf.Abs(a + b - 16f) + Mathf.Abs(a - b) * 0.5f;
                    if (score >= best) continue;
                    best = score; _riverFrom = from; _riverDir = d; _riverMid = new Vector3(p.x, wy, p.z); _riverOut = outAt; _hasRiver = true;
                }
            }
        if (_hasRiver) Debug.Log($"[MoveDemo] river crossing at {_riverMid} along {_riverDir}");
    }

    public static bool PickStart(out Vector3 pos, out float yaw)
    {
        FindHill(); FindLedge(); FindRiver();
        pos = _hasHill ? _hillTop : _hasLedge ? _ledgeFrom : _riverFrom;
        var d = _hasHill ? _hillDir : _hasLedge ? _ledgeDir : _riverDir;
        yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        pos.y = Ground(pos) + 0.1f;
        return _hasHill || _hasLedge || _hasRiver;
    }

    // ------------------------------------------------------------------ the script

    protected override IEnumerator Script()
    {
        yield return Wait(0.8f);
        _slot = 1;

        // 1. sprint down the hill into a slide, crouch-walk on, a few crouched shots
        if (_hasHill)
        {
            var d = _hillDir;
            _look = _hillTop + d * 40f + Vector3.up * (Ground(_hillTop + d * 40f) - _hillTop.y + 1.5f);
            yield return Wait(0.8f);
            _sprint = true;
            _move = new Vector2(0f, 1f);
            yield return Wait(1.5f);
            _crouch = true;
            yield return null;
            _sprint = false;                    // sprinting would stand the slide's crouch back up
            Debug.Log($"[MoveDemo] slide {(pc.moves.sliding ? "started" : "did not start")} at {new Vector2(pc.velocity.x, pc.velocity.z).magnitude:0.0} m/s");
            yield return Until(() => !pc.moves.sliding, 2.4f);
            if (pc.moves.sliding)
            {
                _crouch = true;                 // out of the slide (it stands up) ...
                yield return Wait(0.25f);
                _crouch = true;                 // ... and down into a crouch
                yield return null;
            }
            yield return Wait(1.3f);           // crouch-walking
            _move = Vector2.zero;
            _lookRate = 4f;
            _look = Feet + d * 25f + Vector3.up * 1.2f;
            yield return Wait(0.6f);
            _aim = true;
            yield return Wait(0.6f);
            for (int i = 0; i < 4; i++) { _tap = true; yield return Wait(0.22f); }
            yield return Wait(0.4f);
            _aim = false;
            _crouch = true;                     // stand up
            _lookRate = 6f;
            yield return Wait(0.8f);
        }

        // 2. run at the crates, jump: mantle up, hop down the far side
        if (_hasLedge)
        {
            TeleportTo(_ledgeFrom, _ledgeDir);
            yield return Wait(0.7f);
            _move = new Vector2(0f, 1f);
            var c = _ledge.Center;
            float reach = Mathf.Max(_ledge.GetComponentInChildren<Renderer>().bounds.extents.x, _ledge.GetComponentInChildren<Renderer>().bounds.extents.z) + 1.1f;
            yield return Until(() => { var t = c - Feet; t.y = 0f; return t.magnitude < reach; }, 5f);
            _jump = true;
            yield return null;
            yield return Until(() => pc.moves.mantling, 0.5f);
            yield return Until(() => !pc.moves.mantling, 2f);
            _move = Vector2.zero;
            yield return Wait(0.9f);
            _move = new Vector2(0f, 1f);
            yield return Wait(0.5f);
            _jump = true;
            yield return Wait(1.0f);
            _move = Vector2.zero;
            yield return Wait(0.6f);
        }

        // 3. into the river, across it, out on the far bank
        if (_hasRiver)
        {
            TeleportTo(_riverFrom, _riverDir);
            yield return Wait(0.7f);
            _sprint = true;
            _move = new Vector2(0f, 1f);
            yield return Until(() => !Dry(Feet + _riverDir * 1.2f), 6f);
            _jump = true;
            yield return Until(() => pc.moves.swimming, 3f);
            _look = _riverMid + _riverDir * 30f + Vector3.up * 1f;
            yield return Until(() => Vector3.Dot(Feet - _riverMid, _riverDir) > -1f, 8f);
            _move = Vector2.zero;
            _sprint = false;
            _lookRate = 2.5f;
            _look = Feet + Quaternion.Euler(0f, 70f, 0f) * _riverDir * 20f + Vector3.up * 1.5f;
            yield return Wait(1.4f);
            _look = Feet + Quaternion.Euler(0f, -50f, 0f) * _riverDir * 20f + Vector3.up * 1.5f;
            yield return Wait(1.2f);
            _lookRate = 6f;
            _look = _riverOut + _riverDir * 25f + Vector3.up * 1.5f;
            yield return Wait(0.5f);
            _move = new Vector2(0f, 1f);
            yield return WalkTo(_riverOut, 1f, 12f);
            yield return Wait(0.8f);
        }
        yield return Finish($"[MoveDemo] {Frames} frames, hill {_hasHill}, ledge {_hasLedge}, river {_hasRiver}");
    }
}
