# 滑条静态 GPU 网格

## 参考范围

参考仓库：[McKay42/McOsu](https://github.com/McKay42/McOsu)，固定提交 `db2add20ea291f6f3b6d022fcd4eba100a5bd161`。

- [`OsuSliderRenderer.cpp`](https://github.com/McKay42/McOsu/blob/db2add20ea291f6f3b6d022fcd4eba100a5bd161/src/App/Osu/OsuSliderRenderer.cpp)：预生成网格、区间绘制和 framebuffer 范围限制。
- [`OsuSlider.cpp`](https://github.com/McKay42/McOsu/blob/db2add20ea291f6f3b6d022fcd4eba100a5bd161/src/App/Osu/OsuSlider.cpp)：单独绘制蛇形动画的平滑端点。

McOsu 使用 GPLv3。本次参考静态网格与动态端点的设计，基于当前 MIT 框架独立实现，未复制其源码、shader 或网格数据。

## 数据流

1. `SnakingSliderBody.Refresh()` 将完整的 `SliderPath.CalculatedPath` 设置到框架 `Path.Vertices`，固定完整路径的尺寸、位置和局部包围盒。
2. `SliderPath.GetPathToProgressRange()` 对累计距离进行下界、上界查找，返回原始顶点区间及精确插值端点。重复距离点保留原来的包含规则，起止点位于同一段时返回零个中间顶点。
3. `Path.DrawRange` 只使 framebuffer 绘制版本失效，不改变完整几何的版本。命中检测使用相同的可见区间。
4. 每个 `PathDrawNode` 在首次实际绘制时生成完整局部网格，并创建不可变 GPU 缓冲。几何或半径改变时创建新网格；位置、缩放、颜色和蛇形范围变化不重传完整网格。
5. 重绘时提交可见完整线段的索引区间，再通过小型动态 batch 补画一至两段边界几何。保留现有距离场 shader、最大值混合和皮肤渐变纹理。
6. 反向箭头直接查找端点附近的第一个不同坐标，不再为了朝向每帧复制可见曲线。`CurrentCurve` 保留读取入口，按需物化。
7. 应用范围前检查 `SliderPath` 实例及其版本。池化复用或回放跳转时，即使子对象的调度刷新尚未执行，或蛇形进度与上次相同，也先同步完整顶点、尺寸及偏移，再计算范围；几何未变时只进行常数时间比较。

## 纹理与直接绘制

- 当前滑条恢复使用 `SmoothPath` 的一行 RGBA 渐变纹理。只有颜色、边框或半径等使纹理缓存失效时才重新生成、上传；蛇形进度本身不会触发这一上传。
- 完整路径的 R16Float 距离场由 GPU 绘制到 framebuffer，以最大值混合合并线段；随后读取距离场及渐变纹理，应用颜色、透明度和遮罩。距离场重绘不涉及 CPU 上传整张路径图片。
- 参数化渐变实验已按用户要求撤回，包括 `GradientPath`、`PathGradient`、`sh_PathGradient.fs`、三个皮肤的参数化公式，以及为其添加的 Path 扩展点。保留静态网格、范围绘制、回放路径同步和更早的优化。
- McOsu 普通桌面采用圆锥深度测试、shader 着色和离屏颜色合成；其 VR 双网格路径绕过专用滑条 framebuffer，但源码明确记录边框内侧平滑受限。未复制其实现。
- 下一阶段研究目标是消除每滑条的离屏路径纹理；严格无滑条纹理的方案还需将渐变采样移入数据缓冲。候选设计及限制见 [slider-direct-rendering-research.md](slider-direct-rendering-research.md)，目前未实施直接渲染。

## 后端实现

- `Rendering/StaticQuadMesh.cs`：内部不可变网格抽象，验证 quad 对齐和区间范围；长网格按 `IRenderer.MAX_QUADS` 切块，避免 ushort 索引溢出。
- `OpenGL/Buffers/GLStaticQuadMesh.cs`：独立 VAO、VBO、EBO，通过 `StaticDraw` 上传一次，随后只改变索引偏移和数量。释放时同步更新 renderer 的 VAO 绑定缓存。
- `Veldrid/Buffers/VeldridStaticQuadMesh.cs`：普通 Veldrid 使用一次性 buffer-update 命令上传，绘制仍经过原来的 global uniform、管线和资源绑定路径。
- Deferred 使用新建的不可变 DeviceBuffer，并记录 `DrawStaticMeshEvent`。事件处理时绑定静态缓冲并绘制，不经过每帧 `VertexManager` 的顶点复制或游标推进。端点仍使用原有动态上传路径。
- Dummy renderer 提供无绘制实现，保持无图形环境下的资源接口可用。

创建静态资源前先提交当前动态 batch，避免破坏资源绑定。网格从不原地更新；替换或销毁时使用既有 `ScheduleDisposal` 延迟释放策略。Deferred 事件保留原网格引用，避免旧事件读取被覆盖的数据。

## 裁剪、精度与开销

- 每 64 个 quad 缓存一个包围盒。每次绘制转换到屏幕空间，只有整个包围盒位于 framebuffer 范围外才跳过，并保留两像素余量。
- 连续可见块合并成区间提交，实际绘制再按 GPU 索引容量分块。
- 区间路径保留每条原始采样线段，不进行跨段合并，以保持采样索引、回折和重复点的对应关系。内部接缝继续缩短覆盖区域，动态边界提供完整圆帽。
- 通用、未指定 `DrawRange` 的 Path 继续使用现有动态路径及线段合并算法。
- 每个三缓冲 DrawNode 独立持有静态资源，因此同一滑条最多保留三份网格。资源随路径节点回收，未改用跨帧可写共享缓冲。
- 几何生成从逐帧变为首次绘制或几何改变时执行。蛇形更新包含二分查找；绘制仍需检查可见块，不能宣称整个过程为常数复杂度。
- 复杂曲线取消跨段合并后可能增加 GPU 顶点数与覆盖开销；静态缓存本身也占用显存。实际收益取决于曲线、后端和硬件。

## 验证范围

客户端 Debug 构建通过，0 警告、0 错误。未添加或运行测试，未运行应用、GPU 探测或性能基准。

用户运行该版本反馈性能提升，并在日志 `1792512833.runtime.log` 中报告回放回退到 0 后 `Path.DrawRange` 越界。代码已补上范围应用前的路径实例、版本同步，保持框架的范围校验。修复版客户端构建通过（0 警告、0 错误，26.23 秒）；未运行测试或应用，此修复的运行时效果仍待确认。

参数化渐变实验曾构建通过（0 警告、0 错误，27.23 秒）。用户反馈提升不明显后已撤回。

撤回后的客户端构建通过（0 警告、0 错误，29.91 秒）。仅编译客户端，未启动应用或运行测试。

运行时仍需确认蛇入、双向缩尾、重复点、零长度滑条、回放跳转、皮肤切换、渐变过渡和不同后端的视觉表现；编译成功不等于这些场景已通过验证。框架公共类型 `PathDrawRange`、`Path.DrawRange` 为新接口，正式发布需先发布框架包，再更新客户端依赖；在此之前客户端以 `-p:G0V0SourceBuild=true` 构建。
