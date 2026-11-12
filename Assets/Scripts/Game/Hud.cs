using System.Collections.Generic;
using UnityEngine;

/// IMGUI HUD laid out like Fortnite's (default HUD, measured on a 1920x1080 screenshot): sprint, shield
/// and health bottom left; the pickaxe and five inventory slots in rarity colours bottom right with
/// magazine / reserve; minimap top right with the storm, the next safe zone and the bus route, the
/// storm clock, players left and eliminations under it; compass top centre; kill feed on the left;
/// elimination banner, damage numbers, hit markers, damage direction, pick-up card, the full map (M),
/// the bus jump prompt, the victory and elimination screens. Impact stands in for Fortnite's font.
public partial class Hud : MonoBehaviour
{
    public PlayerController pc;
    public bool showHelp = true;

    struct Number { public Vector3 pos; public float value, t; public bool head; }
    struct Feed { public string a, b, mid, post; public bool aMe, bMe; public float t; }
    struct Hurt { public Vector3 from; public float t; }
    readonly List<Number> _numbers = new List<Number>();
    readonly List<Feed> _feed = new List<Feed>();
    readonly List<(string text, float t)> _toasts = new List<(string, float)>();
    readonly List<Hurt> _hurts = new List<Hurt>();
    readonly List<(BuildMat m, int n, bool crit, float t)> _harvests = new List<(BuildMat, int, bool, float)>();
    float _hitT = 9f, _elimT = 9f, _helpUntil;
    bool _hitKill, _hitHead;
    string _elimName;
    Texture2D _white, _stormMask;
    Font _font;
    GUIStyle _st;
    float s = 1f;
    GameAssets A => GameAssets.I;
    Combatant Me => pc.me;

    void OnEnable()
    {
        GameEvents.Hit += OnHit;
        GameEvents.Eliminated += OnElim;
        GameEvents.Feed += OnToast;
        GameEvents.PlayerHurt += OnHurt;
        GameEvents.Harvest += OnHarvest;
    }

    void OnHarvest(Combatant who, BuildMat m, int n, bool crit)
    {
        if (who == null || !who.isPlayer) return;
        _harvests.Add((m, n, crit, Time.time));
        if (_harvests.Count > 6) _harvests.RemoveAt(0);
    }

    void OnDisable()
    {
        GameEvents.Hit -= OnHit;
        GameEvents.Eliminated -= OnElim;
        GameEvents.Feed -= OnToast;
        GameEvents.PlayerHurt -= OnHurt;
        GameEvents.Harvest -= OnHarvest;
    }

