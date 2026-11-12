using System.Collections.Generic;
using UnityEngine;

/// The sound effects: synthesised at start-up from filtered noise and decaying tones, the lobby and
/// emote music too (original loops: drums, bass and chords). The gunshots are recordings instead (CC0,
/// Resources/Sfx, see SOURCE.txt there), a near and a far one per gun; a clip in Resources/Sfx replaces
/// the synthesised one of the same name. Three beds play all the time: the island's ambience (wind,
/// birds), the wind of falling and the storm's rumble, the last two at the levels the game sets.
public class Sfx : MonoBehaviour
{
    public static Sfx I;
    public static float windLevel, stormLevel;   // 0..1, set by the player controller and the HUD
    AudioSource _ui, _amb, _wind, _storm;
    // the music loops (lobby, emote beats) and their own volume, scaled by the music setting
    static readonly Dictionary<AudioSource, float> _music = new Dictionary<AudioSource, float>();

    /// change a music loop's own volume (the loading screen fades the lobby music out)
    public static void SetMusicVolume(AudioSource a, float v) { if (a != null) _music[a] = v; }
    readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
    const int Rate = 44100;

    void Awake()
    {
        I = this;
        _ui = gameObject.AddComponent<AudioSource>();
        _ui.spatialBlend = 0f;
        _ui.playOnAwake = false;
        var rng = new System.Random(7);
        _clips["assault_rifle"] = Shot(rng, 0.22f, 0.55f, 95f, 0.9f);
        _clips["shotgun"] = Shot(rng, 0.45f, 0.35f, 70f, 1.0f);
        _clips["sniper_rifle"] = Shot(rng, 0.9f, 0.75f, 60f, 1.0f);
        _clips["pistol"] = Shot(rng, 0.16f, 0.65f, 140f, 0.75f);
        _clips["rocket_launcher"] = Whoosh(rng, 0.6f);
        _clips["explosion"] = Boom(rng, 1.6f);
        _clips["swing"] = Swing(rng, 0.28f);
        _clips["melee_hit"] = Shot(rng, 0.12f, 0.2f, 180f, 0.6f);
        _clips["hitmarker"] = Tone(1850f, 0.035f, 0.35f);
        _clips["headshot"] = Tone(2600f, 0.06f, 0.4f);
        _clips["kill"] = Chime(new[] { 880f, 1320f }, 0.09f, 0.35f);
        _clips["reload"] = Clicks(rng, new[] { 0f, 0.35f }, 0.5f);
        _clips["empty"] = Clicks(rng, new[] { 0f }, 0.08f);
        _clips["pickup"] = Chime(new[] { 660f, 990f, 1320f }, 0.07f, 0.3f);
        _clips["throw"] = Swing(rng, 0.22f);
        _clips["bounce"] = Shot(rng, 0.06f, 0.1f, 300f, 0.3f);
        _clips["equip"] = Clicks(rng, new[] { 0f, 0.07f }, 0.15f);
        _clips["hit_wood"] = Shot(rng, 0.14f, 0.12f, 140f, 0.7f);
        _clips["hit_stone"] = Shot(rng, 0.1f, 0.5f, 260f, 0.6f);
        _clips["hit_metal"] = Chime(new[] { 1240f, 1870f }, 0.01f, 0.35f);
        _clips["break"] = Boom(rng, 0.7f);
        _clips["build"] = Chime(new[] { 520f, 780f }, 0.05f, 0.25f);
        _clips["splash"] = Whoosh(rng, 0.7f);
        _clips["slide"] = Swing(rng, 0.6f);
        _clips["mantle"] = Swing(rng, 0.25f);
        _clips["edit"] = Clicks(rng, new[] { 0f, 0.05f, 0.1f }, 0.18f);
        _clips["launch"] = Whoosh(rng, 0.9f);
        _clips["rift"] = Chime(new[] { 330f, 495f, 740f }, 0.12f, 0.5f);
        _clips["zip"] = Swing(rng, 0.8f);
        _clips["step_soft"] = Step(rng, 0.1f, 0.05f, 0.5f, 0f);
        _clips["step_hard"] = Step(rng, 0.07f, 0.35f, 0.55f, 0f);
        _clips["step_wood"] = Step(rng, 0.11f, 0.12f, 0.6f, 170f);
        _clips["stroke"] = Swing(rng, 0.45f);
        _clips["ui_click"] = Clicks(rng, new[] { 0f }, 0.06f);
        _clips["ui_play"] = Chime(new[] { 523f, 659f, 784f, 1047f }, 0.06f, 0.3f);
        _clips["beat"] = Music(rng, 112f, 4, false);
        _clips["lobby"] = Music(rng, 96f, 8, true);
        // recorded clips win over the synthesised ones (the synthesised shots sounded like paper)
        foreach (var rec in Resources.LoadAll<AudioClip>("Sfx")) _clips[rec.name] = rec;
        _amb = Bed(Make("ambience", Ambience(rng, 12f)), 0.3f);
        _wind = Bed(Make("wind", WindNoise(rng, 4f)), 0f);
        _storm = Bed(Make("storm", Rumble(rng, 6f)), 0f);
    }

