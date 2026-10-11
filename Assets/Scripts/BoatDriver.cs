using UnityEngine;

/// <summary>
/// 船的驾驶台：角色靠近按 F 上去驾驶，再按 F 下来。
/// 驾驶时 W/S 油门（前进 / 倒车），A/D 转舷外机。推力加在螺旋桨位置，
/// 方向随舷外机偏转，转向力矩自然来自船尾的侧向分力；螺旋桨出水时没有推力。
/// </summary>
public class BoatDriver : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("船的刚体，留空则在父物体里找")]
    [SerializeField] Rigidbody body;
    [Tooltip("驾驶位置（角色脚底），朝向即船头方向")]
    [SerializeField] Transform seatPoint;
    [Tooltip("推力作用点（螺旋桨）")]
    [SerializeField] Transform propeller;

    [Header("交互")]
    [SerializeField] KeyCode interactKey = KeyCode.F;
    [Tooltip("角色离驾驶位多近可以上去（米）")]
    [Min(0.1f)] [SerializeField] float interactRange = 3f;

    [Header("动力")]
    [Tooltip("满油门推力（牛）")]
    [Min(0f)] [SerializeField] float maxThrust = 180000f;
    [Tooltip("倒车推力占前进推力的比例")]
    [Range(0f, 1f)] [SerializeField] float reverseRatio = 0.4f;
    [Tooltip("按住 W 时油门增加速度（每秒），越小加速越慢")]
    [Min(0.01f)] [SerializeField] float throttleRate = 0.2f;
    [Tooltip("按住 Shift 时油门增加速度（每秒）")]
    [Min(0.01f)] [SerializeField] float boostThrottleRate = 0.6f;
    [Tooltip("按住 Shift 时油门上限（1 = 满油门）")]
    [Min(1f)] [SerializeField] float boostThrottle = 1.5f;
    [Tooltip("松开油门后回落速度（每秒）")]
    [Min(0.01f)] [SerializeField] float throttleReleaseRate = 0.5f;
    [Tooltip("舷外机最大偏转角（度）")]
    [Range(0f, 60f)] [SerializeField] float maxSteerAngle = 30f;
    [Tooltip("舷外机转动速度（度 / 秒）")]
    [Min(1f)] [SerializeField] float steerRate = 60f;

    ThirdPersonCamera followCamera;
    ThirdPersonController driver;
    ThirdPersonController nearby;
    float throttle;
    float steer;

    public bool IsDriven => driver != null;

    void Awake()
    {
        if (body == null)
            body = GetComponentInParent<Rigidbody>();
    }

    void Update()
    {
        if (driver != null)
        {
            if (Input.GetKeyDown(interactKey))
            {
                driver.Dismount();
                driver = null;
                if (followCamera != null)
                    followCamera.FollowCharacter();
                return;
            }
            float dt = Time.deltaTime;
            float input = Input.GetAxisRaw("Vertical");
            bool boost = Input.GetKey(KeyCode.LeftShift) && input > 0f;
            float target = input * (boost ? boostThrottle : 1f);
            // 往油门方向推用加速率，回落（松开或超出普通上限）用回落率
            bool pushing = Mathf.Abs(target) > Mathf.Abs(throttle) && target * throttle >= 0f;
            float rate = pushing ? (boost ? boostThrottleRate : throttleRate) : throttleReleaseRate;
            throttle = Mathf.MoveTowards(throttle, target, rate * dt);
            steer = Mathf.MoveTowards(steer, Input.GetAxisRaw("Horizontal") * maxSteerAngle, steerRate * dt);
            return;
        }

        throttle = Mathf.MoveTowards(throttle, 0f, throttleReleaseRate * Time.deltaTime);
        steer = Mathf.MoveTowards(steer, 0f, steerRate * Time.deltaTime);

        nearby = FindNearbyPlayer();
        if (nearby != null && Input.GetKeyDown(interactKey))
        {
            driver = nearby;
            nearby = null;
            driver.Mount(seatPoint);
            if (followCamera == null && Camera.main != null)
                followCamera = Camera.main.GetComponent<ThirdPersonCamera>();
            if (followCamera != null)
                followCamera.FollowVehicle(body.transform);
        }
    }

    ThirdPersonController FindNearbyPlayer()
    {
        if (seatPoint == null)
            return null;
        foreach (var c in Physics.OverlapSphere(seatPoint.position, interactRange, ~0, QueryTriggerInteraction.Ignore))
        {
            var p = c.GetComponent<ThirdPersonController>();
            if (p != null && !p.IsMounted)
                return p;
        }
        return null;
    }

    void FixedUpdate()
    {
        if (body == null || propeller == null || Mathf.Abs(throttle) < 1e-3f)
            return;
        // 螺旋桨露出水面就没有推力
        Vector3 p = propeller.position;
        if (!PLOceanSimulation.TrySampleHeight(p.x, p.z, out float sea) || p.y > sea)
            return;

        // 往右打舵：舷外机朝左偏，船尾被推向左边，船头转向右
        Vector3 dir = Quaternion.AngleAxis(-steer, body.transform.up) * body.transform.forward;
        float force = throttle * maxThrust * (throttle < 0f ? reverseRatio : 1f);
        body.AddForceAtPosition(dir * force, p, ForceMode.Force);
    }

    void OnGUI()
    {
        string text = driver != null ? $"W/S 油门  Shift 加速  A/D 转向  {interactKey} 离开"
            : nearby != null ? $"按 {interactKey} 驾驶" : null;
        if (text == null)
            return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
        ShadowLabel(new Rect(0f, Screen.height * 0.8f, Screen.width, 40f), text, style);

        if (driver == null || body == null)
            return;
        // 左下角：水平航速（km/h）和油门
        float kmh = Vector3.ProjectOnPlane(body.velocity, Vector3.up).magnitude * 3.6f;
        var big = new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft };
        var small = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.LowerLeft };
        ShadowLabel(new Rect(30f, Screen.height - 110f, 400f, 60f), $"{kmh:0} km/h", big);
        ShadowLabel(new Rect(32f, Screen.height - 50f, 400f, 30f), $"油门 {throttle * 100f:0}%", small);
    }

    static void ShadowLabel(Rect rect, string text, GUIStyle style)
    {
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, style);
        GUI.color = Color.white;
        GUI.Label(rect, text, style);
    }

    void OnDrawGizmosSelected()
    {
        if (seatPoint != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(seatPoint.position, interactRange);
        }
        if (propeller != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(propeller.position, 0.3f);
        }
    }
}
