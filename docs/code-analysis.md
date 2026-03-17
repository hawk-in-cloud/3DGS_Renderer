# UnityGaussianSplatting 代码解析

## 你先看这 30 秒

这个项目的主线非常简单：

1. 从磁盘读 `point_cloud.ply`（高斯点数据）。
2. 上传到 GPU。
3. 每帧先按相机深度排序，再按排序结果绘制透明高斯。
4. 结束时释放资源。

你现在先只盯一个文件：`Assets/Scripts/GaussianSplatRenderer.cs`。  
这个文件就是“运行时总控”。

---

## 一图总览（文字版）

`OnEnable`  
-> 读数据（PLY/JSON）  
-> 建 GPU Buffer（数据、位置、排序键、排序索引）  
-> 初始化排序器  
-> 绑定到材质

`OnPreCullCamera`（每个相机每帧）  
-> `SortPoints(cam)` 计算深度键并 GPU 排序  
-> `DrawProcedural` 绘制所有 splat

`OnDisable`  
-> 解绑事件  
-> 释放 NativeArray 和 GraphicsBuffer

---

## GaussianSplatRenderer.cs 逐段解读

### 1. 这个脚本到底管什么

它做 4 件事：

1. 加载数据。
2. 管理 GPU 资源生命周期。
3. 每帧排序。
4. 发起绘制。

一句话：它是“数据加载 + 排序 + 绘制”的总调度器。

---

### 2. 字段速查（你最常改的）

1. `m_PointCloudFolder`  
数据根目录。脚本会在这个目录下找固定相对路径。

2. `m_Use30kVersion`  
优先读取 `iteration_30000/point_cloud.ply`，没有就回退 7000。

3. `m_ScaleDown`  
降采样比例，值越大，实际渲染点数越少。

4. `m_Material`  
渲染材质（最终画 splat 用）。

5. `m_CSSplatUtilities`  
计算用 compute shader：初始化索引、计算深度键值。

6. `m_CSGpuSort`  
GPU 排序 compute shader（bitonic）。

---

### 3. `InputSplat` 是全项目最硬的契约

`InputSplat` 不是普通业务结构体，它是“文件格式映射”：

1. 字段顺序必须固定。
2. 字段类型必须固定。
3. 总字节数必须和 PLY 的 `vertexStride` 一致。

为什么这么严格：  
因为读取后是直接 `Reinterpret<InputSplat>`，不是逐字段解析。  
一旦布局不一致，后面 shader 读到的就是错位垃圾数据。

---

### 4. `LoadPLYSplatFile`：从 PLY 到 `InputSplat[]`

它做了 5 步：

1. 选路径（30k 或 7000）。
2. 调 `PLYFileReader.ReadFile` 读二进制。
3. 校验 `sizeof(InputSplat)` 是否匹配 `vertexStride`。
4. 调 `ReorderSHs` 重排 SH 数据布局。
5. 返回 `NativeArray<InputSplat>`。

这里最关键的是第 3、4 步：  
3 防止错读，4 保证 shader 能按 `float3 shN` 直接取 RGB。

---

### 5. `ReorderSHs`：为什么要重排

输入 SH 常见是“分通道分块”：

1. `R0..R14`
2. `G0..G14`
3. `B0..B14`

而 shader 想要的是“每个系数 RGB 连续”：

1. `R0,G0,B0`
2. `R1,G1,B1`
3. ...

所以这里一次性重排，避免在渲染阶段反复处理。

---

### 6. `LoadJsonCamerasFile`：相机位姿转换

这部分不是渲染必要条件，但对调试很有用：

1. 读 `cameras.json`。
2. 解析每个相机的位置和旋转轴。
3. 做坐标系符号修正（数据集坐标到 Unity 坐标）。
4. 存到 `CameraData[]`，供 Inspector 滑条跳转相机。

---

### 7. `OnEnable`：初始化全管线

按执行顺序看：

1. 订阅 `Camera.onPreCull` 事件。
2. 校验材质和 compute 引用是否齐全。
3. 加载相机数据和点云数据。
4. 按 `m_ScaleDown` 计算实际渲染数量。
5. 计算包围盒（给 `DrawProcedural` 用）。
6. 创建并上传 GPU Buffer：
- 位置 Buffer
- 完整数据 Buffer
- 排序 key/value Buffer
7. 调 compute kernel0 初始化排序索引。
8. 把 Buffer 绑到材质（`_DataBuffer`、`_OrderBuffer`）。
9. 初始化 GPU 排序器参数。

你可以把 `OnEnable` 理解成：  
“把磁盘数据搬上 GPU，并把每帧渲染管线全部接好。”

---

### 8. `OnPreCullCamera`：每帧入口

每个相机每帧都会进来：

1. 检查 `m_GpuData` 是否有效。
2. 调 `SortPoints(cam)`。
3. 调 `Graphics.DrawProcedural(...)` 绘制。

这里的绘制是“按实例绘制 splat”，每个 splat 走一个 quad（6 顶点）。

---

### 9. `SortPoints`：每帧最关键函数

分两步：

1. `m_CSSplatUtilities` kernel1  
把位置变换到相机空间，计算每个 splat 的深度键值。

2. `IslandGPUSort`  
对 `(key, index)` 做 GPU 排序，输出排序后的索引顺序。

