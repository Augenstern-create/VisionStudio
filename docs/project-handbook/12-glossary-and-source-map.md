# 术语表与源码导航

## 1. 术语表

| 术语 | 含义 |
| --- | --- |
| DAG | 有向无环图，工作流的数据依赖结构 |
| Operator Descriptor | 算子接口、端口、参数、名称和限制的声明 |
| Operator Instance | 画布上的具体节点，有唯一 Node ID 和独立参数 |
| Port | 节点的强类型输入或输出连接点 |
| Target Node | 用户当前要查看/执行的结果节点 |
| Execution Slice | 目标节点与全部上游组成的执行子图 |
| Cache Key | 由类型、参数、上游键和外部指纹生成的 SHA-256 |
| Revision | 工作流修订号，用于并发冲突检测 |
| Request ID | 自动化请求唯一标识，用于幂等重试 |
| Graph Action | AI 可提交的有限工作流编辑动作 |
| add_pipeline | 模型侧紧凑管线表示，保存前展开为普通节点和连接 |
| Provider | 向本地或远程模型发送标准会话请求的实现 |
| GGUF | llama.cpp 常用的量化模型文件格式 |
| mmproj | 多模态模型的视觉投影文件 |
| DPAPI | Windows 当前用户范围的数据保护机制 |
| Atomic Write | 先写临时文件，再用原子移动替换目标文件 |
| UIAutomation | Windows 桌面可访问性和自动化接口 |
| Runtime Frame | 由运行平台采集并注入 `Input.RuntimeFrame` 的单帧图像 |
| Result Mapping | 把工作流节点输出映射为主图、附加图或结构化结果的项目级配置 |
| Bounded Frame Queue | 限制容量并定义背压/丢帧策略的采集与执行缓冲区 |

## 2. 核心源码导航

### 数据模型与执行

- `src/VisionStudio.Core/WorkflowModel.cs`：节点、连接、端口、参数和 JSON。
- `src/VisionStudio.Core/WorkflowValidator.cs`：图合法性和拓扑顺序。
- `src/VisionStudio.Core/Operators.cs`：注册表、Context、执行器、目标切片和缓存键。
- `src/VisionStudio.Core/ProjectSystem.cs`：项目目录、保存、图片导入、操作记录。
- `src/VisionStudio.Core/WorkflowAutomation.cs`：批量修改、revision、幂等、撤销和执行接口。

### OpenCV 与代码生成

- `src/VisionStudio.Operators.OpenCv/OpenCvOperators.cs`：基础高频算子和项目缓存。
- `src/VisionStudio.Operators.OpenCv/ExtendedOperatorCatalog.cs`：扩展目录及多类算子。
- `src/VisionStudio.Operators.OpenCv/CrackMeasurementOperator.cs`：专项裂纹基线。
- `src/VisionStudio.Operators.OpenCv/OpenCvWorkflowAutomationRunner.cs`：自动化执行结果适配。
- `src/VisionStudio.CodeGen/CSharpWorkflowGenerator.cs`：C# 导出。

### 桌面端

- `src/VisionStudio.App/MainWindow.axaml` / `.cs`：启动页、工作台、项目、结果、属性和 AI 面板。
- `src/VisionStudio.App/GraphCanvas.cs`：画布绘制和交互。
- `src/VisionStudio.App/ChatMessageControl.cs`：可选择、引用和代码复制的消息控件。
- `src/VisionStudio.App/SettingsWindow.cs`：本地/远程/标准模型设置。
- `src/VisionStudio.App/UiThemeManager.cs`：三主题资源。
- `src/VisionStudio.App/UiLocalization.cs` 与 `Resources/Strings.*.json`：本地化。

### AI

- `src/VisionStudio.Ai.Abstractions/AiContracts.cs`：插件和 Provider 稳定契约。
- `src/VisionStudio.Ai/AiWorkflowAgent.cs`：提示规则、计划解析、展开、校验和应用。
- `src/VisionStudio.Ai/AiImageReferenceCatalog.cs`：IMG 编号和真实节点解析。
- `src/VisionStudio.Ai/AiPluginLoader.cs`：清单验证和动态加载。
- `src/VisionStudio.Ai/AiModelProfileStore.cs`：模型配置与上下文。
- `src/VisionStudio.Ai/OpenAiCompatibleProvider.cs`：远程标准接口。
- `plugins/VisionStudio.Ai.LlamaCpp`：llama-server Provider 插件。

### Web、CLI 和测试

- `src/VisionStudio.Runtimes.App/ProjectRuntimeService.cs`：项目加载、逐帧执行和结果映射。
- `src/VisionStudio.Runtimes.App/FrameSources.cs`：图片、文件夹、视频、相机和模拟输入边界。
- `src/VisionStudio.Runtimes.App/RuntimeController.cs`：状态机、触发、有界队列和统计。
- `src/VisionStudio.Runtimes.App/RuntimeProfile.cs`：项目级运行配置。

- `src/VisionStudio.Web/Program.cs`：HTTP 路由、鉴权和安全头。
- `src/VisionStudio.Web/WebProjectStore.cs`：服务器项目会话。
- `src/VisionStudio.Web/wwwroot`：响应式 Web 工作台。
- `src/VisionStudio.Cli/Program.cs`：命令行入口。
- `tests/VisionStudio.Tests`：xUnit 测试。
- `scripts/test-changes.ps1`：分维度测试入口。
- `scripts/test-ui-workflows.ps1`：真实桌面 E2E。
- `scripts/test-web.ps1`：Web API 全链路。
- `scripts/test-ai-second-image-project.ps1`：真实多图 AI 项目回归。

## 3. 现有专项文档

本手册用于理解和讲解；实现细节与验收规则继续参考：

- `docs/architecture.md`
- `docs/development.md`
- `docs/boundaries.md`
- `docs/ai-plugin-architecture.md`
- `docs/ai-agent-flow.md`
- `docs/ai-workflow-automation-api.md`
- `docs/operator-naming-and-description-standard.md`
- `docs/ui-style-guide.md`
- `docs/testing.md`
- `docs/web-deployment.md`
- `docs/platform-release.md`
- `docs/runtimes-app-architecture.md`
- `docs/runtimes-app-boundaries.md`
- `docs/runtimes-app-flow.md`
- `docs/runtimes-app-testing.md`
