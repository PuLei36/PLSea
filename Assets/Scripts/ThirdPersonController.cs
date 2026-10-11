using Unity.Collections;
using UnityEngine;

/// <summary>
/// 第三人称角色控制：WASD 相对相机方向移动，Shift 跑步，空格跳跃。
/// 地面上的位移来自动画的 Root Motion（OnAnimatorMove），转向由脚本控制，避免滑步。
/// 跳跃是物理抛物线：起跳时给一个竖直速度，空中保持起跳时的水平速度，不用动画的根运动。
/// Animator 参数：Speed（0 = Idle，1 = Walk，2 = Run），AnimSpeed（播放倍率），Grounded，Jump（触发），
/// FallSpeed（下落速度 / 起跳速度，掉得深时从起跳动作切到往下跳的动作）。
/// 水：水深没到脖子以下照常在水底行走；超过 floatDepth 就浮起来，身体跟着浪起伏，水平方向游动。
/// 站在船（带刚体的碰撞体）上时跟着船一起移动、转向。
/// 角色与刚体之间的物理接触被忽略：CharacterController 内部是个无限质量的运动学体，
/// 不忽略的话人在舱里撞到船舷就会把船推走。站立、挡墙靠的是 Move 的扫掠，不受影响。
/// Mount / Dismount：驾驶载具时角色固定在驾驶位上，不再响应移动输入。
/// </summary>
[RequireComponent(typeof(CharacterController), typeof(Animator))]
public class ThirdPersonController : MonoBehaviour
{
    [Header("移动")]
    [SerializeField] float turnSpeed = 10f;
    [SerializeField] float speedDamp = 0.12f;
    [SerializeField] float gravity = -20f;

    [Header("跳跃")]
    [Tooltip("起跳高度（米，按角色缩放 1 计算，放大后跳得更高）")]
    [Min(0f)] [SerializeField] float jumpHeight = 1.0f;
    [Tooltip("空中转向/改变方向的能力，0 = 完全保持起跳时的速度")]
    [Range(0f, 1f)] [SerializeField] float airControl = 0.3f;
    [Tooltip("离地多久后才算腾空（秒），避免走下小台阶时播放跳跃动画")]
    [Min(0f)] [SerializeField] float coyoteTime = 0.15f;

    [Header("跑步")]
    [Tooltip("跑步速度（米/秒，缩放 1 时）。Run 片段是原地跑，前进速度由脚本补，默认值与动画步频匹配，不会滑步")]
    [Min(0f)] [SerializeField] float runSpeed = 4.8f;

    [Header("水")]
    [Tooltip("水面到脚底的距离超过 角色身高 × 此比例 时开始漂浮（脖子大约在 0.85）")]
    [Range(0.3f, 1f)] [SerializeField] float floatDepth = 0.8f;
    [Tooltip("回到行走状态的回差（占身高比例），避免在临界水深来回切换")]
    [Range(0f, 0.3f)] [SerializeField] float floatHysteresis = 0.06f;
    [Tooltip("漂浮时跟随水面的弹簧频率（Hz），越大越贴着浪")]
    [Min(0.1f)] [SerializeField] float bobFrequency = 1.2f;
    [Tooltip("漂浮时的弹簧阻尼比")]
    [Range(0f, 2f)] [SerializeField] float bobDamping = 0.7f;
    [Tooltip("游动时根运动速度的倍率")]
    [Range(0f, 1f)] [SerializeField] float swimSpeedScale = 0.45f;
    [Tooltip("入水时的竖直速度保留比例（跳/掉进深水会先沉一下再浮上来）")]
    [Range(0f, 1f)] [SerializeField] float seaEntryDamping = 0.3f;

