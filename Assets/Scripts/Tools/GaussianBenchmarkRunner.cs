using System;
using System.Collections;
using Unity.Profiling;
using UnityEngine;

public class GaussianBenchmarkRunner : MonoBehaviour
{
    public enum GpuTimingMode
    {
        Auto,
        PreferProfilerRecorder,
        FrameTimingOnly,
        ProfilerRecorderOnly,
        EditorStatsOnly
    }

    [Header("References")]
    public Camera targetCamera;
    public bool autoUseMainCamera = true;
    public Texture2D referenceImage;

    [Header("Realtime Metrics")]
    public bool showOverlay = true;
    public KeyCode toggleOverlayKey = KeyCode.F9;
    [Range(0.0f, 0.99f)] public float fpsSmoothing = 0.90f;
    public bool useUnscaledTime = true;
    public bool showCpuGpuFrameTime = true;
    public GpuTimingMode gpuTimingMode = GpuTimingMode.PreferProfilerRecorder;
    [Range(0.0f, 0.99f)] public float gpuFrameSmoothing = 0.85f;
    [Min(1)] public int gpuSourceSwitchGraceFrames = 30;
    public bool pauseMetricsWhileQualityCapture = true;

    [Header("Quality Compare")]
    public bool enableQualityCompare = true;
    [Min(0.05f)] public float qualityUpdateInterval = 0.5f;
    public bool useReferenceResolution = true;
    [Min(16)] public int captureWidth = 640;
    [Min(16)] public int captureHeight = 360;

    [Header("Overlay UI")]
    public Vector2 overlayAnchor = new Vector2(20, 20);
    [Range(300, 1200)] public int overlayWidth = 640;
    public bool showImagePreview = true;
    [Range(120, 600)] public int previewWidth = 260;
    [Range(80, 400)] public int previewHeight = 146;
    public bool autoFitToCameraSplit = true;
    public CameraViewportSplit viewportSplit;
    [Range(0, 80)] public int overlayPadding = 12;
    [Range(0, 240)] public int overlayExtraTextHeight = 24;

    float m_FpsSmoothed;
    float m_FrameMs;
    float m_CpuFrameMs = float.NaN;
    float m_GpuFrameMs = float.NaN;
    float m_GpuFrameMsRaw = float.NaN;
    string m_GpuTimeSource = "N/A";
    int m_GpuUnavailableFrameCount;
    string m_GpuLockedSource;
    int m_GpuLockedInvalidStreak;

    float m_Psnr = float.NaN;
    float m_Ssim = float.NaN;
    bool m_QualityValid;
    string m_QualityStatus = "Pending";

    bool m_QualityBusy;
    float m_NextQualityUpdateTime;

    int m_CaptureWidth = -1;
    int m_CaptureHeight = -1;
    RenderTexture m_CaptureRt;
    Texture2D m_CurrentCapture;

    Color32[] m_ReferencePixelsResampled;
    int m_RefCacheWidth = -1;
    int m_RefCacheHeight = -1;

    readonly FrameTiming[] m_FrameTimings = new FrameTiming[1];
    ProfilerRecorder m_GpuFrameRecorder;

    GUIStyle m_TitleStyle;
    GUIStyle m_LabelStyle;

    static readonly string[] kGpuSourceOrderAuto = { "FrameTiming", "ProfilerRecorder", "UnityEditor.UnityStats" };
    static readonly string[] kGpuSourceOrderPreferRecorder = { "ProfilerRecorder", "FrameTiming", "UnityEditor.UnityStats" };

    void Awake()
    {
        if (targetCamera == null && autoUseMainCamera)
            targetCamera = Camera.main;

        if (viewportSplit == null && targetCamera != null)
            viewportSplit = targetCamera.GetComponent<CameraViewportSplit>();
    }

    void OnEnable()
    {
        m_NextQualityUpdateTime = Time.unscaledTime + 0.1f;
        StartGpuRecorder();
        m_GpuLockedSource = null;
        m_GpuLockedInvalidStreak = 0;
        m_GpuFrameMs = float.NaN;
        m_GpuFrameMsRaw = float.NaN;
        m_GpuTimeSource = "N/A";
        m_GpuUnavailableFrameCount = 0;
    }