    AudioSource Bed(AudioClip c, float vol)
    {
        var a = gameObject.AddComponent<AudioSource>();
        a.clip = c; a.loop = true; a.volume = vol; a.spatialBlend = 0f; a.playOnAwake = false;
        a.Play();
        return a;
    }

    void Update()
    {
        float dt = UiClock.Delta;
        _wind.volume = Mathf.MoveTowards(_wind.volume, Mathf.Clamp01(windLevel) * 0.55f, dt * 1.5f);
        _storm.volume = Mathf.MoveTowards(_storm.volume, Mathf.Clamp01(stormLevel) * 0.5f, dt * 0.8f);
        List<AudioSource> gone = null;
        foreach (var kv in _music)
        {
            if (kv.Key == null) { (gone ??= new List<AudioSource>()).Add(kv.Key); continue; }
            kv.Key.volume = kv.Value * Settings.MusicVolume;
        }
        if (gone != null) foreach (var g in gone) _music.Remove(g);
    }

    /// a looping sound on something (emote music on a dancer, the lobby music); null if unknown
    public AudioSource Loop(string name, Transform on, float volume, bool spatial)
    {
        if (!_clips.TryGetValue(name, out var c)) return null;
        var go = new GameObject("loop_" + name);
        go.transform.SetParent(on, false);
        var a = go.AddComponent<AudioSource>();
        a.clip = c; a.loop = true; a.volume = volume; a.spatialBlend = spatial ? 1f : 0f; a.minDistance = 5f; a.maxDistance = 120f;
        if (name == "lobby" || name == "beat") { _music[a] = volume; a.volume = volume * Settings.MusicVolume; }
        a.Play();
        return a;
    }

