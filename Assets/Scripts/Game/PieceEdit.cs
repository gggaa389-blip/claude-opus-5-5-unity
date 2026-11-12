using System.Collections.Generic;
using UnityEngine;

/// Fortnite piece editing: a wall is a 3 x 3 grid of tiles (windows, doors, half walls, arches), a floor
/// 2 x 2 (holes to drop through). An edit is a bit mask of removed tiles. The piece's mesh (a readable copy
/// in the slot frame, see GameAssets.buildMeshes) is cut along the grid lines, the removed tiles are
/// dropped, the cut edges get faces across the piece's thickness, and the kept tiles get box colliders.
public static class PieceEdit
{
    public const int Window = 1 << 4, Door = (1 << 1) | (1 << 4), HalfWall = (1 << 6) | (1 << 7) | (1 << 8);

    public static bool Editable(PieceType t) => t == PieceType.Wall || t == PieceType.Floor;
    public static int Cols(PieceType t) => t == PieceType.Wall ? 3 : 2;
    public static int Tiles(PieceType t) => Cols(t) * Cols(t);
    public static int Full(PieceType t) => (1 << Tiles(t)) - 1;
    static float Size(PieceType t) => BuildRules.Grid / Cols(t);

    /// a tile's centre and size in the slot frame (walls stand in x / y around z = 0, floors lie in x / z)
    public static Bounds Tile(PieceType t, int i, float thick)
    {
        int n = Cols(t), c = i % n, r = i / n;
        float s = Size(t), u = -BuildRules.Grid * 0.5f + (c + 0.5f) * s;
        return t == PieceType.Wall
            ? new Bounds(new Vector3(u, (r + 0.5f) * s, 0f), new Vector3(s, s, thick))
            : new Bounds(new Vector3(u, thick * 0.5f, -BuildRules.Grid * 0.5f + (r + 0.5f) * s), new Vector3(s, thick, s));
    }

    /// the tile a point (slot frame) lies on, or -1
    public static int TileAt(PieceType t, Vector3 p)
    {
        int n = Cols(t);
        float s = Size(t), half = BuildRules.Grid * 0.5f;
        float a = p.x + half, b = t == PieceType.Wall ? p.y : p.z + half;
        if (a < 0f || b < 0f || a >= BuildRules.Grid || b >= BuildRules.Grid) return -1;
        return Mathf.Min(n - 1, (int)(b / s)) * n + Mathf.Min(n - 1, (int)(a / s));
    }

    struct V { public Vector3 p, n; public Vector2 uv; }

    static readonly Dictionary<(Mesh, int), Mesh> Cache = new Dictionary<(Mesh, int), Mesh>();

