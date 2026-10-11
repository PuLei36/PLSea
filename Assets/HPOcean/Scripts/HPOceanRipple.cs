using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// 基于 RT 的交互水波（参考 HPWater 的 HPWaterFluidDynamics）：
/// 相机周围一块正方形区域里解二维波动方程，波高写进全局纹理，水面着色器叠加到 FFT 海浪上（位移、法线、焦散）。
///   1. 俯视渲染场景高度：高于水面的物体是障碍（波会反射），浅滩额外衰减
///   2. 区域跟随相机按整像素平移，世界空间里的波纹保持不动
///   3. 固定 60Hz 子步推进波动方程，四周海绵层吸收
/// 波源：穿过水面的物体移动时自动产生（对比前后两帧的场景高度），按住鼠标左键在水面拖动（运行时），
/// HPOceanRippleSource 组件，或 AddImpulse。
/// </summary>
[ExecuteAlways]
public class HPOceanRipple : MonoBehaviour
{
    const int MaxSources = 32;
    const float StepTime = 1f / 60f;
    const int MaxStepsPerFrame = 4;

    [Header("模拟区域")]
    [Tooltip("模拟区域边长（米），以相机为中心")]
    [Min(4f)] public float areaSize = 64f;
    [Tooltip("波高纹理分辨率")]
    public Resolution resolution = Resolution._512;
    [Tooltip("模拟区域跟随哪个相机，留空用 Camera.main")]
    public Camera followCamera;

    [Header("波动方程")]
    [Tooltip("波速（米/秒），受 CFL 条件限制会自动夹到稳定范围")]
    [Range(0.1f, 6f)] public float waveSpeed = 1.6f;
    [Tooltip("每步的速度阻尼，越大波纹消失越快")]
    [Range(0f, 0.1f)] public float damping = 0.012f;
    [Tooltip("比这更浅的水里波纹衰减更快（米），0 关闭")]
    [Min(0f)] public float shallowDepth = 0.6f;
    [Tooltip("场景高于水面多少米算障碍物")]
    public float obstacleOffset = 0.05f;
    [Tooltip("波高上限（米）")]
    [Min(0.01f)] public float maxHeight = 0.25f;

    [Header("场景高度")]
    [Tooltip("参与障碍物判断的层")]
    public LayerMask obstacleLayers = ~0;
    [Tooltip("多久重新收集一次场景里的 Renderer（秒）")]
    [Min(0.1f)] public float rendererRefreshInterval = 1f;
    [Tooltip("穿过水面的物体移动时，每占据/离开一个格子推高/拉低多少米，0 关闭")]
    [Min(0f)] public float objectDisplacement = 0.04f;

    [Header("鼠标交互（运行时按住左键）")]
    public bool mouseInteraction = true;
    [Min(0.01f)] public float mouseRadius = 0.35f;
    [Tooltip("每秒把水面推高/压低多少米，负数为下压")]
    public float mouseStrength = -6f;

    public enum Resolution { _256 = 256, _512 = 512, _1024 = 1024 }

    /// <summary>自定义波源，每帧往 output 里加 (世界 X, 世界 Z, 半径, 每秒推高米数)。</summary>
    public interface IEmitter { void GetSources(List<Vector4> output); }

    static readonly List<HPOceanRippleSource> s_Sources = new List<HPOceanRippleSource>();
    static readonly List<IEmitter> s_Emitters = new List<IEmitter>();
    static readonly List<Vector4> s_Impulses = new List<Vector4>();
    static readonly HashSet<Renderer> s_Excluded = new HashSet<Renderer>();
    static readonly List<Vector4> s_EmitterBuffer = new List<Vector4>();

    /// <summary>当前启用的模拟（取最后启用的那个）。</summary>
    public static HPOceanRipple Active { get; private set; }

    public static void RegisterEmitter(IEmitter e) { if (!s_Emitters.Contains(e)) s_Emitters.Add(e); }
    public static void UnregisterEmitter(IEmitter e) => s_Emitters.Remove(e);
    /// <summary>不画进场景高度：不当障碍、也不靠轮廓挤水（由自己的波源负责交互）。</summary>
    public static void ExcludeRenderer(Renderer r) { if (r != null) s_Excluded.Add(r); }
    public static void IncludeRenderer(Renderer r) => s_Excluded.Remove(r);

    public static void Register(HPOceanRippleSource source) { if (!s_Sources.Contains(source)) s_Sources.Add(source); }
    public static void Unregister(HPOceanRippleSource source) => s_Sources.Remove(source);

