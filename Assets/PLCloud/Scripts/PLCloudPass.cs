using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

static class PLCloudShaderIDs
{
    public static readonly int DepthBuffer = Shader.PropertyToID("_PLCloudDepthBuffer");
    public static readonly int TraceColor = Shader.PropertyToID("_PLCloudTraceColor");
    public static readonly int TraceDepth = Shader.PropertyToID("_PLCloudTraceDepth");
    public static readonly int History = Shader.PropertyToID("_PLCloudHistory");
    public static readonly int Result = Shader.PropertyToID("_PLCloudResult");
    public static readonly int TraceSize = Shader.PropertyToID("_PLCloudTraceSize");
    public static readonly int UVScale = Shader.PropertyToID("_PLCloudUVScale");
    public static readonly int Frame = Shader.PropertyToID("_PLCloudFrame");
    public static readonly int ShadowCenter = Shader.PropertyToID("_PLCloudShadowCenter");
    public static readonly int ShadowRight = Shader.PropertyToID("_PLCloudShadowRight");
    public static readonly int ShadowUp = Shader.PropertyToID("_PLCloudShadowUp");
    public static readonly int ShadowForward = Shader.PropertyToID("_PLCloudShadowForward");
    public static readonly int ShadowSize = Shader.PropertyToID("_PLCloudShadowSize");
}

/// <summary>
/// 体积云渲染（Nubis 式性能方案）：
///   Trace     1/downsample 分辨率 Ray March，每帧换抖动偏移
///   Temporal  按云深度重投影上一帧，邻域裁剪后累积
///   Composite 全分辨率上采样，按场景深度遮挡后预乘叠加到相机颜色
/// 注入点 BeforePostProcess：地形、海面都已写入颜色和深度。
/// 每帧先把云影透射率画进 PLCloudVolume 的 cookie RT（下一帧的光照使用）。
/// </summary>
[System.Serializable]
public class PLCloudPass : CustomPass
{
    [Range(1, 4)] public int downsample = 2;
    [Range(0f, 0.97f)] public float historyWeight = 0.9f;

    class CameraHistory
    {
        public RenderTexture traceColor;
        public RenderTexture traceDepth;
        public RenderTexture[] accumulation = new RenderTexture[2];
        public int current;
        public bool valid;
    }

    public static readonly ProfilingSampler Sampler = new ProfilingSampler("PLCloud");

    readonly Dictionary<Camera, CameraHistory> _histories = new Dictionary<Camera, CameraHistory>();
    readonly RenderTargetIdentifier[] _traceTargets = new RenderTargetIdentifier[2];
    int _lastShadowFrame = -1;
    int _frameIndex;

    protected override bool executeInSceneView => true;

    protected override void Execute(CustomPassContext ctx)
    {
        var camera = ctx.hdCamera.camera;
        if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
            return;
        var volume = PLCloudVolume.Active;
        if (volume == null || volume.material == null)
            return;
        var material = volume.material;
        int tracePass = material.FindPass("PLCloudTrace");
        int temporalPass = material.FindPass("PLCloudTemporal");
        int compositePass = material.FindPass("PLCloudComposite");
        if (tracePass < 0 || temporalPass < 0 || compositePass < 0)
            return;

        var cmd = ctx.cmd;
        using var scope = new ProfilingScope(cmd, Sampler);

        // 云影每帧只画一次（多个相机共用）
        var shadow = volume.ShadowTexture;
        if (volume.castShadows && volume.sun != null && shadow != null && _lastShadowFrame != Time.renderedFrameCount)
        {
            _lastShadowFrame = Time.renderedFrameCount;
            RenderShadow(cmd, volume, material, shadow);
        }

        int fullWidth = ctx.hdCamera.actualWidth;
        int fullHeight = ctx.hdCamera.actualHeight;
        int ds = Mathf.Clamp(downsample, 1, 4);
        int width = Mathf.Max(1, (fullWidth + ds - 1) / ds);
        int height = Mathf.Max(1, (fullHeight + ds - 1) / ds);
        var history = GetHistory(camera, width, height);

        // 每次渲染都轮换历史（读上一张、写另一张），同一张 RT 不会既读又写
        history.current ^= 1;
        _frameIndex++;
        var previous = history.accumulation[history.current ^ 1];
        var target = history.accumulation[history.current];

        // 低分辨率像素中心 → 全屏 uv：(pixel × ds) / fullSize
        cmd.SetGlobalVector(PLCloudShaderIDs.TraceSize, new Vector4(width, height, (float)ds / fullWidth, (float)ds / fullHeight));
        // 全屏 uv → 低分辨率 RT uv（向上取整后 RT 比屏幕略大）
        cmd.SetGlobalVector(PLCloudShaderIDs.UVScale, new Vector4((float)fullWidth / (width * ds), (float)fullHeight / (height * ds), ds, 0f));
        cmd.SetGlobalVector(PLCloudShaderIDs.Frame, new Vector4(_frameIndex, history.valid ? 1f : 0f, historyWeight, 0f));
        cmd.SetGlobalTexture(PLCloudShaderIDs.DepthBuffer, ctx.cameraDepthBuffer);

        // 1. Trace
        _traceTargets[0] = history.traceColor;
        _traceTargets[1] = history.traceDepth;
        cmd.SetRenderTarget(_traceTargets, history.traceColor.depthBuffer);
        cmd.DrawProcedural(Matrix4x4.identity, material, tracePass, MeshTopology.Triangles, 3, 1);

        // 2. Temporal
        cmd.SetGlobalTexture(PLCloudShaderIDs.TraceColor, history.traceColor);
        cmd.SetGlobalTexture(PLCloudShaderIDs.TraceDepth, history.traceDepth);
        cmd.SetGlobalTexture(PLCloudShaderIDs.History, previous);
        cmd.SetRenderTarget(target);
        cmd.DrawProcedural(Matrix4x4.identity, material, temporalPass, MeshTopology.Triangles, 3, 1);
        history.valid = true;

        // 3. Composite
        cmd.SetGlobalTexture(PLCloudShaderIDs.Result, target);
        CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
        CoreUtils.DrawFullScreen(cmd, material, ctx.propertyBlock, compositePass);
    }