    /// the piece's mesh with the removed tiles cut out
    public static Mesh Cut(Mesh src, PieceType t, int removed)
    {
        if (Cache.TryGetValue((src, removed), out var cached)) return cached;
        bool wall = t == PieceType.Wall;
        int n = Cols(t);
        float s = Size(t), half = BuildRules.Grid * 0.5f;
        // grid lines: walls along x and y, floors along x and z
        var planes = new List<(int axis, float v)>();
        for (int k = 1; k < n; k++) { planes.Add((0, -half + k * s)); planes.Add((wall ? 1 : 2, wall ? k * s : -half + k * s)); }

        var sv = src.vertices; var sn = src.normals; var su = src.uv;
        if (su == null || su.Length != sv.Length) su = new Vector2[sv.Length];
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
        var tris = new List<int>[src.subMeshCount];
        for (int sm = 0; sm < src.subMeshCount; sm++)
        {
            tris[sm] = new List<int>();
            var st = src.GetTriangles(sm);
            for (int k = 0; k < st.Length; k += 3)
            {
                var polys = new List<List<V>> { new List<V> { Vtx(st[k]), Vtx(st[k + 1]), Vtx(st[k + 2]) } };
                foreach (var (axis, v) in planes)
                {
                    var next = new List<List<V>>();
                    foreach (var poly in polys) { Split(poly, axis, v, out var lo, out var hi); if (lo.Count >= 3) next.Add(lo); if (hi.Count >= 3) next.Add(hi); }
                    polys = next;
                }
                foreach (var poly in polys)
                {
                    var c = Vector3.zero;
                    foreach (var q in poly) c += q.p;
                    c /= poly.Count;
                    int tile = TileAt(t, c);
                    if (tile >= 0 && (removed & (1 << tile)) != 0) continue;
                    int b0 = verts.Count;
                    foreach (var q in poly) { verts.Add(q.p); norms.Add(q.n); uvs.Add(q.uv); }
                    for (int j = 1; j < poly.Count - 1; j++) { tris[sm].Add(b0); tris[sm].Add(b0 + j); tris[sm].Add(b0 + j + 1); }
                }
            }
        }
        V Vtx(int i) => new V { p = sv[i], n = sn[i], uv = su[i] };

        // faces across the thickness where a removed tile meets a kept one
        var b = src.bounds;
        float lo0 = wall ? b.min.z : b.min.y, hi0 = wall ? b.max.z : b.max.y;
        for (int i = 0; i < n * n; i++)
        {
            if ((removed & (1 << i)) == 0) continue;
            int c = i % n, r = i / n;
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int cc = c + dc, rr = r + dr;
                if (cc < 0 || cc >= n || rr < 0 || rr >= n || (removed & (1 << (rr * n + cc))) != 0) continue;
                // the shared edge, and the face's normal pointing into the hole
                Vector3 p0, p1, nrm;
                if (dc != 0)
                {
                    float x = -half + (dc > 0 ? c + 1 : c) * s;
                    float y0 = r * s, y1 = (r + 1) * s;
                    p0 = wall ? new Vector3(x, y0, 0f) : new Vector3(x, 0f, -half + y0);
                    p1 = wall ? new Vector3(x, y1, 0f) : new Vector3(x, 0f, -half + y1);
                    nrm = new Vector3(-dc, 0f, 0f);
                }
                else
                {
                    float e = dr > 0 ? (r + 1) * s : r * s;
                    float x0 = -half + c * s, x1 = -half + (c + 1) * s;
                    p0 = wall ? new Vector3(x0, e, 0f) : new Vector3(x0, 0f, -half + e);
                    p1 = wall ? new Vector3(x1, e, 0f) : new Vector3(x1, 0f, -half + e);
                    nrm = wall ? new Vector3(0f, -dr, 0f) : new Vector3(0f, 0f, -dr);
                }
                Vector3 Off(Vector3 p, float d) => wall ? new Vector3(p.x, p.y, d) : new Vector3(p.x, d, p.z);
                var quad = new[] { Off(p0, lo0), Off(p1, lo0), Off(p1, hi0), Off(p0, hi0) };
                int b0 = verts.Count;
                for (int q = 0; q < 4; q++) { verts.Add(quad[q]); norms.Add(nrm); uvs.Add(new Vector2(q == 1 || q == 2 ? 1f : 0f, q >= 2 ? 0.08f : 0f)); }
                // wind so the face looks along nrm
                bool flip = Vector3.Dot(Vector3.Cross(quad[1] - quad[0], quad[2] - quad[0]), nrm) < 0f;
                int[] o = flip ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
                foreach (var x in o) tris[0].Add(b0 + x);
            }
        }

        var mesh = new Mesh { name = src.name + "_edit" + removed };
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uvs);
        mesh.subMeshCount = tris.Length;
        for (int sm = 0; sm < tris.Length; sm++) mesh.SetTriangles(tris[sm], sm);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        Cache[(src, removed)] = mesh;
        return mesh;
    }

    /// Sutherland-Hodgman against one axis-aligned plane: the part below and the part above
    static void Split(List<V> poly, int axis, float v, out List<V> lo, out List<V> hi)
    {
        lo = new List<V>(); hi = new List<V>();
        const float eps = 1e-5f;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            float da = a.p[axis] - v, db = b.p[axis] - v;
            if (da <= eps) lo.Add(a);
            if (da >= -eps) hi.Add(a);
            if ((da < -eps && db > eps) || (da > eps && db < -eps))
            {
                float k = da / (da - db);
                var m = new V { p = Vector3.Lerp(a.p, b.p, k), n = Vector3.Lerp(a.n, b.n, k).normalized, uv = Vector2.Lerp(a.uv, b.uv, k) };
                lo.Add(m); hi.Add(m);
            }
        }
    }
}
