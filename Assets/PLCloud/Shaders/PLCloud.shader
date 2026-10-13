Shader "PLCloud/PLCloud"
{
    // 体积云：跟随地球曲率的球壳云层（Nubis Vertical Profile Method），一直铺到地平线。
    // 渲染流程（PLCloudPass，参考 Nubis 2015/2022 的性能方案）：
    //   1. Trace     低分辨率 Ray March，每帧换一个抖动偏移；输出预乘颜色 + 透射率、重投影距离 + 云的入射距离
    //   2. Temporal  按云的深度重投影上一帧结果，邻域裁剪后混合，把抖动噪点累积掉
    //   3. Composite 全分辨率上采样，按场景深度遮挡后预乘叠加到相机颜色
    //   4. Shadow    沿光线积分透射率，写进太阳 cookie
    // 阳光 = HDRP 主方向光 × PBR 大气透射；环境光 = Ambient Probe；空气透视 = HDRP 高度雾；输出预曝光 HDR。
    Properties
    {
        [Header(Layer)]
        _CloudBottom("Cloud Bottom Altitude (m)", Float) = 1500
        _CloudTop("Cloud Top Altitude (m)", Float) = 3500
        _PlanetRadius("Planet Radius (m)", Float) = 6378100
        _SeaLevel("Sea Level (m)", Float) = 0
        _MaxDistance("Max Render Distance (m)", Float) = 120000

        [Header(Shape)]
        // RGBA 3D 噪声：R = Perlin-Worley 主体，GBA = 三个频段的 Worley FBM（Tools/PLCloud/Generate Cloud Noise 128 生成，带 mip）。
        _NoiseTex("3D Noise", 3D) = "" {}
        // 以下尺寸都以世界空间"米"为单位。
        _ShapeSize("Shape Size (m)", Range(500.0, 20000.0)) = 4500.0
        _DetailSize("Detail Size (m)", Range(50.0, 3000.0)) = 700.0
        _WeatherSize("Weather Size (m)", Range(2000.0, 100000.0)) = 24000.0
        _Coverage("Coverage", Range(0.0, 1.0)) = 0.5
        _CoverageVariation("Coverage Variation", Range(0.0, 1.0)) = 0.55
        _HeightVariation("Height Variation", Range(0.0, 1.0)) = 0.45
        _DetailErosion("Detail Erosion", Range(0.0, 1.0)) = 0.45
        _BottomFade("Bottom Fade", Range(0.001, 0.5)) = 0.08
        _TopRound("Top Roundness", Range(0.001, 1.0)) = 0.55
        _WindDirection("Wind Direction", Vector) = (1.0, 0.0, 0.3, 0.0)
        _WindSpeed("Wind Speed (m/s)", Range(0.0, 100.0)) = 15.0

        [Header(Density)]
        _Density("Extinction (1/m)", Range(0.0, 0.2)) = 0.02

        [Header(Sun)]
        [HDR] _SunTint("Sun Tint", Color) = (1.0, 1.0, 1.0, 1.0)
        _SunIntensity("Sun Intensity", Range(0.0, 10.0)) = 1.0
        _ForwardG("Forward Eccentricity", Range(0.0, 0.95)) = 0.75
        _BackwardG("Backward Eccentricity", Range(-0.95, 0.0)) = -0.124
        _LobeBlend("Backward Lobe Weight", Range(0.0, 1.0)) = 0.277
        _MSAttenuation("Multi Scatter Extinction (a)", Range(0.05, 1.0)) = 0.36
        _MSContribution("Multi Scatter Contribution (b)", Range(0.0, 1.0)) = 0.5
        _MSEccentricity("Multi Scatter Eccentricity (c)", Range(0.0, 1.0)) = 0.62
        _PowderStrength("Powder Strength", Range(0.0, 1.0)) = 0.45
        _PowderDepth("Powder Depth (m)", Range(1.0, 2000.0)) = 300.0

        [Header(Ambient)]
        // 天空光取 Ambient Probe 上半球；地面 / 海面反射只取下半球的亮度再乘这个色调，
        // 否则沙滩和 PBR 天空的橙色地面会把云底染黄。
        [HDR] _SkyAmbient("Sky Ambient Tint", Color) = (1.0, 1.0, 1.0, 1.0)
        [HDR] _GroundAmbient("Ground Bounce Tint", Color) = (0.55, 0.62, 0.72, 1.0)
        _AmbientStrength("Ambient Strength", Range(0.0, 8.0)) = 1.0

        [Header(Atmosphere)]
        _AerialPerspective("Aerial Perspective", Range(0.0, 1.0)) = 1.0

        [Header(Marching)]
        // Nubis 距离自适应步长：step = Near + 距离 × Growth；空白区域用粗采样走 Empty Step Scale 倍的大步。
        _NearStepSize("Near Step Size (m)", Range(5.0, 500.0)) = 60.0
        _StepGrowth("Step Growth (per m)", Range(0.0, 0.05)) = 0.006
        _EmptyStepScale("Empty Step Scale", Range(1.0, 8.0)) = 3.0
        _StepCount("Max View Steps", Range(16, 256)) = 160
        _LightStepCount("Light Steps", Range(1, 12)) = 6
        _LightMarchDistance("Light March Distance (m)", Range(50.0, 6000.0)) = 1800.0
        _MipDistanceScale("Noise Mip Distance Scale", Range(0.0, 0.01)) = 0.0004
        _EarlyExit("Early Exit Transmittance", Range(0.001, 0.2)) = 0.01
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/PhysicallyBasedSky/PhysicallyBasedSkyCommon.hlsl"
    #include "PLCloudDensity.cginc"

    float4 _SunTint;
    float _SunIntensity;
    float _ForwardG;
    float _BackwardG;
    float _LobeBlend;
    float _MSAttenuation;
    float _MSContribution;
    float _MSEccentricity;
    float _PowderStrength;
    float _PowderDepth;
    float4 _SkyAmbient;
    float4 _GroundAmbient;
    float _AmbientStrength;
    float _AerialPerspective;
    float _NearStepSize;
    float _StepGrowth;
    float _EmptyStepScale;
    float _StepCount;
    float _LightStepCount;
    float _LightMarchDistance;
    float _EarlyExit;
    float _MaxDistance;

    // PLCloudPass 设置
    TEXTURE2D_X(_PLCloudDepthBuffer);
    TEXTURE2D(_PLCloudTraceColor);   // rgb = 预乘、预曝光的云颜色，a = 透射率
    TEXTURE2D(_PLCloudTraceDepth);   // x = 重投影距离（按不透明度加权），y = 云的入射距离
    TEXTURE2D(_PLCloudHistory);
    TEXTURE2D(_PLCloudResult);
    float4 _PLCloudTraceSize;        // xy = 低分辨率尺寸，zw = 降采样倍数 / 全分辨率尺寸（低分辨率像素 → 全屏 uv）
    float4 _PLCloudUVScale;          // xy = 全屏 uv → 低分辨率 RT uv，z = 降采样倍数
    float4 _PLCloudFrame;            // x = 帧序号，y = 历史是否有效，z = 历史权重

    float4 _PLCloudShadowCenter;
    float4 _PLCloudShadowRight;
    float4 _PLCloudShadowUp;
    float4 _PLCloudShadowForward;
    float4 _PLCloudShadowSize;

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
    };

    Varyings FullscreenVert(uint vertexID : SV_VertexID)
    {
        Varyings output;
        output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
        return output;
    }

    // 全屏 uv → 世界空间视线方向（相机相对渲染下，重建出的位置就是从相机出发的向量）
    float3 ViewDirectionWS(float2 uv)
    {
        return normalize(ComputeWorldSpacePosition(uv, 0.5, UNITY_MATRIX_I_VP));
    }

    // 场景深度换算成沿视线的距离；天空返回一个很大的值
    float SceneDistance(uint2 pixel, float3 directionWS)
    {
        float deviceDepth = LOAD_TEXTURE2D_X(_PLCloudDepthBuffer, pixel).r;
        if (deviceDepth == UNITY_RAW_FAR_CLIP_VALUE)
        {
            return 1e9;
        }
        float3 forward = -UNITY_MATRIX_V[2].xyz;
        return LinearEyeDepth(deviceDepth, _ZBufferParams) / max(dot(forward, directionWS), 1e-4);
    }
    float PLCloudIGN(float2 pixel, float frame)
    {
        pixel += 5.588238 * fmod(frame, 64.0);
        return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    }

    // 球面上某点的"上"方向（行星球心在世界原点正下方 _PlanetRadius 处）
    float3 PlanetUp(float3 positionWS)
    {
        return normalize(float3(positionWS.x, positionWS.y - _SeaLevel + _PlanetRadius, positionWS.z));
    }

    // 归一化的 Henyey-Greenstein（含 1/4π）：HDRP 的光强是照度，乘上它直接得到辐亮度
    float HenyeyGreenstein(float cosAngle, float g)
    {
        float g2 = g * g;
        return INV_FOUR_PI * (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cosAngle, 1e-4), 1.5);
    }

    // HDRP 里最亮的方向光作为太阳。light.color 已包含强度，但不含曝光和大气透射
    void GetSun(out float3 L, out float3 color)
    {
        L = float3(0.0, 1.0, 0.0);
        color = 0.0;
        float best = -1.0;
        for (uint i = 0; i < _DirectionalLightCount; ++i)
        {
            DirectionalLightData light = _DirectionalLightDatas[i];
            float luminance = Luminance(light.color.rgb);
            if (luminance > best)
            {
                best = luminance;
                L = -light.forward.xyz;
                color = light.color.rgb;
            }
        }
    }

    // 阳光穿过 PBR 大气到达该点的透射率（场景没有 PBR 天空时为 1）
    float3 SunAtmosphereTransmittance(float3 positionWS, float3 L)
    {
        if (_PlanetaryRadius <= 0.0)
        {
            return 1.0;
        }
        return EvaluateSunColorAttenuation(dot(PlanetUp(positionWS), L), _PlanetaryRadius + max(CloudAltitude(positionWS), 0.0));
    }

    // 相机到云之间的空气透射率，用 PBR 天空的空气 / 气溶胶参数做 6 步积分
    float3 AirTransmittance(float3 originWS, float3 directionWS, float distance)
    {
        if (_AirScaleHeight <= 0.0 || _AerialPerspective <= 0.0)
        {
            return 1.0;
        }
        float stepSize = distance / 6.0;
        float3 opticalDepth = 0.0;
        for (int i = 0; i < 6; ++i)
        {
            float h = max(CloudAltitude(originWS + directionWS * ((i + 0.5) * stepSize)), 0.0);
            opticalDepth += _AirSeaLevelExtinction.xyz * exp(-h * _AirDensityFalloff) + _AerosolSeaLevelExtinction * exp(-h * _AerosolDensityFalloff);
        }
        return exp(-opticalDepth * stepSize * _AerialPerspective);
    }

    // Nubis 光照步进：步长平方递增，前两步带细节，最后补一个远处的粗采样抓住远处云的遮挡
    float LightOpticalDepth(float3 positionWS, float3 L, float lod, float viewDistance)
    {
        int steps = (int)_LightStepCount;
        if (viewDistance > 30000.0)
        {
            steps = max(2, steps / 2);
        }
        float opticalDepth = 0.0;
        float previous = 0.0;
        for (int i = 0; i < steps; ++i)
        {
            float x = (i + 1.0) / steps;
            float d = _LightMarchDistance * x * x;
            float segment = d - previous;
            opticalDepth += SampleCloudDensity(positionWS + L * (previous + segment * 0.5), i < 2, lod) * segment;
            previous = d;
        }
        opticalDepth += SampleCloudDensity(positionWS + L * (_LightMarchDistance * 3.0), false, lod + 1.0) * _LightMarchDistance;
        return opticalDepth;
    }
    // ---------------------------------------------------------------------------------------------
    // 1. Trace：低分辨率 Ray March
    // ---------------------------------------------------------------------------------------------
    struct TraceOutput
    {
        float4 color : SV_Target0;   // rgb = 预乘、预曝光颜色，a = 透射率
        float4 depth : SV_Target1;   // x = 重投影距离，y = 云的入射距离
    };

    TraceOutput TraceFrag(Varyings input)
    {
        TraceOutput output;
        output.color = float4(0.0, 0.0, 0.0, 1.0);
        output.depth = float4(1e9, 1e9, 0.0, 0.0);

        float2 lowPixel = input.positionCS.xy;
        float2 uv = lowPixel * _PLCloudTraceSize.zw;
        float3 directionWS = ViewDirectionWS(uv);
        float3 originWS = _WorldSpaceCameraPos.xyz;

        // 低分辨率像素取其覆盖区域内一个固定位置的场景深度（与 uv 对应），并记下来供 Composite 做深度感知上采样
        int downsample = (int)_PLCloudUVScale.z;
        uint2 fullPixel = min((uint2)(floor(lowPixel) * downsample) + (uint)(downsample / 2), (uint2)_ScreenSize.xy - 1);
        float sceneDistance = SceneDistance(fullPixel, directionWS);
        output.depth.z = sceneDistance;

        float2 interval;
        if (!IntersectCloudLayer(originWS, directionWS, interval))
        {
            return output;
        }
        interval.y = min(interval.y, min(sceneDistance, _MaxDistance));
        if (interval.y <= interval.x)
        {
            return output;
        }

        float3 sunL, sunColor;
        GetSun(sunL, sunColor);
        float cosAngle = dot(directionWS, sunL);

        // 三阶多次散射的相函数只和角度有关，循环外算好
        float3 phases;
        [unroll]
        for (int octave = 0; octave < 3; ++octave)
        {
            float c = pow(_MSEccentricity, octave);
            phases[octave] = lerp(HenyeyGreenstein(cosAngle, _ForwardG * c), HenyeyGreenstein(cosAngle, _BackwardG * c), _LobeBlend);
        }
        // Powder 在背对太阳时最强，逆光时减弱，避免压暗银边
        float powderAmount = _PowderStrength * saturate(0.5 - 0.5 * cosAngle + 0.25);

        // 环境光：天空取上半球；地面 / 海面反射只取亮度再乘冷色调，不让沙滩和 PBR 天空的橙色地面染黄云底
        float3 skyAmbient = max(SampleSH9(_AmbientProbeData, float3(0.0, 1.0, 0.0)), 0.0) * _SkyAmbient.rgb;
        float groundLuminance = Luminance(max(SampleSH9(_AmbientProbeData, float3(0.0, -1.0, 0.0)), 0.0));
        float3 groundAmbient = groundLuminance * _GroundAmbient.rgb;

        float jitter = PLCloudIGN(lowPixel, _PLCloudFrame.x);
        float t = interval.x + jitter * (_NearStepSize + interval.x * _StepGrowth) * _EmptyStepScale;

        float transmittance = 1.0;
        float3 scattered = 0.0;
        float weightedDistance = 0.0;
        float entryDistance = 1e9;
        float3 sunAtCloud = 0.0;
        bool sunEvaluated = false;
        bool fineMode = false;
        int emptyCount = 0;
        int maxSteps = min((int)_StepCount, 256);

        [loop]
        for (int i = 0; i < maxSteps; ++i)
        {
            if (t >= interval.y || transmittance < _EarlyExit)
            {
                break;
            }

            float stepSize = _NearStepSize + t * _StepGrowth;
            float lod = log2(1.0 + t * _MipDistanceScale);
            float3 positionWS = originWS + directionWS * t;

            if (!fineMode)
            {
                // 空白区域只采主体形状、走大步；碰到云就退回半步切到精细模式（Nubis 的两段式步进）
                float coarse = SampleCloudDensity(positionWS, false, lod);
                if (coarse > 0.0)
                {
                    fineMode = true;
                    emptyCount = 0;
                    t = max(interval.x, t - stepSize * _EmptyStepScale);
                    continue;
                }
                t += stepSize * _EmptyStepScale;
                continue;
            }

            float extinction = SampleCloudDensity(positionWS, true, lod);
            if (extinction > 0.0)
            {
                emptyCount = 0;
                entryDistance = min(entryDistance, t);

                if (!sunEvaluated)
                {
                    // 整个视线上阳光颜色变化很小，在第一个命中点算一次大气透射
                    sunAtCloud = sunColor * SunAtmosphereTransmittance(positionWS, sunL) * _SunTint.rgb * _SunIntensity;
                    sunEvaluated = true;
                }

                float opticalDepth = LightOpticalDepth(positionWS, sunL, lod, t);
                float3 sunScattering = 0.0;
                [unroll]
                for (int o = 0; o < 3; ++o)
                {
                    sunScattering += pow(_MSContribution, o) * phases[o] * exp(-pow(_MSAttenuation, o) * opticalDepth);
                }
                float powder = 1.0 - exp(-extinction * _PowderDepth * 2.0);
                sunScattering *= lerp(1.0, powder, powderAmount);

                float height01 = CloudHeight01(positionWS);
                float3 ambient = lerp(groundAmbient, skyAmbient, saturate(height01 * 1.2));
                ambient *= lerp(0.55, 1.0, saturate(height01 + (1.0 - extinction / max(_Density, 1e-5)) * 0.5)) * _AmbientStrength;

                // 能量守恒积分（Hillaire 2015）
                float segmentT = exp(-extinction * stepSize);
                float weight = transmittance * (1.0 - segmentT);
                scattered += weight * (sunAtCloud * sunScattering + ambient);
                weightedDistance += weight * t;
                transmittance *= segmentT;
            }
            else if (++emptyCount > 6)
            {
                fineMode = false;
            }
            t += stepSize;
        }

        float alpha = 1.0 - transmittance;
        if (alpha < 1e-4)
        {
            return output;
        }

        // 空气透视：云按到相机的距离被空气衰减，并向同方向的天空颜色过渡，远处的云自然融进地平线的雾霭
        float cloudDistance = weightedDistance / alpha;
        float3 airT = AirTransmittance(originWS, directionWS, cloudDistance);
        float3 horizonSky = SampleSkyTexture(directionWS, 2.0, 0).rgb;
        scattered = scattered * airT + horizonSky * (1.0 - airT) * alpha;

        output.color = float4(scattered * GetCurrentExposureMultiplier(), transmittance);
        output.depth.xy = float2(cloudDistance, entryDistance);
        return output;
    }
    // ---------------------------------------------------------------------------------------------
    // 2. Temporal：按云的深度重投影上一帧，邻域裁剪后混合（Nubis 用 4×4 分帧 + 重投影，这里用抖动 + 累积）
    // ---------------------------------------------------------------------------------------------
    float4 TemporalFrag(Varyings input) : SV_Target
    {
        float2 lowPixel = input.positionCS.xy;
        int2 p = (int2)lowPixel;
        float4 current = LOAD_TEXTURE2D(_PLCloudTraceColor, p);
        if (_PLCloudFrame.y < 0.5)
        {
            return current;
        }

        // 3×3 邻域的包围盒（YCoCg 下更紧，这里直接用 RGB + 透射率）
        float4 minColor = current;
        float4 maxColor = current;
        int2 maxPixel = (int2)_PLCloudTraceSize.xy - 1;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                float4 s = LOAD_TEXTURE2D(_PLCloudTraceColor, clamp(p + int2(x, y), 0, maxPixel));
                minColor = min(minColor, s);
                maxColor = max(maxColor, s);
            }
        }

        // 重投影：没有云的像素按无穷远（天空）处理，只受相机旋转影响
        float2 uv = lowPixel * _PLCloudTraceSize.zw;
        float3 directionWS = ViewDirectionWS(uv);
        float distance = LOAD_TEXTURE2D(_PLCloudTraceDepth, p).x;
        distance = min(distance, _MaxDistance * 2.0);
        float3 positionRWS = directionWS * distance;
        float4 previousCS = mul(UNITY_MATRIX_PREV_VP, float4(positionRWS, 1.0));
        float2 previousUV = previousCS.xy / previousCS.w * 0.5 + 0.5;
    #if UNITY_UV_STARTS_AT_TOP
        previousUV.y = 1.0 - previousUV.y;
    #endif
        if (any(previousUV < 0.0) || any(previousUV > 1.0))
        {
            return current;
        }

        float4 history = SAMPLE_TEXTURE2D_LOD(_PLCloudHistory, s_linear_clamp_sampler, previousUV * _PLCloudUVScale.xy, 0);
        history = clamp(history, minColor, maxColor);
        return lerp(current, history, _PLCloudFrame.z);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Composite：全分辨率上采样，按场景深度遮挡后预乘叠加
    // ---------------------------------------------------------------------------------------------
    float4 CompositeFrag(Varyings input) : SV_Target
    {
        uint2 pixel = (uint2)input.positionCS.xy;
        float2 uv = input.positionCS.xy * _ScreenSize.zw;
        float3 directionWS = ViewDirectionWS(uv);
        float sceneDistance = SceneDistance(pixel, directionWS);

        // 深度感知上采样：在 2×2 低分辨率邻域里，按各自记录的场景深度与本像素的接近程度加权。
        // 天空与海面 / 地形交界处，低分辨率样本只采用同一侧的，云不会漏到前景上，也不会被前景抠出黑边。
        float2 lowPosition = uv * _PLCloudTraceSize.xy - 0.5;
        int2 base = (int2)floor(lowPosition);
        float2 f = lowPosition - base;
        int2 maxPixel = (int2)_PLCloudTraceSize.xy - 1;
        float logScene = log2(max(sceneDistance, 1.0));

        float4 sum = 0.0;
        float weightSum = 0.0;
        float4 nearest = float4(0.0, 0.0, 0.0, 1.0);
        float nearestDiff = 1e9;
        [unroll]
        for (int i = 0; i < 4; ++i)
        {
            int2 offset = int2(i & 1, i >> 1);
            int2 p = clamp(base + offset, 0, maxPixel);
            float4 c = LOAD_TEXTURE2D(_PLCloudResult, p);
            float lowScene = LOAD_TEXTURE2D(_PLCloudTraceDepth, p).z;
            float diff = abs(log2(max(lowScene, 1.0)) - logScene);
            float bilinear = (offset.x ? f.x : 1.0 - f.x) * (offset.y ? f.y : 1.0 - f.y);
            float w = bilinear * exp2(-diff * 8.0) + 1e-5;
            sum += c * w;
            weightSum += w;
            if (diff < nearestDiff)
            {
                nearestDiff = diff;
                nearest = c;
            }
        }
        float4 cloud = nearestDiff > 0.5 ? nearest : sum / weightSum;
        // Blend One SrcAlpha：dst = cloud.rgb + dst × 透射率
        return cloud;
    }

    // ---------------------------------------------------------------------------------------------
    // 4. Shadow：沿太阳方向积分透射率，写进 cookie（正交投影，中心 / 轴与 HDRP 方向光 cookie 一致）
    // ---------------------------------------------------------------------------------------------
    float4 ShadowFrag(Varyings input) : SV_Target
    {
        float2 uv = input.positionCS.xy * _PLCloudShadowSize.zw;
        float3 L = -_PLCloudShadowForward.xyz;
        float3 planePosition = _PLCloudShadowCenter.xyz + (uv.x - 0.5) * _PLCloudShadowRight.xyz + (uv.y - 0.5) * _PLCloudShadowUp.xyz;

        // 从 cookie 平面沿光的反方向射向云层，只采主体形状
        float2 interval;
        if (!IntersectCloudLayer(planePosition, L, interval))
        {
            return 1.0;
        }
        const int steps = 24;
        float stepSize = (interval.y - interval.x) / steps;
        float opticalDepth = 0.0;
        for (int i = 0; i < steps; ++i)
        {
            opticalDepth += SampleCloudDensity(planePosition + L * (interval.x + (i + 0.5) * stepSize), false, 1.0) * stepSize;
            if (opticalDepth > 12.0)
            {
                break;
            }
        }
        float transmittance = lerp(1.0, exp(-opticalDepth), _PLCloudShadowForward.w);
        return float4(transmittance.xxx, 1.0);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "PLCloudTrace"
            Blend Off
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment TraceFrag
            ENDHLSL
        }

        Pass
        {
            Name "PLCloudTemporal"
            Blend Off
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment TemporalFrag
            ENDHLSL
        }

        Pass
        {
            Name "PLCloudComposite"
            Blend One SrcAlpha
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment CompositeFrag
            ENDHLSL
        }

        Pass
        {
            Name "PLCloudShadow"
            Blend Off
            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment ShadowFrag
            ENDHLSL
        }
    }

    Fallback Off
}