    void OnDisable()
    {
        ReleaseCaptureBuffers();
        DisposeGpuRecorder();
    }

    void OnDestroy()
    {
        ReleaseCaptureBuffers();
        DisposeGpuRecorder();
    }

    void Update()
    {
        if (targetCamera == null && autoUseMainCamera)
            targetCamera = Camera.main;
        if (viewportSplit == null && targetCamera != null)
            viewportSplit = targetCamera.GetComponent<CameraViewportSplit>();

        if (Input.GetKeyDown(toggleOverlayKey))
            showOverlay = !showOverlay;

        UpdateRealtimeMetrics();
        TryScheduleQualityUpdate();
    }

    void UpdateRealtimeMetrics()
    {
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        dt = Mathf.Max(dt, 1e-6f);

        float fps = 1.0f / dt;
        m_FrameMs = dt * 1000.0f;

        if (m_FpsSmoothed <= 0.0f)
        {
            m_FpsSmoothed = fps;
        }
        else
        {
            float lerpT = 1.0f - fpsSmoothing;
            m_FpsSmoothed = Mathf.Lerp(m_FpsSmoothed, fps, lerpT);
        }

        if (showCpuGpuFrameTime && !(pauseMetricsWhileQualityCapture && m_QualityBusy))
        {
            FrameTimingManager.CaptureFrameTimings();
            uint n = FrameTimingManager.GetLatestTimings(1, m_FrameTimings);
            bool hasFrameTiming = n > 0;
            float frameTimingGpu = float.NaN;
            if (hasFrameTiming)
            {
                m_CpuFrameMs = (float)m_FrameTimings[0].cpuFrameTime;
                frameTimingGpu = (float)m_FrameTimings[0].gpuFrameTime;
            }

            if (TryGetGpuFrameMs(hasFrameTiming, frameTimingGpu, out float gpuMs, out string source))
            {
                m_GpuFrameMsRaw = gpuMs;
                if (float.IsNaN(m_GpuFrameMs))
                    m_GpuFrameMs = gpuMs;
                else
                    m_GpuFrameMs = Mathf.Lerp(m_GpuFrameMs, gpuMs, 1.0f - gpuFrameSmoothing);

                m_GpuTimeSource = source;
                m_GpuUnavailableFrameCount = 0;
            }
            else
            {
                m_GpuFrameMsRaw = float.NaN;
                m_GpuTimeSource = source;
                m_GpuUnavailableFrameCount++;
            }
        }
    }

    void StartGpuRecorder()
    {
        DisposeGpuRecorder();

        // 婵炴垶鎸哥粔鎾箖?Unity 闂佺粯顨呴悧濠傦耿?濡ょ姷鍋涢崯鑳亹鐎靛摜纾奸柣鏃€妞块崥鈧俊鐐€楅幊鎾诲箖閺囩姷鐭撳Λ棰佽兌缁愭鈽夐幘宕囆㈤柟顔芥崌閺佸秶浠﹂幆褏妯嗛梺绋匡攻閻熲晠鍩€椤掆偓椤︾敻濡存惔銏″劅闊洦鎸惧В锕傛偣閸ャ劍绌块柟?        m_GpuFrameRecorder = TryStartRecorder(ProfilerCategory.Render, "GPU Frame Time");
        if (!m_GpuFrameRecorder.Valid)
            m_GpuFrameRecorder = TryStartRecorder(ProfilerCategory.Render, "GPU Total Frame Time");
        if (!m_GpuFrameRecorder.Valid)
            m_GpuFrameRecorder = TryStartRecorder(ProfilerCategory.Render, "GPU Time");
        if (!m_GpuFrameRecorder.Valid)
            m_GpuFrameRecorder = TryStartRecorder(ProfilerCategory.Internal, "GPU Frame Time");
        if (!m_GpuFrameRecorder.Valid)
            m_GpuFrameRecorder = TryStartRecorder(ProfilerCategory.Internal, "GPU Total Frame Time");
        if (!m_GpuFrameRecorder.Valid)
            m_GpuFrameRecorder = TryStartRecorder(ProfilerCategory.Internal, "GPU Time");
    }

