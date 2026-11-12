using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

/// Zone layout and object placement.
public static partial class IslandBuilder
{
    static Dictionary<string, GameObject> Prefabs;
    static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
    static readonly Dictionary<string, Bounds> BoundsCache = new Dictionary<string, Bounds>();
    static Random Rng;
    static Random TintRng = new Random(77);

    // ---------------------------------------------------------------- occupancy (circles on the ground plane)
    static readonly List<Vector3> OccSmall = new List<Vector3>();   // x, z, radius
    static readonly Dictionary<long, List<int>> OccGrid = new Dictionary<long, List<int>>();
    static readonly List<Vector3> OccBig = new List<Vector3>();
    const float OccCell = 6f;

    static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

    static void OccClear() { OccSmall.Clear(); OccGrid.Clear(); OccBig.Clear(); }

    static void OccAdd(Vector2 p, float r)
    {
        if (r > 3f) { OccBig.Add(new Vector3(p.x, p.y, r)); return; }
        OccSmall.Add(new Vector3(p.x, p.y, r));
        long k = Key(Mathf.FloorToInt(p.x / OccCell), Mathf.FloorToInt(p.y / OccCell));
        if (!OccGrid.TryGetValue(k, out var l)) OccGrid[k] = l = new List<int>();
        l.Add(OccSmall.Count - 1);
    }

    static bool OccFree(Vector2 p, float r)
    {
        foreach (var b in OccBig)
            if ((new Vector2(b.x, b.y) - p).sqrMagnitude < (b.z + r) * (b.z + r)) return false;
        int ci = Mathf.FloorToInt(p.x / OccCell), cj = Mathf.FloorToInt(p.y / OccCell);
        for (int i = ci - 1; i <= ci + 1; i++)
            for (int j = cj - 1; j <= cj + 1; j++)
                if (OccGrid.TryGetValue(Key(i, j), out var l))
                    foreach (int idx in l)
                    {
                        var c = OccSmall[idx];
                        if ((new Vector2(c.x, c.y) - p).sqrMagnitude < (c.z + r) * (c.z + r)) return false;
                    }
        return true;
    }

    // ---------------------------------------------------------------- terrain queries
    static float GH(float x, float z) => terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;

    static Vector3 TNormal(float x, float z) =>
        terrain.terrainData.GetInterpolatedNormal((x + Size / 2f) / Size, (z + Size / 2f) / Size);

    static float Slope(float x, float z) =>
        terrain.terrainData.GetSteepness((x + Size / 2f) / Size, (z + Size / 2f) / Size);

