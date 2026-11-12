using UnityEngine;
using UnityEngine.AI;

/// Assets the game layer needs at runtime, made by IslandGame in the editor (so they are part of the
/// player build): effect materials and textures, HUD art, loot and bot prefabs, the storm material
/// and the island's navigation mesh.
public class GameAssets : ScriptableObject
{
    public static GameAssets I;

    public Material additive, smoke, decal, tracer, flash, stormMat;
    public Texture2D softDot, scope, ring;
    // HUD art (generated or rendered by IslandGame): icons in WeaponTable / Consumables order, the
    // minimap (the top-down render of the island, +-mapHalf metres around the origin, north up)
    public Texture2D[] weaponIcons, consumableIcons;
    public Texture2D ammoIcon, minimap;
    public float mapHalf = 215.7f;
    public Texture2D iconShield, iconHealth, iconSprint, iconPlayers, iconElims, iconClock, mapArrow, slotShine;
    // ground loot models by id (weapons, consumables, ammo_box) and the bot bodies
    public GameObject[] itemPrefabs, botPrefabs;
    /// the locker's outfits (thirteenth run): a player prefab, a name, a rarity (0-4) and a portrait each;
    /// entry 0 is the default, Aj
    public GameObject[] playerPrefabs;
    public string[] outfitNames;
    public int[] outfitRarity;
    public Texture2D[] outfitIcons;
    // building: the modular kit pieces (PieceType x BuildMat, wall wood/brick/metal, floor ..., ramp ...,
    // pyramid roof ...), their yaw and offset that put them in the canonical slot frame, the ghost
    // material and the HUD icons for the materials and the four pieces
    public GameObject[] buildPrefabs;
    public float[] buildYaw;
    public Vector3[] buildOffset;
    public Mesh[] buildMeshes;          // readable copies in the slot frame, cut up when a piece is edited
    public Material ghostMat;
    public Texture2D[] matIcons, pieceIcons;
    public NavMeshData navMesh;
    public float swimDrop = 0.83f, treadDrop = 1.2f;   // feet below the water surface: swimming, treading water
    public float mantleLength = 1f;
    public GameObject launchPad;                      // the full-size pad (the item prefab is a small one)
    public Material riftMat, zipPoleMat, zipCapMat, zipCableMat;
    public GameObject supplyFalling, supplyLanded;
    public Material podiumMat, podiumRingMat;
    /// emotes: Animator states (and clip names) of the emote wheel, and their wheel labels
    public static readonly string[] Emotes = { "E_Robot", "E_RunningMan", "E_Chicken", "E_Cabbage", "E_Wave", "E_Flex" };
    public static readonly string[] EmoteLabels = { "ROBOT", "RUNNING MAN", "CHICKEN DANCE", "CABBAGE PATCH", "WAVE", "FLEX" };                   // the mantle clip, played over the pull-up
}
