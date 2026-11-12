using System;
using System.Collections.Generic;
using UnityEngine;

/// Fortnite inventory: the pickaxe plus five slots, and ammo by type.
[Serializable]
public class Inventory
{
    public static readonly Item PickaxeItem = Item.Weapon(WeaponTable.Pickaxe, Rarity.Epic);
    public readonly Item[] slots = new Item[5];
    public readonly int[] ammo = new int[6];
    public readonly int[] mats = new int[3];   // wood, brick, metal
    public int selected;                     // 0 = pickaxe, 1..5 = slots

    public Item Current => selected <= 0 ? PickaxeItem : slots[selected - 1];
    public Item this[int hotbar] => hotbar <= 0 ? PickaxeItem : slots[hotbar - 1];
    public int FreeSlot => Array.FindIndex(slots, s => s == null);
    public bool Full => FreeSlot < 0;

    public int Ammo(AmmoType t) => ammo[(int)t];
    public void AddAmmo(AmmoType t, int n) { if (t != AmmoType.None) ammo[(int)t] = Mathf.Min(999, ammo[(int)t] + n); }
    public void AddMats(BuildMat m, int n) => mats[(int)m] = Mathf.Clamp(mats[(int)m] + n, 0, BuildRules.MaxMats);

    /// put an item in: stacks merge, then the first free slot; returns how many units did not fit
    public int Add(Item it)
    {
        if (it.Stacks)
            foreach (var s in slots)
                if (s != null && s.Id == it.Id && s.mag < s.MaxStack)
                {
                    int take = Mathf.Min(it.mag, s.MaxStack - s.mag);
                    s.mag += take;
                    it.mag -= take;
                    if (it.mag <= 0) return 0;
                }
        int free = FreeSlot;
        if (free < 0) return it.Stacks ? it.mag : 1;
        slots[free] = it;
        return 0;
    }

    /// remove a used-up stack (grenades thrown, consumables used)
    public void Consume(int hotbar)
    {
        if (hotbar <= 0) return;
        var s = slots[hotbar - 1];
        if (s == null) return;
        s.mag--;
        if (s.mag <= 0) slots[hotbar - 1] = null;
    }

    public int Count(Func<Item, bool> f)
    {
        int n = 0;
        foreach (var s in slots) if (s != null && f(s)) n++;
        return n;
    }
}

/// Anyone in the match (the player and the bots): health and shield, the inventory, a hitbox, death
/// with a loot spill, and who eliminated whom.
public class Combatant : MonoBehaviour, IDamageable
{
    public static readonly List<Combatant> All = new List<Combatant>();

    public string displayName = "Player";
    public bool isPlayer;
    public float maxHealth = 100f, maxShield = 100f;
    /// everyone lands with a full shield: on health alone one shotgun blast or sniper body shot took the
    /// whole bar and fights were over in a hit (the user's complaint)
    public float startShield = 100f;
    public Animator anim;
    public WeaponHands hands;
    public float damageTakenMult = 1f;

    [NonSerialized] public float health = 100f, shield;
    [NonSerialized] public Inventory inv = new Inventory();
    [NonSerialized] public int kills;
    [NonSerialized] public Combatant lastAttacker;
    [NonSerialized] public float lastHitTime = -99f;
    [NonSerialized] public Vector3 lastHitFrom;
    [NonSerialized] public int placement;
    [NonSerialized] public bool inMatch;          // false while still in the bus
    Transform _head, _headTop;     // the base of the skull and Mixamo's HeadTop_End bone
    float _fullHitAt = -99f;       // when a hit last found this body at full health and shield
    CapsuleCollider _hitbox;

    /// match-wide numbers for the [Stats] log: weapon hits on anyone (each shotgun pellet counts), the
    /// headshots among them, eliminations, and eliminations of a full-health body by a single shot
    public static int Hits, HeadHits, Kills, OneShotKills;

    public bool Alive => health > 0f;
    public string DisplayName => displayName;
    /// the middle of the chest (the bots aim here): 69 % of the body height, lower on the outfits whose big
    /// heads sit on low head bones, where that height was the chin
    public Vector3 Chest => transform.position + Vector3.up * (_head == null ? _bodyH * 0.69f
        : Mathf.Clamp(_head.position.y - transform.position.y - 0.25f, _bodyH * 0.45f, _bodyH * 0.69f));