    /// <summary>在世界坐标处产生一次波源，持续一个模拟步。height 为这一下推高的米数（负数下压）。</summary>
    public static void AddImpulse(Vector3 position, float radius, float height)
    {
        if (s_Impulses.Count < MaxSources)
            s_Impulses.Add(new Vector4(position.x, position.z, radius, height / StepTime));
    }

    ComputeShader _compute;
    int _kShift, _kUpdate;
    RenderTexture _current, _previous, _temp, _sceneHeight, _prevSceneHeight, _display;
    bool _hasPrevSceneHeight;
    Material _heightMaterial;
    CommandBuffer _heightCmd;
    readonly List<Renderer> _renderers = new List<Renderer>();
    float _nextRendererRefresh;

    Vector2Int _originTexel;     // 区域左下角的世界整像素坐标
    bool _hasOrigin;
    float _accumulator;
    float _lastTime;
    int _lastFrame = -1;
    readonly Vector4[] _sourceArray = new Vector4[MaxSources];

    static readonly int s_RippleTex = Shader.PropertyToID("_HPOceanRipple");
    static readonly int s_RippleParams = Shader.PropertyToID("_HPOceanRippleParams");
    static readonly int s_HeightVP = Shader.PropertyToID("_HPOceanRippleHeightVP");

    int Res => (int)resolution;
    float TexelSize => areaSize / Res;

    void OnEnable()
    {
        _compute = Resources.Load<ComputeShader>("HPOceanRipple");
        var heightShader = Shader.Find("Hidden/HPOcean/RippleSceneHeight");
        if (_compute == null || heightShader == null)
        {
            Debug.LogError("HPOcean: 找不到 Resources/HPOceanRipple.compute 或 Hidden/HPOcean/RippleSceneHeight");
            enabled = false;
            return;
        }
        _kShift = _compute.FindKernel("RippleShift");
        _kUpdate = _compute.FindKernel("RippleUpdate");
        _heightMaterial = new Material(heightShader) { hideFlags = HideFlags.HideAndDontSave };
        _heightCmd = new CommandBuffer { name = "HPOcean Ripple Scene Height" };

        AllocateTextures();
        Active = this;
        _lastTime = CurrentTime();
        _nextRendererRefresh = 0f;
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        if (Active == this)
            Active = null;
        ReleaseTextures();
        if (_heightMaterial != null)
            DestroyImmediate(_heightMaterial);
        _heightCmd?.Release();
        _heightCmd = null;
        Shader.SetGlobalVector(s_RippleParams, Vector4.zero);
    }

    void OnValidate()
    {
        // 分辨率改了需要重建纹理
        if (_current != null && _current.width != Res)
        {
            ReleaseTextures();
            AllocateTextures();
        }
    }

    void AllocateTextures()
    {
        _current = CreateRT(GraphicsFormat.R32_SFloat, "HPOcean_RippleA");
        _previous = CreateRT(GraphicsFormat.R32_SFloat, "HPOcean_RippleB");
        _temp = CreateRT(GraphicsFormat.R32_SFloat, "HPOcean_RippleTemp");
        // 给水面着色器读的副本，带 mip：顶点着色器按网格疏密取 mip，粗网格上不出尖三角
        // （模拟用的几张会和 _temp 互相交换，不能带 mip）
        _display = CreateRT(GraphicsFormat.R32_SFloat, "HPOcean_RippleDisplay", 0, true);
        _sceneHeight = CreateRT(GraphicsFormat.R32_SFloat, "HPOcean_RippleSceneHeight", 24);
        _prevSceneHeight = CreateRT(GraphicsFormat.R32_SFloat, "HPOcean_RipplePrevSceneHeight");
        _hasOrigin = false;
        _hasPrevSceneHeight = false;
    }

    void ReleaseTextures()
    {
        Release(ref _current);
        Release(ref _previous);
        Release(ref _temp);
        Release(ref _sceneHeight);
        Release(ref _prevSceneHeight);
        Release(ref _display);
    }

    RenderTexture CreateRT(GraphicsFormat format, string name, int depthBits = 0, bool mips = false)
    {
        var rt = new RenderTexture(Res, Res, depthBits, format, mips ? 0 : 1)
        {
            name = name,
            useMipMap = mips,
            autoGenerateMips = false,
            enableRandomWrite = true,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,
        };
        rt.Create();
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(depthBits > 0, true, Color.clear);
        RenderTexture.active = previous;
        return rt;
    }

    static void Release(ref RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        DestroyImmediate(rt);
        rt = null;
    }

    static float CurrentTime()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            return (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
        return Time.time;
    }

