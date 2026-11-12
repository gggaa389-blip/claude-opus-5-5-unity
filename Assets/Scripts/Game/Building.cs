using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// Something that breaks: a built piece, or a part of the level (trees, rocks, crates, fences, house
/// porches and roofs, warehouse roofs and doors, the modular kit fort). The pickaxe harvests building
/// materials from the level's parts; a blue weak point appears after a hit and doubles the next one.
public class Destructible : MonoBehaviour
{
    public static readonly List<Destructible> Built = new List<Destructible>();
    public static readonly List<Destructible> Harvestables = new List<Destructible>();
    public static int LevelBroken, BuiltBroken, PickaxeHits, MatsHarvested;   // for the demo logs

    public BuildMat mat;
    public float maxHp = 100f, hp = 100f;
    public bool harvestable = true;
    public bool built;
    public PieceType piece;
    public string slotKey;
    public float baseY;
    public Combatant owner;
    [System.NonSerialized] public float buildStart, buildEnd, lastHit = -99f, weakUntil = -99f;
    [System.NonSerialized] public Vector3 weakPoint;
    [System.NonSerialized] public Combatant lastHitBy;
    [System.NonSerialized] public Bounds bounds;
    [System.NonSerialized] public bool doomed;   // lost its support, breaks in a moment
    [System.NonSerialized] public int editMask, meshIdx;     // removed tiles (PieceEdit), the kit piece it is
    [System.NonSerialized] public GameObject model, edited;  // the kit model, and its cut-up copy while edited
    float _damage;   // taken while still building
    bool _broken;

    public bool Alive => hp > 0f && gameObject.activeInHierarchy;
    public bool Building => built && Time.time < buildEnd;

    void OnEnable() { if (built) Built.Add(this); else if (harvestable) Harvestables.Add(this); }
    void OnDisable() { Built.Remove(this); Harvestables.Remove(this); }

    /// Fortnite build-up: hit points climb from 10 % to full while the piece builds
    void Update()
    {
        if (!built || Time.time > buildEnd + 0.1f) return;
        float k = Mathf.Clamp01((Time.time - buildStart) / Mathf.Max(0.01f, buildEnd - buildStart));
        hp = Mathf.Max(1f, Mathf.Lerp(0.1f, 1f, k) * maxHp - _damage);
        transform.localScale = Vector3.one * Mathf.Lerp(0.94f, 1f, Mathf.Clamp01(k * 4f));
    }

    public Vector3 Center => built ? bounds.center : GetComponentInChildren<Renderer>() is Renderer r ? r.bounds.center : transform.position;

    /// damage from a weapon; the pickaxe also yields materials (twice as much on the weak point).
    /// Returns the materials gained.
    public int Hit(float damage, Vector3 point, Vector3 dir, Combatant by, bool pickaxe, Collider col = null)
    {
        if (!Alive) return 0;
        bool crit = pickaxe && Time.time < weakUntil && Vector3.Distance(point, weakPoint) < 0.55f;
        if (crit) damage *= 2f;
        hp -= damage;
        if (Building) _damage += damage;
        lastHit = Time.time;
        lastHitBy = by;
        int gained = 0;
        if (pickaxe && harvestable && by != null)
        {
            gained = BuildRules.PerHit(mat) * (crit ? 2 : 1);
            PickaxeHits++;
            MatsHarvested += gained;
            by.inv.AddMats(mat, gained);
            GameEvents.RaiseHarvest(by, mat, gained, crit);
            // the next weak point: somewhere else on the same surface
            weakPoint = point;
            if (col != null)
            {
                var rot = Quaternion.LookRotation(dir);
                for (int k = 0; k < 6; k++)
                {
                    var off = rot * (Vector3)(Random.insideUnitCircle.normalized * Random.Range(0.35f, 0.7f));
                    if (col.Raycast(new Ray(point + off - dir * 1.2f, dir), out var h, 2.5f)) { weakPoint = h.point - dir * 0.02f; break; }
                }
            }
            weakUntil = Time.time + 6f;
        }
        Fx.I.Debris(point, -dir, mat, false);
        Sfx.I?.PlayAt(pickaxe ? (mat == BuildMat.Wood ? "hit_wood" : mat == BuildMat.Brick ? "hit_stone" : "hit_metal") : "melee_hit", point, pickaxe ? 0.8f : 0.4f);
        if (hp <= 0f) Break();
        return gained;
    }

    public void BreakAfter(float t)
    {
        doomed = true;
        StartCoroutine(BreakLater(t));
    }

    System.Collections.IEnumerator BreakLater(float t)
    {
        yield return new WaitForSeconds(t);
        if (Alive) Break();
    }

