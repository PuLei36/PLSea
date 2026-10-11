using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 船：浮力 + 船舱防进水。
/// 船体用沿本地 Z 均匀分布的若干横截面（站位）描述，在编辑器里右键组件 “从船体网格测量” 自动生成：
///   外壳截面 → 浮力（按列积分排开水体积）和碰撞体（底板 + 两侧舷墙的盒子，搁浅、人站在船上用）
///   内舱截面 → 水面着色器在舱内的水面像素直接 clip 掉，水不会“进船”
/// 吃水由刚体质量决定：质量 = 水密度 × 吃水深度下的排水体积。水深不够时船底压在海床上，浮力不足以托起，自然搁浅。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class PLOceanBoat : MonoBehaviour
{
    public const int MaxStations = 16;
    const int MaxHulls = 4;

    [System.Serializable]
    public struct Station
    {
        [Tooltip("外壳底部高度（本地）")] public float outerBottom;
        [Tooltip("外壳在底部 / 船舷处的半宽")] public float outerHalfWidthBottom, outerHalfWidthTop;
        [Tooltip("舱底高度（本地），甲板封闭处等于船舷高度")] public float innerBottom;
        [Tooltip("内舱在舱底 / 船舷处的半宽")] public float innerHalfWidthBottom, innerHalfWidthTop;
        [Tooltip("船舷高度（本地）")] public float rim;
    }

    [Header("船体（本地坐标，右键组件可从网格自动测量）")]
    [Tooltip("用来测量的船体网格")]
    public MeshFilter hullMesh;
    public float zMin = -8.4f;
    public float zMax = 8.4f;
    public Station[] stations = new Station[0];

    [Header("浮力")]
    [Tooltip("水的密度 kg/m³")]
    [Min(1f)] public float seaDensity = 1025f;
    [Tooltip("浮力积分的列间距（米）")]
    [Range(0.2f, 2f)] public float columnSpacing = 0.75f;
    [Tooltip("水中线性阻力（每立方米排水体积，N·s/m）")]
    [Min(0f)] public float linearDrag = 600f;
    [Tooltip("竖直方向阻力倍数：船底拍水的阻力比侧向大")]
    [Min(0f)] public float verticalDragScale = 3f;
    [Tooltip("水中的角阻尼")]
    [Min(0f)] public float angularDrag = 0.8f;
    [Tooltip("重心相对船底的高度（米），低一点更稳")]
    public float centerOfMassHeight = 0.4f;

    [Header("防进水")]
    [Tooltip("在船舱内 clip 掉水面")]
    public bool maskSea = true;

    struct Column
    {
        public Vector3 bottom, top;   // 本地坐标
        public float area;
    }

    readonly List<Column> _columns = new List<Column>();
    Rigidbody _rb;
    float _submergedFraction;

    /// <summary>当前排水体积占满载（水没到船舷）的比例</summary>
    public float SubmergedFraction => _submergedFraction;

    static readonly List<PLOceanBoat> s_Boats = new List<PLOceanBoat>();
    static readonly Matrix4x4[] s_Matrices = new Matrix4x4[MaxHulls];
    static readonly Vector4[] s_Params = new Vector4[MaxHulls];
    static readonly Vector4[] s_Stations = new Vector4[MaxHulls * MaxStations];
    static readonly int s_HullCount = Shader.PropertyToID("_PLOceanHullCount");
    static readonly int s_HullMatrices = Shader.PropertyToID("_PLOceanHullWorldToLocal");
    static readonly int s_HullParams = Shader.PropertyToID("_PLOceanHullParams");
    static readonly int s_HullStations = Shader.PropertyToID("_PLOceanHullStations");
    static bool s_Hooked;

    void OnEnable()
    {
        if (!s_Boats.Contains(this))
            s_Boats.Add(this);
        if (!s_Hooked)
        {
            RenderPipelineManager.beginContextRendering += UploadMasks;
            s_Hooked = true;
        }
        BuildColumns();
        _rb = GetComponent<Rigidbody>();
        if (_rb != null && Application.isPlaying && stations.Length >= 2)
            _rb.centerOfMass = new Vector3(0f, MinOuterBottom() + centerOfMassHeight, 0f);
    }

    void OnDisable()
    {
        s_Boats.Remove(this);
        if (s_Boats.Count == 0)
            Shader.SetGlobalFloat(s_HullCount, 0f);
    }

    void OnValidate()
    {
        if (isActiveAndEnabled)
            BuildColumns();
    }

    // ------------------------------------------------------------------
    // 截面插值
    // ------------------------------------------------------------------

    bool StationAt(float z, out Station s)
    {
        s = default;
        int n = stations.Length;
        if (n < 2 || z < zMin || z > zMax)
            return false;
        float f = (z - zMin) / (zMax - zMin) * (n - 1);
        int i = Mathf.Min(Mathf.FloorToInt(f), n - 2);
        float t = f - i;
        Station a = stations[i], b = stations[i + 1];
        s.outerBottom = Mathf.Lerp(a.outerBottom, b.outerBottom, t);
        s.outerHalfWidthBottom = Mathf.Lerp(a.outerHalfWidthBottom, b.outerHalfWidthBottom, t);
        s.outerHalfWidthTop = Mathf.Lerp(a.outerHalfWidthTop, b.outerHalfWidthTop, t);
        s.innerBottom = Mathf.Lerp(a.innerBottom, b.innerBottom, t);
        s.innerHalfWidthBottom = Mathf.Lerp(a.innerHalfWidthBottom, b.innerHalfWidthBottom, t);
        s.innerHalfWidthTop = Mathf.Lerp(a.innerHalfWidthTop, b.innerHalfWidthTop, t);
        s.rim = Mathf.Lerp(a.rim, b.rim, t);
        return true;
    }

    float MinOuterBottom()
    {
        float m = float.MaxValue;
        foreach (var s in stations)
            if (s.outerHalfWidthTop > 0f)
                m = Mathf.Min(m, s.outerBottom);
        return m == float.MaxValue ? 0f : m;
    }

    /// <summary>世界坐标点是否在任何一条船的船舱里（人站在舱里时不算下水）</summary>
    public static bool IsInsideAnyHull(Vector3 world)
    {
        foreach (var b in s_Boats)
            if (b.IsInsideHull(world))
                return true;
        return false;
    }

    public bool IsInsideHull(Vector3 world)
    {
        var p = transform.InverseTransformPoint(world);
        if (!StationAt(p.z, out var s) || p.y < s.innerBottom || p.y > s.rim)
            return false;
        float t = Mathf.Clamp01((p.y - s.innerBottom) / Mathf.Max(s.rim - s.innerBottom, 1e-3f));
        return Mathf.Abs(p.x) < Mathf.Lerp(s.innerHalfWidthBottom, s.innerHalfWidthTop, t);
    }

    // ------------------------------------------------------------------
    // 浮力：把外壳切成竖直的列，每列算没入水中的长度
    // ------------------------------------------------------------------

    void BuildColumns()
    {
        _columns.Clear();
        if (stations.Length < 2)
            return;
        float maxHalf = 0f;
        foreach (var s in stations)
            maxHalf = Mathf.Max(maxHalf, s.outerHalfWidthTop, s.outerHalfWidthBottom);
        float d = columnSpacing;
        for (float z = zMin + d * 0.5f; z < zMax; z += d)
        {
            if (!StationAt(z, out var s) || s.rim <= s.outerBottom)
                continue;
            for (float x = -maxHalf + d * 0.5f; x < maxHalf; x += d)
            {
                float ax = Mathf.Abs(x);
                if (ax >= s.outerHalfWidthTop && ax >= s.outerHalfWidthBottom)
                    continue;
                // 外壳往上张开：|x| 超过底部半宽的列，从船壳与它相交的高度开始
                float bottom = s.outerBottom;
                if (ax > s.outerHalfWidthBottom)
                {
                    float t = (ax - s.outerHalfWidthBottom) / Mathf.Max(s.outerHalfWidthTop - s.outerHalfWidthBottom, 1e-3f);
                    bottom = Mathf.Lerp(s.outerBottom, s.rim, t);
                }
                if (s.rim - bottom < 0.01f)
                    continue;
                _columns.Add(new Column
                {
                    bottom = new Vector3(x, bottom, z),
                    top = new Vector3(x, s.rim, z),
                    area = d * d,
                });
            }
        }
    }

    void FixedUpdate()
    {
        if (!Application.isPlaying || _rb == null || _columns.Count == 0)
            return;

        var sim = PLOceanSimulation.Active;
        if (sim == null)
            return;

        float g = -Physics.gravity.y;
        float totalVolume = 0f, submergedVolume = 0f;
        foreach (var c in _columns)
        {
            Vector3 bottom = transform.TransformPoint(c.bottom);
            Vector3 top = transform.TransformPoint(c.top);
            float length = Vector3.Distance(bottom, top);
            totalVolume += c.area * length;

            float sea = sim.SampleHeight(bottom.x, bottom.z);
            float span = top.y - bottom.y;
            if (span <= 1e-3f || sea <= bottom.y)
                continue;
            float fraction = Mathf.Clamp01((sea - bottom.y) / span);
            float volume = c.area * length * fraction;
            submergedVolume += volume;

            // 浮力作用在没入部分的中点
            Vector3 point = Vector3.Lerp(bottom, top, fraction * 0.5f);
            Vector3 force = Vector3.up * (seaDensity * g * volume);

            Vector3 v = _rb.GetPointVelocity(point);
            Vector3 drag = -v * (linearDrag * volume);
            drag.y *= verticalDragScale;
            _rb.AddForceAtPosition(force + drag, point, ForceMode.Force);
        }

        _submergedFraction = totalVolume > 0f ? submergedVolume / totalVolume : 0f;
        // 角阻尼按没入程度缩放，离开水面（被抬起、搁浅在沙滩上）时不再额外减速
        _rb.angularDrag = angularDrag * Mathf.Clamp01(_submergedFraction * 4f);
    }

    // ------------------------------------------------------------------
    // 舱内水面遮罩：上传给水面着色器
    // ------------------------------------------------------------------

    static void UploadMasks(ScriptableRenderContext context, List<Camera> cameras)
    {
        int count = 0;
        foreach (var b in s_Boats)
        {
            if (count >= MaxHulls)
                break;
            if (!b.maskSea || b.stations.Length < 2)
                continue;
            int n = Mathf.Min(b.stations.Length, MaxStations);
            s_Matrices[count] = b.transform.worldToLocalMatrix;
            s_Params[count] = new Vector4(b.zMin, b.zMax, n, 0f);
            for (int i = 0; i < MaxStations; i++)
            {
                var s = b.stations[Mathf.Min(i, n - 1)];
                s_Stations[count * MaxStations + i] = new Vector4(s.innerBottom, s.rim, s.innerHalfWidthBottom, s.innerHalfWidthTop);
            }
            count++;
        }
        Shader.SetGlobalFloat(s_HullCount, count);
        Shader.SetGlobalMatrixArray(s_HullMatrices, s_Matrices);
        Shader.SetGlobalVectorArray(s_HullParams, s_Params);
        Shader.SetGlobalVectorArray(s_HullStations, s_Stations);
    }

    void OnDrawGizmosSelected()
    {
        if (stations.Length < 2)
            return;
        Gizmos.matrix = transform.localToWorldMatrix;
        int n = stations.Length;
        for (int i = 0; i < n; i++)
        {
            var s = stations[i];
            float z = Mathf.Lerp(zMin, zMax, i / (float)(n - 1));
            Gizmos.color = Color.cyan;
            DrawSection(z, s.outerBottom, s.rim, s.outerHalfWidthBottom, s.outerHalfWidthTop);
            Gizmos.color = Color.yellow;
            DrawSection(z, s.innerBottom, s.rim, s.innerHalfWidthBottom, s.innerHalfWidthTop);
        }

        static void DrawSection(float z, float y0, float y1, float w0, float w1)
        {
            var a = new Vector3(-w0, y0, z); var b = new Vector3(w0, y0, z);
            var c = new Vector3(w1, y1, z); var d = new Vector3(-w1, y1, z);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        }
    }

#if UNITY_EDITOR
    [Header("自动测量")]
    [Tooltip("测量时生成的截面数")]
    [Range(4, MaxStations)] public int stationCount = 14;
    [Tooltip("设计吃水（米，从船底算起），测量时据此设置刚体质量")]
    [Min(0.05f)] public float designDraft = 0.55f;

    [ContextMenu("从船体网格测量")]
    void MeasureFromMesh()
    {
        if (hullMesh == null || hullMesh.sharedMesh == null)
        {
            Debug.LogError("PLOceanBoat: 请先指定 hullMesh");
            return;
        }
        UnityEditor.Undo.RegisterFullObjectHierarchyUndo(gameObject, "Measure Hull");

        // 临时碰撞体，只用来做射线。不能挂在船下面：非凸 MeshCollider 在非运动学刚体下会被物理忽略。
        // 顶点先烘到船的本地空间，探针放在同样的位姿上
        var toLocal = transform.worldToLocalMatrix * hullMesh.transform.localToWorldMatrix;
        var src = hullMesh.sharedMesh;
        var verts = src.vertices;
        var lp = new Vector3[verts.Length];
        Vector3 mn = Vector3.one * float.MaxValue, mx = -mn;
        for (int i = 0; i < verts.Length; i++)
        {
            lp[i] = toLocal.MultiplyPoint3x4(verts[i]);
            mn = Vector3.Min(mn, lp[i]);
            mx = Vector3.Max(mx, lp[i]);
        }
        var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = src.indexFormat, vertices = lp, triangles = src.triangles };

        var probe = new GameObject("HullProbe") { hideFlags = HideFlags.HideAndDontSave };
        probe.transform.SetPositionAndRotation(transform.position, transform.rotation);
        probe.transform.localScale = transform.lossyScale;
        var mc = probe.AddComponent<MeshCollider>();
        mc.sharedMesh = baked;
        Physics.SyncTransforms();
        bool backfaces = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        try
        {
            int n = stationCount;
            zMin = mn.z + 0.05f;
            zMax = mx.z - 0.05f;
            float maxHalf = Mathf.Max(Mathf.Abs(mn.x), Mathf.Abs(mx.x)) + 1f;
            stations = new Station[n];
            for (int i = 0; i < n; i++)
                stations[i] = MeasureStation(mc, Mathf.Lerp(zMin, zMax, i / (float)(n - 1)), mn.y - 0.5f, mx.y + 0.5f, maxHalf);
        }
        finally
        {
            Physics.queriesHitBackfaces = backfaces;
            DestroyImmediate(probe);
            DestroyImmediate(baked);
        }

        BuildColumns();
        BuildColliders();
        SetupRigidbody();
        UnityEditor.EditorUtility.SetDirty(this);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log($"PLOceanBoat: 测量完成，{stations.Length} 个截面，{_columns.Count} 个浮力列，质量 {GetComponent<Rigidbody>().mass:F0} kg");
    }

    // 全部用射线测量：船体网格大面积平板处顶点很稀疏，按顶点切片会漏掉
    Station MeasureStation(MeshCollider mc, float z, float below, float above, float maxHalf)
    {
        var s = new Station();
        const float step = 0.05f;
        float rim = float.MinValue, lowest = float.MaxValue, wTop = 0f;
        for (float y = below; y <= above; y += step)
        {
            float w = OuterHalfWidth(mc, z, y, maxHalf);
            if (w <= 0f)
                continue;
            rim = Mathf.Max(rim, y);
            lowest = Mathf.Min(lowest, y);
            wTop = Mathf.Max(wTop, w);
        }
        if (rim == float.MinValue)
            return s;   // 这里没有船体，宽度为 0

        s.rim = rim;
        s.outerBottom = Cast(mc, new Vector3(0f, below, z), Vector3.up, out var bottomHit) ? Mathf.Min(bottomHit.y, lowest) : lowest;
        s.outerHalfWidthTop = wTop;
        s.outerHalfWidthBottom = Mathf.Max(OuterHalfWidth(mc, z, s.outerBottom + 0.15f * (rim - s.outerBottom), maxHalf), 0.05f);

        // 舱底：从上往下打。打到船舷高度附近说明这里是封闭甲板
        if (!Cast(mc, new Vector3(0f, above, z), Vector3.down, out var floorHit) || floorHit.y > rim - 0.1f)
        {
            s.innerBottom = rim;
            return s;
        }
        s.innerBottom = floorHit.y;
        s.innerHalfWidthBottom = InnerHalfWidth(mc, z, s.innerBottom + 0.1f, s.outerHalfWidthBottom);
        s.innerHalfWidthTop = InnerHalfWidth(mc, z, rim - 0.05f, wTop);
        return s;
    }

    // 从两侧外面往中心线打，取较宽的一侧
    float OuterHalfWidth(MeshCollider mc, float z, float y, float maxHalf)
    {
        float w = 0f;
        foreach (float side in new[] { 1f, -1f })
            if (Cast(mc, new Vector3(side * maxHalf, y, z), new Vector3(-side, 0f, 0f), out var hit) && hit.x * side > 0f)
                w = Mathf.Max(w, Mathf.Abs(hit.x));
        return w;
    }

    // 从中心线往两侧打，取较近的一侧
    float InnerHalfWidth(MeshCollider mc, float z, float y, float fallback)
    {
        float w = fallback;
        bool any = false;
        foreach (var dir in new[] { Vector3.right, Vector3.left })
        {
            if (Cast(mc, new Vector3(0f, y, z), dir, out var hit))
            {
                w = any ? Mathf.Min(w, Mathf.Abs(hit.x)) : Mathf.Abs(hit.x);
                any = true;
            }
        }
        return any ? w : Mathf.Max(fallback - 0.1f, 0f);
    }

    bool Cast(MeshCollider mc, Vector3 localOrigin, Vector3 localDir, out Vector3 localHit)
    {
        var ray = new Ray(transform.TransformPoint(localOrigin), transform.TransformDirection(localDir));
        if (mc.Raycast(ray, out var hit, 100f))
        {
            localHit = transform.InverseTransformPoint(hit.point);
            return true;
        }
        localHit = default;
        return false;
    }

    // 碰撞体：每段一个底板 + 两侧舷墙，舱内留空，人可以站进舱里，搁浅时船底压在沙上
    void BuildColliders()
    {
        const string name = "PLOcean Hull Colliders";
        var old = transform.Find(name);
        if (old != null)
            UnityEditor.Undo.DestroyObjectImmediate(old.gameObject);
        var root = new GameObject(name);
        UnityEditor.Undo.RegisterCreatedObjectUndo(root, "Hull Colliders");
        root.layer = gameObject.layer;
        root.transform.SetParent(transform, false);

        var material = new PhysicMaterial("PLOceanHull") { dynamicFriction = 0.5f, staticFriction = 0.6f, frictionCombine = PhysicMaterialCombine.Average };
        for (int i = 0; i + 1 < stations.Length; i++)
        {
            Station a = stations[i], b = stations[i + 1];
            if (a.outerHalfWidthTop <= 0f || b.outerHalfWidthTop <= 0f)
                continue;
            float z0 = Mathf.Lerp(zMin, zMax, i / (float)(stations.Length - 1));
            float z1 = Mathf.Lerp(zMin, zMax, (i + 1) / (float)(stations.Length - 1));
            float zc = (z0 + z1) * 0.5f, len = z1 - z0;
            float ob = (a.outerBottom + b.outerBottom) * 0.5f;
            float ib = (a.innerBottom + b.innerBottom) * 0.5f;
            float rim = (a.rim + b.rim) * 0.5f;
            float wBottom = (a.outerHalfWidthBottom + b.outerHalfWidthBottom) * 0.5f;
            float wTop = (a.outerHalfWidthTop + b.outerHalfWidthTop) * 0.5f;

            if (ib >= rim - 0.05f)
            {
                // 封闭甲板：一整块
                AddBox(root, material, new Vector3(0f, (ob + rim) * 0.5f, zc), new Vector3((wBottom + wTop), rim - ob, len));
                continue;
            }
            AddBox(root, material, new Vector3(0f, (ob + ib) * 0.5f, zc), new Vector3(wBottom * 2f, Mathf.Max(ib - ob, 0.1f), len));
            float wInner = (a.innerHalfWidthBottom + b.innerHalfWidthBottom + a.innerHalfWidthTop + b.innerHalfWidthTop) * 0.25f;
            float thick = Mathf.Max(wTop - wInner, 0.08f);
            float wallBottom = (ob + ib) * 0.5f;
            foreach (float side in new[] { -1f, 1f })
                AddBox(root, material, new Vector3(side * (wTop - thick * 0.5f), (wallBottom + rim) * 0.5f, zc), new Vector3(thick, rim - wallBottom, len));
        }
    }

    static void AddBox(GameObject root, PhysicMaterial material, Vector3 center, Vector3 size)
    {
        var box = root.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.sharedMaterial = material;
    }

    // 质量 = 设计吃水下的排水量，静水中正好吃水 designDraft
    void SetupRigidbody()
    {
        var rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = UnityEditor.Undo.AddComponent<Rigidbody>(gameObject);
        float sealine = MinOuterBottom() + designDraft;
        float volume = 0f;
        foreach (var c in _columns)
        {
            float span = c.top.y - c.bottom.y;
            volume += c.area * span * Mathf.Clamp01((sealine - c.bottom.y) / Mathf.Max(span, 1e-3f));
        }
        rb.mass = Mathf.Max(volume * seaDensity, 1f);
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.drag = 0f;
    }
#endif
}
