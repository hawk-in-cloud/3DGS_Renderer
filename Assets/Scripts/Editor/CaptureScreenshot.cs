using UnityEditor;
using UnityEngine;

public class CaptureScreenshot : MonoBehaviour
{
    [MenuItem("Tools/Capture Screenshot %g")]
    public static void CaptureShot()
    {
        // 自动找一个未占用文件名，避免覆盖历史截图。
        int counter = 0;
        string path;
        while(true)
        {
            path = $"Shot-{counter:0000}.png";
            if (!System.IO.File.Exists(path))
                break;
            ++counter;
        }
        ScreenCapture.CaptureScreenshot(path);
        Debug.Log($"Captured {path}");
    }
}
