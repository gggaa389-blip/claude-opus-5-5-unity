using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// Pick-up rules shared by the player and the bots.
public static class Pickup
{
    /// take a ground item; when the inventory is full, swap it with the selected slot (if allowed)
    public static bool Take(Combatant who, WorldItem w, bool allowSwap)
    {
        if (w == null) return false;
        var inv = who.inv;
        if (w.IsMats)
        {
            inv.AddMats((BuildMat)w.matType, w.matCount);
            w.Remove();
            return true;
        }
        if (w.IsAmmo)
        {
            inv.AddAmmo(w.ammoType, w.ammoCount);
            w.Remove();
            return true;
        }
        int left = inv.Add(w.item);
        if (left == 0) { w.Remove(); return true; }
        if (w.item.Stacks) w.item.mag = left;
        if (!allowSwap) return false;
        int slot = inv.selected > 0 ? inv.selected : WorstSlot(inv);
        if (slot <= 0) return false;
        var old = inv.slots[slot - 1];
        inv.slots[slot - 1] = w.item;
        inv.selected = slot;
        var at = w.transform.position;
        w.Remove();
        if (old != null) WorldItem.Spawn(old, at + Vector3.right * 0.6f, who.transform.position + Vector3.up);
        return true;
    }

    /// the slot a bot gives up for something better: the weakest weapon, else a consumable
    public static int WorstSlot(Inventory inv)
    {
        int worst = -1; float score = float.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var s = inv.slots[i];
            float v = s == null ? -1f : Value(s);
            if (v < score) { score = v; worst = i + 1; }
        }
        return worst;
    }

    public static float Value(Item it)
    {
        if (it == null) return 0f;
        if (!it.IsWeapon) return 2f + (int)it.rarity * 0.2f;
        float b = it.weapon.id == "assault_rifle" ? 6f : it.weapon.id == "shotgun" ? 5.5f : it.weapon.id == "sniper_rifle" ? 4.5f
                : it.weapon.id == "pistol" ? 3.5f : it.weapon.id == "rocket_launcher" ? 4f : it.weapon.id == "grenade" ? 2.5f : 0f;
        return b + (int)it.rarity * 0.6f;
    }

    /// would a bot want this ground item?
    public static bool Useful(Combatant who, WorldItem w)
    {
        var inv = who.inv;
        if (w.IsMats) return true;
        if (w.IsAmmo)
        {
            foreach (var s in inv.slots) if (s != null && s.IsWeapon && s.weapon.ammo == w.ammoType && inv.Ammo(w.ammoType) < 120) return true;
            return inv.Ammo(w.ammoType) < 20 && inv.Count(s => s.IsWeapon) < 2;
        }
        var it = w.item;
        if (it.Stacks) { foreach (var s in inv.slots) if (s != null && s.Id == it.Id && s.mag < s.MaxStack) return true; }
        if (!inv.Full)
        {
            if (it.IsWeapon && it.weapon.kind != FireKind.Grenade)
                foreach (var s in inv.slots) if (s != null && s.IsWeapon && s.weapon.id == it.weapon.id && s.rarity >= it.rarity) return false;
            return true;
        }
        return Value(it) > Value(inv.slots[WorstSlot(inv) - 1]) + 0.5f;
    }
}

/// Decisions of a battle royale bot, shared by the bots and the player's autopilot (gameplay video):
/// sees enemies by line of sight, reacts after a delay, picks a weapon for the distance, fights while
/// strafing, loots what helps, heals, outruns the storm and later hunts the others. Movement follows
/// NavMesh paths; the body (BotController or AutoPilot) carries the outputs out.
public class BotBrain
{
    public readonly Combatant me;
    public float skill;
    readonly System.Random _rng;

