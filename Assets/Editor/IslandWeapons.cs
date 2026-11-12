using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// Puts a test 1 weapon into a Mixamo character's right hand. The attachment (position and rotation in
/// the hand bone's space) is fitted to the character's own pose in the locomotion clip it plays,
/// averaged over the whole cycle:
///   two-handed (rifles, shotgun, rocket launcher): barrel from the right palm towards the left palm,
///   pistol: barrel along the forearm (elbow -> palm),
///   melee (pickaxe): handle near the fist's axis (knuckle line) but turned towards an upright carry
///   with a slight forward lean, head at shoulder height, blade pointing ahead,
///   grenade: in the palm.
/// The test 1 weapon models have their origin at the grip, barrel along +Z and top along +Y in Unity
/// (the pickaxe and the old hammer-pickaxe: handle along +Y, blade / hammer face +Z).
public static class IslandWeapons
{
    /// melee carry direction (character space: up + lean x forward + out x right) and how far the handle
    /// turns from the knuckle line towards it (0 = knuckle line, 1 = carry direction)
    public static float MeleeLean = 0.3f, MeleeOut = 0.12f, MeleeCarry = 0.7f;

    public enum Grip { TwoHanded, Pistol, Melee, Palm }

    public static Grip GripOf(string weapon)
    {
        switch (weapon)
        {
            case "pistol": return Grip.Pistol;
            case "pickaxe": case "hammer_pickaxe": return Grip.Melee;
            case "grenade": case "med_kit": case "bandage": case "shield_potion": case "launch_pad": case "rift_to_go": return Grip.Palm;
            default: return Grip.TwoHanded;   // assault_rifle, shotgun, sniper_rifle, rocket_launcher
        }
    }

    static Vector3 Palm(Animator an, bool right)
    {
        var hand = an.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand).position;
        var mid = an.GetBoneTransform(right ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
        return mid != null ? Vector3.Lerp(hand, mid.position, 0.55f) : hand;
    }

    static Vector3? _refKnuckle, _refFist;

    /// the knuckle line and the fist's direction in right-hand space, measured on Y Bot: Mixamo hands share
    /// their bone axes, so this stands in for an outfit without finger bones (Doozy)
    static void RefHand(out Vector3 knuckle, out Vector3 fist)
    {
        if (_refKnuckle == null)
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>($"{IslandCharacters.Root}/Characters/YBot.fbx"));
            var a = go.GetComponent<Animator>();
            var h = a.GetBoneTransform(HumanBodyBones.RightHand);
            _refKnuckle = h.InverseTransformDirection(a.GetBoneTransform(HumanBodyBones.RightIndexProximal).position - a.GetBoneTransform(HumanBodyBones.RightLittleProximal).position).normalized;
            _refFist = h.InverseTransformDirection(a.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position - h.position).normalized;
            Object.DestroyImmediate(go);
        }
        knuckle = _refKnuckle.Value;
        fist = _refFist.Value;
    }

    public static GameObject Attach(GameObject character, string weapon, AnimationClip clip, GameObject weaponPrefab)
    {
        var an = character.GetComponent<Animator>();
        var hand = an.GetBoneTransform(HumanBodyBones.RightHand);
        var grip = GripOf(weapon);

        // fit the attachment in hand space over the clip
        Vector3 sumPos = Vector3.zero, sumFwd = Vector3.zero, sumUp = Vector3.zero;
        int n = 24;
        for (int i = 0; i < n; i++)
        {
            clip.SampleAnimation(character, clip.length * i / n);
            Vector3 g = Palm(an, true);
            Vector3 fwd, up;
            var index = an.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            var little = an.GetBoneTransform(HumanBodyBones.RightLittleProximal) ?? an.GetBoneTransform(HumanBodyBones.RightRingProximal);
            var middle = an.GetBoneTransform(HumanBodyBones.RightMiddleProximal) ?? index;
            RefHand(out var refKnuckle, out var refFist);
            Vector3 knuckleDir = index != null && little != null ? (index.position - little.position).normalized : hand.TransformDirection(refKnuckle);
            Vector3 fistFwd = middle != null ? (middle.position - hand.position).normalized : hand.TransformDirection(refFist);
            switch (grip)
            {
                case Grip.TwoHanded:
                    fwd = (Palm(an, false) - g).normalized;
                    up = Vector3.ProjectOnPlane(character.transform.up, fwd).normalized;
                    break;
                case Grip.Pistol:
                    fwd = (g - an.GetBoneTransform(HumanBodyBones.RightLowerArm).position).normalized;
                    up = Vector3.ProjectOnPlane(character.transform.up, fwd).normalized;
                    break;
                case Grip.Melee:   // handle mostly upright beside the body, blade ahead (game-like carry)
                {
                    var ct = character.transform;
                    var carry = (ct.up + MeleeLean * ct.forward + MeleeOut * ct.right).normalized;
                    up = Vector3.Slerp(knuckleDir, carry, MeleeCarry).normalized;   // still close to the fist's axis
                    fwd = Vector3.ProjectOnPlane(ct.forward, up).normalized;
                    break;
                }
                default:   // palm: weapon +Y along the knuckle line, +Z out of the fist
                    up = knuckleDir;
                    fwd = Vector3.ProjectOnPlane(fistFwd, up).normalized;
                    break;
            }
            sumPos += hand.InverseTransformPoint(g);
            sumFwd += hand.InverseTransformDirection(fwd);
            sumUp += hand.InverseTransformDirection(up);
        }
        Vector3 localPos = sumPos / n;
        Quaternion localRot = Quaternion.LookRotation(sumFwd.normalized, sumUp.normalized);

        var w = (GameObject)Object.Instantiate(weaponPrefab);
        w.name = "Held_" + weapon;
        // a held weapon is a visual only: no collider (it would push the player), no own LOD group
        foreach (var c in w.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (var l in w.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(l);
        foreach (var t in w.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);
        w.transform.SetParent(hand, false);
        w.transform.localPosition = localPos;
        w.transform.localRotation = localRot;
        float s = hand.lossyScale.x;
        w.transform.localScale = Vector3.one / (Mathf.Abs(s) > 1e-6f ? s : 1f);   // real weapon size whatever the rig scale

        // the character's LOD group covers the weapon too
        var lod = character.GetComponent<LODGroup>();
        if (lod != null)
        {
            lod.SetLODs(new[] { new LOD(0.015f, character.GetComponentsInChildren<Renderer>(true)) });
            lod.RecalculateBounds();
        }
        return w;
    }
}
