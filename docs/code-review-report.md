# Vision Studio 代码规范审查报告

审查日期：2026-08-25
审查范围：`src/`、`plugins/`、`tests/`、`samples/`、Web 静态资源和构建脚本。
审查基线：Google C#、JavaScript、HTML/CSS 与 Documentation Style Guide。

## 1. 结论

当前代码能够在启用可空引用、警告即错误和确定性构建的条件下编译，基础工程质量设置较好；没有发现警告抑制、JavaScript `var` 或普遍的尾随空格问题。

主要债务不是少量命名错误，而是仓库历史上采用了 .NET 默认格式，与 Google C# 的 2 空格、K&R 花括号和 100 列规则整体不一致。同时，桌面主窗口和 Web 静态代码过于集中，已明显影响审查、维护和单元测试定位。

本轮已按 `Core → AI → 桌面 UI → Web → 算子` 的顺序完成模块级机械格式化，并在每个模块完成后运行受影响维度测试、下调规范基线。没有格式化 CodeGen、CLI、测试和示例生成物；这些内容构成当前剩余债务。

## 2. 量化结果

| 项目 | 结果 |
|---|---:|
| 审查文件 | 120 |
| Google C# 换行花括号 | 235（初始 939） |
| C#/JavaScript 超过 100 列 | 1,240（初始 1,553） |
| 制表符缩进 | 0 |
| 尾随空格 | 0 |
| `dotnet format style` 诊断 | 2,276（初始 9,396） |
| 格式问题 `IDE0055` | 2,080（初始 8,063） |
| 缺少控制流花括号 `IDE0011` | 41（初始 550） |
| `var` 使用不符合配置 `IDE0008` | 94（初始 546） |
| 方法表达式体 `IDE0022` | 17（初始 158） |
| 命名问题 `IDE1006` | 31 |

`dotnet format` 结果包含格式诊断的交叉计数，不能与词法审查结果直接相加。

## 3. 需要优先处理的问题

### 已解决 P1：AI Provider 异步生命周期

位置：`src/VisionStudio.App/MainWindow.axaml.cs:1087`

新增 `AiProviderLifetime`，串行处理 Provider 创建、替换、重置和释放。项目切换、关闭文件夹与自动化会话分离均改为可等待的异步路径；窗口关闭先取消本次关闭，等待清理完成后再最终关闭。

专项测试覆盖同配置复用、替换时等待旧 Provider 释放，以及 Reset 等待释放完成。

### 已解决 P1：同一项目保存串行化

位置：`src/VisionStudio.App/MainWindow.axaml.cs:1146`、`src/VisionStudio.Core/ProjectSystem.cs:98`

`ProjectService` 现在为每个项目维护严格按调用顺序执行的保存队列。工作流在入队前序列化为不可变快照，主工作流、autosave 和项目元数据在同一队列任务内更新；前一任务失败不会污染后续队列。

专项测试并发提交 40 次保存，验证最终主文件和 autosave 都是第 40 个版本，且没有遗留临时文件。

### 已解决 P2：Web 项目扫描静默吞掉损坏项目

位置：`src/VisionStudio.Web/WebProjectStore.cs:95`

`WebProjectStore` 已注入 `ILogger<WebProjectStore>`，列表和按 ID 加载都会记录无法读取的目录及异常，同时只记录目录名，不向客户端泄露服务器绝对路径。`WebProjectStoreTests.ListAsync_LogsUnreadableProjectDirectory` 覆盖损坏项目诊断。

### 已解决 P2：Web 上下文缓存继承首个请求的取消令牌

位置：`src/VisionStudio.Web/WebProjectStore.cs:112`

共享 `Lazy<Task<WebProjectContext>>` 现在使用独立的非请求取消令牌加载；每个请求通过 `WaitAsync` 只取消自己的等待，不能取消共享加载任务。失败的共享任务仍会从缓存移除。`WebProjectStoreTests.GetAsync_CancelledCallerDoesNotCancelSharedLoad` 覆盖两个并发调用者的隔离行为。

## 4. 结构与可维护性问题

### 4.1 桌面窗口职责过多

