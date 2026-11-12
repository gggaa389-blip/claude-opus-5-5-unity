using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// Starts the game in the player build and in editor play mode: the lobby first (the character on its
/// podium, PLAY), then the battle royale match: the level's loot becomes match loot, the player and the
/// bots board the bus (the test 1 drop vehicle), the storm and the HUD start.
/// With -benchmark it does nothing (the FPS fly-through runs instead); with -demo the autopilot plays
/// the match and records it; with -buildDemo the building video plays (no bots, the player starts on
/// the ground); -frontDemo records the front-end video (lobby, settings, emotes, camera tools, victory,
/// replay, summary); -perf lets the autopilot play a match in real time and logs the frame rate.
/// Options: -bots N, -seed N, -stormScale X, -botLevel easy|normal|sweaty, -skipLobby.
public class GameBootstrap : MonoBehaviour
{
    public GameObject playerPrefab;

    /// the outfit picked in the locker (the default player prefab, Aj, when there is no choice)
    public GameObject OutfitPrefab
    {
        get
        {
            var all = assets != null ? assets.playerPrefabs : null;
            if (all == null || all.Length == 0) return playerPrefab;
            return all[Mathf.Clamp(Profile.outfit, 0, all.Length - 1)] ?? playerPrefab;
        }
    }
    public GameAssets assets;
    public Transform spawnPoints;
    public Transform islandRoot;
    public int bots = 24;

    /// set by PLAY AGAIN: the reloaded scene goes straight into a new match
    public static bool SkipLobbyOnce;

    /// wood, brick and metal a person lands with: 50 pieces of each
    const int StartMats = 500;

    static readonly string[] Names = {
        "NeonViper", "Kaito_FN", "BlazeRunner", "SilentPeak", "MangoTango", "Zyphr", "LunaBolt", "OrbitKid",
        "PixelPanda", "RazorWing", "DriftKing77", "FrostByte", "Tomahawk", "EchoLeaf", "NovaStrike", "ChillPill",
        "IronMoss", "BananaBread", "QuickScope", "CobaltFox", "Wavelength", "Hexa", "GhostPepper", "TurboTaco",
        "SunnyDays", "Marshmallow", "Jetstream", "PaperPlane", "Quokka", "StormChaser" };

    bool _ended;
    float _quitAfter;
    string[] _args;
    int _seed, _nBots;
    float _stormScale, _skillLo, _skillHi;
    bool _demo, _buildDemo, _moveDemo, _fightDemo, _travDemo, _frontDemo, _perf;
    System.Random _rng;
    Hud _hud;
    FrontShowcase _front;

