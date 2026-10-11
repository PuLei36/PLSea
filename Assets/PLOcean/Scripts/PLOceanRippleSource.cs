using UnityEngine;

/// <summary>
/// 交互水波的波源：挂到会碰水的物体上（船、角色、漂浮物）。
/// 物体穿过水面时按水平移动速度和竖直速度推波，静止不动时不产生波纹。
/// </summary>
[ExecuteAlways]
public class PLOceanRippleSource : MonoBehaviour
{
    [Tooltip("波源半径（米）")]
    [Min(0.01f)] public float radius = 0.5f;
    [Tooltip("物体在水面上下多少米以内才算接触水面")]
    [Min(0f)] public float contactRange = 0.6f;
    [Tooltip("每 1 m/s 的移动速度推多少米/秒的水")]
    public float strength = -1.5f;
    [Tooltip("始终产生的波（米/秒），用于常驻的振动源，0 关闭")]
    public float constant = 0f;
    [Tooltip("常驻波源的振动频率（Hz），0 为不振动的恒定推力")]
    [Min(0f)] public float frequency = 0f;

    Vector3 _lastPosition;
    Vector3 _velocity;
    bool _hasLast;
    int _lastFrame = -1;

    void OnEnable()
    {
        _hasLast = false;
        PLOceanRipple.Register(this);
    }

    void OnDisable() => PLOceanRipple.Unregister(this);

    /// <summary>xy = 世界 XZ，z = 半径，w = 每秒推高的米数。</summary>
    public bool TryGetSource(float seaLevel, float stepTime, out Vector4 source)
    {
        source = default;
        var p = transform.position;
        // 一帧可能推进多个模拟步，速度每帧只按帧间位移估算一次
        if (Time.frameCount != _lastFrame)
        {
            _lastFrame = Time.frameCount;
            float dt = Mathf.Max(Time.deltaTime, stepTime);
            _velocity = _hasLast ? (p - _lastPosition) / dt : Vector3.zero;
            _lastPosition = p;
            _hasLast = true;
        }
        var velocity = _velocity;

        if (Mathf.Abs(p.y - seaLevel) > contactRange)
            return false;

        float speed = new Vector2(velocity.x, velocity.z).magnitude + Mathf.Abs(velocity.y) * 2f;
        float time = Time.time;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            time = (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
        float wave = frequency > 0f ? Mathf.Sin(time * frequency * 2f * Mathf.PI) : 1f;
        float push = Mathf.Min(speed, 10f) * strength + constant * wave;
        if (Mathf.Abs(push) < 1e-4f)
            return false;
        source = new Vector4(p.x, p.z, radius, push);
        return true;
    }
}
