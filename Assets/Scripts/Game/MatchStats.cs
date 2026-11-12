using UnityEngine;

/// The player's numbers for the end-of-match summary: eliminations, damage, accuracy, headshots,
/// materials, structures, distance and time survived (the XP is worked out by Profile.MatchXp).
public static class MatchStats
{
    public static int shots, hits, headshots, mats;
    public static float damageDealt, damageTaken, distance, survived;
    static int _hitFrame = -1;
    static bool _hooked;

    public static void Reset()
    {
        shots = hits = headshots = mats = 0;
        damageDealt = damageTaken = distance = survived = 0f;
        if (_hooked) return;
        _hooked = true;
        GameEvents.Hit += (e, t, dealt, killed) =>
        {
            if (!(t is Combatant)) return;
            damageDealt += dealt;
            if (e.head) headshots++;
            if (Time.frameCount != _hitFrame) { hits++; _hitFrame = Time.frameCount; }   // one per shot (pellets land together)
        };
        GameEvents.PlayerHurt += e => damageTaken += e.amount;
        GameEvents.Harvest += (who, m, n, crit) => { if (who != null && who.isPlayer) mats += n; };
    }

    public static float Accuracy => shots > 0 ? Mathf.Clamp01(hits / (float)shots) : 0f;

}