    /// the game's text is English: numbers print the same on every Windows locale (the replay showed
    /// "x0,55" on a comma-decimal one)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InvariantNumbers()
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
    }

    static string Arg(string[] args, string key)
    {
        int i = System.Array.IndexOf(args, key);
        return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
    }

    void Start()
    {
        var args = _args = System.Environment.GetCommandLineArgs();
        if (args.Contains("-benchmark") || playerPrefab == null || assets == null) { enabled = false; return; }
        GameAssets.I = assets;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _seed = int.TryParse(Arg(args, "-seed"), out var sd) ? sd : System.Environment.TickCount & 0xffff;
        _stormScale = float.TryParse(Arg(args, "-stormScale"), System.Globalization.NumberStyles.Float, inv, out var ss) ? ss : 1f;
        _perf = args.Contains("-perf");
        _demo = args.Contains("-demo") || _perf; _buildDemo = args.Contains("-buildDemo"); _moveDemo = args.Contains("-moveDemo");
        _fightDemo = args.Contains("-fightDemo"); _travDemo = args.Contains("-travDemo"); _frontDemo = args.Contains("-frontDemo");
        bool video = _demo || _buildDemo || _moveDemo || _fightDemo || _travDemo || _frontDemo;
        _nBots = int.TryParse(Arg(args, "-bots"), out var nb) ? nb : _buildDemo || _moveDemo || _travDemo ? 0 : _fightDemo || _frontDemo ? 1 : bots;
        // bot skill range: aim error, reaction time, how often they crouch, build-fight and stock up materials
        var level = Arg(args, "-botLevel");
        (_skillLo, _skillHi) = level == "easy" || (_frontDemo && level == null) ? (0.15f, 0.45f) : level == "sweaty" || _fightDemo ? (0.7f, 0.95f) : (0.3f, 0.75f);
        _rng = new System.Random(_seed);
        Random.InitState(_seed);   // the same seed plays the same match (fixed 30 fps clock in -demo)
        _quitAfter = float.TryParse(Arg(args, "-quitAfter"), System.Globalization.NumberStyles.Float, inv, out var qa) ? qa : 0f;

        // the videos play with the default settings and never save any
        Settings.Temporary = video;
        Settings.Load();
        Profile.Load();
        Hud.ResetFrontEnd();
        UiPointer.scripted = _frontDemo;
        if (_frontDemo) Time.captureFramerate = 30;

        if (assets.navMesh != null) NavMesh.AddNavMeshData(assets.navMesh);
        Water.Setup();
        // hitboxes are only for the shots' ray casts: no contacts with anything
        for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(GameLayers.Hitbox, i, true);
        // the showcase crowd only walks in the captures; the match has bots instead
        foreach (var a in FindObjectsByType<CrowdAgent>()) a.gameObject.SetActive(false);
        new GameObject("Fx").AddComponent<Fx>();
        new GameObject("Sfx").AddComponent<Sfx>();
        _hud = gameObject.AddComponent<Hud>();

        if (_frontDemo)
        {
            // the front-end video starts recording in the lobby; it drives the player once the match is on
            _front = gameObject.AddComponent<FrontShowcase>();
            _front.outDir = Arg(args, "-demoOut");
            _front.part = Arg(args, "-frontPart") == "2" ? 2 : 1;
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
        }
        bool lobby = (_frontDemo && _front.part == 1) || (!video && !args.Contains("-skipLobby") && !SkipLobbyOnce);
        SkipLobbyOnce = false;
        if (lobby) Lobby.Show(this, _nBots);
        else BeginMatch();
    }

    /// the match itself: called right away, or by the lobby when matchmaking is done
    public void BeginMatch()
    {
        var args = _args;
        var rng = _rng;
        int seed = _seed, nBots = _nBots;
        bool buildDemo = _buildDemo, moveDemo = _moveDemo, fightDemo = _fightDemo, travDemo = _travDemo, frontDemo = _frontDemo;
        MatchStats.Reset();
        // a match from the lobby plays the chosen playlist's bots, unless the command line says otherwise
        if (!_frontDemo && !_demo && Arg(args, "-botLevel") == null)
            (_skillLo, _skillHi) = (Profile.Playlists[Profile.playlist].skillLo, Profile.Playlists[Profile.playlist].skillHi);

        var match = new GameObject("Match").AddComponent<Match>();
        var bus = islandRoot.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Match.BusObject);
        match.Begin(seed, _stormScale, bus);
        int loot = LootContainer.ConvertLevelLoot(islandRoot, rng);
        int breakable = Destructible.SetupLevel(islandRoot);
        BuildSystem.Reset();
        string traversal = Traversal.SetupLevel(rng);
        match.gameObject.AddComponent<SupplyDrops>().Init(seed);
        BotBrain.HarvestTicks = BotBrain.HarvestSwings = 0;
        Combatant.Hits = Combatant.HeadHits = Combatant.Kills = Combatant.OneShotKills = 0;
        BotBrain.IdleSeconds.Clear();
        BotBrain.IdleCases.Clear();
        BotBrain.StateTicks.Clear();
        BotController.SwimSeconds = BotController.CrouchSeconds = 0f;
        BotController.Mantles = BotController.PiecesBuilt = BotController.Edits = 0;
        Traversal.PadLaunches = Traversal.BotLaunches = Traversal.RiftTrips = Traversal.DropsLanded = Traversal.DropsOpened = 0;

        // the player
        var cam = Camera.main;
        cam.nearClipPlane = 0.08f;
        Vector3 start = match.BusPos;
        float startYaw = 0f;
        if (buildDemo && !BuildShowcase.PickSpot(out start, out startYaw)) Debug.LogWarning("[BuildDemo] no spot with wood, brick and metal close together");
        if (moveDemo && !MoveShowcase.PickStart(out start, out startYaw)) Debug.LogWarning("[MoveDemo] no hill, ledge or river found");
        var botStart = Vector3.zero;
        if ((fightDemo || travDemo || frontDemo) && !FightShowcase.PickSpot(out start, out botStart, out startYaw)) Debug.LogWarning("[FightDemo] no open ground found");
        var player = Instantiate(OutfitPrefab, start, Quaternion.Euler(0f, startYaw, 0f));
        player.name = "Player";
        var pc = player.GetComponent<PlayerController>();
        var me = player.GetComponent<Combatant>();
        if (travDemo)
        {
            pc.StartOnGround();
            me.inv.Add(Item.Weapon(WeaponTable.Get("assault_rifle"), Rarity.Rare));
            me.inv.Add(Item.Consumable(WeaponTable.GetConsumable("launch_pad"), 1));
            me.inv.AddAmmo(AmmoType.Medium, 120);
        }
        if (buildDemo || moveDemo || fightDemo)
        {
            pc.StartOnGround();
            me.inv.Add(Item.Weapon(WeaponTable.Get("assault_rifle"), Rarity.Rare));
            me.inv.Add(Item.Weapon(WeaponTable.Get("rocket_launcher"), Rarity.Epic));
            me.inv.AddAmmo(AmmoType.Medium, 120);
            me.inv.AddAmmo(AmmoType.Rockets, 4);
        }
        if (frontDemo)
        {
            pc.StartOnGround();
            me.inv.Add(Item.Weapon(WeaponTable.Get("assault_rifle"), Rarity.Epic));
            me.inv.Add(Item.Weapon(WeaponTable.Get("shotgun"), Rarity.Rare));
            me.inv.Add(Item.Consumable(WeaponTable.Consumables[1], 2));
            me.inv.AddAmmo(AmmoType.Medium, 150);
            me.inv.AddAmmo(AmmoType.Shells, 15);
            for (int m = 0; m < 3; m++) me.inv.AddMats((BuildMat)m, 120 - m * 30);
            me.damageTakenMult = 0.35f;   // the scripted player has to win the duel (documented)
        }
        // a person lands with materials for building straight away (the user's request); the videos keep
        // their scripted stock
        if (!_demo && !buildDemo && !moveDemo && !fightDemo && !travDemo && !frontDemo)
            for (int m = 0; m < 3; m++) me.inv.AddMats((BuildMat)m, StartMats);
        me.isPlayer = true;
        me.displayName = "Player";
        me.hands.owner = me;
        var tpc = cam.gameObject.AddComponent<ThirdPersonCamera>();
        tpc.cam = cam;
        pc.cam = tpc;
        FoliageFade.Collect(islandRoot);
        cam.gameObject.AddComponent<FoliageFade>();
        match.player = me;
        match.Register(me);

        // the bots: everyone jumps from the bus at their own moment towards their own landing spot
        for (int i = 0; i < nBots; i++)
        {
            int nOutfits = assets.botPrefabs.Length;
            var prefab = assets.botPrefabs[((i + seed) % nOutfits + nOutfits) % nOutfits];   // each seed starts the outfits elsewhere
            var go = Instantiate(prefab, match.BusPos, Quaternion.identity);
            var bot = go.GetComponent<BotController>();
            go.name = "Bot_" + Names[i % Names.Length];
            bot.me.displayName = Names[i % Names.Length];
            bot.me.hands.owner = bot.me;
            bot.brain = new BotBrain(bot.me, Mathf.Lerp(_skillLo, _skillHi, (float)rng.NextDouble()), seed * 31 + i);
            bot.jumpAt = 3f + (float)rng.NextDouble() * (match.busTime - 9f);
            bot.landing = PickLanding(match, bot.jumpAt, rng);
            bot.Board();
            match.Register(bot.me);
            if (fightDemo)
            {
                // the duel: the bot stands on the far side of the arena, armed, with heals and materials
                bot.StartOnGround(botStart, startYaw + 180f);
                bot.me.inv.Add(Item.Weapon(WeaponTable.Get("assault_rifle"), Rarity.Rare));
                bot.me.inv.Add(Item.Weapon(WeaponTable.Get("shotgun"), Rarity.Rare));
                bot.me.inv.Add(Item.Consumable(WeaponTable.Consumables[2], 1));
                bot.me.inv.Add(Item.Consumable(WeaponTable.Consumables[1], 1));
                bot.me.inv.AddAmmo(AmmoType.Medium, 150);
                bot.me.inv.AddAmmo(AmmoType.Shells, 20);
                for (int m = 0; m < 3; m++) bot.me.inv.AddMats((BuildMat)m, 200);
            }
            if (frontDemo)
            {
                // the front-end video's opponent waits on the bus until the script brings it to the arena
                bot.jumpAt = 9999f;
                bot.me.inv.Add(Item.Weapon(WeaponTable.Get("assault_rifle"), Rarity.Common));
                bot.me.inv.AddAmmo(AmmoType.Medium, 90);
            }
        }

        _hud.pc = pc;
        // the controls panel: for a person in their first matches only, never in the videos (F1 shows it any time)
        bool anyVideo = _demo || buildDemo || moveDemo || fightDemo || travDemo || frontDemo;
        _hud.ShowHelp(anyVideo || Profile.matches >= 3 ? 0f : 12f);
        // the filming tools and the replay buffer
        cam.gameObject.AddComponent<CameraTools>().pc = pc;
        new GameObject("Replay").AddComponent<ReplayRecorder>();
        if (fightDemo)
        {
            me.inv.Add(Item.Weapon(WeaponTable.Get("shotgun"), Rarity.Epic));
            me.inv.AddAmmo(AmmoType.Shells, 20);
            for (int m = 0; m < 3; m++) me.inv.AddMats((BuildMat)m, 200);
            me.damageTakenMult = 0.35f;   // the scripted player has to last the whole duel (documented)
        }
        if (frontDemo)
        {
            _front.pc = pc;
            _front.bot = FindAnyObjectByType<BotController>();
            _front.botStart = botStart;
            _front.botYaw = startYaw + 180f;
            pc.source = _front;
        }
        else if (buildDemo || moveDemo || fightDemo || travDemo)
        {
            Showcase sc = buildDemo ? gameObject.AddComponent<BuildShowcase>() : moveDemo ? gameObject.AddComponent<MoveShowcase>()
                        : travDemo ? gameObject.AddComponent<TraversalShowcase>() : (Showcase)gameObject.AddComponent<FightShowcase>();
            if (sc is FightShowcase fs) fs.bot = FindAnyObjectByType<BotController>();
            sc.pc = pc;
            sc.outDir = Arg(args, "-demoOut");
            pc.source = sc;
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
        }
        else if (_demo)
        {
            var ap = gameObject.AddComponent<AutoPilot>();
            ap.pc = pc;
            ap.outDir = Arg(args, "-demoOut");
            pc.source = ap;
            me.damageTakenMult = 0.45f;   // the video should last: the autopilot takes less damage (documented)
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            if (_perf)
            {
                // the frame rate of a real match: real time, nothing recorded, no VSync or frame limit
                ap.fps = 0;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                gameObject.AddComponent<PerfLog>();
            }
        }
        else
        {
            pc.source = new HumanInput();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        Debug.Log($"[Game] match seed {seed}, {nBots} bots, {loot} loot spots, {breakable} breakable level parts, {traversal}, bus {match.busA} -> {match.busB}, storm x{_stormScale}");
    }

    /// back to the lobby, or straight into a new match (PLAY AGAIN): the scene loads again from scratch
    public static void Restart(bool lobby)
    {
        SkipLobbyOnce = !lobby;
        Time.timeScale = 1f;
        AudioListener.pause = false;
        CameraTools.Free = false;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public static void LogStats()
    {
        int mats = 0, have10 = 0, alive = 0;
        foreach (var c in Combatant.All)
        {
            if (!c.Alive || c.isPlayer) continue;
            alive++;
            int t = c.inv.mats[0] + c.inv.mats[1] + c.inv.mats[2];
            mats += t;
            if (Mathf.Max(c.inv.mats[0], Mathf.Max(c.inv.mats[1], c.inv.mats[2])) >= BuildRules.Cost) have10++;
        }
        Debug.Log($"[Stats] {Match.I.Now:F0} s: weapon hits {Combatant.Hits} ({Combatant.HeadHits} headshots), eliminations {Combatant.Kills} ({Combatant.OneShotKills} by one shot at full health), " +
                  $"pickaxe hits {Destructible.PickaxeHits}, mats harvested {Destructible.MatsHarvested}, bot harvest ticks {BotBrain.HarvestTicks}, " +
                  $"bots alive {alive} with {mats} mats ({have10} can build), pieces built by bots {BuildSystem.PlacedByBots} / player {BuildSystem.PlacedByPlayer}, pieces broken {Destructible.BuiltBroken}, level parts broken {Destructible.LevelBroken}, harvest swings {BotBrain.HarvestSwings}, bots swam {BotController.SwimSeconds:F0} s, crouched {BotController.CrouchSeconds:F0} s, mantled {BotController.Mantles}, build-fight pieces {BotController.PiecesBuilt}, edits {BotController.Edits}, pad launches {Traversal.PadLaunches} (bots {Traversal.BotLaunches}), rift trips {Traversal.RiftTrips}, supply drops landed {Traversal.DropsLanded} / opened {Traversal.DropsOpened}, states " + string.Join(" ", BotBrain.StateTicks.Select(kv => kv.Key + "=" + kv.Value)));
        Debug.Log("[Stats] bots idle (s, by state): " + string.Join(", ", BotBrain.IdleSeconds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:F0}")));
        foreach (var s in BotBrain.IdleCases) Debug.Log("[Idle] " + s);
        // the hit zones of the outfits still standing, in their pose of the moment
        Debug.Log("[Stats] heads (outfit head bone / head ball radius / chest, m above the feet): " + string.Join(", ", Combatant.All
            .Where(c => c.Alive && c.anim != null && c.anim.GetBoneTransform(HumanBodyBones.Head) != null).Select(c =>
            {
                c.HeadBall(out _, out float hr);
                float feet = c.transform.position.y;
                return $"{(c.anim.avatar != null ? c.anim.avatar.name : c.name)} {c.anim.GetBoneTransform(HumanBodyBones.Head).position.y - feet:F2}/{hr:F2}/{c.Chest.y - feet:F2}";
            })));
    }

    /// near the bus position at the jump time: one of the level's loot spots, else straight below
    static Vector3 PickLanding(Match m, float jumpAt, System.Random rng)
    {
        var at = Vector3.Lerp(m.busA, m.busB, Mathf.Clamp01(jumpAt / m.busTime));
        var spots = LootContainer.All.Select(c => c.transform.position).Concat(WorldItem.All.Select(w => w.transform.position))
            .Where(p => Vector2.Distance(new Vector2(p.x, p.z), new Vector2(at.x, at.z)) < 130f).ToList();
        if (spots.Count > 0) return OpenGround(spots[rng.Next(spots.Count)]);
        var below = new Vector3(at.x, 0f, at.z);
        below.x = Mathf.Clamp(below.x, -150f, 150f);
        below.z = Mathf.Clamp(below.z, -140f, 160f);
        return below;
    }

    /// Where to land for a loot spot: the spot itself under open sky, else the nearest open ground around
    /// it (rings 3 to 18 m out), at about the spot's height if there is such a place. Gliding straight
    /// for a chest inside a house put the glider on the roof, and a roof's ridge holds a body for good.
    public static Vector3 OpenGround(Vector3 spot)
    {
        if (SkyOpen(spot, out _)) return spot;
        for (int pass = 0; pass < 2; pass++)
            for (float r = 3f; r <= 18f; r += 3f)
                for (int k = 0; k < 12; k++)
                {
                    float a = (k * 30f + r * 7f) * Mathf.Deg2Rad;
                    var p = spot + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    if (SkyOpen(p, out float y) && (pass == 1 || Mathf.Abs(y - spot.y) < 2.5f)) return new Vector3(p.x, y, p.z);
                }
        return spot;
    }

    /// the first thing a ray from the sky meets is level ground no higher than the point (not a roof)
    static bool SkyOpen(Vector3 p, out float groundY)
    {
        groundY = p.y;
        if (!Physics.Raycast(new Vector3(p.x, p.y + 80f, p.z), Vector3.down, out var hit, 160f, GameLayers.Env, QueryTriggerInteraction.Ignore)) return false;
        groundY = hit.point.y;
        return hit.point.y < p.y + 1.2f && hit.normal.y > 0.8f;
    }

    void Update()
    {
        var m = Match.I;
        if (m == null) return;
        if (_quitAfter > 0f && m.Now > _quitAfter) { _quitAfter = 0f; LogStats(); Application.Quit(); }
        bool over = m.phase == Match.Phase.Ended || m.PlayerOut;
        if (over && !_ended) { _ended = true; }
        // Enter skips to the summary (after the kill cam or the victory)
        if (over && Input.GetKeyDown(KeyCode.Return) && !Hud.SummaryOpen && !ReplayPlayer.Active && _hud.pc != null && _hud.pc.source is HumanInput)
            Hud.SummaryAt = UiClock.Now;
    }
}
