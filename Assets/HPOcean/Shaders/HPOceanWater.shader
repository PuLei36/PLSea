// HPOcean 水面着色器（HDRP 14，前向透明，独立 Shader 不改 HDRP 包）
//
// 光照模型参考 HPWater 的水体 BSDF：
//   diffR  宏观体积散射：入射菲涅尔 × NdotL × 水下单次散射（沿折射光线指数步进，逐点采样太阳阴影 → 水下光柱）
//   diffT  薄层散射 + 背光透射：浪尖薄处的次表面散射、逆光时穿透浪尖的辉光
//   出射   (1 - F(NdotV)) 统一作用于水体散射光和透射的水下场景
//   表面   GGX 高光（方向光 + 点光/聚光）+ 反射（SSR → 反射探针 → 天空，按层级权重回退）
//   折射   只取法线带来的扰动；可选指数步进光线追踪（IGN 抖动 + 厚度判定），按水下光程做 mip 模糊和色散
//   焦散   HPOceanSimulation 里光子追踪得到的焦散图，按水底深度采样
//   泡沫   FFT Jacobian 累积的湍流 + 物体边缘泡沫
//
// Pass：
//   TransparentDepthPrepass  写深度、法线（给透明 SSR）、模板（SSR 位 + 水面位）
//   ForwardOnly              水面着色（正面从水上看，背面从水下看）
//   HPOceanVolume            低分辨率体积散射 + 时域重投影（由 HPOceanVolumePass 调用）
//   HPOceanUnderwater        水下视角的雾、光柱、焦散（由 HPOceanUnderwaterPass 调用）
Shader "HPOcean/Water"
{
    Properties
    {
        [Header(Volume Scattering)]
        _Absorption ("Absorption Coefficient (1/m)", Vector) = (0.45, 0.065, 0.04, 0)
        _Scattering ("Scattering Coefficient (1/m)", Vector) = (0.006, 0.018, 0.024, 0)
        _ScatterIntensity ("Scatter Intensity", Range(0, 8)) = 1
        _PhaseG ("Phase G", Range(0, 0.99)) = 0.8
        _MultiScatter ("Multi Scatter (Isotropic Blend)", Range(0, 1)) = 0.6
        _AmbientScatter ("Ambient Scatter", Range(0, 4)) = 1
        _MaxCrossDistance ("Max Cross Distance (m)", Float) = 100

        [Header(Surface)]
        _Smoothness ("Smoothness", Range(0, 1)) = 0.96
        _DistanceRoughness ("Distance Roughness (per m)", Range(0, 0.002)) = 0.0003
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1
        _SpecularIntensity ("Sun Specular", Range(0, 4)) = 1
        _ReflectionIntensity ("Reflection", Range(0, 2)) = 1
        _PunctualIntensity ("Point / Spot Lights", Range(0, 4)) = 1

        [Header(Refraction)]
        _RefractionStrength ("Refraction Strength", Range(0, 2)) = 1
        _ForwardScatterBlur ("Forward Scatter Blur", Range(0, 2)) = 0.3
        _Dispersion ("Chromatic Dispersion", Range(0, 2)) = 0.4
        [Toggle(_HPO_RAYMARCH_REFRACTION)] _RefractionRayMarch ("Ray Marched Refraction", Float) = 1
        [IntRange] _RefractionSteps ("Ray March Steps", Range(2, 24)) = 8
        _RefractionMaxDistance ("Ray March Max Distance (m)", Float) = 30
        _RefractionThickness ("Ray March Thickness (m)", Float) = 0.5

        [Header(Caustics)]
        _CausticIntensity ("Caustic Intensity", Range(0, 4)) = 1
        _CausticDispersion ("Caustic Dispersion", Range(0, 2)) = 0.5
        _CausticFadeDistance ("Caustic Fade Distance (m)", Float) = 120
        _CausticShallowDepth ("Caustic Shallow Fade Depth (m)", Float) = 3

        [Header(Thin Layer)]
        _CrestHeight ("Crest Height (m)", Float) = 1.5
        _SSSIntensity ("Thin Layer SSS", Range(0, 4)) = 1
        _BacklitIntensity ("Backlit Transmission", Range(0, 4)) = 0.5
        _BacklitG ("Backlit Phase G", Range(0, 0.99)) = 0.85

        [Header(Scatter Ramp)]
        [NoScaleOffset] _ScatterRamp ("Scatter Ramp (Shallow to Deep)", 2D) = "white" {}
        _ScatterRampBlend ("Ramp Blend", Range(0, 1)) = 1
        _ScatterRampDepth ("Ramp Depth Range (m)", Float) = 15

        [Header(Foam)]
        [NoScaleOffset] _FoamMap ("Foam Map (R Thick, G Medium, B Light)", 2D) = "black" {}
        _FoamColor ("Foam Albedo", Color) = (0.85, 0.9, 0.92, 1)
        _FoamIntensity ("Crest Foam Intensity", Range(0, 4)) = 1
        _FoamTiling ("Crest Foam Tiling (1/m)", Float) = 0.05
        _FoamScrollSpeed ("Foam Scroll Speed", Float) = 0.02

        [Header(Edge Foam)]
        _EdgeFoamDistance ("Edge Foam Distance (m)", Float) = 1.5
        _EdgeFoamIntensity ("Edge Foam Intensity", Range(0, 3)) = 1
        _EdgeFoamTiling ("Edge Foam Tiling (1/m)", Float) = 0.35
        _EdgeFoamContact ("Edge Contact Line", Range(0, 1)) = 0.5

        [Header(Underwater)]
        _UnderwaterFogDensity ("Underwater Fog Density Scale", Range(0, 4)) = 1
        _UnderwaterGodRays ("Underwater Light Shafts", Range(0, 4)) = 1

        [Header(Interactive Ripples)]
        _RippleHeightScale ("Ripple Height Scale", Range(0, 4)) = 1
        _RippleNormalScale ("Ripple Normal Scale", Range(0, 4)) = 1
        _RippleFoam ("Ripple Foam", Range(0, 4)) = 0.5

        [Header(LOD)]
        _Cascade1Fade ("Cascade 1 Vertex Fade (m)", Float) = 600
        _Cascade2Fade ("Cascade 2 Vertex Fade (m)", Float) = 150
    }

    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
    #pragma multi_compile_fragment SHADOW_LOW SHADOW_MEDIUM SHADOW_HIGH

    #define _SURFACE_TYPE_TRANSPARENT
    #define AREA_SHADOW_MEDIUM

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

    #define SHADERPASS SHADERPASS_FORWARD

    CBUFFER_START(UnityPerMaterial)
        float4 _Absorption;
        float4 _Scattering;
        float4 _FoamColor;
        float _ScatterIntensity;
        float _PhaseG;
        float _MultiScatter;
        float _AmbientScatter;
        float _MaxCrossDistance;
        float _Smoothness;
        float _DistanceRoughness;
        float _NormalStrength;
        float _SpecularIntensity;
        float _ReflectionIntensity;
        float _PunctualIntensity;
        float _RefractionStrength;
        float _ForwardScatterBlur;
        float _Dispersion;
        float _RefractionRayMarch;
        float _RefractionSteps;
        float _RefractionMaxDistance;
        float _RefractionThickness;
        float _CausticIntensity;
        float _CausticDispersion;
        float _CausticFadeDistance;
        float _CausticShallowDepth;
        float _CrestHeight;
        float _SSSIntensity;
        float _BacklitIntensity;
        float _BacklitG;
        float _ScatterRampBlend;
        float _ScatterRampDepth;
        float _FoamIntensity;
        float _FoamTiling;
        float _FoamScrollSpeed;
        float _EdgeFoamDistance;
        float _EdgeFoamIntensity;
        float _EdgeFoamTiling;
        float _EdgeFoamContact;
        float _UnderwaterFogDensity;
        float _UnderwaterGodRays;
        float _Cascade1Fade;
        float _Cascade2Fade;
        float _RippleHeightScale;
        float _RippleNormalScale;
        float _RippleFoam;
    CBUFFER_END

    // Material.hlsl 的 ApplyBlendMode 会引用它，这里水面不走 HDRP 的混合模式
    static const float _BlendMode = 0;

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Lighting.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoopDef.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightEvaluation.hlsl"

    // FFT 模拟结果（HPOceanSimulation 以全局纹理形式设置）
    TEXTURE2D(_HPOceanDisplacement0);
    TEXTURE2D(_HPOceanDisplacement1);
    TEXTURE2D(_HPOceanDisplacement2);
    TEXTURE2D(_HPOceanDerivatives0);
    TEXTURE2D(_HPOceanDerivatives1);
    TEXTURE2D(_HPOceanDerivatives2);
    TEXTURE2D(_HPOceanTurbulence0);
    TEXTURE2D(_HPOceanTurbulence1);
    TEXTURE2D(_HPOceanTurbulence2);
    SAMPLER(sampler_HPOceanDerivatives0);
    float4 _HPOceanLengthScales;
    float _HPOceanTime;
    float _HPOceanWaterLevel;

    // 焦散（HPOceanSimulation）：x = 平铺尺寸，y = 是否可用，z = 图案放大倍数；深度切片 xyz
    TEXTURE2D_ARRAY(_HPOceanCaustics);
    float4 _HPOceanCausticParams;
    float4 _HPOceanCausticDepths;

    // 低分辨率体积散射（HPOceanVolumePass）
    TEXTURE2D_X(_HPOceanVolumeTex);
    TEXTURE2D_X(_HPOceanVolumeHistory);
    float4 _HPOceanVolumeSize;    // 低分辨率宽高及倒数
    float4 _HPOceanVolumeParams;  // x = 历史有效，y = 历史权重
    float _HPOceanVolumeOwner;    // 当前体积缓冲属于哪个相机
    float _HPOceanCameraId;       // 本次绘制的相机（WaterMesh 用 MaterialPropertyBlock 设置）
    // 全屏 Pass 读取的相机缓冲（Custom Pass 绑定）
    TEXTURE2D_X(_HPOceanDepthBuffer);
    TEXTURE2D_X_UINT2(_HPOceanStencil);
    TEXTURE2D_X(_HPOceanSceneColorCopy);

    // 交互水波（HPOceanRipple）：xy = 区域左下角世界 XZ，z = 边长，w = 像素尺寸；z = 0 表示关闭
    TEXTURE2D(_HPOceanRipple);
    float4 _HPOceanRippleParams;

    TEXTURE2D(_FoamMap);
    TEXTURE2D(_ScatterRamp);

    #define HPO_SAMPLE_COUNT 6
    #define HPO_EXP_FACTOR 12.0
    #define HPO_ETA (1.0 / 1.33)
    #define HPO_F0 0.02
    #define HPO_SSS_PATH_SCALE 20.0
    #define HPO_SSS_NONLINEAR_STRENGTH 0.5
    #define HPO_SSS_SCATTER_BOOST 2.0
    #define HPO_BACKLIT_PATH_SCALE 20.0
    #define HPO_WATER_BLUR_SCALE 0.2
    #define HPO_BLUR_DENSITY_SCALE 10.0
    #define HPO_REFRACTION_REFERENCE_DISTANCE 20.0
    #define HPO_STENCIL_WATER 64   // STENCILUSAGE_USER_BIT0：标记水面正面像素

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    // 归一化 Henyey-Greenstein 相位函数（1/sr）
    float HenyeyGreenstein(float cosTheta, float g)
    {
        float g2 = g * g;
        float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
        return (1.0 - g2) / (4.0 * PI * denom * sqrt(denom));
    }

    // 单次散射主导时保留方向性，多次散射时趋于各向同性
    float WaterPhase(float cosTheta)
    {
        return lerp(HenyeyGreenstein(cosTheta, _PhaseG), 1.0 / (4.0 * PI), _MultiScatter);
    }

    void GetMedium(out float3 sigmaS, out float3 sigmaT)
    {
        sigmaS = max(_Scattering.xyz, 0.0);
        sigmaT = max(max(_Absorption.xyz, 0.0) + sigmaS, 1e-5);
    }

    // ∫[t0, t1] exp(-k t) dt，k 可能接近 0 或为负（水下仰视）
    float3 SegmentIntegral(float3 k, float t0, float t1)
    {
        float3 safeK = abs(k) < 1e-5 ? 1e-5 : k;
        return (exp(-safeK * t0) - exp(-safeK * t1)) / safeK;
    }

    // 水下光程 → 颜色金字塔 mip（散射越强，越浅就越模糊）
    float WaterBlurMip(float pathLength, float scatterDensity)
    {
        float scale = HPO_WATER_BLUR_SCALE + scatterDensity * _ForwardScatterBlur * HPO_BLUR_DENSITY_SCALE;
        return clamp(log2(1.0 + pathLength * scale), 0.0, min(6.0, _ColorPyramidLodCount));
    }

    // 泡沫贴图三层：R 浓、G 中、B 淡
    // 泡沫量少时只露出稀疏的淡层，越多越过渡到中层、浓层
    float FoamFromLayers(float3 layers, float amount)
    {
        float a = saturate(amount);
        float pattern = a < 0.5 ? lerp(layers.b, layers.g, a * 2.0) : lerp(layers.g, layers.r, a * 2.0 - 1.0);
        return saturate(pattern * saturate(amount * 1.5));
    }

    float3 SampleFoamMap(float2 uv, float2 scroll)
    {
        // 两个尺度错开采样，打散贴图的重复感
        float3 a = SAMPLE_TEXTURE2D(_FoamMap, s_trilinear_repeat_sampler, uv + scroll).rgb;
        float3 b = SAMPLE_TEXTURE2D(_FoamMap, s_trilinear_repeat_sampler, uv * 0.37 + float2(0.31, 0.17) - scroll * 0.5).rgb;
        return lerp(a, b, 0.3);
    }

    // 色带决定散射光的色相（u = 0 浅，1 深），亮度仍由物理模型给出
    float3 ApplyScatterRamp(float3 light, float u)
    {
        float3 ramp = SAMPLE_TEXTURE2D_LOD(_ScatterRamp, s_linear_clamp_sampler, float2(saturate(u), 0.5), 0).rgb;
        float3 tinted = ramp * (Luminance(light) / max(Luminance(ramp), 1e-4));
        return lerp(light, tinted, _ScatterRampBlend);
    }

    // ------------------------------------------------------------------
    // 交互水波
    // ------------------------------------------------------------------

    struct Ripple
    {
        float height;
        float2 gradient;  // dh/dx, dh/dz
    };

    // xzAbs 必须是位移之后的真实世界 XZ（FFT 带水平位移，用纹理坐标采样会让波纹整体错位）
    float RippleFade(float2 uv)
    {
        if (any(uv < 0.0) || any(uv > 1.0))
            return 0.0;
        // 区域边缘淡出（模拟里海绵层已经把波吸收掉，这里只防止硬边）
        float2 edge = saturate(min(uv, 1.0 - uv) * 20.0);
        return edge.x * edge.y;
    }

    // 顶点位移用：按网格单元边长选 mip，把比网格还细的波纹滤掉，否则粗网格上只剩孤立的尖三角
    float SampleRippleHeight(float2 xzAbs, float cellSize)
    {
        if (_HPOceanRippleParams.z <= 0)
            return 0.0;
        float2 uv = (xzAbs - _HPOceanRippleParams.xy) / _HPOceanRippleParams.z;
        float fade = RippleFade(uv);
        if (fade <= 0.0)
            return 0.0;
        float lod = max(log2(cellSize / _HPOceanRippleParams.w), 0.0);
        return SAMPLE_TEXTURE2D_LOD(_HPOceanRipple, s_trilinear_clamp_sampler, uv, lod).r * fade * _RippleHeightScale;
    }

    Ripple SampleRipple(float2 xzAbs)
    {
        Ripple r;
        r.height = 0;
        r.gradient = 0;
        if (_HPOceanRippleParams.z <= 0)
            return r;

        float2 uv = (xzAbs - _HPOceanRippleParams.xy) / _HPOceanRippleParams.z;
        float fade = RippleFade(uv);
        if (fade <= 0.0)
            return r;

        float texelUV = _HPOceanRippleParams.w / _HPOceanRippleParams.z;
        float h  = SAMPLE_TEXTURE2D_LOD(_HPOceanRipple, s_linear_clamp_sampler, uv, 0).r;
        float hL = SAMPLE_TEXTURE2D_LOD(_HPOceanRipple, s_linear_clamp_sampler, uv - float2(texelUV, 0), 0).r;
        float hR = SAMPLE_TEXTURE2D_LOD(_HPOceanRipple, s_linear_clamp_sampler, uv + float2(texelUV, 0), 0).r;
        float hD = SAMPLE_TEXTURE2D_LOD(_HPOceanRipple, s_linear_clamp_sampler, uv - float2(0, texelUV), 0).r;
        float hU = SAMPLE_TEXTURE2D_LOD(_HPOceanRipple, s_linear_clamp_sampler, uv + float2(0, texelUV), 0).r;

        r.height = h * fade * _RippleHeightScale;
        r.gradient = float2(hR - hL, hU - hD) / (2.0 * _HPOceanRippleParams.w) * fade * _RippleNormalScale;
        return r;
    }

    // ------------------------------------------------------------------
    // 海面采样
    // ------------------------------------------------------------------

    struct OceanSurface
    {
        float3 normalWS;
        float turbulence;
    };

    // xz = 纹理坐标（未位移），rippleXZ = 位移后的真实世界 XZ
    OceanSurface SampleOceanSurface(float2 xz, float2 rippleXZ, float dist)
    {
        float3 Ls = _HPOceanLengthScales.xyz;
        float cascade2Weight = saturate(1.0 - dist / (_Cascade2Fade * 4.0));
        float4 deriv = SAMPLE_TEXTURE2D(_HPOceanDerivatives0, sampler_HPOceanDerivatives0, xz / Ls.x)
                     + SAMPLE_TEXTURE2D(_HPOceanDerivatives1, sampler_HPOceanDerivatives0, xz / Ls.y)
                     + SAMPLE_TEXTURE2D(_HPOceanDerivatives2, sampler_HPOceanDerivatives0, xz / Ls.z) * cascade2Weight;
        float2 slope = deriv.xy / max(1.0 + deriv.zw, 0.1) * _NormalStrength;

        // 交互水波叠加到斜率上
        Ripple ripple = SampleRipple(rippleXZ);
        slope += ripple.gradient;

        OceanSurface s;
        s.normalWS = normalize(float3(-slope.x, 1.0, -slope.y));
        s.turbulence = SAMPLE_TEXTURE2D(_HPOceanTurbulence0, sampler_HPOceanDerivatives0, xz / Ls.x).r
                     + SAMPLE_TEXTURE2D(_HPOceanTurbulence1, sampler_HPOceanDerivatives0, xz / Ls.y).r
                     + SAMPLE_TEXTURE2D(_HPOceanTurbulence2, sampler_HPOceanDerivatives0, xz / Ls.z).r * cascade2Weight * 0.5;
        // 陡的波纹（被物体搅起来的水）带一点泡沫
        s.turbulence += saturate((length(ripple.gradient) - 0.5) * 0.6) * _RippleFoam;
        return s;
    }

    // 掠射角下避免法线背对相机
    float3 FaceCamera(float3 N, float3 V)
    {
        float NdotV = dot(N, V);
        return NdotV < 0.02 ? normalize(N + V * (0.02 - NdotV)) : N;
    }

    float SurfacePerceptualRoughness(float dist)
    {
        return saturate(1.0 - _Smoothness + dist * _DistanceRoughness);
    }

    // 世界 XZ 处的水面高度（只算竖直位移，水下视角判断水线用）
    // 必须和顶点着色器里的位移保持一致，否则判断水线时会系统性地偏低导致提前进入水下效果
    // 纹理坐标 xz 处的三维位移（与顶点着色器在相机附近 mip0、级联权重 1 时一致）
    float3 OceanDisplacementAt(float2 xz)
    {
        float3 Ls = _HPOceanLengthScales.xyz;
        float3 d = SAMPLE_TEXTURE2D_LOD(_HPOceanDisplacement0, s_linear_repeat_sampler, xz / Ls.x, 0).xyz
                 + SAMPLE_TEXTURE2D_LOD(_HPOceanDisplacement1, s_linear_repeat_sampler, xz / Ls.y, 0).xyz
                 + SAMPLE_TEXTURE2D_LOD(_HPOceanDisplacement2, s_linear_repeat_sampler, xz / Ls.z, 0).xyz;
        d.y += SampleRipple(xz + d.xz).height;
        return d;
    }

    // 世界 XZ 处的真实水面高度。FFT 带水平位移：纹理坐标 x0 的顶点被推到 x0 + disp.xz，
    // 所以要先反解 x0 = xzAbs - disp(x0).xz（不动点迭代），再取 disp(x0).y。
    // 只读 disp(xzAbs).y 会在浪峰两侧系统性地偏移，水线出现锯齿和错位。
    float OceanHeightAt(float2 xzAbs)
    {
        float2 x0 = xzAbs;
        [unroll]
        for (int i = 0; i < 3; i++)
            x0 = xzAbs - OceanDisplacementAt(x0).xz;
        return _HPOceanWaterLevel + OceanDisplacementAt(x0).y;
    }

    // ------------------------------------------------------------------
    // 太阳：大气/高度雾衰减（与 HDRP EvaluateLight_Directional 一致）+ 阴影
    // ------------------------------------------------------------------

    float3 EvaluateSunColor(DirectionalLightData light, float3 positionRWS)
    {
        float3 color = light.color;
        float3 L = -light.forward;

        float cosZenith = max(L.y, 0.001);
        float3 oDepth = OpticalDepthHeightFog(_HeightFogBaseExtinction, _HeightFogBaseHeight,
                                              _HeightFogExponents, cosZenith, positionRWS.y);
        color *= TransmittanceFromOpticalDepth(oDepth);

    #if !SHADEROPTIONS_PRECOMPUTED_ATMOSPHERIC_ATTENUATION
        if (asint(light.distanceFromCamera) >= 0)
        {
            float3 X = GetAbsolutePositionWS(positionRWS);
            float3 C = _PlanetCenterPosition.xyz;
            float r = distance(X, C);
            float cosHoriz = ComputeCosineOfHorizonAngle(r);
            float cosTheta = dot(X - C, L) * rcp(r);

            if (cosTheta >= cosHoriz)
            {
                float3 od = ComputeAtmosphericOpticalDepth(r, cosTheta, true);
                float3 opacity = 1.0 - TransmittanceFromOpticalDepth(od);
                color *= 1.0 - (Desaturate(opacity, _AlphaSaturation) * _AlphaMultiplier);
            }
            else
            {
                color = 0;
            }
        }
    #endif
        return color;
    }

    void GetSun(float3 positionRWS, out DirectionalLightData light, out float3 sunColor)
    {
        ZERO_INITIALIZE(DirectionalLightData, light);
        light.shadowIndex = -1;
        light.forward = float3(0, -1, 0);
        sunColor = 0;

        if (_DirectionalLightCount > 0)
        {
            uint lightIndex = _DirectionalShadowIndex >= 0 ? (uint)_DirectionalShadowIndex : 0u;
            light = _DirectionalLightDatas[lightIndex];
            sunColor = EvaluateSunColor(light, positionRWS);
        }
    }

    // 水下体积采样用的廉价阴影：单级联 Gather PCF（参考 HPWater 的 EvalWater_CascadedDepth）
    float SampleSunShadowCheap(DirectionalLightData light, float3 positionRWS)
    {
        if (light.shadowIndex < 0 || light.shadowDimmer <= 0)
            return 1.0;

        HDShadowContext sc = InitShadowContext();
        float alpha;
        int cascadeCount;
        int split = EvalShadow_GetSplitIndex(sc, light.shadowIndex, positionRWS, alpha, cascadeCount);
        if (split < 0)
            return 1.0;

        HDShadowData sd = sc.shadowDatas[light.shadowIndex];
        LoadDirectionalShadowDatas(sd, sc, light.shadowIndex + split);
        float3 p = positionRWS + sd.cacheTranslationDelta.xyz;
        float3 tc = EvalShadow_GetTexcoordsAtlas(sd, _CascadeShadowAtlasSize.zw, p, false);
        float shadow = SampleShadow_Gather_PCF(_CascadeShadowAtlasSize.zwxy, tc, _ShadowmapCascadeAtlas,
                                               s_linear_clamp_compare_sampler, FIXED_UNIFORM_BIAS);
        shadow = split < cascadeCount - 1 ? shadow : lerp(shadow, 1.0, alpha);
        return lerp(1.0, shadow, light.shadowDimmer);
    }

    float SampleSunShadowSurface(DirectionalLightData light, float2 positionSS, float3 positionRWS, float3 normalWS)
    {
        if (light.shadowIndex < 0 || light.shadowDimmer <= 0)
            return 1.0;

        HDShadowContext sc = InitShadowContext();
        float shadow = GetDirectionalShadowAttenuation(sc, positionSS, positionRWS, normalWS, light.shadowIndex, -light.forward);
        return lerp(1.0, shadow, light.shadowDimmer);
    }

    // ------------------------------------------------------------------
    // 焦散
    // ------------------------------------------------------------------

    float3 SampleCaustics(float3 positionAbs, float depth, float3 L)
    {
        // 光子是按"平面折射"对齐生成的，水底点先沿平面折射方向找回对应的水面位置
        float3 t0 = refract(-L, float3(0, 1, 0), HPO_ETA);
        float2 uv = (positionAbs.xz - t0.xz / max(-t0.y, 0.1) * depth) / (_HPOceanCausticParams.x * max(_HPOceanCausticParams.z, 1.0));

        float3 d = _HPOceanCausticDepths.xyz;
        float s = depth <= d.y ? (depth - d.x) / (d.y - d.x) : 1.0 + (depth - d.y) / (d.z - d.y);
        s = clamp(s, 0.0, 2.0);
        float s0 = min(floor(s), 1.0);
        float f = s - s0;

        // 单通道焦散图上模拟色散：RGB 沿光线水平方向错开采样（HPWater 的单通道模式）
        float2 dispersion = normalize(L.xz + 1e-5) * (_CausticDispersion * 0.004 * depth);
        float3 c0, c1;
        c0.r = SAMPLE_TEXTURE2D_ARRAY_LOD(_HPOceanCaustics, s_linear_repeat_sampler, uv + dispersion, s0, 0).r;
        c0.g = SAMPLE_TEXTURE2D_ARRAY_LOD(_HPOceanCaustics, s_linear_repeat_sampler, uv, s0, 0).r;
        c0.b = SAMPLE_TEXTURE2D_ARRAY_LOD(_HPOceanCaustics, s_linear_repeat_sampler, uv - dispersion, s0, 0).r;
        c1.r = SAMPLE_TEXTURE2D_ARRAY_LOD(_HPOceanCaustics, s_linear_repeat_sampler, uv + dispersion, s0 + 1, 0).r;
        c1.g = SAMPLE_TEXTURE2D_ARRAY_LOD(_HPOceanCaustics, s_linear_repeat_sampler, uv, s0 + 1, 0).r;
        c1.b = SAMPLE_TEXTURE2D_ARRAY_LOD(_HPOceanCaustics, s_linear_repeat_sampler, uv - dispersion, s0 + 1, 0).r;
        float3 c = lerp(c0, c1, f);
        // 压缩对比度：均值 1 保持不变，亮线不再刺眼、暗格不再死黑，网格感弱很多
        c = log2(1.0 + max(c, 0.0));

        // 贴近水面时还没聚焦
        return lerp(1.0, c, saturate(depth / max(d.x, 1e-3)));
    }

    // 返回乘到水底颜色上的系数（平均为 1，只作用于被太阳照到的部分）
    float3 CausticModulation(float3 positionRWS, float depth, float3 L, float shadow, float viewDist)
    {
        if (_HPOceanCausticParams.y <= 0 || depth <= 0)
            return 1.0;

        float3 c = SampleCaustics(GetAbsolutePositionWS(positionRWS), depth, L);
        float fade = saturate(1.0 - viewDist / max(_CausticFadeDistance, 1e-3)) * saturate(1.0 - depth / 40.0) * saturate(L.y * 4.0);
        // 越浅越弱：近岸只剩淡淡的光斑，到 _CausticShallowDepth 才完全显现，避免浅滩透出棋盘格
        float shallow = saturate(depth / max(_CausticShallowDepth, 1e-3));
        fade *= shallow * shallow * (3.0 - 2.0 * shallow);
        return max(1.0 + (c - 1.0) * _CausticIntensity * shadow * fade, 0.0);
    }

    // ------------------------------------------------------------------
    // 折射（参考 HPWater 的 FragRefraction）
    // ------------------------------------------------------------------

    struct RefractionResult
    {
        float2 sampleUV;
        float2 uvOffset;
        float3 bedRWS;      // 折射后看到的水下场景点
        bool bedValid;
        float3 sceneRWS;    // 不折射时屏幕上的场景点（边缘泡沫用）
        bool sceneValid;
    };

    RefractionResult ComputeRefraction(float3 positionRWS, float3 N, float3 V, float2 screenUV, float dither)
    {
        RefractionResult r;

        float sceneDepthRaw = SampleCameraDepth(screenUV);
        r.sceneValid = sceneDepthRaw != UNITY_RAW_FAR_CLIP_VALUE;
        r.sceneRWS = ComputeWorldSpacePosition(screenUV, sceneDepthRaw, UNITY_MATRIX_I_VP);

        // 只保留法线带来的扰动：去掉平面折射，再加回指向场景点的方向
        float3 flatRefract = refract(-V, float3(0, 1, 0), HPO_ETA);
        float3 crossDir = r.sceneValid ? SafeNormalize(r.sceneRWS - positionRWS) : SafeNormalize(flatRefract);
        float crossLength = r.sceneValid ? min(distance(r.sceneRWS, positionRWS), _MaxCrossDistance) : 10.0;

        float3 gainedNormal = SafeNormalize(N * float3(_RefractionStrength, 1.0, _RefractionStrength));
        float3 refractDir = SafeNormalize(refract(-V, gainedNormal, HPO_ETA) - flatRefract + crossDir);
        float2 refractUV = ComputeNormalizedDeviceCoordinates(positionRWS + refractDir * crossLength, UNITY_MATRIX_VP);

    #if defined(_HPO_RAYMARCH_REFRACTION)
        // 指数步进找折射光线与场景的交点：近处密、远处疏，IGN 抖动起点消除断层，厚度阈值防止穿透
        {
            float maxDistance = max(_RefractionMaxDistance, 0.1);
            int steps = clamp((int)_RefractionSteps, 2, 24);
            float expFactor = clamp(pow(maxDistance / HPO_REFRACTION_REFERENCE_DISTANCE, 2.0), 1.01, 32.0);
            float rcpCount = rcp((float)steps);
            float kDenom = rcp(expFactor - 1.0);
            float expStep = pow(expFactor, rcpCount);
            float currentExp = pow(expFactor, dither * rcpCount);

            [loop]
            for (int i = 0; i < steps; i++)
            {
                float d = (currentExp - 1.0) * kDenom;
                currentExp *= expStep;

                float3 p = positionRWS + refractDir * (d * maxDistance);
                float3 ndc = ComputeNormalizedDeviceCoordinatesWithZ(p, UNITY_MATRIX_VP);
                if (any(ndc.xy < 0.0) || any(ndc.xy > 1.0))
                    break;

                float sceneLinear = LinearEyeDepth(SampleCameraDepth(ndc.xy), _ZBufferParams);
                float rayLinear = LinearEyeDepth(ndc.z, _ZBufferParams);
                if (rayLinear > sceneLinear && rayLinear < sceneLinear + _RefractionThickness)
                {
                    refractUV = ndc.xy;
                    break;
                }
            }
        }
    #endif

        // 屏幕边缘的折射逐渐收回，避免采到屏幕外
        float edge = saturate(screenUV.x * 6.0) * saturate((1.0 - screenUV.x) * 6.0)
                   * saturate(screenUV.y * 6.0) * saturate((1.0 - screenUV.y) * 6.0);
        r.uvOffset = clamp((refractUV - screenUV) * sqrt(edge), -0.25, 0.25);
        r.sampleUV = saturate(screenUV + r.uvOffset);

        float refrDepthRaw = SampleCameraDepth(r.sampleUV);
        r.bedRWS = ComputeWorldSpacePosition(r.sampleUV, refrDepthRaw, UNITY_MATRIX_I_VP);
        r.bedValid = refrDepthRaw != UNITY_RAW_FAR_CLIP_VALUE;

        // 折射到了水面以上的物体：退回到不折射
        if (r.bedValid && r.bedRWS.y > positionRWS.y)
        {
            r.uvOffset = 0;
            r.sampleUV = screenUV;
            r.bedRWS = r.sceneRWS;
            r.bedValid = r.sceneValid;
        }
        return r;
    }

    // ------------------------------------------------------------------
    // 水下光程与体积散射
    // ------------------------------------------------------------------

    struct WaterPath
    {
        float3 rayDir;
        float bedDepth;
        float pathLength;
        float marchLength;
        float viewDepthRate; // 沿视线每走 1m 下潜多少
    };

    WaterPath ComputeWaterPath(float3 positionRWS, float3 N, float3 V, RefractionResult refr)
    {
        WaterPath p;
        p.rayDir = refract(-V, N, HPO_ETA);
        float depthRate = max(-p.rayDir.y, 0.02);
        p.bedDepth = refr.bedValid ? max(positionRWS.y - refr.bedRWS.y, 0.0) : 1e5;
        p.pathLength = refr.bedValid ? min(p.bedDepth / depthRate, distance(refr.bedRWS, positionRWS) * 1.33) : 1e5;
        p.marchLength = min(p.pathLength, _MaxCrossDistance);
        p.viewDepthRate = refr.bedValid ? saturate(p.bedDepth / max(p.pathLength, 1e-3)) : depthRate;
        return p;
    }

    float SunRefractedCos(float3 L, out float3 sunRefract)
    {
        sunRefract = refract(-L, float3(0, 1, 0), HPO_ETA);
        return max(-sunRefract.y, 0.1);
    }

    // 宏观体积散射：分段解析积分 + 每段采样一次太阳阴影（指数步进 + 抖动）
    // 段 [t0, t1] 上：∫ σs · p · E · exp(-σt (t + z(t)/μ)) dt，z(t) = t · viewDepthRate
    float3 ComputeSunVolume(WaterPath path, float3 positionRWS, DirectionalLightData light, float3 sunColor, float dither)
    {
        float3 sigmaS, sigmaT;
        GetMedium(sigmaS, sigmaT);

        float3 sunRefract;
        float muSun = SunRefractedCos(-light.forward, sunRefract);
        float phase = WaterPhase(dot(path.rayDir, -sunRefract));
        float3 k = sigmaT * (1.0 + path.viewDepthRate / muSun);

        float3 S = 0;
        float expStep = pow(HPO_EXP_FACTOR, 1.0 / HPO_SAMPLE_COUNT);
        float kDenom = 1.0 / (HPO_EXP_FACTOR - 1.0);
        float currentExp = 1.0;

        [unroll]
        for (int i = 0; i < HPO_SAMPLE_COUNT; i++)
        {
            float nextExp = currentExp * expStep;
            float t0 = (currentExp - 1.0) * kDenom * path.marchLength;
            float t1 = (nextExp - 1.0) * kDenom * path.marchLength;
            float ts = lerp(t0, t1, dither);

            float shadow = SampleSunShadowCheap(light, positionRWS + path.rayDir * ts);
            S += shadow * SegmentIntegral(k, t0, t1);
            currentExp = nextExp;
        }
        return S * sunColor * sigmaS * phase * _ScatterIntensity;
    }

    // 低分辨率体积缓冲的深度感知上采样（联合双边）
    bool SampleVolumeBuffer(float2 screenUV, float linearDepth, out float3 result)
    {
        result = 0;
        if (_HPOceanCameraId <= 0 || _HPOceanVolumeOwner != _HPOceanCameraId)
            return false;

        float2 p = screenUV * _HPOceanVolumeSize.xy - 0.5;
        int2 base = int2(floor(p));
        float2 f = p - base;
        int2 maxCoord = int2(_HPOceanVolumeSize.xy) - 1;

        float3 acc = 0;
        float weightSum = 0;
        [unroll]
        for (int i = 0; i < 4; i++)
        {
            int2 o = int2(i & 1, i >> 1);
            float4 t = LOAD_TEXTURE2D_X(_HPOceanVolumeTex, clamp(base + o, 0, maxCoord));
            float wb = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
            float wd = t.a > 0 ? exp(-abs(t.a - linearDepth) / (0.03 * linearDepth + 0.05)) : 0.0;
            acc += t.rgb * wb * wd;
            weightSum += wb * wd;
        }

        if (weightSum < 1e-3)
            return false;
        result = acc / weightSum * GetInverseCurrentExposureMultiplier();
        return true;
    }

    // ------------------------------------------------------------------
    // 反射：SSR → 反射探针 → 天空
    // ------------------------------------------------------------------

    LightLoopContext InitWaterLightLoopContext(float3 positionRWS)
    {
        // 含 StructuredBuffer 成员，不能用 ZERO_INITIALIZE
        LightLoopContext context;
        context.contactShadow = 0;
        context.contactShadowFade = 0;
        context.shadowContext = InitShadowContext();
        context.shadowValue = 1;
        context.sampleReflection = SINGLE_PASS_CONTEXT_SAMPLE_REFLECTION_PROBES;
        context.splineVisibility = -1;
        context.positionWS = positionRWS;
        return context;
    }

    float3 EvaluateReflectionProbes(LightLoopContext context, PositionInputs posInput, float3 N, float3 R,
                                    float perceptualRoughness, inout float hierarchyWeight)
    {
        float3 acc = 0;
    #if defined(USE_FPTL_LIGHTLIST) || defined(USE_CLUSTERED_LIGHTLIST)
        uint envStart, envCount;
        GetCountAndStart(posInput, LIGHTCATEGORY_ENV, envStart, envCount);
        context.sampleReflection = SINGLE_PASS_CONTEXT_SAMPLE_REFLECTION_PROBES;

        [loop]
        for (uint i = 0; i < min(envCount, 16u); i++)
        {
            if (hierarchyWeight >= 1.0)
                break;

            EnvLightData envLight = FetchEnvLight(envStart, i);
            float3 probeR = R;
            float weight = 1.0;
            float intersectionDistance = EvaluateLight_EnvIntersection(posInput.positionWS, N, envLight, envLight.influenceShapeType, probeR, weight);
            float4 sample = SampleEnvWithDistanceBaseRoughness(context, posInput, envLight, probeR, perceptualRoughness, intersectionDistance);
            weight *= sample.a;
            UpdateLightingHierarchyWeights(hierarchyWeight, weight);
            acc += sample.rgb * weight * envLight.multiplier;
        }
    #endif
        return acc;
    }

    // 点光源 / 聚光灯：表面高光 + 折射进水里的单次散射
    float3 EvaluatePunctualLights(LightLoopContext context, PositionInputs posInput, float3 N, float3 V,
                                  float roughness, float NdotV, WaterPath path, out float3 scatter)
    {
        float3 specular = 0;
        scatter = 0;
    #if defined(USE_FPTL_LIGHTLIST) || defined(USE_CLUSTERED_LIGHTLIST)
        float3 sigmaS, sigmaT;
        GetMedium(sigmaS, sigmaT);

        BuiltinData builtinData;
        ZERO_INITIALIZE(BuiltinData, builtinData);

        uint lightStart, lightCount;
        GetCountAndStart(posInput, LIGHTCATEGORY_PUNCTUAL, lightStart, lightCount);

        [loop]
        for (uint i = 0; i < min(lightCount, 32u); i++)
        {
            LightData light = FetchLight(lightStart, i);
            float3 L;
            float4 distances;
            GetPunctualLightVectors(posInput.positionWS, light, L, distances);

            float4 color = EvaluateLight_Punctual(context, posInput, light, L, distances);
            color.rgb *= color.a;
            if (Max3(color.r, color.g, color.b) <= 0.0)
                continue;

            SHADOW_TYPE shadow = EvaluateShadow_Punctual(context, posInput, light, builtinData, N, L, distances);
            color.rgb *= ComputeShadowColor(shadow, light.shadowTint, light.penumbraTint);

            float NdotL = dot(N, L);
            float3 H = SafeNormalize(L + V);
            float DV = DV_SmithJointGGX(saturate(dot(N, H)), abs(NdotL), NdotV, roughness);
            specular += color.rgb * F_Schlick(HPO_F0, saturate(dot(L, H))) * DV * saturate(NdotL) * light.specularDimmer;

            // 光源在水面以上才折射进水体
            if (L.y > 0.0)
            {
                float3 lightRefract = refract(-L, float3(0, 1, 0), HPO_ETA);
                float mu = max(-lightRefract.y, 0.1);
                float3 k = sigmaT * (1.0 + path.viewDepthRate / mu);
                float entry = saturate(NdotL) * (1.0 - F_Schlick(HPO_F0, saturate(NdotL)));
                scatter += color.rgb * light.diffuseDimmer * entry * sigmaS * WaterPhase(dot(path.rayDir, -lightRefract))
                         * SegmentIntegral(k, 0.0, path.marchLength);
            }
        }
    #endif
        return specular * _PunctualIntensity;
    }

    // ------------------------------------------------------------------
    // 水下看水面（背面）：斯涅尔窗内透出天空，窗外全反射水体自身的颜色
    // ------------------------------------------------------------------

    float3 UnderwaterInscatterColor(float3 sunColor, float3 L)
    {
        float3 sigmaS, sigmaT;
        GetMedium(sigmaS, sigmaT);
        float3 sunRefract;
        float muSun = SunRefractedCos(L, sunRefract);
        float3 ambientDown = SampleSH9(_AmbientProbeData, float3(0, 1, 0));
        return sigmaS / sigmaT * (sunColor * muSun * (1.0 / (4.0 * PI)) * _ScatterIntensity + ambientDown * 0.5 * _AmbientScatter);
    }

    float3 ShadeUnderside(float3 N, float3 V, float3 sunColor, float3 L)
    {
        float3 n = -N;  // 朝向水下相机
        float3 transmitted = refract(-V, n, 1.33);
        float3 underColor = UnderwaterInscatterColor(sunColor, L);

        if (dot(transmitted, transmitted) < 1e-4)
            return underColor;  // 全反射

        float F = F_Schlick(HPO_F0, saturate(dot(transmitted, N)));
        float3 sky = SampleSkyTexture(transmitted, 0, 0).rgb;
        return sky * (1.0 - F) + underColor * F;
    }

    // ------------------------------------------------------------------
    // 顶点
    // ------------------------------------------------------------------

    struct Attributes
    {
        float3 positionOS : POSITION;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 positionRWS : TEXCOORD0;
        float4 oceanData : TEXCOORD1; // xy = 未位移的世界 XZ（纹理坐标），z = 位移高度
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;

        float3 positionRWS = TransformObjectToWorld(input.positionOS);
        float2 xz = GetAbsolutePositionWS(positionRWS).xz;
        float dist = length(positionRWS);

        // 网格单元边长约为 dist / 16，按它选 mip，避免远处顶点采样走样
        float lodBase = log2(max(dist * 0.0625, 1e-4) * 256.0);
        float3 L = _HPOceanLengthScales.xyz;

        float3 disp = SAMPLE_TEXTURE2D_LOD(_HPOceanDisplacement0, s_trilinear_repeat_sampler, xz / L.x, max(lodBase - log2(L.x), 0.0)).xyz;
        disp += SAMPLE_TEXTURE2D_LOD(_HPOceanDisplacement1, s_trilinear_repeat_sampler, xz / L.y, max(lodBase - log2(L.y), 0.0)).xyz
              * saturate(1.0 - dist / _Cascade1Fade);
        disp += SAMPLE_TEXTURE2D_LOD(_HPOceanDisplacement2, s_trilinear_repeat_sampler, xz / L.z, max(lodBase - log2(L.z), 0.0)).xyz
              * saturate(1.0 - dist / _Cascade2Fade);

        // 波纹在世界空间里静止，要按 FFT 水平位移之后的位置采样
        disp.y += SampleRippleHeight(xz + disp.xz, max(dist * 0.0625, _HPOceanRippleParams.w));
        positionRWS += disp;

        output.positionCS = TransformWorldToHClip(positionRWS);
        output.positionRWS = positionRWS;
        output.oceanData = float4(xz, disp.y, 0.0);
        return output;
    }

    // ------------------------------------------------------------------
    // Pass：透明深度预渲染（深度 + 法线 + 模板）
    // ------------------------------------------------------------------

    void FragDepth(Varyings input, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC, out float4 outNormalBuffer : SV_Target0)
    {
        float3 V = GetWorldSpaceNormalizeViewDir(input.positionRWS);
        float dist = length(input.positionRWS);
        OceanSurface surf = SampleOceanSurface(input.oceanData.xy, GetAbsolutePositionWS(input.positionRWS).xz, dist);
        float3 N = IS_FRONT_VFACE(isFrontFace, true, false) ? FaceCamera(surf.normalWS, V) : -surf.normalWS;

        NormalData normalData;
        normalData.normalWS = N;
        normalData.perceptualRoughness = SurfacePerceptualRoughness(dist);
        EncodeIntoNormalBuffer(normalData, outNormalBuffer);
    }

    // ------------------------------------------------------------------
    // Pass：水面着色
    // ------------------------------------------------------------------

    float4 Frag(Varyings input, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC) : SV_Target
    {
        float3 positionRWS = input.positionRWS;
        uint2 tileIndex = uint2(input.positionCS.xy) / GetTileSize();
        PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, input.positionCS.z, input.positionCS.w, positionRWS, tileIndex);
        float3 V = GetWorldSpaceNormalizeViewDir(positionRWS);
        float dist = length(positionRWS);
        float2 xz = input.oceanData.xy;
        float dither = InterleavedGradientNoise(posInput.positionSS, _TaaFrameInfo.z);
        float exposure = GetCurrentExposureMultiplier();

        OceanSurface surf = SampleOceanSurface(xz, GetAbsolutePositionWS(positionRWS).xz, dist);

        DirectionalLightData light;
        float3 sunColor;
        GetSun(positionRWS, light, sunColor);
        float3 L = -light.forward;

        // 水下看水面
        if (!IS_FRONT_VFACE(isFrontFace, true, false))
            return float4(ShadeUnderside(surf.normalWS, V, sunColor, L) * exposure, 1.0);

        float3 N = FaceCamera(surf.normalWS, V);
        float NdotV = ClampNdotV(dot(N, V));
        float sunShadow = SampleSunShadowSurface(light, posInput.positionSS, positionRWS, N);
        float NdotL = dot(N, L);
        float clampedNdotL = saturate(NdotL);

        float3 sigmaS, sigmaT;
        GetMedium(sigmaS, sigmaT);
        float scatterDensity = Luminance(sigmaS);

        // ---------------- 折射与水下光程 ----------------
        RefractionResult refr = ComputeRefraction(positionRWS, N, V, posInput.positionNDC, dither);
        WaterPath path = ComputeWaterPath(positionRWS, N, V, refr);

        float3 sunRefract;
        float muSun = SunRefractedCos(L, sunRefract);

        // ---------------- Part 1：宏观体积散射 ----------------
        // 优先用低分辨率 + 时域降噪的结果，不可用时（反射探针相机、屏幕边缘）当场计算
        float3 S_volume;
        if (!SampleVolumeBuffer(posInput.positionNDC, posInput.linearDepth, S_volume))
            S_volume = ComputeSunVolume(path, positionRWS, light, sunColor, dither);

        float3 T_entry = 1.0 - F_Schlick(HPO_F0, clampedNdotL);
        float3 macroScattering = S_volume * clampedNdotL * T_entry;

        // 天光的体积散射（各向同性，从上半球入射）
        float3 ambientDown = SampleSH9(_AmbientProbeData, float3(0, 1, 0));
        float3 kAmb = sigmaT * (1.0 + path.viewDepthRate / 0.7);
        float3 ambientScattering = ambientDown * sigmaS * 0.5 * SegmentIntegral(kAmb, 0.0, path.marchLength) * _AmbientScatter;

        // ---------------- Part 2：薄层 SSS ----------------
        float crest = saturate(input.oceanData.z / max(_CrestHeight, 1e-3));
        float thickness = max(1.0 - crest, 0.001);

        float sigmaTScalar = Luminance(sigmaT);
        float L_linear = thickness * HPO_SSS_PATH_SCALE;
        float L_nonlinear = thickness * thickness * HPO_SSS_PATH_SCALE * (1.0 + scatterDensity);
        float nonlinearWeight = saturate(sigmaTScalar * thickness * HPO_SSS_PATH_SCALE * HPO_SSS_NONLINEAR_STRENGTH);
        float sssPath = lerp(L_linear, L_nonlinear, nonlinearWeight);

        float3 sssTransmittance = exp(-sigmaT * sssPath);
        float3 S_sss = sunColor * (1.0 - sssTransmittance) * (sigmaS / sigmaT) * WaterPhase(dot(-V, L)) * _ScatterIntensity;

        float G_sss = 1.0 - clampedNdotL;
        float3 thinLayerSSS = S_sss * G_sss * sunShadow * HPO_SSS_SCATTER_BOOST;
        float sssWeight = saturate(1.0 - Luminance(sssTransmittance));
        thinLayerSSS = lerp(S_volume * G_sss, thinLayerSSS, sssWeight) * _SSSIntensity;

        // ---------------- Part 3：背光透射 ----------------
        float G_backlit = saturate(-NdotL);
        float3 T_backlit = exp(-sigmaT * thickness * HPO_BACKLIT_PATH_SCALE);
        float3 backlit = sunColor * G_backlit * T_backlit * HenyeyGreenstein(dot(V, -L), _BacklitG) * sunShadow * _BacklitIntensity;

        // ---------------- 点光源 / 聚光灯 ----------------
        float perceptualRoughness = SurfacePerceptualRoughness(dist);
        float roughness = max(PerceptualRoughnessToRoughness(perceptualRoughness), 0.002);
        LightLoopContext context = InitWaterLightLoopContext(positionRWS);
        float3 punctualScatter;
        float3 punctualSpecular = EvaluatePunctualLights(context, posInput, N, V, roughness, NdotV, path, punctualScatter);

        // 色带：体积散射按水深取色，浪尖薄层取浅水端
        float rampU = refr.bedValid ? path.bedDepth / max(_ScatterRampDepth, 1e-3) : 1.0;
        float3 waterBody = ApplyScatterRamp(macroScattering + ambientScattering + punctualScatter, rampU)
                         + ApplyScatterRamp(thinLayerSSS + backlit, 0.3 * thickness);

        // ---------------- 水下场景（模糊 + 色散 + 焦散）----------------
        float3 T_scene = refr.bedValid ? exp(-sigmaT * (path.pathLength + path.bedDepth / muSun)) : 0.0;
        float3 sceneColor = 0;
        if (refr.bedValid)
        {
            float mip = WaterBlurMip(path.marchLength, scatterDensity);
            float2 dispersionUV = clamp(refr.uvOffset * _Dispersion, -0.01, 0.01) * dither;
            float3 cLong  = SampleCameraColor(refr.sampleUV - dispersionUV, mip);
            float3 cMid   = SampleCameraColor(refr.sampleUV, mip);
            float3 cShort = SampleCameraColor(refr.sampleUV + dispersionUV, mip);
            sceneColor = float3(dot(float3(0.90, 0.05, 0.05), float3(cLong.r, cMid.r, cShort.r)),
                                dot(float3(0.05, 0.90, 0.05), float3(cLong.g, cMid.g, cShort.g)),
                                dot(float3(0.05, 0.05, 0.90), float3(cLong.b, cMid.b, cShort.b)));

            float bedShadow = SampleSunShadowCheap(light, refr.bedRWS);
            // 只在折射到了真正的水底时才叠加焦散（bedDepth < 0.2m 说明折射射线还在水面附近，跳过）
            if (path.bedDepth > 0.2)
                sceneColor *= CausticModulation(refr.bedRWS, path.bedDepth, L, bedShadow, dist);
        }

        // ---------------- 表面反射 ----------------
        float3 H = SafeNormalize(L + V);
        float NdotH = saturate(dot(N, H));
        float LdotH = saturate(dot(L, H));
        float DV = DV_SmithJointGGX(NdotH, abs(NdotL), NdotV, roughness);
        float3 sunSpecular = sunColor * sunShadow * F_Schlick(HPO_F0, LdotH) * DV * clampedNdotL
                           * saturate(clampedNdotL * 5.0) * _SpecularIntensity * light.specularDimmer;

        float3 R = reflect(-V, N);
        // 反射方向朝下（浪背面）时 SSR 会追到水下的物体，这部分不用 SSR
        float ssrValid = smoothstep(0.02, 0.12, R.y);
        R.y = abs(R.y) + 0.05;
        R = normalize(R);

        // 反射层级：SSR → 反射探针 → 天空，前一层没覆盖到的权重留给后一层
        float4 ssr = LOAD_TEXTURE2D_X(_SsrLightingTexture, posInput.positionSS);
        InversePreExposeSsrLighting(ssr);
        ApplyScreenSpaceReflectionWeight(ssr);
        float3 envLighting = ssr.rgb * ssrValid;
        float hierarchyWeight = saturate(ssr.a) * ssrValid;

        envLighting += EvaluateReflectionProbes(context, posInput, N, R, perceptualRoughness, hierarchyWeight);

        if (hierarchyWeight < 1.0 && _EnableSkyReflection != 0)
        {
            float3 sky = SampleSkyTexture(R, PerceptualRoughnessToMipmapLevel(perceptualRoughness), 0).rgb;
            if (_FogEnabled)
            {
                float4 skyFog = EvaluateFogForSkyReflections(positionRWS, R);
                sky = sky * skyFog.a + skyFog.rgb;
            }
            envLighting += sky * (1.0 - hierarchyWeight);
        }

        float fresnelEnv = HPO_F0 + (max(1.0 - perceptualRoughness, HPO_F0) - HPO_F0) * pow(1.0 - NdotV, 5.0);
        float3 reflection = envLighting * fresnelEnv * _ReflectionIntensity;

        // ---------------- 合成 ----------------
        float exitTransmission = 1.0 - F_Schlick(HPO_F0, NdotV);

        // 颜色金字塔已预曝光，其余光照需要乘曝光
        float3 color = (waterBody * exitTransmission + sunSpecular + punctualSpecular + reflection) * exposure
                     + sceneColor * T_scene * exitTransmission;

        // ---------------- 泡沫 ----------------
        float2 scroll = _HPOceanTime * _FoamScrollSpeed * float2(1.0, 0.73);

        // 波峰泡沫：FFT Jacobian 累积的湍流
        float3 crestLayers = SampleFoamMap(xz * _FoamTiling, scroll);
        float foam = FoamFromLayers(crestLayers, surf.turbulence * _FoamIntensity);

        // 边缘泡沫：按垂直水深，水越浅泡沫越多，随时间往复的一圈圈泡沫带模拟浪拍到物体上又退回
        float edgeDepth = refr.sceneValid ? max(positionRWS.y - refr.sceneRWS.y, 0.0) : 1e5;
        float shore = saturate(1.0 - edgeDepth / max(_EdgeFoamDistance, 1e-3));
        float surge = 0.75 + 0.25 * sin(edgeDepth / max(_EdgeFoamDistance, 1e-3) * 9.0 - _HPOceanTime * 1.7);
        float3 edgeLayers = SampleFoamMap(xz * _EdgeFoamTiling, scroll * float2(-0.6, 1.3));
        float edgeFoam = FoamFromLayers(edgeLayers, shore * shore * surge * _EdgeFoamIntensity);

        // 紧贴物体的接触线也用浓泡沫层打散，不做成实心白边
        float contact = saturate(1.0 - edgeDepth / max(_EdgeFoamDistance * 0.1, 1e-3));
        float contactFoam = contact * smoothstep(0.25, 0.75, edgeLayers.r) * _EdgeFoamContact;
        foam = saturate(max(max(foam, edgeFoam), contactFoam));

        float3 ambientN = SampleSH9(_AmbientProbeData, N);
        float3 foamLit = _FoamColor.rgb * (sunColor * sunShadow * clampedNdotL * INV_PI + ambientN) * exposure;
        color = lerp(color, foamLit, foam);

        // ---------------- 雾 ----------------
        float3 fogColor, fogOpacity;
        EvaluateAtmosphericScattering(posInput, V, fogColor, fogOpacity);
        color = color * (1.0 - fogOpacity) + fogColor;

        return float4(color, 1.0);
    }

    // ------------------------------------------------------------------
    // 全屏 Pass 公共
    // ------------------------------------------------------------------

    struct FullScreenAttributes
    {
        uint vertexID : SV_VertexID;
    };

    struct FullScreenVaryings
    {
        float4 positionCS : SV_POSITION;
    };

    FullScreenVaryings VertFullScreen(FullScreenAttributes input)
    {
        FullScreenVaryings output;
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        return output;
    }

    // ------------------------------------------------------------------
    // Pass：低分辨率体积散射 + 时域重投影
    // 参考 HPWater 的解耦架构：低分辨率累积 → 运动矢量重投影 + 深度拒绝 → 全分辨率双边上采样（在 Frag 里）
    // ------------------------------------------------------------------

    float4 FragVolume(FullScreenVaryings input) : SV_Target
    {
        uint2 lowSS = uint2(input.positionCS.xy);
        float2 uv = (lowSS + 0.5) * _HPOceanVolumeSize.zw;
        uint2 fullSS = min(uint2(uv * _ScreenSize.xy), uint2(_ScreenSize.xy) - 1);

        uint stencil = GetStencilValue(LOAD_TEXTURE2D_X(_HPOceanStencil, fullSS));
        if ((stencil & HPO_STENCIL_WATER) == 0)
            return 0;

        float deviceDepth = LOAD_TEXTURE2D_X(_HPOceanDepthBuffer, fullSS).r;
        float3 positionRWS = ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
        float linearDepth = LinearEyeDepth(positionRWS, UNITY_MATRIX_V);
        float3 V = GetWorldSpaceNormalizeViewDir(positionRWS);

        NormalData normalData;
        DecodeFromNormalBuffer(fullSS, normalData);
        float3 N = normalData.normalWS;

        DirectionalLightData light;
        float3 sunColor;
        GetSun(positionRWS, light, sunColor);

        float dither = InterleavedGradientNoise(lowSS, _TaaFrameInfo.z);
        RefractionResult refr = ComputeRefraction(positionRWS, N, V, uv, dither);
        WaterPath path = ComputeWaterPath(positionRWS, N, V, refr);

        // 预曝光存储，避免太阳照度超出 half 精度
        float3 current = ComputeSunVolume(path, positionRWS, light, sunColor, dither) * GetCurrentExposureMultiplier();

        if (_HPOceanVolumeParams.x > 0)
        {
            float2 prevUV = ComputeNormalizedDeviceCoordinates(positionRWS, UNITY_MATRIX_PREV_VP);
            if (all(prevUV > 0.0) && all(prevUV < 1.0))
            {
                float4 history = SAMPLE_TEXTURE2D_X_LOD(_HPOceanVolumeHistory, s_linear_clamp_sampler, prevUV * _RTHandleScale.xy, 0);

                // 深度拒绝：历史像素与当前深度差太多（遮挡变化、边缘）就丢弃
                float depthWeight = history.a > 0 ? saturate(1.0 - abs(history.a - linearDepth) / (0.05 * linearDepth + 0.1)) : 0.0;
                // 亮度夹取：抑制阴影边界移动时的拖影
                history.rgb = clamp(history.rgb, current * 0.25, current * 4.0 + 1e-4);
                current = lerp(current, history.rgb, _HPOceanVolumeParams.y * depthWeight);
            }
        }

        return float4(current, linearDepth);
    }

    // ------------------------------------------------------------------
    // Pass：水下视角（雾、光柱、焦散）
    // ------------------------------------------------------------------

    float4 FragUnderwater(FullScreenVaryings input) : SV_Target
    {
        uint2 positionSS = uint2(input.positionCS.xy);
        float2 uv = (positionSS + 0.5) * _ScreenSize.zw;
        float3 color = LOAD_TEXTURE2D_X(_HPOceanSceneColorCopy, positionSS).rgb;

        // 吃水线：近裁剪面上每个像素对应一个世界点，和该点 XZ 处的真实波浪高度比较。
        // 近裁剪面只覆盖相机周围几十厘米，这里的水面就是镜头"泡"进去的那一截，
        // 水线随波起伏、随相机倾斜自然成形。
        float3 nearRWS = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
        float3 nearAbs = GetAbsolutePositionWS(nearRWS);
        float surfaceHeight = OceanHeightAt(nearAbs.xz);
        float submerged = surfaceHeight - nearAbs.y;  // > 0 在水下

        // 用屏幕导数把高度差换成像素距离，做 1 像素宽的抗锯齿边
        float pixelDist = submerged / max(length(float2(ddx(submerged), ddy(submerged))), 1e-6);
        float underwaterWeight = saturate(pixelDist + 0.5);

        float deviceDepth = LOAD_TEXTURE2D_X(_HPOceanDepthBuffer, positionSS).r;
        bool isSky = deviceDepth == UNITY_RAW_FAR_CLIP_VALUE;
        float3 positionRWS = ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
        uint stencil = GetStencilValue(LOAD_TEXTURE2D_X(_HPOceanStencil, positionSS));

        // 近平面在水上、但视线在近裁剪距离内就钻进水里：那一截水面网格被裁掉了，
        // 像素直接看到了水底。命中的不是水面、且命中点在水下，同样按水下处理。
        // 只在近裁剪面本身贴着水面时才成立；否则岸边水面网格没盖住、但解析浪高略高于沙面的沙子
        // 会被误判成水下，染上一块块水雾色（相机高于水面 maxWaveHeight 后整个 Pass 跳过，所以远看消失）
        if (!isSky && (stencil & HPO_STENCIL_WATER) == 0 && submerged > -2.0 * _ProjectionParams.y)
        {
            float3 hitAbs = GetAbsolutePositionWS(positionRWS);
            if (hitAbs.y < OceanHeightAt(hitAbs.xz))
                underwaterWeight = 1.0;
        }

        if (underwaterWeight <= 0.0)
            return float4(color, 1.0);
        float3 aboveColor = color;
        float3 viewDir = normalize(positionRWS - nearRWS);
        float dist = isSky ? 500.0 : min(length(positionRWS - nearRWS), 500.0);

        float3 sigmaS, sigmaT;
        GetMedium(sigmaS, sigmaT);
        sigmaS *= _UnderwaterFogDensity;
        sigmaT *= _UnderwaterFogDensity;

        DirectionalLightData light;
        float3 sunColor;
        GetSun(nearRWS, light, sunColor);
        float3 L = -light.forward;
        float3 sunRefract;
        float muSun = SunRefractedCos(L, sunRefract);
        float exposure = GetCurrentExposureMultiplier();

        // 直接看到的水底：焦散（只作用于不透明物体，不作用于水面背面）
        float pixelDepth = _HPOceanWaterLevel - GetAbsolutePositionWS(positionRWS).y;
        if (!isSky && (stencil & HPO_STENCIL_WATER) == 0 && pixelDepth > 0.05)
        {
            float bedShadow = SampleSunShadowCheap(light, positionRWS);
            color *= CausticModulation(positionRWS, pixelDepth, L, bedShadow, length(positionRWS));
        }

        // 沿视线的单次散射：水深随距离线性变化 d(t) = d0 + t · rate
        float d0 = max(submerged, 0.0);
        float rate = -viewDir.y;
        float3 k = sigmaT * (1.0 + rate / muSun);
        float phase = WaterPhase(dot(viewDir, -sunRefract));
        float dither = InterleavedGradientNoise(positionSS, _TaaFrameInfo.z);

        // 光柱：分段积分，每段采样一次阴影
        float3 sunScatter = 0;
        float expStep = pow(HPO_EXP_FACTOR, 1.0 / HPO_SAMPLE_COUNT);
        float kDenom = 1.0 / (HPO_EXP_FACTOR - 1.0);
        float currentExp = 1.0;
        float marchLength = min(dist, 120.0);

        [unroll]
        for (int i = 0; i < HPO_SAMPLE_COUNT; i++)
        {
            float nextExp = currentExp * expStep;
            float t0 = (currentExp - 1.0) * kDenom * marchLength;
            float t1 = (nextExp - 1.0) * kDenom * marchLength;
            float ts = lerp(t0, t1, dither);
            float shadow = SampleSunShadowCheap(light, nearRWS + viewDir * ts);
            sunScatter += lerp(1.0, shadow, saturate(_UnderwaterGodRays)) * SegmentIntegral(k, t0, t1);
            currentExp = nextExp;
        }
        // 超过步进距离的部分不再采样阴影
        sunScatter += SegmentIntegral(k, marchLength, dist);
        sunScatter *= sunColor * exp(-sigmaT * d0 / muSun) * sigmaS * phase * _ScatterIntensity * max(_UnderwaterGodRays, 0.5);

        float3 ambientDown = SampleSH9(_AmbientProbeData, float3(0, 1, 0));
        float3 kAmb = sigmaT * (1.0 + rate / 0.7);
        float3 ambientScatter = ambientDown * exp(-sigmaT * d0 / 0.7) * sigmaS * 0.5 * SegmentIntegral(kAmb, 0.0, dist) * _AmbientScatter;

        float3 transmittance = exp(-sigmaT * dist);
        color = color * transmittance + (sunScatter + ambientScatter) * exposure;

        // 水线处镜头上挂着的一层水膜：窄带压暗，像实拍时水线的弯月面
        float meniscus = exp(-abs(pixelDist) * 0.35) * 0.35;
        color = lerp(aboveColor, color, underwaterWeight) * (1.0 - meniscus);
        return float4(color, 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "TransparentDepthPrepass"
            Tags { "LightMode" = "TransparentDepthPrepass" }

            // 正面：SSR 位（让 HDRP 的透明 SSR 追踪水面）+ 水面位（给体积/水下 Pass 识别）
            Stencil
            {
                Ref 72
                WriteMask 72
                CompFront Always
                PassFront Replace
                CompBack Always
                PassBack Keep
            }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDepth
            ENDHLSL
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Blend Off
            ZWrite On
            ZTest LEqual
            Cull Off
            // 透明 Pass 还绑定了运动矢量 / VT 反馈 RT，水面不写它们
            ColorMask 0 1
            ColorMask 0 2

            HLSLPROGRAM
            #pragma multi_compile_fragment USE_FPTL_LIGHTLIST USE_CLUSTERED_LIGHTLIST
            #pragma shader_feature_local_fragment _ _HPO_RAYMARCH_REFRACTION
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "HPOceanVolume"
            Tags { "LightMode" = "HPOceanVolume" }

            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ _HPO_RAYMARCH_REFRACTION
            #pragma vertex VertFullScreen
            #pragma fragment FragVolume
            ENDHLSL
        }

        Pass
        {
            Name "HPOceanUnderwater"
            Tags { "LightMode" = "HPOceanUnderwater" }

            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex VertFullScreen
            #pragma fragment FragUnderwater
            ENDHLSL
        }
    }

    Fallback Off
}
