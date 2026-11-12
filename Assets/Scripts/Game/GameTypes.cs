using System;
using UnityEngine;

// Battle royale layer on top of the island (added on the user's request: playable, shoot like
// Fortnite, then a Fortnite-like match: bus drop, glider, storm, 5-slot inventory, bots, victory).

public enum Stance { Rifle, Pistol, Melee, Unarmed }
public enum FireKind { Hitscan, Rocket, Melee, Grenade, Consume }
public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }
public enum AmmoType { None, Light, Medium, Heavy, Shells, Rockets }
public enum BuildMat { Wood, Brick, Metal }
public enum PieceType { Wall, Floor, Stairs, Cone }

/// Fortnite building numbers: hit points and build-up time per material, materials per pickaxe hit
public static class BuildRules
{
    public const float Grid = 4f, Level = 4f;      // the test 1 modular kit's grid: 4 m cells, 4 m storeys
    public const int Cost = 10, MaxMats = 999;
    public static float MaxHp(BuildMat m) => m == BuildMat.Wood ? 150f : m == BuildMat.Brick ? 300f : 450f;
    public static float BuildTime(BuildMat m) => m == BuildMat.Wood ? 2.5f : m == BuildMat.Brick ? 4.5f : 6.5f;
    public static int PerHit(BuildMat m) => m == BuildMat.Wood ? 8 : m == BuildMat.Brick ? 6 : 5;
    public static string Label(BuildMat m) => m == BuildMat.Wood ? "WOOD" : m == BuildMat.Brick ? "BRICK" : "METAL";
    public static string Key(PieceType t) => t == PieceType.Wall ? "Q" : t == PieceType.Floor ? "Z" : t == PieceType.Stairs ? "C" : "V";
    public static string PrefabId(PieceType t, BuildMat m) =>
        (t == PieceType.Wall ? "wall_" : t == PieceType.Floor ? "floor_" : t == PieceType.Stairs ? "ramp_" : "pyramid_roof_") +
        (m == BuildMat.Wood ? "wood" : m == BuildMat.Brick ? "brick" : "metal");
}

/// One test 1 weapon as a game item. Damage per hit (at Common rarity), interval between shots in
/// seconds, spread in degrees (cone half-angle), ranges in metres.
[Serializable]
public class WeaponDef
{
    public string id, label;
    public Stance stance;
    public FireKind kind;
    public AmmoType ammo;
    public float damage, headMult = 1.5f, interval;
    public bool auto;
    public int pellets = 1, mag;                 // mag 0: no ammo (pickaxe); grenades: a stack, no reload
    public float reload;
    public float spreadHip, spreadAds, bloom, bloomMax;
    public float range = 300f, falloffStart = 1000f;
    public float adsFov = 55f;
    public bool scope;
    public float recoil;                         // camera kick per shot (degrees)
    public float speed, radius;                  // projectile speed (m/s), splash radius (m)
    public float botRange = 40f;                 // preferred fighting distance for bots

    public bool UsesAmmo => ammo != AmmoType.None;
    public bool CanReload => UsesAmmo && mag > 0;
    public bool CanAim => kind == FireKind.Hitscan || kind == FireKind.Rocket;
}

/// Healing and shield items (the test 1 med kit, bandage and shield potion models).
[Serializable]
public class ConsumableDef
{
    public string id, label;
    public Rarity rarity;
    public float useTime, heal, healCap, shield, shieldCap;
    public int maxStack;
    public string action;     // traversal items: "pad" places a launch pad, "rift" opens a rift to the sky
}

/// An inventory or ground item: a weapon of some rarity with its loaded magazine, or a stack of
/// consumables. Grenades are weapons whose "magazine" is the stack size.
[Serializable]
public class Item
{
    public WeaponDef weapon;
    public ConsumableDef consumable;
    public Rarity rarity;
    public int mag;      // weapons: rounds loaded; grenades and consumables: count

    public bool IsWeapon => weapon != null;
    public string Id => weapon != null ? weapon.id : consumable.id;
    public string Label => weapon != null ? weapon.label : consumable.label;
    public bool Stacks => consumable != null || (weapon != null && weapon.kind == FireKind.Grenade);
    public int MaxStack => consumable != null ? consumable.maxStack : weapon != null && weapon.kind == FireKind.Grenade ? 6 : 1;

    public static Item Weapon(WeaponDef d, Rarity r) => new Item { weapon = d, rarity = r, mag = d.kind == FireKind.Grenade ? 3 : d.mag };
    public static Item Consumable(ConsumableDef c, int count) => new Item { consumable = c, rarity = c.rarity, mag = count };
    public Item Clone() => (Item)MemberwiseClone();
}

