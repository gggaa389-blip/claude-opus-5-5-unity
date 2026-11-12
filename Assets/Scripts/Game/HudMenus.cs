using System.Collections.Generic;
using UnityEngine;

/// The pointer the menus answer to: the real mouse, or one a script moves for the videos (then the HUD
/// draws a cursor for it). Positions in the HUD's 1080p units, y down.
public static class UiPointer
{
    public static bool scripted;
    public static Vector2 pos;
    public static bool click, held;     // scripted: pressed this frame / held
    public static KeyCode key;          // scripted: a key press for the key bindings
    public static Vector2 Pos(float s) => scripted ? pos : new Vector2(Input.mousePosition.x / s, (Screen.height - Input.mousePosition.y) / s);
    public static bool Click => scripted ? click : Input.GetMouseButtonDown(0);
    public static bool Held => scripted ? held : Input.GetMouseButton(0);
}

/// The clock of the menus and the front end: real time, or the video's own frame clock while frames are
/// captured (then real time runs at whatever speed the frames are written).
public static class UiClock
{
    public static float Now => Time.captureFramerate > 0 ? Time.frameCount / (float)Time.captureFramerate : Time.unscaledTime;
    public static float Delta => Time.captureFramerate > 0 ? 1f / Time.captureFramerate : Time.unscaledDeltaTime;
}

/// The front end in Fortnite's style, drawn with the HUD's kit: the lobby (PLAY with the mode card and
/// the daily quests, LOCKER, CAREER), the game menu (Esc), the settings, the loading screen, the match
/// summary with its XP, the emote wheel, the replay and kill cam bar and the FPS counter.
public partial class Hud
{
    public static bool Paused, SettingsOpen, Hidden, GameMenu, LobbyWheel;
    public static float SummaryAt = -1f;
    public static bool SummaryOpen => SummaryAt > 0f && UiClock.Now >= SummaryAt;
    public static bool Loading => _loadStart > 0f;
    /// gameplay input is off while any of these is up
    public static bool MenuOpen => Paused || SettingsOpen || Lobby.I != null || SummaryOpen || Loading;
    /// the mouse is free while any of these is up
    public static bool CursorFree => Paused || SettingsOpen || Lobby.I != null || SummaryOpen;
    public const float LoadTime = 3.4f;
    static readonly Color Yellow = new Color(1f, 0.86f, 0.16f), Grey = new Color(0.66f, 0.72f, 0.84f);
    static float _loadStart = -1f, _loadEnd = -1f;
    static Texture _loadArt;
    static AudioSource _loadMusic;
    static int _tip;
    static readonly string[] Tips =
    {
        "BUILD A WALL THE MOMENT YOU TAKE FIRE: IT BUYS YOU TIME TO HEAL OR REPOSITION.",
        "SUPPLY DROPS FALL AT THE START OF EVERY STORM PHASE. THEY HOLD EPIC AND LEGENDARY WEAPONS.",
        "LAUNCH PADS AND RIFTS GET YOU BACK INTO THE AIR. THE GLIDER OPENS ON ITS OWN.",
        "EDIT A WINDOW INTO YOUR WALL (G) AND SHOOT THROUGH IT. RESET IT WITH RIGHT CLICK.",
        "THE PICKAXE HITS HARDER ON THE BLUE WEAK POINT. FARM MATERIALS BEFORE THE FIGHT.",
        "SLIDE OUT OF A SPRINT WITH CTRL. IT IS FASTER DOWNHILL.",
    };

    bool _click, _held;
    Vector2 _ptr;
    int _setTab;
    string _rebind, _descTitle, _desc;
    float _fps = 60f, _fpsT;
    int _fpsN;
    Texture2D _disk, _arrow;
    // the summary: its XP lines and the level before and after, worked out once
    List<Profile.XpLine> _xpLines;
    int _xpOld, _xpNew;
    static bool _summaryDone;

    /// the statics outlive a scene reload; a new match starts clean
    public static void ResetFrontEnd()
    {
        Paused = SettingsOpen = Hidden = GameMenu = LobbyWheel = OutfitPick = false;
        SummaryAt = -1f;
        _loadStart = _loadEnd = -1f;
        _summaryDone = false;
        Time.timeScale = 1f;
        AudioListener.pause = false;
        Sfx.windLevel = Sfx.stormLevel = 0f;
    }

    public static void SetPaused(bool p)
    {
        Paused = p;
        Time.timeScale = p || Loading ? 0f : 1f;
        AudioListener.pause = p;
    }

    /// the loading screen between the lobby and the match; the lobby music plays on under it
    public static void StartLoading(Texture art, AudioSource music)
    {
        _loadStart = UiClock.Now;
        _loadArt = art;
        _loadMusic = music;
        _tip = Random.Range(0, Tips.Length);
        GameMenu = LobbyWheel = false;
    }

    void UpdateLoading()
    {
        if (_loadEnd > 0f && UiClock.Now - _loadEnd > 0.6f) _loadEnd = -1f;
        if (!Loading) return;
        float t = UiClock.Now - _loadStart;
        Sfx.SetMusicVolume(_loadMusic, 0.55f * Mathf.Clamp01((LoadTime - t) / 1.2f));
        if (t < LoadTime || Match.I == null) return;
        _loadStart = -1f;
        _loadEnd = UiClock.Now;
        if (_loadMusic != null) Destroy(_loadMusic.gameObject);
        Time.timeScale = Paused ? 0f : 1f;
    }

    void CloseSettings()
    {
        SettingsOpen = false;
        _rebind = null;
        Settings.Save();
        if (pc != null && pc.cam != null) pc.cam.hipFov = Settings.Fov;
    }

    void OnGUI()
    {
        if (A == null) return;
        if (_st == null) _st = new GUIStyle(GUI.skin.label) { font = _font, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), wordWrap = false };
        if (_disk == null) { MakeTextures(); MakeKit(); }
        s = Screen.height / 1080f;
        float W = Screen.width / s, H = 1080f;
        var ev = Event.current;
        _ptr = UiPointer.Pos(s);
        _click = ev.type == EventType.Repaint && UiPointer.Click;
        _held = ev.type == EventType.Repaint && UiPointer.Held;
        if (_rebind != null) CaptureKey(ev);

        if (Loading) { DrawLoading(W, H); if (UiPointer.scripted) { } return; }