为什么这是核心：  
透明累积依赖绘制顺序，顺序不对，颜色就错。

---

### 10. `OnDisable`：收尾

1. 解绑 `Camera.onPreCull`。
2. 释放 `NativeArray`。
3. 释放所有 `GraphicsBuffer`。

这是必须的，不做容易泄漏 CPU 内存和显存。

---

## 你第一次读这个文件，建议这样读

1. 先读 `OnEnable`，建立“初始化地图”。
2. 再读 `OnPreCullCamera -> SortPoints`，建立“每帧地图”。
3. 最后回看 `LoadPLYSplatFile/ReorderSHs`，理解数据格式约束。

按这个顺序，你会比从上到下硬读轻松很多。

---

## PLYFileReader.cs（第二节）

## 你先看这 20 秒

`PLYFileReader` 只做一件事：  
把二进制 PLY 文件读取成“原始字节数组 + 结构信息（点数、步长、属性名）”。

它不负责渲染，也不负责数学计算。  
它的价值是：保证 `GaussianSplatRenderer` 拿到一份可直接 `Reinterpret` 的干净数据。

---

## 一图总览（文字版）

`ReadFile(path)`  
-> 打开文件流  
-> 逐行解析 header（直到 `end_header`）  
-> 得到 `vertexCount` / `vertexStride` / `attrNames`  
-> 一次性读取后续二进制顶点数据到 `NativeArray<byte>`

`GaussianSplatRenderer.LoadPLYSplatFile`  
-> 调这里拿到字节  
-> 验证 `vertexStride == sizeof(InputSplat)`  
-> 重排 SH  
-> `Reinterpret<InputSplat>`

---

## PLYFileReader.cs 逐段解读

### 1. 这个文件为什么是“基础设施”

你可以把它当成“文件层适配器”：

1. 面向磁盘文件格式（PLY header + binary body）。
2. 输出给运行时主控脚本（点数量、每点字节数、原始 bytes）。

它不关心高斯公式，但它决定了后面能不能正确读取字段。

---

### 2. `ReadFile` 的核心流程

函数签名里 4 个输出参数是重点：

1. `vertexCount`：顶点总数。
2. `vertexStride`：每个顶点占多少字节。
3. `attrNames`：属性名列表（调试很有用）。
4. `vertices`：真实二进制数据（`NativeArray<byte>`，Persistent）。

执行细节：

1. `FileStream` 打开文件。
2. 循环 `ReadLine`，直到遇到 `end_header`。
3. 解析 `element vertex N` 得到点数。
4. 解析 `property type name`，把 type 换算成字节并累计到 `vertexStride`。
5. 头结束后，一次性读剩余字节到 `vertices`。
6. 若读取长度不等于预期长度，直接抛 `IOException`。

为什么“一次性读取”：

1. 代码简单，便于后续直接重解释内存。
2. 对这个 toy 项目足够高效。

---

### 3. `ElementType` + `TypeToSize` 在干什么

`TypeToSize` 是把 PLY 文本类型映射到字节长度：

1. `float -> 4`
2. `double -> 8`
3. `uchar -> 1`

这里的设计含义：

1. 只支持本项目当前用到的类型组合。
2. 如果你以后引入 `int16`、`uint` 等类型，要在这里补映射，否则 stride 会算错。

---

### 4. `ReadLine` 为什么自己写

它没有用 `StreamReader`，而是手写按字节读到 `\n`：

1. 避免引入文本读取器状态和额外复杂度。
2. 对 PLY header 这种短文本足够可靠。

注意点：

1. 它只按 `\n` 断行，Windows 的 `\r\n` 会留下 `\r`，但当前 header 解析逻辑通常可接受。
2. 如果你遇到特殊 PLY 文件解析异常，优先检查换行符和编码。

---

### 5. `TestPlyReader`（菜单工具）怎么用

菜单位置：`Tools/Test PLY Reader`

它做三件事：

1. 让你选一个 `.ply` 文件。
2. 打印 `vtx/stride/attrs` 到 Console。
3. 导出同名 `.bytes` 文件方便检查原始数据。

这是排查“为什么这个模型读不进来”的第一工具。

---

## 和 GaussianSplatRenderer 的关系（你必须连起来看）

链路是：

1. `PLYFileReader.ReadFile` 给出 `vertexStride + bytes`。
2. `GaussianSplatRenderer` 验证 stride。
3. 验证通过后才重排 SH 并转成 `InputSplat`。

所以如果渲染异常，先查这 3 件事：

1. header 里的属性顺序是否符合预期。
2. `vertexStride` 是否等于 `sizeof(InputSplat)`。
3. SH 布局是否需要重排且重排规则是否匹配你的数据集。

---

## 这个文件最容易踩的坑

1. 新数据集属性类型变了，但 `TypeToSize` 没同步改。
2. header 看起来对，但属性顺序和 `InputSplat` 不一致。
3. 忘了 `vertices.Dispose()`（编辑器测试工具里已经做了释放）。

---

## 你读这个文件的推荐顺序

1. 先看 `ReadFile` 主流程。
2. 再看 `TypeToSize`，确认 stride 怎么算。
3. 最后看 `TestPlyReader`，掌握调试入口。

掌握这三个点，你就能快速定位“读文件层”的大多数问题。
