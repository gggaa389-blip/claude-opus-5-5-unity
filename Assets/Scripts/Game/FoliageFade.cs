using System.Collections.Generic;
using UnityEngine;

/// Bushes and tree crowns have no colliders, so the camera can end up inside one (an elimination next to
/// a bush, a doorway under a tree) and the screen turns into a wall of leaves. Fortnite fades foliage
/// out at the camera; here the plant the camera is in stops drawing until the camera leaves it.
[DefaultExecutionOrder(200)]   // after the third-person camera has placed itself
public class FoliageFade : MonoBehaviour
{
    struct Plant { public Bounds b; public Renderer[] rs; public bool tree; }

    static readonly List<Plant> _plants = new List<Plant>();
    static readonly Dictionary<Vector2Int, List<int>> _grid = new Dictionary<Vector2Int, List<int>>();
    const float Cell = 8f;
    readonly List<int> _hidden = new List<int>(), _now = new List<int>();

    /// the level's bushes and trees, in an 8 m grid (once per match: the scene is loaded anew each time)
    public static void Collect(Transform root)
    {
        _plants.Clear();
        _grid.Clear();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            bool tree = t.name.StartsWith("tree_"), bush = t.name.StartsWith("bush_");
            if (!tree && !bush) continue;
            if (t.parent != null && (t.parent.name.StartsWith("tree_") || t.parent.name.StartsWith("bush_"))) continue;   // the plant's root only
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) continue;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            int i = _plants.Count;
            _plants.Add(new Plant { b = b, rs = rs, tree = tree });
            for (int x = Mathf.FloorToInt(b.min.x / Cell); x <= Mathf.FloorToInt(b.max.x / Cell); x++)
                for (int z = Mathf.FloorToInt(b.min.z / Cell); z <= Mathf.FloorToInt(b.max.z / Cell); z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
                    list.Add(i);
                }
        }
    }

    /// inside a bush, or inside a tree's crown (its upper part, taken as round): not under it by the trunk
    static bool Inside(in Plant p, Vector3 c)
    {
        var b = p.b;
        b.Expand(0.5f);
        if (!b.Contains(c)) return false;
        if (!p.tree) return true;
        if (c.y < p.b.min.y + p.b.size.y * 0.4f) return false;
        return new Vector2(c.x - b.center.x, c.z - b.center.z).magnitude < Mathf.Max(b.extents.x, b.extents.z) * 0.85f;
    }

    void LateUpdate()
    {
        var c = transform.position;
        _now.Clear();
        if (_grid.TryGetValue(new Vector2Int(Mathf.FloorToInt(c.x / Cell), Mathf.FloorToInt(c.z / Cell)), out var list))
            foreach (int i in list) if (Inside(_plants[i], c)) _now.Add(i);
        foreach (int i in _hidden) if (!_now.Contains(i)) Show(i, true);
        foreach (int i in _now) if (!_hidden.Contains(i)) Show(i, false);
        _hidden.Clear();
        _hidden.AddRange(_now);
    }

    void OnDisable()
    {
        foreach (int i in _hidden) Show(i, true);
        _hidden.Clear();
    }

    static void Show(int i, bool on)
    {
        if (i >= _plants.Count) return;
        foreach (var r in _plants[i].rs) if (r) r.forceRenderingOff = !on;
    }
}