    public void Break()
    {
        if (_broken) return;
        _broken = true;
        hp = 0f;
        var c = Center;
        Fx.I.Debris(c, Vector3.up, mat, true);
        Sfx.I?.PlayAt("break", c, 1f, 8f);
        if (built) BuiltBroken++; else LevelBroken++;
        if (built)
        {
            var r = GetComponentInChildren<Renderer>();
            Fx.I.Chunks(bounds, r != null ? r.sharedMaterial : null, piece == PieceType.Floor ? 7 : 10);
            BuildSystem.OnBroken(this);
            Destroy(gameObject);
        }
        else gameObject.SetActive(false);   // static-batched level geometry can be hidden, not moved
    }

    /// level parts that break, by name: material and hit points
    public static int SetupLevel(Transform root)
    {
        int n = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            string nm = t.name;
            BuildMat m; float hp;
            if (nm == "tree_oak") { m = BuildMat.Wood; hp = 400f; }
            else if (nm == "tree_pine") { m = BuildMat.Wood; hp = 300f; }
            else if (nm == "tree_palm") { m = BuildMat.Wood; hp = 220f; }
            else if (nm.StartsWith("rock_0")) { m = BuildMat.Brick; hp = Mathf.Clamp(Size(t) * 120f, 300f, 1100f); }
            else if (nm == "wood_crate") { m = BuildMat.Wood; hp = 100f; }
            else if (nm == "barrel") { m = BuildMat.Metal; hp = 150f; }
            else if (nm == "fence_segment") { m = BuildMat.Wood; hp = 150f; }
            else if (nm == "SupplyDrop_Landed") { m = BuildMat.Metal; hp = 400f; }
            else if (t.parent != null && (t.parent.name.StartsWith("House") || t.parent.name == "FarmHouse"))
            {
                switch (nm)
                {
                    case "front_door": m = BuildMat.Wood; hp = 100f; break;
                    case "shutters": m = BuildMat.Wood; hp = 80f; break;
                    case "porch": m = BuildMat.Wood; hp = 300f; break;
                    case "porch_roof": m = BuildMat.Wood; hp = 200f; break;
                    case "porch_steps": m = BuildMat.Wood; hp = 150f; break;
                    case "chimney": m = BuildMat.Brick; hp = 300f; break;
                    case "roof": m = BuildMat.Wood; hp = 600f; break;
                    case "interior_props": m = BuildMat.Wood; hp = 150f; break;
                    default: continue;
                }
            }
            else if (t.parent != null && t.parent.name.StartsWith("Warehouse"))
            {
                switch (nm)
                {
                    case "crates": m = BuildMat.Wood; hp = 100f; break;
                    case "roof_vents": m = BuildMat.Metal; hp = 150f; break;
                    case "side_door": m = BuildMat.Metal; hp = 250f; break;
                    case "roller_door": m = BuildMat.Metal; hp = 400f; break;
                    case "roof": m = BuildMat.Metal; hp = 900f; break;
                    default: continue;
                }
            }
            else if (t.parent != null && t.parent.name.StartsWith("LookoutTower"))
            {
                if (nm == "railings") { m = BuildMat.Wood; hp = 150f; }
                else if (nm == "roof") { m = BuildMat.Wood; hp = 300f; }
                else continue;
            }
            else if ((nm.StartsWith("wall_") || nm.StartsWith("floor_") || nm.StartsWith("ramp_") || nm.StartsWith("pyramid_roof_")) && t.GetComponentInChildren<Collider>() != null)
            {
                m = nm.EndsWith("brick") ? BuildMat.Brick : nm.EndsWith("metal") ? BuildMat.Metal : BuildMat.Wood;
                hp = BuildRules.MaxHp(m) * 2f;
            }
            else continue;
            if (t.GetComponentInChildren<Collider>(true) == null) continue;
            var d = t.gameObject.AddComponent<Destructible>();
            d.mat = m;
            d.maxHp = d.hp = hp;
            n++;
        }
        return n;
    }

    static float Size(Transform t)
    {
        var r = t.GetComponentInChildren<Renderer>();
        return r != null ? r.bounds.size.magnitude : 3f;
    }
}

/// Fortnite building on the modular kit's 4 m grid: walls on cell edges, floors, stairs and cones in
/// cells, snapped vertically to the pieces already standing nearby. Pieces cost 10 of a material,
/// build up their hit points, and fall when nothing connects them to the ground any more.
public static class BuildSystem
{
    static readonly Dictionary<string, Destructible> Slots = new Dictionary<string, Destructible>();
    const float G = BuildRules.Grid, L = BuildRules.Level;

    public struct Slot
    {
        public PieceType type;
        public Vector3 pos;
        public float yaw, baseY;
        public string key;
        public bool free;
    }

