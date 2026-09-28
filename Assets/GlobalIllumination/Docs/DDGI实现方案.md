# DDGI × URP14 实现方案（FlowerSea）

基于 Unity 2022.3.62f1c1 + URP 14.0.12（内嵌源码包）的动态漫反射全局光照实现。

## 一、总体架构

DDGI（Majercik et al. 2019, JCGT）核心是探针网格 + 光线追踪 + 时域融合。

每帧流程（DDGIUpdatePass，挂在 BeforeRenderingOpaques）：

1. 网格滚动 Rebasing：相机移动超过 1 个探针间距时，网格原点按整数格平移
2. 探针重定位 Relocation：把陷入几何体的探针沿 SDF 梯度推出（P3）
3. 光线追踪 Tracing：每探针 N 条射线，与场景 SDF 求交（P3）
4. 深度矩更新：命中距离的均值/方差写入深度矩图集，供 Chebyshev 可见性测试（P3）
5. 辐照度更新：新辐照度与历史帧 hysteresis 混合（P3）
6. Border Padding：八面体图边界外扩，供双线性采样（P3）
7. SetGlobalTexture / SetGlobalVector 参数下发

着色阶段：修改 URP 的 `GlobalIllumination.hlsl`，在 Lit / ComplexLit（Forward 与 Deferred 共用路径）采样探针网格（P4）。

关键决策：光线追踪采用 SDF 软件光追（球体追踪），不用硬件 RT。理由：URP14 官方不支持 DXR 管线集成；SDF 方案纯 Compute Shader，桌面通用，后续可加 RT 加速路径。屏幕空间深度步进作为动态物体的可选补充。

## 二、资源与数据布局

| 资源 | 格式 | 说明 |
|---|---|---|
| `_DDGIIrradiance` | RGBA16F（备选 RGBM/RGBA8） | 辐照度八面体图图集，每探针 8×8 texel + border |
| `_DDGIDepth` | RG16F | 深度矩图集，每探针 16×16 texel，存 (mean, mean²) |
| 场景 SDF | Texture3D 图集（brick 化，每 brick 64³） | 线性采样，负值=内部 |

探针网格默认 32³、间距 1m，随相机滚动。图集布局：probeIndex = x + y·countX + z·countX·countY，图集 X 轴排 probeCounts.x 个探针，Y 轴排 probeCounts.y × probeCounts.z 个探针，pitch = texels + 2×border。

场景配置组件 `DDGIVolume`：probeCounts、spacing、lockVertical、hysteresis、raysPerProbe、八面体分辨率、border、normalBias/viewBias、energyScale。

## 三、代码结构（Assets/GlobalIllumination/）

```
Runtime/
├── DDGIVolume.cs           场景配置 + Gizmo 调试（网格框 + 探针点）+ SDF 参数
├── DDGIManager.cs          单例：图集分配、滚动 Rebasing、全局参数下发
├── DDGISdfManager.cs       SDF 烘焙：三角形收集、3D 纹理、Compute 调度、全局参数
└── DDGIRendererFeature.cs  DDGIUpdatePass + DDGIDebugPass（图集预览 / SDF 光追视图）
Shaders/
├── DDGISdfBuild.compute    ClearCounts / RasterizeTriangles / BuildSDF 三个内核
└── DDGIDebugSDF.shader     Hidden/DDGI/DebugSDF：全屏球体追踪 SDF 调试视图
Editor/
└── DDGISdfBakeMenu.cs      Tools/DDGI/Bake SDF 菜单
```

命名空间统一为 `FlowerSea.GlobalIllumination`，类名保留 DDGI 前缀。

## 四、URP 魔改点（最小侵入，仅 2 处）

1. `Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl`：
   `GlobalIllumination()` 内加 `_DDGI_ON` 分支调用 `SampleDDGI(positionWS, normalWS)`，Lit/ComplexLit 的 Forward、Forward+、GBuffer/Deferred 全覆盖。
2. `Shaders/Lit.shader` / `ComplexLit.shader`：加 `multi_compile _ _DDGI_ON`，include DDGISample.hlsl（采样函数文件放 URP 包内 ShaderLibrary/ 目录，保持相对 include 路径干净）。

采样核心：8 邻域探针三线性加权；每探针先做法线方向偏置（采样点沿 N 外推 0.3×spacing），再用深度矩 Chebyshev 测试剔除被遮挡探针；按法线方向采样八面体图求和。`_DDGI_ON` 时替换而非叠加 `SAMPLE_GI` 环境项，保证能量守恒。

## 五、SDF 生成管线（P2 已实现）

算法流程（DDGISdfBuild.compute）：