    // outputs
    public Vector3 moveDir;
    public bool sprint, jump, fireHeld, firePressed, aim, reload, hasAim, holdStill, crouch;
    public Vector3 aimPoint;
    public int select = -1;
    public Component reach;
    public Combatant enemy;
    public string state = "";
    public static int HarvestTicks, HarvestSwings;   // for the demo logs
    public static readonly Dictionary<string, int> StateTicks = new Dictionary<string, int>();
    public bool buildWall;          // put a wall between us and whoever is shooting (yaw towards them)
    public float buildYaw;
    // build fights: pieces for the body to place (type, facing, camera pitch, cells ahead) and an edit
    public bool canBuildFight = true;
    public readonly List<(PieceType type, float yaw, float pitch, int ahead)> buildQueue = new List<(PieceType, float, float, int)>();
    public Destructible editTarget;
    public int editMask;
    bool _rushing, _rushPlaced, _rushFailed;
    int _rushStep;
    float _rushYaw, _rushBase, _rushUntil, _rushCd, _highUntil, _boxCd, _peekUntil, _peekNext;
    Destructible _peekWall, _doorWall;
    Combatant _foe;   // the last one seen, remembered while boxed up
    public int emote;             // 1..6 while dancing after an elimination
    int _killsSeen;
    float _emoteUntil;
    Destructible _harvest;
    // a target the swings never reach (a crate pressed against a house wall: the pickaxe meets the wall)
    // is given up after a few seconds without damage and not chosen again
    float _harvestHp, _harvestCheck;
    readonly HashSet<Destructible> _unreachable = new HashSet<Destructible>();
    float _buildCd;

    readonly NavMeshPath _np = new NavMeshPath();
    readonly List<Vector3> _corners = new List<Vector3>();
    int _ci;
    Vector3 _goal;
    bool _hasGoal;
    float _repathAt, _thinkAt, _reactAt, _seenAt = -99f, _burstFlip, _strafeFlip, _strafe = 1f, _wanderAt, _stuckCheck, _unstickUntil;
    bool _burstOn, _visible, _crouchFight;
    Vector3 _wander, _stuckPos, _unstickDir;
    Component _loot;
    float _lootUntil;
    // loot the bot cannot get to (on a floor the NavMesh does not reach, so the path ends right under it,
    // or behind something it walks into): no nearer in 4 s, it is given up and not chosen again. Before,
    // the bot stood under it and picked it again every 14 s, the most common "doing nothing" in a match
    readonly HashSet<Component> _lootBlocked = new HashSet<Component>();
    Component _lootWatched;
    float _lootBestD, _lootCheckAt;
    int _stuckCount;

    public BotBrain(Combatant me, float skill, int seed)
    {
        this.me = me;
        this.skill = skill;
        _rng = new System.Random(seed);
    }

    float R => (float)_rng.NextDouble();

    public void Tick(float now, Vector3 eye, Vector3 aimDir, float dt)
    {
        jump = false;
        reload = false;
        if (now >= _thinkAt)
        {
            Think(now, eye);
            _thinkAt = now + 0.2f;
            StateTicks.TryGetValue(state, out int n);
            StateTicks[state] = n + 1;
        }
        Steer(now);
        Shoot(now, eye, aimDir);
        WatchIdle(now);
    }

    // diagnostics for the [Stats] log: seconds a bot stood in one spot without firing, swinging or using
    // anything, by state, and the first long cases in detail
    public static readonly Dictionary<string, float> IdleSeconds = new Dictionary<string, float>();
    public static readonly List<string> IdleCases = new List<string>();
    Vector3 _idlePos;
    float _idleSince = -1f, _idleCheck;
    bool _acted, _idleLogged;

    void WatchIdle(float now)
    {
        _acted |= fireHeld || firePressed || me.hands.Using || state == "emote";
        if (now < _idleCheck) return;
        var pos = me.transform.position;
        if (Flat(pos - _idlePos).magnitude < 0.8f && !_acted && _idleCheck > 0f)
        {
            string key = state + (moveDir.sqrMagnitude > 0.01f ? " (walking into something)" : "");
            IdleSeconds.TryGetValue(key, out float s);
            IdleSeconds[key] = s + 2f;
            if (_idleSince < 0f) _idleSince = now - 2f;
            if (now - _idleSince >= 8f && !_idleLogged && IdleCases.Count < 40)
            {
                _idleLogged = true;
                string Dist(Component c) => c == null ? "none" : $"{c.name} {Vector3.Distance(pos, c.transform.position):F1} m (dy {c.transform.position.y - pos.y:F1})";
                IdleCases.Add($"{me.displayName} {key} at {Match.I.Now:F0} s, pos {pos}, hp {me.health:F0}+{me.shield:F0}, " +
                    $"enemy {(enemy == null ? "none" : $"{enemy.displayName} {Vector3.Distance(pos, enemy.transform.position):F0} m visible {_visible}")}, " +
                    $"goal {(_hasGoal ? $"{Vector3.Distance(pos, _goal):F1} m, corner {_ci}/{_corners.Count}" : "none")}, moveDir {moveDir.magnitude:F2}, holdStill {holdStill}, " +
                    $"loot {Dist(_loot)}, harvest {Dist(_harvest)}, weapon {me.hands.Def.id} mag {(me.inv.Current != null ? me.inv.Current.mag : -1)} reserve {me.hands.Reserve}, mats {me.inv.mats[0] + me.inv.mats[1] + me.inv.mats[2]}, queue {buildQueue.Count}");
            }
        }
        else { _idleSince = -1f; _idleLogged = false; }
        _idlePos = pos;
        _acted = false;
        _idleCheck = now + 2f;
    }