    public static int PlacedByPlayer, PlacedByBots;   // for the demo logs

    /// the piece standing in a slot, if any
    public static Destructible At(Slot s) => Slots.TryGetValue(s.key, out var d) && d != null && d.Alive ? d : null;

    public static void Reset()
    {
        Slots.Clear();
        PlacedByPlayer = PlacedByBots = 0;
        Destructible.LevelBroken = Destructible.BuiltBroken = Destructible.PickaxeHits = Destructible.MatsHarvested = 0;
    }

    /// the slot a builder standing at feet and looking along yaw/pitch would build into
    public static Slot Compute(PieceType type, Vector3 feet, float yaw, float pitch)
    {
        float y4 = Mathf.Round(yaw / 90f) * 90f;
        var f = Quaternion.Euler(0f, y4, 0f) * Vector3.forward;
        f = new Vector3(Mathf.Round(f.x), 0f, Mathf.Round(f.z));
        int cx = Mathf.FloorToInt(feet.x / G), cz = Mathf.FloorToInt(feet.z / G);
        var cell = new Vector3((cx + 0.5f) * G, 0f, (cz + 0.5f) * G);
        var s = new Slot { type = type };
        switch (type)
        {
            case PieceType.Wall:
                s.pos = cell + f * G * 0.5f;
                s.yaw = Mathf.Abs(f.x) > 0.5f ? 90f : 0f;
                s.baseY = Snap(s.pos, feet.y, true);
                break;
            case PieceType.Floor:
                s.pos = pitch > 50f ? cell : cell + f * G;
                s.yaw = 0f;
                s.baseY = Snap(s.pos, feet.y - 0.25f, false);
                break;
            case PieceType.Stairs:
                s.pos = cell + f * G;
                s.yaw = y4;
                s.baseY = Snap(s.pos, feet.y - 0.25f, false);
                break;
            default:   // cones: over the cell ahead, or over your head when looking up or straight down
                s.pos = pitch > 50f || pitch < -15f ? cell : cell + f * G;
                s.yaw = 0f;
                s.baseY = Snap(s.pos, feet.y + L - 0.25f, false);
                break;
        }
        s.pos.y = s.baseY;
        s.key = $"{type}:{Mathf.RoundToInt(s.pos.x * 2)}:{Mathf.RoundToInt(s.pos.z * 2)}:{Mathf.RoundToInt(s.baseY * 2)}" + (type == PieceType.Stairs ? ":" + Mathf.RoundToInt(Mathf.Repeat(s.yaw, 360f)) : "");
        s.free = !Slots.ContainsKey(s.key);
        return s;
    }

    /// storey heights follow the nearest structure; on its own a piece sits at the builder's level
    /// (walls reach down to the ground)
    static float Snap(Vector3 p, float guess, bool wall)
    {
        float best = float.MaxValue, y = guess;
        foreach (var d in Destructible.Built)
        {
            var q = d.transform.position;
            if (Mathf.Abs(q.x - p.x) > G * 2.1f || Mathf.Abs(q.z - p.z) > G * 2.1f) continue;
            float lvl = d.baseY + Mathf.Round((guess - d.baseY) / L) * L;
            float e = Mathf.Abs(lvl - guess);
            if (e < best) { best = e; y = lvl; }
        }
        if (best < L) return y;
        if (wall && Physics.Raycast(new Vector3(p.x, guess + 1.5f, p.z), Vector3.down, out var h, 6f, GameLayers.Env, QueryTriggerInteraction.Ignore))
            return Mathf.Min(guess, h.point.y - 0.1f);
        return guess;
    }

