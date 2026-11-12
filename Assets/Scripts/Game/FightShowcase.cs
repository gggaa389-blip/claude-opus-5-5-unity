using System.Collections;
using System.Linq;
using UnityEngine;

/// The build-fight video (-fightDemo -demoOut <dir>): the player and one sweaty bot, 24 m apart on open
/// flat ground found by the game, both with an assault rifle, a pump shotgun and materials. The player
/// (driven only through input) opens fire, puts up a wall, edits a window into it and shoots through it,
/// resets the edit, then takes a ramp towards the bot for height and shoots down at it; the bot reacts on its
/// own (walls under fire, a 1x1 box to heal in, window peeks, ramp rushes).
public class FightShowcase : Showcase
{
    public BotController bot;
    static Vector3 _start, _dir;
    static bool _found;

    // ------------------------------------------------------------------ choosing the place

    /// flat (within 1 m) and free of everything 8 m wide and 9 m high for 48 m along a grid axis
    static bool Open(Vector3 p, Vector3 d)
    {
        float g0 = Ground(p);
        if (g0 < 3f || (Water.Surface(p, out var wy) && wy > g0)) return false;
        for (int i = -2; i <= 11; i++)
        {
            var c = p + d * (4f * i);
            float g = Ground(c);
            if (Mathf.Abs(g - g0) > 1f || (Water.Surface(c, out wy) && wy > g)) return false;
            if (Physics.OverlapBox(new Vector3(c.x, g0 + 5.2f, c.z), new Vector3(4f, 4.5f, 4f), Quaternion.identity, GameLayers.Env, QueryTriggerInteraction.Ignore)
                .Any(col => !(col is TerrainCollider))) return false;
        }
        return true;
    }

    public static bool PickSpot(out Vector3 player, out Vector3 bot, out float yaw)
    {
        for (float x = -180f; x <= 180f && !_found; x += 8f)
            for (float z = -160f; z <= 180f && !_found; z += 8f)
                foreach (var d in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
                {
                    var p = new Vector3(Mathf.Floor(x / 4f) * 4f + 2f, 0f, Mathf.Floor(z / 4f) * 4f + 2f);   // a cell centre
                    if (!Open(p, d)) continue;
                    _start = p; _dir = d; _found = true;
                    break;
                }
        player = _start; player.y = Ground(_start) + 0.1f;
        bot = _start + _dir * 24f; bot.y = Ground(bot) + 0.1f;
        yaw = Mathf.Atan2(_dir.x, _dir.z) * Mathf.Rad2Deg;
        if (_found) Debug.Log($"[FightDemo] arena at {_start} towards {_dir}");
        return _found;
    }

    // ------------------------------------------------------------------ the script

    Vector3 BotChest => bot.me.Chest;

    /// aim down sights at the bot and fire while it can be hit, for a while
    IEnumerator ShootAt(float seconds, float spread = 0f)
    {
        float until = Time.time + seconds;
        _aim = true;
        while (Time.time < until && bot.me.Alive)
        {
            _look = BotChest + Random.insideUnitSphere * spread;
            var eye = pc.cam.transform.position;
            bool clear = GameLayers.Cast(eye, (BotChest - eye).normalized, 80f, out var h, pc.me) && h.collider.GetComponentInParent<Combatant>() == bot.me;
            _hold = clear && Vector3.Angle(pc.cam.Forward, BotChest - eye) < 2.5f;
            yield return null;
        }
        _hold = false;
        _aim = false;
    }

    IEnumerator Place(int piece)
    {
        _build = piece;
        yield return null;
        yield return Wait(0.25f);
        _tap = true;
        yield return Wait(0.3f);
    }

    protected override IEnumerator Script()
    {
        var f = _dir;
        _slot = 1;
        _look = BotChest;
        yield return Wait(1.2f);

        // 1. open fire
        yield return ShootAt(2.2f);

        // 2. a wall in front, a window edited into it, shots through the window
        _look = Feet + f * 20f + Vector3.up * 1.6f;
        yield return Wait(0.3f);
        yield return Place(0);
        _slot = 1;
        yield return Wait(0.5f);
        _look = Feet + f * 2.1f + Vector3.up * 2.0f;      // the wall's middle tile
        yield return Wait(0.5f);
        _edit = true;
        yield return Wait(0.35f);
        _tap = true;
        yield return Wait(0.35f);
        _edit = true;
        yield return Wait(0.3f);
        yield return ShootAt(3.5f);
        // 3. the edit reset: the wall is whole again (aimed at a tile still there, above the window)
        _look = Feet + f * 2.1f + Vector3.up * 3.4f;
        yield return Wait(0.4f);
        _edit = true;
        yield return Wait(0.3f);
        _aimTap = true;
        yield return Wait(0.5f);

        // 4. one cell to the side, a ramp towards the bot for height (one storey: from higher the window is
        // too steep a shot into the box), then shots from above as soon as it peeks
        var side = Vector3.Cross(Vector3.up, f);
        _look = Feet + side * 30f + Vector3.up * 1.5f;
        yield return Wait(0.4f);
        yield return WalkTo(Feet + side * BuildRules.Grid, 0.35f, 2.5f);
        _look = Feet + f * 30f + Vector3.up * 2.5f;
        yield return Wait(0.6f);
        for (int i = 0; i < 1; i++)
        {
            yield return Place(2);
            float baseY = pc.buildSlot.baseY;
            _move = new Vector2(0f, 1f);
            yield return Until(() => Feet.y >= baseY + 3.2f, 2.6f);
            _move = Vector2.zero;
        }
        _slot = 1;
        yield return Wait(0.4f);
        for (int k = 0; k < 6 && bot.me.Alive; k++) yield return ShootAt(2.5f);
        if (!bot.me.Alive)
        {
            _look = BotChest + Vector3.up * 0.5f;
            yield return Wait(3.5f);
        }
        yield return Finish($"[FightDemo] {Frames} frames, bot {(bot.me.Alive ? "alive" : "eliminated")}, player health {pc.me.health:0}, built by bot {BotController.PiecesBuilt}, bot edits {BotController.Edits}");
    }
}