`MainWindow.axaml.cs` 有 1,238 行，同时负责项目生命周期、AI 会话、文件选择、代码导出、属性编辑、预览、自动保存、撤销、主题和操作记录。`GraphCanvas.cs` 有 708 行，同时处理绘制、命中测试、拖动、连线和上下文菜单。

建议按职责拆分：

- `ProjectWorkspaceController`
- `AiChatController`
- `PreviewController`
- `AutosaveCoordinator`
- `PropertyEditorFactory`
- `GraphInteractionController`

拆分后的 UI code-behind 只保留事件转发和视图状态绑定。

### 4.2 单文件类型过多

`WorkflowAutomation.cs` 包含约 20 个类型，`AiContracts.cs` 包含约 15 个类型。Google 风格建议一个核心类型对应一个文件。纯 DTO 可以按强相关小组保留，但 API、实现、事件参数和结果模型应分开。

### 4.3 Web 源码近似压缩文件

`styles.css` 的主要内容集中在一行，最长 11,629 字符；`app.js` 包含约 47 个顶层函数，多行超过 1,000 字符。这会降低差异可读性、断点调试质量和安全审查效率。

建议先用 Google 风格格式化，再拆分为 `api.js`、`canvas.js`、`inspector.js`、`project.js` 和 `localization.js`；发布阶段再生成压缩产物，禁止把压缩形式作为唯一源码。

### 4.4 算子目录数据行过长

`ExtendedOperatorCatalog.cs` 中多个描述符在单行构造，最长超过 1,500 字符。建议每个参数、端口和元数据占独立行，或改用声明式 JSON/资源文件并在启动时校验。

## 5. 已有优点

- 全仓启用了 `Nullable`、`TreatWarningsAsErrors` 和确定性构建。
- 未发现 `#pragma warning disable`、`NoWarn` 或 `SuppressMessage`。
- JavaScript 未使用 `var`，符合 Google 的 `const`/`let` 规则。
- 项目写入采用临时文件后原子移动，能避免单文件写到一半。
- 大部分 `async void` 都是 Avalonia 事件处理器，没有扩散到普通业务 API。
- 文件路径普遍通过 `ProjectPaths.ResolveInside()` 限制在项目目录内。

## 6. 分阶段整改结果

| 阶段 | 验证 | 编译器诊断基线 | 花括号基线 |
|---|---|---:|---:|
| 初始审查 | 解决方案构建 | 9,396 | 939 |
| 两个 P1 + Core | 22 项 Core 测试；3 项 P1 专项测试 | 8,593 | 846 |
| AI 与 llama.cpp 插件 | 41 项 AI/插件测试 | 7,144 | 686 |
| 桌面 UI | 6 项画布、聊天和主题测试 | 4,286 | 400 |
| Web | 实际启动并访问 `/api/health` | 3,996 | 357 |
| OpenCV 算子 | 19 项算子执行测试 | 2,276 | 235 |
| V2.0 发布收口 | Web P2、Runtime 打包、标准模型与导出整改 | 1,791 | 208 |

每阶段完成后都立即更新 `.code-style-baseline.json`，没有通过上调基线规避失败。审计脚本同时补齐了 Windows PowerShell 5.1 兼容性，保证本地与构建脚本使用同一门禁。

## 7. 剩余债务与后续方向

- `IDE1006` 仍有 31 项，`dotnet format` 不支持自动修复，需要逐项评估公开 API 兼容性后重命名。
- CodeGen、CLI、测试工程与示例生成代码尚未纳入本轮模块整改，因此全仓严格模式仍不会通过。
- 100 列问题仍有 1,240 项，其中包含生成代码、长资源文本和算子声明；后续应先区分可读源码与生成产物，再决定拆行或排除策略。
- `MainWindow` 已按 AI、外观、项目、预览、自动化拆为 partial 职责文件，入口文件由 1,364 行降至 354 行。
- `GraphCanvas` 已按交互、布局、渲染拆为 partial 职责文件，入口文件由 893 行降至 250 行。
- Web 服务端已分离启动入口、请求管道和 API 路由；前端已分离 API、项目工作区、画布、结果/属性检查器，`app.js` 由 1,112 行降至 313 行。
- 上述拆分保持原状态所有权、事件签名、API 路由和脚本加载语义不变。桌面定向测试、Web HTTP 资源测试和浏览器初始化测试均通过。
