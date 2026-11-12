using System.Collections;
using UnityEngine;

/// The front-end video (-frontDemo -demoOut <dir>), recorded from the lobby on. Part 1: a scripted mouse
/// opens the locker and tries an emote, looks at the career page, plays an emote from the lobby's wheel,
/// goes through the settings (graphics, mouse, field of view, a key rebind and its reset), changes the
/// playlist and presses PLAY!; after matchmaking and the loading screen the player opens the emote wheel
/// and dances, a free camera with the HUD hidden flies round, the game menu comes up, then one easy bot is
/// beaten for the Victory Royale, replayed in slow motion, and the match summary counts up the XP. The
/// mouse aims at the controls by name (Hud.Spots). Part 2 (-frontPart 2): the player dances in the open,
/// a sweaty bot takes them out, the kill cam and the summary follow.
public class FrontShowcase : Showcase
{
    public BotController bot;
    public Vector3 botStart;
    public float botYaw;
    public int part = 1;

    static float W => Screen.width / (Screen.height / 1080f);
    Vector3 BotChest => bot.me.Chest;

    // ------------------------------------------------------------------ the scripted mouse

    IEnumerator Point(float x, float y, float time = 0.55f)
    {
        var from = UiPointer.pos;
        int n = Mathf.Max(1, Mathf.RoundToInt(time * 30f));
        for (int i = 1; i <= n; i++)
        {
            UiPointer.pos = Vector2.Lerp(from, new Vector2(x, y), Mathf.SmoothStep(0f, 1f, i / (float)n));
            yield return null;
        }
    }

    /// the scripted mouse to a control the HUD has drawn, by its id
    IEnumerator PointOn(string id, float time = 0.55f)
    {
        Vector2 p;
        while (!Hud.Spots.TryGetValue(id, out p)) yield return null;
        yield return Point(p.x, p.y, time);
    }

    IEnumerator ClickOn(string id, float time = 0.55f)
    {
        yield return PointOn(id, time);
        yield return Wait(0.15f);
        UiPointer.click = true;
        yield return null;
        UiPointer.click = false;
        yield return Wait(0.1f);
    }

    /// drag a slider's knob to x
    IEnumerator DragOn(string id, float x)
    {
        Vector2 p;
        while (!Hud.Spots.TryGetValue(id, out p)) yield return null;
        yield return Drag(p.x, x, p.y);
    }

    IEnumerator Drag(float x0, float x1, float y)
    {
        yield return Point(x0, y);
        UiPointer.held = true;
        yield return Point(x1, y, 0.8f);
        yield return Wait(0.1f);
        UiPointer.held = false;
    }

    // ------------------------------------------------------------------ the match

    /// aim down sights at the bot and fire while it can be hit, for a while
    IEnumerator ShootAt(float seconds)
    {
        float until = Time.time + seconds;
        _aim = true;
        while (Time.time < until && bot.me.Alive)
        {
            _look = BotChest;
            var eye = pc.cam.transform.position;
            bool clear = GameLayers.Cast(eye, (BotChest - eye).normalized, 80f, out var h, pc.me) && h.collider.GetComponentInParent<Combatant>() == bot.me;
            _hold = clear && Vector3.Angle(pc.cam.Forward, BotChest - eye) < 2.5f;
            yield return null;
        }
        _hold = false;
        _aim = false;
    }

    /// turn the camera by `degrees` of yaw over `seconds` (the dancer is seen from the front)
    IEnumerator Swing(float degrees, float seconds)
    {
        _look = null;
        int n = Mathf.Max(1, Mathf.RoundToInt(seconds * 30f));
        for (int i = 0; i < n; i++)
        {
            float k0 = Mathf.SmoothStep(0f, 1f, i / (float)n), k1 = Mathf.SmoothStep(0f, 1f, (i + 1) / (float)n);
            _lookDelta = new Vector2(degrees * (k1 - k0), 0f);
            yield return null;
        }
    }

    Vector3 TowardsBot()
    {
        var f = botStart - Feet; f.y = 0f;
        return f.sqrMagnitude > 0.01f ? f.normalized : pc.transform.forward;
    }