public static class WeaponTable
{
    public static readonly WeaponDef[] All =
    {
        new WeaponDef { id = "assault_rifle", label = "Assault Rifle", stance = Stance.Rifle, kind = FireKind.Hitscan, ammo = AmmoType.Medium,
            damage = 30, headMult = 1.5f, interval = 0.18f, auto = true, mag = 30, reload = 2.2f, botRange = 35f,
            spreadHip = 2.2f, spreadAds = 0.45f, bloom = 0.55f, bloomMax = 4f, range = 350f, falloffStart = 60f, adsFov = 50f, recoil = 0.9f },
        new WeaponDef { id = "shotgun", label = "Pump Shotgun", stance = Stance.Rifle, kind = FireKind.Hitscan, ammo = AmmoType.Shells,
            damage = 11, headMult = 1.5f, interval = 0.95f, pellets = 10, mag = 5, reload = 3.2f, botRange = 8f,
            spreadHip = 5.5f, spreadAds = 4.5f, range = 60f, falloffStart = 8f, adsFov = 60f, recoil = 4f },
        new WeaponDef { id = "sniper_rifle", label = "Sniper Rifle", stance = Stance.Rifle, kind = FireKind.Hitscan, ammo = AmmoType.Heavy,
            damage = 105, headMult = 2.5f, interval = 1.5f, mag = 1, reload = 2.6f, botRange = 80f,
            spreadHip = 6f, spreadAds = 0f, range = 900f, adsFov = 14f, scope = true, recoil = 5f },
        new WeaponDef { id = "rocket_launcher", label = "Rocket Launcher", stance = Stance.Rifle, kind = FireKind.Rocket, ammo = AmmoType.Rockets,
            damage = 110, headMult = 1f, interval = 1.2f, mag = 1, reload = 2.8f, botRange = 30f,
            spreadHip = 0.6f, spreadAds = 0.15f, range = 400f, adsFov = 55f, recoil = 5f, speed = 45f, radius = 5f },
        new WeaponDef { id = "pistol", label = "Pistol", stance = Stance.Pistol, kind = FireKind.Hitscan, ammo = AmmoType.Light,
            damage = 24, headMult = 2f, interval = 0.2f, mag = 16, reload = 1.5f, botRange = 20f,
            spreadHip = 1.6f, spreadAds = 0.5f, bloom = 0.8f, bloomMax = 4f, range = 150f, falloffStart = 30f, adsFov = 58f, recoil = 1.4f },
        new WeaponDef { id = "pickaxe", label = "Pickaxe", stance = Stance.Melee, kind = FireKind.Melee,
            damage = 20, headMult = 1f, interval = 0.8f, range = 2.4f, botRange = 1.5f },
        new WeaponDef { id = "grenade", label = "Grenade", stance = Stance.Unarmed, kind = FireKind.Grenade,
            damage = 100, headMult = 1f, interval = 1.1f, mag = 0, range = 0f, speed = 17f, radius = 4.5f, botRange = 18f },
    };

    public static readonly ConsumableDef[] Consumables =
    {
        new ConsumableDef { id = "bandage", label = "Bandages", rarity = Rarity.Common, useTime = 3.5f, heal = 15f, healCap = 75f, maxStack = 15 },
        new ConsumableDef { id = "med_kit", label = "Med Kit", rarity = Rarity.Uncommon, useTime = 8f, heal = 100f, healCap = 100f, maxStack = 3 },
        new ConsumableDef { id = "shield_potion", label = "Shield Potion", rarity = Rarity.Rare, useTime = 4f, shield = 50f, shieldCap = 100f, maxStack = 3 },
        new ConsumableDef { id = "launch_pad", label = "Launch Pad", rarity = Rarity.Rare, useTime = 0.6f, maxStack = 1, action = "pad" },
        new ConsumableDef { id = "rift_to_go", label = "Rift-To-Go", rarity = Rarity.Epic, useTime = 0.5f, maxStack = 1, action = "rift" },
    };

    /// the weapon def used while a consumable is in the hands (unarmed stance, use on fire)
    public static readonly WeaponDef UseDef = new WeaponDef { id = "use", label = "Use", stance = Stance.Unarmed, kind = FireKind.Consume };

    public static int IndexOf(string id) => Array.FindIndex(All, w => w.id == id);
    public static WeaponDef Get(string id) => Array.Find(All, w => w.id == id);
    public static ConsumableDef GetConsumable(string id) => Array.Find(Consumables, c => c.id == id);
    public static WeaponDef Pickaxe => All[5];

    /// higher rarity: more damage, faster reload (about Fortnite's steps)
    public static float DamageMult(Rarity r) => 1f + 0.06f * (int)r;
    public static float ReloadMult(Rarity r) => 1f - 0.05f * (int)r;