    /// the head as a ball from the base of the skull to the top of it, so the big heads of the stylized
    /// outfits are big targets and the small ones small; 14 cm above the head bone without a head-top bone
    public void HeadBall(out Vector3 c, out float r)
    {
        if (_headTop != null)
        {
            c = (_head.position + _headTop.position) * 0.5f;
            r = Mathf.Clamp(Vector3.Distance(_head.position, _headTop.position) * 0.5f, 0.1f, 0.25f);
        }
        else { c = _head.position + Vector3.up * 0.14f; r = 0.14f; }
    }
    float _bodyH = 1.8f;   // lower when crouching or sliding
    public Vector3 HeadPos => _head != null ? _head.position : transform.position + Vector3.up * 1.62f;
    public event Action<Combatant, DamageEvent> Died;

    void Awake()
    {
        if (anim == null) anim = GetComponentInChildren<Animator>();
        _head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
        var go = new GameObject("Hitbox") { layer = GameLayers.Hitbox };
        go.transform.SetParent(transform, false);
        _hitbox = go.AddComponent<CapsuleCollider>();
        _hitbox.isTrigger = true;
        // a collider that moves every frame needs a (kinematic) body: without one PhysX treats it as part
        // of the static level and reworks its static structures each time any of the 25 hitboxes moves
        var body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        _hitbox.center = new Vector3(0f, 0.92f, 0f);
        _hitbox.height = 1.84f;
        _hitbox.radius = 0.34f;
        health = maxHealth;
        shield = startShield;
        if (_head != null)
            foreach (var t in _head.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("HeadTop_End")) { _headTop = t; break; }
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() => All.Remove(this);

    public void SetHitbox(bool on) { if (_hitbox) _hitbox.enabled = on; }

    /// crouching and sliding make the target smaller
    public void SetHitboxHeight(float h)
    {
        _bodyH = h;
        if (!_hitbox) return;
        _hitbox.height = h + 0.04f;
        _hitbox.center = new Vector3(0f, h * 0.5f + 0.02f, 0f);
    }

    /// a headshot: the shot, after entering the body capsule at p, passes through the head ball (with 3 cm
    /// to spare). "Anything above the head bone less 10 cm" took in the shoulders across the whole capsule,
    /// and on the outfits with big heads on low head bones (Timmy's at 1.27 m) also the chest the bots aim at.
    public bool IsHead(Vector3 p, Vector3 dir)
    {
        if (_head == null) return false;
        HeadBall(out var c, out float r);
        if (p.y < c.y - r - 0.05f) return false;   // came in below the chin: the body is in the way
        float t = Mathf.Clamp(Vector3.Dot(c - p, dir), 0f, 0.8f);
        r += 0.03f;
        return (p + dir * t - c).sqrMagnitude < r * r;
    }

    public float TakeDamage(DamageEvent e)
    {
        if (!Alive || !inMatch) return 0f;
        if (e.weapon != null && !e.explosion) { Hits++; if (e.head) HeadHits++; }
        if (health >= maxHealth && shield >= startShield) _fullHitAt = Time.time;
        float amount = e.amount * (e.source != null && e.source != this ? damageTakenMult : 1f);
        float fromShield = e.storm || e.fall ? 0f : Mathf.Min(shield, amount);
        shield -= fromShield;
        float fromHealth = Mathf.Min(health, amount - fromShield);
        health -= fromHealth;
        if (e.source != null && e.source != this)
        {
            lastAttacker = e.source;
            lastHitTime = Time.time;
            lastHitFrom = e.source.transform.position;
        }
        if (isPlayer && (e.source != this || e.explosion)) GameEvents.RaisePlayerHurt(e);
        if (health <= 0f) Die(e);
        return fromShield + fromHealth;
    }

    public void Heal(float hp, float cap) => health = Mathf.Max(health, Mathf.Min(cap, health + hp));
    public void AddShield(float s, float cap) => shield = Mathf.Max(shield, Mathf.Min(cap, shield + s));

    void Die(DamageEvent e)
    {
        health = 0f;
        Kills++;
        if (e.weapon != null && Time.time - _fullHitAt < 0.05f) OneShotKills++;   // a shotgun's pellets land in one frame
        SetHitbox(false);
        var killer = e.source != null && e.source != this ? e.source : (Time.time - lastHitTime < 10f ? lastAttacker : null);
        if (killer != null) killer.kills++;
        // drop everything, like Fortnite
        WorldItem.SpillInventory(inv, transform.position + Vector3.up * 0.6f);
        if (hands != null) hands.Holster();
        if (anim != null)
        {
            anim.SetLayerWeight(1, 0f);
            anim.CrossFadeInFixedTime(UnityEngine.Random.value < 0.5f ? "DeathA" : "DeathB", 0.12f, 0);
        }
        var cc = GetComponent<CharacterController>();
        if (cc) cc.enabled = false;
        GameEvents.RaiseEliminated(killer, this, e);
        Died?.Invoke(this, e);
    }
}