    protected override IEnumerator Script()
    {
        if (part == 2) { yield return DeathCut(); yield break; }

        // 1. the lobby: the character idles on the podium, then tries an emote
        UiPointer.pos = new Vector2(W * 0.5f + 300f, 620f);
        yield return Wait(2.6f);
        // the locker: the loadout, an emote tried on the character
        yield return ClickOn("tab_LOCKER", 0.8f);
        yield return Wait(1.0f);
        yield return PointOn("slot_0", 0.6f);
        yield return Wait(1.0f);
        // the outfits: try on Big Vegas, then back to Aj
        yield return ClickOn("slot_0", 0.3f);
        yield return Wait(0.7f);
        yield return PointOn("outfit_12", 0.6f);
        yield return Wait(0.5f);
        yield return ClickOn("outfit_10", 0.5f);
        yield return Wait(1.8f);
        yield return ClickOn("outfit_0", 0.6f);
        yield return Wait(1.0f);
        yield return ClickOn("slot_0", 0.6f);
        yield return Wait(0.5f);
        yield return ClickOn("emote_2", 0.6f);
        yield return Wait(3.2f);
        yield return ClickOn("tab_CAREER", 0.7f);
        yield return Wait(1.8f);
        yield return ClickOn("tab_PLAY", 0.6f);
        yield return Wait(0.8f);
        // the emote wheel works in the lobby too (B)
        Hud.LobbyWheel = true;
        yield return Wait(0.5f);
        yield return ClickOn("wheel_1", 0.6f);
        yield return Wait(2.6f);

        // the settings: graphics, mouse and field of view, a key rebind and its reset
        yield return ClickOn("settings", 0.8f);
        yield return Wait(0.9f);
        yield return ClickOn("quality<", 0.6f);
        yield return Wait(0.5f);
        yield return ClickOn("quality>", 0.4f);
        yield return Wait(0.5f);
        yield return ClickOn("set_tab_1", 0.6f);
        yield return Wait(0.7f);
        yield return DragOn("sens", 820f + 300f * Mathf.InverseLerp(0.5f, 6f, 3.2f));
        yield return Wait(0.4f);
        yield return DragOn("fov", 820f + 300f * Mathf.InverseLerp(60f, 100f, 80f));
        yield return Wait(0.6f);
        yield return ClickOn("set_tab_2", 0.6f);
        yield return Wait(0.8f);
        yield return ClickOn("set_tab_3", 0.5f);
        yield return Wait(0.8f);
        yield return ClickOn("key_Emote", 0.7f);
        yield return Wait(0.8f);
        UiPointer.key = KeyCode.H;
        yield return Wait(1.1f);
        yield return ClickOn("reset", 0.8f);
        yield return Wait(0.8f);
        yield return ClickOn("back", 0.8f);
        yield return Wait(0.8f);

        // PLAY!: the casual playlist, matchmaking, the loading screen, the match
        yield return ClickOn("change", 0.8f);
        yield return Wait(1.0f);
        yield return ClickOn("play", 0.7f);
        while (pc == null || Hud.Loading) yield return null;
        var f = TowardsBot();
        _look = Feet + f * 30f + Vector3.up * 1.2f;
        yield return Wait(1.2f);
        yield return WalkTo(Feet + f * 3f, 0.3f, 1.5f);
        yield return Wait(0.4f);

        // 4. the emote wheel: open it, the mouse towards RUNNING MAN, open it again to dance
        _look = null;
        _emote = 9;
        yield return Wait(0.7f);
        var toSlice = new Vector2(Mathf.Sin(60f * Mathf.Deg2Rad), Mathf.Cos(60f * Mathf.Deg2Rad)) * 2.2f;
        for (int i = 0; i < 12; i++) { _lookDelta = toSlice; yield return null; }
        yield return Wait(0.8f);
        _emote = 9;
        yield return Wait(0.6f);
        yield return Swing(180f, 3f);
        yield return Wait(2f);

        // 5. the free camera (F7) with the HUD hidden (F8): a crane shot round the dancer
        var cam = Camera.main.transform;
        CameraTools.SetFree(true);
        Hud.Hidden = true;
        var center = Feet + Vector3.up * 1.1f;
        var from = cam.position;
        float a0 = Mathf.Atan2(from.x - center.x, from.z - center.z);
        float r0 = new Vector2(from.x - center.x, from.z - center.z).magnitude, h0 = from.y - center.y;
        for (int i = 0, n = 165; i <= n + 15; i++)
        {
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, i / (float)n));
            float a = a0 + k * 2.6f, r = Mathf.Lerp(r0, 10f, k), h = Mathf.Lerp(h0, 6.5f, k);
            var p = center + new Vector3(Mathf.Sin(a) * r, h, Mathf.Cos(a) * r);
            p.y = Mathf.Max(p.y, Ground(p) + 0.6f);
            cam.SetPositionAndRotation(p, Quaternion.LookRotation(center - p));
            yield return null;
        }
        CameraTools.SetFree(false);
        Hud.Hidden = false;

        // 6. the rifle out (the dance stops), the pause menu; the bot arrives while the match is frozen
        _slot = 1;
        yield return Wait(0.8f);
        Hud.SetPaused(true);
        UiPointer.pos = new Vector2(W * 0.5f, 700f);
        yield return Wait(0.3f);
        yield return PointOn("menu_settings", 0.8f);
        yield return Wait(0.6f);
        bot.StartOnGround(botStart, botYaw);
        yield return ClickOn("resume", 0.5f);

        // 7. the fight: turn to the bot and take it out
        _lookRate = 3.5f;
        _look = BotChest;
        yield return Wait(1.4f);
        _lookRate = 6f;
        for (int k = 0; k < 8 && bot.me.Alive; k++) yield return ShootAt(2.5f);
        _look = null;

        // 8. Victory Royale (PlayerController plays the celebration), the slow-motion replay of the kill, the summary
        yield return Wait(4f);
        Hud.SummaryAt = -1f;
        ReplayPlayer.Play(6f, pc.me, 0.55f, "REPLAY", false);
        if (ReplayPlayer.I != null)
        {
            ReplayPlayer.I.scripted = true;
            ReplayPlayer.I.Orbit(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg - 20f, 12f, 5.5f, 6f);
        }
        yield return Until(() => !ReplayPlayer.Active, 20f);
        yield return Wait(1.2f);
        Hud.SummaryAt = UiClock.Now;
        UiPointer.pos = new Vector2(W * 0.5f, 860f);
        yield return Wait(3.8f);
        yield return PointOn("again", 0.8f);
        yield return Wait(1.5f);
        yield return Finish($"[FrontDemo] part 1: {Frames} frames, bot {(bot.me.Alive ? "alive" : "eliminated")}, player health {pc.me.health:0}, won {Match.I.PlayerWon}, account XP {Profile.xp}");
    }

    /// part 2: a dance in the open, a sweaty bot, the kill cam, the summary
    IEnumerator DeathCut()
    {
        UiPointer.pos = new Vector2(W * 0.5f, 860f);
        while (pc == null) yield return null;
        var f = TowardsBot();
        _look = Feet + f * 30f + Vector3.up * 1.2f;
        yield return Wait(1f);
        _emote = 3;                                                  // CHICKEN DANCE, right in the open
        yield return Wait(0.4f);
        yield return Swing(170f, 2.2f);
        // the bot comes in behind the camera (the replay starts after its jump from the bus)
        bot.StartOnGround(botStart, botYaw);
        yield return Until(() => !pc.me.Alive, 25f);
        yield return Until(() => Hud.SummaryOpen, 25f);
        yield return Wait(4.2f);
        yield return PointOn("lobby", 0.8f);
        yield return Wait(1.2f);
        yield return Finish($"[FrontDemo] part 2: {Frames} frames, player {(pc.me.Alive ? "alive" : "eliminated")} by {(pc.me.lastAttacker != null ? pc.me.lastAttacker.displayName : "-")}, placement {pc.me.placement}");
    }
}