    // ---------------------------------------------------------------- thinking

    void Think(float now, Vector3 eye)
    {
        var pos = me.transform.position;
        var storm = Match.I.storm;
        holdStill = false;
        crouch = false;
        reach = null;

        // perception: nearest enemy in sight; whoever just hit us counts even unseen
        Combatant best = null; float bestD = 95f;
        _visible = false;
        foreach (var c in Combatant.All)
        {
            if (c == me || !c.Alive || !c.inMatch) continue;
            float d = Vector3.Distance(pos, c.transform.position);
            if (d > bestD) continue;
            if (Physics.Linecast(eye, c.Chest, GameLayers.Env, QueryTriggerInteraction.Ignore)) continue;
            best = c; bestD = d;
        }
        if (best != null)
        {
            if (best != enemy) { _reactAt = now + Mathf.Lerp(0.95f, 0.35f, skill) + R * 0.35f; _crouchFight = R < 0.25f + skill * 0.5f; }
            enemy = best; _foe = best; _seenAt = now; _visible = true;
        }
        else if (me.lastAttacker != null && me.lastAttacker.Alive && now - me.lastHitTime < 2.5f)
        {
            if (enemy != me.lastAttacker) _reactAt = now + 0.6f;
            enemy = me.lastAttacker;
        }
        else if (enemy != null && (now - _seenAt > 3.5f || !enemy.Alive)) enemy = null;

        ChooseWeapon(enemy != null ? Vector3.Distance(pos, enemy.transform.position) : -1f);
        if (_rushing) { RushThink(now, pos); return; }

        var mats = me.inv.mats;
        if (me.lastAttacker != null && me.lastAttacker.Alive && now - me.lastHitTime < 0.6f && now > _buildCd
            && Mathf.Max(mats[0], Mathf.Max(mats[1], mats[2])) >= BuildRules.Cost && R < 0.35f + skill * 0.5f)
        {
            var to = me.lastAttacker.transform.position - pos;
            buildYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            buildWall = true;
            _buildCd = now + Mathf.Lerp(7f, 3f, skill) + R * 2f;
        }

        // goals, in order: storm, fight, heal, loot, wander or hunt
        bool stormDanger = !storm.Inside(pos) || (storm.phase > 0 && !storm.InsideNext(pos, 4f) &&
                           (storm.shrinking || StormSeconds(now) < 22f));
        sprint = false;
        aim = false;
        if (stormDanger)
        {
            var c = new Vector3(storm.nextCenter.x, pos.y, storm.nextCenter.y);
            var away = pos - c; away.y = 0f;
            var target = c + (away.sqrMagnitude > 1f ? away.normalized : Vector3.forward) * storm.nextRadius * 0.45f;
            SetGoal(target, now);
            sprint = enemy == null;
            state = "storm";
            return;
        }
        // without a loaded gun, get one first; fight with the pickaxe only when someone is on top of us
        bool armed = false;
        foreach (var s in me.inv.slots)
            if (s != null && s.IsWeapon && s.weapon.kind != FireKind.Grenade && (s.mag > 0 || me.inv.Ammo(s.weapon.ammo) > 0)) armed = true;
        bool fight = enemy != null && (armed || Vector3.Distance(pos, enemy.transform.position) < 5f);
        if (enemy != null && !fight)
        {
            if (_loot == null || !Valid(_loot)) _loot = FindLoot(pos);
            if (_loot != null)
            {
                state = "arm";
                float ld = Vector3.Distance(pos, _loot.transform.position);
                if (ld < 2f) { reach = _loot; _loot = null; _hasGoal = false; return; }
                if (LootStalled(now, ld)) return;
                SetGoal(_loot.transform.position, now);
                sprint = true;
                return;
            }
        }
        // an elimination with nobody else in sight: a short dance now and then (as Fortnite players do)
        if (me.kills > _killsSeen)
        {
            _killsSeen = me.kills;
            if (R < 0.45f) { _emoteUntil = now + 3f + R * 2.5f; emote = 1 + _rng.Next(6); }
        }
        if (now < _emoteUntil && (enemy == null || !_visible)) { state = "emote"; holdStill = true; _hasGoal = false; return; }
        // build fights come first: box up to heal, then peek and shoot through a window edit
        if (canBuildFight && BoxFight(now, pos)) return;
        if (fight)
        {
            state = "fight";
            float d = Vector3.Distance(pos, enemy.transform.position);
            var def = me.hands.Def;
            float want = def.botRange;
            if (d > want * 1.5f || !_visible) SetGoal(enemy.transform.position, now);
            else _hasGoal = false;
            aim = d > 16f && def.CanAim && def.pellets == 1;
            crouch = _crouchFight && !_hasGoal && d > 12f;   // steadier shots from a crouch at range
            if (now < _highUntil) _hasGoal = false;          // keep the high ground just taken
            // take height on them: a ramp rush (walls in front, three ramps up)
            if (canBuildFight && _visible && now > _rushCd && skill > 0.45f && MaxMat >= 60 && d > 10f && d < 40f
                && (enemy.transform.position.y - pos.y > 1.5f || R < 0.03f + skill * 0.07f))
            {
                var to = enemy.transform.position - pos;
                _rushYaw = Mathf.Round(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg / 90f) * 90f;
                _rushing = true; _rushStep = 0; _rushPlaced = false; _rushFailed = false;
                RushThink(now, pos);
            }
            return;
        }
        if (TryHeal()) { state = "heal"; holdStill = true; crouch = true; _hasGoal = false; return; }
        if (me.hands.Def.CanReload && me.inv.Current.mag < me.hands.Def.mag && me.hands.Reserve > 0) reload = true;

        // armed but out of materials: pickaxe something before looting further (walls need 10)
        int matTotal = me.inv.mats[0] + me.inv.mats[1] + me.inv.mats[2];
        // sweaty bots stock up for build fights; the autopilot (no build fights) only needs a few walls'
        // worth, or the match video spends half a minute at one house wall
        int wantMats = skill > 0.7f && canBuildFight ? 120 : 30;
        if (armed && (matTotal < wantMats || (_harvest != null && _harvest.Alive && matTotal < wantMats * 3)) && GoHarvest(pos, now)) return;

        // loot
        if (_loot == null || now > _lootUntil || !Valid(_loot)) _loot = FindLoot(pos);
        if (_loot != null)
        {
            if (_lootUntil < now) _lootUntil = now + 14f;
            state = "loot";
            float d = Vector3.Distance(pos, _loot.transform.position);
            if (d < 2f) { reach = _loot; _loot = null; _hasGoal = false; return; }
            if (LootStalled(now, d)) return;
            SetGoal(_loot.transform.position, now);
            return;
        }
        // low on materials: pickaxe the nearest tree, rock or crate
        if (matTotal < 40 || (_harvest != null && matTotal < (canBuildFight ? 120 : 60))) { if (GoHarvest(pos, now)) return; }
        else _harvest = null;

        // later in the match go after the others (keeps the fights coming), else wander inside the eye
        if (storm.phase >= 2 || now - Match.I.startTime > 120f)
        {
            Combatant prey = null; float pd = 160f;
            foreach (var c in Combatant.All)
            {
                if (c == me || !c.Alive || !c.inMatch) continue;
                float d = Vector3.Distance(pos, c.transform.position);
                if (d < pd) { pd = d; prey = c; }
            }
            if (prey != null) { SetGoal(prey.transform.position, now); state = "hunt"; return; }
        }
        if (now > _wanderAt || Vector3.Distance(pos, _wander) < 4f)
        {
            _wanderAt = now + 10f + R * 8f;
            var c = storm.phase > 0 ? storm.nextCenter : storm.center;
            float r = storm.phase > 0 ? storm.nextRadius * 0.8f : 120f;
            float a = R * Mathf.PI * 2f, rr = Mathf.Sqrt(R) * r;
            _wander = new Vector3(c.x + Mathf.Cos(a) * rr, pos.y, c.y + Mathf.Sin(a) * rr);
        }
        SetGoal(_wander, now);
        state = "wander";
    }

