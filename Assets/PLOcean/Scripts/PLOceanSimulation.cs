using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// FFT 海浪模拟：3 个级联（大浪 / 中浪 / 细碎波纹），每帧在 GPU 上跑一遍 IFFT，
/// 结果以全局纹理的形式交给 PLOcean/Sea 着色器使用。
/// </summary>
[ExecuteAlways]
public class PLOceanSimulation : MonoBehaviour
{
    const int Size = 256;
    const int CascadeCount = 3;

    [Header("风浪")]
    [Tooltip("风速，单位 m/s，决定浪的大小")]
    [Range(0.5f, 40f)] public float windSpeed = 10f;
    [Tooltip("风向，单位度")]
    [Range(0f, 360f)] public float windDirection = 30f;
    [Tooltip("风区长度（风吹过的距离），越长浪越成熟")]
    [Min(100f)] public float fetch = 100000f;
    [Tooltip("水深，影响浅水波速")]
    [Min(1f)] public float depth = 500f;
    [Tooltip("JONSWAP 峰值增强因子")]
    [Range(1f, 7f)] public float peakEnhancement = 3.3f;
    [Tooltip("0 = 各向同性的涌浪，1 = 完全顺着风向")]
    [Range(0f, 1f)] public float directionality = 0.9f;
    [Tooltip("抑制极短波，单位 m")]
    [Range(0f, 1f)] public float shortWavesFade = 0.02f;
    [Tooltip("整体振幅缩放")]
    [Range(0f, 3f)] public float amplitude = 1f;

    [Header("形态")]
    [Tooltip("水平位移强度，越大波峰越尖")]
    [Range(0f, 2f)] public float choppiness = 1.1f;
    [Tooltip("时间缩放")]
    [Range(0f, 4f)] public float timeScale = 1f;

    [Header("泡沫")]
    [Tooltip("Jacobian 低于此值开始产生泡沫")]
    [Range(0f, 2f)] public float foamThreshold = 0.6f;
    [Range(0.1f, 20f)] public float foamSharpness = 3f;
    [Tooltip("泡沫消散速度，单位 1/s")]
    [Range(0f, 5f)] public float foamDecay = 0.6f;

    [Header("级联尺寸（米）")]
    public float lengthScale0 = 250f;
    public float lengthScale1 = 17f;
    public float lengthScale2 = 5f;

    [Header("焦散")]
    public bool caustics = true;
    [Tooltip("太阳，留空则自动找场景里的方向光")]
    public Light sun;
    [Tooltip("三张焦散切片对应的水深（米），水底按深度在它们之间插值")]
    public Vector3 causticDepths = new Vector3(1.5f, 4f, 10f);
    [Tooltip("与上一帧焦散的混合比例（时域降噪）")]
    [Range(0f, 0.95f)] public float causticTemporal = 0.5f;
    [Tooltip("À-trous 降噪的边缘保持强度")]
    [Range(0f, 8f)] public float causticEdgeWeight = 2f;
    [Tooltip("追踪光子时法线取的 mip，越大越滤掉短波，焦散纹路越大越稀疏")]
    [Range(0f, 4f)] public float causticNormalMip = 2.5f;
    [Tooltip("焦散图案放大倍数，越大纹路越大越稀疏（不再与水面严格对齐，水下看不出来）")]
    [Range(1f, 4f)] public float causticScale = 2f;

    [Tooltip("编辑模式下也让海浪动起来（需要 Scene 视图开启 Always Refresh）")]
    public bool animateInEditMode = true;

    const int CausticRes = 256;
    const int CausticPhotonRes = 512;
    const int CausticSlices = 3;

    ComputeShader _compute;
    int _kInit, _kUpdate, _kHorizontal, _kVertical, _kAssemble;

    ComputeShader _causticCompute;
    int _kCausticClear, _kCausticSplat, _kCausticResolve;
    RenderTexture _causticAccum, _causticResult;
    int _causticFrame;
    bool _causticHistoryValid;

    static readonly int s_Caustics = Shader.PropertyToID("_PLOceanCaustics");
    static readonly int s_CausticParams = Shader.PropertyToID("_PLOceanCausticParams");
    static readonly int s_CausticDepths = Shader.PropertyToID("_PLOceanCausticDepths");

    readonly Cascade[] _cascades = new Cascade[CascadeCount];
    RenderTexture _pingA, _pingB, _pongA, _pongB;