    readonly List<Camera> _deadCameras = new List<Camera>();

    CameraHistory GetHistory(Camera camera, int width, int height)
    {
        if (!_histories.TryGetValue(camera, out var history))
        {
            // 新相机出现时顺便清掉已销毁相机的历史（Scene 视图、预览窗口反复开关会留下一堆）
            foreach (var pair in _histories)
            {
                if (pair.Key == null)
                    _deadCameras.Add(pair.Key);
            }
            foreach (var dead in _deadCameras)
            {
                ReleaseHistory(_histories[dead]);
                _histories.Remove(dead);
            }
            _deadCameras.Clear();

            history = new CameraHistory();
            _histories[camera] = history;
        }
        if (history.traceColor == null || history.traceColor.width != width || history.traceColor.height != height)
        {
            ReleaseHistory(history);
            history.traceColor = CreateTarget("PLCloudTraceColor", width, height, RenderTextureFormat.ARGBHalf, FilterMode.Point);
            history.traceDepth = CreateTarget("PLCloudTraceDepth", width, height, RenderTextureFormat.ARGBFloat, FilterMode.Point);
            history.accumulation[0] = CreateTarget("PLCloudHistory0", width, height, RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
            history.accumulation[1] = CreateTarget("PLCloudHistory1", width, height, RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
            history.valid = false;
        }
        return history;
    }

    static RenderTexture CreateTarget(string name, int width, int height, RenderTextureFormat format, FilterMode filter)
    {
        var rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
        {
            name = name,
            filterMode = filter,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        rt.Create();
        return rt;
    }

    static void ReleaseHistory(CameraHistory history)
    {
        Release(ref history.traceColor);
        Release(ref history.traceDepth);
        Release(ref history.accumulation[0]);
        Release(ref history.accumulation[1]);
    }

    static void Release(ref RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        Object.DestroyImmediate(rt);
        rt = null;
    }

    protected override void Cleanup()
    {
        foreach (var history in _histories.Values)
            ReleaseHistory(history);
        _histories.Clear();
    }

    static void RenderShadow(CommandBuffer cmd, PLCloudVolume volume, Material material, RenderTexture shadow)
    {
        int shadowPass = material.FindPass("PLCloudShadow");
        if (shadowPass < 0)
            return;

        // 与 HDRP 方向光 cookie 的正交投影一致：中心 = 灯的位置，right / up = 灯的局部轴 × cookie 尺寸
        var light = volume.sun.transform;
        float size = volume.ShadowFootprint;
        cmd.SetGlobalVector(PLCloudShaderIDs.ShadowCenter, new Vector4(light.position.x, light.position.y, light.position.z, size));
        cmd.SetGlobalVector(PLCloudShaderIDs.ShadowRight, light.right * size);
        cmd.SetGlobalVector(PLCloudShaderIDs.ShadowUp, light.up * size);
        Vector3 forward = light.forward;
        cmd.SetGlobalVector(PLCloudShaderIDs.ShadowForward, new Vector4(forward.x, forward.y, forward.z, volume.shadowStrength));
        cmd.SetGlobalVector(PLCloudShaderIDs.ShadowSize, new Vector4(shadow.width, shadow.height, 1f / shadow.width, 1f / shadow.height));

        cmd.SetRenderTarget(shadow);
        cmd.DrawProcedural(Matrix4x4.identity, material, shadowPass, MeshTopology.Triangles, 3, 1);
        // HDRP 的 cookie 图集按 updateCount 判断 RenderTexture 是否需要重新拷贝
        shadow.IncrementUpdateCount();
    }
}
