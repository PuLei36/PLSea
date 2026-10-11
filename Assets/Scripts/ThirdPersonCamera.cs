using UnityEngine;

/// <summary>
/// 第三人称环绕相机：鼠标旋转，滚轮缩放，遇到障碍物自动拉近。Esc 切换鼠标锁定。
/// </summary>
public class ThirdPersonCamera : MonoBehaviour
{
    [SerializeField] Transform target;
    [SerializeField] float targetHeight = 1.5f;

    [Header("距离")]
    [SerializeField] float distance = 4f;
    [SerializeField] float minDistance = 1.5f;
    [SerializeField] float maxDistance = 8f;
    [SerializeField] float zoomSpeed = 2f;

    [Header("旋转")]
    [SerializeField] float mouseSensitivity = 3f;
    [SerializeField] float minPitch = -30f;
    [SerializeField] float maxPitch = 70f;

    [Header("碰撞")]
    [SerializeField] float collisionRadius = 0.2f;
    [SerializeField] LayerMask collisionLayers = ~0;

    [Header("载具")]
    [Tooltip("驾驶时环绕点离载具原点的高度（米，不按角色缩放）")]
    [SerializeField] float vehicleHeight = 3f;
    [SerializeField] float vehicleDistance = 14f;
    [SerializeField] float vehicleMinDistance = 6f;
    [SerializeField] float vehicleMaxDistance = 30f;

    float yaw;
    float pitch = 15f;

    // 驾驶时 target 换成载具，character 记住原来的角色，退出时换回去
    Transform character;
    Transform vehicle;
    float characterDistance;

    void Start()
    {
        if (target == null)
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
                target = player.transform;
        }
        if (target != null)
            yaw = target.eulerAngles.y;
        character = target;

        SetCursorLocked(true);
    }

    /// <summary>改为跟随载具（相机碰撞忽略载具和驾驶员）</summary>
    public void FollowVehicle(Transform vehicleRoot)
    {
        if (vehicle == null)
            characterDistance = distance;
        vehicle = vehicleRoot;
        distance = vehicleDistance;
    }

    /// <summary>回到跟随角色</summary>
    public void FollowCharacter()
    {
        if (vehicle == null)
            return;
        vehicleDistance = distance;
        vehicle = null;
        distance = characterDistance;
    }

    void LateUpdate()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            SetCursorLocked(Cursor.lockState != CursorLockMode.Locked);
        Transform follow = vehicle != null ? vehicle : target;
        if (follow == null)
            return;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
            pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }
        float scroll = Input.GetAxis("Mouse ScrollWheel") * zoomSpeed * 5f;
        if (vehicle != null)
            distance = Mathf.Clamp(distance - scroll * 2f, vehicleMinDistance, vehicleMaxDistance);
        else
            distance = Mathf.Clamp(distance - scroll, minDistance, maxDistance);

        // 角色模式下高度、距离都按角色缩放，模型放大后相机跟着拉远；载具用米
        float scale = vehicle != null ? 1f : target.lossyScale.y;
        float height = vehicle != null ? vehicleHeight : targetHeight * scale;
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 pivot = follow.position + Vector3.up * height;
        Vector3 back = rotation * Vector3.back;

        float maxD = distance * scale;
        float d = maxD;
        float radius = collisionRadius * scale;
        var hits = Physics.SphereCastAll(pivot, radius, back, maxD, collisionLayers, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            if (hit.transform.IsChildOf(follow) || (character != null && hit.transform.IsChildOf(character)))
                continue;
            // distance 为 0 表示起点就在碰撞体里（比如环绕点在船舱内），跳过
            if (hit.distance <= 0f)
                continue;
            d = Mathf.Min(d, Mathf.Max(hit.distance, 0.3f * scale));
        }

        transform.SetPositionAndRotation(pivot + back * d, rotation);
    }

    static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