        // the settings and the game menu sit on top: the screens under them see no pointer
        var ptr = _ptr;
        bool click = _click, held = _held;
        bool over = SettingsOpen || GameMenu || (Paused && pc != null);
        if (over) { _ptr = new Vector2(-9999f, -9999f); _click = _held = false; }
        if (Lobby.I != null) DrawLobby(W, H);
        else if (pc != null && pc.cam != null && Match.I != null)
        {
            if (ReplayPlayer.Active) DrawReplay(W, H);
            else if (!Hidden && !SummaryOpen) { DrawMatchHud(); if (pc.wheelOpen) DrawWheel(W, H, pc.wheelHover, pc.wheel); }
            if (SummaryOpen && !ReplayPlayer.Active) DrawSummary(W, H);
        }
        _ptr = ptr; _click = click; _held = held;
        if (!SettingsOpen && (GameMenu || (Paused && pc != null && !SummaryOpen))) DrawGameMenu(W, H, Lobby.I == null);
        if (SettingsOpen) DrawSettings(W, H);
        if (_loadEnd > 0f) Box(0, 0, W, H, new Color(0f, 0f, 0f, 1f - Mathf.Clamp01((UiClock.Now - _loadEnd) / 0.6f)));
        if (Settings.ShowFps) DrawFps();
        if (UiPointer.scripted && CursorFree) Tex(_ptr.x, _ptr.y, 26, 36, _arrow, Color.white);
    }

    void MakeTextures()
    {
        const int N = 128;
        _disk = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float r = new Vector2(x + 0.5f - N * 0.5f, y + 0.5f - N * 0.5f).magnitude;
                px[y * N + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(N * 0.5f - r));
            }
        _disk.SetPixels(px); _disk.Apply();
        // a mouse arrow: white with a dark rim
        const int AW = 26, AH = 36;
        _arrow = new Texture2D(AW, AH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var ap = new Color[AW * AH];
        bool In(float x, float y) => y >= 0 && y <= 30 && x >= 0 && x <= y * 0.62f && !(y > 22 && x > 11 - (y - 22) * 0.3f && x < 4 + (y - 22) * 1.4f && x > 5);
        for (int y = 0; y < AH; y++)
            for (int x = 0; x < AW; x++)
            {
                float fy = AH - 1 - y;
                bool inside = In(x, fy), rim = !inside && (In(x - 1.5f, fy) || In(x + 1.5f, fy) || In(x, fy - 1.5f) || In(x, fy + 1.5f));
                ap[y * AW + x] = inside ? Color.white : rim ? new Color(0.05f, 0.05f, 0.1f, 1f) : new Color(0, 0, 0, 0);
            }
        _arrow.SetPixels(ap); _arrow.Apply();
    }

    void CaptureKey(Event ev)
    {
        var k = KeyCode.None;
        if (ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None) k = ev.keyCode;
        if (UiPointer.scripted && UiPointer.key != KeyCode.None && ev.type == EventType.Repaint) { k = UiPointer.key; UiPointer.key = KeyCode.None; }
        if (k == KeyCode.None) return;
        if (k != KeyCode.Escape) Settings.SetKey(_rebind, k);
        _rebind = null;
    }

    bool Hover(float x, float y, float w, float h) => _ptr.x >= x && _ptr.x <= x + w && _ptr.y >= y && _ptr.y <= y + h;

    static string Clock(float seconds) => $"{(int)(seconds / 60f)}:{(int)seconds % 60:00}";

    // ---------------------------------------------------------------- lobby

    void DrawLobby(float W, float H)
    {
        var L = Lobby.I;
        float t = UiClock.Now;
        Profile.EnsureLoaded();
        // the scene darkens towards the top and the bottom, as behind Fortnite's bars
        Fade(0, 0, W, 190, new Color(0f, 0.02f, 0.1f, 0.78f), 0);
        Fade(0, H - 300, W, 300, new Color(0f, 0.02f, 0.1f, 0.72f), 1);
        Fade(0, 0, 360, H, new Color(0f, 0.02f, 0.1f, L.tab == 0 ? 0.25f : 0.6f), 2);

        // top bar: the tabs on the left, the account on the right
        Text(36, 46, "ISLAND ROYALE", 30, Yellow, 0, 2f);
        string[] tabs = { "PLAY", "LOCKER", "CAREER" };
        float x = 300f;
        for (int i = 0; i < tabs.Length; i++)
        {
            float w = TextWidth(tabs[i], 28) + 46f;
            bool on = L.tab == i;
            if (Hit("tab_" + tabs[i], x, 20, w, 52, out bool hv) && !L.matchmaking) { L.tab = i; Sfx.I?.Play2D("ui_click", 0.6f); }
            if (on) RBox(x, 20, w, 52, new Color(1f, 1f, 1f, 0.95f), 8f);
            else if (hv) RBox(x, 20, w, 52, new Color(1f, 1f, 1f, 0.15f), 8f);
            Text(x + w * 0.5f, 47, tabs[i], 28, on ? Ink : hv ? Color.white : White70, 1, on ? 0f : 1.5f);
            x += w + 10f;
        }
        float rx = W - 56f;
        if (IconButton("menu", rx, 46, 24, IcMenu, Color.white)) { GameMenu = true; }
        rx -= 62f;
        if (IconButton("settings", rx, 46, 24, IcGear, Color.white)) { SettingsOpen = true; _setTab = 0; }
        rx -= 62f;
        IconButton("friends", rx, 46, 24, IcPeople, Color.white);
        Text(rx + 18, 64, "1", 16, Green, 1, 1f);
        rx -= 150f;
        VBucks(rx, 46, 0);
        rx -= 230f;
        LevelBadge(rx, 22, 48, Profile.Level);
        Text(rx + 60, 36, "LEVEL", 18, White70, 0, 1f);
        RBox(rx + 60, 54, 140, 10, new Color(1f, 1f, 1f, 0.2f), 5f);
        RBox(rx + 60, 54, 140 * Profile.LevelProgress, 10, Cyan, 5f);

        if (L.tab == 0) DrawLobbyPlay(W, H, L, t);
        else if (L.tab == 1) DrawLocker(W, H, L);
        else DrawCareer(W, H);

        // key hints along the bottom
        float hx = W * 0.5f - 160f;
        hx += KeyHint(hx, H - 34, "B", "EMOTE");
        KeyHint(hx, H - 34, "ESC", "MENU");
        if (LobbyWheel) DrawLobbyWheel(W, H, L);
    }

    void DrawLobbyPlay(float W, float H, Lobby L, float t)
    {
        // the name plate under the character, the empty party slots beside it
        if (L.ScreenOf(L.feet, out var fp))
        {
            float pw = 290f, px = fp.x - pw * 0.5f, py = fp.y + 20f;
            RBox(px, py, pw, 58, new Color(0f, 0.03f, 0.14f, 0.8f), 8f);
            ROutline(px, py, pw, 58, new Color(1f, 1f, 1f, 0.15f), 1.5f, 8f);
            LevelBadge(px + 8, py + 8, 42, Profile.Level);
            Text(px + 62, py + 21, "PLAYER", 26, Color.white, 0, 1.5f);
            Tex(px + 62, py + 37, 10, 10, _disk, Green);
            Text(px + 78, py + 42, L.matchmaking ? "MATCHMAKING" : "READY", 16, Green, 0, 1f);
        }
        for (int i = 0; i < L.ghosts.Length; i++)
            if (L.ScreenOf(L.ghosts[i] + Vector3.up * 1.05f, out var gp))
            {
                bool hv = Hover(gp.x - 34, gp.y - 34, 68, 68);
                Tex(gp.x - 34, gp.y - 34, 68, 68, _disk, new Color(0.02f, 0.06f, 0.2f, hv ? 0.85f : 0.6f));
                Tex(gp.x - 34, gp.y - 34, 68, 68, _ring, new Color(1f, 1f, 1f, hv ? 0.9f : 0.45f));
                Icon(gp.x, gp.y, IcAdd, 28, Color.white);
                if (hv) Text(gp.x, gp.y + 52, "INVITE A FRIEND", 18, White70, 1, 1f);
            }

        // the daily quests and the level, bottom left
        float qx = 40f, qy = H - 360f;
        Panel(qx, qy, 520, 250, 0.78f);
        Icon(qx + 30, qy + 32, IcStar, 24, Yellow);
        Text(qx + 54, qy + 32, "DAILY QUESTS", 26, Color.white, 0, 1.5f);
        Text(qx + 500, qy + 32, "RESETS AT MIDNIGHT", 15, White70, 2, 0f);
        for (int i = 0; i < Profile.Quests.Length; i++)
        {
            var q = Profile.Quests[i];
            int p = Profile.questProgress[i];
            float y = qy + 78 + i * 58;
            bool done = p >= q.target;
            Text(qx + 24, y, q.text, 19, done ? Green : Color.white, 0, 1f);
            Text(qx + 496, y, $"+{q.xp} XP", 17, Yellow, 2, 1f);
            RBox(qx + 24, y + 18, 380, 9, new Color(1f, 1f, 1f, 0.15f), 4f);
            RBox(qx + 24, y + 18, 380 * Mathf.Clamp01(p / (float)q.target), 9, done ? Green : Cyan, 4f);
            Text(qx + 496, y + 22, $"{p} / {q.target}", 15, White70, 2, 0f);
        }

        // the mode card and PLAY!, bottom right
        float cx = W - 500f, cw = 460f, cy = H - 470f;
        var p0 = Profile.Playlists[Profile.playlist];
        RBox(cx - 3, cy - 3, cw + 6, 236, new Color(1f, 1f, 1f, 0.25f), 12f);
        if (L.thumb != null && Event.current.type == EventType.Repaint)
            GUI.DrawTexture(R(cx, cy, cw, 230), L.thumb, ScaleMode.ScaleAndCrop, false, 0f, Color.white, 0f, 10f * s);
        Fade(cx, cy + 90, cw, 140, new Color(0f, 0.02f, 0.12f, 0.92f), 1);
        Text(cx + 22, cy + 148, "BATTLE ROYALE", 44, Color.white, 0, 2f);
        Text(cx + 22, cy + 192, p0.name, 26, Yellow, 0, 1.5f);
        Text(cx + 22 + TextWidth(p0.name, 26) + 14, cy + 193, $"{L.bots} BOT{(L.bots == 1 ? "" : "S")}  /  {p0.skill}", 18, White70, 0, 1f);
        if (!L.matchmaking && DarkButton("change", cx + cw - 150, cy + 14, 136, 44, "CHANGE", 20))
        {
            Profile.playlist = (Profile.playlist + 1) % Profile.Playlists.Length;
            Profile.Save();
        }
        float by = H - 200f;
        if (!L.matchmaking)
        {
            if (YellowButton("play", cx, by, cw, 112, "PLAY!", 68)) L.Play();
        }
        else
        {
            // matchmaking, then joining: the status where the button was, and CANCEL
            float e = L.MmElapsed;
            Panel(cx, by, cw, 112, 0.9f, 10f);
            string dots = new string('.', 1 + (int)(t * 2f) % 3);
            Text(cx + 26, by + 36, (L.joining ? "JOINING SERVER" : "MATCHMAKING") + dots, 32, Yellow, 0, 1.5f);
            Text(cx + 26, by + 78, L.joining ? "MATCH FOUND" : $"PLAYERS  {Mathf.Min(L.bots + 1, 1 + (int)(e * 8f))} / {L.bots + 1}", 20, Color.white, 0, 1f);
            Text(cx + cw - 26, by + 36, Clock(L.joining ? L.mmTime + e : e), 28, Color.white, 2, 1.5f);
            if (!L.joining && DarkButton("cancel", cx + cw - 156, by + 58, 132, 42, "CANCEL", 20)) L.Cancel();
            // and a status chip under the tabs, as Fortnite does
            RBox(300, 86, 330, 40, new Color(0f, 0.03f, 0.14f, 0.85f), 8f);
            Text(318, 106, (L.joining ? "JOINING" : "MATCHMAKING") + "  " + Clock(e), 20, Yellow, 0, 1f);
        }
    }

    /// the locker shows the outfit tiles instead of the emotes (the OUTFIT slot switches)
    public static bool OutfitPick;

    void DrawLocker(float W, float H, Lobby L)
    {
        Text(80, 150, "LOCKER", 64, Color.white, 0, 2.5f);
        Text(84, 214, "CHARACTER", 24, White70, 0, 1f);
        string[] slots = { "OUTFIT", "BACK BLING", "HARVESTING TOOL", "GLIDER" };
        int nOut = A.outfitNames != null ? A.outfitNames.Length : 0;
        int cur = nOut > 0 ? Mathf.Clamp(Profile.outfit, 0, nOut - 1) : 0;
        int curRar = nOut > 0 && A.outfitRarity != null ? A.outfitRarity[cur] : 2;
        int[] rar = { curRar, -1, 3, 2 };
        float ts = 150f, gap = 18f, y0 = 236f;
        string info = null, infoName = null;
        int infoRar = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            float x = 80f + i * (ts + gap);
            bool slotClicked = Hit("slot_" + i, x, y0, ts, ts, out bool hv);
            if (i == 0 && slotClicked && nOut > 0) { OutfitPick = !OutfitPick; Sfx.I?.Play2D("ui_click", 0.6f); }
            if (rar[i] < 0)
            {
                RBox(x, y0, ts, ts, new Color(0f, 0.03f, 0.14f, 0.6f), 6f);
                ROutline(x, y0, ts, ts, new Color(1f, 1f, 1f, 0.2f), 2f, 6f);
                Icon(x + ts * 0.5f, y0 + ts * 0.45f, IcAdd, 40, White70);
            }
            else
            {
                RarityTile(x, y0, ts, ts, rar[i]);
                // the outfit worn: its portrait from the build (as on the outfit tiles), else the lobby's snapshot
                var worn = nOut > 0 && A.outfitIcons != null && cur < A.outfitIcons.Length ? A.outfitIcons[cur] : null;
                if (i == 0 && worn != null) Tex(x + 6, y0 + 4, ts - 12, ts - 32, worn, Color.white);
                else if (i == 0 && L.portrait != null && Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(R(x + 6, y0 + 6, ts - 12, ts - 40), L.portrait, ScaleMode.ScaleAndCrop, true, 0f, Color.white, 0f, 4f * s);
                if (i == 2) Tex(x + 14, y0 + 10, ts - 28, ts - 50, A.weaponIcons != null ? A.weaponIcons[Mathf.Max(0, WeaponTable.IndexOf("pickaxe"))] : null, Color.white);
                if (i == 3) Icon(x + ts * 0.5f, y0 + ts * 0.42f, "", 64, Color.white);
            }
            if (i == 0 && OutfitPick) ROutline(x - 3, y0 - 3, ts + 6, ts + 6, Yellow, 4f, 8f);
            else if (hv) ROutline(x - 3, y0 - 3, ts + 6, ts + 6, Color.white, 3f, 8f);
            Text(x + ts * 0.5f, y0 + ts - 16, slots[i], 15, Color.white, 1, 1f);
            if (hv) { infoName = i == 0 ? (nOut > 0 ? A.outfitNames[cur] : "ISLANDER") : i == 2 ? "PICKAXE" : i == 3 ? "GLIDER" : "EMPTY"; infoRar = Mathf.Max(0, rar[i]); info = slots[i]; }
        }
        if (OutfitPick && nOut > 0)
        {
            DrawOutfits(L, nOut, cur, ref infoName, ref infoRar, ref info);
            InfoPanel(infoName, infoRar, info, "PICK AN OUTFIT. CLICK THE OUTFIT SLOT AGAIN FOR THE EMOTES.");
            return;
        }
        Text(84, 430, "EMOTES", 24, White70, 0, 1f);
        int[] erar = { 2, 3, 4, 3, 1, 2 };
        float es = 132f;
        for (int i = 0; i < GameAssets.Emotes.Length; i++)
        {
            float x = 80f + (i % 3) * (es + gap), y = 452f + (i / 3) * (es + gap + 10f);
            bool clicked = Hit("emote_" + i, x, y, es, es, out bool hv);
            RarityTile(x, y, es, es, erar[i]);
            Icon(x + es * 0.5f, y + es * 0.4f, IcMusic, 48, new Color(1f, 1f, 1f, 0.92f));
            Text(x + es * 0.5f, y + es - 18, GameAssets.EmoteLabels[i], 15, Color.white, 1, 1f);
            if (L.emote == i) ROutline(x - 3, y - 3, es + 6, es + 6, Yellow, 4f, 8f);
            else if (hv) ROutline(x - 3, y - 3, es + 6, es + 6, Color.white, 3f, 8f);
            if (clicked) { L.PlayEmote(i); Sfx.I?.Play2D("ui_click", 0.6f); }
            if (hv) { infoName = GameAssets.EmoteLabels[i]; infoRar = erar[i]; info = "EMOTE"; }
        }
        InfoPanel(infoName, infoRar, info, "CLICK AN EMOTE TO TRY IT. IN A MATCH, PRESS THE EMOTE KEY (B) FOR THE WHEEL.");
    }

    /// what the hovered item is, else a hint
    void InfoPanel(string infoName, int infoRar, string info, string hint)
    {
        if (infoName != null)
        {
            string[] rn = { "COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY" };
            Panel(80, 806, 470, 112, 0.85f);
            Text(104, 844, infoName, 36, Color.white, 0, 1.5f);
            Text(104, 886, rn[infoRar] + "  " + info, 20, RarityIn[infoRar], 0, 1f);
        }
        else Body(84, 816, 470, hint, 17, White70);
    }

    /// the outfit tiles: five a row, the portrait over the rarity colours, the one worn outlined in yellow
    void DrawOutfits(Lobby L, int n, int cur, ref string infoName, ref int infoRar, ref string info)
    {
        Text(84, 430, "OUTFITS", 24, White70, 0, 1f);
        const float os = 104f, og = 12f;
        for (int i = 0; i < n; i++)
        {
            float x = 80f + (i % 5) * (os + og), y = 452f + (i / 5) * (os + og);
            bool clicked = Hit("outfit_" + i, x, y, os, os, out bool hv);
            int r = A.outfitRarity != null && i < A.outfitRarity.Length ? A.outfitRarity[i] : 1;
            RarityTile(x, y, os, os, r);
            var icon = A.outfitIcons != null && i < A.outfitIcons.Length ? A.outfitIcons[i] : null;
            if (icon != null) Tex(x + 4, y + 4, os - 8, os - 26, icon, Color.white);
            Text(x + os * 0.5f, y + os - 12, A.outfitNames[i], 13, Color.white, 1, 1f);
            if (i == cur) ROutline(x - 3, y - 3, os + 6, os + 6, Yellow, 4f, 8f);
            else if (hv) ROutline(x - 3, y - 3, os + 6, os + 6, Color.white, 3f, 8f);
            if (clicked && i != cur) { L.SetOutfit(i); Sfx.I?.Play2D("ui_click", 0.6f); }
            if (hv) { infoName = A.outfitNames[i]; infoRar = r; info = "OUTFIT"; }
        }
    }

    void DrawCareer(float W, float H)
    {
        Text(80, 150, "CAREER", 64, Color.white, 0, 2.5f);
        Text(84, 214, "LIFETIME STATS  /  BATTLE ROYALE", 24, White70, 0, 1f);
        float winRate = Profile.matches > 0 ? Profile.wins / (float)Profile.matches : 0f;
        var tiles = new (string, string)[]
        {
            ("LEVEL", Profile.Level.ToString()), ("MATCHES PLAYED", Profile.matches.ToString()), ("VICTORY ROYALES", Profile.wins.ToString()),
            ("WIN RATE", $"{winRate * 100f:0.#}%"), ("ELIMINATIONS", Profile.kills.ToString()), ("ELIMS / MATCH", Profile.matches > 0 ? (Profile.kills / (float)Profile.matches).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "0"),
            ("TOP 10", Profile.top10.ToString()), ("DAMAGE DEALT", Profile.damage.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)), ("TIME PLAYED", $"{(int)(Profile.secondsPlayed / 3600f)}H {(int)(Profile.secondsPlayed / 60f) % 60}M"),
        };
        for (int i = 0; i < tiles.Length; i++)
        {
            float x = 80f + (i % 3) * 250f, y = 250f + (i / 3) * 150f;
            Panel(x, y, 236, 136, 0.82f, 8f);
            RBox(x, y, 6, 136, Yellow, 3f);
            Text(x + 24, y + 38, tiles[i].Item1, 18, Grey, 0, 1f);
            Text(x + 24, y + 92, tiles[i].Item2, 50, Color.white, 0, 2f);
        }
    }

    void DrawLobbyWheel(float W, float H, Lobby L)
    {
        var c = new Vector2(W * 0.5f, H * 0.5f);
        var d = _ptr - c;
        int hover = d.magnitude > 60f ? Mathf.FloorToInt(Mathf.Repeat(Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg + 30f, 360f) / 60f) % 6 : -1;
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad;
            Spots["wheel_" + i] = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * 190f;
        }
        DrawWheel(W, H, hover, Vector2.ClampMagnitude(d, 120f));
        if (_click && hover >= 0) { _click = false; LobbyWheel = false; L.PlayEmote(hover); Sfx.I?.Play2D("ui_click", 0.6f); }
        else if (_click) { _click = false; LobbyWheel = false; }
    }

    // ---------------------------------------------------------------- the game menu (Esc)

    void DrawGameMenu(float W, float H, bool inMatch)
    {
        float pw = 560f, px = W - pw;
        Box(0, 0, W, H, new Color(0f, 0.01f, 0.05f, 0.35f));
        Fade(px - 200, 0, 200, H, new Color(0.01f, 0.04f, 0.14f, 0.7f), 3);
        Box(px, 0, pw, H, new Color(0.01f, 0.04f, 0.14f, 0.94f));
        Fade(px, 0, pw, 300, new Color(NavyLight.r, NavyLight.g, NavyLight.b, 0.6f), 0);
        LevelBadge(px + 44, 60, 64, Profile.Level);
        Text(px + 126, 80, "PLAYER", 36, Color.white, 0, 1.5f);
        Text(px + 126, 112, inMatch ? (Paused ? "MATCH PAUSED" : "IN MATCH") : "IN LOBBY", 18, inMatch ? Yellow : Green, 0, 1f);
        var items = new List<(string id, string glyph, string label)>();
        if (inMatch) items.Add(("resume", IcPlay, "RESUME"));
        items.Add(("menu_settings", IcGear, "SETTINGS"));
        if (!inMatch) items.Add(("menu_career", IcTrophy, "CAREER"));
        if (inMatch) items.Add(("menu_lobby", IcBack, "RETURN TO LOBBY"));
        items.Add(("menu_quit", IcExit, "QUIT GAME"));
        float y = 190f;
        foreach (var it in items)
        {
            bool clicked = Hit(it.id, px + 40, y, pw - 80, 72, out bool hv);
            RBox(px + 40, y, pw - 80, 72, hv ? new Color(1f, 1f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.06f), 8f);
            Icon(px + 84, y + 36, it.glyph, 28, hv ? Ink : Color.white);
            Text(px + 124, y + 37, it.label, 30, hv ? Ink : Color.white, 0, hv ? 0f : 1.5f);
            y += 86f;
            if (!clicked) continue;
            Sfx.I?.Play2D("ui_click", 0.6f);
            switch (it.id)
            {
                case "resume": SetPaused(false); break;
                case "menu_settings": SettingsOpen = true; _setTab = 0; break;
                case "menu_career": GameMenu = false; if (Lobby.I != null) Lobby.I.tab = 2; break;
                case "menu_lobby": GameBootstrap.Restart(true); break;
                case "menu_quit": Application.Quit(); break;
            }
        }
        if (inMatch && Match.I != null && pc != null)
        {
            float iy = H - 180f;
            Text(px + 44, iy, "BATTLE ROYALE  /  " + Profile.Playlists[Profile.playlist].name, 22, Grey, 0, 1f);
            Icon(px + 56, iy + 46, IcPeople, 22, Color.white);
            Text(px + 80, iy + 46, $"{Match.I.Alive} LEFT", 24, Color.white, 0, 1f);
            Icon(px + 216, iy + 46, IcStar, 22, Color.white);
            Text(px + 240, iy + 46, $"{Me.kills} ELIMINATIONS", 24, Color.white, 0, 1f);
        }
        KeyHint(px + 44, H - 50, "ESC", inMatch ? "RESUME" : "CLOSE");
        if (!inMatch && _click && !Hover(px, 0, pw, H)) { _click = false; GameMenu = false; }
    }

    // ---------------------------------------------------------------- settings

    static readonly string[] SettingTabs = { "VIDEO", "GAME", "AUDIO", "CONTROLS" };
    static readonly string[] SettingGlyphs = { IcMonitor, IcGear, IcVolume, IcKeyboard };

    /// a settings row: its label on the left; hovering it shows its description on the right
    void Row(float y, string label, string desc)
    {
        bool hv = _ptr.y > y - 30 && _ptr.y < y + 30 && _ptr.x > 100 && _ptr.x < 1300;
        if (hv) { RBox(100, y - 30, 1200, 60, new Color(1f, 1f, 1f, 0.06f), 6f); _descTitle = label; _desc = desc; }
        Text(130, y, label, 26, Color.white, 0, 1f);
    }

    void Section(float y, string title)
    {
        Text(110, y, title, 20, Cyan, 0, 0f);
        Box(110, y + 18, 1190, 2, new Color(1f, 1f, 1f, 0.12f));
    }

    void DrawSettings(float W, float H)
    {
        Box(0, 0, W, H, new Color(0.01f, 0.04f, 0.13f, 0.97f));
        Fade(0, 0, W, 260, new Color(NavyLight.r, NavyLight.g, NavyLight.b, 0.55f), 0);
        // the tab icons, centred at the top
        float tw = 96f, tx0 = W * 0.5f - SettingTabs.Length * (tw + 12f) * 0.5f;
        for (int i = 0; i < SettingTabs.Length; i++)
        {
            float x = tx0 + i * (tw + 12f);
            bool on = _setTab == i;
            bool clicked = Hit("set_tab_" + i, x, 26, tw, 70, out bool hv);
            RBox(x, 26, tw, 70, on ? new Color(1f, 1f, 1f, 0.95f) : hv ? new Color(1f, 1f, 1f, 0.15f) : new Color(1f, 1f, 1f, 0.05f), 8f);
            Icon(x + tw * 0.5f, 61, SettingGlyphs[i], 34, on ? Ink : Color.white);
            if (clicked) { _setTab = i; _rebind = null; Sfx.I?.Play2D("ui_click", 0.5f); }
        }
        Text(110, 160, SettingTabs[_setTab], 60, Color.white, 0, 2f);
        _descTitle = null; _desc = null;
        float y = 250f, row = 70f;
        switch (_setTab)
        {
            case 0:
                Section(y, "DISPLAY"); y += 56f;
                Row(y, "WINDOW MODE", "FULLSCREEN TAKES THE WHOLE DISPLAY. WINDOWED FULLSCREEN IS A BORDERLESS WINDOW THAT IS QUICK TO ALT-TAB OUT OF. APPLIED WITH APPLY.");
                Settings.WindowMode = Selector("winmode", 820, y, 420, Settings.WindowMode, Settings.WindowModes); y += row;
                var rn = Settings.ResolutionNames;
                if (Settings.ResIndex < 0 || Settings.ResIndex >= rn.Length) Settings.ResIndex = System.Array.IndexOf(Settings.Resolutions, new Vector2Int(Screen.width, Screen.height));
                Row(y, "RESOLUTION", "THE GAME'S RESOLUTION IN PIXELS. APPLIED WITH APPLY.");
                Settings.ResIndex = Selector("res", 820, y, 420, Mathf.Max(0, Settings.ResIndex), rn); y += row;
                Row(y, "VSYNC", "WAITS FOR THE DISPLAY BETWEEN FRAMES: NO TEARING, BUT A LITTLE MORE INPUT LAG.");
                Settings.VSync = Selector("vsync", 820, y, 420, Settings.VSync ? 1 : 0, Settings.OnOff) == 1; y += row;
                Row(y, "FRAME RATE LIMIT", "THE MOST FRAMES A SECOND THE GAME DRAWS.");
                Settings.FrameLimit = Selector("fps", 820, y, 420, Settings.FrameLimit, Settings.FrameLimitNames); y += row + 20f;
                Section(y, "GRAPHICS"); y += 56f;
                Row(y, "QUALITY PRESETS", "LOW AND MEDIUM SHORTEN THE SHADOWS, LOWER THE DETAIL OF FAR OBJECTS AND TURN OFF ANTI-ALIASING.");
                Settings.Quality = Selector("quality", 820, y, 420, Settings.Quality, Settings.QualityNames); y += row;
                Row(y, "SHOW FPS", "SHOWS THE FRAME RATE IN THE BOTTOM LEFT CORNER.");
                Settings.ShowFps = Selector("showfps", 820, y, 420, Settings.ShowFps ? 1 : 0, Settings.OnOff) == 1;
                break;
            case 1:
                Section(y, "MOUSE"); y += 56f;
                Row(y, "MOUSE SENSITIVITY", "HOW FAR THE CAMERA TURNS FOR A MOVE OF THE MOUSE.");
                Settings.Sensitivity = NumSlider("sens", 820, y, 300, Settings.Sensitivity, 0.5f, 6f, "{0:0.0}"); y += row;
                Row(y, "AIM DOWN SIGHTS SENSITIVITY", "THE SAME WHILE AIMING DOWN SIGHTS. LOWER IS STEADIER FOR LONG SHOTS.");
                Settings.AdsSensitivity = NumSlider("ads", 820, y, 300, Settings.AdsSensitivity, 0.2f, 1.5f, "{0:0.00}"); y += row;
                Row(y, "INVERT VIEW", "MOVING THE MOUSE UP LOOKS DOWN.");
                Settings.InvertY = Selector("invert", 820, y, 420, Settings.InvertY ? 1 : 0, Settings.OnOff) == 1; y += row + 20f;
                Section(y, "CAMERA"); y += 56f;
                Row(y, "FIELD OF VIEW", "HOW WIDE THE CAMERA SEES. WIDER SHOWS MORE AROUND YOU, NARROWER MAKES FAR THINGS BIGGER.");
                Settings.Fov = Mathf.Round(NumSlider("fov", 820, y, 300, Settings.Fov, 60f, 100f, "{0:0}"));
                break;
            case 2:
                Section(y, "VOLUME"); y += 56f;
                Row(y, "MASTER VOLUME", "THE VOLUME OF EVERYTHING.");
                Settings.Volume = NumSlider("volume", 820, y, 300, Settings.Volume, 0f, 1f, "{0:P0}"); y += row;
                Row(y, "MUSIC VOLUME", "THE LOBBY MUSIC AND THE EMOTE BEATS.");
                Settings.MusicVolume = NumSlider("music", 820, y, 300, Settings.MusicVolume, 0f, 1f, "{0:P0}");
                AudioListener.volume = Settings.Volume;
                break;
            default:
                // key bindings in two columns; click one, then press the new key (Esc keeps the old one)
                var acts = Settings.Actions;
                for (int i = 0; i < acts.Length; i++)
                {
                    float colX = i < 9 ? 110f : 720f, ry = 250f + (i % 9) * 64f;
                    bool waiting = _rebind == acts[i].action;
                    bool hv = Hover(colX, ry - 28, 580, 56);
                    if (hv) { _descTitle = acts[i].label; _desc = "CLICK THE KEY, THEN PRESS THE NEW ONE. ESC KEEPS THE OLD KEY. A KEY ALREADY IN USE SWAPS WITH IT."; }
                    RBox(colX, ry - 28, 580, 56, new Color(1f, 1f, 1f, hv ? 0.08f : 0.03f), 6f);
                    Text(colX + 20, ry, acts[i].label, 22, Color.white, 0, 1f);
                    if (DarkButton("key_" + acts[i].action, colX + 340, ry - 22, 220, 44, waiting ? "PRESS A KEY" : KeyName(Settings.Key(acts[i].action)), 20, waiting)) _rebind = acts[i].action;
                }
                break;
        }
        // the description of the hovered setting
        float dx = W - 560f;
        if (_descTitle != null)
        {
            Panel(dx, 250, 480, 300, 0.8f);
            Text(dx + 28, 290, _descTitle, 30, Color.white, 0, 1f);
            Body(dx + 28, 320, 424, _desc, 18, White70);
        }
        // the bottom bar
        Box(0, H - 96, W, 96, new Color(0f, 0.02f, 0.08f, 0.8f));
        if (Hit("back", 40, H - 76, 200, 56, out _)) { CloseSettings(); return; }
        KeyHint(60, H - 48, "ESC", "BACK");
        if (DarkButton("reset", W - 560, H - 78, 240, 60, "RESET", 26, false, IcRefresh))
        {
            if (_setTab == 3) Settings.ResetKeys();
            else if (_setTab == 1) { Settings.Sensitivity = 2.2f; Settings.AdsSensitivity = 0.7f; Settings.Fov = 72f; Settings.InvertY = false; }
            else if (_setTab == 0) { Settings.Quality = 2; Settings.ShowFps = false; Settings.VSync = false; Settings.FrameLimit = 0; }
            else { Settings.Volume = 0.8f; Settings.MusicVolume = 0.8f; }
        }
        if (YellowButton("apply", W - 300, H - 78, 260, 60, "APPLY", 30)) { Settings.Save(); Settings.ApplyDisplay(); }
    }

    static string KeyName(KeyCode k)
    {
        switch (k)
        {
            case KeyCode.LeftShift: return "LEFT SHIFT";
            case KeyCode.LeftControl: return "LEFT CTRL";
            case KeyCode.Space: return "SPACE";
            case KeyCode.None: return "-";
            default: return k.ToString().ToUpperInvariant();
        }
    }

    // ---------------------------------------------------------------- loading

    void DrawLoading(float W, float H)
    {
        float t = UiClock.Now - _loadStart, k = Mathf.Clamp01(t / LoadTime);
        Box(0, 0, W, H, Color.black);
        if (_loadArt != null && Event.current.type == EventType.Repaint)
        {
            // a slow push-in on the island
            float z = 1f + 0.04f * k;
            GUI.DrawTexture(R(W * 0.5f * (1f - z), H * 0.5f * (1f - z), W * z, H * z), _loadArt, ScaleMode.ScaleAndCrop);
        }
        Fade(0, H - 420, W, 420, new Color(0f, 0.02f, 0.1f, 0.92f), 1);
        Fade(0, 0, W, 160, new Color(0f, 0.02f, 0.1f, 0.6f), 0);
        Text(40, 50, "ISLAND ROYALE", 30, Yellow, 0, 2f);
        Text(80, H - 290, "BATTLE ROYALE", 90, Color.white, 0, 3f);
        var pl = Profile.Playlists[Profile.playlist];
        int nb = Mathf.Max(0, Combatant.All.Count - 1);
        Text(86, H - 214, $"{pl.name}  /  {nb} BOT{(nb == 1 ? "" : "S")}  /  {pl.skill}", 28, Yellow, 0, 1.5f);
        Icon(96, H - 150, IcInfo, 22, Cyan);
        Body(120, H - 166, 1100, "TIP: " + Tips[_tip], 20, Color.white);
        // the progress bar along the bottom
        float bx = 80f, bw = W - 160f, by = H - 70f;
        float shown = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k * 1.08f));
        RBox(bx, by, bw, 10, new Color(1f, 1f, 1f, 0.18f), 5f);
        RBox(bx, by, bw * shown, 10, Yellow, 5f);
        Text(bx, by - 26, "LOADING" + new string('.', 1 + (int)(t * 2f) % 3), 24, Color.white, 0, 1f);
        Text(bx + bw, by - 26, $"{Mathf.RoundToInt(shown * 100f)}%", 24, Color.white, 2, 1f);
    }

    // ---------------------------------------------------------------- end of match

    void DrawSummary(float W, float H)
    {
        var m = Match.I;
        bool won = m.PlayerWon;
        int players = Combatant.All.Count;
        if (!_summaryDone)
        {
            // the match goes into the account once
            _summaryDone = true;
            Profile.EnsureLoaded();
            _xpLines = Profile.MatchXp(Me.placement, Me.kills, players, MatchStats.survived, MatchStats.damageDealt);
            int total = 0;
            foreach (var l in _xpLines) total += l.xp;
            _xpOld = Profile.xp;
            Profile.AddMatch(Me.placement, Me.kills, players, MatchStats.survived, MatchStats.damageDealt, total);
            _xpNew = _xpOld + total;
        }
        float t = UiClock.Now - SummaryAt;
        Box(0, 0, W, H, new Color(0.01f, 0.03f, 0.12f, 0.9f));
        Fade(0, 0, W, 300, new Color(NavyLight.r, NavyLight.g, NavyLight.b, 0.5f), 0);

        // left: the placement
        Text(90, 80, "MATCH SUMMARY", 28, Grey, 0, 1.5f);
        Text(80, 240, $"#{Me.placement}", 200, won ? Yellow : Color.white, 0, 4f);
        Text(90, 390, won ? "VICTORY ROYALE" : "ELIMINATED", 54, won ? Yellow : new Color(1f, 0.5f, 0.45f), 0, 2.5f);
        Text(92, 440, $"OUT OF {players} PLAYERS", 24, Grey, 0, 1f);
        if (!won && Me.lastAttacker != null) Text(92, 476, "ELIMINATED BY " + Me.lastAttacker.displayName.ToUpperInvariant(), 22, White70, 0, 1f);

        // middle: the XP, line by line, then the level bar
        float px = Mathf.Max(560f, W * 0.5f - 400f), pw = 520f, py = 150f;
        Panel(px, py, pw, 620, 0.85f);
        Text(px + 28, py + 40, "MATCH XP", 30, Color.white, 0, 1.5f);
        int shownTotal = 0;
        for (int i = 0; i < _xpLines.Count; i++)
        {
            float lt = t - 0.3f - i * 0.35f;
            if (lt < 0f) break;
            var l = _xpLines[i];
            float a = Mathf.Clamp01(lt / 0.25f);
            int v = Mathf.RoundToInt(l.xp * Mathf.Clamp01(lt / 0.3f));
            shownTotal += v;
            float y = py + 100 + i * 56;
            RBox(px + 20, y - 24, pw - 40, 48, new Color(1f, 1f, 1f, 0.05f * a), 6f);
            Text(px + 36, y, l.label, 21, l.quest ? new Color(Green.r, Green.g, Green.b, a) : new Color(1f, 1f, 1f, a), 0, 1f);
            Text(px + pw - 36, y, $"+{v}", 24, new Color(Yellow.r, Yellow.g, Yellow.b, a), 2, 1f);
        }
        Box(px + 28, py + 450, pw - 56, 2, new Color(1f, 1f, 1f, 0.15f));
        Text(px + 36, py + 488, "TOTAL", 26, Color.white, 0, 1f);
        Text(px + pw - 36, py + 488, $"+{shownTotal} XP", 34, Yellow, 2, 1.5f);
        int xpNow = _xpOld + shownTotal;
        int lvl = 1 + xpNow / Profile.XpPerLevel;
        LevelBadge(px + 28, py + 528, 60, lvl);
        RBox(px + 104, py + 548, pw - 140, 16, new Color(1f, 1f, 1f, 0.15f), 8f);
        RBox(px + 104, py + 548, (pw - 140) * ((xpNow % Profile.XpPerLevel) / (float)Profile.XpPerLevel), 16, Cyan, 8f);
        Text(px + pw - 36, py + 584, $"{xpNow % Profile.XpPerLevel} / {Profile.XpPerLevel}", 16, White70, 2, 0f);
        if (lvl > 1 + _xpOld / Profile.XpPerLevel)
        {
            float pop = 1f + Mathf.Max(0f, 0.25f - Mathf.Repeat(t, 2f)) * 1.2f;
            Text(px + 104, py + 586, "LEVEL UP!", 24 * pop, Yellow, 0, 1.5f);
        }

        // right: the numbers
        var tiles = new (string, string)[]
        {
            ("ELIMINATIONS", Me.kills.ToString()), ("DAMAGE DEALT", $"{MatchStats.damageDealt:0}"), ("ACCURACY", $"{MatchStats.Accuracy * 100f:0}%"), ("HEADSHOTS", MatchStats.headshots.ToString()),
            ("MATERIALS GATHERED", MatchStats.mats.ToString()), ("STRUCTURES BUILT", BuildSystem.PlacedByPlayer.ToString()),
            ("DISTANCE TRAVELED", MatchStats.distance < 1000f ? $"{MatchStats.distance:0} M" : (MatchStats.distance / 1000f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " KM"),
            ("TIME SURVIVED", Clock(MatchStats.survived)),
        };
        float x0 = px + pw + 30f, tw = Mathf.Min(330f, (W - x0 - 80f - 16f) / 2f), th = 130f;
        for (int i = 0; i < tiles.Length; i++)
        {
            float x = x0 + (i % 2) * (tw + 16f), y = 150f + (i / 2) * (th + 16f);
            Panel(x, y, tw, th, 0.82f, 8f);
            RBox(x, y, 6, th, Yellow, 3f);
            Text(x + 22, y + 36, tiles[i].Item1, 17, Grey, 0, 1f);
            Text(x + 22, y + 88, tiles[i].Item2, 44, Color.white, 0, 2f);
        }
        if (YellowButton("again", W - 760, H - 150, 330, 86, "PLAY AGAIN", 36)) GameBootstrap.Restart(false);
        if (DarkButton("lobby", W - 410, H - 150, 330, 86, "RETURN TO LOBBY", 26, false, IcBack)) GameBootstrap.Restart(true);
    }

    // ---------------------------------------------------------------- emote wheel, replay, FPS

    /// Fortnite's emote wheel: six slices round the middle, the one under the pointer lit up
    void DrawWheel(float W, float H, int hover, Vector2 pointer)
    {
        float cx = W * 0.5f, cy = H * 0.5f, R0 = 290f;
        Tex(cx - 420, cy - 420, 840, 840, _radial, new Color(0f, 0.02f, 0.1f, 0.6f));
        for (int i = 0; i < 6; i++)
        {
            bool hv = i == hover;
            float ang = i * 60f;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, new Vector2(cx * s, cy * s));
            float r = hv ? R0 + 14f : R0;
            Tex(cx - r, cy - r, r * 2f, r * 2f, _wedge, hv ? new Color(0.3f, 0.65f, 1f, 0.95f) : new Color(0.03f, 0.1f, 0.3f, 0.82f));
            GUI.matrix = m;
            float a = ang * Mathf.Deg2Rad;
            var p = new Vector2(cx + Mathf.Sin(a) * R0 * 0.71f, cy - Mathf.Cos(a) * R0 * 0.71f);
            Icon(p.x, p.y - 14, IcMusic, hv ? 40 : 34, Color.white);
            Text(p.x, p.y + 26, GameAssets.EmoteLabels[i], hv ? 19 : 16, Color.white, 1, 1f);
        }
        Tex(cx - R0 * 0.82f, cy - R0 * 0.82f, R0 * 1.64f, R0 * 1.64f, _ring, new Color(1f, 1f, 1f, 0.25f));
        Text(cx, cy - 30, "EMOTES", 22, Grey, 1, 1f);
        Text(cx, cy + 8, hover >= 0 ? GameAssets.EmoteLabels[hover] : "PICK ONE", hover >= 0 ? 30 : 22, hover >= 0 ? Yellow : White70, 1, 1.5f);
        Tex(cx + pointer.x - 9, cy + pointer.y - 9, 18, 18, _disk, Color.white);
        Text(cx, cy + R0 + 50, "CLICK TO PLAY   /   B TO CLOSE", 20, White70, 1, 1f);
    }

    void DrawReplay(float W, float H)
    {
        var r = ReplayPlayer.I;
        bool kill = r.title == "KILL CAM";
        Fade(0, 0, W, 200, new Color(0f, 0.02f, 0.1f, 0.6f), 0);
        Fade(0, H - 220, W, 220, new Color(0f, 0.02f, 0.1f, 0.75f), 1);
        float tw = TextWidth(r.title, 34) + 64f;
        RBox(W * 0.5f - tw * 0.5f, 34, tw, 58, kill ? new Color(0.85f, 0.14f, 0.18f, 0.95f) : new Color(0.12f, 0.4f, 0.95f, 0.95f), 8f);
        Icon(W * 0.5f - tw * 0.5f + 28, 63, kill ? IcStar : IcRefresh, 22, Color.white);
        Text(W * 0.5f + 12, 63, r.title, 34, Color.white, 1, 1.5f);
        // the player card of whoever we watch
        if (r.focus != null)
        {
            float cx = 60f, cy = H - 190f;
            Panel(cx, cy, 420, 96, 0.85f);
            LevelBadge(cx + 16, cy + 18, 60, 1 + (r.focus.isPlayer ? Profile.Level - 1 : r.focus.displayName.Length * 7 % 90));
            Text(cx + 92, cy + 34, r.focus.displayName.ToUpperInvariant(), 30, Color.white, 0, 1.5f);
            Text(cx + 92, cy + 70, $"{r.focus.kills} ELIMINATIONS", 18, Yellow, 0, 1f);
        }
        float bw = W * 0.5f, bx = W - bw - 60f, by = H - 160f;
        RBox(bx, by, bw, 10, new Color(1f, 1f, 1f, 0.2f), 5f);
        RBox(bx, by, bw * Mathf.InverseLerp(r.t0, r.t1, r.t), 10, Yellow, 5f);
        Text(bx, by + 34, $"x{r.speed:0.##}" + (r.paused ? "   PAUSED" : ""), 22, Color.white, 0, 1.5f);
        if (!r.scripted)
        {
            float hx = 60f;
            hx += KeyHint(hx, H - 40, "SPACE", "PAUSE");
            hx += KeyHint(hx, H - 40, "1 2 3", "SPEED");
            hx += KeyHint(hx, H - 40, "TAB", "NEXT PLAYER");
            KeyHint(hx, H - 40, "ESC", "EXIT");
        }
    }

    void DrawFps()
    {
        if (Event.current.type == EventType.Repaint)
        {
            _fpsN++;
            if (Time.unscaledTime - _fpsT > 0.5f) { _fps = _fpsN / (Time.unscaledTime - _fpsT); _fpsT = Time.unscaledTime; _fpsN = 0; }
        }
        Text(14, 1062, $"FPS {Mathf.RoundToInt(_fps)}", 20, new Color(0.6f, 1f, 0.6f), 0, 1.5f);
    }
}
