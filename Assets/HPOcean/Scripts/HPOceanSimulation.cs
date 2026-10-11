using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// FFT 海浪模拟：3 个级联（大浪 / 中浪 / 细碎波纹），每帧在 GPU 上跑一遍 IFFT，
/// 结果以全局纹理的形式交给 HPOcean/Water 着色器使用。
/// </summary>
[ExecuteAlways]
public class HPOceanSimulation : MonoBehaviour
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
    bool _causticHistoryValid;

    static readonly int s_Caustics = Shader.PropertyToID("_HPOceanCaustics");
    static readonly int s_CausticParams = Shader.PropertyToID("_HPOceanCausticParams");
    static readonly int s_CausticDepths = Shader.PropertyToID("_HPOceanCausticDepths");

    readonly Cascade[] _cascades = new Cascade[CascadeCount];
    RenderTexture _pingA, _pingB, _pongA, _pongB;

    int _lastFrame = -1;
    float _lastTime;
    float _simTime;
    int _paramsHash;

    static readonly int s_LengthScales = Shader.PropertyToID("_HPOceanLengthScales");
    static readonly int s_OceanTime = Shader.PropertyToID("_HPOceanTime");

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
        _compute = Resources.Load<ComputeShader>("HPOceanFFT");
        if (_compute == null)
        {
            Debug.LogError("HPOcean: 找不到 Resources/HPOceanFFT.compute");
            enabled = false;
            return;
        }

        _kInit = _compute.FindKernel("InitSpectrum");
        _kUpdate = _compute.FindKernel("UpdateSpectrum");
        _kHorizontal = _compute.FindKernel("IFFTHorizontal");
        _kVertical = _compute.FindKernel("IFFTVertical");
        _kAssemble = _compute.FindKernel("Assemble");

        _pingA = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "HPOcean_PingA");
        _pingB = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "HPOcean_PingB");
        _pongA = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "HPOcean_PongA");
        _pongB = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, "HPOcean_PongB");

        for (int i = 0; i < CascadeCount; i++)
        {
            _cascades[i] = new Cascade
            {
                h0 = CreateRT(GraphicsFormat.R32G32B32A32_SFloat, false, $"HPOcean_H0_{i}"),
                displacement = CreateRT(GraphicsFormat.R16G16B16A16_SFloat, true, $"HPOcean_Displacement_{i}"),
                derivatives = CreateRT(GraphicsFormat.R16G16B16A16_SFloat, true, $"HPOcean_Derivatives_{i}"),
                turbulence = CreateRT(GraphicsFormat.R32_SFloat, true, $"HPOcean_Turbulence_{i}"),
                displacementId = Shader.PropertyToID($"_HPOceanDisplacement{i}"),
                derivativesId = Shader.PropertyToID($"_HPOceanDerivatives{i}"),
                turbulenceId = Shader.PropertyToID($"_HPOceanTurbulence{i}"),
            };
        }

        _causticCompute = Resources.Load<ComputeShader>("HPOceanCaustics");
        if (_causticCompute != null)
        {
            _kCausticClear = _causticCompute.FindKernel("CausticClear");
            _kCausticSplat = _causticCompute.FindKernel("CausticSplat");
            _kCausticResolve = _causticCompute.FindKernel("CausticResolve");
            _causticAccum = CreateArrayRT(GraphicsFormat.R32_UInt, "HPOcean_CausticAccum");
            _causticResult = CreateArrayRT(GraphicsFormat.R32_SFloat, "HPOcean_Caustics");
            _causticHistoryValid = false;
        }

        _paramsHash = 0;
        _lastFrame = -1;
        _lastTime = CurrentTime();
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;

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
            _compute.SetFloat("_LengthScale", c.lengthScale);
            _compute.SetFloat("_CutoffLow", c.cutoffLow);
            _compute.SetFloat("_CutoffHigh", c.cutoffHigh);
            _compute.SetInt("_Seed", i + 1);

            if (rebuildSpectrum)
            {
                _compute.SetTexture(_kInit, "_H0", c.h0);
                _compute.Dispatch(_kInit, Size / 8, Size / 8, 1);
            }

            _compute.SetTexture(_kUpdate, "_H0Read", c.h0);
            _compute.SetTexture(_kUpdate, "_OutA", _pingA);
            _compute.SetTexture(_kUpdate, "_OutB", _pingB);
            _compute.Dispatch(_kUpdate, Size / 8, Size / 8, 1);

            _compute.SetTexture(_kHorizontal, "_InA", _pingA);
            _compute.SetTexture(_kHorizontal, "_InB", _pingB);
            _compute.SetTexture(_kHorizontal, "_OutA", _pongA);
            _compute.SetTexture(_kHorizontal, "_OutB", _pongB);
            _compute.Dispatch(_kHorizontal, 1, Size, 1);

            _compute.SetTexture(_kVertical, "_InA", _pongA);
            _compute.SetTexture(_kVertical, "_InB", _pongB);
            _compute.SetTexture(_kVertical, "_OutA", _pingA);
            _compute.SetTexture(_kVertical, "_OutB", _pingB);
            _compute.Dispatch(_kVertical, 1, Size, 1);

            _compute.SetTexture(_kAssemble, "_InA", _pingA);
            _compute.SetTexture(_kAssemble, "_InB", _pingB);
            _compute.SetTexture(_kAssemble, "_Displacement", c.displacement);
            _compute.SetTexture(_kAssemble, "_Derivatives", c.derivatives);
            _compute.SetTexture(_kAssemble, "_Turbulence", c.turbulence);
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

        SimulateCaustics();
    }

    // 焦散：在中浪级联的平铺域里追踪光子（细碎波纹太短，形成不了清晰的焦散线）
    void SimulateCaustics()
    {
        var light = FindSun();
        if (!caustics || _causticCompute == null || light == null)
        {
            Shader.SetGlobalVector(s_CausticParams, Vector4.zero);
            _causticHistoryValid = false;
            return;
        }

        var c = _cascades[1];
        Vector3 toSun = -light.transform.forward;

        var cs = _causticCompute;
        cs.SetFloat("_CausticLength", c.lengthScale);
        cs.SetVector("_CausticSunDir", toSun);
        cs.SetVector("_CausticDepths", causticDepths);
        cs.SetVector("_CausticJitter", new Vector4(Random.value - 0.5f, Random.value - 0.5f, 0f, 0f));
        cs.SetFloat("_CausticTemporal", _causticHistoryValid ? causticTemporal : 0f);
        cs.SetFloat("_CausticEdgeWeight", causticEdgeWeight);
        cs.SetFloat("_CausticNormalMip", causticNormalMip);

        cs.SetTexture(_kCausticClear, "_CausticAccum", _causticAccum);
        cs.Dispatch(_kCausticClear, CausticRes / 8, CausticRes / 8, 1);

        cs.SetTexture(_kCausticSplat, "_CausticAccum", _causticAccum);
        cs.SetTexture(_kCausticSplat, "_CausticDerivatives", c.derivatives);
        cs.SetTexture(_kCausticSplat, "_CausticDisplacement", c.displacement);
        cs.Dispatch(_kCausticSplat, CausticPhotonRes / 8, CausticPhotonRes / 8, 1);

        cs.SetTexture(_kCausticResolve, "_CausticAccum", _causticAccum);
        cs.SetTexture(_kCausticResolve, "_Caustic", _causticResult);
        cs.Dispatch(_kCausticResolve, CausticRes / 8, CausticRes / 8, 1);
        _causticHistoryValid = true;

        Shader.SetGlobalTexture(s_Caustics, _causticResult);
        Shader.SetGlobalVector(s_CausticParams, new Vector4(c.lengthScale, 1f, causticScale, 0f));
        Shader.SetGlobalVector(s_CausticDepths, causticDepths);
    }

    Light FindSun()
    {
        if (sun != null && sun.isActiveAndEnabled)
            return sun;
        if (RenderSettings.sun != null && RenderSettings.sun.isActiveAndEnabled)
            return RenderSettings.sun;
        foreach (var l in FindObjectsOfType<Light>())
        {
            if (l.type == LightType.Directional && l.isActiveAndEnabled)
                return l;
        }
        return null;
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
        _compute.SetFloat("_WindSpeed", windSpeed);
        _compute.SetFloat("_WindAngle", windDirection * Mathf.Deg2Rad);
        _compute.SetFloat("_Fetch", fetch);
        _compute.SetFloat("_Depth", depth);
        _compute.SetFloat("_PeakEnhancement", peakEnhancement);
        _compute.SetFloat("_Directionality", directionality);
        _compute.SetFloat("_ShortWavesFade", shortWavesFade);
        _compute.SetFloat("_Amplitude", amplitude);
        _compute.SetFloat("_Time", _simTime);
        _compute.SetFloat("_DeltaTime", dt);
        _compute.SetFloat("_Choppiness", choppiness);
        _compute.SetFloat("_FoamThreshold", foamThreshold);
        _compute.SetFloat("_FoamSharpness", foamSharpness);
        _compute.SetFloat("_FoamDecay", foamDecay);
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
