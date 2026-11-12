using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

/// The lobby before a match, set up like Fortnite's: the player's character stands on an open hilltop of
/// the island, lit from the front by the sun, with the island softly out of focus behind (depth of field),
/// two dim party slots beside it and the lobby music. The HUD draws the screens over it (PLAY, LOCKER,
/// CAREER); PLAY matchmakes for a few seconds, joins, and the loading screen hides the match setup.
public class Lobby : MonoBehaviour
{
    public static Lobby I;
    public GameBootstrap boot;
    public bool matchmaking, joining;
    public float mmStart, mmTime = 3.2f, joinTime = 1.2f;
    public int emote = -1, bots, tab;        // tab: 0 PLAY, 1 LOCKER, 2 CAREER
    public RenderTexture thumb, portrait;     // the island for the mode card, the character for the locker
    public Vector3 feet, face, side;
    public readonly Vector3[] ghosts = new Vector3[2];

    GameObject _set, _bus;
    Animator _anim;
    GameObject _model;
    readonly Animator[] _ghostAnims = new Animator[2];
    AudioSource _music;
    PostProcessVolume _dof;
    float _emoteUntil, _camSide;
    int _frames;
    Camera _cam;

    public static Lobby Show(GameBootstrap boot, int bots)
    {
        var l = boot.gameObject.AddComponent<Lobby>();
        l.boot = boot;
        l.bots = bots;
        I = l;
        return l;
    }

    void OnDestroy()
    {
        if (I == this) I = null;
        if (_dof != null) RuntimeUtilities.DestroyVolume(_dof, true, true);
    }

    public float MmElapsed => UiClock.Now - mmStart;

    void Start()
    {
        _cam = Camera.main;
        var sun = RenderSettings.sun;
        var toSun = sun != null ? -sun.transform.forward : new Vector3(0.4f, 0.6f, -0.6f);
        face = new Vector3(toSun.x, 0f, toSun.z).normalized;
        if (face.sqrMagnitude < 0.5f) face = Vector3.back;
        side = Vector3.Cross(Vector3.up, face);   // the camera's left

        // the character faces the sun (lit from the front, the camera on the sunny side); the spot: open,
        // flat, dry and high, with the most open view behind it and room for the camera in front
        float best = -1f;
        for (float x = -190f; x <= 190f; x += 6f)
            for (float z = -170f; z <= 190f; z += 6f)
            {
                var p = new Vector3(x, 0f, z);
                float g = Traversal.Ground(p);
                if (g < 6f || !Traversal.Dry(p) || !Traversal.Flat(p, 2.5f) || !Traversal.Free(p, 2.6f, 4f)) continue;
                var camAt = p + face * 4.4f;
                if (!Traversal.Free(camAt, 1.2f, 3f) || Mathf.Abs(Traversal.Ground(camAt) - g) > 1.2f) continue;
                if (!Traversal.Free(p + side * 1.8f - face, 1f, 3f) || !Traversal.Free(p - side * 1.8f - face, 1f, 3f)) continue;
                p.y = g;
                // how far the view goes behind, at head height, across a few directions
                float open = 0f;
                for (int k = -2; k <= 2; k++)
                {
                    var dir = Quaternion.Euler(0f, k * 12f, 0f) * -face;
                    open += Physics.Raycast(p + Vector3.up * 1.7f, dir, out var h, 120f, GameLayers.Env, QueryTriggerInteraction.Ignore) ? h.distance : 120f;
                }
                // tree trunks and buildings behind (canopies have no colliders): fewer is a wider view
                int clutter = 0;
                for (float d = 10f; d <= 60f; d += 10f)
                    foreach (var c in Physics.OverlapSphere(p - face * d + Vector3.up * 2f, 5f, GameLayers.Env, QueryTriggerInteraction.Ignore))
                        if (!(c is TerrainCollider)) clutter++;
                float score = open + g * 2f - clutter * 8f;
                if (score <= best) continue;
                best = score;
                feet = p;
            }
        if (best < 0f) feet = new Vector3(0f, Traversal.Ground(Vector3.zero), 0f);
        ghosts[0] = Grounded(feet + side * 1.75f - face * 0.9f);
        ghosts[1] = Grounded(feet - side * 1.75f - face * 0.9f);

        // the bus would hang in the sky behind the character
        foreach (var t in boot.islandRoot.GetComponentsInChildren<Transform>(true))
            if (t.name == Match.BusObject && t.gameObject.activeSelf) { _bus = t.gameObject; _bus.SetActive(false); }

        _set = new GameObject("LobbySet");
        Dress();
        // the empty party slots: dim blue silhouettes (always the default outfit's shape)
        var src = boot.playerPrefab.transform.Find("Model").gameObject;
        var ghostMat = new Material(GameAssets.I.ghostMat);
        ghostMat.SetColor("_Color", new Color(0.12f, 0.26f, 0.7f, 0.14f));
        for (int i = 0; i < 2; i++)
        {
            var g = Instantiate(src, ghosts[i], Quaternion.LookRotation(face + side * (i == 0 ? -0.15f : 0.15f)), _set.transform);
            foreach (var r in g.GetComponentsInChildren<Renderer>())
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int k = 0; k < mats.Length; k++) mats[k] = ghostMat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            _ghostAnims[i] = g.GetComponent<Animator>();
            _ghostAnims[i].keepAnimatorStateOnDisable = true;   // the slots hide on the other pages
        }
        // a warm key light from the front, a cool rim light from behind
        Light(feet + face * 2.6f + side * 1.2f + Vector3.up * 2.4f, new Color(1f, 0.9f, 0.78f), 1.3f, 7f);
        Light(feet - face * 1.6f - side * 0.8f + Vector3.up * 2.6f, new Color(0.45f, 0.7f, 1f), 2.2f, 5f);