1. **RasterizeTriangles**：每线程处理一个三角形（CPU 侧已变换到世界空间），计算 AABB 对应的体素范围，对每个候选体素做「体素中心到三角形的精确最近点距离 ≤ 0.87×体素尺寸」的保守判定；命中则 `InterlockedAdd` 占一个槽位（每体素 12 槽预算，溢出丢弃并计入 count）
2. **BuildSDF**：每线程处理一个体素，遍历其槽位内三角形求精确最近距离 `best`；同时做符号投票：按面法线平面方程求 planeDist，以 `1/max(d, 0.25×voxel)²` 加权累加，vote<0 判内部 → 取负；无三角形体素写 +1000
3. 最近点算法为 Ericson《Real-Time Collision Detection》标准 point-triangle 分段实现

数据：

- 场景 SDF 为单张 R32F 3D 纹理（对齐 DDGIVolume bounds，分辨率 = size/voxelSize，每轴上限 256），保证所有平台 Typed UAV Load 可用
- 全局参数：`_DDGISDF`（tex3D）、`_DDGISDFOriginSize`（xyz=网格原点，w=体素尺寸）、`_DDGISDFTexSize`

收集规则（DDGISdfManager.CollectTriangles）：sdfGeometryRoot 下的 MeshRenderer，跳过未激活/禁用、renderQueue≥3000（半透明）、无 MeshFilter 的对象；CPU 侧 localToWorldMatrix 变换后拼成扁平 float 数组上传

使用方式：

1. DDGIVolume 上把 **Sdf Geometry Root** 指向场景静态几何的根节点，按需调 **Sdf Voxel Size**（默认 0.5m）
2. 菜单 **Tools/DDGI/Bake SDF** 烘焙
3. Renderer Feature 的 **Debug Mode** 切到 **SDFRaymarch**：屏幕显示球体追踪结果——命中面用法线+Lambert 着色，内部命中显示红色，未命中深蓝背景

已知限制（P3+ 解决）：

- 符号投票对非封闭网格（花瓣、单片树叶）会有误差，GI 遮蔽场景建议用封闭低模替代
- 烘焙目前为编辑器菜单手动触发；进入 Play（domain reload）后静态实例丢失需重新烘焙，P3 接入运行时自动烘焙
- Compute Shader 通过 AssetDatabase 加载（Editor 路径），运行时自动烘焙时改为 Feature 上序列化引用

## 六、分期计划与验收标准

| 阶段 | 内容 | 工期 | 验收标准 | 状态 |
|---|---|---|---|---|
| P1 框架 | RendererFeature/Pass/Volume、图集分配、Rebasing、调试视图 | 3~5 天 | Gizmo 网格随相机滚动；FrameDebugger/全屏可见图集 | ✅ 完成 |
| P2 SDF 管线 | 体素化、距离场、brick 管理、SDF 剖面调试 | 1~1.5 周 | SDF 剖面与场景吻合；球体追踪不穿墙 | ✅ 代码完成，待编辑器验收 |
| P3 追踪+更新 | 射线打包、追踪、深度矩、辐照度混合、border、重定位 | 1.5 周 | 辐照度调试视图合理；光源移动 0.5s 收敛无闪烁 | ⬜ |
| P4 着色接入 | GlobalIllumination.hlsl 挂钩、keyword、双 pass、bias 调参 | 3~5 天 | 颜色溢出正确；无漏光/条带 | ⬜ |
| P5 稳定+性能 | 分帧更新、自适应射线、TAA 兼容、profile、参数预设 | 1 周 | 1080p 稳定 60fps；运动场景无残留噪点 | ⬜ |
| P6 打磨 | Gizmo/UI、动态物体屏幕空间补充、可选 RT 加速、回退档 | 按需 | — | ⬜ |

## 七、风险与对策

- 漏光：Chebyshev + 重定位 + 法线偏置配合调参；探针间距不超过最小墙体厚度
- 条带伪影：8×8 起步，必要时升 10×10 或加大 border
- 带宽压力：仅更新本轮激活探针区域；必要时 async compute
- URP 版本耦合：仅改 2 个文件，升级时 diff 可合并
- 能量守恒：RGBM 编码范围与 ∏ 归一化易出错，P3 预留数值校验

## 八、关键 API 速查（URP14 实测）

- `ScriptableRendererFeature.Create()` 是 **public** abstract（URP12 之前为 protected）
- `GraphicsFormat` 在 `UnityEngine.Experimental.Rendering` 命名空间
- `ScriptableRenderer` 在 `UnityEngine.Rendering.Universal` 命名空间
- `RTHandles.Alloc(RenderTexture)`、`Blitter.BlitCameraTexture(cmd, src, dst)`、`renderer.cameraColorTargetHandle` 均可直接使用
