#ifndef PLCLOUD_DENSITY_INCLUDED
#define PLCLOUD_DENSITY_INCLUDED

// 体积云与云影共用的密度场。云层是跟随地球曲率的球壳（Nubis "Vertical Profile Method"），
// 海拔 [_CloudBottom, _CloudTop] 之间，水平方向无限延伸，因此能一直铺到地平线。
// 行星球心在世界原点正下方 _PlanetRadius 处。坐标都是绝对世界坐标（米）。

TEXTURE3D(_NoiseTex);
SAMPLER(sampler_NoiseTex);

float _Density;
float _ShapeSize;
float _DetailSize;
float _WeatherSize;
float _Coverage;
float _CoverageVariation;
float _HeightVariation;
float _DetailErosion;
float _BottomFade;
float _TopRound;
float4 _WindDirection;
float _WindSpeed;
float _CloudBottom;
float _CloudTop;
float _PlanetRadius;
float _SeaLevel;
float _MipDistanceScale;

float CloudRemap(float value, float oldMin, float oldMax, float newMin, float newMax)
{
    return newMin + (value - oldMin) / max(oldMax - oldMin, 0.0001) * (newMax - newMin);
}

// 海拔高度。行星半径 6.4e6 m，直接 length(p - center) - R 会丢掉米级精度，
// 这里把 |p - c|² - R² 展开成小量之和再除以 (|p - c| + R)。
float CloudAltitude(float3 positionWS)
{
    float y = positionWS.y - _SeaLevel;
    float horizontalSq = positionWS.x * positionWS.x + positionWS.z * positionWS.z;
    float radius = sqrt(horizontalSq + (y + _PlanetRadius) * (y + _PlanetRadius));
    return (horizontalSq + y * (y + 2.0 * _PlanetRadius)) / (radius + _PlanetRadius);
}

float CloudHeight01(float3 positionWS)
{
    return saturate((CloudAltitude(positionWS) - _CloudBottom) / max(_CloudTop - _CloudBottom, 1.0));
}

// 射线与海拔 altitude 的球面求交，返回两个根（t.x <= t.y）。
// c = |o - center|² - (R + h)² 同样展开成小量，避免大数相减；根用 q 与 c / q 的数值稳定形式。
bool IntersectAltitudeSphere(float3 originWS, float3 directionWS, float altitude, out float2 t)
{
    float y = originWS.y - _SeaLevel;
    float3 offset = float3(originWS.x, y + _PlanetRadius, originWS.z);
    float b = dot(offset, directionWS);
    float c = originWS.x * originWS.x + originWS.z * originWS.z + (y - altitude) * (y + altitude + 2.0 * _PlanetRadius);
    float discriminant = b * b - c;
    t = 0.0;
    if (discriminant < 0.0)
    {
        return false;
    }

    float q = -(b + (b >= 0.0 ? 1.0 : -1.0) * sqrt(discriminant));
    float t0 = q;
    float t1 = abs(q) > 1e-6 ? c / q : 0.0;
    t = float2(min(t0, t1), max(t0, t1));
    return true;
}

// 视线穿过云层的区间。相机在云层下方 / 内部 / 上方三种情况分别处理。
bool IntersectCloudLayer(float3 originWS, float3 directionWS, out float2 interval)
{
    interval = 0.0;
    float altitude = CloudAltitude(originWS);
    float2 innerHit, outerHit;
    bool hitInner = IntersectAltitudeSphere(originWS, directionWS, _CloudBottom, innerHit);
    bool hitOuter = IntersectAltitudeSphere(originWS, directionWS, _CloudTop, outerHit);

    if (altitude < _CloudBottom)
    {
        // 在内球面里面：一定会从内球面穿出，再从外球面穿出
        interval = float2(innerHit.y, outerHit.y);
    }
    else if (altitude < _CloudTop)
    {
        // 在云层内：往下看会先碰到云底
        interval = float2(0.0, (hitInner && innerHit.x > 0.0) ? innerHit.x : outerHit.y);
    }
    else
    {
        if (!hitOuter || outerHit.y <= 0.0)
        {
            return false;
        }
        interval = float2(max(outerHit.x, 0.0), (hitInner && innerHit.x > 0.0) ? innerHit.x : outerHit.y);
    }

    return interval.y > interval.x;
}

