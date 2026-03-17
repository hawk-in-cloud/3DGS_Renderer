using UnityEngine;

[DisallowMultipleComponent]
public class CameraViewportSplit : MonoBehaviour
{
    [Header("Target")]
    public Camera targetCamera;

    [Header("Layout")]
    [Range(0.1f, 0.9f)] public float guiRatio = 0.4f;
    public bool applyOnlyInPlayMode = true;
    public bool restoreOriginalRectOnDisable = true;

    Rect m_OriginalRect;
    bool m_HasOriginalRect;

    // 左侧 GUI 区域宽度占比（0~1）
    public float CurrentGuiRatio => Mathf.Clamp01(guiRatio);

    void Reset()
    {
        targetCamera = GetComponent<Camera>();
    }

    void Awake()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>();
    }

    void OnEnable()
    {
        if (targetCamera == null)
            return;

        if (!m_HasOriginalRect)
        {
            m_OriginalRect = targetCamera.rect;
            m_HasOriginalRect = true;
        }

        ApplyIfNeeded();
    }

    void LateUpdate()
    {
        ApplyIfNeeded();
    }

    void OnDisable()
    {
        if (!restoreOriginalRectOnDisable || targetCamera == null || !m_HasOriginalRect)
            return;

        targetCamera.rect = m_OriginalRect;
    }

    void ApplyIfNeeded()
    {
        if (targetCamera == null)
            return;

        if (applyOnlyInPlayMode && !Application.isPlaying)
            return;

        float left = Mathf.Clamp(guiRatio, 0.05f, 0.95f);
        targetCamera.rect = new Rect(left, 0f, 1f - left, 1f);
    }
}
