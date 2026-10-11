using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 人物涉水：不把整个俯视轮廓当成挤水的障碍物，而是沿骨骼链（双腿、双臂、躯干）找出穿过水面的点，
/// 在那些点上推水（前方推高、后方下陷的偶极子），手脚出入水面时再补一下溅水。
/// 水深到腰时腿整条在水下，交点自然落在躯干上；手垂进水里时手臂也会划出波纹。
/// 挂在带 Humanoid Animator 的角色上；非 Humanoid 退化为在自身位置产生波源。
/// </summary>
[DisallowMultipleComponent]
public class HPOceanWader : MonoBehaviour, HPOceanRipple.IEmitter
{
    [Header("波源半径（米）")]
    [Min(0.01f)] public float legRadius = 0.25f;
    [Min(0.01f)] public float armRadius = 0.15f;
    [Min(0.01f)] public float bodyRadius = 0.35f;

    [Header("强度")]
    [Tooltip("每 1 m/s 的移动速度推多少米/秒的水")]
    [Min(0f)] public float pushStrength = 0.6f;
    [Tooltip("推水速度上限（m/s），避免快速动作炸出一大片")]
    [Min(0.1f)] public float maxSpeed = 2f;
    [Tooltip("速度平滑时间（秒），过滤动画骨骼的抖动")]
    [Min(0f)] public float velocitySmoothing = 0.08f;
    [Tooltip("站着不动时身体边轻微的水波（米/秒），0 关闭")]
    [Min(0f)] public float idleStrength = 0.08f;
    [Tooltip("手脚进出水面时溅起的波高（米/每 1 m/s 竖直速度）")]
    [Min(0f)] public float splashStrength = 0.012f;
    [Min(0.01f)] public float splashRadius = 0.3f;

    // 一条骨骼链：从根到末端，找第一个跨过水面的线段
    class Chain
    {
        public Transform[] bones;
        public float radius;
        public bool splashAtEnd;   // 末端（手/脚）穿过水面时溅水
        public Vector3 lastPoint;
        public Vector2 velocity;
        public bool hadPoint;
        public float lastEndY;
        public bool hasEndY;
    }

    readonly List<Vector4> _sources = new List<Vector4>(16);
    readonly List<Chain> _chains = new List<Chain>();
    Renderer[] _renderers;
    Vector3 _lastPosition;
    Vector2 _rootVelocity;
    bool _hasLast;

    void OnEnable()
    {
        BuildChains();

        // 角色自身不参与“轮廓挤水”，也不当反射墙，避免身体下面挖出一块暗坑
        _renderers = GetComponentsInChildren<Renderer>();
        foreach (var r in _renderers)
            HPOceanRipple.ExcludeRenderer(r);
        HPOceanRipple.RegisterEmitter(this);
        _hasLast = false;
    }

    void OnDisable()
    {
        HPOceanRipple.UnregisterEmitter(this);
        if (_renderers != null)
            foreach (var r in _renderers)
                HPOceanRipple.IncludeRenderer(r);
        _sources.Clear();
    }

    void BuildChains()
    {
        _chains.Clear();
        var a = GetComponentInChildren<Animator>();
        if (a == null || !a.isHuman)
            return;

        AddChain(a, legRadius, true, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
        AddChain(a, legRadius, true, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
        AddChain(a, armRadius, true, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        AddChain(a, armRadius, true, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        AddChain(a, bodyRadius, false, HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
                 HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head);
    }

    void AddChain(Animator a, float radius, bool splashAtEnd, params HumanBodyBones[] ids)
    {
        // 可选骨骼（UpperChest、Neck 等）可能没有，跳过
        var bones = new List<Transform>(ids.Length);
        foreach (var id in ids)
        {
            var t = a.GetBoneTransform(id);
            if (t != null)
                bones.Add(t);
        }
        if (bones.Count >= 2)
            _chains.Add(new Chain { bones = bones.ToArray(), radius = radius, splashAtEnd = splashAtEnd });
    }
    // 动画在 LateUpdate 之前已经写好骨骼，这里算好本帧的波源，渲染前交给模拟
    void LateUpdate()
    {
        _sources.Clear();
        var ripple = HPOceanRipple.Active;
        if (ripple == null)
            return;
        float waterLevel = ripple.transform.position.y;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float blend = velocitySmoothing > 0f ? 1f - Mathf.Exp(-dt / velocitySmoothing) : 1f;
        float idle = idleStrength * Mathf.Sin(Time.time * 2f * Mathf.PI * 0.7f);

        if (_chains.Count == 0)
        {
            var p = transform.position;
            var v = _hasLast ? Flat(p - _lastPosition) / dt : Vector2.zero;
            _rootVelocity = Vector2.Lerp(_rootVelocity, v, blend);
            _lastPosition = p;
            _hasLast = true;
            AddDipole(p, _rootVelocity, bodyRadius, idle);
            return;
        }

        foreach (var c in _chains)
        {
            bool found = FindCrossing(c.bones, waterLevel, out var point);
            if (found)
            {
                var v = c.hadPoint ? Flat(point - c.lastPoint) / dt : Vector2.zero;
                c.velocity = c.hadPoint ? Vector2.Lerp(c.velocity, v, blend) : Vector2.zero;
                AddDipole(point, c.velocity, c.radius, idle);
                c.lastPoint = point;
            }
            c.hadPoint = found;

            if (!c.splashAtEnd)
                continue;
            // 手脚穿过水面时溅一下，入水下压、出水上提
            var end = c.bones[c.bones.Length - 1];
            float endY = end.position.y;
            if (c.hasEndY && (c.lastEndY - waterLevel) * (endY - waterLevel) < 0f)
            {
                float vy = (endY - c.lastEndY) / dt;
                HPOceanRipple.AddImpulse(end.position, splashRadius, Mathf.Clamp(vy, -3f, 3f) * splashStrength);
            }
            c.lastEndY = endY;
            c.hasEndY = true;
        }
    }

    static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

    static bool FindCrossing(Transform[] bones, float level, out Vector3 point)
    {
        for (int i = 0; i + 1 < bones.Length; i++)
        {
            if (WaterlineCrossing(bones[i].position, bones[i + 1].position, level, out point))
                return true;
        }
        point = default;
        return false;
    }

    static bool WaterlineCrossing(Vector3 a, Vector3 b, float level, out Vector3 point)
    {
        point = default;
        if ((a.y - level) * (b.y - level) > 0f || Mathf.Approximately(a.y, b.y))
            return false;
        point = Vector3.Lerp(a, b, (level - a.y) / (b.y - a.y));
        return true;
    }

    // 往前推：前方水被推高，后方留下低洼，比单个下压的波源更像真的在蹚水
    void AddDipole(Vector3 p, Vector2 v, float radius, float idle)
    {
        float speed = Mathf.Min(v.magnitude, maxSpeed);
        if (speed > 0.05f)
        {
            var dir = v / v.magnitude * (radius * 0.6f);
            float push = speed * pushStrength;
            _sources.Add(new Vector4(p.x + dir.x, p.z + dir.y, radius, push));
            _sources.Add(new Vector4(p.x - dir.x, p.z - dir.y, radius, -push));
        }
        else if (Mathf.Abs(idle) > 1e-4f)
        {
            _sources.Add(new Vector4(p.x, p.z, radius, idle));
        }
    }

    public void GetSources(List<Vector4> output) => output.AddRange(_sources);
}