    static float GroundMin(float x, float z, float r)
    {
        float m = GH(x, z);
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.PI / 4f;
            m = Mathf.Min(m, GH(x + Mathf.Cos(a) * r, z + Mathf.Sin(a) * r));
        }
        return m;
    }

    // ---------------------------------------------------------------- spawning
    static Bounds LB(string name)
    {
        if (!BoundsCache.TryGetValue(name, out var b)) { b = IslandAssets.LocalBounds(Prefabs[name], null); BoundsCache[name] = b; }
        return b;
    }

    static GameObject Spawn(string name, Transform parent, Vector3 pos, Quaternion rot, float scale = 1f)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(Prefabs[name], parent.gameObject.scene);
        go.transform.SetParent(parent, true);
        go.transform.SetPositionAndRotation(pos, rot);
        if (Mathf.Abs(scale - 1f) > 1e-4f) go.transform.localScale = Vector3.one * scale;
        Counts[name] = Counts.TryGetValue(name, out var c) ? c + 1 : 1;
        // foliage colour variety: one of three leaf tints per tree / bush (own RNG, placement unchanged)
        int tint = TintRng.Next(3);
        if (tint > 0)
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && IslandAssets.FoliageVariants.TryGetValue(mats[i], out var vs)) { mats[i] = vs[tint - 1]; changed = true; }
                if (changed) r.sharedMaterials = mats;
            }
        return go;
    }

    static float LowestY(string name, Quaternion q, float scale)
    {
        var lb = LB(name);
        float minY = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            var c = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            minY = Mathf.Min(minY, (q * (c * scale)).y);
        }
        return minY;
    }

    static readonly Dictionary<string, Vector3[]> BottomCache = new Dictionary<string, Vector3[]>();

    /// mesh vertices of a prefab in root space, thinned to about 256 points (all of them, not just the
    /// bottom, because lying loot is rolled onto its side before it is placed)
    static Vector3[] BottomPoints(string name)
    {
        if (BottomCache.TryGetValue(name, out var pts)) return pts;
        var root = Prefabs[name].transform;
        var list = new List<Vector3>();
        foreach (var mf in Prefabs[name].GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices) list.Add(m.MultiplyPoint3x4(v));
        }
        int step = Mathf.Max(1, list.Count / 256);
        pts = list.Where((p, i) => i % step == 0).ToArray();
        BottomCache[name] = pts;
        return pts;
    }

    /// Put a prefab on the terrain. Untilted objects: lowest point of the bounds on the lowest ground
    /// under the footprint. Tilted objects (align): 60 % tilt toward the ground normal, then lowered
    /// until none of the real bottom vertices is above the terrain. Either way nothing floats; `sink`
    /// pushes it a little further into the ground.
    static GameObject PlaceOnGround(string name, Transform parent, float x, float z, float yaw, float scale = 1f,
        float sink = 0f, bool align = false, Quaternion? extra = null, float footR = -1f)
    {
        var lb = LB(name);
        Quaternion q = Quaternion.Euler(0f, yaw, 0f) * (extra ?? Quaternion.identity);
        if (!align)
        {
            float minY = LowestY(name, q, scale);
            if (footR < 0f) footR = Mathf.Max(lb.extents.x, lb.extents.z) * scale * 0.6f;
            return Spawn(name, parent, new Vector3(x, GroundMin(x, z, footR) - minY - sink, z), q, scale);
        }
        q = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, TNormal(x, z)), 0.6f) * q;
        // the bottom is chosen after the rotation: the lowest 15 % of the rotated vertices
        var rotated = BottomPoints(name).Select(bp => q * (bp * scale)).ToArray();
        float lo = rotated.Min(w => w.y), hi = rotated.Max(w => w.y);
        float cut = lo + 0.15f * (hi - lo);
        float py = float.MaxValue;
        foreach (var w in rotated)
            if (w.y <= cut) py = Mathf.Min(py, GH(x + w.x, z + w.z) - w.y);
        return Spawn(name, parent, new Vector3(x, py - sink, z), q, scale);
    }

    /// Put a prefab on a floor inside a building: local position (floor height) in the building frame.
    static GameObject PlaceInside(string name, Transform parent, Transform building, Vector3 local, float yaw, Quaternion? extra = null)
    {
        Quaternion q = building.rotation * Quaternion.Euler(0f, yaw, 0f) * (extra ?? Quaternion.identity);
        Vector3 p = building.TransformPoint(local);
        p.y += -LowestY(name, q, 1f) + 0.005f;
        return Spawn(name, parent, p, q);
    }

    static float RandRange(float a, float b) => a + (float)Rng.NextDouble() * (b - a);
    static float RandYaw() => (float)Rng.NextDouble() * 360f;
    static T Pick<T>(IList<T> list) => list[Rng.Next(list.Count)];

    /// generic validity for objects standing on natural ground
    static bool GroundOk(float x, float z, float minCoast = 2f, float minWater = 2.5f, float maxRoad = 0.25f, float maxSlope = 32f)
    {
        if (Mathf.Abs(x) > 280f || Mathf.Abs(z) > 280f) return false;
        if (CoastSd(x, z) < minCoast) return false;
        if (WaterSd(x, z) < minWater) return false;
        if (Road(x, z) > maxRoad) return false;
        if (Paved(x, z) > 0.3f) return false;
        if (GH(x, z) < 0.6f) return false;
        if (Slope(x, z) > maxSlope) return false;
        float s; if (ChannelDist(new Vector2(x, z), out s) < 6f) return false;
        return true;
    }

    // ---------------------------------------------------------------- layout plan (before the terrain)
    struct Building { public Vector2 c; public float yaw; public Pad pad; }
    static readonly List<Building> VillageHouses = new List<Building>();
    static Building FarmHouse, Barn, Fort, TowerA, TowerB, TowerC;
    static readonly List<Building> Warehouses = new List<Building>();

    struct BridgeSpec { public Vector2 c; public float yaw, length, width, deckY, water; }
    static BridgeSpec Bridge1, Bridge2;

    static Building MakeBuilding(string name, Vector2 c, float yaw, Vector2 padOffset, Vector2 padHalf, float margin, float? h = null)
    {
        var pad = AddPad(name, ToWorld(padOffset, c, yaw), padHalf, yaw, margin, h);
        return new Building { c = c, yaw = yaw, pad = pad };
    }

    static float FacingYaw(Vector2 from, Vector2 to)
    {
        Vector2 d = to - from;
        return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
    }

    static void PlanLayout()
    {
        Rng = new Random(20261026);
        TintRng = new Random(77);
        VillageHouses.Clear();
        Warehouses.Clear();
        OccClear();

        // village: 7 houses around the plaza, angles of the 6 map houses plus one in the widest gap;
        // each spot is nudged off the radial roads
        float[] angles = { 90f, 148f, 32.5f, 204f, 335.5f, 266f, 300f };
        foreach (float a0 in angles)
        {
            Vector2 best = Vector2.zero; float bestRoad = float.MaxValue;
            foreach (float da in new[] { 0f, 6f, -6f, 11f, -11f })
                foreach (float r in new[] { 30f, 27.5f, 32.5f })
                {
                    float a = (a0 + da) * Mathf.Deg2Rad;
                    var c = PlazaC + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    float yaw = FacingYaw(c, PlazaC);
                    float road = 0f;
                    for (int k = -1; k <= 1; k++)
                        for (int m = -1; m <= 1; m++)
                        {
                            var w = ToWorld(new Vector2(k * 3.6f, 0.6f + m * 3.8f), c, yaw);
                            road = Mathf.Max(road, Road(w.x, w.y));
                        }
                    road += Mathf.Abs(da) * 0.004f + Mathf.Abs(r - 30f) * 0.004f;
                    if (road < bestRoad) { bestRoad = road; best = c; }
                }
            float y = FacingYaw(best, PlazaC);
            VillageHouses.Add(MakeBuilding("house", best, y, new Vector2(0f, 0.6f), new Vector2(4.1f, 4.6f), 5f));
        }

        FarmHouse = MakeBuilding("farmhouse", FarmHouseC, 180f, new Vector2(0f, 0.6f), new Vector2(4.1f, 4.6f), 6f);
        Barn = MakeBuilding("barn", new Vector2(157f, 91f), 270f, Vector2.zero, new Vector2(2.6f, 2.6f), 4f);

        foreach (var c in WarehouseC)
            Warehouses.Add(MakeBuilding("warehouse", c, WarehouseYaw, new Vector2(0f, 0.45f), new Vector2(7.6f, 6.1f), 6f));
        Fort = MakeBuilding("fort", FortC, FortYaw, new Vector2(0f, -2f), new Vector2(4.6f, 6.6f), 5f);

        TowerA = MakeBuilding("tower_nw", TowerNW, 160f, Vector2.zero, new Vector2(3f, 3f), 5f);
        TowerB = MakeBuilding("tower_se", TowerSE, 250f, Vector2.zero, new Vector2(3f, 3f), 5f);
        TowerC = MakeBuilding("tower_s", TowerS, 0f, Vector2.zero, new Vector2(3f, 3f), 5f);

        Bridge1 = PlanBridge(Bridge1Guess);
        Bridge2 = PlanBridge(Bridge2Guess);
    }

    /// Bridge across the river near `guess`: perpendicular to the local river direction, long enough to
    /// rest on both banks; the road ends are flattened to deck height with pads.
    static BridgeSpec PlanBridge(Vector2 guess)
    {
        Vector2 Centroid(float z)
        {
            float s = 0f; int n = 0;
            for (float x = guess.x - 35f; x <= guess.x + 35f; x += 0.25f)
                if (WaterSd(x, z) < 0f) { s += x; n++; }
            return new Vector2(n > 0 ? s / n : guess.x, z);
        }
        Vector2 a = Centroid(guess.y + 9f), b = Centroid(guess.y - 9f), c = Centroid(guess.y);
        Vector2 river = (a - b).normalized;
        Vector2 axis = new Vector2(river.y, -river.x);
        float l = 0f, r = 0f;
        while (WaterSd(c.x + axis.x * l, c.y + axis.y * l) < 0f && l < 40f) l += 0.25f;
        while (WaterSd(c.x - axis.x * r, c.y - axis.y * r) < 0f && r < 40f) r += 0.25f;
        Vector2 center = c + axis * (l - r) * 0.5f;
        float width = l + r;
        float length = width + 11f;
        float yaw = Mathf.Atan2(axis.x, axis.y) * Mathf.Rad2Deg;
        float wl = WaterLevel(center.y);
        Vector2 e1 = center + axis * (length * 0.5f + 1.5f), e2 = center - axis * (length * 0.5f + 1.5f);
        float deck = Mathf.Max(wl + 2.0f, Mathf.Max(RawHeight(e1.x, e1.y), RawHeight(e2.x, e2.y)) + 0.15f);
        var spec = new BridgeSpec { c = center, yaw = yaw, length = length, width = 4.4f, deckY = deck, water = wl };
        // ends of the deck rest on flattened ground: pads along the axis beyond the banks
        AddPad("bridge_end", center + axis * (length * 0.5f + 1.0f), new Vector2(3.2f, 3.0f), yaw, 9f, deck - 0.12f);
        AddPad("bridge_end", center - axis * (length * 0.5f + 1.0f), new Vector2(3.2f, 3.0f), yaw, 9f, deck - 0.12f);
        return spec;
    }

    // ---------------------------------------------------------------- population
    static readonly Dictionary<string, Transform> ZoneGroups = new Dictionary<string, Transform>();

    static Transform Zone(Transform root, string name)
    {
        if (!ZoneGroups.TryGetValue(name, out var t) || t == null) ZoneGroups[name] = t = Group(root, name);
        return t;
    }

    static void PopulateZones(Transform root)
    {
        ZoneGroups.Clear();
        var village = Zone(root, "Zone_Center_Village");
        var farm = Zone(root, "Zone_NorthEast_Farm");
        var yard = Zone(root, "Zone_SouthWest_Industrial");
        var north = Zone(root, "Zone_North_PineForest_Lake");
        var west = Zone(root, "Zone_West_OakForest");
        var beach = Zone(root, "Zone_East_Beach");
        var cliff = Zone(root, "Zone_South_Cliff");
        var hills = Zone(root, "Zone_Hilltops");
        var roads = Zone(root, "Roads_Bridges");
        var scatter = Zone(root, "Scatter_Everywhere");
        var sky = Zone(root, "Sky_Events");

        // register planned buildings so nothing else spawns in them
        foreach (var h in VillageHouses) OccAdd(h.pad.c, 6.4f);
        OccAdd(FarmHouse.pad.c, 6.4f);
        OccAdd(Barn.pad.c, 4.0f);
        foreach (var w in Warehouses) OccAdd(w.pad.c, 9.9f);
        OccAdd(Fort.pad.c, 8.5f);
        OccAdd(TowerA.c, 4f); OccAdd(TowerB.c, 4f); OccAdd(TowerC.c, 4f);
        foreach (var b in new[] { Bridge1, Bridge2 })
            for (float s = -b.length / 2f - 3f; s <= b.length / 2f + 3f; s += 3f)
                OccAdd(b.c + new Vector2(Mathf.Sin(b.yaw * Mathf.Deg2Rad), Mathf.Cos(b.yaw * Mathf.Deg2Rad)) * s, 3.2f);

        BuildVillage(village);
        BuildFarm(farm);
        BuildIndustrial(yard);
        BuildTowers(hills, cliff);
        BuildBridge("Bridge_North", Bridge1, Group(roads, "Bridges"));
        BuildBridge("Bridge_South", Bridge2, Group(roads, "Bridges"));
        BuildNorth(north);
        BuildWest(west);
        BuildBeach(beach);
        BuildScatter(scatter, cliff);
        BuildLoot(root);
        BuildSkyEvents(sky);
    }

    // ---------------------------------------------------------------- village
    static readonly Vector3 ChestSpot = new Vector3(-2.3f, 0.45f, -2.15f);   // ground floor, back corner opposite the stairs
    static readonly Vector3 UpstairsSpot = new Vector3(1.2f, 3.45f, -2.0f);  // second floor, beside the bed
    static readonly Vector3 HallSpot = new Vector3(-2.6f, 0.45f, 1.4f);     // ground floor, front corner

    static void BuildVillage(Transform g)
    {
        var houses = Group(g, "Houses");
        var props = Group(g, "Props");
        var veg = Group(g, "Vegetation");
        var loot = Group(g, "Loot");
        string[] upstairs = { "assault_rifle", "shield_potion", "shotgun", "med_kit", "sniper_rifle", "ammo_box", "pistol" };
        for (int i = 0; i < VillageHouses.Count; i++)
        {
            var hs = VillageHouses[i];
            var house = Spawn("house", houses, new Vector3(hs.c.x, hs.pad.h - 0.03f, hs.c.y), Quaternion.Euler(0f, hs.yaw, 0f));
            house.name = $"House_{i + 1:00}";
            if (i < 5) PlaceInside("treasure_chest", loot, house.transform, ChestSpot, 0f).name = $"TreasureChest_House_{i + 1:00}";
            else PlaceInside("ammo_box", loot, house.transform, ChestSpot + new Vector3(0.2f, 0f, 0.1f), 15f);
            PlaceInside(upstairs[i], loot, house.transform, UpstairsSpot, 70f, LyingRot(upstairs[i]));
            if (i % 2 == 0) PlaceInside("bandage", loot, house.transform, HallSpot, 30f);
            // a barrel and a crate beside each porch
            var bp = house.transform.TransformPoint(new Vector3(3.25f, 0f, 3.7f));
            PlaceOnGround("barrel", props, bp.x, bp.z, RandYaw(), 1f, 0.02f, true);
            var cp = house.transform.TransformPoint(new Vector3(-3.35f, 0f, 3.6f));
            PlaceOnGround("wood_crate", props, cp.x, cp.z, hs.yaw + RandRange(-12f, 12f), 1f, 0.02f, true);
            // bushes along the back wall
            for (int k = 0; k < 3; k++)
            {
                var p = house.transform.TransformPoint(new Vector3(RandRange(-4.5f, 4.5f), 0f, RandRange(-6.2f, -5.0f)));
                if (Road(p.x, p.z) < 0.2f) PlaceOnGround(Rng.NextDouble() < 0.5 ? "bush_plain" : "bush_berries", veg, p.x, p.z, RandYaw(), RandRange(0.8f, 1.2f), 0.08f, true);
            }
        }
        // plaza: the big tree in the middle with a ring of bushes, crates and barrels at the edge
        PlaceOnGround("tree_oak", veg, PlazaC.x, PlazaC.y, 40f, 1.2f, 0.25f, false, null, 0.8f);
        OccAdd(PlazaC, 3.5f);
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.PI / 4f + 0.2f;
            PlaceOnGround(k % 2 == 0 ? "bush_berries" : "bush_plain", veg, PlazaC.x + Mathf.Cos(a) * 4.6f, PlazaC.y + Mathf.Sin(a) * 4.6f, RandYaw(), RandRange(0.85f, 1.1f), 0.08f, true);
        }
        for (int k = 0; k < 6; k++)
        {
            float a = (k * 60f + 25f) * Mathf.Deg2Rad;
            var p = PlazaC + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 17.5f;
            if (k % 2 == 0) { var c = PlaceOnGround("wood_crate", props, p.x, p.y, RandYaw(), 1f, 0.02f, true); if (k == 0) StackOn("wood_crate", props, c.transform); }
            else PlaceOnGround("barrel", props, p.x, p.y, RandYaw(), 1f, 0.02f, true);
            OccAdd(p, 0.8f);
        }
    }

    static GameObject StackOn(string name, Transform parent, Transform below)
    {
        var p = below.position + below.up * 0.818f;
        var q = below.rotation * Quaternion.Euler(0f, RandRange(-20f, 20f), 0f);
        return Spawn(name, parent, p, q);
    }

    // ---------------------------------------------------------------- farm
    static void BuildFarm(Transform g)
    {
        var bld = Group(g, "Buildings");
        var props = Group(g, "Props");
        var fences = Group(g, "Fences");
        var crops = Group(g, "Crops");
        var loot = Group(g, "Loot");
        var veg = Group(g, "Vegetation");

        var fh = Spawn("house", bld, new Vector3(FarmHouse.c.x, FarmHouse.pad.h - 0.03f, FarmHouse.c.y), Quaternion.Euler(0f, FarmHouse.yaw, 0f));
        fh.name = "FarmHouse";
        PlaceInside("treasure_chest", loot, fh.transform, ChestSpot, 0f).name = "TreasureChest_FarmHouse";
        PlaceInside("shotgun", loot, fh.transform, UpstairsSpot, 80f, LyingRot("shotgun"));

        // small barn from the modular kit (wood): floor, three walls, pyramid roof, open toward the fields
        var barn = new GameObject("Barn_ModularKit").transform;
        barn.SetParent(bld, false);
        barn.position = new Vector3(Barn.c.x, Barn.pad.h, Barn.c.y);
        barn.rotation = Quaternion.Euler(0f, Barn.yaw, 0f);
        KitCell("floor_wood", barn, new Vector3(0f, 0f, 0f), 0f);
        KitWall("wall_wood", barn, new Vector3(0f, 0f, -2f), 0f);
        KitWall("wall_wood", barn, new Vector3(-2f, 0f, 0f), 90f);
        KitWall("wall_wood", barn, new Vector3(2f, 0f, 0f), 90f);
        KitCell("pyramid_roof_wood", barn, new Vector3(0f, 4f, 0f), 0f);
        PlaceInside("ammo_box", loot, barn, new Vector3(0.6f, 0.25f, -1.0f), 10f);

        // crop rows on the wheat and green fields, ploughed soil in the middle field (terrain layer)
        foreach (var f in Fields)
            if (f.kind != 1) BuildCrops(crops, f);

        // fences: compound perimeter (gaps where roads cross) and a fence around the wheat field
        Vector2 c = FarmCompoundC, h = FarmCompoundHalf - new Vector2(1f, 1f);
        Vector2 sw = c - h, se = new Vector2(c.x + h.x, c.y - h.y), ne = c + h, nw = new Vector2(c.x - h.x, c.y + h.y);
        FenceLine(fences, sw, se); FenceLine(fences, se, ne); FenceLine(fences, ne, nw); FenceLine(fences, nw, sw);
        var wf = Fields[0];
        Vector2 wsw = wf.c - wf.half - new Vector2(1.5f, 1.5f), wne = wf.c + wf.half + new Vector2(1.5f, 1.5f);
        FenceLine(fences, wsw, new Vector2(wne.x, wsw.y), gapAt: 0.5f);
        FenceLine(fences, new Vector2(wne.x, wsw.y), wne);
        FenceLine(fences, wne, new Vector2(wsw.x, wne.y));
        FenceLine(fences, new Vector2(wsw.x, wne.y), wsw);

        // barrels and crates around the farmhouse and the barn
        var spots = new List<Vector2>();
        for (int k = 0; k < 5; k++) spots.Add(ToWorld(new Vector2(RandRange(-6.5f, -5f) * (k % 2 == 0 ? 1 : -1), RandRange(-3f, 3f)), FarmHouse.c, FarmHouse.yaw));
        for (int k = 0; k < 6; k++) spots.Add(Barn.c + new Vector2(RandRange(-9f, -4f), RandRange(-6f, 6f)));
        int n = 0;
        foreach (var p in spots)
        {
            if (!OccFree(p, 0.7f) || FieldAt(p.x, p.y, 0.5f) >= 0) continue;
            if (n % 3 == 1) PlaceOnGround("barrel", props, p.x, p.y, RandYaw(), 1f, 0.02f, true);
            else
            {
                var cr = PlaceOnGround("wood_crate", props, p.x, p.y, RandYaw(), 1f, 0.02f, true);
                if (n % 4 == 0) StackOn("wood_crate", props, cr.transform);
            }
            OccAdd(p, 0.8f);
            n++;
        }
        // a few trees and bushes around the farm yard
        for (int k = 0, tries = 0; k < 14 && tries < 600; tries++)
        {
            var p = new Vector2(RandRange(c.x - h.x - 8f, c.x + h.x + 4f), RandRange(c.y - h.y - 6f, c.y + h.y + 8f));
            if (FieldAt(p.x, p.y, 3f) >= 0 || !GroundOk(p.x, p.y) || !OccFree(p, 1.4f)) continue;
            if (RectDist(p, c, h) < 2f && RectDist(p, c, h - new Vector2(3f, 3f)) > 0f) continue; // keep the fence line clear
            PlaceOnGround(k % 3 == 0 ? "tree_oak" : (k % 3 == 1 ? "bush_plain" : "bush_berries"), veg, p.x, p.y, RandYaw(), RandRange(0.8f, 1.2f), k % 3 == 0 ? 0.25f : 0.08f, k % 3 != 0, null, k % 3 == 0 ? 0.8f : -1f);
            OccAdd(p, k % 3 == 0 ? 2.6f : 1.0f);
            k++;
        }
    }

    static void FenceLine(Transform g, Vector2 a, Vector2 b, float gapAt = -1f)
    {
        float len = Vector2.Distance(a, b);
        int n = Mathf.FloorToInt(len / 2f);
        Vector2 dir = (b - a) / len;
        float start = (len - n * 2f) * 0.5f;
        for (int k = 0; k < n; k++)
        {
            if (gapAt >= 0f && Mathf.Abs((k + 0.5f) / n - gapAt) < 1.6f / n) continue;   // a gate
            Vector2 p1 = a + dir * (start + k * 2f), p2 = p1 + dir * 2f, m = (p1 + p2) * 0.5f;
            if (Road(m.x, m.y) > 0.3f || WaterSd(m.x, m.y) < 1.5f || CoastSd(m.x, m.y) < 1.5f) continue;
            float h1 = GH(p1.x, p1.y), h2 = GH(p2.x, p2.y);
            Vector3 right = new Vector3(p2.x - p1.x, h2 - h1, p2.y - p1.y).normalized;
            Vector3 fwd = Vector3.Cross(right, Vector3.up).normalized;
            Vector3 up = Vector3.Cross(fwd, right);
            var q = Quaternion.LookRotation(fwd, up);
            Spawn("fence_segment", g, new Vector3(m.x, Mathf.Min(h1, h2) + Mathf.Abs(h2 - h1) * 0.5f - 0.06f, m.y), q);
            OccAdd(m, 1.0f);
        }
    }

    static Material _cropMat;

    /// Crop rows as one mesh per field: wheat = clumps of golden stalks with ears, green field = low
    /// leafy rosettes. Vertex alpha carries the wind weight (0 at the root, 1 at the tip).
    static void BuildCrops(Transform g, Field f)
    {
        if (_cropMat == null)
        {
            _cropMat = new Material(Shader.Find("Island/VertexColorLitNoShadow")) { name = "crops", enableInstancing = true };
            _cropMat.SetColor("_Color", Color.white);
            _cropMat.SetFloat("_Glossiness", 0.15f);
            _cropMat.SetFloat("_Cull", 0f);
            _cropMat.SetFloat("_Wind", 0.07f);
            _cropMat.SetFloat("_WindAlpha", 1f);
            AssetDatabase.CreateAsset(_cropMat, $"{GenDir}/Materials/crops.mat");
        }
        bool wheat = f.kind == 0;
        var rng = new Random(wheat ? 101 : 202);
        var verts = new List<Vector3>(); var cols = new List<Color>(); var norms = new List<Vector3>(); var tris = new List<int>();
        var origin = new Vector3(f.c.x, 0f, f.c.y);
        float rowSpacing = wheat ? 0.42f : 0.62f, step = wheat ? 0.26f : 0.38f;
        Color stalkBase = new Color(0.72f, 0.56f, 0.20f).linear, stalkTip = new Color(0.98f, 0.81f, 0.30f).linear, ear = new Color(1.0f, 0.83f, 0.28f).linear;
        Color leafBase = new Color(0.18f, 0.40f, 0.12f).linear, leafTip = new Color(0.45f, 0.72f, 0.24f).linear;

        void Blade(Vector3 root, Vector3 lean, float h, float w, Color cb, Color ct, float tint)
        {
            Vector3 side = Vector3.Cross(Vector3.up, lean.sqrMagnitude > 1e-6f ? lean.normalized : Vector3.forward).normalized * w;
            int i0 = verts.Count;
            Vector3 mid = root + Vector3.up * h * 0.55f + lean * 0.3f, tip = root + Vector3.up * h + lean;
            verts.Add(root - side); verts.Add(root + side); verts.Add(mid - side * 0.7f); verts.Add(mid + side * 0.7f); verts.Add(tip);
            Color b = cb * tint, t = ct * tint, m = Color.Lerp(b, t, 0.5f);
            b.a = 0f; m.a = 0.5f; t.a = 1f;
            cols.Add(b); cols.Add(b); cols.Add(m); cols.Add(m); cols.Add(t);
            var nrm = (Vector3.up + (lean.sqrMagnitude > 1e-6f ? lean.normalized : Vector3.zero) * 0.2f).normalized;
            for (int k = 0; k < 5; k++) norms.Add(nrm);
            tris.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3, i0 + 2, i0 + 4, i0 + 3 });
        }

        for (float x = -f.half.x + rowSpacing * 0.5f; x <= f.half.x; x += rowSpacing)
            for (float z = -f.half.y + 0.4f; z <= f.half.y - 0.4f; z += step)
            {
                float wx = f.c.x + x + ((float)rng.NextDouble() - 0.5f) * 0.12f, wz = f.c.y + z + ((float)rng.NextDouble() - 0.5f) * 0.12f;
                if (Road(wx, wz) > 0.35f) continue;   // farm tracks cross the fields on the map
                var root = new Vector3(wx, GH(wx, wz) - 0.04f, wz) - origin;
                float tint = 0.9f + 0.2f * Mathf.PerlinNoise(wx * 0.15f + 3f, wz * 0.15f);
                int n = wheat ? 4 : 7;
                for (int k = 0; k < n; k++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                    var lean = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    if (wheat)
                    {
                        float h = 0.85f + 0.3f * (float)rng.NextDouble();
                        var r = root + lean * 0.05f;
                        Blade(r, lean * 0.08f, h, 0.012f, stalkBase, stalkTip, tint);
                        // the ear: a fatter short blade on top of the stalk
                        var top = r + Vector3.up * h + lean * 0.08f;
                        Blade(top - Vector3.up * 0.03f, lean * 0.06f, 0.2f, 0.045f, ear, ear * 1.05f, tint);
                    }
                    else
                    {
                        float h = 0.32f + 0.16f * (float)rng.NextDouble();
                        Blade(root, lean * h * 0.9f, h, 0.09f, leafBase, leafTip, tint);
                    }
                }
            }
        var mesh = new Mesh { name = wheat ? "wheat_rows" : "crop_rows", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts); mesh.SetColors(cols); mesh.SetNormals(norms); mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, $"{GenDir}/Meshes/{mesh.name}.asset");
        var go = new GameObject(wheat ? "WheatField" : "CropField");
        go.transform.SetParent(g, false);
        go.transform.position = origin;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _cropMat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // thousands of thin blades: receive only
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
    }

    // ---------------------------------------------------------------- modular kit helpers
    // Kit pieces have their origin on a grid corner: floor/ramp/roof cover x [-4,0], z [-4,0];
    // a wall runs along x [-4,0] on z = 0. These helpers place them by cell centre / edge centre.
    static GameObject KitCell(string piece, Transform parent, Vector3 cellCenter, float yaw)
    {
        var r = Quaternion.Euler(0f, yaw, 0f);
        var go = Spawn(piece, parent, Vector3.zero, Quaternion.identity);
        go.transform.localPosition = cellCenter - r * new Vector3(-2f, 0f, -2f);
        go.transform.localRotation = r;
        return go;
    }

    static GameObject KitWall(string piece, Transform parent, Vector3 edgeCenter, float yaw)
    {
        var r = Quaternion.Euler(0f, yaw, 0f);
        var go = Spawn(piece, parent, Vector3.zero, Quaternion.identity);
        go.transform.localPosition = edgeCenter - r * new Vector3(-2f, 0f, 0f);
        go.transform.localRotation = r;
        return go;
    }

    // ---------------------------------------------------------------- industrial yard
    static void BuildIndustrial(Transform g)
    {
        var bld = Group(g, "Buildings");
        var props = Group(g, "Props");
        var loot = Group(g, "Loot");
        string[] inside = { "assault_rifle", "rocket_launcher", "sniper_rifle" };
        for (int i = 0; i < Warehouses.Count; i++)
        {
            var w = Warehouses[i];
            var wh = Spawn("warehouse", bld, new Vector3(w.c.x, w.pad.h - 0.02f, w.c.y), Quaternion.Euler(0f, w.yaw, 0f));
            wh.name = $"Warehouse_{i + 1}";
            PlaceInside("ammo_box", loot, wh.transform, new Vector3(-3.0f, 0.22f, -2.0f), 20f);
            PlaceInside(inside[i], loot, wh.transform, new Vector3(3.5f, 0.22f, -2.5f), 60f, LyingRot(inside[i]));
            PlaceInside(i == 1 ? "treasure_chest" : "med_kit", loot, wh.transform, new Vector3(0.5f, 0.22f, 0.5f), 180f);
            for (int k = 0; k < 3; k++)
            {
                var c = PlaceInside("wood_crate", props, wh.transform, new Vector3(-5.6f + k * 0.95f, 0.22f, -3.9f), RandRange(-6f, 6f));
                if (k == 1) StackOn("wood_crate", props, c.transform);
            }
        }

        // fort from the modular kit: brick ground floor (door on the south-west side), metal upper deck,
        // wooden ramp up to the deck, covered back room with metal pyramid roofs
        var fort = new GameObject("Fort_ModularKit").transform;
        fort.SetParent(bld, false);
        fort.position = new Vector3(Fort.c.x, Fort.pad.h, Fort.c.y);
        fort.rotation = Quaternion.Euler(0f, Fort.yaw, 0f);
        foreach (var cc in new[] { new Vector3(-2, 0, -2), new Vector3(2, 0, -2), new Vector3(-2, 0, 2), new Vector3(2, 0, 2) })
        {
            KitCell("floor_brick", fort, cc, 0f);
            KitCell("floor_metal", fort, cc + new Vector3(0, 4f, 0), 0f);
        }
        KitWall("wall_brick", fort, new Vector3(2f, 0f, -4f), 0f);
        KitWall("wall_brick", fort, new Vector3(-2f, 0f, 4f), 0f);
        KitWall("wall_brick", fort, new Vector3(2f, 0f, 4f), 0f);
        KitWall("wall_brick", fort, new Vector3(-4f, 0f, -2f), 90f);
        KitWall("wall_brick", fort, new Vector3(-4f, 0f, 2f), 90f);
        KitWall("wall_brick", fort, new Vector3(4f, 0f, -2f), 90f);
        KitWall("wall_brick", fort, new Vector3(4f, 0f, 2f), 90f);
        KitWall("wall_metal", fort, new Vector3(-2f, 4f, 4f), 0f);
        KitWall("wall_metal", fort, new Vector3(2f, 4f, 4f), 0f);
        KitWall("wall_metal", fort, new Vector3(-4f, 4f, 2f), 90f);
        KitWall("wall_metal", fort, new Vector3(4f, 4f, 2f), 90f);
        KitCell("pyramid_roof_metal", fort, new Vector3(-2f, 8f, 2f), 0f);
        KitCell("pyramid_roof_metal", fort, new Vector3(2f, 8f, 2f), 0f);
        KitCell("ramp_wood", fort, new Vector3(2f, 0f, -6f), 180f);
        PlaceInside("ammo_box", loot, fort, new Vector3(-2.5f, 0.25f, 2.5f), 30f);
        PlaceInside("shotgun", loot, fort, new Vector3(1.5f, 0.25f, 2.0f), 100f, LyingRot("shotgun"));
        PlaceInside("shield_potion", loot, fort, new Vector3(-1.5f, 4.25f, -2.0f), 0f);
        PlaceInside("rocket_launcher", loot, fort, new Vector3(1.0f, 4.25f, 2.0f), 75f, LyingRot("rocket_launcher"));

        // crate stacks, barrels and ammo boxes on the paved yard
        int placed = 0;
        for (int tries = 0; tries < 4000 && placed < 34; tries++)
        {
            var p = new Vector2(RandRange(-150f, -28f), RandRange(-130f, -28f));
            if (Paved(p.x, p.y) < 0.6f || !OccFree(p, 1.3f)) continue;
            float yaw = WarehouseYaw + RandRange(-10f, 10f);
            if (placed % 5 == 4) PlaceOnGround("barrel", props, p.x, p.y, RandYaw(), 1f, 0.02f, true);
            else
            {
                var c = PlaceOnGround("wood_crate", props, p.x, p.y, yaw, 1f, 0.02f, true);
                if (placed % 3 == 0) { var c2 = StackOn("wood_crate", props, c.transform); if (placed % 9 == 0) StackOn("wood_crate", props, c2.transform); }
                if (placed % 4 == 1) Spawn("ammo_box", loot, c.transform.position + c.transform.up * 0.82f, c.transform.rotation * Quaternion.Euler(0f, 20f, 0f));
                // a second crate beside it makes a small cluster
                var side = c.transform.position + c.transform.right * 0.9f;
                if (OccFree(new Vector2(side.x, side.z), 0.5f) && Paved(side.x, side.z) > 0.6f) PlaceOnGround("wood_crate", props, side.x, side.z, yaw, 1f, 0.02f, true);
            }
            OccAdd(p, 1.6f);
            placed++;
        }
    }

    // ---------------------------------------------------------------- towers
    static void BuildTowers(Transform hills, Transform cliff)
    {
        var t1 = Spawn("lookout_tower", Group(hills, "Buildings"), new Vector3(TowerA.c.x, TowerA.pad.h - 0.02f, TowerA.c.y), Quaternion.Euler(0f, TowerA.yaw, 0f));
        t1.name = "LookoutTower_NorthWestHill";
        var t2 = Spawn("lookout_tower", Group(hills, "Buildings"), new Vector3(TowerB.c.x, TowerB.pad.h - 0.02f, TowerB.c.y), Quaternion.Euler(0f, TowerB.yaw, 0f));
        t2.name = "LookoutTower_SouthEastHill";
        var t3 = Spawn("lookout_tower", Group(cliff, "Buildings"), new Vector3(TowerC.c.x, TowerC.pad.h - 0.02f, TowerC.c.y), Quaternion.Euler(0f, TowerC.yaw, 0f));
        t3.name = "LookoutTower_SouthCliff";
        PlaceInside("sniper_rifle", Group(hills, "Loot"), t1.transform, new Vector3(0.5f, 7.0f, -0.4f), 45f, LyingRot("sniper_rifle"));
        PlaceInside("shield_potion", Group(hills, "Loot"), t2.transform, new Vector3(-0.6f, 7.0f, 0.6f), 0f);
        PlaceInside("assault_rifle", Group(hills, "Loot"), t2.transform, new Vector3(0.4f, 7.0f, -0.5f), 120f, LyingRot("assault_rifle"));
        PlaceInside("sniper_rifle", Group(cliff, "Loot"), t3.transform, new Vector3(0.3f, 7.0f, 0.5f), 80f, LyingRot("sniper_rifle"));
        PlaceInside("ammo_box", Group(cliff, "Loot"), t3.transform, new Vector3(-0.7f, 7.0f, -0.6f), 10f);
    }

    static Quaternion? LyingRot(string name)
    {
        switch (name)
        {
            case "pistol": case "shotgun": case "assault_rifle": case "sniper_rifle": case "rocket_launcher":
            case "hammer_pickaxe": case "pickaxe": case "grenade":
                return Quaternion.Euler(0f, 0f, 90f);   // on its side, like a dropped weapon
            default: return null;
        }
    }

    // ---------------------------------------------------------------- forests, beach, scatter
    static int Scatter(Transform g, string[] names, int target, Func<float, float, float> density, Vector4 box, float radius,
        float scaleMin, float scaleMax, float sink, bool align, int maxTries = 60000, Func<float, float, bool> extra = null,
        float minCoast = 3f, float maxSlope = 32f, float footR = -1f)
    {
        int placed = 0;
        for (int t = 0; t < maxTries && placed < target; t++)
        {
            float x = RandRange(box.x, box.z), z = RandRange(box.y, box.w);
            if (Rng.NextDouble() > density(x, z)) continue;
            if (!GroundOk(x, z, minCoast, 3.0f, 0.2f, maxSlope)) continue;
            if (extra != null && !extra(x, z)) continue;
            var p = new Vector2(x, z);
            if (!OccFree(p, radius)) continue;
            string n = names[Rng.Next(names.Length)];
            float s = RandRange(scaleMin, scaleMax);
            PlaceOnGround(n, g, x, z, RandYaw(), s, n.StartsWith("rock") ? sink * LB(n).size.y * s : sink, align, null, footR);
            OccAdd(p, radius);
            placed++;
        }
        return placed;
    }

    static bool NotInClearing(float x, float z)
    {
        foreach (var c in OakClearings) if (Vector2.Distance(new Vector2(x, z), new Vector2(c.x, c.y)) < c.z) return false;
        return true;
    }

    static void BuildNorth(Transform g)
    {
        var veg = Group(g, "Pines");
        var rocks = Group(g, "Rocks");
        var bushes = Group(g, "Bushes");
        int pines = Scatter(veg, new[] { "tree_pine" }, 330, PineZone, new Vector4(-175f, 55f, 90f, 215f), 2.2f, 0.85f, 1.24f, 0.25f, false,
            extra: (x, z) => Vector2.Distance(new Vector2(x, z), TowerNW) > 9f && WaterSd(x, z) > 4f, footR: 0.7f);
        Debug.Log($"[IslandBuilder] pines {pines}");
        Scatter(rocks, new[] { "rock_02", "rock_04" }, 40, PineZone, new Vector4(-175f, 55f, 90f, 215f), 1.3f, 0.8f, 1.25f, 0.12f, true);
        // rocks on the lake shore
        Scatter(rocks, new[] { "rock_03", "rock_01", "rock_05", "rock_04" }, 18, (x, z) => 1f, new Vector4(-45f, 115f, 40f, 175f), 1.3f, 0.8f, 1.25f, 0.15f, true,
            extra: (x, z) => WaterSd(x, z) < 6f, maxSlope: 40f);
        Scatter(bushes, new[] { "bush_plain", "bush_berries" }, 45, PineZone, new Vector4(-175f, 55f, 90f, 215f), 1.0f, 0.8f, 1.25f, 0.08f, true);
    }

    static void BuildWest(Transform g)
    {
        var veg = Group(g, "Oaks");
        var rocks = Group(g, "Rocks");
        var bushes = Group(g, "Bushes");
        int oaks = Scatter(veg, new[] { "tree_oak" }, 235, OakZone, new Vector4(-215f, -50f, -60f, 135f), 2.6f, 0.85f, 1.24f, 0.25f, false,
            extra: NotInClearing, footR: 0.8f);
        Debug.Log($"[IslandBuilder] oaks {oaks}");
        Scatter(bushes, new[] { "bush_plain", "bush_berries" }, 50, OakZone, new Vector4(-215f, -50f, -60f, 135f), 1.0f, 0.8f, 1.25f, 0.08f, true);
        Scatter(rocks, new[] { "rock_01", "rock_02", "rock_03", "rock_05" }, 18, OakZone, new Vector4(-215f, -50f, -60f, 135f), 1.3f, 0.8f, 1.25f, 0.12f, true);
    }

    static void BuildBeach(Transform g)
    {
        var veg = Group(g, "Palms");
        var props = Group(g, "Props");
        int palms = Scatter(veg, new[] { "tree_palm" }, 40, (x, z) => Smooth(0.35f, 0.6f, SandMap(x, z)) * Smooth(100f, 125f, x),
            new Vector4(100f, -160f, 215f, 175f), 2.6f, 0.8f, 1.22f, 0.2f, false, minCoast: 2.5f, footR: 0.5f);
        Debug.Log($"[IslandBuilder] palms {palms}");
        Scatter(veg, new[] { "rock_01", "rock_05", "rock_04" }, 14, (x, z) => Smooth(0.3f, 0.6f, SandMap(x, z)), new Vector4(100f, -160f, 215f, 175f), 1.0f, 0.8f, 1.25f, 0.12f, true, minCoast: 1f);
        // washed-up crates and barrels
        Scatter(props, new[] { "wood_crate", "barrel" }, 7, (x, z) => Smooth(0.4f, 0.7f, SandMap(x, z)), new Vector4(100f, -160f, 215f, 175f), 1.0f, 1f, 1f, 0.03f, true, minCoast: 3f);
    }

    static void BuildScatter(Transform g, Transform cliff)
    {
        var trees = Group(g, "Trees");
        var bushes = Group(g, "Bushes");
        var rocks = Group(g, "Rocks");
        Func<float, float, bool> meadow = (x, z) =>
            Vector2.Distance(new Vector2(x, z), PlazaC) > 47f && FarmMask(x, z) < 0.3f && YardMask(x, z) < 0.2f && SandNear(x, z) < 0.25f;
        Func<float, float, float> open = (x, z) => 1f - Mathf.Max(PineZone(x, z), OakZone(x, z));
        // the map dots the meadows with round trees, each with a few bushes at its foot
        Func<float, float, bool> meadowTree = (x, z) => meadow(x, z) && Road(x, z) < 0.05f && Road(x + 4f, z) < 0.1f && Road(x - 4f, z) < 0.1f && Road(x, z + 4f) < 0.1f && Road(x, z - 4f) < 0.1f;
        int before = trees.childCount;
        Scatter(trees, new[] { "tree_oak", "tree_oak", "tree_oak", "tree_pine" }, 115, open, new Vector4(-200f, -170f, 200f, 200f), 3.2f, 0.8f, 1.22f, 0.25f, false, extra: meadowTree, footR: 0.8f);
        var meadowTrees = new List<Transform>();
        for (int i = before; i < trees.childCount; i++) meadowTrees.Add(trees.GetChild(i));
        foreach (var t in meadowTrees)
        {
            int nb = Rng.Next(1, 4);
            for (int k = 0; k < nb; k++)
            {
                float a = RandRange(0f, Mathf.PI * 2f), r = RandRange(3.4f, 4.6f);
                float bx = t.position.x + Mathf.Cos(a) * r, bz = t.position.z + Mathf.Sin(a) * r;
                if (!GroundOk(bx, bz) || FieldAt(bx, bz, 1f) >= 0 || !OccFree(new Vector2(bx, bz), 0.6f)) continue;
                PlaceOnGround(Rng.NextDouble() < 0.5 ? "bush_plain" : "bush_berries", bushes, bx, bz, RandYaw(), RandRange(0.85f, 1.25f), 0.08f, true);
                OccAdd(new Vector2(bx, bz), 0.6f);
            }
        }
        Scatter(bushes, new[] { "bush_plain", "bush_berries" }, 230, (x, z) => 1f, new Vector4(-205f, -190f, 205f, 205f), 1.0f, 0.8f, 1.25f, 0.08f, true,
            extra: (x, z) => FieldAt(x, z, 1f) < 0 && Vector2.Distance(new Vector2(x, z), PlazaC) > 20f);

        // rock clusters where the map draws grey rock (coastal rocks, the south cliff point, inland outcrops)
        int clusters = 0;
        for (float z = -205f; z <= 205f; z += 6.5f)
            for (float x = -205f; x <= 205f; x += 6.5f)
            {
                float jx = x + RandRange(-2.5f, 2.5f), jz = z + RandRange(-2.5f, 2.5f);
                if (RockMap(jx, jz) < 0.55f || Rng.NextDouble() > 0.55) continue;
                if (CoastSd(jx, jz) < 1.0f || WaterSd(jx, jz) < 2f || Road(jx, jz) > 0.2f || Paved(jx, jz) > 0.3f) continue;
                if (GH(jx, jz) < 0.5f || Slope(jx, jz) > 42f) continue;
                var p = new Vector2(jx, jz);
                if (!OccFree(p, 1.8f)) continue;
                bool south = Bell(jx, jz, TowerS, 80f) > 0f;
                var parent = south ? Group(cliff, "Rocks") : rocks;
                string big = Rng.NextDouble() < 0.6 ? "rock_03" : "rock_04";
                PlaceOnGround(big, parent, jx, jz, RandYaw(), RandRange(0.9f, 1.25f), 0.15f * LB(big).size.y, true);
                int small = Rng.Next(1, 4);
                for (int k = 0; k < small; k++)
                {
                    float a = RandRange(0f, Mathf.PI * 2f), r = RandRange(1.5f, 2.6f);
                    float sx = jx + Mathf.Cos(a) * r, sz = jz + Mathf.Sin(a) * r;
                    if (CoastSd(sx, sz) < 0.5f || Road(sx, sz) > 0.3f || WaterSd(sx, sz) < 1.5f || Slope(sx, sz) > 42f) continue;
                    string n = Pick(new[] { "rock_01", "rock_02", "rock_05", "rock_04" });
                    PlaceOnGround(n, parent, sx, sz, RandYaw(), RandRange(0.8f, 1.25f), 0.12f * LB(n).size.y, true);
                }
                OccAdd(p, 2.6f);
                clusters++;
            }
        Debug.Log($"[IslandBuilder] rock clusters {clusters}");

        int cliffRocks = 0;
        for (float z = -210f; z <= 210f; z += 3.6f)
            for (float x = -210f; x <= 210f; x += 3.6f)
            {
                float jx = x + RandRange(-1.4f, 1.4f), jz = z + RandRange(-1.4f, 1.4f);
                float sl = Slope(jx, jz);
                if (sl < 31f || sl > 48f || Rng.NextDouble() > 0.6) continue;   // very steep faces would swallow the rock
                if (GH(jx, jz) < 0.4f || CoastSd(jx, jz) < -1f || Road(jx, jz) > 0.2f || WaterSd(jx, jz) < 2f || Paved(jx, jz) > 0.3f) continue;
                var p = new Vector2(jx, jz);
                if (!OccFree(p, 1.1f)) continue;
                bool south = Bell(jx, jz, TowerS, 80f) > 0f;
                string n = Pick(new[] { "rock_03", "rock_04", "rock_04", "rock_02", "rock_01" });
                PlaceOnGround(n, south ? Group(cliff, "Rocks") : rocks, jx, jz, RandYaw(), RandRange(0.95f, 1.25f), 0.08f * LB(n).size.y, true);
                OccAdd(p, 1.2f);
                cliffRocks++;
            }
        Debug.Log($"[IslandBuilder] cliff rocks {cliffRocks}");

        // offshore boulders in the shallows
        var sea = Group(g, "OffshoreRocks");
        int off = 0;
        for (int t = 0; t < 20000 && off < 34; t++)
        {
            float x = RandRange(-215f, 215f), z = RandRange(-215f, 215f);
            float sd = CoastSd(x, z);
            if (sd > -2f || sd < -8f) continue;
            float gh = GH(x, z);
            if (gh < -2.2f || gh > -0.3f) continue;
            var p = new Vector2(x, z);
            if (!OccFree(p, 2.5f)) continue;
            PlaceOnGround("rock_03", sea, x, z, RandYaw(), RandRange(1.1f, 1.25f), 0.1f, false);
            if (Rng.NextDouble() < 0.6) PlaceOnGround("rock_04", sea, x + RandRange(-1.8f, 1.8f), z + RandRange(-1.8f, 1.8f), RandYaw(), RandRange(1f, 1.25f), 0.15f, true);
            OccAdd(p, 2.5f);
            off++;
        }
    }

    // ---------------------------------------------------------------- loot on the ground
    struct LootZone { public string group; public Func<float, float, bool> inside; public Vector4 box; }

    static void BuildLoot(Transform root)
    {
        var zones = new List<LootZone>
        {
            new LootZone { group = "Zone_Center_Village", inside = (x, z) => Vector2.Distance(new Vector2(x, z), PlazaC) < 50f, box = new Vector4(-55f, -45f, 55f, 62f) },
            new LootZone { group = "Zone_NorthEast_Farm", inside = (x, z) => FarmMask(x, z) > 0.5f && FieldAt(x, z, 0.5f) < 0, box = new Vector4(68f, 66f, 168f, 150f) },
            new LootZone { group = "Zone_East_Beach", inside = (x, z) => SandMap(x, z) > 0.5f, box = new Vector4(120f, -160f, 210f, 170f) },
            new LootZone { group = "Zone_SouthWest_Industrial", inside = (x, z) => Paved(x, z) > 0.5f, box = new Vector4(-150f, -130f, -28f, -28f) },
            new LootZone { group = "Zone_West_OakForest", inside = (x, z) => OakZone(x, z) > 0.6f, box = new Vector4(-210f, -40f, -70f, 130f) },
            new LootZone { group = "Zone_North_PineForest_Lake", inside = (x, z) => PineZone(x, z) > 0.6f, box = new Vector4(-170f, 60f, 85f, 210f) },
            new LootZone { group = "Zone_South_Cliff", inside = (x, z) => Bell(x, z, TowerS, 60f) > 0.05f, box = new Vector4(-60f, -205f, 60f, -110f) },
            new LootZone { group = "Zone_Hilltops", inside = (x, z) => Bell(x, z, TowerSE, 45f) > 0.05f || Bell(x, z, TowerNW, 45f) > 0.05f, box = new Vector4(-140f, -110f, 185f, 210f) },
        };
        string[] weapons = { "pistol", "shotgun", "assault_rifle", "sniper_rifle", "rocket_launcher", "grenade", "pickaxe" };
        var items = new List<string>();
        foreach (var w in weapons) for (int k = 0; k < 4; k++) items.Add(w);
        for (int k = 0; k < 12; k++) items.Add("ammo_box");
        for (int k = 0; k < 10; k++) items.Add("med_kit");
        for (int k = 0; k < 10; k++) items.Add("shield_potion");
        for (int k = 0; k < 6; k++) items.Add("bandage");
        for (int k = 0; k < 6; k++) items.Add("treasure_chest");
        int zi = Rng.Next(zones.Count);
        int failed = 0;
        foreach (var item in items)
        {
            bool ok = false;
            for (int attempt = 0; attempt < zones.Count && !ok; attempt++)
            {
                var zone = zones[(zi++) % zones.Count];
                for (int t = 0; t < 600; t++)
                {
                    float x = RandRange(zone.box.x, zone.box.z), z = RandRange(zone.box.y, zone.box.w);
                    if (!zone.inside(x, z)) continue;
                    if (CoastSd(x, z) < 2.5f || WaterSd(x, z) < 2.5f || GH(x, z) < 0.8f || Slope(x, z) > 22f) continue;
                    var p = new Vector2(x, z);
                    if (!OccFree(p, 1.0f)) continue;
                    float gy = GH(x, z);
                    if (Physics.OverlapSphere(new Vector3(x, gy + 0.9f, z), 0.7f).Any(c => !(c is TerrainCollider))) continue;
                    var parent = Group(Zone(root, zone.group), "Loot");
                    var go = PlaceOnGround(item, parent, x, z, RandYaw(), 1f, 0.0f, true, LyingRot(item));
                    OccAdd(p, 0.9f);
                    ok = true;
                    break;
                }
            }
            if (!ok) failed++;
        }
        if (failed > 0) Debug.LogWarning($"[IslandBuilder] {failed} loot items could not be placed");
    }

    // ---------------------------------------------------------------- supply drop and blimp
    static void BuildSkyEvents(Transform g)
    {
        // landed supply drop: preferred spots in the east meadow, otherwise any free flat meadow spot there
        var candidates = new List<Vector2> { new Vector2(86f, -10f), new Vector2(78f, 24f), new Vector2(96f, 40f), new Vector2(-48f, -18f), new Vector2(-30f, 70f) };
        for (int t = 0; t < 400; t++) candidates.Add(new Vector2(RandRange(60f, 130f), RandRange(-100f, 60f)));
        bool dropped = false;
        foreach (var c in candidates)
        {
            if (!GroundOk(c.x, c.y, 5f, 5f, 0.3f, 12f) || !OccFree(c, 3.5f) || FarmMask(c.x, c.y) > 0.2f) continue;
            var drop = PlaceOnGround("supply_drop_landed", g, c.x, c.y, 35f, 1f, 0.02f, true);
            drop.name = "SupplyDrop_Landed";
            OccAdd(c, 3.5f);
            Debug.Log($"[IslandBuilder] supply drop at ({c.x:F1}, {c.y:F1})");
            dropped = true;
            break;
        }
        if (!dropped) Debug.LogError("[IslandBuilder] no free spot for the landed supply drop");
        // the drop vehicle (a bus under a hot-air balloon, the match's bus) hangs 80 m above the island,
        // nose toward the north-east; it replaced the cargo blimp on the user's request
        var bus = Spawn("drop_vehicle", g, new Vector3(100f, 86f, -118f), Quaternion.Euler(0f, 35f, 0f));
        bus.name = Match.BusObject;
    }

    // ---------------------------------------------------------------- spawn points
    static void PlaceSpawnPoints(Transform g)
    {
        var cand = new List<Vector3>();
        for (int t = 0; t < 30000 && cand.Count < 2500; t++)
        {
            float x = RandRange(-205f, 205f), z = RandRange(-205f, 205f);
            if (CoastSd(x, z) < 4f || WaterSd(x, z) < 3f || Slope(x, z) > 24f) continue;
            float gy = GH(x, z);
            if (gy < 1.0f) continue;
            float cs; if (ChannelDist(new Vector2(x, z), out cs) < 6f) continue;
            var a = new Vector3(x, gy + 0.45f, z); var b = new Vector3(x, gy + 1.75f, z);
            if (Physics.OverlapCapsule(a, b, 0.5f, ~0, QueryTriggerInteraction.Collide).Any(c => !(c is TerrainCollider))) continue;
            if (Physics.Raycast(new Vector3(x, gy + 60f, z), Vector3.down, out var hit, 70f) && !(hit.collider is TerrainCollider)) continue; // nothing overhead
            cand.Add(new Vector3(x, gy, z));
        }
        // farthest-point sampling spreads the 30 points over the whole island
        var chosen = new List<Vector3> { cand.OrderBy(c => new Vector2(c.x - PlazaC.x - 14f, c.z - PlazaC.y + 12f).sqrMagnitude).First() };
        var dmin = cand.Select(c => float.MaxValue).ToArray();
        while (chosen.Count < 30)
        {
            var last = chosen[chosen.Count - 1];
            int best = -1; float bestD = -1f;
            for (int i = 0; i < cand.Count; i++)
            {
                float d = new Vector2(cand[i].x - last.x, cand[i].z - last.z).sqrMagnitude;
                if (d < dmin[i]) dmin[i] = d;
                if (dmin[i] > bestD) { bestD = dmin[i]; best = i; }
            }
            chosen.Add(cand[best]);
        }
        for (int i = 0; i < chosen.Count; i++)
        {
            var go = new GameObject($"Spawn_{i + 1:00}");
            go.transform.SetParent(g, false);
            go.transform.position = chosen[i] + Vector3.up * 0.05f;
            go.transform.rotation = Quaternion.Euler(0f, FacingYaw(new Vector2(chosen[i].x, chosen[i].z), PlazaC), 0f);
        }
        Debug.Log($"[IslandBuilder] spawn candidates {cand.Count}");
    }
}
