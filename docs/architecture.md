# Vision Studio 架构设计

## 1. 架构目标

Vision Studio 是内部使用的视觉算法快速验证工具。核心体验是“选择项目 → 添加节点 → 拖拽连接端口 → 点击节点看结果 → 调参数即时刷新 → 导出普通 C#”。第一版优先 Windows 桌面，同时让核心库保持跨平台；移动端只保留未来兼容性，不承诺第一版完整编辑体验。

关键决策：

- C#/.NET 9 为主语言，Avalonia 为桌面 UI。
- 图像值直接使用 EmguCV `Mat`，不增加 `ImageFrame` 包装。
- `Mat.DataPointer` 是未来 C++ SDK 的零额外封装入口，但原生侧不得跨调用持有该指针。
- UI、DAG、OpenCV 算子和代码生成相互隔离。
- UI 与未来 AI 助手共享 `IWorkflowAutomationApi` 应用服务；AI 不操作控件或工作流文件。
- 项目元数据与工作流分文件保存，项目目录可以整体复制、Git 管理和归档。

## 2. 分层与依赖

```text
VisionStudio.App (Avalonia)
        │
        ├── VisionStudio.Core
        ├── VisionStudio.Operators.OpenCv
        └── VisionStudio.CodeGen

VisionStudio.Cli
        ├── VisionStudio.Core
        ├── VisionStudio.Operators.OpenCv
        └── VisionStudio.CodeGen

VisionStudio.Web (ASP.NET Core + browser UI)
        ├── VisionStudio.Core
        ├── VisionStudio.Operators.OpenCv
        └── VisionStudio.CodeGen
```

- `Core`：项目、工作流模型、端口类型、验证、拓扑排序、执行编排、序列化。
- `Operators.OpenCv`：EmguCV 算子及 `Mat` 生命周期管理。
- `CodeGen`：把 DAG 展开为不依赖 Studio Runtime 的普通 EmguCV C#。
- `App`：启动项目页、圆角正交节点画布、中文属性编辑和选中节点预览。
- `Cli`：创建、校验、执行和导出，作为自动化入口。
- `Web`：提供响应式浏览器工作台、受令牌保护的 HTTP API、服务器项目隔离及 OpenCV 服务端执行。

AI 自动化通过 `WorkflowAutomationApiHost` 获取当前项目会话。`WorkflowAutomationSession` 位于 Core，负责目录查询、快照、事务编辑、乐观并发、幂等、项目保存和事件；`OpenCvWorkflowAutomationRunner` 位于算子层，负责执行并把可视结果发布到项目缓存。Avalonia 只订阅事件刷新画布与预览，完整协议见 `docs/ai-workflow-automation-api.md`。

## 3. 核心模型

`.vflow` 包含 `nodes` 与 `connections`。每个节点保存稳定 ID、算子类型、画布位置和 JSON 参数。端口由算子描述符定义，不写入工作流，避免元数据漂移。

端口使用 `NodeValueType` 强类型校验。当前实现 `Mat`、数字、布尔、字符串、点、点集、直线、圆、矩形、轮廓、矩阵、集合与任意类型。多输入节点为每个输入定义独立端口；多输出节点（例如 Split、Contours、TemplateMatch）为每个结果定义独立可连接端口。

执行器针对当前选中节点提取“该节点 + 全部祖先”的执行切片，验证后做确定性拓扑排序。每个算子只从 `NodeExecutionContext` 读取输入、写输出。执行结果拥有本次加载和产生的全部 `IDisposable`，成功与异常路径都会统一释放。

## 4. 节点缓存与失效传播

每个节点缓存键由以下内容计算 SHA-256：

```text
算子类型 + 排序后的参数 JSON + 输入端口/上游缓存键 + 外部输入指纹
```

Load Image 与 Video 的外部指纹包含文件长度和最后写入时间。上游参数或连接改变会改变其缓存键，并自然改变所有下游键；只移动节点不会改变执行键。图像结果以 PNG 写入 `images/cache/<node-id>`，数值、布尔、点、线、圆、矩形、轮廓和点集以 JSON 写入同一节点缓存目录。Camera 和 Save Image 具有实时采集/文件写入副作用，因此不缓存节点本身。

轮廓、定位和测量节点遵循“双结果”约定：结构化结果通过强类型输出端口供下游连接，同时 `Image` 端口返回在输入图上绘制了轮廓、形状或文字的预览图。没有图像输入的纯数据节点在结果区显示端口名称和值。