    void Start()
    {
        useGUILayout = false;   // no GUILayout anywhere: skips the layout pass of OnGUI
        _white = Texture2D.whiteTexture;
        _font = Font.CreateDynamicFontFromOSFont(new[] { "Impact", "Arial Black", "Arial" }, 32);
        // storm overlay for the maps: clear inside a disc, purple outside
        const int N = 256;
        _stormMask = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float r = new Vector2(x + 0.5f - N * 0.5f, y + 0.5f - N * 0.5f).magnitude / (N * 0.5f);
                px[y * N + x] = new Color(0.55f, 0.18f, 0.85f, Mathf.Clamp01((r - 0.985f) / 0.015f) * 0.5f);
            }
        _stormMask.SetPixels(px);
        _stormMask.Apply();
    }

    /// the controls panel for the first seconds of a match
    public void ShowHelp(float seconds) => _helpUntil = Time.time + seconds;

    void OnHit(DamageEvent e, IDamageable target, float dealt, bool killed)
    {
        if (dealt <= 0f) return;
        _numbers.Add(new Number { pos = e.point + Vector3.up * 0.25f + Random.insideUnitSphere * 0.12f, value = dealt, head = e.head });
        _hitT = 0f; _hitKill = killed; _hitHead = e.head;
        if (killed) { _elimT = 0f; _elimName = target.DisplayName; }
    }

    void OnElim(Combatant killer, Combatant victim, DamageEvent e)
    {
        var f = new Feed { t = Time.time, b = victim.displayName, bMe = victim.isPlayer };
        if (killer != null)
        {
            f.a = killer.displayName; f.aMe = killer.isPlayer;
            f.mid = "eliminated";
            string w = e.weapon != null ? e.weapon.label : "";
            string an = w.Length > 0 && "AEIOUaeiou".IndexOf(w[0]) >= 0 ? "an" : "a";
            f.post = e.weapon != null ? (e.head ? $"with {an} {w} headshot" : $"with the {w}") : "";
        }
        else f.mid = e.storm ? "was lost in the storm" : "fell to their death";
        _feed.Add(f);
        if (_feed.Count > 6) _feed.RemoveAt(0);
    }

    void OnToast(string text)
    {
        _toasts.Add((text, Time.time));
        if (_toasts.Count > 4) _toasts.RemoveAt(0);
    }

    void OnHurt(DamageEvent e)
    {
        if (e.source != null && e.source != Me) _hurts.Add(new Hurt { from = e.source.transform.position, t = Time.time });
        else if (e.explosion) _hurts.Add(new Hurt { from = e.point - e.dir * 3f, t = Time.time });
    }

    bool _wasMenu;

    void Update()
    {
        // Esc: the pause menu in a match (or back out of the settings); the mouse is free while a menu is up
        UpdateLoading();
        bool human = (pc != null && pc.source is HumanInput) || (Lobby.I != null && !UiPointer.scripted);
        if (human && Input.GetKeyDown(KeyCode.Escape) && !ReplayPlayer.Active && _rebind == null && !Loading)
        {
            if (SettingsOpen) CloseSettings();
            else if (LobbyWheel) LobbyWheel = false;
            else if (OutfitPick && Lobby.I != null && Lobby.I.tab == 1 && !GameMenu) OutfitPick = false;
            else if (Lobby.I != null) GameMenu = !GameMenu;
            else if (!SummaryOpen && Match.I != null) SetPaused(!Paused);
        }
        if (human && Lobby.I != null && !SettingsOpen && !GameMenu && Input.GetKeyDown(Settings.Key("Emote"))) LobbyWheel = !LobbyWheel;
        if (human && CursorFree != _wasMenu)
        {
            _wasMenu = CursorFree;
            Cursor.lockState = CursorFree ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = CursorFree;
        }
        // the storm's rumble: loud inside, growing towards the wall
        if (Match.I != null && pc != null && Me != null)
        {
            var st = Match.I.storm;
            float d = st.radius - Vector2.Distance(new Vector2(pc.transform.position.x, pc.transform.position.z), st.center);
            Sfx.stormLevel = st.radius < 400f ? Mathf.Clamp01(1f - d / 40f) : 0f;
        }
        if (Input.GetKeyDown(KeyCode.F1)) { showHelp = !showHelp; _helpUntil = showHelp ? float.MaxValue : 0f; }
        _hitT += Time.deltaTime;
        _elimT += Time.deltaTime;
        for (int i = _numbers.Count - 1; i >= 0; i--)
        {
            var n = _numbers[i];
            n.t += Time.deltaTime;
            n.pos += Vector3.up * Time.deltaTime * 0.5f;
            if (n.t > 1f) _numbers.RemoveAt(i); else _numbers[i] = n;
        }
        _hurts.RemoveAll(h => Time.time - h.t > 1.2f);
    }

    // ---------------------------------------------------------------- drawing helpers (1080p units)

    Rect R(float x, float y, float w, float h) => new Rect(x * s, y * s, w * s, h * s);

    void Box(float x, float y, float w, float h, Color c)
    {
        var old = GUI.color; GUI.color = c;
        GUI.DrawTexture(R(x, y, w, h), _white);
        GUI.color = old;
    }

    void Tex(float x, float y, float w, float h, Texture t, Color c)
    {
        if (t == null) return;
        var old = GUI.color; GUI.color = c;
        GUI.DrawTexture(R(x, y, w, h), t, ScaleMode.ScaleToFit, true);
        GUI.color = old;
    }

    // the HUD redraws a few hundred labels a frame: one reused GUIContent (no garbage) and the measured
    // sizes kept, since measuring lays the text out as dearly as drawing it
    readonly GUIContent _gc = new GUIContent();
    readonly Dictionary<(string, int), Vector2> _sizes = new Dictionary<(string, int), Vector2>(2048);

    Vector2 Measure(string t, int fontSize)
    {
        if (_sizes.TryGetValue((t, fontSize), out var sz)) return sz;
        if (_sizes.Count > 8192) _sizes.Clear();
        _gc.text = t;
        sz = _st.CalcSize(_gc);
        _sizes[(t, fontSize)] = sz;
        return sz;
    }

    /// text in the HUD font; anchor 0 left, 1 centre, 2 right of x; y is the vertical centre
    float Text(float x, float y, string t, float size, Color c, int anchor = 0, float outline = 2f)
    {
        if (string.IsNullOrEmpty(t)) return 0f;
        int fs = Mathf.Max(6, Mathf.RoundToInt(size * s));
        _st.fontSize = fs;
        var sz = Measure(t, fs);
        float w = sz.x / s, h = sz.y / s;
        // labels only draw on the repaint; other events (and clear text) only want the width
        if (Event.current.type != EventType.Repaint || c.a <= 0.003f) return w;
        float left = anchor == 0 ? x : anchor == 1 ? x - w * 0.5f : x - w;
        var r = R(left, y - h * 0.5f, w + 4, h);
        _gc.text = t;
        if (outline > 0f)
        {
            _st.normal.textColor = new Color(0f, 0f, 0f, 0.55f * c.a);
            float o = outline * s;
            GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), _gc, _st);
        }
        _st.normal.textColor = c;
        GUI.Label(r, _gc, _st);
        return w;
    }

    float TextWidth(string t, float size)
    {
        int fs = Mathf.Max(6, Mathf.RoundToInt(size * s));
        _st.fontSize = fs;
        return Measure(t, fs).x / s;
    }

    void Rotated(float cx, float cy, float deg, System.Action draw)
    {
        var m = GUI.matrix;
        GUIUtility.RotateAroundPivot(deg, new Vector2(cx * s, cy * s));
        draw();
        GUI.matrix = m;
    }

    void Line(Vector2 a, Vector2 b, float w, Color c, float dash = 0f)
    {
        var d = b - a;
        float len = d.magnitude;
        if (len < 0.5f) return;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (dash <= 0f) { Rotated(a.x, a.y, ang, () => Box(a.x, a.y - w * 0.5f, len, w, c)); return; }
        for (float t = 0f; t < len; t += dash * 2f)
        {
            var p = a + d / len * t;
            float l = Mathf.Min(dash, len - t);
            Rotated(p.x, p.y, ang, () => Box(p.x, p.y - w * 0.5f, l, w, c));
        }
    }

    void Circle(Vector2 c, float r, float w, Color col)
    {
        int seg = Mathf.Clamp((int)(r * 0.6f), 24, 96);
        var prev = c + new Vector2(r, 0f);
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var p = c + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            Line(prev, p, w, col);
            prev = p;
        }
    }

    Texture IconOf(Item it)
    {
        if (it == null) return null;
        if (it.IsWeapon) { int i = WeaponTable.IndexOf(it.weapon.id); return A.weaponIcons != null && i >= 0 && i < A.weaponIcons.Length ? A.weaponIcons[i] : null; }
        int c = System.Array.FindIndex(WeaponTable.Consumables, x => x.id == it.consumable.id);
        return A.consumableIcons != null && c >= 0 && c < A.consumableIcons.Length ? A.consumableIcons[c] : null;
    }

    // ---------------------------------------------------------------- layout

    /// the match HUD (OnGUI, in HudMenus, puts the menus over it)
    void DrawMatchHud()
    {
        if (pc == null || pc.cam == null || A == null || Match.I == null) return;
        if (_st == null) _st = new GUIStyle(GUI.skin.label) { font = _font, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), wordWrap = false };
        s = Screen.height / 1080f;
        float W = Screen.width / s, H = 1080f;
        float cx = W * 0.5f, cy = H * 0.5f;
        var m = Match.I;
        var cam = pc.cam.cam;
        var hands = pc.hands;
        var d = hands.Def;

        // in the storm: purple screen
        if (Me.Alive && Me.inMatch && !m.storm.Inside(pc.transform.position))
            Box(0, 0, W, H, new Color(0.45f, 0.1f, 0.7f, 0.2f + 0.05f * Mathf.Sin(Time.time * 4f)));

        bool ground = pc.mode == PlayerController.Mode.Ground && Me.Alive;
        bool scoped = ground && pc.aiming && d.scope && cam.fieldOfView < d.adsFov + 6f;
        if (scoped) DrawScope(W, H, cx, cy);
        else if (ground) DrawCrosshair(cx, cy, d, cam);
        if (ground) { DrawHitMarker(cx, cy); DrawNumbers(cam); DrawHurt(cx, cy); }
        DrawCompass(cx);
        DrawMinimap(W, m);
        DrawStormLine(cx, m);
        if (Me.Alive) DrawVitals();
        if (Me.Alive && pc.mode != PlayerController.Mode.Bus)
        {
            if (pc.building) DrawBuildBar(W); else if (pc.editing != null) DrawEditBar(W); else DrawHotbar(W, hands, d);
            DrawMats(W);
        }
        if (ground) DrawStructure(cx, cy, cam);
        DrawFeed();
        DrawToasts(W);
        if (ground) { DrawElimination(cx); DrawPickup(cx, cy); DrawProgress(cx, cy, hands); }
        if (pc.mode == PlayerController.Mode.Bus) DrawBus(cx, m);
        if (pc.mode == PlayerController.Mode.Skydive || pc.mode == PlayerController.Mode.Glide) DrawAltitude();
        if (pc.showMap) DrawFullMap(W, H, m);
        DrawEnd(W, H, cx, m);
        DrawHelp(W);
    }

    void DrawScope(float W, float H, float cx, float cy)
    {
        float size = H;
        GUI.DrawTexture(R(cx - size * 0.5f, 0, size, size), A.scope);
        Box(0, 0, cx - size * 0.5f + 1, H, Color.black);
        Box(cx + size * 0.5f - 1, 0, W - cx - size * 0.5f + 2, H, Color.black);
        Box(cx - size * 0.47f, cy - 1, size * 0.94f, 2, new Color(0, 0, 0, 0.9f));
        Box(cx - 1, cy - size * 0.47f, 2, size * 0.94f, new Color(0, 0, 0, 0.9f));
    }

    void DrawCrosshair(float cx, float cy, WeaponDef d, Camera cam)
    {
        float px = Mathf.Tan(pc.hands.Spread(pc.aiming, pc.Motion) * Mathf.Deg2Rad) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 540f;
        var col = new Color(1f, 1f, 1f, 0.92f);
        var shade = new Color(0f, 0f, 0f, 0.35f);
        if (pc.building || pc.editing != null || d.kind == FireKind.Melee || d.kind == FireKind.Grenade || d.kind == FireKind.Consume)
        {
            Box(cx - 3, cy - 3, 6, 6, shade); Box(cx - 2, cy - 2, 4, 4, col);
            return;
        }
        if (d.pellets > 1)
        {
            float r = Mathf.Max(22f, px);
            for (int i = 0; i < 4; i++)
                Rotated(cx, cy, 45f + 90f * i, () => { Box(cx + r, cy - 7, 3, 14, col); });
            Box(cx - 2, cy - 2, 4, 4, col);
            return;
        }
        float g = Mathf.Max(5f, px), L = 11f, T = 2.5f;
        foreach (var (x, y, ww, hh) in new[] { (cx - g - L, cy - T * 0.5f, L, T), (cx + g, cy - T * 0.5f, L, T), (cx - T * 0.5f, cy - g - L, T, L), (cx - T * 0.5f, cy + g, T, L) })
        {
            Box(x - 1, y - 1, ww + 2, hh + 2, shade);
            Box(x, y, ww, hh, col);
        }
        Box(cx - 1.5f, cy - 1.5f, 3, 3, col);
    }

    void DrawHitMarker(float cx, float cy)
    {
        if (_hitT > 0.2f) return;
        var hc = _hitKill ? new Color(1f, 0.3f, 0.25f) : _hitHead ? new Color(1f, 0.85f, 0.25f) : Color.white;
        float grow = 1f + (0.2f - _hitT) * 1.5f;
        for (int i = 0; i < 4; i++)
            Rotated(cx, cy, 45f + 90f * i, () => Box(cx + 8 * grow, cy - 1.5f, 10 * grow, 3, hc));
    }

    void DrawNumbers(Camera cam)
    {
        foreach (var n in _numbers)
        {
            var sp = cam.WorldToScreenPoint(n.pos);
            if (sp.z <= 0f) continue;
            float pop = 1f + Mathf.Max(0f, 0.25f - n.t) * 2.4f;
            var c = n.head ? new Color(1f, 0.86f, 0.26f) : Color.white;
            c.a = Mathf.Clamp01((1f - n.t) * 4f);
            Text(sp.x / s, (Screen.height - sp.y) / s, Mathf.RoundToInt(n.value).ToString(), 38 * pop * (n.head ? 1.12f : 1f), c, 1, 3f);
        }
    }

    // red arcs around the crosshair pointing at whoever hurt us
    void DrawHurt(float cx, float cy)
    {
        var fwd = Vector3.ProjectOnPlane(pc.cam.Forward, Vector3.up).normalized;
        foreach (var h in _hurts)
        {
            var to = Vector3.ProjectOnPlane(h.from - pc.transform.position, Vector3.up);
            if (to.sqrMagnitude < 0.01f) continue;
            float ang = Vector3.SignedAngle(fwd, to.normalized, Vector3.up);
            float a = Mathf.Clamp01(1.2f - (Time.time - h.t)) * 0.9f;
            for (int k = -3; k <= 3; k++)
                Rotated(cx, cy, ang + k * 4f - 90f, () => Box(cx + 150f, cy - 4f, 14f, 8f, new Color(1f, 0.15f, 0.1f, a * (1f - Mathf.Abs(k) / 4.5f))));
        }
    }

    // compass: 15 degree ticks, cardinal letters, the heading in the middle (4.4 px per degree)
    void DrawCompass(float cx)
    {
        float yaw = Mathf.Repeat(pc.cam.yaw, 360f);
        const float ppd = 4.4f, span = 78f;
        string[] card = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        for (int deg = 0; deg < 360; deg += 15)
        {
            float off = Mathf.DeltaAngle(yaw, deg);
            if (Mathf.Abs(off) > span || Mathf.Abs(off) < 11f) continue;
            float x = cx + off * ppd;
            float a = Mathf.Clamp01(1.2f - Mathf.Abs(off) / span) * 0.95f;
            if (Mathf.Abs(off) < 18f) a *= (Mathf.Abs(off) - 11f) / 7f;
            if (deg % 45 == 0)
            {
                bool main = deg % 90 == 0;
                Text(x, 27, card[deg / 45], main ? 34 : 26, new Color(1f, 1f, 1f, a * (main ? 1f : 0.7f)), 1, 1.5f);
            }
            else
            {
                Box(x - 1, 14, 2, 8, new Color(1f, 1f, 1f, a * 0.8f));
                Text(x, 36, deg.ToString(), 17, new Color(1f, 1f, 1f, a * 0.8f), 1, 1f);
            }
        }
        Text(cx, 30, Mathf.RoundToInt(yaw).ToString(), 26, Color.white, 1, 1.5f);
    }

    // ---------------------------------------------------------------- maps

    /// world (x, z) to a map rectangle: centre and metres per half size
    Vector2 MapPoint(Vector3 w, Vector2 centreWorld, float halfMetres, float x0, float y0, float size) =>
        new Vector2(x0 + size * 0.5f + (w.x - centreWorld.x) / halfMetres * size * 0.5f,
                    y0 + size * 0.5f - (w.z - centreWorld.y) / halfMetres * size * 0.5f);

    void DrawMapLayers(Match m, float x0, float y0, float size, Vector2 cw, float half, bool full)
    {
        GUI.BeginGroup(R(x0, y0, size, size));
        float ox = 0f, oy = 0f;
        if (A.minimap != null)
        {
            float H2 = A.mapHalf;
            float u = (cw.x - half + H2) / (2f * H2), v = (cw.y - half + H2) / (2f * H2), k = half / H2;
            GUI.DrawTextureWithTexCoords(R(ox, oy, size, size), A.minimap, new Rect(u, v, k, k));
        }
        // storm: purple outside the eye, the next safe zone as a white circle
        var st = m.storm;
        if (st.radius < 400f)
        {
            var c = MapPoint(new Vector3(st.center.x, 0, st.center.y), cw, half, ox, oy, size);
            float r = st.radius / half * size * 0.5f;
            float rr = r / 0.985f;
            Tex(c.x - rr, c.y - rr, rr * 2f, rr * 2f, _stormMask, Color.white);
            var purple = new Color(0.55f, 0.18f, 0.85f, 0.5f);
            Box(ox, oy, size, Mathf.Max(0f, c.y - rr - oy), purple);
            Box(ox, c.y + rr, size, Mathf.Max(0f, oy + size - c.y - rr), purple);
            Box(ox, Mathf.Max(oy, c.y - rr), Mathf.Max(0f, c.x - rr - ox), Mathf.Min(size, rr * 2f), purple);
            Box(c.x + rr, Mathf.Max(oy, c.y - rr), Mathf.Max(0f, ox + size - c.x - rr), Mathf.Min(size, rr * 2f), purple);
        }
        if (st.phase > 0 && st.nextRadius > 0.5f)
            Circle(MapPoint(new Vector3(st.nextCenter.x, 0, st.nextCenter.y), cw, half, ox, oy, size), st.nextRadius / half * size * 0.5f, full ? 2.5f : 2f, Color.white);
        // the bus route while in the bus
        if (m.phase == Match.Phase.Bus)
        {
            var a = MapPoint(m.busA, cw, half, ox, oy, size); var b = MapPoint(m.busB, cw, half, ox, oy, size);
            Line(a, b, full ? 4f : 3f, new Color(1f, 1f, 1f, 0.9f), full ? 10f : 6f);
            var bp = MapPoint(m.BusPos, cw, half, ox, oy, size);
            Box(bp.x - 6, bp.y - 6, 12, 12, new Color(0.2f, 0.75f, 1f));
        }
        // traversal: zip lines, rifts, launch pads; supply drops in blue
        foreach (var z in ZipLine.All) Line(MapPoint(z.a, cw, half, ox, oy, size), MapPoint(z.b, cw, half, ox, oy, size), full ? 2.5f : 2f, new Color(1f, 0.82f, 0.2f, 0.9f), 0f);
        foreach (var r in Rift.All) { var q = MapPoint(r.transform.position, cw, half, ox, oy, size); Box(q.x - 5, q.y - 5, 10, 10, new Color(0.65f, 0.35f, 1f)); }
        foreach (var lp in LaunchPad.All) { var q = MapPoint(lp.transform.position, cw, half, ox, oy, size); Box(q.x - 3, q.y - 3, 6, 6, new Color(0.35f, 0.8f, 1f)); }
        foreach (var sd in SupplyDrop.All)
        {
            var q = MapPoint(sd.transform.position, cw, half, ox, oy, size);
            Box(q.x - 8, q.y - 8, 16, 16, Color.white);
            Box(q.x - 6, q.y - 6, 12, 12, new Color(0.2f, 0.55f, 1f));
        }
        // the player (or the bus): arrow pointing where the camera looks
        var pp = MapPoint(pc.mode == PlayerController.Mode.Bus ? m.BusPos : pc.transform.position, cw, half, ox, oy, size);
        float ar = full ? 30f : 22f;
        var mtx = GUI.matrix;
        GUIUtility.RotateAroundPivot(pc.cam.yaw, new Vector2(pp.x * s, pp.y * s));
        Tex(pp.x - ar * 0.5f, pp.y - ar * 0.5f, ar, ar, A.mapArrow, Color.white);
        GUI.matrix = mtx;
        // the way to the safe zone when outside it
        if (st.phase > 0 && Me.Alive && pc.mode == PlayerController.Mode.Ground && !st.InsideNext(pc.transform.position))
            Line(pp, MapPoint(new Vector3(st.nextCenter.x, 0, st.nextCenter.y), cw, half, ox, oy, size), 2f, new Color(1f, 1f, 1f, 0.85f), 5f);
        GUI.EndGroup();
    }

    void DrawMinimap(float W, Match m)
    {
        float size = 300f, x0 = W - size - 8f, y0 = 8f;
        Box(x0 - 2, y0 - 2, size + 4, size + 4, new Color(0f, 0f, 0f, 0.35f));
        var p = pc.mode == PlayerController.Mode.Bus ? m.BusPos : pc.transform.position;
        DrawMapLayers(m, x0, y0, size, new Vector2(p.x, p.z), pc.mode == PlayerController.Mode.Bus ? 200f : 95f, false);

        // storm clock, players left, eliminations
        m.storm.Status(m.Now, out float secs);
        secs = Mathf.Max(0f, secs);
        var items = new (Texture2D icon, string text)[] {
            (A.iconClock, $"{(int)(secs / 60)}:{(int)(secs % 60):00}"), (A.iconPlayers, m.Alive.ToString()), (A.iconElims, Me.kills.ToString()) };
        float x = W - 10f;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            float tw = TextWidth(items[i].text, 27);
            x -= tw;
            Text(x, y0 + size + 22, items[i].text, 27, Color.white, 0, 1.5f);
            x -= 30;
            Tex(x, y0 + size + 9, 26, 26, items[i].icon, Color.white);
            x -= 16;
        }
    }

    // the storm message under the compass at phase changes
    void DrawStormLine(float cx, Match m)
    {
        string msg = m.storm.Status(m.Now, out float secs);
        if (string.IsNullOrEmpty(msg) || m.phase == Match.Phase.Bus) return;
        bool show = secs < 10f || (m.storm.shrinking && m.Now % 20f < 4f) || (!m.storm.shrinking && m.storm.phase > 0 && m.Now % 30f < 5f);
        if (!show) return;
        string t = m.storm.shrinking ? msg : $"{msg} {(int)Mathf.Max(0f, secs)} SECONDS";
        float tw = TextWidth(t, 26);
        Box(cx - tw * 0.5f - 18, 64, tw + 36, 34, new Color(0.35f, 0.08f, 0.55f, 0.75f));
        Text(cx, 81, t, 26, Color.white, 1, 1.5f);
    }

    void DrawFullMap(float W, float H, Match m)
    {
        float size = 860f, x0 = (W - size) * 0.5f, y0 = (H - size) * 0.5f;
        Box(0, 0, W, H, new Color(0.02f, 0.03f, 0.08f, 0.55f));
        Box(x0 - 4, y0 - 4, size + 8, size + 8, new Color(1f, 1f, 1f, 0.8f));
        DrawMapLayers(m, x0, y0, size, Vector2.zero, A.mapHalf, true);
        Text(x0, y0 - 26, "MAP", 30, Color.white, 0, 2f);
        Text(x0 + size, y0 - 26, "M CLOSE", 20, new Color(1, 1, 1, 0.8f), 2, 1.5f);
    }

    // ---------------------------------------------------------------- player status

    void DrawVitals()
    {
        const float x = 122f, wBar = 334f;
        var track = new Color(0.06f, 0.07f, 0.12f, 0.55f);
        Tex(96, 924, 16, 16, A.iconSprint, new Color(1f, 1f, 1f, 0.9f));
        Box(x, 929, wBar, 5, track);
        Box(x, 929, wBar * pc.stamina, 5, Color.white);

        Tex(95, 941, 20, 20, A.iconShield, Color.white);
        Box(x, 942, wBar, 18, track);
        float sh = wBar * Me.shield / Me.maxShield;
        Box(x, 942, sh, 18, new Color(0.24f, 0.55f, 0.92f));
        Box(x, 942, sh, 8, new Color(0.45f, 0.75f, 1f, 0.55f));
        if (sh > 2f) Box(x + sh - 3, 942, 3, 18, new Color(0.8f, 0.92f, 1f));
        Text(466, 951, Mathf.CeilToInt(Me.shield).ToString(), 27, Color.white, 0, 1.5f);

        Tex(95, 970, 22, 22, A.iconHealth, Color.white);
        Box(x, 966, wBar, 30, track);
        float hp = wBar * Me.health / Me.maxHealth;
        var green = Me.health > 30f ? new Color(0.36f, 0.78f, 0.22f) : new Color(0.9f, 0.3f, 0.2f);
        Box(x, 966, hp, 30, green);
        Box(x, 966, hp, 12, new Color(0.62f, 0.95f, 0.4f, 0.45f));
        if (hp > 2f) Box(x + hp - 3, 966, 3, 30, new Color(0.85f, 1f, 0.75f));
        Text(466, 981, Mathf.CeilToInt(Me.health).ToString(), 27, Color.white, 0, 1.5f);
    }

    // pickaxe slot, then the five inventory slots; magazine / reserve and the item name to the left
    void DrawHotbar(float W, WeaponHands hands, WeaponDef d)
    {
        var inv = Me.inv;
        const float slot = 78f, gap = 6f, y = 920f;
        float x0 = W - 8f - 6 * slot - 5 * gap;
        for (int k = 0; k < 6; k++)
        {
            var it = inv[k];
            bool sel = k == inv.selected;
            float x = x0 + k * (slot + gap), yy = sel ? y - 8f : y;
            if (it == null)
            {
                var edge = new Color(1, 1, 1, 0.25f);
                Box(x, yy, slot, slot, new Color(0.05f, 0.07f, 0.15f, 0.35f));
                Box(x, yy, slot, 1.5f, edge); Box(x, yy + slot - 1.5f, slot, 1.5f, edge);
                Box(x, yy, 1.5f, slot, edge); Box(x + slot - 1.5f, yy, 1.5f, slot, edge);
            }
            else
            {
                Box(x, yy, slot, slot, WeaponTable.RarityColor(it.rarity));
                Tex(x, yy, slot, slot, A.slotShine, Color.white);
                Tex(x + 4, yy + 4, slot - 8, slot - 8, IconOf(it), Color.white);
                string count = it.Stacks ? it.mag.ToString() : it.IsWeapon && it.weapon.UsesAmmo ? inv.Ammo(it.weapon.ammo).ToString() : "";
                if (count.Length > 0) Text(x + slot - 5, yy + slot - 13, count, 22, Color.white, 2, 1.5f);
            }
            if (sel)
            {
                const float b = 3f;
                Box(x - b, yy - b, slot + 2 * b, b, Color.white); Box(x - b, yy + slot, slot + 2 * b, b, Color.white);
                Box(x - b, yy, b, slot, Color.white); Box(x + slot, yy, b, slot, Color.white);
            }
        }
        float rx = x0 - 26f;
        var cur = inv.Current;
        if (cur != null && cur.IsWeapon && d.UsesAmmo)
        {
            string rest = $" / {hands.Reserve}";
            float rw = TextWidth(rest, 34);
            Text(rx, 948, rest, 34, new Color(1f, 1f, 1f, 0.92f), 2, 2f);
            Text(rx - rw, 946, cur.mag.ToString(), 44, cur.mag == 0 ? new Color(1f, 0.4f, 0.35f) : Color.white, 2, 2f);
        }
        if (cur != null) Text(rx, 984, cur.Label.ToUpperInvariant(), 21, WeaponTable.RarityColor(cur.rarity) * 0.4f + new Color(0.6f, 0.6f, 0.62f, 1f), 2, 1.5f);
    }

    // materials, right side above the hotbar (Fortnite): wood, brick, metal; the one used for building lit
    void DrawMats(float W)
    {
        var inv = Me.inv;
        float x0 = W - 8f - 3 * 74f, y = 834f;
        for (int m = 0; m < 3; m++)
        {
            float x = x0 + m * 74f;
            bool sel = pc.building && (int)pc.buildMat == m;
            if (sel) Box(x - 2, y - 4, 70, 70, new Color(1f, 1f, 1f, 0.25f));
            Tex(x + 13, y, 44, 40, A.matIcons != null && m < A.matIcons.Length ? A.matIcons[m] : null, inv.mats[m] > 0 ? Color.white : new Color(1f, 1f, 1f, 0.45f));
            Text(x + 35, y + 52, inv.mats[m].ToString(), 23, Color.white, 1, 1.5f);
        }
        // +8 popups from the pickaxe, rising over the counters
        foreach (var (m, n, crit, t) in _harvests)
        {
            float age = Time.time - t;
            if (age > 1.2f) continue;
            float a = Mathf.Clamp01(1.4f - age);
            Text(x0 + (int)m * 74f + 35, y - 14 - age * 40f, $"+{n}", crit ? 30 : 24, crit ? new Color(0.5f, 0.85f, 1f, a) : new Color(1f, 1f, 1f, a), 1, 2f);
        }
    }

    // edit mode: what is being edited and the keys, where the build bar goes
    void DrawEditBar(float W)
    {
        float rx = W - 8f - 26f;
        Text(rx, 946, "EDIT " + BuildRules.Label(pc.editing.mat) + " " + (pc.editing.piece == PieceType.Wall ? "WALL" : "FLOOR"), 40, Color.white, 2, 2f);
        Text(rx, 984, "LMB SELECT   G CONFIRM   RMB RESET", 19, new Color(0.82f, 0.83f, 0.86f), 2, 1.5f);
    }

    // build mode: the four pieces with their keys instead of the weapons
    void DrawBuildBar(float W)
    {
        const float slot = 78f, gap = 6f, y = 920f;
        float x0 = W - 8f - 4 * slot - 3 * gap;
        for (int k = 0; k < 4; k++)
        {
            bool sel = k == (int)pc.piece;
            float x = x0 + k * (slot + gap), yy = sel ? y - 8f : y;
            Box(x, yy, slot, slot, sel ? new Color(0.16f, 0.45f, 0.85f, 0.9f) : new Color(0.08f, 0.2f, 0.45f, 0.75f));
            Tex(x, yy, slot, slot, A.slotShine, Color.white);
            Tex(x + 8, yy + 8, slot - 16, slot - 16, A.pieceIcons != null && k < A.pieceIcons.Length ? A.pieceIcons[k] : null, Color.white);
            Text(x + 6, yy + 12, BuildRules.Key((PieceType)k), 18, Color.white, 0, 1.5f);
            if (sel)
            {
                const float b = 3f;
                Box(x - b, yy - b, slot + 2 * b, b, Color.white); Box(x - b, yy + slot, slot + 2 * b, b, Color.white);
                Box(x - b, yy, b, slot, Color.white); Box(x + slot, yy, b, slot, Color.white);
            }
        }
        float rx = x0 - 26f;
        Text(rx, 946, BuildRules.Label(pc.buildMat), 40, pc.buildOk ? Color.white : new Color(1f, 0.4f, 0.35f), 2, 2f);
        Text(rx, 984, pc.buildOk ? "LMB BUILD   RMB MATERIAL" : Me.inv.mats[(int)pc.buildMat] < BuildRules.Cost ? "NOT ENOUGH MATERIALS" : "SPOT TAKEN", 19, new Color(0.82f, 0.83f, 0.86f), 2, 1.5f);
    }

    // the structure under the crosshair: hit points; the weak point of what you are harvesting
    void DrawStructure(float cx, float cy, Camera cam)
    {
        var ray = pc.cam.AimRay(0f);
        if (!Physics.Raycast(ray, out var h, 40f, GameLayers.Env, QueryTriggerInteraction.Ignore)) return;
        var st = h.collider.GetComponentInParent<Destructible>();
        if (st == null || !st.Alive) return;
        if (st.built || Time.time - st.lastHit < 4f)
        {
            float w = 170f, x = cx - w * 0.5f, y = cy - 92f;
            var col = st.mat == BuildMat.Wood ? new Color(0.85f, 0.62f, 0.32f) : st.mat == BuildMat.Brick ? new Color(0.85f, 0.45f, 0.35f) : new Color(0.7f, 0.78f, 0.88f);
            Box(x, y, w, 10, new Color(0, 0, 0, 0.5f));
            Box(x, y, w * Mathf.Clamp01(st.hp / st.maxHp), 10, col);
            if (st.Building) Box(x, y + 10, w * Mathf.Clamp01((Time.time - st.buildStart) / (st.buildEnd - st.buildStart)), 3, new Color(0.45f, 0.8f, 1f));
            Text(cx, y - 14, $"{Mathf.CeilToInt(st.hp)} / {Mathf.RoundToInt(st.maxHp)}", 19, Color.white, 1, 1.5f);
        }
        if (st.lastHitBy == Me && Time.time < st.weakUntil && !st.built)
        {
            var sp = cam.WorldToScreenPoint(st.weakPoint);
            if (sp.z > 0f)
            {
                var c = new Vector2(sp.x / s, (Screen.height - sp.y) / s);
                float pulse = 1f + 0.12f * Mathf.Sin(Time.time * 9f);
                Circle(c, 15f * pulse, 3f, new Color(0.35f, 0.75f, 1f, 0.95f));
                Box(c.x - 3, c.y - 3, 6, 6, new Color(0.35f, 0.75f, 1f, 0.95f));
            }
        }
    }

    // kill feed: left side, newest at the bottom; the victim on a red tag, you highlighted
    void DrawFeed()
    {
        float y = 862f;
        for (int k = _feed.Count - 1; k >= 0; k--)
        {
            var f = _feed[k];
            float a = Mathf.Clamp01(8f - (Time.time - f.t));
            if (a <= 0f) continue;
            float x = 12f;
            if (!string.IsNullOrEmpty(f.a))
                x += Text(x, y, f.a, 21, f.aMe ? new Color(1f, 0.85f, 0.3f, a) : new Color(1f, 1f, 1f, a), 0, 1.5f) + 8f;
            if (!string.IsNullOrEmpty(f.a))
            {
                x += Text(x, y, f.mid, 19, new Color(0.85f, 0.85f, 0.88f, a), 0, 1.5f) + 8f;
                float nw = TextWidth(f.b, 21);
                Box(x - 4, y - 12, nw + 10, 24, f.bMe ? new Color(0.95f, 0.65f, 0.1f, 0.9f * a) : new Color(0.86f, 0.17f, 0.16f, 0.9f * a));
                Text(x + 1, y, f.b, 21, new Color(1f, 1f, 1f, a), 0, 0f);
                x += nw + 14f;
                Text(x, y, f.post, 17, new Color(0.8f, 0.8f, 0.84f, a), 0, 1.5f);
            }
            else
            {
                float nw = TextWidth(f.b, 21);
                Box(x - 4, y - 12, nw + 10, 24, f.bMe ? new Color(0.95f, 0.65f, 0.1f, 0.9f * a) : new Color(0.86f, 0.17f, 0.16f, 0.9f * a));
                Text(x + 1, y, f.b, 21, new Color(1f, 1f, 1f, a), 0, 0f);
                Text(x + nw + 14f, y, f.mid, 19, new Color(0.85f, 0.85f, 0.88f, a), 0, 1.5f);
            }
            y -= 30f;
        }
    }

    // picked-up items, right side above the hotbar
    void DrawToasts(float W)
    {
        float y = 790f;
        for (int k = _toasts.Count - 1; k >= 0; k--)
        {
            float a = Mathf.Clamp01(3f - (Time.time - _toasts[k].t));
            if (a <= 0f) continue;
            Text(W - 14f, y, _toasts[k].text.ToUpperInvariant(), 20, new Color(1f, 1f, 1f, a), 2, 1.5f);
            y -= 26f;
        }
    }

    void DrawElimination(float cx)
    {
        if (_elimT > 2.6f || _elimName == null) return;
        float a = Mathf.Clamp01((2.6f - _elimT) * 3f);
        float pop = 1f + Mathf.Max(0f, 0.15f - _elimT) * 3f;
        float size = 30f * pop;
        string lead = "ELIMINATED";
        float lw = TextWidth(lead + " ", size), nw = TextWidth(_elimName.ToUpperInvariant(), size);
        float x = cx - (lw + nw + 12f) * 0.5f, y = 770f;
        Text(x, y, lead, size, new Color(1f, 1f, 1f, a), 0, 2f);
        Box(x + lw - 4, y - size * 0.55f, nw + 12, size * 1.1f, new Color(0.86f, 0.17f, 0.16f, 0.9f * a));
        Text(x + lw + 2, y, _elimName.ToUpperInvariant(), size, new Color(1f, 1f, 1f, a), 0, 0f);
    }

    // pick-up card: rarity stripe, item name, rarity or count, and the key
    void DrawPickup(float cx, float cy)
    {
        var n = pc.nearest;
        string name, sub, action;
        Rarity rar;
        if (n == null)
        {
            if (pc.nearZip == null || pc.mode != PlayerController.Mode.Ground) return;
            name = "Zipline"; sub = "RIDE THE WAY YOU LOOK"; action = "Ride"; rar = Rarity.Rare;
        }
        else if (n is LootContainer lc) { name = lc.Label; sub = "SEARCH"; action = "Search"; rar = lc.Rarity; }
        else if (n is WorldItem wi)
        {
            name = wi.Label; rar = wi.Rarity;
            sub = wi.IsMats ? $"x{wi.matCount}" : wi.IsAmmo ? $"x{wi.ammoCount}" : wi.item.Stacks && wi.item.mag > 1 ? $"{wi.Rarity.ToString().ToUpperInvariant()}  x{wi.item.mag}" : wi.Rarity.ToString().ToUpperInvariant();
            action = !wi.IsAmmo && Me.inv.Full && Me.inv.selected > 0 ? "Swap" : "Pick Up";
        }
        else return;
        var rc = WeaponTable.RarityColor(rar);
        float x = cx + 140f, y = cy + 40f, w = 330f, h = 86f;
        Box(x, y, w, h, new Color(0.03f, 0.04f, 0.1f, 0.72f));
        Box(x, y, 7, h, rc);
        Text(x + 22, y + 24, name.ToUpperInvariant(), 28, Color.white, 0, 1.5f);
        Text(x + 22, y + 52, sub, 17, rc, 0, 1f);
        Box(x + 22, y + 63, 22, 22, Color.white);
        Text(x + 33, y + 74, "E", 18, new Color(0.05f, 0.05f, 0.1f), 1, 0f);
        Text(x + 52, y + 74, action, 20, Color.white, 0, 1f);
    }

    void DrawProgress(float cx, float cy, WeaponHands hands)
    {
        if (hands.Reloading)
        {
            Box(cx - 50, cy + 46, 100, 5, new Color(0, 0, 0, 0.5f));
            Box(cx - 50, cy + 46, 100 * hands.ReloadProgress, 5, Color.white);
        }
        if (hands.Using && Me.inv.Current != null)
        {
            Text(cx, cy + 120, Me.inv.Current.Label.ToUpperInvariant(), 26, Color.white, 1, 2f);
            Box(cx - 130, cy + 140, 260, 10, new Color(0, 0, 0, 0.55f));
            Box(cx - 130, cy + 140, 260 * hands.UseProgress, 10, Me.inv.Current.consumable.shield > 0 ? new Color(0.3f, 0.6f, 1f) : new Color(0.4f, 0.85f, 0.3f));
        }
    }

    void DrawBus(float cx, Match m)
    {
        if (m.DoorsOpen)
        {
            Box(cx - 170, 830, 340, 50, new Color(0.03f, 0.04f, 0.1f, 0.65f));
            Box(cx - 150, 841, 72, 28, Color.white);
            Text(cx - 114, 855, "SPACE", 18, new Color(0.05f, 0.05f, 0.1f), 1, 0f);
            Text(cx - 66, 855, "JUMP FROM THE BUS", 24, Color.white, 0, 1.5f);
        }
        Text(cx, 120, $"THE BUS CROSSES THE ISLAND   {Mathf.CeilToInt(Mathf.Max(0f, m.busTime - m.Now))}", 24, Color.white, 1, 2f);
        Text(cx, 150, "M  MAP", 18, new Color(1f, 1f, 1f, 0.8f), 1, 1.5f);
    }

    void DrawAltitude()
    {
        float h = Flight.Height(pc.transform.position);
        Box(40, 400, 6, 260, new Color(0, 0, 0, 0.4f));
        float k = Mathf.Clamp01(h / 160f);
        Box(40, 400 + 260 * (1f - k), 6, 260 * k, Color.white);
        Text(56, 400 + 260 * (1f - k), $"{Mathf.RoundToInt(h)} M", 24, Color.white, 0, 1.5f);
        if (pc.mode == PlayerController.Mode.Skydive)
            Text(56, 690, "SPACE  OPEN GLIDER", 18, new Color(1f, 1f, 1f, 0.85f), 0, 1.5f);
    }

    // victory royale / eliminated screens
    void DrawEnd(float W, float H, float cx, Match m)
    {
        bool won = m.PlayerWon;
        bool lost = m.PlayerOut;
        if (!won && !lost) return;
        if (lost && pc.spectating != null && m.phase != Match.Phase.Ended)
        {
            string who = pc.spectating.displayName.ToUpperInvariant();
            float sw = TextWidth("SPECTATING  " + who, 26) + 60f;
            RBox(cx - sw * 0.5f, 150, sw, 48, new Color(0f, 0.03f, 0.14f, 0.85f), 8f);
            Text(cx, 174, "SPECTATING  " + who, 26, Color.white, 1, 0f);
            Text(cx, 222, $"#{Me.placement}   ELIMINATED BY {(Me.lastAttacker != null ? Me.lastAttacker.displayName.ToUpperInvariant() : "THE STORM")}", 22, new Color(1f, 0.6f, 0.55f), 1, 2f);
            return;
        }
        if (_endAt < 0f) _endAt = UiClock.Now;
        float t = UiClock.Now - _endAt, k = Mathf.Clamp01(t / 0.35f);
        float pop = 1f + (1f - k) * (1f - k) * 0.7f;
        // a band across the screen, fading at its edges; light rays turning behind the title when won
        var band = won ? new Color(0.02f, 0.08f, 0.3f, 0.78f * k) : new Color(0.2f, 0.02f, 0.05f, 0.75f * k);
        Fade(0, 200, W, 90, band, 1);
        Box(0, 290, W, 90, band);
        Fade(0, 380, W, 90, band, 0);
        if (won)
        {
            Rotated(cx, 330, t * 10f, () => Tex(cx - 620, 330 - 620, 1240, 1240, _rays, new Color(1f, 0.82f, 0.3f, 0.3f * k)));
            Text(cx, 238, "#1", 72 * pop, new Color(1f, 0.9f, 0.4f, k), 1, 3f);
            Outlined(cx, 330, "VICTORY ROYALE", 124 * pop, new Color(1f, 0.86f, 0.18f, k), new Color(0.75f, 0.3f, 0.02f, k), 5f);
        }
        else
        {
            Text(cx, 238, $"#{Me.placement}", 72 * pop, new Color(1f, 1f, 1f, k), 1, 3f);
            Outlined(cx, 330, "ELIMINATED", 116 * pop, new Color(1f, 0.42f, 0.38f, k), new Color(0.35f, 0.02f, 0.04f, k), 5f);
        }
        string by = won ? "" : $"BY {(Me.lastAttacker != null ? Me.lastAttacker.displayName.ToUpperInvariant() : "THE STORM")}     ";
        string line = $"{by}ELIMINATIONS {Me.kills}     TIME {(int)(m.Now / 60)}:{(int)(m.Now % 60):00}";
        float lw = TextWidth(line, 22) + 60f;
        RBox(cx - lw * 0.5f, 404, lw, 44, new Color(0f, 0.02f, 0.1f, 0.8f * k), 22f);
        Text(cx, 426, line, 22, new Color(0.9f, 0.92f, 1f, k), 1, 0f);
        if (m.phase == Match.Phase.Ended && m.winner != null && !won) Text(cx, 480, $"WINNER  {m.winner.displayName.ToUpperInvariant()}", 26, new Color(1f, 0.85f, 0.3f), 1, 2f);
        if (pc.source is HumanInput && !SummaryOpen && !ReplayPlayer.Active)
        {
            float hw = TextWidth("MATCH SUMMARY", 20) + 90f;
            KeyHint(cx - hw * 0.5f, 520, "ENTER", "MATCH SUMMARY");
        }
    }

    float _endAt = -1f;

    /// big title text with a thick coloured rim and a drop shadow (VICTORY ROYALE)
    void Outlined(float x, float y, string t, float size, Color fill, Color rim, float width)
    {
        Text(x + width * 0.8f, y + width * 1.4f, t, size, new Color(0f, 0f, 0f, 0.45f * fill.a), 1, 0f);
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.PI / 6f;
            Text(x + Mathf.Cos(a) * width, y + Mathf.Sin(a) * width, t, size, rim, 1, 0f);
        }
        Text(x, y, t, size, fill, 1, 0f);
    }

    void DrawHelp(float W)
    {
        if (!(showHelp && Time.time < _helpUntil))
        {
            if (Cursor.lockState != CursorLockMode.Locked && pc.source is HumanInput)
                Text(W * 0.5f, 240, "CLICK TO PLAY   (F1 CONTROLS)", 26, Color.white, 1, 2f);
            return;
        }
        string[] lines = {
            "WASD MOVE   SHIFT SPRINT   SPACE JUMP / GLIDER",
            "LMB FIRE / USE   RMB AIM   R RELOAD",
            "E PICK UP / SWAP / SEARCH   M MAP",
            "1-5 SLOTS   F PICKAXE   WHEEL SWITCH",
            "G EDIT A WALL OR FLOOR   CTRL CROUCH / SLIDE",
            "Q Z C V BUILD WALL FLOOR STAIRS CONE   RMB MATERIAL",
            "B EMOTES   F7 FREE CAMERA   F8 HUD   F9 REPLAY",
            "ESC PAUSE / SETTINGS   F1 HIDE" };
        float a = Mathf.Clamp01(_helpUntil - Time.time);
        Box(12, 400, 470, lines.Length * 28 + 16, new Color(0.03f, 0.04f, 0.1f, 0.55f * a));
        for (int i = 0; i < lines.Length; i++)
            Text(24, 422 + i * 28, lines[i], 19, new Color(1f, 1f, 1f, a), 0, 1f);
    }
}
