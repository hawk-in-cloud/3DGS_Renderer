// 本文件改编自 Unity HDRP/SRP 的 GPUSort 代码，来源：
//   https://github.com/Unity-Technologies/Graphics/tree/8bdd620b16/Packages/com.unity.render-pipelines.high-definition/Runtime/Utilities/GPUSort
//   https://github.com/Unity-Technologies/Graphics/tree/8bdd620b16/Packages/com.unity.render-pipelines.core/Runtime/Utilities/GPUSort
// 该实现本身又很可能改编自 Tim Gfrerer 的 "Project Island"：
//   https://poniesandlight.co.uk/reflect/bitonic_merge_sort/
//   https://github.com/tgfrerer/island
// Project Island 使用 MIT 许可证，Copyright (c) 2020 Tim Gfrerer。
//
// 相比 HDRP 原实现，这里做了以下调整：
// - 移除了与 SRP RenderGraph 集成相关的部分；
// - 修复了当缓冲区超过 4M 时的拷贝处理；
// - 修复了在 Metal（以及部分 Vulkan 实现）下小数据量场景的问题。

using System;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

/// <summary>
/// GPU 上的 (key, value) 键值对排序工具。
/// </summary>
public struct IslandGPUSort
{
    /// <summary>
    /// 排序调度参数。
    /// </summary>
    public struct Args
    {
        /// <summary>元素总数，必须是 2 的幂。</summary>
        public uint count;
        /// <summary>限制 bitonic 排序深度；0 表示默认完整深度。</summary>
        public uint maxDepth;
        /// <summary>排序键缓冲。</summary>
        public GraphicsBuffer keys;
        /// <summary>排序值缓冲（与键一一对应）。</summary>
        public GraphicsBuffer values;

        internal int workGroupCount;
    }

    private LocalKeyword[] m_Keywords;

    enum Stage
    {
        // 对应 compute shader 内四个 keyword 分支。
        LocalBMS,
        LocalDisperse,
        BigFlip,
        BigDisperse
    }

    ComputeShader computeShader;

    /// <summary>
    /// 初始化可复用的 GPU 排序器实例。
    /// </summary>
    public IslandGPUSort(ComputeShader cs)
    {
        computeShader = cs;
        m_Keywords = new LocalKeyword[4]
        {
            new(cs, "STAGE_BMS"),
            new(cs, "STAGE_LOCAL_DISPERSE"),
            new(cs, "STAGE_BIG_FLIP"),
            new(cs, "STAGE_BIG_DISPERSE")
        };
    }

    void DispatchStage(CommandBuffer cmd, Args args, uint h, Stage stage)
    {
        Assert.IsTrue(args.workGroupCount != -1);
        Assert.IsNotNull(computeShader);
        {
#if false
            m_SortCS.enabledKeywords = new[]  { keywords[(int)stage] };
#else
            // 当前实现通过切换 keyword 选择阶段逻辑（单 kernel，多分支）。
            foreach (var k in m_Keywords)
                cmd.SetKeyword(computeShader, k, false);
            cmd.SetKeyword(computeShader, m_Keywords[(int)stage], true);
#endif

            cmd.SetComputeIntParam(computeShader, "_H", (int)h);
            cmd.SetComputeIntParam(computeShader, "_Total", (int)args.count);
            cmd.SetComputeBufferParam(computeShader, 0, "_KeyBuffer", args.keys);
            cmd.SetComputeBufferParam(computeShader, 0, "_ValueBuffer", args.values);
            cmd.DispatchCompute(computeShader, 0, args.workGroupCount, 1, 1);
        }
    }

    static int DivRoundUp(int x, int y) => (x + y - 1) / y;

    /// <summary>
    /// 对 (key, value) 列表执行 bitonic merge sort。
    /// </summary>
    /// <param name="cmd">记录排序命令的命令缓冲。</param>
    /// <param name="args">本次排序参数。</param>
    public void Dispatch(CommandBuffer cmd, Args args)
    {
        Assert.IsNotNull(computeShader);
        Assert.IsTrue(Mathf.IsPowerOfTwo((int)args.count));
        uint n = args.count;

        computeShader.GetKernelThreadGroupSizes(0, out var workGroupSizeX, out var workGroupSizeY, out var workGroupSizeZ);

        // 每个 workgroup 处理 2*groupSize 个元素。
        args.workGroupCount = Math.Max(1, DivRoundUp((int)n, (int)workGroupSizeX * 2));

        if (args.maxDepth == 0 || args.maxDepth > n)
            args.maxDepth = n;
        uint h = Math.Min(workGroupSizeX * 2, args.maxDepth);

        // 先做本地 bitonic merge，再逐级扩展到全局阶段。
        DispatchStage(cmd, args, h, Stage.LocalBMS);

        h *= 2;

        for (; h <= Math.Min(n, args.maxDepth); h *= 2)
        {
            DispatchStage(cmd, args, h, Stage.BigFlip);

            for (uint hh = h / 2; hh > 1; hh /= 2)
            {
                if (hh <= workGroupSizeX * 2)
                {
                    DispatchStage(cmd, args, hh, Stage.LocalDisperse);
                    break;
                }

                DispatchStage(cmd, args, hh, Stage.BigDisperse);
            }
        }
    }
}
