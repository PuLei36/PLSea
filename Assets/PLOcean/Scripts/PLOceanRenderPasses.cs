using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// 给每个相机分配一个从 1 开始的小整数编号，写进 shader 判断体积缓冲是不是属于当前相机。
/// </summary>
public static class PLOceanCameraIds
{
    static readonly Dictionary<Camera, int> s_Ids = new Dictionary<Camera, int>();
    static readonly List<Camera> s_Dead = new List<Camera>();
    static int s_Next = 1;

    public static int Get(Camera camera)
    {
        if (!s_Ids.TryGetValue(camera, out int id))
        {
            // 新相机出现时清掉已销毁的相机，字典不会无限增长；编号单调递增，不会和旧的体积缓冲撞号
            foreach (var pair in s_Ids)
            {
                if (pair.Key == null)
                    s_Dead.Add(pair.Key);
            }
            foreach (var dead in s_Dead)
                s_Ids.Remove(dead);
            s_Dead.Clear();

            id = s_Next++;
            s_Ids.Add(camera, id);
        }
        return id;
    }
}

static class PLOceanPassShaderIDs
{
    public static readonly int DepthBuffer = Shader.PropertyToID("_PLOceanDepthBuffer");
    public static readonly int Stencil = Shader.PropertyToID("_PLOceanStencil");
    public static readonly int SceneColorCopy = Shader.PropertyToID("_PLOceanSceneColorCopy");
    public static readonly int VolumeTex = Shader.PropertyToID("_PLOceanVolumeTex");
    public static readonly int VolumeHistory = Shader.PropertyToID("_PLOceanVolumeHistory");
    public static readonly int VolumeSize = Shader.PropertyToID("_PLOceanVolumeSize");
    public static readonly int VolumeParams = Shader.PropertyToID("_PLOceanVolumeParams");
    public static readonly int VolumeOwner = Shader.PropertyToID("_PLOceanVolumeOwner");
    public static readonly int SeaLevel = Shader.PropertyToID("_PLOceanSeaLevel");

    public static bool ShouldRender(Camera camera)
        => camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView;

    public static RTHandle AllocScreenRT(Vector2 scale, string name)
        => RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: GraphicsFormat.R16G16B16A16_SFloat, filterMode: FilterMode.Bilinear,
            wrapMode: TextureWrapMode.Clamp, useDynamicScale: true, name: name);
}

/// <summary>
/// 低分辨率水体体积散射 + 时域降噪（参考 HPWater 的 HPWaterVolume）。
/// 注入点 BeforeTransparent：此时深度/法线/模板里已有水面（TransparentDepthPrepass 写入），
/// 在半分辨率下沿折射光线步进算散射，再用上一帧结果重投影混合；水面 Forward Pass 做深度感知上采样。
/// </summary>
[System.Serializable]
public class PLOceanVolumePass : CustomPass
{
    public Material seaMaterial;
    [Range(0f, 0.97f)] public float temporalWeight = 0.85f;

    class History
    {
        public RTHandle a;
        public RTHandle b;
        public bool flip;
        public int lastFrame = -10;
        public Vector2Int size;
    }

    readonly Dictionary<Camera, History> _histories = new Dictionary<Camera, History>();
    readonly List<Camera> _deadCameras = new List<Camera>();

    protected override bool executeInSceneView => true;

