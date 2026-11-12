using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// The building video (-buildDemo -demoOut <dir>): the player, driven only through input, pickaxes an
/// oak (hitting the blue weak points), a rock and something metal, rushes three ramps up in wood, brick
/// and metal, puts a floor on top, walls on three sides and a cone over it, runs back down and brings
/// the whole thing down with one rocket at the foot of the first ramp (nothing else holds it up).
public class BuildShowcase : Showcase
{
    // chosen by PickSpot before the player is spawned
    static Destructible _tree, _rock, _metal;
    static Vector3 _buildCell;
    static float _buildYaw;

    const float G = BuildRules.Grid;

    // ------------------------------------------------------------------ choosing the place

    static bool OnLand(Destructible d)
    {
        var c = d.Center;
        float g = Ground(c);
        return g > 2.5f && Mathf.Abs(c.y - g) < 3f;
    }

    /// an oak, a rock and a metal part close together on open land, with a 4 m wide strip from a cell next
    /// to the metal part that is free of everything for 20 m both ways (the ramps, then the rocket shot)
    public static bool PickSpot(out Vector3 start, out float yaw)
    {
        start = default; yaw = 0f;
        var all = Destructible.Harvestables.Where(d => d != null && d.Alive && OnLand(d)).ToList();
        var trees = all.Where(d => d.name == "tree_oak").ToList();
        var rocks = all.Where(d => d.name.StartsWith("rock_0")).ToList();
        var metals = all.Where(d => d.mat == BuildMat.Metal).ToList();
        if (trees.Count == 0 || rocks.Count == 0 || metals.Count == 0) return false;
        Destructible Near(List<Destructible> l, Vector3 p) => l.OrderBy(d => Vector3.Distance(d.Center, p)).First();
        var cands = rocks.Select(r => (r, t: Near(trees, r.Center), m: Near(metals, r.Center)))
            .Select(c => (c.r, c.t, c.m, score: Vector3.Distance(c.r.Center, c.t.Center) + Vector3.Distance(c.r.Center, c.m.Center)))
            .Where(c => c.score < 50f).OrderBy(c => c.score).Take(40);
        foreach (var (r, t, m, score) in cands)
        {
            var ignore = new HashSet<Destructible> { r, t, m };
            var mc = m.Center;
            int mx = Mathf.FloorToInt(mc.x / G), mz = Mathf.FloorToInt(mc.z / G);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var cell = new Vector3((mx + dx + 0.5f) * G, 0f, (mz + dz + 0.5f) * G);
                    for (int k = 0; k < 4; k++)
                        if (Strip(cell, k * 90f, ignore))
                        {
                            _tree = t; _rock = r; _metal = m;
                            _buildCell = new Vector3(cell.x, Ground(cell), cell.z);
                            _buildYaw = k * 90f;
                            var tc = t.Center;
                            var away = r.Center - tc; away.y = 0f;
                            start = tc + away.normalized * 4.5f;
                            start.y = Ground(start) + 0.1f;
                            yaw = Mathf.Atan2(-away.x, -away.z) * Mathf.Rad2Deg;
                            Debug.Log($"[BuildDemo] {t.name} {r.name} {m.name} (path {score:0} m), build cell {_buildCell} facing {_buildYaw}");
                            return true;
                        }
                }
        }
        return false;
    }

    static bool Strip(Vector3 cell, float yaw, HashSet<Destructible> ignore)
    {
        var f = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        f = new Vector3(Mathf.Round(f.x), 0f, Mathf.Round(f.z));
        float g0 = Ground(cell);
        if (g0 < 2.5f) return false;
        for (int i = -5; i <= 5; i++)
        {
            var c = cell + f * G * i;
            float g = Ground(c);
            if (g > g0 + 0.9f || g < g0 - 3f || g < 1.5f) return false;
            float top = i >= 0 ? 17f : 4f;
            foreach (var col in Physics.OverlapBox(new Vector3(c.x, g0 + 0.8f + top * 0.5f, c.z), new Vector3(G * 0.55f, top * 0.5f, G * 0.55f), Quaternion.identity, GameLayers.Env, QueryTriggerInteraction.Ignore))
            {
                if (col is TerrainCollider) continue;
                var d = col.GetComponentInParent<Destructible>();
                if (d == null || !ignore.Contains(d)) return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ the moves

    /// in pickaxe reach of the part: the chest ray towards it hits it
    bool InReach(Destructible d)
    {
        var o = pc.me.Chest;
        var to = d.Center - o;
        return Physics.Raycast(o, to.normalized, out var h, 2f, GameLayers.Env, QueryTriggerInteraction.Ignore) && h.collider.GetComponentInParent<Destructible>() == d;
    }

    /// pickaxe a level part, aiming at the blue weak point after each hit
    IEnumerator Harvest(Destructible d, int maxHits)
    {
        if (d == null || !d.Alive) yield break;
        var c = d.Center;
        _look = new Vector3(c.x, Mathf.Min(c.y, Feet.y + 1.25f), c.z);
        float until = Time.time + 7f;
        while (Time.time < until && !InReach(d))
        {
            var to = c - Feet; to.y = 0f;
            var local = Quaternion.Inverse(Quaternion.Euler(0f, pc.cam.yaw, 0f)) * to.normalized;
            _move = new Vector2(local.x, local.z);
            yield return null;
        }
        _move = Vector2.zero;
        yield return Wait(0.3f);
        for (int i = 0; i < maxHits && d != null && d.Alive; i++)
        {
            if (d.lastHitBy == pc.me && Time.time < d.weakUntil) _look = d.weakPoint;
            yield return Wait(0.3f);
            _tap = true;
            yield return Wait(0.62f);
        }
        yield return Wait(0.4f);
    }

    void LookAlong(float yaw, float up = 1.2f) => _look = Feet + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * 40f + Vector3.up * up;

    /// switch to a piece, pick the material with RMB, click
    IEnumerator Place(int piece, int mat)
    {
        _build = piece;
        yield return null;
        yield return null;
        for (int k = 0; k < 3 && (int)pc.buildMat != mat && pc.me.inv.mats[mat] >= BuildRules.Cost; k++) { _aimTap = true; yield return null; yield return null; }
        yield return Wait(0.35f);
        _tap = true;
        yield return null;
        yield return Wait(0.4f);
    }

    /// up the ramp just placed, stopping short of its top edge
    IEnumerator WalkUp(float baseY)
    {
        float until = Time.time + 3f;
        _move = new Vector2(0f, 1f);
        while (Time.time < until && Feet.y < baseY + 3.25f) yield return null;
        _move = Vector2.zero;
        yield return Wait(0.15f);
    }

    protected override IEnumerator Script()
    {
        yield return Wait(1.2f);
        _slot = 0;
        if (_tree == null) { yield return Finish("[BuildDemo] no spot"); yield break; }

        // 1. materials: wood from the oak (weak points), brick from the rock, metal last
        yield return Harvest(_tree, 12);
        yield return Harvest(_rock, 6);
        yield return Harvest(_metal, 5);
        Debug.Log($"[BuildDemo] harvested wood {pc.me.inv.mats[0]}, brick {pc.me.inv.mats[1]}, metal {pc.me.inv.mats[2]} at {Time.time:0.0}s");

        // 2. to the build cell, facing along the free strip
        var fwd = Quaternion.Euler(0f, _buildYaw, 0f) * Vector3.forward;
        _look = _buildCell + Vector3.up * 1.3f;
        yield return WalkTo(_buildCell, 0.35f, 8f);
        _look = _buildCell + fwd * 40f + Vector3.up * 2f;
        yield return Wait(0.9f);

        // 3. ramp rush: wood, brick, metal
        var rampFoot = Vector3.zero;
        for (int i = 0; i < 3; i++)
        {
            yield return Place((int)PieceType.Stairs, i);
            float baseY = pc.buildSlot.baseY;
            if (i == 0) rampFoot = pc.buildSlot.pos;
            _look = Feet + fwd * 40f + Vector3.up * 3f;
            yield return WalkUp(baseY);
        }

        // 4. a floor at the top, walls ahead and to the sides, a cone over it
        _look = Feet + fwd * 40f;
        yield return Place((int)PieceType.Floor, 0);
        var floorAt = pc.buildSlot.pos;
        yield return WalkTo(floorAt, 0.4f, 3f);
        LookAlong(_buildYaw, 0f);
        yield return Wait(0.4f);
        yield return Place((int)PieceType.Wall, 1);
        LookAlong(_buildYaw - 90f, 0f);
        yield return Wait(0.6f);
        yield return Place((int)PieceType.Wall, 0);
        LookAlong(_buildYaw + 90f, 0f);
        yield return Wait(0.9f);
        yield return Place((int)PieceType.Wall, 0);
        _look = Feet + fwd * 10f + Vector3.up * 7.5f;
        yield return Wait(0.9f);
        yield return Place((int)PieceType.Cone, 0);
        _look = Feet - fwd * 40f - Vector3.up * 4f;
        yield return Wait(1.6f);

        // 5. back down the ramps and off to the side (to see the stairs from the side), then one rocket
        // at the foot of the first ramp
        _slot = 1;
        var below = rampFoot - fwd * G;
        _look = below - fwd * 30f + Vector3.up * 1.4f;
        yield return WalkTo(below, 1.2f, 8f);
        var shootFrom = ShootSpot(rampFoot, fwd);
        _look = shootFrom + (shootFrom - below).normalized * 30f + Vector3.up * 1.4f;
        yield return WalkTo(shootFrom, 0.8f, 8f);
        _slot = 2;
        _look = rampFoot + Vector3.up * 0.8f;
        yield return Wait(1.5f);
        _aim = true;
        yield return Wait(0.9f);
        _tap = true;
        yield return Wait(0.4f);
        _aim = false;
        _lookRate = 2.5f;
        _look = rampFoot + fwd * 8f + Vector3.up * 7f;
        yield return Wait(4.5f);
        yield return Finish($"[BuildDemo] {Frames} frames, wood {pc.me.inv.mats[0]}, brick {pc.me.inv.mats[1]}, metal {pc.me.inv.mats[2]}, pieces standing {Destructible.Built.Count}");
    }

    /// to the side and back of the stairs with a clear line to their foot; straight back if nothing else is
    static Vector3 ShootSpot(Vector3 foot, Vector3 fwd)
    {
        var right = Vector3.Cross(Vector3.up, fwd);
        float g0 = Ground(foot);
        foreach (var p in new[] { foot - fwd * 13f + right * 15f, foot - fwd * 13f - right * 15f })
        {
            float g = Ground(p);
            if (Mathf.Abs(g - g0) > 1.5f) continue;
            var eye = new Vector3(p.x, g + 1.7f, p.z);
            var aim = foot + Vector3.up * 0.8f;
            if (Physics.Raycast(eye, (aim - eye).normalized, out var h, Vector3.Distance(eye, aim) + 2f, GameLayers.Env, QueryTriggerInteraction.Ignore)
                && Vector3.Distance(h.point, aim) > 3f && !(h.collider.GetComponentInParent<Destructible>()?.built ?? false)) continue;
            // nothing in the way of walking there from the foot of the stairs
            var from = foot - fwd * G + Vector3.up * 1.2f;
            var to = new Vector3(p.x, g + 1.2f, p.z);
            if (Physics.SphereCast(from, 0.45f, (to - from).normalized, out _, Vector3.Distance(from, to), GameLayers.Env, QueryTriggerInteraction.Ignore)) continue;
            return new Vector3(p.x, g, p.z);
        }
        return foot - fwd * 22f;
    }
}
