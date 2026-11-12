using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// An item lying on the ground: a weapon of some rarity, a consumable stack or a pile of ammo. Shows
/// the test 1 model with a rarity-coloured glow; spilled items fly out in a short arc first.
public class WorldItem : MonoBehaviour
{
    public static readonly List<WorldItem> All = new List<WorldItem>();
    static Dictionary<string, GameObject> _prefabs;
    static Mesh _quad;
    static MaterialPropertyBlock _mpb;

    public Item item;                       // null for an ammo pile or materials
    public AmmoType ammoType;
    public int ammoCount;
    public int matType = -1, matCount;      // a pile of building materials
    Vector3 _from, _to;
    float _t0 = -1f;
    Transform _glow;
    float _phase;

    public bool IsAmmo => item == null;
    public bool IsMats => matType >= 0;
    public string Label => item != null ? item.Label : IsMats ? BuildRules.Label((BuildMat)matType) : WeaponTable.AmmoLabel(ammoType);
    public Rarity Rarity => item != null ? item.rarity : Rarity.Common;
    public bool Settled => _t0 < 0f || Time.time - _t0 > 0.5f;

    void OnEnable() { All.Add(this); _phase = Random.value * 6.28f; }
    void OnDisable() => All.Remove(this);

    static GameObject Prefab(string id)
    {
        if (_prefabs == null)
        {
            _prefabs = new Dictionary<string, GameObject>();
            foreach (var p in GameAssets.I.itemPrefabs) if (p) _prefabs[p.name] = p;
        }
        return _prefabs.TryGetValue(id, out var g) ? g : null;
    }

    public static WorldItem Spawn(Item it, Vector3 at, Vector3? from = null)
    {
        var w = Make(it.Id, at, from, it.IsWeapon && it.weapon.kind != FireKind.Grenade, 1f, it.rarity);
        w.item = it;
        return w;
    }

    public static WorldItem SpawnAmmo(AmmoType t, int n, Vector3 at, Vector3? from = null)
    {
        var w = Make("ammo_box", at, from, false, 0.5f, Rarity.Common);
        w.ammoType = t;
        w.ammoCount = n;
        return w;
    }

    public static WorldItem SpawnMats(BuildMat m, int n, Vector3 at, Vector3? from = null)
    {
        var w = Make(null, at, from, false, 0.13f, Rarity.Common, GameAssets.I.buildPrefabs[(int)m]);
        w.matType = (int)m;
        w.matCount = n;
        return w;
    }

