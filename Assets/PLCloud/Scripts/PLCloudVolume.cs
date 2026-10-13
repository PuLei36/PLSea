using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// 体积云的参数入口与云影。云层是跟随地球曲率的球壳（高度、范围都在材质里设置），一直铺到地平线，
/// 不再需要包围盒；这个物体的 Transform 不影响云的形状。
/// 渲染由 CustomPassVolume 里的 PLCloudPass 完成；这里只负责云影 RT 和太阳 cookie。
/// </summary>
[ExecuteAlways]
public class PLCloudVolume : MonoBehaviour
{
    public static PLCloudVolume Active { get; private set; }

    static readonly int s_PlanetRadius = Shader.PropertyToID("_PlanetRadius");
    static readonly int s_SeaLevel = Shader.PropertyToID("_SeaLevel");

    public Material material;

    [Header("Cloud Shadow")]
    [Tooltip("把云的透射率作为太阳的 cookie，地形和体积雾自动接收云影")]
    public bool castShadows = true;
    public Light sun;
    [Tooltip("cookie 跟随这个物体（通常是主相机）；为空时用 Camera.main")]
    public Transform shadowFollow;
    [Tooltip("云影覆盖的边长（米），以跟随目标为中心")]
    public float shadowFootprint = 20000f;
    [Range(64, 2048)] public int shadowResolution = 512;
    [Range(0f, 1f)] public float shadowStrength = 0.85f;

    RenderTexture _shadowTexture;
    Texture _previousCookie;
    Vector2 _previousCookieSize;
    Light _cookieLight;

    public RenderTexture ShadowTexture => _shadowTexture;

    void OnEnable()
    {
        Active = this;
    }

    void OnDisable()
    {
        if (Active == this)
            Active = null;
        RestoreCookie();
        if (_shadowTexture != null)
        {
            _shadowTexture.Release();
            DestroyImmediate(_shadowTexture);
            _shadowTexture = null;
        }
    }

    void Update()
    {
        // 云层与海面共用同一个行星：有 SeaMesh 时以它的半径和水面高度为准
        var sea = SeaMesh.Active;
        if (material != null && sea != null && sea.planetRadius > 0f)
        {
            // 材质是资源，值没变就不写，避免编辑器里每帧把 .mat 标脏
            if (material.GetFloat(s_PlanetRadius) != sea.planetRadius)
                material.SetFloat(s_PlanetRadius, sea.planetRadius);
            if (material.GetFloat(s_SeaLevel) != sea.transform.position.y)
                material.SetFloat(s_SeaLevel, sea.transform.position.y);
        }

        if (!castShadows || sun == null || material == null)
        {
            RestoreCookie();
            return;
        }

        EnsureShadowTexture();

        // HDRP 方向光 cookie 以灯的位置为投影中心，把灯挪到跟随目标处，按 cookie 像素对齐避免云影游动。
        // 方向光的位置对其他光照没有影响。
        var follow = shadowFollow != null ? shadowFollow : (Camera.main != null ? Camera.main.transform : null);
        if (follow != null)
        {
            float texel = ShadowFootprint / _shadowTexture.width;
            Vector3 p = follow.position;
            p = new Vector3(Mathf.Round(p.x / texel) * texel, 0f, Mathf.Round(p.z / texel) * texel);
            if ((sun.transform.position - p).sqrMagnitude > 0.01f)
                sun.transform.position = p;
        }

        var hdLight = sun.GetComponent<HDAdditionalLightData>();
        if (_cookieLight != sun)
        {
            RestoreCookie();
            _cookieLight = sun;
            _previousCookie = sun.cookie;
            if (hdLight != null)
                _previousCookieSize = new Vector2(hdLight.shapeWidth, hdLight.shapeHeight);
        }

        float size = ShadowFootprint;
        if (hdLight != null && (sun.cookie != _shadowTexture || !Mathf.Approximately(hdLight.shapeWidth, size)))
            hdLight.SetCookie(_shadowTexture, new Vector2(size, size));
    }

    public float ShadowFootprint => Mathf.Max(100f, shadowFootprint);

    void EnsureShadowTexture()
    {
        int resolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(shadowResolution, 64, 2048));
        if (_shadowTexture != null && _shadowTexture.width == resolution)
            return;
        if (_shadowTexture != null)
        {
            _shadowTexture.Release();
            DestroyImmediate(_shadowTexture);
        }

        // cookie 图集按 rgb 采样，单通道格式会把阳光染成红色，所以用 ARGB32
        _shadowTexture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            name = "PLCloudShadow",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        _shadowTexture.Create();
    }

    void RestoreCookie()
    {
        if (_cookieLight != null)
        {
            var hdLight = _cookieLight.GetComponent<HDAdditionalLightData>();
            if (_cookieLight.cookie == _shadowTexture)
            {
                if (hdLight != null && _previousCookie != null)
                    hdLight.SetCookie(_previousCookie, _previousCookieSize);
                else
                    _cookieLight.cookie = _previousCookie;
            }
        }
        _cookieLight = null;
        _previousCookie = null;
    }
}