    float StormSeconds(float now)
    {
        Match.I.storm.Status(Match.I.Now, out float s);
        return s;
    }

    /// walk up to the part to harvest (its nearest side, so big rocks work too) and swing at it
    bool GoHarvest(Vector3 pos, float now)
    {
        if (_harvest == null || !_harvest.Alive) { _harvest = FindHarvest(pos); _harvestCheck = 0f; }
        if (_harvest == null) return false;
        var col = _harvest.GetComponentInChildren<Collider>();
        var near = col != null ? col.bounds.ClosestPoint(pos) : _harvest.Center;
        var flat = new Vector3(near.x - pos.x, 0f, near.z - pos.z);
        state = "harvest";
        HarvestTicks++;
        bool close = flat.magnitude < 1.4f;
        if (!close)
        {
            var o = me.Chest;
            var to = _harvest.Center - o; to.y = 0f;
            close = Physics.Raycast(o, to.normalized, out var h, 2.4f, GameLayers.Env, QueryTriggerInteraction.Ignore) && h.collider.GetComponentInParent<Destructible>() == _harvest;
        }
        // the pickaxe out (overrides the gun ChooseWeapon picked this tick, or they would trade places
        // every think and the equip time would block every swing)
        if (close)
        {
            if (_harvestCheck <= 0f) { _harvestCheck = now + 3.5f; _harvestHp = _harvest.hp; }
            else if (now > _harvestCheck)
            {
                if (_harvest.hp >= _harvestHp) { _unreachable.Add(_harvest); _harvest = null; _harvestCheck = 0f; return false; }
                _harvestCheck = now + 3.5f; _harvestHp = _harvest.hp;
            }
            _hasGoal = false; holdStill = true; select = me.inv.selected != 0 ? 0 : -1; return true;
        }
        _harvestCheck = 0f;
        SetGoal(near, now);
        return true;
    }