    [Header("船上")]
    [Tooltip("站在甲板上时身体跟随甲板倾斜的比例（人会本能地保持平衡，所以不是 1）")]
    [Range(0f, 1f)] [SerializeField] float deckTilt = 0.6f;
    [Tooltip("身体倾斜跟随的平滑速度，越大越贴着甲板")]
    [Min(0.1f)] [SerializeField] float tiltSmoothing = 6f;
    [Tooltip("脚离开甲板不超过这个距离（米，缩放 1 时）就直接贴回甲板，船快速下落时不会反复腾空")]
    [Min(0f)] [SerializeField] float deckSnapDistance = 0.5f;

    static readonly int SpeedId = Animator.StringToHash("Speed");
    static readonly int AnimSpeedId = Animator.StringToHash("AnimSpeed");
    static readonly int GroundedId = Animator.StringToHash("Grounded");
    static readonly int JumpId = Animator.StringToHash("Jump");
    // 下落速度 / 起跳速度：普通跳跃落地时约为 1，超过 1.4（比起跳点低约 1 米）就切到往下跳的动作
    static readonly int FallSpeedId = Animator.StringToHash("FallSpeed");

    CharacterController controller;
    Animator animator;
    Transform cam;
    float verticalVelocity;

    // 空中状态：起跳或走下边缘后保持的水平速度
    bool airborne;
    Vector3 airVelocity;
    Vector3 groundVelocity;   // 上一帧在地面上的水平速度，起跳/离地时继承
    float ungroundedTime;
    bool jumpQueued;
    Vector3 desiredDir;

    bool floating;
    float seaHeight;
    bool hasSea;

    // 脚下的移动平台（船）
    Transform platform;
    Vector3 platformLocalPos;
    float platformLastYaw;
    Transform groundHit;
    // 本帧是否站在地面上。controller.isGrounded 只反映最后一次 Move，船上会额外修正位置，所以自己记
    bool grounded;

    // 驾驶位（为空表示自由行走）
    Transform seat;

    // 叠加在偏航朝向上的倾斜（跟随甲板摇晃）。CharacterController 的胶囊永远竖直，倾斜只影响外观
    Quaternion tilt = Quaternion.identity;

    // 物理线程回调里只能用实例 ID，不能访问 Unity 对象
    static int s_ColliderId;

    /// <summary>当前是否浮在水里</summary>
    public bool IsFloating => floating;

    /// <summary>当前是否在驾驶载具</summary>
    public bool IsMounted => seat != null;

