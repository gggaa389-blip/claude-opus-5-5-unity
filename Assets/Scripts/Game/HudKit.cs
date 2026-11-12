using System.Collections.Generic;
using UnityEngine;

/// The front end's drawing kit, in the HUD's 1080p units: rounded panels and outlines, fades, the icon
/// font (Windows' Segoe icon fonts), wrapped body text, key hints, rarity tiles, and
/// the textures the screens share (vignette, light rays, emote wheel slice). Every control records where
/// it was drawn, so the scripted mouse of the videos can aim at it by name.
public partial class Hud
{
    /// the centre of every control as last drawn, by id
    public static readonly Dictionary<string, Vector2> Spots = new Dictionary<string, Vector2>();

    // Fortnite's front-end palette
    static readonly Color Navy = new Color(0.03f, 0.07f, 0.2f), NavyLight = new Color(0.07f, 0.16f, 0.4f), Cyan = new Color(0.28f, 0.82f, 1f),
                          Ink = new Color(0.05f, 0.06f, 0.13f), White70 = new Color(1f, 1f, 1f, 0.7f), Green = new Color(0.35f, 0.95f, 0.45f);
    // rarity tile colours: inner and outer of the radial gradient (common, uncommon, rare, epic, legendary)
    static readonly Color[] RarityIn = { new Color(0.62f, 0.66f, 0.7f), new Color(0.42f, 0.86f, 0.2f), new Color(0.27f, 0.72f, 1f), new Color(0.78f, 0.38f, 1f), new Color(1f, 0.62f, 0.2f) };
    static readonly Color[] RarityOut = { new Color(0.28f, 0.3f, 0.35f), new Color(0.12f, 0.4f, 0.08f), new Color(0.05f, 0.25f, 0.62f), new Color(0.3f, 0.08f, 0.55f), new Color(0.6f, 0.22f, 0.04f) };

    // Segoe MDL2 / Fluent icon glyphs
    public const string IcGear = "", IcPeople = "", IcPerson = "", IcAdd = "", IcBell = "", IcMenu = "",
                        IcMonitor = "", IcVolume = "", IcKeyboard = "", IcGame = "", IcMusic = "", IcBack = "",
                        IcLeft = "", IcRight = "", IcStar = "", IcTrophy = "", IcClock = "", IcPlay = "",
                        IcExit = "", IcGlobe = "", IcCheck = "", IcLock = "", IcRefresh = "", IcInfo = "";

    Font _iconFont;
    GUIStyle _ist;
    Texture2D _ramp, _radial, _rays, _wedge, _ring;

    void MakeKit()
    {
        _iconFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 32);
        _ist = new GUIStyle(_st) { font = _iconFont, alignment = TextAnchor.MiddleCenter };