    void DisposeGpuRecorder()
    {
        if (m_GpuFrameRecorder.Valid)
            m_GpuFrameRecorder.Dispose();
        m_GpuFrameRecorder = default;
    }

    static ProfilerRecorder TryStartRecorder(ProfilerCategory category, string statName)
    {
        var recorder = ProfilerRecorder.StartNew(category, statName, 1);
        if (recorder.Valid)
            return recorder;

        recorder.Dispose();
        return default;
    }

    bool TryGetGpuFrameMs(bool hasFrameTiming, float frameTimingGpu, out float gpuMs, out string source)
    {
        gpuMs = float.NaN;
        source = "Unavailable";

        // Fixed modes: do not auto-switch source.
        if (gpuTimingMode == GpuTimingMode.FrameTimingOnly)
            return TryGetGpuFromSource("FrameTiming", hasFrameTiming, frameTimingGpu, out gpuMs, out source);
        if (gpuTimingMode == GpuTimingMode.ProfilerRecorderOnly)
            return TryGetGpuFromSource("ProfilerRecorder", hasFrameTiming, frameTimingGpu, out gpuMs, out source);
        if (gpuTimingMode == GpuTimingMode.EditorStatsOnly)
            return TryGetGpuFromSource("UnityEditor.UnityStats", hasFrameTiming, frameTimingGpu, out gpuMs, out source);

        // Auto modes: lock to one source and only switch when continuously invalid.
        if (!string.IsNullOrEmpty(m_GpuLockedSource))
        {
            if (TryGetGpuFromSource(m_GpuLockedSource, hasFrameTiming, frameTimingGpu, out gpuMs, out source))
            {
                m_GpuLockedInvalidStreak = 0;
                return true;
            }

            m_GpuLockedInvalidStreak++;
            source = m_GpuLockedSource;
            if (m_GpuLockedInvalidStreak < Mathf.Max(1, gpuSourceSwitchGraceFrames))
                return false;

            m_GpuLockedSource = null;
            m_GpuLockedInvalidStreak = 0;
        }

        string[] order = gpuTimingMode == GpuTimingMode.PreferProfilerRecorder
            ? kGpuSourceOrderPreferRecorder
            : kGpuSourceOrderAuto;

        for (int i = 0; i < order.Length; i++)
        {
            if (TryGetGpuFromSource(order[i], hasFrameTiming, frameTimingGpu, out gpuMs, out source))
            {
                m_GpuLockedSource = source;
                m_GpuLockedInvalidStreak = 0;
                return true;
            }
        }

        return false;
    }

    bool TryGetGpuFromSource(string candidate, bool hasFrameTiming, float frameTimingGpu, out float gpuMs, out string source)
    {
        gpuMs = float.NaN;
        source = candidate;

        if (candidate == "FrameTiming")
        {
            if (hasFrameTiming && frameTimingGpu > 0.001f)
            {
                gpuMs = frameTimingGpu;
                return true;
            }
            return false;
        }

        if (candidate == "ProfilerRecorder")
        {
            gpuMs = RecorderNsToMs(m_GpuFrameRecorder);
            return !float.IsNaN(gpuMs) && gpuMs > 0.001f;
        }

        if (candidate == "UnityEditor.UnityStats")
            return TryEditorGpuTiming(out gpuMs);

        return false;
    }

    static float RecorderNsToMs(ProfilerRecorder recorder)
    {
        if (!recorder.Valid || recorder.Count <= 0)
            return float.NaN;

        return recorder.LastValue / 1_000_000f;
    }