    static WorldItem Make(string id, Vector3 at, Vector3? from, bool lying, float scale, Rarity r, GameObject prefabOverride = null)
    {
        var root = new GameObject("Loot_" + id);
        // settle on the ground under the target point
        if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out var g, 6f, GameLayers.Env, QueryTriggerInteraction.Ignore)) at = g.point;
        root.transform.position = at;
        var w = root.AddComponent<WorldItem>();
        var prefab = prefabOverride != null ? prefabOverride : Prefab(id);
        if (prefab != null)
        {
            var v = Instantiate(prefab, root.transform, false);
            foreach (var c in v.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var l in v.GetComponentsInChildren<LODGroup>(true)) Destroy(l);
            v.transform.localScale = Vector3.one * scale;
            v.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * (lying ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity);
            // lift so the lowest point rests on the ground
            var rs = v.GetComponentsInChildren<Renderer>();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds;
                foreach (var rr in rs) b.Encapsulate(rr.bounds);
                v.transform.position += Vector3.up * (at.y + 0.02f - b.min.y);
            }
        }
        w.AddGlow(r);
        w._to = at;
        if (from.HasValue)
        {
            w._from = from.Value;
            w._t0 = Time.time;
            root.transform.position = from.Value;
        }
        return w;
    }

    void AddGlow(Rarity r)
    {
        if (_quad == null)
        {
            _quad = new Mesh();
            _quad.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.RecalculateBounds();
            _mpb = new MaterialPropertyBlock();
        }
        var go = new GameObject("Glow");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.up * 0.03f;
        go.transform.localScale = Vector3.one * (r >= Rarity.Epic ? 1.5f : 1.1f);
        go.AddComponent<MeshFilter>().sharedMesh = _quad;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GameAssets.I.additive;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var c = WeaponTable.RarityColor(r) * 0.6f;
        c.a = 0.6f;
        _mpb.SetColor("_TintColor", c);
        mr.SetPropertyBlock(_mpb);
        _glow = go.transform;
    }

    void Update()
    {
        if (_t0 >= 0f)
        {
            float k = Mathf.Clamp01((Time.time - _t0) / 0.45f);
            var p = Vector3.Lerp(_from, _to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 1.2f;
            transform.position = p;
            if (k >= 1f) _t0 = -1f;
        }
        if (_glow != null) _glow.localScale = Vector3.one * (1.1f + 0.12f * Mathf.Sin(Time.time * 3f + _phase));
    }

    public void Remove() => Destroy(gameObject);

    /// spill items around a point in a ring (chests, eliminated players)
    public static void Spill(List<Item> items, List<(AmmoType t, int n)> ammo, Vector3 at)
    {
        int total = items.Count + ammo.Count;
        int i = 0;
        float a0 = Random.Range(0f, 360f);
        foreach (var it in items) Spawn(it, Ring(at, i++, total, a0), at + Vector3.up * 0.5f);
        foreach (var (t, n) in ammo) SpawnAmmo(t, n, Ring(at, i++, total, a0), at + Vector3.up * 0.5f);
    }

    static Vector3 Ring(Vector3 c, int i, int n, float a0)
    {
        float a = (a0 + 360f * i / Mathf.Max(1, n)) * Mathf.Deg2Rad;
        float r = 1.1f + 0.35f * (i % 2);
        return c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
    }

    public static void SpillInventory(Inventory inv, Vector3 at)
    {
        var items = new List<Item>();
        for (int k = 0; k < inv.slots.Length; k++) if (inv.slots[k] != null) { items.Add(inv.slots[k]); inv.slots[k] = null; }
        var ammo = new List<(AmmoType, int)>();
        for (int t = 1; t < inv.ammo.Length; t++) if (inv.ammo[t] > 0) { ammo.Add(((AmmoType)t, inv.ammo[t])); inv.ammo[t] = 0; }
        inv.selected = 0;
        Spill(items, ammo, at);
        for (int m = 0; m < 3; m++)
            if (inv.mats[m] > 0) { SpawnMats((BuildMat)m, inv.mats[m], at + Random.insideUnitSphere * 1.6f, at + Vector3.up * 0.5f); inv.mats[m] = 0; }
    }
}

/// A treasure chest or ammo box from the level: search it once to spill its loot.
public class LootContainer : MonoBehaviour
{
    public static readonly List<LootContainer> All = new List<LootContainer>();
    public bool chest;
    public bool supply;      // a supply drop: an epic or legendary weapon, a shield, materials, a traversal item
    public bool opened;
    static readonly System.Random Rng = new System.Random();