    /// a footstep: a short filtered noise thud, a knock in it for wood
    static AudioClip Step(System.Random rng, float len, float bright, float vol, float knockHz)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * bright;
            float env = Mathf.Min(1f, t * 400f) * Mathf.Exp(-t * 45f);
            float knock = knockHz > 0f ? Mathf.Sin(2 * Mathf.PI * knockHz * t) * Mathf.Exp(-t * 60f) * 0.8f : 0f;
            d[i] = Mathf.Clamp((lp * 2.5f + knock) * env * vol, -1f, 1f);
        }
        return Make("step", d);
    }

    /// wind (slowly breathing low noise) and birds now and then; loops seamlessly
    static float[] Ambience(System.Random rng, float len)
    {
        var d = WindNoise(rng, len);
        for (int i = 0; i < d.Length; i++) d[i] *= 0.6f;
        for (int b = 0; b < 7; b++)
        {
            float at = (float)rng.NextDouble() * (len - 1.2f), f0 = 2200f + (float)rng.NextDouble() * 1800f;
            int chirps = 2 + rng.Next(4);
            for (int c = 0; c < chirps; c++)
            {
                float t0 = at + c * 0.13f;
                for (int i = (int)(t0 * Rate); i < (int)((t0 + 0.09f) * Rate) && i < d.Length; i++)
                {
                    float t = i / (float)Rate - t0;
                    float f = f0 + 900f * Mathf.Sin(t * 70f);
                    d[i] += Mathf.Sin(2 * Mathf.PI * f * t) * 0.12f * Mathf.Sin(Mathf.PI * t / 0.09f);
                }
            }
        }
        return d;
    }

    static float[] WindNoise(System.Random rng, float len)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * 0.04f;
            lp2 += (lp - lp2) * 0.1f;
            float breathe = 0.6f + 0.4f * Mathf.Sin(2 * Mathf.PI * t / len * 2f) * Mathf.Sin(2 * Mathf.PI * t / len * 3f + 1f);
            d[i] = lp2 * 3f * breathe;
        }
        Seam(d);
        return d;
    }

    static float[] Rumble(System.Random rng, float len)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * 0.01f;
            lp2 += (lp - lp2) * 0.02f;
            d[i] = Mathf.Clamp(lp2 * 14f + Mathf.Sin(2 * Mathf.PI * 41f * t) * 0.15f * (0.5f + 0.5f * Mathf.Sin(t * 1.7f)), -1f, 1f);
        }
        Seam(d);
        return d;
    }

    /// cross-fades the end into the start so the loop has no click at the seam
    static void Seam(float[] d)
    {
        int f = Mathf.Min(d.Length / 4, (int)(0.3f * Rate));
        for (int i = 0; i < f; i++)
        {
            float k = i / (float)f;
            d[i] = d[i] * k + d[d.Length - f + i] * (1f - k);
        }
    }

    /// an original loop: kick, snare, hats and a bass line in A minor; the lobby one adds soft chords
    static AudioClip Music(System.Random rng, float bpm, int bars, bool pad)
    {
        float beat = 60f / bpm, len = bars * 4 * beat;
        int n = (int)(len * Rate);
        var d = new float[n];
        void Add(float at, float dur, System.Func<float, float> f, float vol)
        {
            for (int i = (int)(at * Rate); i < Mathf.Min(n, (int)((at + dur) * Rate)); i++) d[i] += f(i / (float)Rate - at) * vol;
        }
        float[] bass = { 55f, 55f, 43.65f, 43.65f, 65.41f, 65.41f, 49f, 49f };       // A A F F C C G G
        float[][] chords = { new[] { 220f, 261.6f, 329.6f }, new[] { 174.6f, 220f, 261.6f }, new[] { 196f, 261.6f, 329.6f }, new[] { 196f, 246.9f, 293.7f } };
        for (int b = 0; b < bars * 4; b++)
        {
            float t0 = b * beat;
            if (b % 2 == 0) Add(t0, 0.35f, t => Mathf.Sin(2 * Mathf.PI * (50f + 90f * Mathf.Exp(-t * 30f)) * t) * Mathf.Exp(-t * 9f), 0.75f);
            else
            {
                float lpS = 0f;
                Add(t0, 0.22f, t => { lpS += ((float)(rng.NextDouble() * 2 - 1) - lpS) * 0.5f; return (lpS * 0.8f + Mathf.Sin(2 * Mathf.PI * 190f * t) * 0.4f) * Mathf.Exp(-t * 18f); }, 0.45f);
            }
            for (int h = 0; h < 2; h++)
                Add(t0 + h * beat * 0.5f, 0.05f, t => (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 90f), 0.12f);
            float bf = bass[(b / 2) % bass.Length];
            Add(t0, beat * 0.9f, t => (Mathf.Sin(2 * Mathf.PI * bf * t) + 0.3f * Mathf.Sin(2 * Mathf.PI * bf * 2f * t)) * Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 2.5f), 0.35f);
            if (pad && b % 4 == 0)
            {
                var ch = chords[(b / 4) % chords.Length];
                foreach (var f in ch)
                    Add(t0, beat * 4f, t => (Mathf.Sin(2 * Mathf.PI * f * t) + 0.25f * Mathf.Sin(2 * Mathf.PI * f * 2.01f * t)) * Mathf.Min(1f, t * 4f) * Mathf.Min(1f, (beat * 4f - t) * 4f), 0.07f);
            }
        }
        float peak = 0.001f;
        foreach (var v in d) peak = Mathf.Max(peak, Mathf.Abs(v));
        for (int i = 0; i < n; i++) d[i] = d[i] / peak * 0.8f;
        return Make("music", d);
    }

    public bool Has(string name) => _clips.ContainsKey(name);

    public void Play2D(string name, float volume = 1f)
    {
        if (_clips.TryGetValue(name, out var c)) _ui.PlayOneShot(c, volume);
    }

    public void PlayAt(string name, Vector3 pos, float volume = 1f, float minDist = 4f, float pitch = 1f)
    {
        if (!_clips.TryGetValue(name, out var c)) return;
        ReplayRecorder.RecordSound(name, pos, volume, minDist);
        var go = new GameObject("sfx_" + name);
        go.transform.position = pos;
        var s = go.AddComponent<AudioSource>();
        s.clip = c;
        s.volume = volume;
        s.spatialBlend = 1f;
        s.minDistance = minDist;
        s.maxDistance = 400f;
        s.rolloffMode = AudioRolloffMode.Logarithmic;
        s.pitch = pitch;
        s.Play();
        Destroy(go, c.length / pitch + 0.1f);
    }

    static AudioClip Make(string name, float[] data)
    {
        var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// gunshot: a noise crack with a fast decay, a low body thump and a longer filtered tail
    static AudioClip Shot(System.Random rng, float len, float bright, float thumpHz, float gain)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * bright;
            lp2 += (lp - lp2) * 0.08f;
            float crack = lp * Mathf.Exp(-t * 38f);
            float tail = lp2 * 2.2f * Mathf.Exp(-t * 7f / len);
            float thump = Mathf.Sin(2 * Mathf.PI * thumpHz * t * (1f - t)) * Mathf.Exp(-t * 22f);
            d[i] = Mathf.Clamp((crack * 0.9f + tail + thump * 0.8f) * gain, -1f, 1f);
        }
        return Make("shot", d);
    }

    static AudioClip Boom(System.Random rng, float len)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.12f;
            lp2 += (lp - lp2) * 0.05f;
            float env = Mathf.Min(1f, t * 200f) * Mathf.Exp(-t * 2.6f);
            float rumble = Mathf.Sin(2 * Mathf.PI * (48f - 20f * t) * t) * Mathf.Exp(-t * 3.5f);
            d[i] = Mathf.Clamp((lp2 * 5f + lp * 0.6f * Mathf.Exp(-t * 12f) + rumble * 0.7f) * env, -1f, 1f);
        }
        return Make("boom", d);
    }

    static AudioClip Whoosh(System.Random rng, float len)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * Mathf.Lerp(0.05f, 0.4f, t / len);
            float env = Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 3f);
            float thump = Mathf.Sin(2 * Mathf.PI * 70f * t) * Mathf.Exp(-t * 25f);
            d[i] = Mathf.Clamp(lp * 1.8f * env + thump * 0.7f, -1f, 1f);
        }
        return Make("whoosh", d);
    }

    static AudioClip Swing(System.Random rng, float len)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float a = Mathf.Sin(Mathf.PI * t / len);
            lp += (noise - lp) * (0.03f + 0.15f * a);
            d[i] = lp * 2.5f * a * a;
        }
        return Make("swing", d);
    }

    static AudioClip Tone(float hz, float len, float vol)
    {
        int n = (int)(len * Rate);
        var d = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            d[i] = Mathf.Sin(2 * Mathf.PI * hz * t) * vol * Mathf.Exp(-t * 4f / len);
        }
        return Make("tone", d);
    }

    static AudioClip Chime(float[] notes, float step, float vol)
    {
        int n = (int)((notes.Length * step + 0.25f) * Rate);
        var d = new float[n];
        for (int k = 0; k < notes.Length; k++)
            for (int i = (int)(k * step * Rate); i < n; i++)
            {
                float t = i / (float)Rate - k * step;
                d[i] += Mathf.Sin(2 * Mathf.PI * notes[k] * t) * vol * Mathf.Exp(-t * 14f);
            }
        return Make("chime", d);
    }

    static AudioClip Clicks(System.Random rng, float[] at, float len)
    {
        int n = (int)((len + 0.05f) * Rate);
        var d = new float[n];
        foreach (var a in at)
            for (int i = (int)(a * Rate); i < Mathf.Min(n, (int)((a + 0.03f) * Rate)); i++)
            {
                float t = i / (float)Rate - a;
                d[i] += (float)(rng.NextDouble() * 2 - 1) * 0.6f * Mathf.Exp(-t * 180f) + Mathf.Sin(2 * Mathf.PI * 3200f * t) * 0.25f * Mathf.Exp(-t * 250f);
            }
        return Make("click", d);
    }
}
