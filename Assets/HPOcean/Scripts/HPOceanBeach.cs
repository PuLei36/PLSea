using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 程序化生成的沙滩：从岸上缓慢倾斜到深水，叠加沙丘起伏和沙纹，用来观察水的折射、散射和边缘泡沫。
/// 坡度沿本地 +Z 方向抬升，高度相对物体自身的 Y。
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HPOceanBeach : MonoBehaviour
{
    [Header("尺寸")]
    [Min(1f)] public float sizeX = 240f;
    [Min(1f)] public float sizeZ = 240f;
    [Range(16, 400)] public int resolution = 256;

    [Header("剖面")]
    [Tooltip("最深处的高度（相对物体 Y）")]
    public float deepHeight = -14f;
    [Tooltip("岸上最高处的高度")]
    public float shoreHeight = 3f;
    [Tooltip("坡度开始抬升的本地 Z")]
    public float slopeStartZ = -80f;
    [Tooltip("坡度结束的本地 Z")]
    public float slopeEndZ = 70f;

    [Header("起伏")]
    [Tooltip("大尺度沙丘高度")]
    public float duneHeight = 1.2f;
    [Tooltip("沙丘尺度（米）")]
    public float duneScale = 35f;
    [Tooltip("沙纹高度")]
    public float rippleHeight = 0.06f;
    [Tooltip("沙纹波长（米）")]
    public float rippleWavelength = 1.4f;
    public int seed = 7;

    Mesh _mesh;

    void OnEnable() => Rebuild();
    void OnValidate() => _dirty = true;

    bool _dirty;

    void Update()
    {
        if (_dirty)
        {
            _dirty = false;
            Rebuild();
        }
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

    public float SampleHeight(float x, float z)
    {
        float t = Mathf.InverseLerp(slopeStartZ, slopeEndZ, z);
        float h = Mathf.Lerp(deepHeight, shoreHeight, t * t * (3f - 2f * t));

        float ox = seed * 17.31f, oz = seed * 9.73f;
        float dune = Mathf.PerlinNoise(x / duneScale + ox, z / duneScale + oz) - 0.5f;
        dune += 0.5f * (Mathf.PerlinNoise(x / (duneScale * 0.4f) + oz, z / (duneScale * 0.4f) + ox) - 0.5f);
        h += dune * duneHeight * 2f;

        // 沙纹大致垂直于坡度方向，用噪声扭一下避免过于规则
        float warp = (Mathf.PerlinNoise(x * 0.05f + ox, z * 0.05f) - 0.5f) * 6f;
        float ripple = Mathf.Sin((z + warp + x * 0.15f) * (2f * Mathf.PI / rippleWavelength));
        h += ripple * rippleHeight * (1f - t * 0.7f);
        return h;
    }

    public void Rebuild()
    {
        if (_mesh == null)
        {
            _mesh = new Mesh { name = "HPOceanBeach", hideFlags = HideFlags.DontSave };
            GetComponent<MeshFilter>().sharedMesh = _mesh;
        }

        int n = resolution + 1;
        var vertices = new Vector3[n * n];
        var uvs = new Vector2[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                float x = (i / (float)resolution - 0.5f) * sizeX;
                float z = (j / (float)resolution - 0.5f) * sizeZ;
                int v = j * n + i;
                vertices[v] = new Vector3(x, SampleHeight(x, z), z);
                uvs[v] = new Vector2(x, z) * 0.25f;
            }
        }

        var indices = new int[resolution * resolution * 6];
        int k = 0;
        for (int j = 0; j < resolution; j++)
        {
            for (int i = 0; i < resolution; i++)
            {
                int v = j * n + i;
                indices[k++] = v;
                indices[k++] = v + n;
                indices[k++] = v + n + 1;
                indices[k++] = v;
                indices[k++] = v + n + 1;
                indices[k++] = v + 1;
            }
        }

        _mesh.Clear();
        _mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = vertices;
        _mesh.uv = uvs;
        _mesh.triangles = indices;
        _mesh.RecalculateNormals();
        _mesh.RecalculateTangents();
        _mesh.RecalculateBounds();

        var meshCollider = GetComponent<MeshCollider>();
        if (meshCollider != null)
            meshCollider.sharedMesh = _mesh;
    }
}
