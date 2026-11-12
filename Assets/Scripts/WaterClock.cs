using UnityEngine;

/// Drives everything that moves with time: the water ripples and wind (global _IslandTime), the bus
/// (drop vehicle) propellers and the Mixamo crowd. Play mode uses Time.time; the offline captures call Apply per frame.
[ExecuteAlways]
public class WaterClock : MonoBehaviour
{
    public Transform[] propellers;
    public float propellerRpm = 90f;

    void Update()
    {
        if (Application.isPlaying) Apply(Time.time);
    }

    public void Apply(float time)
    {
        Shader.SetGlobalFloat("_IslandTime", time);
        CrowdAgent.ApplyAll(time);
        if (propellers == null) return;
        foreach (var p in propellers)
            if (p) p.localRotation = Quaternion.Euler(0f, 0f, time * propellerRpm * 6f);
    }
}