    void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
    {
        if (Time.renderedFrameCount == _lastFrame)
            return;
        _lastFrame = Time.renderedFrameCount;

        float now = CurrentTime();
        float dt = Mathf.Clamp(now - _lastTime, 0f, 0.1f);
        _lastTime = now;

        var cam = followCamera != null ? followCamera : Camera.main;
#if UNITY_EDITOR
        // 编辑模式下跟随 Scene 视图相机
        if (!Application.isPlaying && UnityEditor.SceneView.lastActiveSceneView != null)
            cam = UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
        if (cam == null)
            return;

        var cmd = _heightCmd;
        cmd.Clear();
        UpdateOrigin(cmd, cam.transform.position);
        RenderSceneHeight(cmd);

        _accumulator = Mathf.Min(_accumulator + dt, StepTime * MaxStepsPerFrame);
        // 场景高度每帧只渲染一次，物体挤水只在这一帧的第一步注入
        float displace = _hasPrevSceneHeight ? objectDisplacement : 0f;
        while (_accumulator >= StepTime)
        {
            _accumulator -= StepTime;
            Step(cmd, displace);
            displace = 0f;
        }
        // 这一帧没推进模拟就保留旧的高度，变化留到下一步再注入
        if (displace == 0f)
        {
            cmd.CopyTexture(_sceneHeight, _prevSceneHeight);
            _hasPrevSceneHeight = true;
        }
        cmd.CopyTexture(_current, 0, 0, _display, 0, 0);
        cmd.GenerateMips(_display);

        Graphics.ExecuteCommandBuffer(cmd);

        Vector2 min = (Vector2)_originTexel * TexelSize;
        Shader.SetGlobalTexture(s_RippleTex, _display);
        Shader.SetGlobalVector(s_RippleParams, new Vector4(min.x, min.y, areaSize, TexelSize));
    }

    float WaterLevel => transform.position.y;

    // 区域以相机为中心，按整像素对齐；移动时把已有波高整体平移
    void UpdateOrigin(CommandBuffer cmd, Vector3 cameraPos)
    {
        float texel = TexelSize;
        var origin = new Vector2Int(Mathf.FloorToInt(cameraPos.x / texel) - Res / 2,
                                    Mathf.FloorToInt(cameraPos.z / texel) - Res / 2);
        if (!_hasOrigin)
        {
            _originTexel = origin;
            _hasOrigin = true;
            return;
        }

        var shift = origin - _originTexel;
        if (shift == Vector2Int.zero)
            return;
        _originTexel = origin;

        cmd.SetComputeIntParams(_compute, "_Shift", shift.x, shift.y);
        cmd.SetComputeIntParam(_compute, "_Resolution", Res);
        Shift(cmd, ref _current);
        Shift(cmd, ref _previous);
        Shift(cmd, ref _prevSceneHeight);
    }

    void Shift(CommandBuffer cmd, ref RenderTexture rt)
    {
        cmd.SetComputeTextureParam(_compute, _kShift, "_ShiftSrc", rt);
        cmd.SetComputeTextureParam(_compute, _kShift, "_ShiftDst", _temp);
        cmd.DispatchCompute(_compute, _kShift, Res / 8, Res / 8, 1);
        (rt, _temp) = (_temp, rt);
    }

    // 俯视正交渲染场景最高点的世界高度
    void RenderSceneHeight(CommandBuffer cmd)
    {
        if (CurrentTime() >= _nextRendererRefresh)
        {
            _nextRendererRefresh = CurrentTime() + rendererRefreshInterval;
            _renderers.Clear();
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if ((r is MeshRenderer || r is SkinnedMeshRenderer) && (obstacleLayers.value & (1 << r.gameObject.layer)) != 0)
                    _renderers.Add(r);
            }
        }

        Vector2 min = (Vector2)_originTexel * TexelSize;
        float top = WaterLevel + 50f;
        float range = 100f;
        // 自己拼投影：x/z → 裁剪空间 xy，高度 → 深度（越高越近）
        bool flip = SystemInfo.graphicsUVStartsAtTop;
        var vp = Matrix4x4.zero;
        vp.m00 = 2f / areaSize; vp.m03 = -1f - 2f * min.x / areaSize;
        float sy = flip ? -1f : 1f;
        vp.m12 = sy * 2f / areaSize; vp.m13 = sy * (-1f - 2f * min.y / areaSize);
        vp.m21 = SystemInfo.usesReversedZBuffer ? 1f / range : -1f / range;
        vp.m23 = SystemInfo.usesReversedZBuffer ? 1f - top / range : top / range;
        vp.m33 = 1f;
        cmd.SetGlobalMatrix(s_HeightVP, vp);

