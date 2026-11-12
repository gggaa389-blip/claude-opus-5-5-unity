using UnityEngine;

/// On a character model (next to its Animator): puts the left hand on the fore-grip of a two-handed
/// weapon, so rifles, the shotgun and the rocket launcher are held with both hands in every pose.
[RequireComponent(typeof(Animator))]
public class PlayerHandIK : MonoBehaviour
{
    public WeaponHands weapons;
    Animator _anim;
    float _w;

    void Awake() => _anim = GetComponent<Animator>();

    void OnAnimatorIK(int layer)
    {
        if (layer != 0 || weapons == null || weapons.owner == null) return;
        var grip = weapons.LeftGrip;
        bool want = grip != null && grip.gameObject.activeInHierarchy && weapons.Stance == Stance.Rifle && !weapons.Reloading && weapons.owner.Alive;
        _w = Mathf.MoveTowards(_w, want ? 1f : 0f, Time.deltaTime * 6f);
        _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, _w);
        _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, _w * 0.6f);
        if (grip == null || _w <= 0f) return;
        _anim.SetIKPosition(AvatarIKGoal.LeftHand, grip.position);
        _anim.SetIKRotation(AvatarIKGoal.LeftHand, grip.rotation);
    }
}
