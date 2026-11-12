using System;
using UnityEngine;

/// The weapon system shared by the player and the bots: the held test 1 models on the right hand
/// (fitted by the editor), the item from the inventory, hitscan fire with spread and bloom, pump
/// shotgun pellets, rockets, grenades, pickaxe swings, magazines and reloads (reserve ammo by type),
/// and using heals and shield potions. Damage scales with rarity.
public class WeaponHands : MonoBehaviour
{
    public Animator anim;
    public GameObject[] held = new GameObject[0];             // WeaponTable.All order
    public GameObject[] heldConsumables = new GameObject[0];  // WeaponTable.Consumables order
    public Transform[] muzzles = new Transform[0];
    public Transform[] leftGrips = new Transform[0];
    public Transform rightHand;
    public GameObject rocketTip;

    [NonSerialized] public Combatant owner;
    [NonSerialized] public float bloom;
    [NonSerialized] public bool buildMode;          // building: weapons away, empty hands
    [NonSerialized] public bool stowed;             // swimming or mantling: weapons away, no firing
    [NonSerialized] public float spreadMul = 1f;    // crouching steadies the aim
    float _next, _reloadEnd = -1f, _actionEnd = -1f, _lastShot = -9f, _equipEnd, _useEnd = -1f, _useStart;
    float _pendingAt = -1f;
    Action _pending;
    Item _shown;
    bool _shownBuild;
    const int ActionLayer = 1;

    Inventory Inv => owner.inv;
    public Item Item => Inv.Current;
    public WeaponDef Def => Item != null && Item.IsWeapon ? Item.weapon : Item != null ? WeaponTable.UseDef : WeaponTable.Pickaxe;
    bool Away => buildMode || stowed;
    public Stance Stance => Away ? Stance.Unarmed : Def.stance;
    int WIndex => WeaponTable.IndexOf(Def.id);
    public Transform Muzzle => WIndex >= 0 && WIndex < muzzles.Length ? muzzles[WIndex] : null;
    public Transform LeftGrip => WIndex >= 0 && WIndex < leftGrips.Length ? leftGrips[WIndex] : null;
    public bool Reloading => _reloadEnd > 0f;
    public bool Using => _useEnd > 0f;
    public float ReloadProgress => Reloading ? 1f - (_reloadEnd - Time.time) / ReloadTime : 0f;
    public float UseProgress => Using ? Mathf.Clamp01((Time.time - _useStart) / (_useEnd - _useStart)) : 0f;
    public bool BusyFiring => Time.time - _lastShot < 0.5f;
    public bool Busy => Time.time < _actionEnd || Reloading || Using || Time.time < _equipEnd;
    float ReloadTime => Def.reload * WeaponTable.ReloadMult(Item != null ? Item.rarity : Rarity.Common);
    public int Reserve => Def.UsesAmmo ? Inv.Ammo(Def.ammo) : 0;
    public bool Empty => Item != null && Item.IsWeapon && Def.UsesAmmo && Item.mag <= 0;
    bool IsPlayer => owner != null && owner.isPlayer;

    /// cone of fire (degrees): hip or aimed base, plus movement, plus bloom from firing
    public float Spread(bool aiming, float motion)
    {
        var d = Def;
        float s = aiming ? d.spreadAds : d.spreadHip;
        s += motion * (aiming ? 0.6f : 1.4f) * (d.kind == FireKind.Hitscan && d.pellets == 1 ? 1f : 0.4f);
        return (s + bloom) * spreadMul;
    }

    void LateUpdate()
    {
        if (owner != null && owner.Alive && (_shown != Item || _shownBuild != Away)) { _shownBuild = Away; Refresh(); }
    }

    /// show the model of the selected item
    public void Refresh()
    {
        _shown = Item;
        int wi = Item != null && Item.IsWeapon && !Away ? WeaponTable.IndexOf(Item.weapon.id) : -1;
        int ci = Item != null && !Item.IsWeapon && !Away ? Array.FindIndex(WeaponTable.Consumables, c => c.id == Item.consumable.id) : -1;
        for (int k = 0; k < held.Length; k++) if (held[k]) held[k].SetActive(k == wi);
        for (int k = 0; k < heldConsumables.Length; k++) if (heldConsumables[k]) heldConsumables[k].SetActive(k == ci);
        if (rocketTip) rocketTip.SetActive(!Away && Item != null && Item.IsWeapon && Item.weapon.kind == FireKind.Rocket && Item.mag > 0);
    }

    public void Holster()
    {
        CancelAction();
        foreach (var h in held) if (h) h.SetActive(false);
        foreach (var h in heldConsumables) if (h) h.SetActive(false);
        _shown = null;
    }