        cmd.SetRenderTarget(_sceneHeight, _sceneHeight.depthBuffer);
        cmd.ClearRenderTarget(true, true, new Color(-1000f, 0, 0, 0));

        var areaBounds = new Bounds(new Vector3(min.x + areaSize * 0.5f, WaterLevel, min.y + areaSize * 0.5f),
                                    new Vector3(areaSize, range * 2f, areaSize));
        foreach (var r in _renderers)
        {
            if (r == null || s_Excluded.Contains(r) || !r.enabled || !r.gameObject.activeInHierarchy || !r.bounds.Intersects(areaBounds))
                continue;
            int subMeshCount = r is MeshRenderer ? (r.GetComponent<MeshFilter>()?.sharedMesh?.subMeshCount ?? 0)
                                                 : (((SkinnedMeshRenderer)r).sharedMesh?.subMeshCount ?? 0);
            for (int i = 0; i < subMeshCount; i++)
                cmd.DrawRenderer(r, _heightMaterial, i, 0);
        }
    }

    void Step(CommandBuffer cmd, float displace)
    {
        float texel = TexelSize;
        // CFL：c·Δt/Δx 必须小于 1/√2
        float courant = Mathf.Min(waveSpeed * StepTime / texel, 0.7f);

        int count = CollectSources();
        cmd.SetComputeIntParam(_compute, "_Resolution", Res);
        cmd.SetComputeFloatParam(_compute, "_Courant2", courant * courant);
        cmd.SetComputeFloatParam(_compute, "_Retention", 1f - damping);
        cmd.SetComputeFloatParam(_compute, "_WaterLevel", WaterLevel);
        cmd.SetComputeFloatParam(_compute, "_ObstacleOffset", obstacleOffset);
        cmd.SetComputeFloatParam(_compute, "_ShallowDepth", shallowDepth);
        cmd.SetComputeFloatParam(_compute, "_MaxHeight", maxHeight);
        cmd.SetComputeFloatParam(_compute, "_StepTime", StepTime);
        Vector2 min = (Vector2)_originTexel * texel;
        cmd.SetComputeVectorParam(_compute, "_BoxMin", new Vector4(min.x, min.y, texel, 0f));
        cmd.SetComputeIntParam(_compute, "_SourceCount", count);
        cmd.SetComputeVectorArrayParam(_compute, "_Sources", _sourceArray);

        cmd.SetComputeTextureParam(_compute, _kUpdate, "_RippleCurrent", _current);
        cmd.SetComputeTextureParam(_compute, _kUpdate, "_RipplePrevious", _previous);
        cmd.SetComputeTextureParam(_compute, _kUpdate, "_SceneHeight", _sceneHeight);
        cmd.SetComputeTextureParam(_compute, _kUpdate, "_PrevSceneHeight", _prevSceneHeight);
        cmd.SetComputeFloatParam(_compute, "_Displace", displace);
        cmd.DispatchCompute(_compute, _kUpdate, Res / 8, Res / 8, 1);

        // Previous 里现在是 t+1
        (_current, _previous) = (_previous, _current);
    }

    int CollectSources()
    {
        int count = 0;

        foreach (var impulse in s_Impulses)
        {
            if (count < MaxSources)
                _sourceArray[count++] = impulse;
        }
        s_Impulses.Clear();

        if (Application.isPlaying && mouseInteraction && Input.GetMouseButton(0) && count < MaxSources)
        {
            var cam = followCamera != null ? followCamera : Camera.main;
            if (cam != null)
            {
                var ray = cam.ScreenPointToRay(Input.mousePosition);
                if (new Plane(Vector3.up, new Vector3(0f, WaterLevel, 0f)).Raycast(ray, out float hit))
                {
                    var p = ray.GetPoint(hit);
                    _sourceArray[count++] = new Vector4(p.x, p.z, mouseRadius, mouseStrength);
                }
            }
        }

        foreach (var s in s_Sources)
        {
            if (count >= MaxSources)
                break;
            if (s.TryGetSource(WaterLevel, StepTime, out var source))
                _sourceArray[count++] = source;
        }

        // 自定义波源每帧给一次，多个子步都用同一组（按每秒推高量累积）
        s_EmitterBuffer.Clear();
        foreach (var e in s_Emitters)
            e.GetSources(s_EmitterBuffer);
        foreach (var s in s_EmitterBuffer)
        {
            if (count >= MaxSources)
                break;
            _sourceArray[count++] = s;
        }

        for (int i = count; i < MaxSources; i++)
            _sourceArray[i] = Vector4.zero;
        return count;
    }
}
