# Vision Studio V2.0 发布范围与追踪矩阵

状态：已冻结
版本：2.0.0
目标平台：Windows x64、Ubuntu Server 24.04 x64 Web
暂缓平台：Ubuntu 24.04 x64 桌面版

## 1. 发布产品

| 产品 | 交付方式 | V2.0 内容 |
| --- | --- | --- |
| Vision Studio Desktop | Windows Setup、Windows 免安装压缩包 | 可视化编辑器、CLI、`VisionStudio.Runtimes.App`、Core 和 OpenCV 算子 |
| Vision Studio Runtime | 主安装包内组件 | 直接打开 `.vstudio`；单次/连续运行、图片/文件夹/视频/EmguCV 相机/模拟输入、状态机、触发、有界队列和结果映射 |
| Vision Studio Web | Ubuntu Server tar.gz | 响应式编辑器、ASP.NET Core API、服务器端 OpenCV 执行 |
| llama.cpp AI Provider | 独立插件包 | 本地 OpenAI 兼容 Provider；不进入主安装包 |
| 本地模型 | 用户自行配置 | llama.cpp Provider 和配置接口保留；预制模型一键下载暂不作为 V2.0 稳定能力 |

Ubuntu/Linux 桌面版、Android 和 iOS 明确不属于本次 V2.0 正式发布，不上传相应安装包。Linux 构建工程保留到后续具备真实桌面设备时继续验收。

## 2. 关键边界

- 编辑器设计工作流；Runtime 执行 `.vstudio` 项目；C# 导出用于二次开发，Runtime 不执行导出脚本。
- Runtime 复用现有 `WorkflowExecutor`。采集、触发、连续循环和上传不做成普通算子。
- V2.0 提供 EmguCV 通用相机源和软件手动触发边界，不包含厂商相机 SDK、PLC 或真实硬件触发适配器。
- V2.0 不包含检测记录长期数据库、MES/数据管理系统上传；以后通过结果消费者扩展。
- AI 只通过注册算子、强类型端口和 Graph Action 修改工作流，不执行模型生成的任意代码。
- 模型、llama.cpp、CUDA 和 Provider 插件不嵌入主安装包；V2.0 优先使用远程 OpenAI 兼容 API，本地模型由用户自行配置。
- 高级标定、卡尺、特征等部分算子仍可能只是稳定接口或基础预览，不能宣称已经达到生产计量精度。

## 3. 需求—实现—文档—测试矩阵

| V1.0 后主题 | 主要实现 | 权威文档 | 自动化证据 | V2.0 状态 |
| --- | --- | --- | --- | --- |
| 编辑器布局、主题、语言、画布与连线 | `VisionStudio.App`、`GraphCanvas`、资源文件 | `ui-style-guide.md`、`operator-naming-and-description-standard.md` | UiStyle、Typography、CanvasInteraction、DesktopInteraction | 纳入 |
| `.vstudio` 项目、自动保存、缓存、撤销 | `VisionStudio.Core`、桌面项目会话 | `architecture.md`、`development.md` | Project、Workflow、EndToEnd | 纳入 |
| 140+ 算子目录与常用 OpenCV 路径 | `VisionStudio.Operators.OpenCv` | `boundaries.md`、项目手册 05 | Operators | 纳入；精度边界保留 |
| C# 导出 | `VisionStudio.CodeGen`、CLI/桌面/Web 导出入口 | `development.md`、`testing.md` | CodeExport 独立编译执行 | 纳入 |
| Web 工作台与部署 | `VisionStudio.Web` | `web-deployment.md`、项目手册 08 | WebProjectStoreTests、Web API 脚本 | 纳入 |
| Web 损坏项目诊断 | `WebProjectStore.ListAsync/LoadAsync` 日志 | `code-review-report.md` | `ListAsync_LogsUnreadableProjectDirectory` | 已修复 |
| Web 共享加载取消隔离 | 独立共享加载令牌 + 每请求 `WaitAsync` | `code-review-report.md` | `GetAsync_CancelledCallerDoesNotCancelSharedLoad` | 已修复 |
| AI 插件、本地/远程配置、上下文 | `VisionStudio.Ai*`、LlamaCpp 插件、Settings | AI 架构/边界/流程文档 | Ai、AI UI、Provider 生命周期、远程配置 | 纳入 |
| 标准模型一键下载 | 标准目录、设置页、`LocalAiRuntimeManager` | `ai-plugin-architecture.md` | 下载成功、大小/SHA 校验、损坏删除 | 实验入口；不作为 V2.0 发布承诺 |
| AI 多图引用与流程校验 | IMG 目录、Graph Action、连接规范 | AI 连接规范与裂纹案例 | Ai、CrackMeasurement、ChatMessageControl | 纳入 |
| VisionAI 外部推理算子 | VisionAI 客户端/算子 | `visionai-operator-integration.md` | VisionAiOperatorTests、专项脚本 | 纳入；外部服务独立部署 |
| Runtime 直接打开项目 | `VisionStudio.Runtimes.App` | Runtime 架构/边界/流程 | RuntimeAppTests | 纳入 |
| 连续采集、状态、队列、触发 | Runtime 控制器与输入源 | Runtime 四份专项文档 | 状态、手动/定时触发、LatestFrame 丢帧 | 纳入 |
| Runtime 合并主安装包 | 桌面发布、Inno、启动器 | `platform-release.md`、`build/README.md` | Windows 发布物与安装包检查 | 纳入 |
| Google 风格与职责拆分 | 模块整改、MainWindow/GraphCanvas/Web 拆分 | `code-style.md`、`code-review-report.md` | CodeStyle 基线 | 渐进治理，不宣称零债务 |

## 4. 标准模型发布记录

- 仓库：`ggml-org/Qwen2.5-VL-3B-Instruct-GGUF`
- 固定修订：`5037fcf163dd95d1e41d1974465f0898ed108ca2`
- 许可证：Apache-2.0
- 主模型：`Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf`
- 主模型大小：1,929,901,056 字节
- 主模型 SHA-256：`d02fe9b69ad8cadbbd228e387667af66612c44bed29ffc8eb1e7caf9ac486c12`
- mmproj：`mmproj-Qwen2.5-VL-3B-Instruct-Q8_0.gguf`
- mmproj 大小：844,757,728 字节
- mmproj SHA-256：`980c9b2f78c04e6cff93d277ada09e768394f112d75db3b4e9dea8a69f9fb904`

下载配置只包含链接和元数据。模型文件不提交 Git、不放入主安装包；安装时先写 `.download` 临时文件，校验通过后再原子移动。

## 5. 发布门禁

V2.0 标签前必须同时满足：

1. `Directory.Build.props` 和全部发布物显示 `2.0.0`。
2. Release 编译无 warning/error，全部 xUnit 测试通过。
3. CodeStyle 基线不增加。
4. CodeExport、AI、Web、RuntimeApp、DesktopInteraction 和 EndToEnd 通过。
5. Windows 免安装包、Windows Setup 与 Web tar.gz 都生成 SHA-256。
6. Windows 安装、编辑器/Runtime 启动、CLI/OpenCV 执行和卸载通过。
7. 主包扫描确认不含 GGUF、mmproj、llama-server、CUDA 或 Provider 插件。
8. 远程 OpenAI 兼容模型的添加、加密保存和调用路径通过测试；本地模型由用户自行配置。
9. 工作区整理为可审查提交，不含密钥、模型、缓存、临时项目或发布产物，再创建 `V2.0.0` 标签。
