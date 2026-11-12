using UnityEngine;

/// Camera flight used by the flythrough capture and by the FPS benchmark.
/// Positions and look targets are Catmull-Rom splines, re-parameterised by arc length so the camera
/// moves at constant speed.
public class FlyPath : MonoBehaviour
{
    public Vector3[] points;
    public Vector3[] targets;
    public float duration = 40f;

    float[] _cumLen;
    float _total;
    const int Steps = 4000;

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    static Vector3 Spline(Vector3[] pts, float u)
    {
        int n = pts.Length - 1;
        u = Mathf.Clamp(u, 0f, n - 1e-4f);
        int i = Mathf.FloorToInt(u);
        float t = u - i;
        Vector3 p0 = pts[Mathf.Max(i - 1, 0)], p1 = pts[i], p2 = pts[Mathf.Min(i + 1, n)], p3 = pts[Mathf.Min(i + 2, n)];
        return CatmullRom(p0, p1, p2, p3, t);
    }

    void Build()
    {
        _cumLen = new float[Steps + 1];
        int n = points.Length - 1;
        Vector3 prev = Spline(points, 0);
        _total = 0;
        for (int s = 1; s <= Steps; s++)
        {
            Vector3 p = Spline(points, n * s / (float)Steps);
            _total += Vector3.Distance(prev, p);
            _cumLen[s] = _total;
            prev = p;
        }
    }

    public float Length { get { if (_cumLen == null) Build(); return _total; } }

    /// t01 in [0,1] over the whole flight.
    public void Evaluate(float t01, out Vector3 pos, out Quaternion rot)
    {
        if (_cumLen == null || _cumLen.Length != Steps + 1) Build();
        float target = Mathf.Clamp01(t01) * _total;
        int lo = 0, hi = Steps;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_cumLen[mid] < target) lo = mid; else hi = mid;
        }
        float seg = _cumLen[hi] - _cumLen[lo];
        float f = seg > 1e-5f ? (target - _cumLen[lo]) / seg : 0f;
        float u = (lo + f) / Steps * (points.Length - 1);
        pos = Spline(points, u);
        Vector3 look = Spline(targets, u);
        rot = Quaternion.LookRotation((look - pos).normalized, Vector3.up);
    }
}