    Destructible FindHarvest(Vector3 pos)
    {
        Destructible best = null; float bestD = 32f;
        foreach (var d in Destructible.Harvestables)
        {
            if (d == null || !d.Alive || _unreachable.Contains(d)) continue;
            var q = d.transform.position;
            float dd = Vector3.Distance(pos, q);
            if (dd < bestD && Mathf.Abs(q.y - pos.y) < 4f && (d.name.StartsWith("tree_") || d.name == "wood_crate" || d.name.StartsWith("rock_") || d.name == "fence_segment"))
            { bestD = dd; best = d; }
        }
        return best;
    }

    static bool Valid(Component c) => c != null && c.gameObject.activeInHierarchy && !(c is LootContainer lc && lc.opened);

    bool LootStalled(float now, float d)
    {
        if (_loot != _lootWatched) { _lootWatched = _loot; _lootBestD = d; _lootCheckAt = now + 4f; return false; }
        if (d < _lootBestD - 0.5f) { _lootBestD = d; _lootCheckAt = now + 4f; return false; }
        if (now < _lootCheckAt) return false;
        _lootBlocked.Add(_loot);
        _loot = null; _lootWatched = null; _hasGoal = false;
        return true;
    }

    Component FindLoot(Vector3 pos)
    {
        Component best = null; float bestD = 42f;
        bool fewGuns = me.inv.Count(s => s.IsWeapon && s.weapon.kind != FireKind.Grenade) < 2;
        foreach (var w in WorldItem.All)
        {
            if (w == null || !w.Settled || _lootBlocked.Contains(w)) continue;
            float d = Vector3.Distance(pos, w.transform.position) * (w.IsAmmo ? 1.3f : 1f) * (fewGuns && w.item != null && w.item.IsWeapon ? 0.5f : 1f);
            if (d < bestD && Mathf.Abs(w.transform.position.y - pos.y) < 6f && Pickup.Useful(me, w)) { bestD = d; best = w; }
        }
        // containers when short of guns or with nothing on the ground; a supply drop always, from 120 m
        foreach (var c in LootContainer.All)
            {
                if (c.opened || !(fewGuns || best == null || c.supply) || _lootBlocked.Contains(c)) continue;
                float d = Vector3.Distance(pos, c.transform.position) * (c.supply ? 0.35f : c.chest ? 0.8f : 1.2f);
                if (d < bestD && Mathf.Abs(c.transform.position.y - pos.y) < 6f) { bestD = d; best = c; }
            }
        return best;
    }

