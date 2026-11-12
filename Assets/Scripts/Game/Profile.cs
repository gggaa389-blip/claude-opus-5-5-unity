using UnityEngine;

/// The player's account, kept in PlayerPrefs: XP and level, career numbers, the three daily quests and
/// the chosen playlist and outfit. The recorded videos (Settings.Temporary) use a fresh profile and never save it.
public static class Profile
{
    public const int XpPerLevel = 1000;
    public static int xp, matches, wins, kills, top10, damage;
    public static float secondsPlayed;
    public static int playlist;              // index into Playlists
    public static int outfit;                // index into GameAssets.playerPrefabs (0: Aj)

    public struct Playlist { public string name, skill; public float skillLo, skillHi; }
    public static readonly Playlist[] Playlists =
    {
        new Playlist { name = "SOLO", skill = "NORMAL", skillLo = 0.3f, skillHi = 0.75f },
        new Playlist { name = "SOLO CASUAL", skill = "EASY", skillLo = 0.15f, skillHi = 0.45f },
        new Playlist { name = "SOLO SWEATY", skill = "HARD", skillLo = 0.7f, skillHi = 0.95f },
    };

    public struct Quest { public string text; public int target, xp; }
    public static readonly Quest[] Quests =
    {
        new Quest { text = "ELIMINATE OPPONENTS", target = 3, xp = 300 },
        new Quest { text = "DEAL DAMAGE TO OPPONENTS", target = 500, xp = 300 },
        new Quest { text = "FINISH IN THE TOP 10", target = 1, xp = 400 },
    };
    public static readonly int[] questProgress = new int[3];

    static bool _loaded;

    public static int Level => 1 + xp / XpPerLevel;
    public static float LevelProgress => (xp % XpPerLevel) / (float)XpPerLevel;

    public static void Load()
    {
        _loaded = true;
        if (Settings.Temporary) return;
        xp = PlayerPrefs.GetInt("p_xp", 0);
        matches = PlayerPrefs.GetInt("p_matches", 0);
        wins = PlayerPrefs.GetInt("p_wins", 0);
        kills = PlayerPrefs.GetInt("p_kills", 0);
        top10 = PlayerPrefs.GetInt("p_top10", 0);
        damage = PlayerPrefs.GetInt("p_damage", 0);
        secondsPlayed = PlayerPrefs.GetFloat("p_seconds", 0f);
        playlist = Mathf.Clamp(PlayerPrefs.GetInt("p_playlist", 0), 0, Playlists.Length - 1);
        outfit = Mathf.Max(0, PlayerPrefs.GetInt("p_outfit", 0));
        // daily quests start again every day
        string today = System.DateTime.Now.ToString("yyyy-MM-dd");
        if (PlayerPrefs.GetString("p_questDay", "") != today)
        {
            PlayerPrefs.SetString("p_questDay", today);
            for (int i = 0; i < questProgress.Length; i++) PlayerPrefs.SetInt("p_quest" + i, 0);
        }
        for (int i = 0; i < questProgress.Length; i++) questProgress[i] = PlayerPrefs.GetInt("p_quest" + i, 0);
    }

    public static void EnsureLoaded() { if (!_loaded) Load(); }

    public static void Save()
    {
        if (Settings.Temporary) return;
        PlayerPrefs.SetInt("p_xp", xp);
        PlayerPrefs.SetInt("p_matches", matches);
        PlayerPrefs.SetInt("p_wins", wins);
        PlayerPrefs.SetInt("p_kills", kills);
        PlayerPrefs.SetInt("p_top10", top10);
        PlayerPrefs.SetInt("p_damage", damage);
        PlayerPrefs.SetFloat("p_seconds", secondsPlayed);
        PlayerPrefs.SetInt("p_playlist", playlist);
        PlayerPrefs.SetInt("p_outfit", outfit);
        for (int i = 0; i < questProgress.Length; i++) PlayerPrefs.SetInt("p_quest" + i, questProgress[i]);
        PlayerPrefs.Save();
    }

    /// one line of the summary's XP breakdown
    public struct XpLine { public string label; public int xp; public bool quest; }

    /// the match's XP, line by line (playing, eliminations, placement, survival, quests finished in it)
    public static System.Collections.Generic.List<XpLine> MatchXp(int placement, int matchKills, int players, float survived, float dealt)
    {
        var l = new System.Collections.Generic.List<XpLine>
        {
            new XpLine { label = "PLAYED A MATCH", xp = 300 },
            new XpLine { label = $"ELIMINATIONS  x{matchKills}", xp = matchKills * 150 },
            new XpLine { label = $"PLACEMENT  #{placement}", xp = Mathf.RoundToInt(900f * Mathf.Clamp01((players - placement) / (float)Mathf.Max(1, players - 1))) },
            new XpLine { label = $"SURVIVED  {(int)(survived / 60f)}:{(int)survived % 60:00}", xp = Mathf.RoundToInt(survived / 60f * 120f) },
        };
        int[] add = { matchKills, Mathf.RoundToInt(dealt), placement <= 10 ? 1 : 0 };
        for (int i = 0; i < Quests.Length; i++)
            if (questProgress[i] < Quests[i].target && questProgress[i] + add[i] >= Quests[i].target)
                l.Add(new XpLine { label = "QUEST: " + Quests[i].text, xp = Quests[i].xp, quest = true });
        return l;
    }

    /// the match goes into the account once, when its summary opens
    public static void AddMatch(int placement, int matchKills, int players, float survived, float dealt, int matchXp)
    {
        EnsureLoaded();
        xp += matchXp;
        matches++;
        if (placement == 1) wins++;
        if (placement <= 10) top10++;
        kills += matchKills;
        damage += Mathf.RoundToInt(dealt);
        secondsPlayed += survived;
        int[] add = { matchKills, Mathf.RoundToInt(dealt), placement <= 10 ? 1 : 0 };
        for (int i = 0; i < Quests.Length; i++) questProgress[i] = Mathf.Min(Quests[i].target, questProgress[i] + add[i]);
        Save();
    }
}
