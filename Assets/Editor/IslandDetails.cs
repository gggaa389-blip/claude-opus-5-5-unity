using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

/// Procedural terrain detail meshes: a grass tuft and two flower tufts, built from code (blades are
/// bent three-triangle strips with a dark-root / light-tip vertex gradient). Drawn by the terrain as
/// GPU-instanced detail meshes with the vertex-colour shader and height-weighted wind.
public static class IslandDetails
{
    const string Dir = IslandBuilder.GenDir + "/Details";

    static Color Lin(float r, float g, float b) => new Color(r, g, b, 1f).linear;

    static void Blade(List<Vector3> v, List<Color> c, List<Vector3> n, List<int> t, Random rng,
        Vector3 root, float height, float width, Vector3 lean, Color baseCol, Color tipCol)
    {
        Vector3 up = Vector3.up;
        Vector3 side = Vector3.Cross(up, lean.sqrMagnitude > 1e-6f ? lean.normalized : Vector3.forward).normalized;
        Vector3 mid = root + up * height * 0.55f + lean * 0.3f;
        Vector3 tip = root + up * height + lean;
        int i0 = v.Count;
        v.Add(root - side * width); v.Add(root + side * width);
        v.Add(mid - side * width * 0.6f); v.Add(mid + side * width * 0.6f);
        v.Add(tip);
        Color midCol = Color.Lerp(baseCol, tipCol, 0.55f);
        baseCol.a = 0f; midCol.a = 0.5f; tipCol.a = 1f;   // alpha = wind weight
        c.Add(baseCol); c.Add(baseCol); c.Add(midCol); c.Add(midCol); c.Add(tipCol);
        // soft, mostly-up normals: the tuft shades like the ground under it, not like flat cards
        Vector3 nn = (up + lean.normalized * 0.15f).normalized;
        for (int k = 0; k < 5; k++) n.Add(nn);
        t.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3, i0 + 2, i0 + 4, i0 + 3 });
    }

    static void Head(List<Vector3> v, List<Color> c, List<Vector3> n, List<int> t, Vector3 center, float r, Color col, Color eye)
    {
        // a small flat flower: 5 petals around a centre, slightly cupped
        int i0 = v.Count;
        v.Add(center + Vector3.up * 0.01f); c.Add(eye); n.Add(Vector3.up);
        for (int k = 0; k < 10; k++)
        {
            float a = k * Mathf.PI * 2f / 10f;
            float rr = (k % 2 == 0) ? r : r * 0.45f;
            v.Add(center + new Vector3(Mathf.Cos(a) * rr, (k % 2 == 0) ? 0.012f : 0f, Mathf.Sin(a) * rr));
            c.Add(col); n.Add(Vector3.up);
        }
        for (int k = 0; k < 10; k++) t.AddRange(new[] { i0, i0 + 1 + (k + 1) % 10, i0 + 1 + k });
    }

    static GameObject SaveTuft(string name, Mesh mesh, Material mat)
    {
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, $"{Dir}/{name}.asset");
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{Dir}/{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    static Mesh Build(Random rng, int blades, float hMin, float hMax, float spread, Color baseCol, Color tipCol,
        int heads = 0, Color headCol = default, Color eyeCol = default)
    {
        var v = new List<Vector3>(); var c = new List<Color>(); var n = new List<Vector3>(); var t = new List<int>();
        for (int b = 0; b < blades; b++)
        {
            float a = (float)rng.NextDouble() * Mathf.PI * 2f;
            float d = (float)rng.NextDouble() * spread;
            var root = new Vector3(Mathf.Cos(a) * d, -0.03f, Mathf.Sin(a) * d);
            float h = Mathf.Lerp(hMin, hMax, (float)rng.NextDouble());
            float la = a + ((float)rng.NextDouble() - 0.5f) * 1.2f;
            var lean = new Vector3(Mathf.Cos(la), 0f, Mathf.Sin(la)) * h * Mathf.Lerp(0.15f, 0.45f, (float)rng.NextDouble());
            float tint = 0.93f + 0.14f * (float)rng.NextDouble();
            Blade(v, c, n, t, rng, root, h, Mathf.Lerp(0.022f, 0.04f, (float)rng.NextDouble()), lean, baseCol * tint, tipCol * tint);
        }
        for (int k = 0; k < heads; k++)
        {
            float a = (float)rng.NextDouble() * Mathf.PI * 2f, d = (float)rng.NextDouble() * spread * 0.8f;
            var root = new Vector3(Mathf.Cos(a) * d, -0.03f, Mathf.Sin(a) * d);
            float h = Mathf.Lerp(hMax * 0.9f, hMax * 1.25f, (float)rng.NextDouble());
            var lean = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.05f;
            Blade(v, c, n, t, rng, root, h, 0.008f, lean, baseCol, Color.Lerp(baseCol, tipCol, 0.5f));
            Head(v, c, n, t, root + Vector3.up * h + lean, Mathf.Lerp(0.035f, 0.05f, (float)rng.NextDouble()), headCol, eyeCol);
        }
        var m = new Mesh();
        m.SetVertices(v); m.SetColors(c); m.SetNormals(n); m.SetTriangles(t, 0);
        return m;
    }

    public static DetailPrototype[] Prototypes()
    {
        AssetDatabase.DeleteAsset(Dir);
        IslandAssets.EnsureFolder(Dir);
        var mat = new Material(Shader.Find("Island/VertexColorLitNoShadow")) { name = "grass_detail", enableInstancing = true };
        mat.SetColor("_Color", Color.white);
        mat.SetFloat("_Glossiness", 0.12f);
        mat.SetFloat("_Cull", 0f);
        mat.SetFloat("_Wind", 0.09f);
        mat.SetFloat("_WindAlpha", 1f);
        AssetDatabase.CreateAsset(mat, $"{Dir}/grass_detail.mat");

        var rng = new Random(424242);
        // colours tuned to sit on the grass terrain layer (darker roots, sunlit yellow-green tips)
        var grass = SaveTuft("grass_tuft", Build(rng, 8, 0.28f, 0.55f, 0.15f, Lin(0.44f, 0.63f, 0.20f), Lin(0.58f, 0.76f, 0.27f)), mat);
        var yellow = SaveTuft("flowers_yellow", Build(rng, 5, 0.2f, 0.38f, 0.14f, Lin(0.25f, 0.45f, 0.12f), Lin(0.55f, 0.75f, 0.25f),
            4, Lin(1.0f, 0.82f, 0.18f), Lin(0.95f, 0.55f, 0.1f)), mat);
        var white = SaveTuft("flowers_white_pink", Build(rng, 5, 0.2f, 0.38f, 0.14f, Lin(0.25f, 0.45f, 0.12f), Lin(0.55f, 0.75f, 0.25f),
            4, Lin(0.97f, 0.93f, 0.95f), Lin(1.0f, 0.78f, 0.25f)), mat);

        DetailPrototype Proto(GameObject go, float wMin, float wMax, float hMin, float hMax) => new DetailPrototype
        {
            prototype = go,
            usePrototypeMesh = true,
            renderMode = DetailRenderMode.VertexLit,
            useInstancing = true,
            minWidth = wMin, maxWidth = wMax, minHeight = hMin, maxHeight = hMax,
            noiseSpread = 0.35f,
            healthyColor = Color.white,
            dryColor = new Color(0.92f, 0.9f, 0.78f),
            alignToGround = 70f,      // percent
            positionJitter = 100f,    // percent
        };
        return new[] { Proto(grass, 0.8f, 1.4f, 0.75f, 1.3f), Proto(yellow, 0.9f, 1.2f, 0.9f, 1.2f), Proto(white, 0.9f, 1.2f, 0.9f, 1.2f) };
    }
}