## 5. 项目格式

```text
ProjectName/
├── 项目名.vstudio
├── workflows/main.vflow
├── images/input
├── images/cache
├── images/output
├── output
├── models
├── resources
├── snapshots
├── generated
└── .project/autosave
```

`项目名.vstudio` 是 Vision Studio 的专属项目入口文件，只保存项目级信息。Windows 当前用户文件关联将 `.vstudio` 注册到 Vision Studio，双击时把项目文件路径作为首个启动参数传入。应用只接受 `.vstudio` 项目文件。工作流修改通过原子替换保存；每次显式保存前可写入轻量快照。最近项目索引位于用户应用数据目录，不放进业务项目。

外部图片通过属性面板导入时立即复制到 `images/input`，工作流只保存项目相对路径。`images/cache` 保存可重用节点输出，`images/output` 保存业务导出图像。

桌面端 C# 导出固定写入 `output/GeneratedVisionAlgorithm.cs`，不弹出外部路径选择框。只包含早期基础节点时生成独立 EmguCV 类；包含扩展算子时生成引用 `VisionStudio.Core` 与 `VisionStudio.Operators.OpenCv` 的运行时支持版，保证完整算子目录都可导出执行。`generated` 保留给 CLI 或模板生成。

## 6. 操作历史与撤销

编辑器在修改前保存工作流 JSON 快照，内存中保留最近 100 步。Ctrl+Z 恢复上一份快照并触发自动保存。覆盖范围包括添加/删除/移动节点、创建/删除连接、参数变化和输入图片变化。

用户操作同时追加到 `.project/operations.jsonl`，右侧操作记录面板显示最近 200 条。操作日志用于追踪，不承担工作流恢复职责；真正的长期恢复仍使用 `snapshots`。

AI 批量修改同样保存修改前快照并写入操作日志。整个批次在副本上完成结构验证后一次提交，对 UI 表现为一个可撤销步骤；`requestId` 防止调用重试造成重复节点，`Revision` 防止覆盖并发用户编辑。

每次追加操作记录都会重置 800 毫秒自动保存计时器；空闲后以原子替换方式写回 `workflows/main.vflow` 和 `.project/autosave/main.vflow`，但不为每次自动保存创建快照。显式保存仍创建长期快照。

## 7. 画布与结果布局

中央编辑区只承载节点画布。画布使用世界坐标保存节点位置，视口平移量仅属于 UI 状态，不写入工作流；网格随平移量渲染，所有节点、端口、连接命中和自动化边界统一经过世界/屏幕坐标转换。

新增节点会扫描当前视口的网格空位，避免覆盖已有节点。“自动排版”根据连接计算 DAG 层级，同层节点纵向分离、上下游从左到右排列，并作为一次可撤销操作记录。

结果预览位于右侧属性面板上方。适应模式根据预览区域和像素尺寸计算比例；滚轮或按钮缩放范围为 5%–1600%，放大后由滚动容器查看。操作记录使用默认收起的向下展开面板。

算子描述符分别保存规范中文、英文名称和介绍作为资源缺项时的回退。画布和按大类折叠的算子菜单只显示当前语言名称；属性面板显示稳定接口名，翻译不参与工作流持久化标识。

界面本地化资源使用 `Resources/Strings.<语言代码>.json`。应用启动时扫描资源目录并自动生成项目页语言菜单；切换后刷新静态界面、分类、算子名称、算子介绍、端口和参数接口名。属性面板显示稳定算子接口名，不并排显示另一种语言的名称。资源缺项时回退到描述符内置的规范中文或英文元数据。

状态栏、选择器、对话框、操作记录和自动化辅助文本同样通过资源键生成。历史操作日志中的中文动作在英文显示时进行兼容映射；底层算子抛出的中文异常不会直接泄漏到英文界面，而会转换成带异常类型的英文安全摘要。项目名称、文件名和路径属于用户数据，不进行翻译。

## 8. 扩展机制

新增算子需要同时提供 `OperatorDescriptor` 与 `IVisionOperator`。同一描述符驱动端口校验、属性编辑、缓存类型和序列化；执行器会检查每个声明的输出确实被产生。原生 SDK 算子应通过稳定 C ABI 暴露，C# 侧使用 `SafeHandle` 管理长期句柄，只在单次调用内传递 `Mat.DataPointer`。