    /// switch to a hotbar slot (0 = pickaxe); empty slots are skipped
    public void Select(int hotbar)
    {
        if (hotbar < 0 || hotbar > 5 || hotbar == Inv.selected) return;
        if (hotbar > 0 && Inv.slots[hotbar - 1] == null) return;
        CancelAction();
        Inv.selected = hotbar;
        bloom = 0f;
        _equipEnd = Time.time + 0.3f;
        Refresh();
        if (IsPlayer) Sfx.I?.Play2D("equip", 0.5f);
    }

    /// next or previous filled slot (mouse wheel)
    public void Cycle(int dir)
    {
        for (int k = 1; k <= 6; k++)
        {
            int h = ((Inv.selected + dir * k) % 6 + 6) % 6;
            if (h == 0 || Inv.slots[h - 1] != null) { Select(h); return; }
        }
    }

    public void CancelAction()
    {
        _reloadEnd = -1f;
        _useEnd = -1f;
        _pending = null;
        _pendingAt = -1f;
        _actionEnd = -1f;
        if (anim) anim.SetLayerWeight(ActionLayer, 0f);
    }

    void Update()
    {
        if (owner == null || !owner.Alive) return;
        float now = Time.time;
        var d = Def;
        bloom = Mathf.MoveTowards(bloom, 0f, Time.deltaTime * (d.bloomMax > 0 ? d.bloomMax * 1.6f : 4f));
        if (_pending != null && now >= _pendingAt) { var p = _pending; _pending = null; p(); }
        float w = anim.GetLayerWeight(ActionLayer);
        bool inAction = now < _actionEnd || Reloading || Using;
        anim.SetLayerWeight(ActionLayer, Mathf.MoveTowards(w, inAction ? 1f : 0f, Time.deltaTime * (inAction ? 10f : 4f)));
        if (Reloading && now >= _reloadEnd) FinishReload();
        if (Using && now >= _useEnd) FinishUse();
        if (d.CanReload && Item != null && Item.mag == 0 && Reserve > 0 && !Reloading && now >= _next && now >= _equipEnd) StartReload();
    }

    /// trigger handling; aimRay(spread) gives a ray through the aim (the player's crosshair or the bot's eyes)
    public bool TryFire(bool pressed, bool heldDown, bool aiming, float motion, Func<float, Ray> aimRay, Action<WeaponDef> onShot)
    {
        var d = Def;
        float now = Time.time;
        if (stowed || now < _equipEnd || Reloading || Using) return false;
        bool trigger = d.auto ? heldDown : pressed;
        if (!trigger || now < _next) return false;
        if (d.kind == FireKind.Consume) { StartUse(); return true; }
        if (d.UsesAmmo && Item.mag <= 0)
        {
            if (Reserve > 0) StartReload();
            else if (pressed && IsPlayer) Sfx.I?.Play2D("empty");
            return false;
        }
        switch (d.kind)
        {
            case FireKind.Hitscan: FireHitscan(d, aiming, motion, aimRay); break;
            case FireKind.Rocket: FireRocket(d, aiming, aimRay); break;
            case FireKind.Melee: Swing(d, aimRay); break;
            case FireKind.Grenade: Throw(d, aimRay); break;
        }
        _next = now + d.interval;
        _lastShot = now;
        onShot?.Invoke(d);
        return true;
    }

    float Mult => WeaponTable.DamageMult(Item != null ? Item.rarity : Rarity.Common);

    const float FarShot = 35f;

    void Sound(string id, float vol, Vector3 at)
    {
        if (IsPlayer) { Sfx.I?.Play2D(id, vol); ReplayRecorder.RecordSound(id, at, vol, 6f); }   // replays play it where it was fired
        else
        {
            // a gun heard from afar: its distant recording (the crack gone, the echo of the island instead)
            var cam = Camera.main;
            if (cam != null && Sfx.I != null && Sfx.I.Has(id + "_far") && (cam.transform.position - at).sqrMagnitude > FarShot * FarShot) id += "_far";
            Sfx.I?.PlayAt(id, at, vol, 6f);
        }
    }

