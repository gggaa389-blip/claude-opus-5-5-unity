using System.Collections.Generic;
using UnityEngine;

/// The player's settings, kept in PlayerPrefs: mouse sensitivity (hip and aimed), field of view, invert
/// Y, graphics quality, volume, the FPS counter, and the key bindings (Fortnite's PC defaults).
public static class Settings
{
    public static float Sensitivity = 2.2f, AdsSensitivity = 0.7f, Fov = 72f, Volume = 0.8f, MusicVolume = 0.8f;
    public static bool InvertY, ShowFps;
    public static int Quality = 2;   // 0 low, 1 medium, 2 high
    public static bool Temporary;    // the recorded videos: the defaults, never saved
    public static int WindowMode = 1, FrameLimit, ResIndex = -1;   // display: applied with APPLY
    public static bool VSync;
    public static readonly string[] WindowModes = { "FULLSCREEN", "WINDOWED FULLSCREEN", "WINDOWED" };
    public static readonly int[] FrameLimits = { 0, 30, 60, 120, 144, 240 };
    public static readonly string[] FrameLimitNames = { "UNLIMITED", "30 FPS", "60 FPS", "120 FPS", "144 FPS", "240 FPS" };
    public static readonly string[] OnOff = { "OFF", "ON" };
    static Vector2Int[] _res;

    /// the screen's resolutions, one per size, smallest first
    public static Vector2Int[] Resolutions
    {
        get
        {
            if (_res == null)
            {
                var l = new List<Vector2Int>();
                foreach (var r in Screen.resolutions) { var v = new Vector2Int(r.width, r.height); if (!l.Contains(v) && r.width >= 1024) l.Add(v); }
                var cur = new Vector2Int(Screen.width, Screen.height);
                if (!l.Contains(cur)) l.Add(cur);
                l.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                _res = l.ToArray();
            }
            return _res;
        }
    }

    public static string[] ResolutionNames
    {
        get
        {
            var r = Resolutions;
            var n = new string[r.Length];
            for (int i = 0; i < r.Length; i++) n[i] = $"{r[i].x} x {r[i].y}";
            return n;
        }
    }

    /// window mode and resolution (the videos keep their own)
    public static void ApplyDisplay()
    {
        if (Temporary) return;
        var r = Resolutions;
        if (ResIndex < 0 || ResIndex >= r.Length) ResIndex = System.Array.IndexOf(r, new Vector2Int(Screen.width, Screen.height));
        var size = ResIndex >= 0 ? r[ResIndex] : new Vector2Int(Screen.width, Screen.height);
        var mode = WindowMode == 0 ? FullScreenMode.ExclusiveFullScreen : WindowMode == 1 ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
        Screen.SetResolution(size.x, size.y, mode);
    }
    public static readonly string[] QualityNames = { "LOW", "MEDIUM", "HIGH" };

    public static readonly (string action, string label, KeyCode key)[] Actions =
    {
        ("Forward", "MOVE FORWARD", KeyCode.W), ("Back", "MOVE BACKWARD", KeyCode.S), ("Left", "MOVE LEFT", KeyCode.A), ("Right", "MOVE RIGHT", KeyCode.D),
        ("Jump", "JUMP", KeyCode.Space), ("Sprint", "SPRINT", KeyCode.LeftShift), ("Crouch", "CROUCH / SLIDE", KeyCode.LeftControl),
        ("Reload", "RELOAD", KeyCode.R), ("Interact", "USE / PICK UP", KeyCode.E), ("Pickaxe", "HARVESTING TOOL", KeyCode.F), ("Map", "MAP", KeyCode.M),
        ("Wall", "BUILD WALL", KeyCode.Q), ("Floor", "BUILD FLOOR", KeyCode.Z), ("Stairs", "BUILD STAIRS", KeyCode.C), ("Cone", "BUILD CONE", KeyCode.V),
        ("Edit", "EDIT", KeyCode.G), ("Emote", "EMOTE WHEEL", KeyCode.B),
    };
    static readonly Dictionary<string, KeyCode> _keys = new Dictionary<string, KeyCode>();
    static bool _loaded;
    static float _shadowDist, _lodBias;
    static ShadowQuality _shadows;
    static int _aa;