float4 SampleCloudNoise(float3 uvw, float lod)
{
    return SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, uvw, lod);
}

// 返回世界空间消光系数 sigma_t（单位 1/m）。
// withDetail = false：只有主体形状（Nubis 的 "cheap sample"），自适应步进的粗步和光照步进的远端使用。
// 去掉细节侵蚀只会让密度变大，因此粗采样为 0 的地方完整采样一定也是 0。
// lod：噪声 mip 级别，远处用低 mip，既省带宽又减少走样（Nubis 3 按距离选 mip）。
float SampleCloudDensity(float3 positionWS, bool withDetail, float lod)
{
    float height01 = (CloudAltitude(positionWS) - _CloudBottom) / max(_CloudTop - _CloudBottom, 1.0);
    if (height01 <= 0.0 || height01 >= 1.0)
    {
        return 0.0;
    }

    // 噪声在世界空间以"米"为单位采样
    float3 windOffset = _WindDirection.xyz * (_Time.y * _WindSpeed);
    float3 shapePosition = positionWS - windOffset;

    // 覆盖率图：取 3D 噪声的两个水平切片作为天气图，决定哪里有云、云能长多高
    float weather = SampleCloudNoise(float3(shapePosition.xz / _WeatherSize, 0.37), 0).r;
    float weatherDetail = SampleCloudNoise(float3(shapePosition.xz / (_WeatherSize * 0.37), 0.71), 0).g;
    weather = saturate(weather * 0.75 + weatherDetail * 0.25);
    float coverage = saturate(_Coverage + (weather * 2.0 - 1.0) * _CoverageVariation);
    float localTop = lerp(1.0 - _HeightVariation, 1.0, weather);
    float cloudHeight = height01 / max(localTop, 0.05);
    if (cloudHeight >= 1.0 || coverage <= 0.0)
    {
        return 0.0;
    }

    // 积云高度剖面：底部很快出现（平坦的云底），顶部缓慢收窄（圆鼓的云顶）
    float bottom = smoothstep(0.0, max(_BottomFade, 0.001), cloudHeight);
    float top = smoothstep(1.0, 1.0 - max(_TopRound, 0.001), cloudHeight);
    float heightGradient = bottom * top;

    // 主体形状：Perlin-Worley 被低频 Worley FBM 膨胀成团块
    float4 shapeNoise = SampleCloudNoise(shapePosition / _ShapeSize, lod);
    float shapeFbm = dot(shapeNoise.gba, float3(0.625, 0.25, 0.125));
    float baseShape = saturate(CloudRemap(shapeNoise.r, shapeFbm - 1.0, 1.0, 0.0, 1.0));

    // 覆盖率阈值同时乘上高度剖面，云在顶部是"缩小"而不是"变淡"，轮廓更清楚
    baseShape *= heightGradient;
    float density = saturate(CloudRemap(baseShape, 1.0 - coverage, 1.0, 0.0, 1.0)) * coverage;

    if (withDetail && density > 0.0)
    {
        // 细节噪声流动得更快，并被主体噪声轻微扭曲，边缘会有翻滚的感觉
        float3 detailPosition = positionWS - windOffset * 1.6 + shapeNoise.gba * (_DetailSize * 0.35);
        float4 detailNoise = SampleCloudNoise(detailPosition / _DetailSize, lod);
        float detailFbm = dot(detailNoise.gba, float3(0.625, 0.25, 0.125));
        // 云底用丝缕状侵蚀，往上过渡到菜花状的鼓包侵蚀
        float detailModifier = lerp(detailFbm, 1.0 - detailFbm, saturate(cloudHeight * 5.0));
        density = saturate(CloudRemap(density, detailModifier * _DetailErosion, 1.0, 0.0, 1.0));
    }

    return density * max(_Density, 0.0);
}

#endif