    public string Label => supply ? "Supply Drop" : chest ? "Treasure Chest" : "Ammo Box";
    public Rarity Rarity => supply ? Rarity.Legendary : chest ? Rarity.Rare : Rarity.Common;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public void Open()
    {
        if (opened) return;
        opened = true;
        var items = new List<Item>();
        var ammo = new List<(AmmoType, int)>();
        if (supply)
        {
            Traversal.DropsOpened++;
            var guns = new[] { "assault_rifle", "shotgun", "sniper_rifle", "rocket_launcher" };
            var d = WeaponTable.Get(guns[Rng.Next(guns.Length)]);
            items.Add(Item.Weapon(d, Rng.NextDouble() < 0.4 ? Rarity.Legendary : Rarity.Epic));
            ammo.Add((d.ammo, WeaponTable.AmmoPile(d.ammo) * 3));
            items.Add(Item.Consumable(WeaponTable.GetConsumable("shield_potion"), 2));
            items.Add(Item.Consumable(WeaponTable.GetConsumable(Rng.NextDouble() < 0.5 ? "launch_pad" : "rift_to_go"), 1));
            for (int m = 0; m < 3; m++) WorldItem.SpawnMats((BuildMat)m, 50, transform.position + Random.insideUnitSphere * 1.8f, transform.position + Vector3.up);
        }
        else if (chest)
        {
            var guns = new[] { "assault_rifle", "shotgun", "sniper_rifle", "rocket_launcher", "pistol" };
            var d = WeaponTable.Get(guns[Rng.Next(guns.Length)]);
            items.Add(Item.Weapon(d, WeaponTable.RollRarity(Rng, true)));
            ammo.Add((d.ammo, WeaponTable.AmmoPile(d.ammo) * 2));
            double r = Rng.NextDouble();
            if (r < 0.35) items.Add(Item.Weapon(WeaponTable.Get("grenade"), Rarity.Common));
            else if (r < 0.7) items.Add(Item.Consumable(WeaponTable.GetConsumable("shield_potion"), 1));
            else items.Add(Item.Consumable(WeaponTable.GetConsumable("bandage"), 5));
            var glow = transform.Find("chest_inner_glow");
            if (glow) glow.gameObject.SetActive(false);
        }
        else
        {
            for (int k = 0; k < 2; k++)
            {
                var t = (AmmoType)(1 + Rng.Next(5));
                ammo.Add((t, WeaponTable.AmmoPile(t)));
            }
        }
        Sfx.I?.PlayAt("pickup", transform.position, 0.9f);
        WorldItem.Spill(items, ammo, transform.position + transform.forward * 0.6f);
    }

    /// the level's placed loot becomes match loot: weapons get a rarity and some ammo, consumables
    /// stay what they are, chests and ammo boxes become containers
    public static int ConvertLevelLoot(Transform root, System.Random rng)
    {
        int n = 0;
        var todo = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.parent != null && t.GetComponent<Collider>() != null && t.gameObject.activeInHierarchy) todo.Add(t);
        string[] guns = { "assault_rifle", "shotgun", "sniper_rifle", "rocket_launcher", "pistol" };
        foreach (var t in todo)
        {
            string nm = t.name;
            var at = t.position;
            if (nm.StartsWith("treasure_chest") || nm.StartsWith("TreasureChest")) { t.gameObject.AddComponent<LootContainer>().chest = true; n++; continue; }
            if (nm.StartsWith("ammo_box")) { t.gameObject.AddComponent<LootContainer>(); n++; continue; }
            var c = WeaponTable.GetConsumable(nm);
            if (c != null)
            {
                WorldItem.Spawn(Item.Consumable(c, c.id == "bandage" ? 5 : 1), at);
                t.gameObject.SetActive(false); n++; continue;
            }
            if (nm == "grenade") { var g = Item.Weapon(WeaponTable.Get("grenade"), Rarity.Common); WorldItem.Spawn(g, at); t.gameObject.SetActive(false); n++; continue; }
            bool gun = System.Array.IndexOf(guns, nm) >= 0;
            if (gun || nm == "pickaxe" || nm == "hammer_pickaxe")
            {
                var d = WeaponTable.Get(gun ? nm : guns[rng.Next(guns.Length)]);
                WorldItem.Spawn(Item.Weapon(d, WeaponTable.RollRarity(rng, false)), at);
                WorldItem.SpawnAmmo(d.ammo, WeaponTable.AmmoPile(d.ammo), at + new Vector3(0.7f, 0f, 0.3f));
                t.gameObject.SetActive(false); n++;
            }
        }
        return n;
    }
}