    void FireHitscan(WeaponDef d, bool aiming, float motion, Func<float, Ray> aimRay)
    {
        Item.mag--;
        Physics.SyncTransforms();   // characters moved this frame; hitboxes must be where the bodies are
        var muzzle = Muzzle;
        float spread = Spread(aiming, motion);
        if (aiming && motion < 0.1f && bloom < 0.05f && d.pellets == 1) spread = d.spreadAds * 0.25f;   // first-shot accuracy
        bool anyHit = false, anyHead = false, anyKill = false;
        for (int p = 0; p < d.pellets; p++)
        {
            var ray = aimRay(spread);
            Vector3 end = ray.origin + ray.direction * d.range;
            bool hitSomething = GameLayers.Cast(ray.origin, ray.direction, d.range, out var hit, owner);
            if (hitSomething) end = hit.point;
            if (muzzle != null)
            {
                var md = end - muzzle.position;
                if (md.sqrMagnitude > 0.01f && GameLayers.Cast(muzzle.position, md.normalized, md.magnitude - 0.05f, out var block, owner))
                { hit = block; end = block.point; hitSomething = true; }
            }
            if (muzzle != null && (p == 0 || p % 3 == 0)) Fx.I.Tracer(muzzle.position, end, d.id == "sniper_rifle" ? 0.05f : 0.03f);
            if (!hitSomething) continue;
            var target = GameLayers.Target(hit.collider);
            if (target != null && target.Alive)
            {
                float dist = Vector3.Distance(ray.origin, hit.point);
                float fall = dist <= d.falloffStart ? 1f : Mathf.Lerp(1f, 0.45f, (dist - d.falloffStart) / Mathf.Max(1f, d.range - d.falloffStart));
                bool head = target.IsHead(hit.point, ray.direction);
                var e = new DamageEvent { amount = d.damage * Mult * fall * (head ? d.headMult : 1f), point = hit.point, dir = ray.direction, head = head, weapon = d, source = owner };
                float dealt = target.TakeDamage(e);
                bool killed = !target.Alive;
                if (IsPlayer) GameEvents.RaiseHit(e, target, dealt, killed);
                Fx.I.HitPuff(hit.point, ray.direction, head);
                anyHit = true; anyHead |= head; anyKill |= killed;
            }
            else
            {
                Fx.I.Impact(hit);
                var st = hit.collider.GetComponentInParent<Destructible>();
                if (st != null) st.Hit(d.damage * Mult, hit.point, ray.direction, owner, false);
            }
        }
        Fx.I.MuzzleFlash(muzzle, d.pellets > 1 ? 0.55f : d.id == "sniper_rifle" ? 0.5f : d.id == "pistol" ? 0.25f : 0.35f);
        Sound(d.id, d.id == "sniper_rifle" ? 0.9f : 0.7f, muzzle != null ? muzzle.position : transform.position);
        if (anyHit && IsPlayer) Sfx.I?.Play2D(anyKill ? "kill" : anyHead ? "headshot" : "hitmarker");
        bloom = Mathf.Min(d.bloomMax, bloom + d.bloom);
    }

    void FireRocket(WeaponDef d, bool aiming, Func<float, Ray> aimRay)
    {
        Item.mag--;
        var muzzle = Muzzle;
        var start = muzzle != null ? muzzle.position : owner.Chest;
        var ray = aimRay(aiming ? d.spreadAds : d.spreadHip);
        var target = GameLayers.Cast(ray.origin, ray.direction, d.range, out var hit, owner) ? hit.point : ray.origin + ray.direction * d.range;
        Rocket.Launch(start, (target - start).normalized, d, owner, Mult, rocketTip);
        if (rocketTip) rocketTip.SetActive(false);
        Fx.I.MuzzleFlash(muzzle, 0.7f);
        Sound("rocket_launcher", 0.9f, start);
    }

    void Swing(WeaponDef d, Func<float, Ray> aimRay)
    {
        PlayAction("Melee", 1.25f, 0.85f);
        Sound("swing", 0.6f, transform.position);
        _pending = () =>
        {
            Physics.SyncTransforms();
            var origin = owner.Chest + Vector3.up * 0.2f;
            var ray = aimRay(0f);
            var look = ray.direction;
            // swing at what the crosshair is on (the camera sits behind the shoulder), so weak points can be hit
            if (Physics.Raycast(ray, out var ch, 14f, GameLayers.Env, QueryTriggerInteraction.Ignore) && Vector3.Distance(ch.point, origin) < d.range + 0.6f)
                look = (ch.point - origin).normalized;
            var fwd = Vector3.ProjectOnPlane(look, Vector3.up).normalized;
            IDamageable best = null; Vector3 bestPoint = default; float bestD = 99f;
            foreach (var c in Physics.OverlapSphere(origin + fwd * 1.1f, 1.1f, GameLayers.Hitboxes, QueryTriggerInteraction.Collide))
            {
                var t = GameLayers.Target(c);
                if (t == null || !t.Alive || ReferenceEquals(t, owner)) continue;
                var p = c.ClosestPoint(origin);
                float dd = Vector3.Distance(origin, p);
                if (dd < bestD) { bestD = dd; best = t; bestPoint = p; }
            }
            if (best != null)
            {
                var e = new DamageEvent { amount = d.damage * Mult, point = bestPoint, dir = fwd, weapon = d, source = owner };
                float dealt = best.TakeDamage(e);
                if (IsPlayer) { GameEvents.RaiseHit(e, best, dealt, !best.Alive); Sfx.I?.Play2D(best.Alive ? "hitmarker" : "kill"); }
                Fx.I.HitPuff(bestPoint, fwd, false);
                Sound("melee_hit", 0.9f, bestPoint);
            }
            else if (Physics.Raycast(origin, look, out var hit, d.range + 0.8f, GameLayers.Env, QueryTriggerInteraction.Ignore))
            {
                // harvesting: 50 per swing on the level's parts and on builds, materials from the level
                var st = hit.collider.GetComponentInParent<Destructible>();
                if (st != null) st.Hit(50f, hit.point, look, owner, true, hit.collider);
                else { Fx.I.Impact(hit, false); Sound("melee_hit", 0.7f, hit.point); }
                if (IsPlayer) Camera.main?.GetComponent<ThirdPersonCamera>()?.Shake(0.08f);
            }
        };
        _pendingAt = Time.time + 0.32f;
    }

