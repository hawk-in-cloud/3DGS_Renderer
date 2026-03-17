using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

/*
Func：
1. 读取点云 PLY + 相机 JSON。
2. 把点云上传到 GPU buffer。
3. 每个相机每帧先算深度并排序，再绘制所有 splat。
4. 生命周期结束时释放 NativeArray 和 GraphicsBuffer。
*/
public static class PLYFileReader
{
    // 读取 binary PLY：解析 header 得到顶点数量/步长，再把 vertex 原始字节读入 NativeArray。
    public static void ReadFile(string filePath, out int vertexCount, out int vertexStride, out List<string> attrNames, out NativeArray<byte> vertices)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        
        // 逐行读取头部，直到 end_header。
        vertexCount = 0;//高斯点数量
        vertexStride = 0;//每个点占多少字节
        attrNames = new List<string>();//属性名列表
        while (true)
        {
            var line = ReadLine(fs);
            if (line == "end_header")
                break;
            var tokens = line.Split(' ');
            if (tokens.Length == 3 && tokens[0] == "element" && tokens[1] == "vertex")
                vertexCount = int.Parse(tokens[2]);
            if (tokens.Length == 3 && tokens[0] == "property")
            {
                // 这里只支持当前项目用到的三种类型。
                ElementType type = tokens[1] switch
                {
                    "float" => ElementType.Float,
                    "double" => ElementType.Double,
                    "uchar" => ElementType.UChar,
                    _ => ElementType.None
                };
                vertexStride += TypeToSize(type);
                attrNames.Add(tokens[2]);
            }
        }
        // header 之后是连续的 vertex 二进制数据。分配vertexCount * vertexStride字节（1字节=1*Allocator.Persistent）的 NativeArray，把数据读入。
        vertices = new NativeArray<byte>(vertexCount * vertexStride, Allocator.Persistent);
        var readBytes = fs.Read(vertices);
        if (readBytes != vertices.Length)//检查是否完整读完所有数据
            throw new IOException($"PLY {filePath} read error, expected {vertices.Length} data bytes got {readBytes}");
    }

    enum ElementType
    {
        None,
        Float,
        Double,
        UChar
    }

    static int TypeToSize(ElementType t)
    {
        return t switch
        {
            ElementType.None => 0,
            ElementType.Float => 4,
            ElementType.Double => 8,
            ElementType.UChar => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
        };
    }

    static string ReadLine(FileStream fs)
    {
        // 用简单字节读取实现，避免流式读取时编码器状态复杂度。
        var byteBuffer = new List<byte>();
        while (true)
        {
            int b = fs.ReadByte();
            if (b == -1 || b == '\n')
                break;
            byteBuffer.Add((byte)b);
        }
        return Encoding.UTF8.GetString(byteBuffer.ToArray());
        //Encoding.UTF8.GetString(...) 把字节按 UTF-8 解码成 C# 字符串。
        //ToArray() 把 List<byte> 转成 byte[]
    }

    [MenuItem("Tools/Test PLY Reader")]
    public static void TestPlyReader()
    {
        // 便捷工具：打开一个 ply，打印结构，并导出同名 .bytes 方便调试。
        var filePath = EditorUtility.OpenFilePanel("Open PLY File", "", "ply");
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        ReadFile(filePath, out int vertexCount, out int vertexStride, out var attrNames, out var vertices);
        Debug.Log($"PLY: vtx {vertexCount} stride {vertexStride} attrs {attrNames.Count}: {string.Join(", ", attrNames)}");
        var newPath = Path.ChangeExtension(filePath, ".bytes");
        File.WriteAllBytes(newPath, vertices.ToArray());
        vertices.Dispose();
    }
}