    int _lastFrame = -1;
    float _lastTime;
    float _simTime;
    int _paramsHash;

    static readonly int s_LengthScales = Shader.PropertyToID("_PLOceanLengthScales");
    static readonly int s_OceanTime = Shader.PropertyToID("_PLOceanTime");
    static readonly int s_Cascade1Fade = Shader.PropertyToID("_Cascade1Fade");
    static readonly int s_Cascade2Fade = Shader.PropertyToID("_Cascade2Fade");

    // CPU 端水面高度（浮力用）：位移纹理每一级 mip 异步回读，按顶点着色器的方式采样
    const int ReadbackMips = 9;   // 256 → 1
    public static PLOceanSimulation Active { get; private set; }
    ushort[][][] _cpuDisp;        // [级联][mip][texel * 4]，half
    bool[][] _cpuValid;
    bool _cpuReady;
    int _pendingReadbacks;
    float _lastQueryTime = float.NegativeInfinity;
    System.Action<AsyncGPUReadbackRequest>[][] _readbackCallbacks;
    int _readbackGeneration;
    SeaMesh _seaMesh;
    int _queryFrame = -1;
    Vector3 _queryCamPos;
    float _queryFade1, _queryFade2;

    // compute 参数的属性 ID（每帧要设几十次，避免反复按字符串查找）
    static class ID
    {
        public static readonly int CausticJitter = Shader.PropertyToID("_CausticJitter");
        public static readonly int Amplitude = Shader.PropertyToID("_Amplitude");
        public static readonly int Caustic = Shader.PropertyToID("_Caustic");
        public static readonly int CausticAccum = Shader.PropertyToID("_CausticAccum");
        public static readonly int CausticDepths = Shader.PropertyToID("_CausticDepths");
        public static readonly int CausticDerivatives = Shader.PropertyToID("_CausticDerivatives");
        public static readonly int CausticDisplacement = Shader.PropertyToID("_CausticDisplacement");
        public static readonly int CausticEdgeWeight = Shader.PropertyToID("_CausticEdgeWeight");
        public static readonly int CausticLength = Shader.PropertyToID("_CausticLength");
        public static readonly int CausticNormalMip = Shader.PropertyToID("_CausticNormalMip");
        public static readonly int CausticSunDir = Shader.PropertyToID("_CausticSunDir");
        public static readonly int CausticTemporal = Shader.PropertyToID("_CausticTemporal");
        public static readonly int Choppiness = Shader.PropertyToID("_Choppiness");
        public static readonly int CutoffHigh = Shader.PropertyToID("_CutoffHigh");
        public static readonly int CutoffLow = Shader.PropertyToID("_CutoffLow");
        public static readonly int DeltaTime = Shader.PropertyToID("_DeltaTime");
        public static readonly int Depth = Shader.PropertyToID("_Depth");
        public static readonly int Derivatives = Shader.PropertyToID("_Derivatives");
        public static readonly int Directionality = Shader.PropertyToID("_Directionality");
        public static readonly int Displacement = Shader.PropertyToID("_Displacement");
        public static readonly int Fetch = Shader.PropertyToID("_Fetch");
        public static readonly int FoamDecay = Shader.PropertyToID("_FoamDecay");
        public static readonly int FoamSharpness = Shader.PropertyToID("_FoamSharpness");
        public static readonly int FoamThreshold = Shader.PropertyToID("_FoamThreshold");
        public static readonly int H0 = Shader.PropertyToID("_H0");
        public static readonly int H0Read = Shader.PropertyToID("_H0Read");
        public static readonly int InA = Shader.PropertyToID("_InA");
        public static readonly int InB = Shader.PropertyToID("_InB");
        public static readonly int LengthScale = Shader.PropertyToID("_LengthScale");
        public static readonly int OutA = Shader.PropertyToID("_OutA");
        public static readonly int OutB = Shader.PropertyToID("_OutB");
        public static readonly int PeakEnhancement = Shader.PropertyToID("_PeakEnhancement");
        public static readonly int Seed = Shader.PropertyToID("_Seed");
        public static readonly int ShortWavesFade = Shader.PropertyToID("_ShortWavesFade");
        public static readonly int Time = Shader.PropertyToID("_Time");
        public static readonly int Turbulence = Shader.PropertyToID("_Turbulence");
        public static readonly int WindAngle = Shader.PropertyToID("_WindAngle");
        public static readonly int WindSpeed = Shader.PropertyToID("_WindSpeed");
    }

