[English](README.md) | **简体中文**

# PLSea

Unity HDRP 下的实时海洋与体积云，自己写的着色器和 Compute Shader，不依赖付费插件。

- **PLOcean** — FFT 海浪、交互水波、水下光照、焦散、浮力
- **PLCloud** — Nubis 风格的体积云，铺满整个天空，投下云影

两套系统共用同一个地球曲率参数，远处的海面会弯到地平线以下。

环境：Unity 2022.3 + HDRP 14。

<img src="https://github.com/PuLei36/notes/blob/note/images/SeaSample.png?raw=true" alt="Alt Text" style="zoom: 50%;" />

## 海洋

**波形**：JONSWAP 谱 + 方向谱，每帧在 GPU 上跑 IFFT。三个级联按波数分段（250 m / 17 m / 5 m），互不重叠，所以同一波长不会被叠加两次。水平位移让波峰变尖，Jacobian 累积出泡沫。

**网格**：四叉树 LOD，格子离相机越近越密，LOD 边界是一圈圈圆形。做了 2:1 平衡并在粗细交界插入中点，没有 T 型接缝。网格按整格对齐跟随相机，顶点不会随移动而游动。

**光照**（参考 HPWater 的水体 BSDF）：
- 宏观体积散射沿折射光线指数步进，逐点采样太阳阴影，水下因此有光柱
- 浪尖薄处的次表面散射和逆光透射
- 折射支持指数步进光线追踪，按水下光程做 mip 模糊和色散
- 反射按 SSR → 反射探针 → 天空的层级回退
- 焦散用 Compute Shader 追踪光子得到，按水底深度在三张切片间插值

**交互水波**：相机周围一块区域里解二维波动方程，60 Hz 固定子步，四周海绵层吸收。俯视渲染一张场景高度图，高于水面的物体成为障碍（波会反射），浅滩额外衰减。物体穿过水面移动时自动产生波纹，也可以挂 `PLOceanRippleSource` 或调 `AddImpulse`。

**浮力**：按船体截面积分水压，水面高度来自 FFT 位移纹理的异步回读，和画面相差 1 到 3 帧。船舱内部的水面会被遮罩掉，水不会"进船"。

**水下**：相机吃水线逐像素判断，随浪起伏、随相机倾斜。水下有体积雾、光柱、焦散，水线处还有一圈镜头水膜。

## 体积云

云层是跟随地球曲率的球壳，海拔范围在材质里设置，水平方向无限延伸，所以能一直铺到地平线。密度场用 Perlin-Worley 成团、Worley FBM 侵蚀，天气图控制云量和云顶高度。

光照是双瓣 Henyey-Greenstein 相函数加三阶多次散射近似，带 Powder 效应。阳光取 HDRP 主方向光并乘以 PBR 大气透射，环境光取环境探针，远处的云向天空色过渡，自然融进地平线的雾霭。

性能上参考了 Nubis 的做法：半分辨率步进，每帧换抖动偏移，按云深度重投影上一帧做时域累积，合成时按场景深度做深度感知上采样。步长随距离增长，空白区域粗采样走大步，噪声按距离选 mip。1080p 下大约 3 到 10 ms（RTX 3060 Laptop，粗测）。

云影的做法是把云的透光量画成一张图，挂到方向光的 cookie 上，所有物体和体积雾都会自动接收。

## 运行

仓库里没有场景文件，需要自己搭一个。

1. 用 Unity 2022.3 打开项目，Project Settings 里的渲染管线资源选 `Assets/Settings/HDRP High Fidelity.asset`。
2. 菜单 `Tools/PLCloud/Generate Cloud Noise 128` 生成云的 3D 噪声（仓库里已有一份，重新生成也可以）。
3. 新建场景，加入：
   - 一个空物体，挂 `SeaMesh` + `PLOceanSimulation`，`Sea Material` 指向 `PLOcean/Materials/PLOceanSea.mat`。物体的 Y 就是海平面高度。
   - 一个空物体挂 `PLOceanRipple`（交互水波，可选）。
   - 一个空物体挂 `PLCloudVolume`，`Material` 指向 `PLCloud/Materials/PLCloud.mat`，`Sun` 指向场景里的方向光。同一物体上加 `CustomPassVolume`，注入点选 `Before Post Process`，添加 `PLCloudPass`。
   - 另一个 `CustomPassVolume`：注入点 `Before Transparent` 加 `PLOceanVolumePass`，注入点 `Before Post Process` 加 `PLOceanUnderseaPass`，两个 Pass 的 `Sea Material` 都指向水面材质。
   - 一个 Volume，用 `Assets/Settings/OutdoorsSkyProfile.asset`（Physically Based Sky + 雾）。
4. 想要地球曲率，把 `SeaMesh` 的 `Planet Radius` 和天空 Profile 里 Physically Based Sky 的 `Planetary Radius` 填成同一个值（默认 6378100）。填 0 就是平面海。云会自动跟随这个值。
5. 要铺到地平线，把 `SeaMesh` 的 `LOD Levels` 调到 12、`Outer Radius` 调到 150000，相机远裁面设到 160 km。

### 第三方资源

以下内容因为授权限制不在仓库里，需要自己从 Asset Store 导入，或者改用自己的资源：

- NatureManufacture 的地形和植被
- Atlantis Ruins
- Quantum Stage（角色）

`Assets/Scripts` 里的角色控制器需要一个 Humanoid 角色加 Animator，动画控制器在 `Assets/Player/PlayerAnimator.controller`。

## 参数速查

| 位置 | 参数 | 作用 |
| --- | --- | --- |
| PLOceanSimulation | Wind Speed / Fetch | 浪的大小和成熟度 |
| | Choppiness | 波峰尖锐程度 |
| | Length Scale 0/1/2 | 三个级联的平铺尺寸 |
| | Caustics | 焦散开关和参数 |
| SeaMesh | Base Cell Size / Inner Radius / LOD Levels | 网格密度和范围 |
| | Planet Radius | 地球曲率，0 为平面 |
| PLOceanSea.mat | Scatter / Absorption | 水色 |
| | Reflection / Specular Intensity | 反射和高光强度 |
| | Undersea Fog Density / God Rays | 水下雾和光柱 |
| PLCloud.mat | Cloud Bottom / Top Altitude | 云层海拔范围 |
| | Coverage / Coverage Variation | 云量和疏密 |
| | Extinction | 云的厚实程度 |
| | Max View Steps / Light Steps | 画质与性能 |
| PLCloudPass | Downsample / History Weight | 云的渲染分辨率和时域累积强度 |

## 参考

- Tessendorf, *Simulating Ocean Water* — FFT 海浪
- Hillaire, *Physically Based and Unified Volumetric Rendering in Frostbite* (2015) — 能量守恒的体积积分
- Schneider, *The Real-time Volumetric Cloudscapes of Horizon Zero Dawn* (2015) 与 *Nubis³* (Advances in Real-Time Rendering, 2023) — 体积云
- Wrenninge et al., *Oz: The Great and Volumetric* (2013) — 多次散射近似
- Hillaire, *A Scalable and Production Ready Sky and Atmosphere Rendering Technique* (2020) — 大气
