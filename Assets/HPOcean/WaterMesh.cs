using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 以相机为中心的圆形 LOD 水面网格。
/// 用四叉树细分：格子离中心越近切得越细，第 k 级格子边长 = baseCellSize * 2^k，
/// 覆盖半径 innerRadius * 2^(k-1) ~ innerRadius * 2^k 的圆环，所以 LOD 边界是一圈圈圆形。
/// 细分后做了 2:1 平衡，并在粗细交界处插入边中点缝合，没有 T 型接缝，后面做顶点动画不会裂缝。
/// </summary>
[ExecuteAlways]
public class WaterMesh : MonoBehaviour
{
    [Header("网格")]
    [Tooltip("最内层（最密）网格单元的边长，单位米")]
    [Min(0.01f)] public float baseCellSize = 0.25f;

    [Tooltip("最密区域的半径，单位米。每往外一级半径翻倍、格子边长翻倍")]
    [Min(0.1f)] public float innerRadius = 8f;

    [Tooltip("LOD 级数（不含最密的第 0 级）")]
    [Range(0, 12)] public int lodLevels = 7;

    [Tooltip("整个水面网格的半径，单位米，超出部分会被裁掉。最大不超过 128 个最粗格子")]
    [Min(1f)] public float outerRadius = 1500f;

    [Header("渲染")]
    public Material waterMaterial;

    [Tooltip("网格中心放在相机前方多远的位置")]
    public float forwardOffset = 10f;

    [Tooltip("位置对齐步长 = baseCellSize * 2^snapLevel。层级 <= snapLevel 的顶点在移动时完全不\"游动\"")]
    [Range(0, 12)] public int snapLevel = 4;

    [Tooltip("包围盒的垂直扩展，给后面的顶点动画留余量，避免被错误剔除")]
    public float boundsHeight = 10f;

    Mesh _mesh;
    bool _dirty = true;
    MaterialPropertyBlock _props;

    static readonly int s_WaterLevel = Shader.PropertyToID("_HPOceanWaterLevel");
    static readonly int s_CameraId = Shader.PropertyToID("_HPOceanCameraId");