    protected override void Execute(CustomPassContext ctx)
    {
        var camera = ctx.hdCamera.camera;
        if (seaMaterial == null || !PLOceanPassShaderIDs.ShouldRender(camera))
            return;
        int pass = seaMaterial.FindPass("PLOceanVolume");
        if (pass < 0)
            return;

        if (!_histories.TryGetValue(camera, out var history))
        {
            // 新相机出现时顺便释放已销毁相机的历史（Scene 视图、预览相机反复开关会留下 RTHandle）
            foreach (var pair in _histories)
            {
                if (pair.Key == null)
                    _deadCameras.Add(pair.Key);
            }
            foreach (var dead in _deadCameras)
            {
                RTHandles.Release(_histories[dead].a);
                RTHandles.Release(_histories[dead].b);
                _histories.Remove(dead);
            }
            _deadCameras.Clear();

            history = new History
            {
                a = PLOceanPassShaderIDs.AllocScreenRT(Vector2.one * 0.5f, "PLOceanVolumeA"),
                b = PLOceanPassShaderIDs.AllocScreenRT(Vector2.one * 0.5f, "PLOceanVolumeB"),
            };
            _histories.Add(camera, history);
        }

        var size = new Vector2Int((ctx.hdCamera.actualWidth + 1) / 2, (ctx.hdCamera.actualHeight + 1) / 2);
        bool historyValid = history.lastFrame == Time.renderedFrameCount - 1 && history.size == size;
        history.lastFrame = Time.renderedFrameCount;
        history.size = size;

        var current = history.flip ? history.a : history.b;
        var previous = history.flip ? history.b : history.a;
        history.flip = !history.flip;

        var cmd = ctx.cmd;
        cmd.SetGlobalTexture(PLOceanPassShaderIDs.DepthBuffer, ctx.cameraDepthBuffer);
        cmd.SetGlobalTexture(PLOceanPassShaderIDs.Stencil, ctx.cameraDepthBuffer, RenderTextureSubElement.Stencil);

        var sizeVector = new Vector4(size.x, size.y, 1f / size.x, 1f / size.y);
        ctx.propertyBlock.SetTexture(PLOceanPassShaderIDs.VolumeHistory, previous);
        ctx.propertyBlock.SetVector(PLOceanPassShaderIDs.VolumeSize, sizeVector);
        ctx.propertyBlock.SetVector(PLOceanPassShaderIDs.VolumeParams, new Vector4(historyValid ? 1f : 0f, temporalWeight, 0f, 0f));

        CoreUtils.SetRenderTarget(cmd, current, ClearFlag.Color, Color.clear);
        CoreUtils.DrawFullScreen(cmd, seaMaterial, ctx.propertyBlock, pass);

        cmd.SetGlobalTexture(PLOceanPassShaderIDs.VolumeTex, current);
        cmd.SetGlobalVector(PLOceanPassShaderIDs.VolumeSize, sizeVector);
        cmd.SetGlobalFloat(PLOceanPassShaderIDs.VolumeOwner, PLOceanCameraIds.Get(camera));
    }

    protected override void Cleanup()
    {
        foreach (var h in _histories.Values)
        {
            RTHandles.Release(h.a);
            RTHandles.Release(h.b);
        }
        _histories.Clear();
    }
}

/// <summary>
/// 水下视角：相机在水面以下时，对水线以下的像素做水体雾、光柱和水底焦散。
/// 注入点 BeforePostProcess：水面背面已经画进颜色和深度缓冲。
/// </summary>
[System.Serializable]
public class PLOceanUnderseaPass : CustomPass
{
    public Material seaMaterial;
    [Tooltip("相机高于水面这么多米时跳过（大于最大浪高即可）")]
    public float maxWaveHeight = 6f;

    RTHandle _colorCopy;

    protected override bool executeInSceneView => true;

    protected override void Execute(CustomPassContext ctx)
    {
        var camera = ctx.hdCamera.camera;
        if (seaMaterial == null || !PLOceanPassShaderIDs.ShouldRender(camera))
            return;
        // 粗过滤：相机比最大浪高还高时跳过；精确的吃水线由 shader 在近裁剪面上逐像素判断。
        // 海平面要扣掉相机正下方的曲率下沉，否则离原点几公里后相机已经在水下了这里还以为在水上
        var cameraPos = camera.transform.position;
        float seaLevel = Shader.GetGlobalFloat(PLOceanPassShaderIDs.SeaLevel) - SeaMesh.PlanetDrop(cameraPos.x, cameraPos.z);
        if (cameraPos.y > seaLevel + maxWaveHeight)
            return;
        int pass = seaMaterial.FindPass("PLOceanUndersea");
        if (pass < 0)
            return;

        if (_colorCopy == null)
            _colorCopy = PLOceanPassShaderIDs.AllocScreenRT(Vector2.one, "PLOceanSceneColorCopy");

        CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, _colorCopy);

        var cmd = ctx.cmd;
        cmd.SetGlobalTexture(PLOceanPassShaderIDs.DepthBuffer, ctx.cameraDepthBuffer);
        cmd.SetGlobalTexture(PLOceanPassShaderIDs.Stencil, ctx.cameraDepthBuffer, RenderTextureSubElement.Stencil);
        ctx.propertyBlock.SetTexture(PLOceanPassShaderIDs.SceneColorCopy, _colorCopy);

        CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
        CoreUtils.DrawFullScreen(cmd, seaMaterial, ctx.propertyBlock, pass);
    }

    protected override void Cleanup()
    {
        if (_colorCopy != null)
            RTHandles.Release(_colorCopy);
        _colorCopy = null;
    }
}
