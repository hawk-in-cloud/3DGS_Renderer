using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Batch compare Unity renders against two reference folders (A/B).
// Usage: add to any GameObject and run the context menu.
public class GaussianBatchCompare : MonoBehaviour
{
    [Header("Folders")]
    public string folderA;
    public string folderB;
    public string folderUnity;
    public bool includeSubfolders = true;

    [Header("Matching")]
    public bool matchByFilename = true;
    public string[] extensions = { ".png", ".jpg", ".jpeg" };

    [Header("Output")]
    public bool writeCsv = true;
    public string outputCsvPath = "Assets/Benchmarks/batch_compare.csv";

    [ContextMenu("Run Batch Compare")]
    public void RunBatchCompare()
    {
        if (!ValidateFolder(folderUnity, "Unity"))
            return;
        if (!ValidateFolder(folderA, "A"))
            return;
        if (!ValidateFolder(folderB, "B"))
            return;

        var unityMap = BuildImageMap(folderUnity);
        var aMap = BuildImageMap(folderA);
        var bMap = BuildImageMap(folderB);

        if (unityMap.Count == 0)
        {
            Debug.LogWarning("GaussianBatchCompare: no images found in Unity folder.");
            return;
        }

        var rows = new List<ResultRow>(unityMap.Count);
        var aPsnr = new List<float>();
        var aSsim = new List<float>();
        var bPsnr = new List<float>();
        var bSsim = new List<float>();

        foreach (var kv in unityMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string key = kv.Key;
            string unityPath = kv.Value;
            string aPath = matchByFilename && aMap.TryGetValue(key, out var ap) ? ap : null;
            string bPath = matchByFilename && bMap.TryGetValue(key, out var bp) ? bp : null;

            Texture2D unityTex = LoadTexture(unityPath);
            if (!unityTex)
            {
                rows.Add(ResultRow.Missing(key, unityPath, aPath, bPath));
                continue;
            }

            float psnrA = float.NaN, ssimA = float.NaN;
            float psnrB = float.NaN, ssimB = float.NaN;

            if (!string.IsNullOrEmpty(aPath))
            {
                using var texA = new TempTexture(LoadTexture(aPath));
                if (texA.Tex != null)
                {
                    var aPixels = ResampleTo(unityTex, texA.Tex);
                    var uPixels = unityTex.GetPixels32();
                    psnrA = ComputePsnr(uPixels, aPixels);
                    ssimA = ComputeSsim(uPixels, aPixels);
                    if (!float.IsNaN(psnrA)) aPsnr.Add(psnrA);
                    if (!float.IsNaN(ssimA)) aSsim.Add(ssimA);
                }
            }

            if (!string.IsNullOrEmpty(bPath))
            {
                using var texB = new TempTexture(LoadTexture(bPath));
                if (texB.Tex != null)
                {
                    var bPixels = ResampleTo(unityTex, texB.Tex);
                    var uPixels = unityTex.GetPixels32();
                    psnrB = ComputePsnr(uPixels, bPixels);
                    ssimB = ComputeSsim(uPixels, bPixels);
                    if (!float.IsNaN(psnrB)) bPsnr.Add(psnrB);
                    if (!float.IsNaN(ssimB)) bSsim.Add(ssimB);
                }
            }

            rows.Add(new ResultRow
            {
                name = key,
                unityPath = unityPath,
                aPath = aPath,
                bPath = bPath,
                psnrA = psnrA,
                ssimA = ssimA,
                psnrB = psnrB,
                ssimB = ssimB
            });

            DestroyImmediate(unityTex);
        }

        if (writeCsv)
            WriteCsv(outputCsvPath, rows, aPsnr, aSsim, bPsnr, bSsim);

        Debug.Log($"GaussianBatchCompare: {rows.Count} items, A avg PSNR {AvgOrNa(aPsnr)}, A avg SSIM {AvgOrNa(aSsim)}, " +
                  $"B avg PSNR {AvgOrNa(bPsnr)}, B avg SSIM {AvgOrNa(bSsim)}");
    }

