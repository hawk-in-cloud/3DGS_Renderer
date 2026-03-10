using UnityEngine;

public class FlyCamera : MonoBehaviour
{
    [Header("Move")]
    public float moveSpeed = 5f;
    public float fastMultiplier = 3f;

    [Header("Look")]
    public float lookSensitivity = 2f;
    public bool holdRightMouseToLook = true;

    float m_Yaw;
    float m_Pitch;

    void Start()
    {
        // 用初始朝向作为鼠标控制的起点，避免第一帧跳变。
        Vector3 euler = transform.rotation.eulerAngles;
        m_Yaw = euler.y;
        m_Pitch = euler.x;
    }

    void Update()
    {
        // 右键按住进入观察模式（可配置为常驻观察）。
        bool looking = !holdRightMouseToLook || Input.GetMouseButton(1);
        if (looking)
        {
            float mouseX = Input.GetAxisRaw("Mouse X");
            float mouseY = Input.GetAxisRaw("Mouse Y");
            m_Yaw += mouseX * lookSensitivity;
            m_Pitch -= mouseY * lookSensitivity;
            m_Pitch = Mathf.Clamp(m_Pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
        }

        if (holdRightMouseToLook && Input.GetMouseButtonDown(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        if (holdRightMouseToLook && Input.GetMouseButtonUp(1))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        Vector3 move = Vector3.zero;
        // WASD 平面移动，Q/E 垂直升降。
        if (Input.GetKey(KeyCode.W)) move += transform.forward;
        if (Input.GetKey(KeyCode.S)) move -= transform.forward;
        if (Input.GetKey(KeyCode.D)) move += transform.right;
        if (Input.GetKey(KeyCode.A)) move -= transform.right;
        if (Input.GetKey(KeyCode.E)) move += transform.up;
        if (Input.GetKey(KeyCode.Q)) move -= transform.up;

        float speed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift)) speed *= fastMultiplier;

        if (move.sqrMagnitude > 0f)
            transform.position += move.normalized * speed * Time.deltaTime;
    }
}