    int MaxMat => Mathf.Max(me.inv.mats[0], Mathf.Max(me.inv.mats[1], me.inv.mats[2]));

    bool NeedsHeal()
    {
        foreach (var s in me.inv.slots)
        {
            if (s == null || s.consumable == null) continue;
            var c = s.consumable;
            if ((c.heal > 0f && me.health < Mathf.Min(70f, c.healCap)) || (c.shield > 0f && me.shield < Mathf.Min(60f, c.shieldCap))) return true;
        }
        return false;
    }

    static Destructible BoxWall(Vector3 pos, float yaw) => BuildSystem.At(BuildSystem.Compute(PieceType.Wall, pos, yaw, 0f));
    static bool Boxed(Vector3 pos) { for (int k = 0; k < 4; k++) if (BoxWall(pos, k * 90f) == null) return false; return true; }

    /// low on health with someone around: a 1x1 box (four walls and a cone) to heal in. Inside the box it
    /// heals first, then cuts a window towards whoever it last saw, shoots through it and closes it again
    /// after a moment; with nobody around any more it cuts a door towards the safe zone and leaves.
    bool BoxFight(float now, Vector3 pos)
    {
        bool threatened = enemy != null || now - me.lastHitTime < 6f;
        bool heal = NeedsHeal();
        bool boxed = Boxed(pos);
        if (heal && threatened && !boxed && MaxMat >= 40 && buildQueue.Count == 0 && now > _boxCd)
        {
            for (int k = 0; k < 4; k++) buildQueue.Add((PieceType.Wall, k * 90f, 0f, 0));
            if (MaxMat >= 50) buildQueue.Add((PieceType.Cone, 0f, -30f, 0));
            _boxCd = now + 8f;
            _doorWall = null;
            state = "box"; holdStill = true; _hasGoal = false;
            return true;
        }
        if (!boxed) return false;
        if (heal)
        {
            if (_peekWall != null) { editTarget = _peekWall; editMask = 0; _peekWall = null; }
            if (!TryHeal()) return false;
            state = "heal"; holdStill = true; crouch = true; _hasGoal = false;
            return true;
        }
        if (_foe != null && _foe.Alive && now - _seenAt < 10f)
        {
            enemy = _foe;
            if (_peekWall == null && now > _peekNext)
            {
                var to = _foe.transform.position - pos;
                _peekWall = BoxWall(pos, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
                if (_peekWall != null) { editTarget = _peekWall; editMask = PieceEdit.Window; _peekUntil = now + 1.6f + R; }
            }
            else if (_peekWall != null && now > _peekUntil)
            {
                editTarget = _peekWall; editMask = 0; _peekWall = null;
                _peekNext = now + 1.4f + R * 1.6f;
            }
            state = "peek"; holdStill = true; _hasGoal = false;
            aim = me.hands.Def.CanAim && me.hands.Def.pellets == 1;
            return true;
        }
        if (_peekWall != null) { editTarget = _peekWall; editMask = 0; _peekWall = null; }
        if (_doorWall == null)
        {
            var c = Match.I.storm.phase > 0 ? Match.I.storm.nextCenter : Match.I.storm.center;
            _doorWall = BoxWall(pos, Mathf.Atan2(c.x - pos.x, c.y - pos.z) * Mathf.Rad2Deg);
            if (_doorWall != null) { editTarget = _doorWall; editMask = PieceEdit.Door; }
        }
        return false;
    }

    /// one ramp step at a time: a wall at the far edge of the next cell and a ramp into it, then up the ramp
    void RushThink(float now, Vector3 pos)
    {
        state = "rush"; aim = false; holdStill = false; _hasGoal = false;
        bool stop = enemy == null || !enemy.Alive || MaxMat < 20 || _rushFailed;
        if (!stop && !_rushPlaced && buildQueue.Count == 0)
        {
            buildQueue.Add((PieceType.Wall, _rushYaw, 0f, 1));
            buildQueue.Add((PieceType.Stairs, _rushYaw, 0f, 0));
            _rushPlaced = true; _rushUntil = now + 2.8f; _rushBase = float.NaN;
            return;
        }
        if (_rushPlaced && buildQueue.Count == 0 && ((!float.IsNaN(_rushBase) && pos.y >= _rushBase + 3.2f) || now > _rushUntil))
        {
            _rushStep++;
            _rushPlaced = false;
            if (_rushStep >= 3 || (enemy != null && enemy.transform.position.y < pos.y - 3f)) stop = true;
        }
        if (stop) { _rushing = false; _highUntil = now + 6f; _rushCd = now + 14f + R * 8f; state = "fight"; }
    }

    /// the body placed a queued piece (null if the spot was taken)
    public void OnPlaced(PieceType type, Destructible d)
    {
        if (!_rushing || type != PieceType.Stairs) return;
        if (d == null) _rushFailed = true; else _rushBase = d.baseY;
    }

    bool TryHeal()
    {
        var inv = me.inv;
        for (int i = 0; i < 5; i++)
        {
            var s = inv.slots[i];
            if (s == null || s.consumable == null) continue;
            var c = s.consumable;
            bool useful = (c.heal > 0f && me.health < Mathf.Min(70f, c.healCap)) || (c.shield > 0f && me.shield < Mathf.Min(60f, c.shieldCap));
            if (!useful) continue;
            select = i + 1;
            if (me.inv.selected == i + 1 && !me.hands.Using) firePressed = true;
            return true;
        }
        return false;
    }

    void ChooseWeapon(float dist)
    {
        var inv = me.inv;
        if (me.hands.Using) return;
        int pick = 0; float score = 0f;
        for (int i = 0; i < 5; i++)
        {
            var s = inv.slots[i];
            if (s == null || !s.IsWeapon) continue;
            var d = s.weapon;
            bool loaded = d.kind == FireKind.Grenade ? s.mag > 0 : s.mag > 0 || inv.Ammo(d.ammo) > 0;
            if (!loaded) continue;
            float v = Pickup.Value(s);
            if (dist >= 0f)
            {
                if (d.id == "shotgun") v += dist < 9f ? 6f : dist > 20f ? -4f : 0f;
                if (d.id == "sniper_rifle") v += dist > 55f ? 5f : dist < 20f ? -5f : 0f;
                if (d.id == "rocket_launcher") v += dist > 12f && dist < 45f && R < 0.2f ? 4f : -3f;
                if (d.id == "grenade") v += dist > 9f && dist < 25f && R < 0.08f ? 8f : -6f;
                if (d.id == "pistol" && dist > 45f) v -= 2f;
            }
            else if (d.id == "grenade" || d.id == "rocket_launcher") v -= 3f;
            if (v > score) { score = v; pick = i + 1; }
        }
        if (dist >= 0f && dist < 2.5f && pick == 0) pick = 0;
        if (pick != inv.selected) select = pick;
    }

    void SetGoal(Vector3 g, float now)
    {
        if (!_hasGoal || Vector3.Distance(g, _goal) > 3f || now > _repathAt)
        {
            _goal = g;
            _hasGoal = true;
            _repathAt = now + 2f;
            _corners.Clear();
            _ci = 0;
            var from = me.transform.position;
            if (NavMesh.SamplePosition(from, out var a, 4f, NavMesh.AllAreas) && NavMesh.SamplePosition(g, out var b, 8f, NavMesh.AllAreas)
                && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, _np) && _np.status != NavMeshPathStatus.PathInvalid)
            {
                _corners.AddRange(_np.corners);
                _ci = _corners.Count > 1 ? 1 : 0;
            }
            else _corners.Add(g);
        }
    }