    public static KeyCode Key(string action)
    {
        if (!_loaded) Load();
        return _keys.TryGetValue(action, out var k) ? k : KeyCode.None;
    }

    public static void SetKey(string action, KeyCode key)
    {
        // a key does one thing: whatever had it before gets the new action's old key
        foreach (var a in Actions)
            if (a.action != action && Key(a.action) == key) _keys[a.action] = Key(action);
        _keys[action] = key;
    }

    public static void ResetKeys()
    {
        foreach (var a in Actions) _keys[a.action] = a.key;
    }

    public static void Load()
    {
        _loaded = true;
        _shadowDist = QualitySettings.shadowDistance; _lodBias = QualitySettings.lodBias;
        _shadows = QualitySettings.shadows; _aa = QualitySettings.antiAliasing;
        if (Temporary) { ResetKeys(); Apply(); return; }
        WindowMode = Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen ? 0 : Screen.fullScreenMode == FullScreenMode.Windowed ? 2 : 1;
        ResIndex = System.Array.IndexOf(Resolutions, new Vector2Int(Screen.width, Screen.height));
        VSync = PlayerPrefs.GetInt("vsync", 0) == 1;
        FrameLimit = Mathf.Clamp(PlayerPrefs.GetInt("frameLimit", 0), 0, FrameLimits.Length - 1);
        Sensitivity = PlayerPrefs.GetFloat("sens", Sensitivity);
        AdsSensitivity = PlayerPrefs.GetFloat("adsSens", AdsSensitivity);
        Fov = PlayerPrefs.GetFloat("fov", Fov);
        Volume = PlayerPrefs.GetFloat("volume", Volume);
        MusicVolume = PlayerPrefs.GetFloat("music", MusicVolume);
        InvertY = PlayerPrefs.GetInt("invertY", 0) == 1;
        ShowFps = PlayerPrefs.GetInt("fps", 0) == 1;
        Quality = PlayerPrefs.GetInt("quality", Quality);
        foreach (var a in Actions) _keys[a.action] = (KeyCode)PlayerPrefs.GetInt("key_" + a.action, (int)a.key);
        Apply();
    }

    public static void Save()
    {
        if (Temporary) { Apply(); return; }
        PlayerPrefs.SetFloat("sens", Sensitivity);
        PlayerPrefs.SetFloat("adsSens", AdsSensitivity);
        PlayerPrefs.SetFloat("fov", Fov);
        PlayerPrefs.SetFloat("volume", Volume);
        PlayerPrefs.SetFloat("music", MusicVolume);
        PlayerPrefs.SetInt("invertY", InvertY ? 1 : 0);
        PlayerPrefs.SetInt("fps", ShowFps ? 1 : 0);
        PlayerPrefs.SetInt("quality", Quality);
        PlayerPrefs.SetInt("vsync", VSync ? 1 : 0);
        PlayerPrefs.SetInt("frameLimit", FrameLimit);
        foreach (var a in Actions) PlayerPrefs.SetInt("key_" + a.action, (int)Key(a.action));
        PlayerPrefs.Save();
        Apply();
    }

    /// quality: high is the project's own settings; medium and low shorten shadows and lower detail
    public static void Apply()
    {
        AudioListener.volume = Volume;
        float k = Quality == 2 ? 1f : Quality == 1 ? 0.6f : 0.3f;
        QualitySettings.shadowDistance = _shadowDist * k;
        QualitySettings.lodBias = _lodBias * (Quality == 2 ? 1f : Quality == 1 ? 0.75f : 0.5f);
        QualitySettings.shadows = Quality == 0 ? ShadowQuality.HardOnly : _shadows;
        QualitySettings.antiAliasing = Quality == 2 ? _aa : 0;
        if (!Temporary)
        {
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = FrameLimits[FrameLimit] > 0 ? FrameLimits[FrameLimit] : -1;
        }
    }
}