    void Throw(WeaponDef d, Func<float, Ray> aimRay)
    {
        PlayAction("Throw", 1.4f, 0.9f);
        int slot = Inv.selected;
        var item = Item;
        var mult = Mult;
        _pending = () =>
        {
            if (Inv.selected != slot || Inv.Current != item) return;
            var start = rightHand != null ? rightHand.position : owner.Chest;
            var dir = (aimRay(0f).direction + Vector3.up * 0.18f).normalized;
            Grenade.Throw(start, dir * d.speed, d, owner, mult, held[WeaponTable.IndexOf("grenade")]);
            Sound("throw", 0.7f, start);
            Inv.Consume(slot);
            if (Inv.Current == null) Inv.selected = 0;
            Refresh();
        };
        _pendingAt = Time.time + 0.45f;
    }

    void StartUse()
    {
        var c = Item.consumable;
        if (c == null) return;
        // no point using a full heal or shield
        if ((c.heal > 0f && owner.health >= c.healCap) || (c.shield > 0f && owner.shield >= c.shieldCap)) { if (IsPlayer) Sfx.I?.Play2D("empty"); return; }
        _useStart = Time.time;
        _useEnd = Time.time + c.useTime;
        if (c.id == "shield_potion") { anim.SetFloat("ActionSpeed", 1f); anim.CrossFadeInFixedTime("Drink", 0.15f, ActionLayer, 0f); }
    }

    void FinishUse()
    {
        _useEnd = -1f;
        var c = Item != null ? Item.consumable : null;
        if (c == null) return;
        if (c.action == "pad") LaunchPad.PlaceAhead(owner);
        if (c.action == "rift") Rift.Open(owner.Chest, 3f);
        if (c.heal > 0f) owner.Heal(c.heal, c.healCap);
        if (c.shield > 0f) owner.AddShield(c.shield, c.shieldCap);
        Inv.Consume(Inv.selected);
        if (Inv.Current == null) Inv.selected = 0;
        Refresh();
        if (IsPlayer) Sfx.I?.Play2D("pickup", 0.6f);
    }

    void PlayAction(string state, float speed, float duration)
    {
        anim.SetFloat("ActionSpeed", speed);
        anim.CrossFadeInFixedTime(state, 0.08f, ActionLayer, 0f);
        _actionEnd = Time.time + duration;
    }

    public void StartReload()
    {
        var d = Def;
        if (!d.CanReload || Reloading || Item.mag >= d.mag || Reserve <= 0) return;
        _reloadEnd = Time.time + ReloadTime;
        string state = d.stance == Stance.Pistol ? "Reload_Pistol" : "Reload_Rifle";
        float len = 3f;
        foreach (var c in anim.runtimeAnimatorController.animationClips)
            if (c.name == (d.stance == Stance.Pistol ? "P_Reload" : "R_Reload")) len = c.length;
        anim.SetFloat("ActionSpeed", len / ReloadTime);
        anim.CrossFadeInFixedTime(state, 0.15f, ActionLayer, 0f);
        Sound("reload", 0.6f, transform.position);
    }

    void FinishReload()
    {
        _reloadEnd = -1f;
        var d = Def;
        if (!d.UsesAmmo || Item == null) return;
        int take = Mathf.Min(d.mag - Item.mag, Reserve);
        Item.mag += take;
        Inv.ammo[(int)d.ammo] -= take;
        if (d.kind == FireKind.Rocket && rocketTip) rocketTip.SetActive(true);
    }
}
