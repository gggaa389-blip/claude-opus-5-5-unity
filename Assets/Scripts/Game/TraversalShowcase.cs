using System.Collections;
using System.Linq;
using UnityEngine;

/// The traversal video (-travDemo -demoOut <dir>), no bots, cuts between places: a supply drop floats
/// down onto open ground ahead of the player, who searches it; the player places a Launch Pad from the
/// inventory, steps on it and glides away; rides a zip line down from a hilltop to its end; walks into
/// a rift and skydives from high above until the glider sets them down. Driven only through input.
public class TraversalShowcase : Showcase
{
    /// somewhere to land: dry, flat, open ground minD to maxD from p (the nearest such ring first), with
    /// land all the way there (a glide over a lake ends in the water)
    static Vector3? OpenSpot(Vector3 p, float minD, float maxD)
    {
        for (float d = minD; d <= maxD; d += 10f)
            for (int k = 0; k < 16; k++)
            {
                float a = k * 22.5f * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var q = p + dir * d;
                if (Traversal.Ground(q) < 2.5f || !Traversal.Dry(q) || !Traversal.Flat(q, 3f) || !Traversal.Free(q, 3f, 8f)) continue;
                bool overLand = true;
                for (float s = 5f; s < d && overLand; s += 5f) { var w = p + dir * s; overLand = Traversal.Dry(w) && Traversal.Ground(w) > 1.5f; }
                if (!overLand) continue;
                q.y = Traversal.Ground(q);
                return q;
            }
        return null;
    }

    /// steer the skydive and the glide to a point, then stop steering above it
    IEnumerator FlyTo(Vector3 target, float maxTime)
    {
        float end = Time.time + maxTime;
        while (pc.mode != PlayerController.Mode.Ground && Time.time < end)
        {
            var to = target - Feet; to.y = 0f;
            _look = Feet + to.normalized * 40f - Vector3.up * (pc.mode == PlayerController.Mode.Skydive ? 50f : 20f);
            _move = to.magnitude > 6f ? new Vector2(0f, 0.9f) : Vector2.zero;
            yield return null;
        }
        _move = Vector2.zero;
    }

    protected override IEnumerator Script()
    {
        var f = pc.transform.forward;
        var start = Feet;
        _slot = 1;
        yield return Wait(0.8f);

        // 1. a supply drop comes down ahead; search it, walk through the loot
        var drop = SupplyDrop.Spawn(start + f * 14f, 46f);
        _lookRate = 3f;
        float until = Time.time + 10f;
        while (!drop.landed && Time.time < until) { _look = drop.transform.position + Vector3.up * 1.5f; yield return null; }
        _lookRate = 6f;
        var at = drop.transform.position;
        _look = at + Vector3.up * 0.6f;
        yield return Wait(0.6f);
        yield return WalkTo(at, 2.1f, 4f);
        _interact = true;
        yield return Wait(1.2f);
        // walk over what spilled out (picked up on the way)
        foreach (var w in WorldItem.All.Where(w => w != null && Vector3.Distance(w.transform.position, at) < 5f).ToList())
        {
            if (w == null) continue;
            _look = w.transform.position + Vector3.up * 0.4f;
            yield return WalkTo(w.transform.position, 0.5f, 2f);
        }
        yield return Wait(0.5f);

        // 2. the Launch Pad from the inventory: put it down ahead, step on, fly and glide
        int pad = System.Array.FindIndex(pc.me.inv.slots, s => s != null && s.consumable != null && s.consumable.action == "pad");
        if (pad >= 0)
        {
            // a few steps away from the crate first, so the pad is in view, facing open ground to glide to
            var land = OpenSpot(at, 45f, 75f) ?? (at - f * 60f);
            Debug.Log($"[TravDemo] launch pad flight to {land}");
            var away = land - at; away.y = 0f; away.Normalize();
            _look = Feet + away * 30f + Vector3.up * 0.5f;
            yield return WalkTo(at + away * 7f, 0.5f, 4f);
            yield return Wait(0.5f);
            _slot = pad + 1;
            yield return Wait(0.5f);
            _tap = true;
            yield return Wait(1.0f);
            _move = new Vector2(0f, 1f);
            yield return Until(() => pc.mode == PlayerController.Mode.Skydive, 3f);
            yield return FlyTo(land, 16f);
            yield return Wait(1.0f);
        }

        // 3. a zip line down from a hilltop, all the way to its end
        var zip = ZipLine.All.OrderByDescending(z => Mathf.Abs(z.a.y - z.b.y)).FirstOrDefault();
        if (zip != null)
        {
            var hi = zip.a.y >= zip.b.y ? zip.a : zip.b;
            var lo = zip.a.y >= zip.b.y ? zip.b : zip.a;
            var along = lo - hi; along.y = 0f; along.Normalize();
            TeleportTo(hi + along * 0.7f, along);   // under the cable, next to the high pole
            _look = lo + Vector3.up * 2f;
            yield return Wait(1.0f);
            _interact = true;
            yield return Until(() => pc.mode == PlayerController.Mode.Zip, 1f);
            _lookRate = 4f;
            _look = lo + along * 20f + Vector3.up * 1.5f;
            yield return Until(() => pc.mode != PlayerController.Mode.Zip, 14f);
            _lookRate = 6f;
            yield return Wait(1.4f);
        }

        // 4. into a rift: up in the sky, skydiving, the glider
        var rift = Rift.All.FirstOrDefault();
        if (rift != null)
        {
            var r = rift.transform.position;
            // glide towards open ground within reach of the glider
            var spot = OpenSpot(r, 50f, 110f);
            var target = spot ?? (r + Vector3.right * 80f);
            Debug.Log($"[TravDemo] rift at {r}, landing at {target} ({(spot.HasValue ? "open ground" : "nothing open found")})");
            var dir = target - r; dir.y = 0f; dir.Normalize();
            TeleportTo(r - dir * 8f, dir);
            _look = r;
            yield return Wait(0.9f);
            yield return WalkTo(r, 0.2f, 4f);
            yield return Until(() => pc.mode == PlayerController.Mode.Skydive, 2f);
            yield return FlyTo(target, 18f);
            _move = Vector2.zero;
            _look = Feet + dir * 30f + Vector3.up * 1.5f;
            yield return Wait(1.4f);
        }
        yield return Finish($"[TravDemo] {Frames} frames, zip lines {ZipLine.All.Count}, rifts {Rift.All.Count}, pads {LaunchPad.All.Count}");
    }
}