    float Height => controller.height * transform.lossyScale.y;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        animator.applyRootMotion = true;
    }

    void OnEnable()
    {
        s_ColliderId = controller.GetInstanceID();
        controller.hasModifiableContacts = true;
        Physics.ContactModifyEvent += IgnoreBodyContacts;
        Physics.ContactModifyEventCCD += IgnoreBodyContacts;
    }

    // 只给 CharacterController 打开 hasModifiableContacts 时 PhysX 不会把接触对交给回调，
    // 必须由刚体一侧的碰撞体打开。每个物理步把身边带刚体的碰撞体都标上
    readonly Collider[] nearby = new Collider[32];

    void FixedUpdate()
    {
        if (!controller.enabled)
            return;
        float scale = transform.lossyScale.y;
        float radius = controller.radius * scale + 0.5f;
        Vector3 center = transform.TransformPoint(controller.center);
        Vector3 half = Vector3.up * Mathf.Max(controller.height * 0.5f * scale - radius, 0f);
        int n = Physics.OverlapCapsuleNonAlloc(center - half, center + half, radius, nearby, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            if (nearby[i].attachedRigidbody != null && !nearby[i].hasModifiableContacts)
                nearby[i].hasModifiableContacts = true;
        }
    }

    void OnDisable()
    {
        Physics.ContactModifyEvent -= IgnoreBodyContacts;
        Physics.ContactModifyEventCCD -= IgnoreBodyContacts;
    }

    static void IgnoreBodyContacts(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
    {
        for (int p = 0; p < pairs.Length; p++)
        {
            var pair = pairs[p];
            if (pair.colliderInstanceID != s_ColliderId && pair.otherColliderInstanceID != s_ColliderId)
                continue;
            for (int i = 0; i < pair.contactCount; i++)
                pair.IgnoreContact(i);
        }
    }

    /// <summary>进入驾驶位：角色贴在 seatPoint 上，朝向跟随它</summary>
    public void Mount(Transform seatPoint)
    {
        seat = seatPoint;
        controller.enabled = false;
        animator.applyRootMotion = false;
        floating = false;
        airborne = false;
        jumpQueued = false;
        animator.SetBool(GroundedId, true);
        verticalVelocity = 0f;
        platform = null;
        SnapToSeat();
    }

    /// <summary>离开驾驶位，原地站起，继续跟着船走</summary>
    public void Dismount()
    {
        if (seat == null)
            return;
        SnapToSeat();
        var rb = seat.GetComponentInParent<Rigidbody>();
        seat = null;
        animator.applyRootMotion = true;
        controller.enabled = true;
        platform = rb != null ? rb.transform : null;
        if (platform != null)
        {
            platformLastYaw = platform.eulerAngles.y;
            platformLocalPos = platform.InverseTransformPoint(transform.position);
        }
    }

    // 驾驶时扶着控制台，身体完全跟着船倾斜
    void SnapToSeat()
    {
        tilt = Quaternion.FromToRotation(Vector3.up, seat.up);
        transform.SetPositionAndRotation(seat.position, seat.rotation);
    }

    // 去掉倾斜后的纯偏航朝向
    Quaternion YawRotation()
    {
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        return forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward) : Quaternion.identity;
    }

    void ApplyTilt(float dt)
    {
        Vector3 up = platform != null ? Vector3.Slerp(Vector3.up, platform.up, deckTilt) : Vector3.up;
        Quaternion target = Quaternion.FromToRotation(Vector3.up, up);
        tilt = Quaternion.Slerp(tilt, target, 1f - Mathf.Exp(-tiltSmoothing * dt));
        transform.rotation = tilt * transform.rotation;
    }

    void Start()
    {
        if (Camera.main != null)
            cam = Camera.main.transform;
    }

    void Update()
    {
        if (seat != null)
        {
            animator.SetFloat(SpeedId, 0f, speedDamp, Time.deltaTime);
            animator.SetFloat(AnimSpeedId, 1f, speedDamp, Time.deltaTime);
            return;
        }

        var input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        input = Vector3.ClampMagnitude(input, 1f);

        Vector3 moveDir = input;
        if (cam != null)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
            moveDir = forward * input.z + right * input.x;
        }

        desiredDir = moveDir.sqrMagnitude > 0.01f ? moveDir.normalized : Vector3.zero;
        if (moveDir.sqrMagnitude > 0.01f)
        {
            Quaternion target = Quaternion.LookRotation(moveDir);
            float turn = airborne ? turnSpeed * airControl : turnSpeed;
            transform.rotation = Quaternion.Slerp(transform.rotation, target, turn * Time.deltaTime);
        }

        // Speed: 0 = Idle，1 = Walk，2 = Run
        bool sprint = Input.GetKey(KeyCode.LeftShift) && !floating;
        animator.SetFloat(SpeedId, input.magnitude * (sprint ? 2f : 1f), speedDamp, Time.deltaTime);
        animator.SetFloat(AnimSpeedId, 1f);

        if (Input.GetKeyDown(KeyCode.Space))
            jumpQueued = true;
    }

    void OnAnimatorMove()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;
        if (seat != null)
        {
            SnapToSeat();
            return;
        }

        // 先去掉上一帧叠加的倾斜，后面的转向、位移都在竖直朝向下计算
        transform.rotation = YawRotation();
        Vector3 platformDelta = FollowPlatform();
        UpdateSeaState();
        // 跳进深水：直接转为漂浮
        if (floating && airborne)
            Land();

        float scale = transform.lossyScale.y;

        // 只取根运动在朝向上的分量，去掉动捕片段里的横向漂移。
        // Run 片段是原地跑，按它在 Blend Tree 里的权重（Speed 1→2）补上前进速度
        Vector3 delta = transform.forward * Vector3.Dot(animator.deltaPosition, transform.forward);
        float runWeight = Mathf.Clamp01(animator.GetFloat(SpeedId) - 1f);
        delta += transform.forward * (runWeight * runSpeed * scale * dt);

        // 起跳：只能在地面上（含刚离地的宽限时间内）
        if (jumpQueued)
        {
            jumpQueued = false;
            if (!airborne && !floating)
            {
                airborne = true;
                verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight * scale);
                airVelocity = groundVelocity;
                animator.SetTrigger(JumpId);
                animator.SetBool(GroundedId, false);
            }
        }

        if (airborne)
        {
            // 空中不用根运动：保持起跳时的水平速度，按 airControl 朝输入方向微调
            float speed = Mathf.Max(airVelocity.magnitude, desiredDir == Vector3.zero ? 0f : 1.3f * scale);
            Vector3 wanted = desiredDir * speed;
            airVelocity = Vector3.MoveTowards(airVelocity, wanted, airControl * 10f * scale * dt);
            delta = airVelocity * dt;
            verticalVelocity += gravity * dt;
            float takeoff = Mathf.Sqrt(2f * -gravity * Mathf.Max(jumpHeight, 0.01f) * scale);
            animator.SetFloat(FallSpeedId, -verticalVelocity / takeoff);
        }
        else if (floating)
        {
            delta *= swimSpeedScale;
            // 阻尼弹簧把身体拉到“水面在脖子处”的高度，浪起浪落跟着走
            float targetY = seaHeight - floatDepth * Height;
            float omega = 2f * Mathf.PI * bobFrequency;
            float accel = omega * omega * (targetY - transform.position.y) - 2f * bobDamping * omega * verticalVelocity;
            verticalVelocity += accel * dt;
        }
        else
        {
            if (grounded && verticalVelocity < 0f)
                verticalVelocity = -2f;
            verticalVelocity += gravity * dt;
        }

        delta.y = verticalVelocity * dt;
        groundHit = null;
        Vector3 before = transform.position;
        var flags = controller.Move(delta + platformDelta);
        grounded = (flags & CollisionFlags.Below) != 0;
        // 甲板倾斜、上下起伏时胶囊会被斜面法线挤向低处，站着不动也会一路滑出船外。
        // 没撞到墙就把水平位置拉回“跟着船 + 自己走”的预期位置
        // （这次 Move 只有水平分量，不能用它的结果判断是否着地）
        if (platform != null && !airborne && (flags & CollisionFlags.Sides) == 0)
        {
            Vector3 drift = before + platformDelta + delta - transform.position;
            drift.y = 0f;
            if (drift.sqrMagnitude > 1e-8f)
                controller.Move(drift);
        }
        // 船往下落得比重力快时脚会离开甲板，甲板就在脚下不远处时直接贴回去，不进入腾空
        if (platform != null && !airborne && !floating && !grounded)
            grounded = SnapToDeck();
        if (grounded && platform != null && verticalVelocity < 0f)
            verticalVelocity = -2f;

        // 浮在水里被海床托住（往岸上游）时不要积累向下的速度
        if (floating && grounded && verticalVelocity < 0f)
            verticalVelocity = 0f;

        UpdateAirState(transform.position - before, dt);

        UpdatePlatform();
        ApplyTilt(dt);
    }

    // 落地检测；在地面上时记录水平速度，走下边缘超过 coyoteTime 才转为腾空
    void UpdateAirState(Vector3 moved, float dt)
    {
        if (floating)
        {
            ungroundedTime = 0f;
            return;
        }

        if (airborne)
        {
            if (grounded && verticalVelocity <= 0f)
                Land();
            return;
        }

        if (grounded)
        {
            ungroundedTime = 0f;
            // 用实际位移（被墙挡住就是 0），并且包含平台的速度，从船上起跳时不会被船甩在后面
            groundVelocity = new Vector3(moved.x, 0f, moved.z) / dt;
        }
        else if ((ungroundedTime += dt) > coyoteTime)
        {
            airborne = true;
            airVelocity = groundVelocity;
            animator.SetBool(GroundedId, false);
        }
    }

    void Land()
    {
        airborne = false;
        ungroundedTime = 0f;
        animator.SetBool(GroundedId, true);
        animator.SetFloat(FallSpeedId, 0f);
    }

    void UpdateSeaState()
    {
        hasSea = PLOceanSimulation.TrySampleHeight(transform.position.x, transform.position.z, out seaHeight);
        // 站在船舱里时脚可能略低于海面，但人不在水里
        if (!hasSea || platform != null || PLOceanBoat.IsInsideAnyHull(transform.position + Vector3.up * 0.1f))
        {
            floating = false;
            return;
        }

        float depth = seaHeight - transform.position.y;
        float enter = floatDepth * Height;
        float exit = (floatDepth - floatHysteresis) * Height;
        if (!floating && depth > enter)
        {
            floating = true;
            verticalVelocity *= seaEntryDamping;
        }
        else if (floating && depth < exit)
        {
            floating = false;
        }
    }

    // ------------------------------------------------------------------
    // 移动平台：记住角色在平台本地空间里的位置，平台动了就把差值补上
    // ------------------------------------------------------------------

    Vector3 FollowPlatform()
    {
        if (platform == null)
            return Vector3.zero;
        Vector3 target = platform.TransformPoint(platformLocalPos);
        float yaw = platform.eulerAngles.y;
        transform.Rotate(0f, Mathf.DeltaAngle(platformLastYaw, yaw), 0f, Space.World);
        platformLastYaw = yaw;
        return target - transform.position;
    }

    // 往下扫一段，碰到的是当前平台就贴上去
    bool SnapToDeck()
    {
        float scale = transform.lossyScale.y;
        float radius = controller.radius * scale;
        Vector3 center = transform.TransformPoint(controller.center);
        Vector3 bottom = center + Vector3.down * Mathf.Max(controller.height * 0.5f * scale - radius, 0f);
        float reach = deckSnapDistance * scale + controller.skinWidth;
        // 起点稍微抬高一点，避免球体一开始就和甲板重叠导致扫不到
        const float lift = 0.05f;
        if (!Physics.SphereCast(bottom + Vector3.up * lift, radius * 0.95f, Vector3.down, out var hit,
                                reach + lift, ~0, QueryTriggerInteraction.Ignore))
            return false;
        if (hit.normal.y < 0.5f || hit.rigidbody == null || hit.rigidbody.transform != platform)
            return false;
        var flags = controller.Move(Vector3.down * (hit.distance - lift + controller.skinWidth));
        groundHit = hit.transform;
        return (flags & CollisionFlags.Below) != 0;
    }

    void UpdatePlatform()
    {
        Transform next = null;
        if (grounded && groundHit != null)
        {
            var rb = groundHit.GetComponentInParent<Rigidbody>();
            if (rb != null)
                next = rb.transform;
        }
        // 船往下沉得比重力快时会短暂离开甲板，宽限时间内仍然跟着船走，不被甩出去
        if (next == null && !airborne && !floating)
            next = platform;
        if (next != platform && next != null)
            platformLastYaw = next.eulerAngles.y;
        platform = next;
        if (platform != null)
            platformLocalPos = platform.InverseTransformPoint(transform.position);
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // 只认脚下的面
        if (hit.normal.y > 0.5f)
            groundHit = hit.collider.transform;
    }
}