    public static Color RarityColor(Rarity r)
    {
        switch (r)
        {
            case Rarity.Uncommon: return new Color(0.32f, 0.70f, 0.16f);
            case Rarity.Rare: return new Color(0.16f, 0.52f, 0.93f);
            case Rarity.Epic: return new Color(0.62f, 0.27f, 0.90f);
            case Rarity.Legendary: return new Color(0.93f, 0.55f, 0.15f);
            default: return new Color(0.55f, 0.57f, 0.60f);
        }
    }

    public static string AmmoLabel(AmmoType t)
    {
        switch (t)
        {
            case AmmoType.Light: return "Light Bullets";
            case AmmoType.Medium: return "Medium Bullets";
            case AmmoType.Heavy: return "Heavy Bullets";
            case AmmoType.Shells: return "Shells";
            case AmmoType.Rockets: return "Rockets";
            default: return "";
        }
    }

    /// ammo found in one pile (ammo boxes, drops)
    public static int AmmoPile(AmmoType t)
    {
        switch (t)
        {
            case AmmoType.Light: return 36;
            case AmmoType.Medium: return 30;
            case AmmoType.Heavy: return 6;
            case AmmoType.Shells: return 10;
            case AmmoType.Rockets: return 3;
            default: return 0;
        }
    }

    /// floor loot rarity: mostly common to rare; chests roll one tier better
    public static Rarity RollRarity(System.Random rng, bool chest)
    {
        double r = rng.NextDouble();
        int tier = r < 0.38 ? 0 : r < 0.68 ? 1 : r < 0.88 ? 2 : r < 0.97 ? 3 : 4;
        if (chest) tier = Mathf.Min(4, tier + 1);
        return (Rarity)tier;
    }
}

public struct DamageEvent
{
    public float amount;
    public Vector3 point, dir;
    public bool head, explosion, storm, fall;
    public WeaponDef weapon;
    public Combatant source;
}

public interface IDamageable
{
    bool Alive { get; }
    string DisplayName { get; }
    /// returns the damage actually dealt
    float TakeDamage(DamageEvent e);
    /// whether a shot that entered the hitbox at point, travelling along dir, hits the head
    bool IsHead(Vector3 point, Vector3 dir);
}

public static class GameEvents
{
    /// damage dealt by the player: event, target, damage dealt, killed
    public static event Action<DamageEvent, IDamageable, float, bool> Hit;
    /// every elimination: killer (null for the storm or a fall), victim, the event that finished them
    public static event Action<Combatant, Combatant, DamageEvent> Eliminated;
    public static event Action<string> Feed;
    public static event Action<DamageEvent> PlayerHurt;
    /// materials from the pickaxe: who, material, amount, weak-point hit
    public static event Action<Combatant, BuildMat, int, bool> Harvest;

    public static void RaiseHit(DamageEvent e, IDamageable target, float dealt, bool killed) => Hit?.Invoke(e, target, dealt, killed);
    public static void RaiseEliminated(Combatant killer, Combatant victim, DamageEvent e) => Eliminated?.Invoke(killer, victim, e);
    public static void RaiseFeed(string text) => Feed?.Invoke(text);
    public static void RaisePlayerHurt(DamageEvent e) => PlayerHurt?.Invoke(e);
    public static void RaiseHarvest(Combatant who, BuildMat m, int n, bool crit) => Harvest?.Invoke(who, m, n, crit);
}

public static class GameLayers
{
    public const int Player = 9, Hitbox = 10;
    /// world geometry: everything except characters, hitboxes, Ignore Raycast and the water surfaces
    public static int Env => ~((1 << Player) | (1 << Hitbox) | (1 << 2) | (1 << 4));
    public static int Hitboxes => 1 << Hitbox;
    static readonly RaycastHit[] _buf = new RaycastHit[16];

    /// nearest hit along a ray: world geometry (triggers ignored) and character hitboxes (triggers),
    /// skipping the shooter's own hitbox
    public static bool Cast(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit, Combatant ignore = null, bool hitboxes = true)
    {
        bool a = Physics.Raycast(origin, dir, out var h1, maxDist, Env, QueryTriggerInteraction.Ignore);
        hit = h1;
        float best = a ? h1.distance : float.MaxValue;
        if (hitboxes)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, _buf, a ? h1.distance : maxDist, Hitboxes, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                if (_buf[i].distance >= best) continue;
                if (ignore != null && _buf[i].collider.GetComponentInParent<Combatant>() == ignore) continue;
                best = _buf[i].distance;
                hit = _buf[i];
                a = true;
            }
        }
        return a;
    }

    public static IDamageable Target(Collider c) => c != null && c.gameObject.layer == Hitbox ? c.GetComponentInParent<IDamageable>() : null;
}