    bool ValidateFolder(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogWarning($"GaussianBatchCompare: {label} folder is empty.");
            return false;
        }
        if (!Directory.Exists(path))
        {
            Debug.LogWarning($"GaussianBatchCompare: {label} folder not found: {path}");
            return false;
        }
        return true;
    }

    Dictionary<string, string> BuildImageMap(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var exts = new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()));

        foreach (var file in Directory.EnumerateFiles(root, "*.*", option))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!exts.Contains(ext))
                continue;
            string key = Path.GetFileNameWithoutExtension(file);
            if (!map.ContainsKey(key))
                map.Add(key, file);
        }
        return map;
    }

    static Texture2D LoadTexture(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!ImageConversion.LoadImage(tex, bytes, false))
            {
                DestroyImmediate(tex);
                return null;
            }
            return tex;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GaussianBatchCompare: failed to load texture {path}: {e.Message}");
            return null;
        }
    }

    static Color32[] ResampleTo(Texture2D targetSize, Texture2D src)
    {
        if (!src || !targetSize)
            return null;
        if (src.width == targetSize.width && src.height == targetSize.height)
            return src.GetPixels32();

        int width = targetSize.width;
        int height = targetSize.height;
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
        double sumX = 0.0, sumY = 0.0;
        for (int i = 0; i < n; i++)
        {
            double lx = Luma(a[i]);
            double ly = Luma(b[i]);
            sumX += lx;
            sumY += ly;
        }
        double meanX = sumX / n;
        double meanY = sumY / n;

        double varX = 0.0, varY = 0.0, cov = 0.0;
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

    static void WriteCsv(
        string outputPath,
        List<ResultRow> rows,
        List<float> aPsnr, List<float> aSsim,
        List<float> bPsnr, List<float> bSsim)
    {
        string dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var sw = new StreamWriter(outputPath, false);
        sw.WriteLine("name,unity_path,a_path,b_path,psnr_a,ssim_a,psnr_b,ssim_b");
        foreach (var r in rows)
        {
            sw.WriteLine($"{Escape(r.name)},{Escape(r.unityPath)},{Escape(r.aPath)},{Escape(r.bPath)}," +
                         $"{Format(r.psnrA)},{Format(r.ssimA)},{Format(r.psnrB)},{Format(r.ssimB)}");
        }
        sw.WriteLine();
        sw.WriteLine($"avg_psnr_a,{AvgOrNa(aPsnr)}");
        sw.WriteLine($"avg_ssim_a,{AvgOrNa(aSsim)}");
        sw.WriteLine($"avg_psnr_b,{AvgOrNa(bPsnr)}");
        sw.WriteLine($"avg_ssim_b,{AvgOrNa(bSsim)}");
    }

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Contains(",") || s.Contains("\""))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    static string Format(float v)
    {
        return float.IsNaN(v) ? "" : v.ToString("F6");
    }

    static string AvgOrNa(List<float> values)
    {
        if (values == null || values.Count == 0)
            return "";
        return values.Average().ToString("F6");
    }

    struct ResultRow
    {
        public string name;
        public string unityPath;
        public string aPath;
        public string bPath;
        public float psnrA;
        public float ssimA;
        public float psnrB;
        public float ssimB;

        public static ResultRow Missing(string name, string unityPath, string aPath, string bPath)
        {
            return new ResultRow
            {
                name = name,
                unityPath = unityPath,
                aPath = aPath,
                bPath = bPath,
                psnrA = float.NaN,
                ssimA = float.NaN,
                psnrB = float.NaN,
                ssimB = float.NaN
            };
        }
    }

    readonly struct TempTexture : IDisposable
    {
        public readonly Texture2D Tex;
        public TempTexture(Texture2D tex) { Tex = tex; }
        public void Dispose()
        {
            if (Tex != null)
                DestroyImmediate(Tex);
        }
    }
}
