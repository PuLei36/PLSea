using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// 给每个相机分配一个从 1 开始的小整数编号，写进 shader 判断体积缓冲是不是属于当前相机。
/// </summary>
public static class HPOceanCameraIds
{
    static readonly Dictionary<Camera, int> s_Ids = new Dictionary<Camera, int>();

    public static int Get(Camera camera)
    {
        if (!s_Ids.TryGetValue(camera, out int id))
        {
            id = s_Ids.Count + 1;
            s_Ids.Add(camera, id);
        }
        return id;
    }
}

static class HPOceanPassShaderIDs
{
    public static readonly int DepthBuffer = Shader.PropertyToID("_HPOceanDepthBuffer");
    public static readonly int Stencil = Shader.PropertyToID("_HPOceanStencil");
    public static readonly int SceneColorCopy = Shader.PropertyToID("_HPOceanSceneColorCopy");
    public static readonly int VolumeTex = Shader.PropertyToID("_HPOceanVolumeTex");
    public static readonly int VolumeHistory = Shader.PropertyToID("_HPOceanVolumeHistory");
    public static readonly int VolumeSize = Shader.PropertyToID("_HPOceanVolumeSize");
    public static readonly int VolumeParams = Shader.PropertyToID("_HPOceanVolumeParams");
    public static readonly int VolumeOwner = Shader.PropertyToID("_HPOceanVolumeOwner");
    public static readonly int WaterLevel = Shader.PropertyToID("_HPOceanWaterLevel");

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
public class HPOceanVolumePass : CustomPass
{
    public Material waterMaterial;
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

    protected override bool executeInSceneView => true;

    protected override void Execute(CustomPassContext ctx)
    {
        var camera = ctx.hdCamera.camera;
        if (waterMaterial == null || !HPOceanPassShaderIDs.ShouldRender(camera))
            return;
        int pass = waterMaterial.FindPass("HPOceanVolume");
        if (pass < 0)
            return;

        if (!_histories.TryGetValue(camera, out var history))
        {
            history = new History
            {
                a = HPOceanPassShaderIDs.AllocScreenRT(Vector2.one * 0.5f, "HPOceanVolumeA"),
                b = HPOceanPassShaderIDs.AllocScreenRT(Vector2.one * 0.5f, "HPOceanVolumeB"),
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
        cmd.SetGlobalTexture(HPOceanPassShaderIDs.DepthBuffer, ctx.cameraDepthBuffer);
        cmd.SetGlobalTexture(HPOceanPassShaderIDs.Stencil, ctx.cameraDepthBuffer, RenderTextureSubElement.Stencil);

        var sizeVector = new Vector4(size.x, size.y, 1f / size.x, 1f / size.y);
        ctx.propertyBlock.SetTexture(HPOceanPassShaderIDs.VolumeHistory, previous);
        ctx.propertyBlock.SetVector(HPOceanPassShaderIDs.VolumeSize, sizeVector);
        ctx.propertyBlock.SetVector(HPOceanPassShaderIDs.VolumeParams, new Vector4(historyValid ? 1f : 0f, temporalWeight, 0f, 0f));

        CoreUtils.SetRenderTarget(cmd, current, ClearFlag.Color, Color.clear);
        CoreUtils.DrawFullScreen(cmd, waterMaterial, ctx.propertyBlock, pass);

        cmd.SetGlobalTexture(HPOceanPassShaderIDs.VolumeTex, current);
        cmd.SetGlobalVector(HPOceanPassShaderIDs.VolumeSize, sizeVector);
        cmd.SetGlobalFloat(HPOceanPassShaderIDs.VolumeOwner, HPOceanCameraIds.Get(camera));
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
public class HPOceanUnderwaterPass : CustomPass
{
    public Material waterMaterial;
    [Tooltip("相机高于水面这么多米时跳过（大于最大浪高即可）")]
    public float maxWaveHeight = 6f;

    RTHandle _colorCopy;

    protected override bool executeInSceneView => true;

    protected override void Execute(CustomPassContext ctx)
    {
        var camera = ctx.hdCamera.camera;
        if (waterMaterial == null || !HPOceanPassShaderIDs.ShouldRender(camera))
            return;
        float waterLevel = Shader.GetGlobalFloat(HPOceanPassShaderIDs.WaterLevel);
        // 粗过滤：相机比最大浪高还高时跳过；精确的吃水线由 shader 在近裁剪面上逐像素判断
        if (camera.transform.position.y > waterLevel + maxWaveHeight)
            return;
        int pass = waterMaterial.FindPass("HPOceanUnderwater");
        if (pass < 0)
            return;

        if (_colorCopy == null)
            _colorCopy = HPOceanPassShaderIDs.AllocScreenRT(Vector2.one, "HPOceanSceneColorCopy");

        CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, _colorCopy);

        var cmd = ctx.cmd;
        cmd.SetGlobalTexture(HPOceanPassShaderIDs.DepthBuffer, ctx.cameraDepthBuffer);
        cmd.SetGlobalTexture(HPOceanPassShaderIDs.Stencil, ctx.cameraDepthBuffer, RenderTextureSubElement.Stencil);
        ctx.propertyBlock.SetTexture(HPOceanPassShaderIDs.SceneColorCopy, _colorCopy);

        CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
        CoreUtils.DrawFullScreen(cmd, waterMaterial, ctx.propertyBlock, pass);
    }

    protected override void Cleanup()
    {
        if (_colorCopy != null)
            RTHandles.Release(_colorCopy);
        _colorCopy = null;
    }
}