        // a vertical ramp: opaque at the top, clear at the bottom
        _ramp = new Texture2D(2, 128, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < 128; y++) { var c = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, y / 127f)); _ramp.SetPixel(0, y, c); _ramp.SetPixel(1, y, c); }
        _ramp.Apply();
        // a radial glow: opaque in the middle
        const int N = 128;
        _radial = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        // light rays round the middle (the Victory Royale banner)
        _rays = new Texture2D(256, 256, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float r = new Vector2(x + 0.5f - N * 0.5f, y + 0.5f - N * 0.5f).magnitude / (N * 0.5f);
                _radial.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r)));
            }
        _radial.Apply();
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                var d = new Vector2(x + 0.5f - 128f, y + 0.5f - 128f);
                float r = d.magnitude / 128f, a = Mathf.Atan2(d.y, d.x);
                float ray = Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 8f)), 6f);
                _rays.SetPixel(x, y, new Color(1f, 1f, 1f, ray * Mathf.Clamp01(1f - r) * Mathf.Clamp01(r * 4f)));
            }
        _rays.Apply();
        // one slice of the emote wheel (60 degrees with gaps), pointing up, and a thin ring
        const int M = 512;
        _wedge = new Texture2D(M, M, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        _ring = new Texture2D(M, M, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < M; y++)
            for (int x = 0; x < M; x++)
            {
                var d = new Vector2(x + 0.5f - M * 0.5f, y + 0.5f - M * 0.5f);
                float r = d.magnitude / (M * 0.5f);
                float ang = Mathf.Abs(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg);       // 0 straight up (texture y is up)
                float edge = Mathf.Min(Mathf.Min(r - 0.42f, 1f - r) * M * 0.5f, (29f - ang) * Mathf.Deg2Rad * r * M * 0.5f);
                _wedge.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(edge)));
                float ring = Mathf.Min(r - 0.4f, 0.415f - r) * M * 0.5f;
                _ring.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(ring + 1f)));
            }
        _wedge.Apply(); _ring.Apply();
    }

    // ---------------------------------------------------------------- shapes

    void RBox(float x, float y, float w, float h, Color c, float radius = 8f)
    {
        if (Event.current.type != EventType.Repaint) return;
        GUI.DrawTexture(R(x, y, w, h), _white, ScaleMode.StretchToFill, true, 0f, c, 0f, radius * s);
    }

    void ROutline(float x, float y, float w, float h, Color c, float width = 2f, float radius = 8f)
    {
        if (Event.current.type != EventType.Repaint) return;
        GUI.DrawTexture(R(x, y, w, h), _white, ScaleMode.StretchToFill, true, 0f, c, width * s, radius * s);
    }

    /// a fade: dir 0 opaque at the top, 1 at the bottom, 2 on the left, 3 on the right
    void Fade(float x, float y, float w, float h, Color c, int dir)
    {
        if (Event.current.type != EventType.Repaint) return;
        var old = GUI.color; GUI.color = c;
        var r = R(x, y, w, h);
        if (dir < 2) GUI.DrawTextureWithTexCoords(r, _ramp, dir == 0 ? new Rect(0, 0, 1, 1) : new Rect(0, 1, 1, -1));
        else
        {
            // the ramp turned on its side
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(dir == 2 ? -90f : 90f, r.center);
            var rr = new Rect(r.center.x - r.height * 0.5f, r.center.y - r.width * 0.5f, r.height, r.width);
            GUI.DrawTextureWithTexCoords(rr, _ramp, new Rect(0, 0, 1, 1));
            GUI.matrix = m;
        }
        GUI.color = old;
    }

    /// a panel with Fortnite's look: a dark blue body, lighter at the top, and a thin bright rim
    void Panel(float x, float y, float w, float h, float alpha = 0.86f, float radius = 10f)
    {
        RBox(x, y, w, h, new Color(Navy.r, Navy.g, Navy.b, alpha), radius);
        Fade(x, y, w, Mathf.Min(h, 140f), new Color(NavyLight.r, NavyLight.g, NavyLight.b, alpha * 0.7f), 0);
        ROutline(x, y, w, h, new Color(1f, 1f, 1f, 0.12f), 1.5f, radius);
    }

    /// a rarity tile: the radial gradient of Fortnite's item cards
    void RarityTile(float x, float y, float w, float h, int rarity, float radius = 6f)
    {
        rarity = Mathf.Clamp(rarity, 0, 4);
        RBox(x, y, w, h, RarityOut[rarity], radius);
        Tex(x - w * 0.25f, y - h * 0.1f, w * 1.5f, h * 1.3f, _radial, new Color(RarityIn[rarity].r, RarityIn[rarity].g, RarityIn[rarity].b, 0.95f));
        Fade(x, y + h * 0.62f, w, h * 0.38f, new Color(0f, 0f, 0.05f, 0.55f), 1);
        ROutline(x, y, w, h, new Color(1f, 1f, 1f, 0.25f), 2f, radius);
    }

    // ---------------------------------------------------------------- text

    float Icon(float x, float y, string glyph, float size, Color c, int anchor = 1)
    {
        if (_ist == null) return 0f;
        int fs = Mathf.Max(6, Mathf.RoundToInt(size * s));
        _ist.fontSize = fs;
        _gc.text = glyph;
        // the icon font's sizes are kept under a negative font size so they never mix with the text's
        if (!_sizes.TryGetValue((glyph, -fs), out var sz)) { sz = _ist.CalcSize(_gc); _sizes[(glyph, -fs)] = sz; }
        float w = sz.x / s, h = sz.y / s;
        if (Event.current.type != EventType.Repaint || c.a <= 0.003f) return w;
        float left = anchor == 0 ? x : anchor == 1 ? x - w * 0.5f : x - w;
        _ist.normal.textColor = c;
        GUI.Label(R(left, y - h * 0.5f, w + 2, h), _gc, _ist);
        return w;
    }

    /// body text wrapped inside a width; returns the height used. Wrapped by hand and drawn line by line
    /// with the HUD's own style: IMGUI's word wrap, and a second label style, drew the text of an earlier
    /// label in this Unity version.
    float Body(float x, float y, float w, string t, float size, Color c)
    {
        if (string.IsNullOrEmpty(t)) return 0f;
        float lineH = size * 1.3f, ly = y;
        string line = "";
        foreach (var word in t.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && TextWidth(next, size) > w)
            {
                Text(x, ly + lineH * 0.5f, line, size, c, 0, 0f);
                ly += lineH;
                line = word;
            }
            else line = next;
        }
        if (line.Length > 0) { Text(x, ly + lineH * 0.5f, line, size, c, 0, 0f); ly += lineH; }
        return ly - y;
    }

    /// a key hint: the key in a light rounded chip, then what it does
    float KeyHint(float x, float y, string key, string label, float size = 20f)
    {
        float kw = Mathf.Max(TextWidth(key, size) + 18f, size * 1.5f);
        RBox(x, y - size * 0.8f, kw, size * 1.6f, new Color(1f, 1f, 1f, 0.9f), 5f);
        Text(x + kw * 0.5f, y, key, size, Ink, 1, 0f);
        return kw + 10f + Text(x + kw + 10f, y, label, size, Color.white, 0, 1.5f) + 26f;
    }

    // ---------------------------------------------------------------- controls

    bool Hit(string id, float x, float y, float w, float h, out bool hover)
    {
        Spots[id] = new Vector2(x + w * 0.5f, y + h * 0.5f);
        hover = Hover(x, y, w, h);
        if (hover && _click) { _click = false; return true; }
        return false;
    }

    /// the big yellow button (PLAY!, PLAY AGAIN)
    bool YellowButton(string id, float x, float y, float w, float h, string label, float size)
    {
        bool clicked = Hit(id, x, y, w, h, out bool hover);
        RBox(x + 2, y + 5, w, h, new Color(0f, 0f, 0f, 0.35f), 10f);
        RBox(x, y, w, h, hover ? new Color(1f, 0.95f, 0.45f) : new Color(1f, 0.9f, 0.13f), 10f);
        Fade(x, y + h * 0.55f, w, h * 0.45f, new Color(0.95f, 0.66f, 0f, 0.55f), 1);
        if (hover) ROutline(x - 4, y - 4, w + 8, h + 8, Color.white, 3f, 13f);
        Text(x + w * 0.5f, y + h * 0.5f, label, size, Ink, 1, 0f);
        if (clicked) Sfx.I?.Play2D("ui_play", 0.6f);
        return clicked;
    }

    /// a dark rounded button, light when hovered or selected
    bool DarkButton(string id, float x, float y, float w, float h, string label, float size, bool on = false, string glyph = null)
    {
        bool clicked = Hit(id, x, y, w, h, out bool hover);
        RBox(x, y, w, h, on ? new Color(1f, 1f, 1f, 0.95f) : hover ? new Color(0.2f, 0.4f, 0.85f, 0.95f) : new Color(0.05f, 0.12f, 0.32f, 0.88f), 8f);
        if (!on) ROutline(x, y, w, h, new Color(1f, 1f, 1f, hover ? 0.6f : 0.18f), 1.5f, 8f);
        var tc = on ? Ink : Color.white;
        if (glyph != null)
        {
            float tw = TextWidth(label, size) + size * 1.3f;
            Icon(x + w * 0.5f - tw * 0.5f + size * 0.45f, y + h * 0.5f, glyph, size * 0.85f, tc);
            Text(x + w * 0.5f - tw * 0.5f + size * 1.3f, y + h * 0.5f, label, size, tc, 0, 0f);
        }
        else Text(x + w * 0.5f, y + h * 0.5f, label, size, tc, 1, on ? 0f : 1.5f);
        if (clicked) Sfx.I?.Play2D("ui_click", 0.6f);
        return clicked;
    }

    /// a round icon button (the top bar's gear, friends, menu)
    bool IconButton(string id, float cx, float cy, float r, string glyph, Color c)
    {
        bool clicked = Hit(id, cx - r, cy - r, r * 2f, r * 2f, out bool hover);
        Tex(cx - r, cy - r, r * 2f, r * 2f, _disk, hover ? new Color(1f, 1f, 1f, 0.25f) : new Color(0f, 0f, 0.1f, 0.35f));
        Icon(cx, cy, glyph, r * 0.95f, c);
        if (clicked) Sfx.I?.Play2D("ui_click", 0.6f);
        return clicked;
    }

    /// Fortnite's option selector: < VALUE >
    int Selector(string id, float x, float y, float w, int v, string[] names)
    {
        float h = 48f;
        RBox(x, y - h * 0.5f, w, h, new Color(0f, 0.02f, 0.1f, 0.55f), 6f);
        if (Hit(id + "<", x, y - h * 0.5f, 56f, h, out bool hl)) { v = (v + names.Length - 1) % names.Length; Sfx.I?.Play2D("ui_click", 0.5f); }
        if (Hit(id + ">", x + w - 56f, y - h * 0.5f, 56f, h, out bool hr)) { v = (v + 1) % names.Length; Sfx.I?.Play2D("ui_click", 0.5f); }
        Icon(x + 28f, y, IcLeft, 22f, hl ? Yellow : Color.white);
        Icon(x + w - 28f, y, IcRight, 22f, hr ? Yellow : Color.white);
        Text(x + w * 0.5f, y, names[v], 26f, Color.white, 1, 0f);
        // one dot per choice
        float dx = 14f, d0 = x + w * 0.5f - (names.Length - 1) * dx * 0.5f;
        for (int i = 0; i < names.Length && names.Length <= 8; i++) RBox(d0 + i * dx - 4f, y + 17f, 8f, 4f, i == v ? Yellow : new Color(1f, 1f, 1f, 0.3f), 2f);
        return v;
    }

    /// a slider with its number in a box on the right
    float NumSlider(string id, float x, float y, float w, float v, float min, float max, string fmt)
    {
        float k = Mathf.InverseLerp(min, max, v);
        Spots[id] = new Vector2(x + w * k, y);
        RBox(x, y - 4, w, 8, new Color(1f, 1f, 1f, 0.18f), 4f);
        RBox(x, y - 4, w * k, 8, Cyan, 4f);
        Tex(x + w * k - 13f, y - 13f, 26f, 26f, _disk, Color.white);
        if (_held && _ptr.x > x - 14 && _ptr.x < x + w + 14 && Mathf.Abs(_ptr.y - y) < 24f) v = Mathf.Lerp(min, max, Mathf.Clamp01((_ptr.x - x) / w));
        RBox(x + w + 22f, y - 22f, 100f, 44f, new Color(0f, 0.02f, 0.1f, 0.55f), 6f);
        Text(x + w + 72f, y, string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, v), 24f, Color.white, 1, 0f);
        return v;
    }

    /// a level badge: a rounded square in the season's blue with the level in it
    void LevelBadge(float x, float y, float size, int level)
    {
        RBox(x, y, size, size, new Color(0.15f, 0.45f, 1f), size * 0.22f);
        Fade(x, y, size, size * 0.6f, new Color(0.55f, 0.85f, 1f, 0.6f), 0);
        ROutline(x, y, size, size, new Color(1f, 1f, 1f, 0.5f), 2f, size * 0.22f);
        Text(x + size * 0.5f, y + size * 0.52f, level.ToString(), size * 0.55f, Color.white, 1, 1.5f);
    }

    /// V-Bucks: a light blue coin with a V
    float VBucks(float x, float y, int amount)
    {
        Tex(x, y - 15f, 30f, 30f, _disk, new Color(0.45f, 0.85f, 1f));
        Tex(x + 4f, y - 11f, 22f, 22f, _disk, new Color(0.15f, 0.5f, 0.95f));
        Text(x + 15f, y + 1f, "V", 18f, Color.white, 1, 0f);
        return 38f + Text(x + 38f, y, amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), 26f, Color.white, 0, 1.5f);
    }
}
