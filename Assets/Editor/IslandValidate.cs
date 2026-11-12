using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// Self-check of the saved scene: floating / buried objects, spawn points, bridge ends.
public static class IslandValidate
{
    public static void Run()
    {
        EditorSceneManager.OpenScene(IslandBuilder.ScenePath, OpenSceneMode.Single);
        // sanity check that physics queries see the scene before the collider-based checks below
        int colliderCount = Object.FindObjectsByType<Collider>().Count(c => c.enabled);
        Physics.SyncTransforms();
        int probeHits = Physics.RaycastAll(new Vector3(0f, 200f, 9f), Vector3.down, 400f).Length;
        var terrain = Object.FindAnyObjectByType<Terrain>();
        float GH(Vector3 p) => terrain.SampleHeight(p) + terrain.transform.position.y;
        var sb = new StringBuilder("# Validation of Assets/Scenes/Island.unity\n");
        var root = GameObject.Find("Island").transform;
        sb.AppendLine($"physics: {colliderCount} colliders, test ray over the plaza hits {probeHits}");

        // every placed prefab instance (direct children of the zone groups' leaf folders)
        var instances = root.GetComponentsInChildren<Transform>(true)
            .Where(t => PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) && t.GetComponent<CrowdAgent>() == null).ToList();
        int floating = 0, buried = 0, halfBuried = 0, checkedN = 0;
        foreach (var t in instances)
        {
            string name = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject)?.name ?? t.name;
            if (name == "cargo_blimp" || name == "drop_vehicle") continue;   // in the air on purpose
            var rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) continue;
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            // kit pieces of the upper fort floor / roofs and stacked crates rest on other objects
            var own = t.GetComponentsInChildren<Collider>();
            var hits = Physics.RaycastAll(new Vector3(b.center.x, b.max.y + 0.3f, b.center.z), Vector3.down, b.size.y + 60f)
                .Where(h => !own.Contains(h.collider)).OrderBy(h => h.distance).ToArray();
            float support = hits.Length > 0 ? hits[0].point.y : GH(b.center);
            float bottom = name.StartsWith("tree_") ? t.position.y : b.min.y;
            float gap = bottom - support;
            checkedN++;
            bool isKit = name.StartsWith("wall_") || name.StartsWith("pyramid_") || name.StartsWith("floor_") || name.StartsWith("ramp_");
            if (gap > 0.25f && !isKit)
            {
                floating++;
                if (floating <= 25) sb.AppendLine($"FLOATING {name} '{t.name}' at {t.position} gap {gap:F2} m (support {(hits.Length > 0 ? hits[0].collider.name : "terrain")})");
            }
            if (b.max.y < GH(b.center) - 0.05f)
            {
                buried++;
                sb.AppendLine($"BURIED {name} at {t.position}");
            }
            else if (!name.StartsWith("tree_") && (support - b.min.y) / Mathf.Max(b.size.y, 0.01f) > 0.5f)
            {
                halfBuried++;
                if (halfBuried <= 15) sb.AppendLine($"MORE THAN HALF BURIED {name} at {t.position} ({(support - b.min.y) / b.size.y:P0})");
            }
        }
        sb.AppendLine($"objects checked: {checkedN}, floating > 0.25 m: {floating}, fully buried: {buried}, more than half buried at the centre: {halfBuried}");

        // Mixamo crowd: walkers and runners must stay on the ground or a bridge deck, swimmers in the water
        var agents = root.GetComponentsInChildren<CrowdAgent>(true);
        float worstGround = 0f; string worstName = "";
        foreach (var a in agents)
        {
            if (a.speed <= 0f || a.path == null || a.name.Contains("_swim_")) continue;
            for (int k = 0; k < a.path.Length; k += 3)
            {
                var p = a.path[k];
                float support = GH(p);
                // a sphere, not a ray: the deck planks have 5 cm gaps a thin ray can slip through
                foreach (var h in Physics.SphereCastAll(p + Vector3.up * 1.5f, 0.2f, Vector3.down, 4f))
                    if (h.collider.name == "Plank") support = Mathf.Max(support, h.point.y);
                float gap = Mathf.Abs(p.y - support);
                if (gap > worstGround) { worstGround = gap; worstName = $"{a.name} at ({p.x:F1}, {p.y:F2}, {p.z:F1})"; }
            }
        }
        sb.AppendLine($"mixamo crowd agents: {agents.Length}; largest gap between a walking path and the ground or bridge deck: {worstGround:F2} m ({worstName})");

        // spawn points: on walkable ground, above sea level, out of the water, a free player capsule
        int bad = 0;
        foreach (Transform s in root.Find("SpawnPoints"))
        {
            var p = s.position;
            float g = GH(p);
            var overlaps = Physics.OverlapCapsule(p + Vector3.up * 0.45f, p + Vector3.up * 1.75f, 0.4f).Where(c => !(c is TerrainCollider)).ToArray();
            bool ok = Mathf.Abs(p.y - g) < 0.3f && g > 0.5f && overlaps.Length == 0;
            if (!ok) { bad++; sb.AppendLine($"BAD SPAWN {s.name} {p} ground {g:F2} overlaps {string.Join(",", overlaps.Select(o => o.name))}"); }
        }
        sb.AppendLine($"spawn points: {root.Find("SpawnPoints").childCount}, problems: {bad}");

        // bridges: both deck ends must meet the ground within 0.4 m
        foreach (var bridge in root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Bridge_")))
        {
            var planks = bridge.GetComponentsInChildren<Transform>().Where(t => t.name == "Plank").ToArray();
            var first = planks.First().position; var last = planks.Last().position;
            sb.AppendLine($"{bridge.name}: deck {first.y:F2}; ground at ends {GH(first):F2} / {GH(last):F2}");
        }

        // colliders on everything solid
        int noCol = instances.Count(t =>
        {
            string n = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject)?.name ?? "";
            return !n.StartsWith("bush_") && t.GetComponentsInChildren<Collider>().Length == 0;
        });
        sb.AppendLine($"solid prefab instances without a collider: {noCol}");
        File.WriteAllText(Path.Combine(IslandBuilder.OutDir, "logs", "validation.txt"), sb.ToString());
        Debug.Log("[IslandValidate]\n" + sb);
    }
}