    class Cascade
    {
        public RenderTexture h0;
        public RenderTexture displacement;
        public RenderTexture derivatives;
        public RenderTexture turbulence;
        public float lengthScale;
        public float cutoffLow;
        public float cutoffHigh;
        public int displacementId;
        public int derivativesId;
        public int turbulenceId;
    }

    void OnEnable()
    {
        _compute = Resources.Load<ComputeShader>("PLOceanFFT");
        if (_compute == null)
        {
            Debug.LogError("PLOcean: 找不到 Resources/PLOceanFFT.compute");
            enabled = false;
            return;
        }

        _kInit = _compute.FindKernel("InitSpectrum");
        _kUpdate = _compute.FindKernel("UpdateSpectrum");
        _kHorizontal = _compute.FindKernel("IFFTHorizontal");
        _kVertical = _compute.FindKernel("IFFTVertical");
        _kAssemble = _compute.FindKernel("Assemble");

        _pingA = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "PLOcean_PingA");
        _pingB = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "PLOcean_PingB");
        _pongA = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "PLOcean_PongA");
        _pongB = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "PLOcean_PongB");

        for (int i = 0; i < CascadeCount; i++)
        {
            _cascades[i] = new Cascade
            {
                h0 = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, $"PLOcean_H0_{i}"),
                displacement = CreateRT(GraphicsFormat.R16G16B16A16_SFloat, true, $"PLOcean_Displacement_{i}"),
                derivatives = CreateRT(GraphicsFormat.R16G16B16A16_SFloat, true, $"PLOcean_Derivatives_{i}"),
                turbulence = CreateRT(GraphicsFormat.R32_SFloat, true, $"PLOcean_Turbulence_{i}"),
                displacementId = Shader.PropertyToID($"_PLOceanDisplacement{i}"),
                derivativesId = Shader.PropertyToID($"_PLOceanDerivatives{i}"),
                turbulenceId = Shader.PropertyToID($"_PLOceanTurbulence{i}"),
            };
        }

        _causticCompute = Resources.Load<ComputeShader>("PLOceanCaustics");
        if (_causticCompute != null)
        {
            _kCausticClear = _causticCompute.FindKernel("CausticClear");
            _kCausticSplat = _causticCompute.FindKernel("CausticSplat");
            _kCausticResolve = _causticCompute.FindKernel("CausticResolve");
            _causticAccum = CreateArrayRT(GraphicsFormat.R32_UInt, "PLOcean_CausticAccum");
            _causticResult = CreateArrayRT(GraphicsFormat.R32_SFloat, "PLOcean_Caustics");
            _causticHistoryValid = false;
        }

        _paramsHash = 0;
        _lastFrame = -1;
        _lastTime = CurrentTime();
        InitReadback();
        Active = this;
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        if (Active == this)
            Active = null;
        _cpuReady = false;
        _readbackGeneration++;

        Release(ref _pingA);
        Release(ref _pingB);
        Release(ref _pongA);
        Release(ref _pongB);
        Release(ref _causticAccum);
        Release(ref _causticResult);
        Shader.SetGlobalVector(s_CausticParams, Vector4.zero);
        for (int i = 0; i < CascadeCount; i++)
        {
            if (_cascades[i] == null)
                continue;
            Release(ref _cascades[i].h0);
            Release(ref _cascades[i].displacement);
            Release(ref _cascades[i].derivatives);
            Release(ref _cascades[i].turbulence);
            _cascades[i] = null;
        }
    }

    void OnBeginContextRendering(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras)
    {
        // 每帧只模拟一次（多个相机共享结果）
        if (Time.renderedFrameCount == _lastFrame)
            return;
        _lastFrame = Time.renderedFrameCount;
        Simulate();
    }

    static float CurrentTime()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            return (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
        return Time.time;
    }

    void Simulate()
    {
        float now = CurrentTime();
        float dt = Mathf.Clamp(now - _lastTime, 0f, 0.1f);
        _lastTime = now;
        if (Application.isPlaying || animateInEditMode)
            _simTime += dt * timeScale;

        UpdateCascadeRanges();

        int hash = ComputeParamsHash();
        bool rebuildSpectrum = hash != _paramsHash;
        _paramsHash = hash;

        SetCommonParams(dt * timeScale);

        for (int i = 0; i < CascadeCount; i++)
        {
            var c = _cascades[i];
            _compute.SetFloat(ID.LengthScale, c.lengthScale);
            _compute.SetFloat(ID.CutoffLow, c.cutoffLow);
            _compute.SetFloat(ID.CutoffHigh, c.cutoffHigh);
            _compute.SetInt(ID.Seed, i + 1);

            if (rebuildSpectrum)
            {
                _compute.SetTexture(_kInit, ID.H0, c.h0);
                _compute.Dispatch(_kInit, Size / 8, Size / 8, 1);
            }

            _compute.SetTexture(_kUpdate, ID.H0Read, c.h0);
            _compute.SetTexture(_kUpdate, ID.OutA, _pingA);
            _compute.SetTexture(_kUpdate, ID.OutB, _pingB);
            _compute.Dispatch(_kUpdate, Size / 8, Size / 8, 1);

            _compute.SetTexture(_kHorizontal, ID.InA, _pingA);
            _compute.SetTexture(_kHorizontal, ID.InB, _pingB);
            _compute.SetTexture(_kHorizontal, ID.OutA, _pongA);
            _compute.SetTexture(_kHorizontal, ID.OutB, _pongB);
            _compute.Dispatch(_kHorizontal, 1, Size, 1);

            _compute.SetTexture(_kVertical, ID.InA, _pongA);
            _compute.SetTexture(_kVertical, ID.InB, _pongB);
            _compute.SetTexture(_kVertical, ID.OutA, _pingA);
            _compute.SetTexture(_kVertical, ID.OutB, _pingB);
            _compute.Dispatch(_kVertical, 1, Size, 1);

            _compute.SetTexture(_kAssemble, ID.InA, _pingA);
            _compute.SetTexture(_kAssemble, ID.InB, _pingB);
            _compute.SetTexture(_kAssemble, ID.Displacement, c.displacement);
            _compute.SetTexture(_kAssemble, ID.Derivatives, c.derivatives);
            _compute.SetTexture(_kAssemble, ID.Turbulence, c.turbulence);
            _compute.Dispatch(_kAssemble, Size / 8, Size / 8, 1);

            c.displacement.GenerateMips();
            c.derivatives.GenerateMips();
            c.turbulence.GenerateMips();

            Shader.SetGlobalTexture(c.displacementId, c.displacement);
            Shader.SetGlobalTexture(c.derivativesId, c.derivatives);
            Shader.SetGlobalTexture(c.turbulenceId, c.turbulence);
        }

        Shader.SetGlobalVector(s_LengthScales, new Vector4(lengthScale0, lengthScale1, lengthScale2, 0f));
        Shader.SetGlobalFloat(s_OceanTime, _simTime);

        RequestReadback();
        SimulateCaustics();
    }

    // ------------------------------------------------------------------
    // CPU 水面高度查询
    // ------------------------------------------------------------------

    /// <summary>世界 XZ 处的水面高度。没有海面或数据还没回读时返回 false。</summary>
    public static bool TrySampleHeight(float x, float z, out float height)
    {
        var sim = Active;
        if (sim == null)
        {
            height = 0f;
            return false;
        }
        height = sim.SampleHeight(x, z);
        return sim._cpuReady;
    }

    /// <summary>
    /// 世界 XZ 处的水面高度，与顶点着色器保持一致：按到相机的距离选 mip、远处淡出小级联，
    /// 并像 OceanHeightAt 那样反解水平位移。数据比画面晚 1~3 帧。
    /// </summary>
    public float SampleHeight(float x, float z)
    {
        _lastQueryTime = Time.realtimeSinceStartup;
        float level = transform.position.y;
        if (!_cpuReady)
            return level - SeaMesh.PlanetDrop(x, z);

        // 相机位置和级联淡出距离每帧取一次
        if (_queryFrame != Time.frameCount)
        {
            _queryFrame = Time.frameCount;
            var cam = Camera.main;
            _queryCamPos = cam != null ? cam.transform.position
                         : SeaMesh.HasRenderedCamera ? SeaMesh.LastCameraPosition : transform.position;
            _queryFade1 = 600f;
            _queryFade2 = 150f;
            if (_seaMesh != null && _seaMesh.seaMaterial != null)
            {
                _queryFade1 = Mathf.Max(_seaMesh.seaMaterial.GetFloat(s_Cascade1Fade), 1e-3f);
                _queryFade2 = Mathf.Max(_seaMesh.seaMaterial.GetFloat(s_Cascade2Fade), 1e-3f);
            }
        }

        float x0 = x, z0 = z;
        for (int i = 0; i < 3; i++)
        {
            var d = DisplacementAt(x0, z0, level);
            x0 = x - d.x;
            z0 = z - d.z;
        }
        return level + DisplacementAt(x0, z0, level).y - SeaMesh.PlanetDrop(x, z);
    }

    // 纹理坐标 (x, z) 处的位移，对应顶点着色器 Vert 里的三次采样
    Vector3 DisplacementAt(float x, float z, float level)
    {
        float dist = Vector3.Distance(new Vector3(x, level, z), _queryCamPos);
        float lodBase = Mathf.Log(Mathf.Max(dist * 0.0625f, 1e-4f) * 256f, 2f);
        float w1 = Mathf.Clamp01(1f - dist / _queryFade1);
        float w2 = Mathf.Clamp01(1f - dist / _queryFade2);

        Vector3 d = SampleCascade(0, x, z, lodBase);
        if (w1 > 0f)
            d += SampleCascade(1, x, z, lodBase) * w1;
        if (w2 > 0f)
            d += SampleCascade(2, x, z, lodBase) * w2;
        return d;
    }

    Vector3 SampleCascade(int cascade, float x, float z, float lodBase)
    {
        float length = _cascades[cascade].lengthScale;
        float lod = Mathf.Clamp(lodBase - Mathf.Log(length, 2f), 0f, ReadbackMips - 1);
        int m0 = Mathf.FloorToInt(lod);
        int m1 = Mathf.Min(m0 + 1, ReadbackMips - 1);
        float u = x / length, v = z / length;
        var a = Bilinear(_cpuDisp[cascade][m0], Size >> m0, u, v);
        if (m1 == m0)
            return a;
        return Vector3.Lerp(a, Bilinear(_cpuDisp[cascade][m1], Size >> m1, u, v), lod - m0);
    }

    // 与 GPU 的 repeat + bilinear 采样一致
    static Vector3 Bilinear(ushort[] data, int size, float u, float v)
    {
        float fx = u * size - 0.5f, fy = v * size - 0.5f;
        int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
        float tx = fx - ix, ty = fy - iy;
        int mask = size - 1;
        int x0 = ix & mask, x1 = (ix + 1) & mask;
        int y0 = iy & mask, y1 = (iy + 1) & mask;
        var bottom = Vector3.Lerp(Texel(data, size, x0, y0), Texel(data, size, x1, y0), tx);
        var top = Vector3.Lerp(Texel(data, size, x0, y1), Texel(data, size, x1, y1), tx);
        return Vector3.Lerp(bottom, top, ty);
    }

    static Vector3 Texel(ushort[] data, int size, int x, int y)
    {
        int i = (y * size + x) * 4;
        return new Vector3(Mathf.HalfToFloat(data[i]), Mathf.HalfToFloat(data[i + 1]), Mathf.HalfToFloat(data[i + 2]));
    }

    void InitReadback()
    {
        _seaMesh = GetComponent<SeaMesh>();
        _readbackGeneration++;
        _cpuReady = false;
        _pendingReadbacks = 0;
        _cpuDisp = new ushort[CascadeCount][][];
        _cpuValid = new bool[CascadeCount][];
        _readbackCallbacks = new System.Action<AsyncGPUReadbackRequest>[CascadeCount][];
        for (int c = 0; c < CascadeCount; c++)
        {
            _cpuDisp[c] = new ushort[ReadbackMips][];
            _cpuValid[c] = new bool[ReadbackMips];
            _readbackCallbacks[c] = new System.Action<AsyncGPUReadbackRequest>[ReadbackMips];
            for (int m = 0; m < ReadbackMips; m++)
            {
                int size = Size >> m;
                _cpuDisp[c][m] = new ushort[size * size * 4];
                int ci = c, mi = m, gen = _readbackGeneration;
                _readbackCallbacks[c][m] = r => OnReadback(r, ci, mi, gen);
            }
        }
    }

    // 只有最近有人查询过高度才回读，避免没有浮力物体时白白占带宽
    void RequestReadback()
    {
        if (!SystemInfo.supportsAsyncGPUReadback || Time.realtimeSinceStartup - _lastQueryTime > 2f)
            return;
        // 上一批还没回来就跳过，不让请求越积越多
        if (_pendingReadbacks > 0)
            return;
        for (int c = 0; c < CascadeCount; c++)
        {
            for (int m = 0; m < ReadbackMips; m++)
            {
                _pendingReadbacks++;
                AsyncGPUReadback.Request(_cascades[c].displacement, m, _readbackCallbacks[c][m]);
            }
        }
    }

    void OnReadback(AsyncGPUReadbackRequest request, int cascade, int mip, int generation)
    {
        // 组件禁用再启用后，旧批次的结果属于已经释放的纹理，不能计入新的计数，也不能写进新的数组
        if (generation != _readbackGeneration)
            return;
        _pendingReadbacks = Mathf.Max(_pendingReadbacks - 1, 0);
        if (request.hasError || _cpuDisp == null)
            return;
        var data = request.GetData<ushort>();
        var target = _cpuDisp[cascade][mip];
        if (data.Length != target.Length)
            return;
        data.CopyTo(target);
        _cpuValid[cascade][mip] = true;

        if (!_cpuReady)
        {
            bool all = true;
            for (int c = 0; c < CascadeCount && all; c++)
                for (int m = 0; m < ReadbackMips && all; m++)
                    all = _cpuValid[c][m];
            _cpuReady = all;
        }
    }

    // 焦散：在中浪级联的平铺域里追踪光子（细碎波纹太短，形成不了清晰的焦散线）
    void SimulateCaustics()
    {
        var light = caustics && _causticCompute != null ? FindSun() : null;
        if (light == null)
        {
            Shader.SetGlobalVector(s_CausticParams, Vector4.zero);
            _causticHistoryValid = false;
            return;
        }

        var c = _cascades[1];
        Vector3 toSun = -light.transform.forward;

        var cs = _causticCompute;
        cs.SetFloat(ID.CausticLength, c.lengthScale);
        cs.SetVector(ID.CausticSunDir, toSun);
        cs.SetVector(ID.CausticDepths, causticDepths);
        _causticFrame++;
        float jitterX = (_causticFrame * 0.7548777f) % 1f - 0.5f;
        float jitterY = (_causticFrame * 0.5698403f) % 1f - 0.5f;
        cs.SetVector(ID.CausticJitter, new Vector4(jitterX, jitterY, 0f, 0f));
        cs.SetFloat(ID.CausticTemporal, _causticHistoryValid ? causticTemporal : 0f);
        cs.SetFloat(ID.CausticEdgeWeight, causticEdgeWeight);
        cs.SetFloat(ID.CausticNormalMip, causticNormalMip);

        cs.SetTexture(_kCausticClear, ID.CausticAccum, _causticAccum);
        cs.Dispatch(_kCausticClear, CausticRes / 8, CausticRes / 8, 1);

        cs.SetTexture(_kCausticSplat, ID.CausticAccum, _causticAccum);
        cs.SetTexture(_kCausticSplat, ID.CausticDerivatives, c.derivatives);
        cs.SetTexture(_kCausticSplat, ID.CausticDisplacement, c.displacement);
        cs.Dispatch(_kCausticSplat, CausticPhotonRes / 8, CausticPhotonRes / 8, 1);

        cs.SetTexture(_kCausticResolve, ID.CausticAccum, _causticAccum);
        cs.SetTexture(_kCausticResolve, ID.Caustic, _causticResult);
        cs.Dispatch(_kCausticResolve, CausticRes / 8, CausticRes / 8, 1);
        _causticHistoryValid = true;

        Shader.SetGlobalTexture(s_Caustics, _causticResult);
        Shader.SetGlobalVector(s_CausticParams, new Vector4(c.lengthScale, 1f, causticScale, 0f));
        Shader.SetGlobalVector(s_CausticDepths, causticDepths);
    }

    Light _autoSun;
    float _nextSunSearch;

    Light FindSun()
    {
        if (sun != null && sun.isActiveAndEnabled)
            return sun;
        if (RenderSettings.sun != null && RenderSettings.sun.isActiveAndEnabled)
            return RenderSettings.sun;
        // 遍历场景所有灯很贵（大场景里几千个物体），找到的结果缓存起来，失效后每秒最多重找一次
        if (_autoSun != null && _autoSun.isActiveAndEnabled && _autoSun.type == LightType.Directional)
            return _autoSun;
        float now = Time.realtimeSinceStartup;
        if (now < _nextSunSearch)
            return null;
        _nextSunSearch = now + 1f;
        _autoSun = null;
        foreach (var l in FindObjectsOfType<Light>())
        {
            if (l.type == LightType.Directional && l.isActiveAndEnabled)
            {
                _autoSun = l;
                break;
            }
        }
        return _autoSun;
    }

    static RenderTexture CreateArrayRT(GraphicsFormat format, string name)
    {
        var desc = new RenderTextureDescriptor(CausticRes, CausticRes, format, 0)
        {
            dimension = TextureDimension.Tex2DArray,
            volumeDepth = CausticSlices,
            enableRandomWrite = true,
            useMipMap = false,
            msaaSamples = 1,
        };
        var rt = new RenderTexture(desc)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = format == GraphicsFormat.R32_UInt ? FilterMode.Point : FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,
        };
        rt.Create();
        return rt;
    }

    // 三个级联按波数分段，互不重叠，避免同一波长被叠加多次
    void UpdateCascadeRanges()
    {
        float boundary1 = 2f * Mathf.PI / lengthScale1 * 6f;
        float boundary2 = 2f * Mathf.PI / lengthScale2 * 6f;

        Setup(_cascades[0], lengthScale0, 0.0001f, boundary1);
        Setup(_cascades[1], lengthScale1, boundary1, boundary2);
        Setup(_cascades[2], lengthScale2, boundary2, 9999f);

        static void Setup(Cascade c, float length, float low, float high)
        {
            c.lengthScale = Mathf.Max(length, 0.1f);
            c.cutoffLow = low;
            c.cutoffHigh = high;
        }
    }

    void SetCommonParams(float dt)
    {
        _compute.SetFloat(ID.WindSpeed, windSpeed);
        _compute.SetFloat(ID.WindAngle, windDirection * Mathf.Deg2Rad);
        _compute.SetFloat(ID.Fetch, fetch);
        _compute.SetFloat(ID.Depth, depth);
        _compute.SetFloat(ID.PeakEnhancement, peakEnhancement);
        _compute.SetFloat(ID.Directionality, directionality);
        _compute.SetFloat(ID.ShortWavesFade, shortWavesFade);
        _compute.SetFloat(ID.Amplitude, amplitude);
        _compute.SetFloat(ID.Time, _simTime);
        _compute.SetFloat(ID.DeltaTime, dt);
        _compute.SetFloat(ID.Choppiness, choppiness);
        _compute.SetFloat(ID.FoamThreshold, foamThreshold);
        _compute.SetFloat(ID.FoamSharpness, foamSharpness);
        _compute.SetFloat(ID.FoamDecay, foamDecay);
    }

    int ComputeParamsHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + windSpeed.GetHashCode();
            h = h * 31 + windDirection.GetHashCode();
            h = h * 31 + fetch.GetHashCode();
            h = h * 31 + depth.GetHashCode();
            h = h * 31 + peakEnhancement.GetHashCode();
            h = h * 31 + directionality.GetHashCode();
            h = h * 31 + shortWavesFade.GetHashCode();
            h = h * 31 + amplitude.GetHashCode();
            h = h * 31 + lengthScale0.GetHashCode();
            h = h * 31 + lengthScale1.GetHashCode();
            h = h * 31 + lengthScale2.GetHashCode();
            return h == 0 ? 1 : h;
        }
    }

    static RenderTexture CreateRT(GraphicsFormat format, bool mips, string name)
    {
        var rt = new RenderTexture(Size, Size, 0, format, mips ? 0 : 1)
        {
            name = name,
            enableRandomWrite = true,
            useMipMap = mips,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = mips ? FilterMode.Trilinear : FilterMode.Point,
            anisoLevel = mips ? 8 : 0,
            hideFlags = HideFlags.DontSave,
        };
        rt.Create();

        // 新建的 RT 内容未定义，泡沫是逐帧累积的，必须先清零
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
        return rt;
    }

    static void Release(ref RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        if (Application.isPlaying)
            Destroy(rt);
        else
            DestroyImmediate(rt);
        rt = null;
    }
}