    public static Destructible Place(Combatant who, Slot s, BuildMat m)
    {
        if (!s.free || who.inv.mats[(int)m] < BuildRules.Cost) return null;
        var a = GameAssets.I;
        int idx = (int)s.type * 3 + (int)m;
        var root = new GameObject($"Build_{s.type}_{m}");
        root.transform.SetPositionAndRotation(s.pos, Quaternion.Euler(0f, s.yaw, 0f));
        var model = Object.Instantiate(a.buildPrefabs[idx], root.transform, false);
        model.transform.localRotation = Quaternion.Euler(0f, a.buildYaw[idx], 0f);
        model.transform.localPosition = a.buildOffset[idx];
        foreach (var l in model.GetComponentsInChildren<LODGroup>(true)) Object.Destroy(l);
        var d = root.AddComponent<Destructible>();
        d.built = true;
        d.harvestable = false;
        d.mat = m;
        d.piece = s.type;
        d.maxHp = BuildRules.MaxHp(m);
        d.hp = d.maxHp * 0.1f;
        d.buildStart = Time.time;
        d.buildEnd = Time.time + BuildRules.BuildTime(m);
        d.slotKey = s.key;
        d.baseY = s.baseY;
        d.owner = who;
        d.model = model;
        d.meshIdx = idx;
        Destructible.Harvestables.Remove(d);
        Destructible.Built.Add(d);
        var rs = model.GetComponentsInChildren<Renderer>();
        d.bounds = rs.Length > 0 ? rs[0].bounds : new Bounds(s.pos, Vector3.one);
        foreach (var r in rs) d.bounds.Encapsulate(r.bounds);
        if (s.type == PieceType.Wall)
        {
            var ob = root.AddComponent<NavMeshObstacle>();
            ob.shape = NavMeshObstacleShape.Box;
            ob.center = new Vector3(0f, 2f, 0f);
            ob.size = new Vector3(G, L, 0.4f);
            ob.carving = true;
        }
        who.inv.mats[(int)m] -= BuildRules.Cost;
        if (who.isPlayer) PlacedByPlayer++; else PlacedByBots++;
        Slots[s.key] = d;
        Fx.I.BuildPop(d.bounds.center, d.bounds.size);
        Sfx.I?.PlayAt("build", s.pos + Vector3.up, 0.7f);
        return d;
    }

    /// cut tiles out of a wall or floor (0 makes it whole again); at least one tile stays
    public static void Edit(Destructible d, int removed)
    {
        if (d == null || !d.built || !d.Alive || !PieceEdit.Editable(d.piece) || d.editMask == removed || removed == PieceEdit.Full(d.piece)) return;
        d.editMask = removed;
        if (d.edited != null) Object.Destroy(d.edited);
        d.edited = null;
        d.model.SetActive(removed == 0);
        if (removed != 0)
        {
            var src = GameAssets.I.buildMeshes[d.meshIdx];
            var go = new GameObject("Edited") { layer = d.model.layer };
            go.transform.SetParent(d.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = PieceEdit.Cut(src, d.piece, removed);
            go.AddComponent<MeshRenderer>().sharedMaterials = d.model.GetComponentInChildren<Renderer>(true).sharedMaterials;
            float thick = d.piece == PieceType.Wall ? src.bounds.size.z : src.bounds.size.y;
            for (int i = 0; i < PieceEdit.Tiles(d.piece); i++)
            {
                if ((removed & (1 << i)) != 0) continue;
                var tb = PieceEdit.Tile(d.piece, i, thick);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = tb.center;
                bc.size = tb.size;
            }
            d.edited = go;
        }
        // a door lets the bots' paths through the wall
        var ob = d.GetComponent<NavMeshObstacle>();
        if (ob != null) ob.enabled = (removed & PieceEdit.Door) != PieceEdit.Door;
        Sfx.I?.PlayAt("edit", d.bounds.center, 0.7f);
    }

    /// a piece broke: everything that is no longer connected to the ground through other pieces falls,
    /// the nearest first, so a structure comes down in a quick chain
    public static void OnBroken(Destructible gone)
    {
        if (gone.slotKey != null) Slots.Remove(gone.slotKey);
        var pieces = new List<Destructible>();
        foreach (var d in Destructible.Built) if (d != gone && d.Alive && !d.doomed) pieces.Add(d);
        var supported = new HashSet<Destructible>();
        var queue = new Queue<Destructible>();
        foreach (var d in pieces)
            if (Grounded(d)) { supported.Add(d); queue.Enqueue(d); }
        while (queue.Count > 0)
        {
            var a = queue.Dequeue();
            var ab = a.bounds; ab.Expand(0.3f);
            foreach (var b in pieces)
                if (!supported.Contains(b) && ab.Intersects(b.bounds)) { supported.Add(b); queue.Enqueue(b); }
        }
        var from = gone.Center;
        foreach (var d in pieces)
            if (!supported.Contains(d)) d.BreakAfter(0.12f + 0.16f * Vector3.Distance(d.Center, from) / G);
    }

    static bool Grounded(Destructible d)
    {
        var b = d.bounds;
        var c = new Vector3(b.center.x, b.min.y + 0.4f, b.center.z);
        var ext = b.extents * 0.8f;
        foreach (var o in new[] { Vector3.zero, new Vector3(ext.x, 0, ext.z), new Vector3(-ext.x, 0, ext.z), new Vector3(ext.x, 0, -ext.z), new Vector3(-ext.x, 0, -ext.z) })
            foreach (var h in Physics.RaycastAll(c + o, Vector3.down, 1.2f, GameLayers.Env, QueryTriggerInteraction.Ignore))
            {
                var hd = h.collider.GetComponentInParent<Destructible>();
                if (hd == null || !hd.built) return true;
            }
        return false;
    }
}
