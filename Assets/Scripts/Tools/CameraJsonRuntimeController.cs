using UnityEngine;

public class CameraJsonRuntimeController : MonoBehaviour
{
    [Header("References")]
    public GaussianSplatRenderer source;
    public Camera targetCamera;

    [Header("Apply")]
    public bool applyOnStart = true;
    [Min(0)] public int startIndex = 0;
    public bool applyFov = false;

    [Header("Keyboard")]
    public bool enableKeyboardSwitch = true;
    public KeyCode prevKey = KeyCode.LeftBracket;
    public KeyCode nextKey = KeyCode.RightBracket;

    int m_CurrentIndex;

    void Start()
    {
        if (source == null)
            source = GetComponent<GaussianSplatRenderer>();
        if (targetCamera == null)
            targetCamera = Camera.main;

        m_CurrentIndex = Mathf.Max(0, startIndex);

        if (applyOnStart)
            ApplyCamera(m_CurrentIndex);
    }

    void Update()
    {
        if (!enableKeyboardSwitch)
            return;

        if (Input.GetKeyDown(prevKey))
            ApplyCamera(m_CurrentIndex - 1);
        if (Input.GetKeyDown(nextKey))
            ApplyCamera(m_CurrentIndex + 1);
    }

    [ContextMenu("Apply Current Camera")]
    public void ApplyCurrentCamera()
    {
        ApplyCamera(m_CurrentIndex);
    }

    public void ApplyCamera(int index)
    {
        if (source == null)
        {
            Debug.LogWarning("CameraJsonRuntimeController: source is null.");
            return;
        }

        if (targetCamera == null)
        {
            Debug.LogWarning("CameraJsonRuntimeController: targetCamera is null.");
            return;
        }

        var cameras = source.cameras;
        if (cameras == null || cameras.Length == 0)
        {
            Debug.LogWarning(
                "CameraJsonRuntimeController: no camera data loaded. " +
                "Check PointCloudFolder and cameras.json path.");
            return;
        }

        int count = cameras.Length;
        index = ((index % count) + count) % count;
        m_CurrentIndex = index;

        var cam = cameras[index];
        targetCamera.transform.position = cam.pos;
        targetCamera.transform.rotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);

        if (applyFov && cam.fov > 0.001f)
            targetCamera.fieldOfView = cam.fov;

        Debug.Log($"CameraJsonRuntimeController: applied camera {index}/{count - 1}");
    }
}