    static bool TryEditorGpuTiming(out float gpuMs)
    {
        gpuMs = float.NaN;
#if UNITY_EDITOR
        // Editor fallback: some versions return 0 for FrameTiming/Recorder.
        try
        {
            Type t = Type.GetType("UnityEditor.UnityStats, UnityEditor");
            if (t != null)
            {
                var prop = t.GetProperty(
                    "gpuTimeLastFrame",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                if (prop != null)
                {
                    object raw = prop.GetValue(null, null);
                    if (raw != null)
                    {
                        float v = Convert.ToSingle(raw);
                        if (!float.IsNaN(v) && !float.IsInfinity(v) && v > 0.001f)
                        {
                            gpuMs = v;
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
#endif
        return false;
    }

    void TryScheduleQualityUpdate()
    {
        if (!enableQualityCompare || m_QualityBusy)
            return;

        if (Time.unscaledTime < m_NextQualityUpdateTime)
            return;

        m_NextQualityUpdateTime = Time.unscaledTime + qualityUpdateInterval;
        StartCoroutine(CaptureAndCompareAtEndOfFrame());
    }

    [ContextMenu("Force Quality Update")]
    public void ForceQualityUpdate()
    {
        if (!m_QualityBusy)
            StartCoroutine(CaptureAndCompareAtEndOfFrame());
    }

    IEnumerator CaptureAndCompareAtEndOfFrame()
    {
        m_QualityBusy = true;
        yield return new WaitForEndOfFrame();

        if (targetCamera == null)
        {
            m_QualityValid = false;
            m_QualityStatus = "TargetCameraNull";
            m_QualityBusy = false;
            yield break;
        }

        ResolveCaptureSize(out int w, out int h);
        EnsureCaptureBuffers(w, h);

        CaptureFromCamera(targetCamera, m_CaptureRt, m_CurrentCapture);

        if (referenceImage == null)
        {
            m_QualityValid = false;
            m_QualityStatus = "ReferenceImageMissing";
            m_QualityBusy = false;
            yield break;
        }

        EnsureReferenceCache(w, h);
        if (m_ReferencePixelsResampled == null)
        {
            m_QualityValid = false;
            m_QualityStatus = "ReferenceCacheFailed";
            m_QualityBusy = false;
            yield break;
        }

        Color32[] currentPixels = m_CurrentCapture.GetPixels32();
        m_Psnr = ComputePsnr(currentPixels, m_ReferencePixelsResampled);
        m_Ssim = ComputeSsim(currentPixels, m_ReferencePixelsResampled);

        m_QualityValid = !float.IsNaN(m_Psnr) && !float.IsNaN(m_Ssim);
        m_QualityStatus = m_QualityValid ? "OK" : "ComputeFailed";

        m_QualityBusy = false;
    }

    void ResolveCaptureSize(out int width, out int height)
    {
        if (useReferenceResolution && referenceImage != null)
        {
            width = referenceImage.width;
            height = referenceImage.height;
            return;
        }

        width = Mathf.Max(16, captureWidth);
        height = Mathf.Max(16, captureHeight);

        if (width <= 16 || height <= 16)
        {
            if (targetCamera != null)
            {
                width = Mathf.Max(16, targetCamera.pixelWidth);
                height = Mathf.Max(16, targetCamera.pixelHeight);
            }
            else
            {
                width = Mathf.Max(16, Screen.width);
                height = Mathf.Max(16, Screen.height);
            }
        }
    }

    void EnsureCaptureBuffers(int width, int height)
    {
        if (m_CaptureRt != null && m_CurrentCapture != null && width == m_CaptureWidth && height == m_CaptureHeight)
            return;

        ReleaseCaptureBuffers();

        m_CaptureWidth = width;
        m_CaptureHeight = height;

        m_CaptureRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "GaussianBenchmarkRunner_CaptureRT"
        };

        m_CurrentCapture = new Texture2D(width, height, TextureFormat.RGB24, false)
        {
            name = "GaussianBenchmarkRunner_CurrentCapture"
        };
    }

    void ReleaseCaptureBuffers()
    {
        if (m_CaptureRt != null)
        {
            m_CaptureRt.Release();
            Destroy(m_CaptureRt);
            m_CaptureRt = null;
        }

        if (m_CurrentCapture != null)
        {
            Destroy(m_CurrentCapture);
            m_CurrentCapture = null;
        }
    }

    static void CaptureFromCamera(Camera cam, RenderTexture rt, Texture2D dst)
    {
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture prevTarget = cam.targetTexture;

        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        dst.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        dst.Apply(false, false);

        cam.targetTexture = prevTarget;
        RenderTexture.active = prevActive;
    }

    void EnsureReferenceCache(int width, int height)
    {
        if (referenceImage == null)
        {
            m_ReferencePixelsResampled = null;
            m_RefCacheWidth = -1;
            m_RefCacheHeight = -1;
            return;
        }

        if (m_ReferencePixelsResampled != null && m_RefCacheWidth == width && m_RefCacheHeight == height)
            return;

        m_RefCacheWidth = width;
        m_RefCacheHeight = height;
        m_ReferencePixelsResampled = ResampleTexture(referenceImage, width, height);
    }

    static Color32[] ResampleTexture(Texture2D src, int width, int height)
    {
        if (src == null || width <= 0 || height <= 0)
            return null;

        // 婵炴潙鍚嬮敋闁告ɑ鐩弫?CPU 闂備焦褰冨ú锕傛偋闁秵鏅繛鎴炵矊椤忕喓绱掗幆褎璐￠柟顔硷攻缁嬪顓奸崨顓☆唹闁荤姴娲ｇ槐顔炬濠靛绀嗘繛鍡樺笩濞?GPU blit + ReadPixels 闂佹悶鍎抽崑鐘活敋娴煎瓨鏅?        // 闂備緡鍓欓悘婵嬪储閵堝牏鐤€闁告稒鐣埀顒€绻樺畷鐑藉Ω閵夘喖娈奸梺绋跨箞閸庢挳顢欓弴鐘电＞妞ゆ洍鍋撻柛锝囧厴瀹曠喖骞橀崨顔碱伓?Read/Write闂?        if (src.isReadable)
            return ResampleReadable(src, width, height);

        return ResampleViaGpuReadback(src, width, height);
    }

    static Color32[] ResampleReadable(Texture2D src, int width, int height)
    {
        var dst = new Color32[width * height];
        float invW = 1.0f / Mathf.Max(1, width - 1);
        float invH = 1.0f / Mathf.Max(1, height - 1);

        int idx = 0;
        for (int y = 0; y < height; y++)
        {
            float v = y * invH;
            for (int x = 0; x < width; x++)
            {
                float u = x * invW;
                dst[idx++] = src.GetPixelBilinear(u, v);
            }
        }

        return dst;
    }

    static Color32[] ResampleViaGpuReadback(Texture2D src, int width, int height)
    {
        RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        RenderTexture prevActive = RenderTexture.active;

        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var tmp = new Texture2D(width, height, TextureFormat.RGB24, false);
            tmp.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            tmp.Apply(false, false);
            var pixels = tmp.GetPixels32();
            Destroy(tmp);
            return pixels;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GaussianBenchmarkRunner: reference texture readback failed: {e.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    static float ComputePsnr(Color32[] a, Color32[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
            return float.NaN;

        double mse = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            int dr = a[i].r - b[i].r;
            int dg = a[i].g - b[i].g;
            int db = a[i].b - b[i].b;
            mse += (dr * dr + dg * dg + db * db) / 3.0;
        }

        mse /= a.Length;
        if (mse <= 1e-12)
            return 99.0f;

        double psnr = 10.0 * Math.Log10((255.0 * 255.0) / mse);
        return (float)psnr;
    }

    static float ComputeSsim(Color32[] a, Color32[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
            return float.NaN;

        int n = a.Length;
        double sumX = 0.0;
        double sumY = 0.0;

        for (int i = 0; i < n; i++)
        {
            double lx = Luma(a[i]);
            double ly = Luma(b[i]);
            sumX += lx;
            sumY += ly;
        }

        double meanX = sumX / n;
        double meanY = sumY / n;

        double varX = 0.0;
        double varY = 0.0;
        double cov = 0.0;

        for (int i = 0; i < n; i++)
        {
            double dx = Luma(a[i]) - meanX;
            double dy = Luma(b[i]) - meanY;
            varX += dx * dx;
            varY += dy * dy;
            cov += dx * dy;
        }

        double denom = Math.Max(1, n - 1);
        varX /= denom;
        varY /= denom;
        cov /= denom;

        const double c1 = (0.01 * 255) * (0.01 * 255);
        const double c2 = (0.03 * 255) * (0.03 * 255);

        double num = (2 * meanX * meanY + c1) * (2 * cov + c2);
        double den = (meanX * meanX + meanY * meanY + c1) * (varX + varY + c2);

        if (Math.Abs(den) <= 1e-12)
            return float.NaN;

        return (float)(num / den);
    }

    static double Luma(Color32 c)
    {
        return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
    }

    void OnGUI()
    {
        if (!showOverlay)
            return;

        EnsureGuiStyles();

        float x = overlayAnchor.x;
        float y = overlayAnchor.y;
        float width = overlayWidth;

        if (autoFitToCameraSplit && targetCamera != null)
        {
            float guiRatio = GetGuiAreaRatio();
            if (guiRatio > 0.01f)
            {
                float leftWidth = Screen.width * guiRatio;
                x = Mathf.Clamp(x, 0, Mathf.Max(0, leftWidth - 40));
                width = Mathf.Min(width, Mathf.Max(120, leftWidth - x - overlayPadding));
            }
        }

        // 闁哄倸娲﹀﹢鐗堫殗濡搫顔婇柟绋款槼椤㈡垿寮幏灞芥闁告柣鍔忛鍝ョ不濡ゅ绀夐梺顒€鐏濋崢銈囨偖椤愩垺绂堥柣妤€娲ょ亸顖炴焼椤旇棄鐨￠柕?
        int cpuGpuRows = 0;
        if (showCpuGpuFrameTime)
            cpuGpuRows = m_GpuUnavailableFrameCount > 30 ? 3 : 2;
        int infoRows = 2 + cpuGpuRows + 5; // fps/frame + cpu/gpu + size/psnr/ssim/status + hint
        float titleBlock = 28.0f;
        float rowHeight = 22.0f;
        float textBlockHeight = 10.0f + titleBlock + infoRows * rowHeight + overlayExtraTextHeight;
        float imagesHeight = showImagePreview ? previewHeight + 40.0f : 0.0f;
        float totalHeight = textBlockHeight + imagesHeight + 8.0f;

        GUI.Box(new Rect(x, y, width, totalHeight), GUIContent.none);

        float tx = x + 12;
        float ty = y + 10;

        GUI.Label(new Rect(tx, ty, width - 24, 24), "Gaussian Benchmark (Realtime)", m_TitleStyle);
        ty += 28;

        GUI.Label(new Rect(tx, ty, width - 24, 22), $"FPS: {m_FpsSmoothed:F1}", m_LabelStyle);
        ty += 22;
        GUI.Label(new Rect(tx, ty, width - 24, 22), $"Frame: {m_FrameMs:F2} ms", m_LabelStyle);
        ty += 22;

        if (showCpuGpuFrameTime)
        {
            GUI.Label(new Rect(tx, ty, width - 24, 22), $"CPU Frame: {FormatMetric(m_CpuFrameMs)} ms", m_LabelStyle);
            ty += 22;
            string gpuLine = float.IsNaN(m_GpuFrameMsRaw)
                ? $"GPU Frame: {FormatMetric(m_GpuFrameMs)} ms ({m_GpuTimeSource})"
                : $"GPU Frame: {FormatMetric(m_GpuFrameMs)} ms [raw {FormatMetric(m_GpuFrameMsRaw)}] ({m_GpuTimeSource})";
            GUI.Label(new Rect(tx, ty, width - 24, 22), gpuLine, m_LabelStyle);
            ty += 22;
            if (m_GpuUnavailableFrameCount > 30)
            {
                GUI.Label(new Rect(tx, ty, width - 24, 22), "GPU time unavailable in current mode/API", m_LabelStyle);
                ty += 22;
            }
        }

        string sizeText = m_CurrentCapture != null
            ? $"Compare Size: {m_CurrentCapture.width}x{m_CurrentCapture.height}"
            : "Compare Size: (not ready)";
        GUI.Label(new Rect(tx, ty, width - 24, 22), sizeText, m_LabelStyle);
        ty += 22;

        string psnrText = m_QualityValid ? m_Psnr.ToString("F3") : "N/A";
        string ssimText = m_QualityValid ? m_Ssim.ToString("F5") : "N/A";
        GUI.Label(new Rect(tx, ty, width - 24, 22), $"PSNR: {psnrText}", m_LabelStyle);
        ty += 22;
        GUI.Label(new Rect(tx, ty, width - 24, 22), $"SSIM: {ssimText}", m_LabelStyle);
        ty += 22;

        GUI.Label(new Rect(tx, ty, width - 24, 22), $"Quality Status: {m_QualityStatus}", m_LabelStyle);
        ty += 22;

        GUI.Label(new Rect(tx, ty, width - 24, 22), "F9: Show/Hide Overlay", m_LabelStyle);

        if (!showImagePreview)
            return;

        float usableForImages = width - 24.0f;
        float localPreviewWidth = Mathf.Min(previewWidth, Mathf.Max(80.0f, (usableForImages - 20.0f) * 0.5f));
        float localPreviewHeight = previewHeight;

        float imgY = y + textBlockHeight + 4.0f;
        float spacing = 20f;
        float leftX = x + 12;
        float rightX = leftX + localPreviewWidth + spacing;

        GUI.Label(new Rect(leftX, imgY, localPreviewWidth, 20), "Reference", m_LabelStyle);
        GUI.Label(new Rect(rightX, imgY, localPreviewWidth, 20), "Current", m_LabelStyle);

        Rect leftRect = new Rect(leftX, imgY + 20, localPreviewWidth, localPreviewHeight);
        Rect rightRect = new Rect(rightX, imgY + 20, localPreviewWidth, localPreviewHeight);

        GUI.Box(leftRect, GUIContent.none);
        GUI.Box(rightRect, GUIContent.none);

        if (referenceImage != null)
            GUI.DrawTexture(leftRect, referenceImage, ScaleMode.ScaleToFit, false);

        if (m_CurrentCapture != null)
            GUI.DrawTexture(rightRect, m_CurrentCapture, ScaleMode.ScaleToFit, false);
    }

    float GetGuiAreaRatio()
    {
        if (viewportSplit != null)
            return viewportSplit.CurrentGuiRatio;

        if (targetCamera == null)
            return 0f;

        // 闂佸吋鐪归崕鏌ユ儉閸涙潙绠伴柛灞捐壘閻庡鎮橀悙鑼闁告挸銈稿鐢割敆婵犲嫮顦繛鎴炴⒒閸犲酣鎯冮崜褎瀚氶柡鍥ㄦ皑閻?Camera.rect 闂佽浜介崝宥夊蓟閸パ屽晠闁挎梹瀵у▍鐘绘煕閺嵮勬儓闁绘挻鐟╅弫?        // 缂備焦鎷濈粻鎴︽偩妤ｅ啯鏅慨姗嗗墮缁€浣搞€掑鈧崟顕呮船闂佸搫鏈幐璇差渻閸岀偞鏅悗鍦嚠ct.x 闂佸憡顨呴崯鎸庣▕韫囨梻鐟圭憸宀€鎹㈠鑸靛仼婵炲棗绻愰梾妯绘叏閿濆棙鐓ｇ紒韬插劦閺?        if (targetCamera.rect.width < 0.999f && targetCamera.rect.x > 0f)
            return Mathf.Clamp01(targetCamera.rect.x);

        return 0f;
    }

    void EnsureGuiStyles()
    {
        if (m_TitleStyle == null)
        {
            m_TitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
        }

        if (m_LabelStyle == null)
        {
            m_LabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white }
            };
        }
    }

    static string FormatMetric(float v)
    {
        return float.IsNaN(v) ? "N/A" : v.ToString("F2");
    }
}