    // ---------------------------------------------------------------- moving

    void Steer(float now)
    {
        var pos = me.transform.position;
        moveDir = Vector3.zero;
        if (holdStill) return;
        if (state == "rush")
        {
            // up the ramp just built, once it stands
            if (_rushPlaced && buildQueue.Count == 0) moveDir = Quaternion.Euler(0f, _rushYaw, 0f) * Vector3.forward;
            return;
        }
        if (now < _unstickUntil) { moveDir = _unstickDir; return; }
        if (enemy != null && _visible && state == "fight" && now < _highUntil) return;   // hold the high ground
        if (enemy != null && _visible && state == "fight")
        {
            // strafe around the enemy, keep the weapon's distance
            var to = enemy.transform.position - pos; to.y = 0f;
            float d = to.magnitude;
            var dir = d > 0.1f ? to / d : Vector3.forward;
            if (now > _strafeFlip) { _strafe = R < 0.5f ? -1f : 1f; _strafeFlip = now + 0.6f + R * 1.1f; if (R < 0.15f * skill + 0.05f) jump = true; }
            float want = me.hands.Def.botRange;
            float radial = d > want * 1.2f ? 0.8f : d < want * 0.6f ? -0.7f : 0f;
            moveDir = (Vector3.Cross(Vector3.up, dir) * _strafe * 0.8f + dir * radial);
            if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();
        }
        else if (_hasGoal && _corners.Count > 0)
        {
            while (_ci < _corners.Count - 1 && Flat(_corners[_ci] - pos).magnitude < 1.2f) _ci++;
            var to = Flat(_corners[Mathf.Min(_ci, _corners.Count - 1)] - pos);
            if (to.magnitude > 0.6f) moveDir = to.normalized;
            // the path ran out short of the goal (a partial path ends at the nearest reachable point,
            // often where the bot already stands): head straight for the goal and let the unsticking
            // below find a way, rather than stand there
            else if (Flat(_goal - pos).magnitude > 2f) moveDir = Flat(_goal - pos).normalized;
        }
        // stuck: hop and sidestep, further and more sideways each time it does not help
        if (now > _stuckCheck)
        {
            if (moveDir.sqrMagnitude > 0.1f && Vector3.Distance(pos, _stuckPos) < 0.6f)
            {
                _stuckCount++;
                jump = true;
                float side = _stuckCount > 2 ? 135f : 90f;
                _unstickDir = Quaternion.Euler(0f, R < 0.5f ? side : -side, 0f) * moveDir;
                _unstickUntil = now + 0.6f + 0.3f * Mathf.Min(_stuckCount, 4);
                _repathAt = 0f;
            }
            else _stuckCount = 0;
            _stuckPos = pos;
            _stuckCheck = now + 1.2f + 0.3f * Mathf.Min(_stuckCount, 4);
        }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // ---------------------------------------------------------------- shooting

    void Shoot(float now, Vector3 eye, Vector3 aimDir)
    {
        fireHeld = false;
        if (state != "heal") firePressed = false;
        hasAim = false;
        if (state == "harvest" && _harvest != null && _harvest.Alive && (enemy == null || !_visible))
        {
            var c = _harvest.Center;
            aimPoint = new Vector3(c.x, Mathf.Min(c.y, me.transform.position.y + 1.2f), c.z);
            hasAim = true;
            float herr = Vector3.Angle(Vector3.ProjectOnPlane(aimDir, Vector3.up), Vector3.ProjectOnPlane(aimPoint - eye, Vector3.up));
            firePressed = holdStill && herr < 20f && me.inv.selected == 0;
            if (firePressed) HarvestSwings++;
            return;
        }
        if (enemy == null || !enemy.Alive || state == "arm") return;
        var def = me.hands.Def;
        bool sniper = def.scope;
        aimPoint = sniper && skill > 0.6f ? enemy.HeadPos : enemy.Chest;
        hasAim = true;
        if (now < _reactAt || !_visible) return;
        float d = Vector3.Distance(eye, aimPoint);
        float err = Vector3.Angle(aimDir, aimPoint - eye);
        if (def.kind == FireKind.Melee)
        {
            firePressed = d < 2.4f;
            return;
        }
        float tol = def.kind == FireKind.Rocket ? 3f : def.kind == FireKind.Grenade ? 6f : def.pellets > 1 ? 8f : Mathf.Max(2.5f, 9f - d * 0.12f);
        if (err > tol) return;
        if (def.auto)
        {
            if (now > _burstFlip) { _burstOn = !_burstOn; _burstFlip = now + (_burstOn ? 0.45f + R * 0.5f : 0.25f + R * 0.35f); }
            fireHeld = _burstOn;
        }
        else firePressed = true;
    }
}