        // depth of field: the character sharp, the island behind soft
        var layer = _cam != null ? _cam.GetComponent<PostProcessLayer>() : null;
        if (layer != null)
        {
            var dof = ScriptableObject.CreateInstance<DepthOfField>();
            dof.enabled.Override(true);
            dof.focusDistance.Override(4.4f);
            dof.aperture.Override(1.6f);
            dof.focalLength.Override(70f);
            dof.kernelSize.Override(KernelSize.Large);
            int bit = 0;
            for (int b = 0; b < 32; b++) if ((layer.volumeLayer.value & (1 << b)) != 0) { bit = b; break; }
            _dof = PostProcessManager.instance.QuickVolume(bit, 100f, dof);
        }

        thumb = Snapshot(480, 270, IslandView(out var look), look, 50f);
        // the poses start after that snapshot: it switches the set off and on, and an animator switched on
        // anew starts in its default state, the rifle stance (with no rifle in hand)
        _anim.Play("LobbyIdle", 0, 0f);
        for (int i = 0; i < _ghostAnims.Length; i++) _ghostAnims[i].Play("LobbyIdle", 0, 0.35f + i * 0.3f);
        if (!UiPointer.scripted) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        _music = Sfx.I?.Loop("lobby", transform, 0.55f, false);
    }

    static Vector3 Grounded(Vector3 p) { p.y = Traversal.Ground(p); return p; }

    /// the character in the outfit picked in the locker
    void Dress()
    {
        var src = boot.OutfitPrefab.transform.Find("Model").gameObject;
        _model = Instantiate(src, feet, Quaternion.LookRotation(face), _set.transform);
        _anim = _model.GetComponent<Animator>();
        _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _anim.keepAnimatorStateOnDisable = true;
    }

    /// the locker's outfit tiles: change clothes on the spot, keep the choice, render a new portrait
    public void SetOutfit(int i)
    {
        int n = GameAssets.I.playerPrefabs != null ? GameAssets.I.playerPrefabs.Length : 0;
        if (n == 0 || i < 0 || i >= n || i == Profile.outfit) return;
        Profile.outfit = i;
        Profile.Save();
        if (_model != null) Destroy(_model);
        Dress();
        _anim.Play(emote >= 0 ? GameAssets.Emotes[emote] : "LobbyIdle", 0, 0f);
        if (portrait != null) { portrait.Release(); Destroy(portrait); portrait = null; }
        _frames = 0;
    }

    void Light(Vector3 at, Color c, float intensity, float range)
    {
        var l = new GameObject("LobbyLight").AddComponent<Light>();
        l.transform.SetParent(_set.transform, false);
        l.transform.position = at;
        l.type = LightType.Point;
        l.color = c;
        l.intensity = intensity;
        l.range = range;
    }

    /// a high view of the island from the south-east sea
    public static Vector3 IslandView(out Vector3 look)
    {
        look = new Vector3(0f, 10f, 10f);
        return new Vector3(170f, 120f, -230f);
    }

    /// the main camera renders one picture into a texture (with its post-processing), then goes back
    public RenderTexture Snapshot(int w, int h, Vector3 from, Vector3 at, float fov, bool withSet = false)
    {
        if (_cam == null) return null;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var pos = _cam.transform.position; var rot = _cam.transform.rotation; float oldFov = _cam.fieldOfView;
        bool setOn = _set != null && _set.activeSelf;
        if (_set != null && !withSet) _set.SetActive(false);
        if (_dof != null) _dof.enabled = false;
        _cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
        _cam.fieldOfView = fov;
        _cam.targetTexture = rt;
        _cam.Render();
        _cam.targetTexture = null;
        _cam.transform.SetPositionAndRotation(pos, rot);
        _cam.fieldOfView = oldFov;
        if (_set != null) _set.SetActive(setOn);
        if (_dof != null) _dof.enabled = true;
        return rt;
    }

    /// where a point is on screen, in the HUD's 1080p units (y down); false behind the camera
    public bool ScreenOf(Vector3 world, out Vector2 p)
    {
        p = Vector2.zero;
        if (_cam == null) return false;
        var v = _cam.WorldToScreenPoint(world);
        float s = Screen.height / 1080f;
        p = new Vector2(v.x / s, (Screen.height - v.y) / s);
        return v.z > 0f;
    }

    public void PlayEmote(int k)
    {
        emote = k;
        _emoteUntil = UiClock.Now + 7f;
        _anim.CrossFadeInFixedTime(GameAssets.Emotes[k], 0.25f);
    }

    public void Play()
    {
        if (matchmaking) return;
        matchmaking = true;
        joining = false;
        mmStart = UiClock.Now;
        tab = 0;
    }

    public void Cancel() { matchmaking = false; joining = false; }

    void Update()
    {
        float t = UiClock.Now;
        // the locker's portrait, once the idle pose is on (an animator shows its bind pose on the first frame)
        if (portrait == null && ++_frames > 3)
        {
            var head = _anim.GetBoneTransform(HumanBodyBones.Head);
            var face0 = head != null ? head.position + Vector3.up * 0.14f : feet + Vector3.up * 1.48f;   // the outfit's own face height
            portrait = Snapshot(256, 256, face0 + face * 1.5f + Vector3.up * 0.14f + side * 0.15f, face0, 30f, true);
        }
        if (emote >= 0 && t > _emoteUntil) { emote = -1; _anim.CrossFadeInFixedTime("LobbyIdle", 0.3f); }
        // the camera: in front at chest height; the locker and career pages put the character on the right
        float wantSide = tab == 0 ? 0f : -1.35f;
        _camSide = Mathf.MoveTowards(_camSide, wantSide, UiClock.Delta * 4f);
        if (_cam != null)
        {
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Abs(_camSide) / 1.35f);
            var drift = side * 0.12f * Mathf.Sin(t * 0.2f);
            var look = feet + Vector3.up * 1.02f + side * (1.35f * k) + drift * 0.5f;
            var at = feet + face * 4.4f + Vector3.up * 1.12f + side * (1.35f * k) + drift;
            _cam.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look - at));
            _cam.fieldOfView = 38f;
        }
        foreach (var g in _ghostAnims)
            if (g != null && g.gameObject.activeSelf != (tab == 0)) g.gameObject.SetActive(tab == 0);   // the party slots only on PLAY
        if (matchmaking && !joining && t - mmStart > mmTime) { joining = true; mmStart = t; }
        if (joining && t - mmStart > joinTime) StartCoroutine(Begin());
    }

    /// the loading screen goes up first; the match is set up behind it on the next frame
    IEnumerator Begin()
    {
        enabled = false;
        var from = Lobby.IslandView(out var look);
        var art = Snapshot(1920, 1080, from + new Vector3(-60f, -40f, 40f), look, 55f);
        Hud.StartLoading(art, _music);
        if (_dof != null) { RuntimeUtilities.DestroyVolume(_dof, true, true); _dof = null; }
        Destroy(_set);
        if (_bus != null) _bus.SetActive(true);
        I = null;
        yield return null;
        boot.BeginMatch();
        Time.timeScale = 0f;   // the bus waits while the loading screen is up
        Destroy(this);
    }
}