    public Mesh Mesh
    {
        get
        {
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "WaterLodMesh", hideFlags = HideFlags.DontSave };
                _dirty = true;
            }
            if (_dirty)
            {
                new LodMeshBuilder(baseCellSize, innerRadius, lodLevels, outerRadius).Build(_mesh, boundsHeight);
                _dirty = false;
            }
            return _mesh;
        }
    }

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    void OnDestroy()
    {
        if (_mesh == null)
            return;
        if (Application.isPlaying)
            Destroy(_mesh);
        else
            DestroyImmediate(_mesh);
    }

    void OnValidate()
    {
        _dirty = true;
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam.cameraType == CameraType.Preview)
            return;
        if (waterMaterial == null)
            return;

        // 网格中心放到相机前方，并按步长对齐，避免顶点随相机移动而滑动
        Vector3 center = cam.transform.position + cam.transform.forward * forwardOffset;
        float step = baseCellSize * (1 << Mathf.Clamp(snapLevel, 0, lodLevels));
        center.x = Mathf.Round(center.x / step) * step;
        center.z = Mathf.Round(center.z / step) * step;
        center.y = transform.position.y;

        // 水面高度给水下 Pass 用；相机编号让水面只使用属于自己相机的体积缓冲
        Shader.SetGlobalFloat(s_WaterLevel, transform.position.y);
        _props ??= new MaterialPropertyBlock();
        _props.SetFloat(s_CameraId, HPOceanCameraIds.Get(cam));

        Graphics.DrawMesh(Mesh,
            Matrix4x4.TRS(center, Quaternion.identity, Vector3.one),
            waterMaterial,
            gameObject.layer,
            cam,
            0,
            _props,
            ShadowCastingMode.Off,
            true,
            null,
            LightProbeUsage.Off,
            null);
    }

    /// <summary>
    /// 四叉树 LOD 网格生成器。格子用 (level, x, z) 表示，x/z 是该级格子的整数索引，
    /// 索引原点在网格左下角，网格中心正好落在所有级别的格点上。
    /// </summary>
    class LodMeshBuilder
    {
        readonly double _cellSize;
        readonly double _innerRadius;
        readonly double _outerRadius;
        readonly int _maxLevel;
        readonly int _rootCount;    // 每条边上最粗一级的格子数（偶数）
        readonly double _halfExtent;

        const int MaxRootsPerRadius = 128;

        readonly HashSet<long> _roots = new HashSet<long>();
        readonly HashSet<long> _leaves = new HashSet<long>();

        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<int> _indices = new List<int>();
        readonly Dictionary<long, int> _vertexLookup = new Dictionary<long, int>();

        public LodMeshBuilder(float cellSize, float innerRadius, int levels, float outerRadius)
        {
            _cellSize = cellSize;
            _innerRadius = innerRadius;
            _maxLevel = levels;

            // 限制最粗一级的格子数量，避免 lodLevels 太小时外圈全是密格子把编辑器卡死
            double rootSize = CellSize(_maxLevel);
            _outerRadius = System.Math.Min(outerRadius, MaxRootsPerRadius * rootSize);
            _rootCount = Mathf.Max(2, 2 * Mathf.CeilToInt((float)(_outerRadius / rootSize)));
            _halfExtent = _rootCount * rootSize * 0.5;
        }

        public void Build(Mesh mesh, float boundsHeight)
        {
            Generate();

            int count = _vertices.Count;
            var normals = new Vector3[count];
            var tangents = new Vector4[count];
            var uvs = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, -1f);
                uvs[i] = new Vector2(_vertices[i].x, _vertices[i].z);
            }

            mesh.Clear();
            mesh.indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(_indices, 0);
            mesh.RecalculateBounds();

            Bounds b = mesh.bounds;
            b.Expand(new Vector3(0f, boundsHeight * 2f, 0f));
            mesh.bounds = b;
        }

        // 只生成顶点和索引，不触碰 Mesh 对象
        void Generate()
        {
            for (int z = 0; z < _rootCount; z++)
            {
                for (int x = 0; x < _rootCount; x++)
                {
                    if (NearestDistance(_maxLevel, x, z) >= _outerRadius)
                        continue;
                    _roots.Add(Key(_maxLevel, x, z));
                    Subdivide(_maxLevel, x, z);
                }
            }

            Balance();

            foreach (long leaf in _leaves)
            {
                Unpack(leaf, out int level, out int x, out int z);
                EmitCell(level, x, z);
            }
        }

        double CellSize(int level) => _cellSize * (1 << level);

        // 距离 d 处期望的 LOD 级别：d < innerRadius 为 0，之后每过一个"翻倍半径"加 1
        int DesiredLevel(double d)
        {
            if (d < _innerRadius)
                return 0;
            int level = (int)System.Math.Floor(System.Math.Log(d / _innerRadius, 2.0)) + 1;
            return Mathf.Min(level, _maxLevel);
        }

        // 格子到网格中心的最近距离
        double NearestDistance(int level, int x, int z)
        {
            double s = CellSize(level);
            double x0 = x * s - _halfExtent, x1 = x0 + s;
            double z0 = z * s - _halfExtent, z1 = z0 + s;
            double dx = System.Math.Max(0.0, System.Math.Max(x0, -x1));
            double dz = System.Math.Max(0.0, System.Math.Max(z0, -z1));
            return System.Math.Sqrt(dx * dx + dz * dz);
        }

        void Subdivide(int level, int x, int z)
        {
            if (level > 0 && DesiredLevel(NearestDistance(level, x, z)) < level)
            {
                for (int i = 0; i < 4; i++)
                    Subdivide(level - 1, 2 * x + (i & 1), 2 * z + (i >> 1));
            }
            else
            {
                _leaves.Add(Key(level, x, z));
            }
        }

        bool InRoots(int level, int x, int z)
        {
            int n = _rootCount << (_maxLevel - level);
            if (x < 0 || z < 0 || x >= n || z >= n)
                return false;
            int shift = _maxLevel - level;
            return _roots.Contains(Key(_maxLevel, x >> shift, z >> shift));
        }

        // 找到覆盖 (level, x, z) 这块区域的叶子级别；区域被切得更细返回 -1，区域在网格外返回 int.MaxValue
        int FindCover(int level, int x, int z)
        {
            if (!InRoots(level, x, z))
                return int.MaxValue;
            for (int m = level; m <= _maxLevel; m++)
            {
                int shift = m - level;
                if (_leaves.Contains(Key(m, x >> shift, z >> shift)))
                    return m;
            }
            return -1;
        }

        static readonly int[] DirX = { -1, 0, 1, 0 };
        static readonly int[] DirZ = { 0, 1, 0, -1 };

        // 保证相邻叶子级别差不超过 1，这样每条边上最多只需要插一个中点
        void Balance()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (long leaf in new List<long>(_leaves))
                {
                    if (!_leaves.Contains(leaf))
                        continue;
                    Unpack(leaf, out int level, out int x, out int z);
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + DirX[d], nz = z + DirZ[d];
                        int m = FindCover(level, nx, nz);
                        if (m == int.MaxValue || m <= level + 1)
                            continue;

                        int shift = m - level;
                        int cx = nx >> shift, cz = nz >> shift;
                        _leaves.Remove(Key(m, cx, cz));
                        for (int i = 0; i < 4; i++)
                            _leaves.Add(Key(m - 1, 2 * cx + (i & 1), 2 * cz + (i >> 1)));
                        changed = true;
                    }
                }
            }
        }

        // 顶点按最细一级的格点坐标去重
        int Vertex(long fx, long fz)
        {
            long key = (fx << 32) | fz;
            if (!_vertexLookup.TryGetValue(key, out int index))
            {
                index = _vertices.Count;
                _vertexLookup.Add(key, index);
                _vertices.Add(new Vector3((float)(fx * _cellSize - _halfExtent), 0f, (float)(fz * _cellSize - _halfExtent)));
            }
            return index;
        }

        readonly List<int> _poly = new List<int>(8);

        void EmitCell(int level, int x, int z)
        {
            long size = 1L << level;
            long half = size >> 1;
            long x0 = x * size, x1 = x0 + size;
            long z0 = z * size, z1 = z0 + size;

            // 边界多边形按顺时针（从上往下看）：左下 [左中] 左上 [上中] 右上 [右中] 右下 [下中]
            // 相邻格子更细时，在这条边上插入中点和它对齐
            _poly.Clear();
            _poly.Add(Vertex(x0, z0));
            if (level > 0 && FindCover(level, x - 1, z) == -1) _poly.Add(Vertex(x0, z0 + half));
            _poly.Add(Vertex(x0, z1));
            if (level > 0 && FindCover(level, x, z + 1) == -1) _poly.Add(Vertex(x0 + half, z1));
            _poly.Add(Vertex(x1, z1));
            if (level > 0 && FindCover(level, x + 1, z) == -1) _poly.Add(Vertex(x1, z0 + half));
            _poly.Add(Vertex(x1, z0));
            if (level > 0 && FindCover(level, x, z - 1) == -1) _poly.Add(Vertex(x0 + half, z0));

            if (_poly.Count == 4)
            {
                _indices.Add(_poly[0]); _indices.Add(_poly[1]); _indices.Add(_poly[2]);
                _indices.Add(_poly[0]); _indices.Add(_poly[2]); _indices.Add(_poly[3]);
                return;
            }

            // 有中点时以格子中心为扇心三角化
            int c = Vertex(x0 + half, z0 + half);
            for (int i = 0; i < _poly.Count; i++)
            {
                _indices.Add(c);
                _indices.Add(_poly[i]);
                _indices.Add(_poly[(i + 1) % _poly.Count]);
            }
        }

        static long Key(int level, int x, int z) => ((long)level << 48) | ((long)x << 24) | (long)z;

        static void Unpack(long key, out int level, out int x, out int z)
        {
            level = (int)(key >> 48);
            x = (int)((key >> 24) & 0xFFFFFF);
            z = (int)(key & 0xFFFFFF);
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Save Mesh As Asset")]
    void SaveMeshAsset()
    {
        string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath("Assets/WaterLodMesh.asset");
        var copy = Instantiate(Mesh);
        copy.hideFlags = HideFlags.None;
        UnityEditor.AssetDatabase.CreateAsset(copy, path);
        UnityEditor.AssetDatabase.SaveAssets();
        Debug.Log($"Water mesh saved to {path}");
    }
#endif
}
