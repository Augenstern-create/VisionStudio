# Vision Studio V2.0 发布前复核报告

复核日期：2026-08-27
目标版本：2.0.0
范围：Windows x64、Ubuntu Server Web；Linux 桌面版暂缓，明确排除 Android 和 iOS

## 1. 复核结论

用户确认的四项 V2.0 决策已经落实：

1. `VisionStudio.Runtimes.App` 已并入 Windows 主安装包和免安装包，不再单独发布。
2. V2.0 正式发布 Windows 与 Web；Linux 桌面版暂缓，Android/iOS 不生成、不测试、不发布。
3. V2.0 优先使用远程 OpenAI 兼容 API；本地模型由用户自行配置，标准模型一键下载不作为稳定发布承诺。
4. 两个 Web P2 已修复并有回归测试。

用户已经接受 Linux 桌面版和标准模型真实下载不进入本次发布门禁。Windows 与 Web 验证通过后可以形成 V2.0.0 提交、标签和 GitHub Release；Linux 发布物不得上传。

## 2. 已完成整改

| 原问题 | 整改结果 | 自动化证据 |
| --- | --- | --- |
| 版本混用 1.0/1.1 | 程序集、Web、插件、构建脚本和安装包统一为 2.0.0 | `test-release-metadata.ps1` |
| 总边界与实现冲突 | 重写 V2 总边界和追踪矩阵；明确 Runtime、AI、Web、移动端和外部系统边界 | 文档链接检查、元数据测试 |
| Runtime 交付不清 | Runtime 合并主安装包，Windows 开始菜单和 Linux desktop/CLI 启动器均包含入口 | Windows release/setup、Linux DEB 结构测试 |
| 示例路径失效 | 稳定入口统一为 `samples/DemoProject/DemoProject.vstudio` | DesktopInteraction、EndToEnd、元数据测试 |
| Web 损坏项目静默消失 | 注入日志并记录不可读目录，不向客户端暴露绝对路径 | `ListAsync_LogsUnreadableProjectDirectory` |
| Web 首请求取消共享加载 | 共享任务不使用请求令牌，每个请求只取消自己的等待 | `GetAsync_CancelledCallerDoesNotCancelSharedLoad` |
| 本地/远程 AI 边界 | 远程 API 为 V2.0 主路径；本地模型手动配置，主包不携带模型 | AI、远程配置、主包隔离扫描 |
| C# 导出不能可靠执行 | 普通图像分支导出、独立编译、执行和 PNG 解码通过；`RuntimeFrame` 分支明确拒绝独立导出 | CodeExport 维度 |
| 端到端测试仍测已移除执行页 | 删除主编辑器脚本执行页断言，保留 C# 导出验证；修复 DPI/连接命中假阳性 | EndToEnd |
| 规范债务增加 | 未提高基线，并格式化本轮新增/修改代码 | CodeStyle：`IDE0055` 1791、`CS-BRACE` 208 |

## 3. 当前权威范围

详细的需求—实现—文档—测试矩阵见 [V2.0 发布范围与追踪矩阵](v2-release-scope-and-traceability.md)。对外口径如下：

- Desktop：编辑器、CLI、Runtime、Core 和 OpenCV 算子进入同一个 Windows 产品包。
- Runtime：直接打开 `.vstudio`，复用 `WorkflowExecutor`；支持单次/连续运行、图片/目录/视频/EmguCV 相机/模拟输入、软件手动与定时触发、有界队列、统一结果映射。
- Web：ASP.NET Core 服务与响应式工作台，部署在 Ubuntu Server；不是桌面安装程序。
- AI：主程序内置配置、上下文和 Graph Action 接口；远程 OpenAI 兼容 API 是 V2.0 主路径，llama.cpp Provider 和本地模型由用户自行配置。
- V2.0 不包含厂商相机 SDK、PLC/硬件触发、MES/数据平台上传、检测结果长期数据库，也不承诺所有高级算子达到生产计量精度。
- C# 导出保留给二次开发；Runtime 不执行 C# 脚本。

## 4. 验证记录

| 维度 | 结果 | 说明 |
| --- | --- | --- |
| Release 编译 | 通过 | 0 warning、0 error |
| xUnit | 通过 | 96/96，0 失败、0 跳过 |
| DesktopInteraction | 通过 | 关闭文件夹后进程存活并返回项目页；UIA 不依赖 DPI 坐标 |
| EndToEnd | 通过 | 菜单、主题、AI、双击节点、缓存、属性、撤销、连接分支操作、导出、语言、CLI |
| CodeStyle | 通过 | 基线未增加，并下调至当前实际值 |
| CodeExport | 通过 | 生成、独立编译、执行、430×287 PNG 解码；RuntimeFrame 预期拒绝 |
| AI | 通过 | 43 项测试、AI UI、Provider 生命周期、标准模型校验和主包隔离 |
| Web | 通过 | 两项 Web P2 回归测试和完整 API 链路 |
| RuntimeApp | 通过 | 10 项加载、状态机、输入源、触发、队列和结果映射测试 |
| Project | 通过 | 8 项项目系统测试 |
| Windows 免安装包 | 通过 | 编辑器/Runtime 启动、CLI 校验和原生 OpenCV 执行 |
| Windows Setup | 通过 | 安装、编辑器/Runtime 启动、CLI 执行、卸载 |
| Ubuntu/Linux 桌面 | 暂缓发布 | 构建资产保留但不上传，等待后续真实 Ubuntu 24.04 桌面设备验收 |
| 标准模型真实下载 | 不作为门禁 | 下载入口保留为实验能力；V2.0 本地模型由用户自行配置 |

已生成候选发布物：

- `artifacts/releases/VisionStudio-2.0.0-win-x64.zip`
- `artifacts/installers/VisionStudio-2.0.0-win-x64-setup.exe`
- `artifacts/web/VisionStudio.Web-2.0.0-ubuntu-x64.tar.gz`

这些文件是当前工作区的验证产物，不应在完成最终 RC 和 Git 提交前直接作为正式 Release 上传。

## 5. 规范与结构复核

- Core、AI、桌面 UI、Web、算子和发布收口均遵循渐进基线；本轮没有通过上调基线规避错误。
- `MainWindow`、`GraphCanvas` 和 Web 已完成职责拆分，入口文件不再承担全部实现。
- 当前仍是“采用 Google 风格并渐进治理”，不是“全仓严格零债务”。当前剩余 `CS-BRACE=208`、`LINE100=1167`、`IDE0055=1791`、`IDE1006=31`。
- 新增 Web 逻辑有独立测试；Runtime 和标准模型的交付边界已经进入发布脚本及元数据门禁。

## 6. 最终发布门禁

以下各项全部完成后，结论才能从“条件式 RC”变为“允许发布”：

1. 在 Windows 重跑 Release、96 项测试、DesktopInteraction、EndToEnd、CodeExport、AI、Web、RuntimeApp、Packaging。
2. 确认开源许可证和第三方运行库通知文件；未签名安装包需在 Release 中明确提示。
3. 清理 Git 工作区，确保密钥、模型、缓存、本地项目、测试进程产物和 `artifacts` 不进入提交。
4. 上传 Windows 免安装包、Windows Setup、Web tar.gz 及各自 SHA-256；不得上传 Linux 桌面发布物。
5. 创建 `V2.0.0` 标签及 GitHub Release。

## 7. 发布建议

Linux 桌面版与标准模型下载已经从 V2.0 稳定承诺中移除。完成 Git 清理和最终 Windows/Web 验证后即可发布；Release Notes 必须明确 Linux 桌面暂缓、本地模型手动配置和安装包未签名状态（如适用）。
