# 分维度自动化测试规范

## 核心规则

每次修改只运行受影响的测试维度，不再默认执行完整测试套件。开始修改前识别影响面，完成后通过 `scripts/test-changes.ps1` 明确列出维度；最终交付说明必须记录实际运行的维度和结果。

编译是源代码变更的共同底线，不算完整回归。统一入口默认先执行一次 Release 构建，再运行所选维度。纯文档修改可以不构建；已经有可信的新鲜构建产物时可使用 `-NoBuild`。

功能测试必须区分以下三层证据，最终交付不得把较低层级描述成较高层级已经通过：

1. **编译检查**：只证明代码可以生成，不证明功能正确。
2. **业务与状态测试**：直接验证解析、图变更、持久化、算法结果和异常边界。
3. **真实交互入口测试**：通过用户实际使用的鼠标、键盘、菜单或按钮入口触发行为，再检查界面状态和持久化结果。

凡修改右键菜单、按钮、拖放、快捷键、选择或页面切换等交互入口，受影响维度必须同时包含第 2、3 层测试。不得只直接调用控件内部方法后宣称菜单或按钮已通过。涉及 AI、网络或模型且不适合依赖真实外部服务时，必须使用确定性的假 Provider/本地测试端点走完同一应用服务链路；不能用真实模型的偶然成功代替回归测试。

一次用户请求包含多个问题时，应建立逐项验收矩阵。每个问题至少记录：复现输入、调用入口、预期状态、持久化或图结构结果、实际测试名称。任何一项仅编译成功或仅做静态源码扫描，都不能标记为功能通过。

只有以下情况运行 `EndToEnd` 或 `All`：发布候选版本、修改公共启动/生命周期基础设施、修改测试框架自身且无法确定边界、多个维度同时出现系统性风险，或用户明确要求完整回归。不能因为“保险”而在单一局部修改后默认运行全部测试。

## 测试维度

| 维度 | 覆盖内容 | 典型文件或变更 | 执行内容 |
| --- | --- | --- | --- |
| `UiStyle` | 布局、颜色、主题、间距、图标和动态资源 | `*.axaml`、`UiThemeManager.cs`、`Assets/` | 主题资源、三种主题菜单和多尺寸 ICO 静态检查 |
| `Typography` | 字体、字号、界面文案、中英文资源 | `App.axaml` 字体规则、`Resources/Strings.*.json`、显示名和介绍 | Inter 配置检查及 `LocalizationTests` |
| `CanvasInteraction` | 画布平移、节点布局、端口连接、线路命中、选择和连接线菜单 | `GraphCanvas.cs` 及相关交互代码 | `GraphCanvasTests`；菜单/鼠标入口改动还须运行桌面交互脚本 |
| `Workflow` | 图结构、验证、拓扑、代码生成基础契约 | `VisionStudio.Core`、基础代码生成 | `WorkflowTests` |
| `Operators` | OpenCV 算法、参数校验、多输入输出、缓存和高级算子导出 | `VisionStudio.Operators.OpenCv`、算子代码生成 | `OpenCvIntegrationTests`、`ExtendedOperatorTests` |
| `Project` | 新建/保存/打开、目录、自动保存、输入图片、文件关联 | `ProjectService`、项目存储、`WindowsFileAssociation.cs` | `ProjectTests`、`FileAssociationTests` |
| `CodeExport` | C# 目标分支选择、源码展开、依赖隔离和可执行性 | `VisionStudio.CodeGen`、桌面/Web/CLI 导出入口、`VisionStudio.ExportExecutionRunner` | 导出 `metrics` 分支并在仓库外独立编译执行；确认依赖 `Input.RuntimeFrame` 的 `metrics_2` 分支被明确拒绝且不留下旧文件 |
| `RuntimeApp` | `.vstudio` 项目运行、运行时帧、输入源、状态机、触发、有界队列和结果映射 | `VisionStudio.Runtimes.App` | `RuntimeAppTests` |
| `Ai` | 插件契约、模型配置与校验下载、算子检索、受控动作、裂纹验收图和发布边界 | `VisionStudio.Ai*`、`plugins/`、AI 面板、AI 相关算子 | 全部 AI/Provider/VisionAI 测试、AI UI 与主包负向扫描 |
| `DesktopInteraction` | 页面切换、窗口生命周期和单个高风险桌面回归 | `MainWindow` 页面状态、关闭文件夹流程 | `test-back-navigation.ps1` |
| `Web` | Web 鉴权、响应式编辑器、服务端项目、并发加载、损坏项目诊断、上传、执行、产物与导出 | `VisionStudio.Web`、`deploy/web/` | `WebProjectStoreTests` + `test-web.ps1` API 全链路与静态入口检查 |
| `Packaging` | Windows/Linux 安装器、安装卸载、编辑器/CLI/Runtime 交付物、品牌资源、系统集成 | `installers/`、`build-installers.ps1`、发布元数据 | 编译安装包；Windows 安装/运行/卸载；Linux DEB 元数据、三类入口、权限与 MIME 检查 |
| `EndToEnd` | 跨模块完整桌面工作流 | 启动、菜单、节点执行、缓存、撤销、导出等跨域修改 | `test-ui-workflows.ps1` |

一项修改可以命中多个维度。例如调整主题菜单文字，应运行 `UiStyle,Typography`；改变连线绘制但不改变命中算法只运行 `UiStyle`，改变线路命中范围则运行 `CanvasInteraction`，两者都改则组合运行。

## 变更选择规则

1. 按实际行为影响选择，不只按文件扩展名。`MainWindow.axaml.cs` 可能涉及界面、语言、项目或桌面交互。
2. 纯样式资源、颜色、间距、图标修改不得触发功能或完整桌面回归。
3. 字体或文字修改必须检查所有受影响语言；只改文案不运行 OpenCV 算子测试。
4. 算子参数、执行结果或缓存键变化选择 `Operators`；仅修改算子在菜单中的视觉样式选择 `UiStyle`。
5. 修改公共序列化、启动参数或应用生命周期时，选择所有直接受影响维度；无法证明边界时升级到 `EndToEnd`。
6. 测试脚本自身发生修改时，至少实际执行被修改脚本所代表的维度。
7. 交互入口测试必须验证操作后的可观察结果，例如节点/连接数量、坐标、保存文件、上下文消息顺序或按钮状态；“菜单能够找到”本身不算完整通过。
8. 修复用户提供的具体复现场景时，必须将该场景加入自动化回归，不能只使用更简单的替代输入。

## 执行示例

```powershell
# 只修改颜色、主题、间距或图标
./scripts/test-changes.ps1 -Dimension UiStyle

# 同时修改界面样式和中英文文案
./scripts/test-changes.ps1 -Dimension UiStyle,Typography

# 修改画布线路命中
./scripts/test-changes.ps1 -Dimension CanvasInteraction

# 修改 OpenCV 算子及项目缓存
./scripts/test-changes.ps1 -Dimension Operators,Project

# 修改 AI 插件、Agent 或 AI 相关算子
./scripts/test-changes.ps1 -Dimension Ai

# 修改 C# 生成器或任一导出入口
./scripts/test-changes.ps1 -Dimension CodeExport

# 修改 `.vstudio` 项目运行平台、采集、触发或队列
./scripts/test-changes.ps1 -Dimension RuntimeApp

# 发布前明确执行完整验收
./scripts/test-changes.ps1 -Dimension All
```

## 各维度关键边界

- `UiStyle`：三种主题资源完整、动态资源引用有效、常用窗口尺寸不遮挡、图标包含 Windows 多尺寸图层。
- `Typography`：统一 Inter 字体和基准字号；英文资源键覆盖中文资源键，英文文案不得包含汉字；名称与介绍符合命名规范。
- `CanvasInteraction`：画布平移坐标转换、节点防重叠、DAG 分层、连线宽命中带、删除和撤销。
- `Workflow`：重复 ID、未知端口、类型不匹配、重复输入、环、缺失输入和确定性拓扑顺序。
- `Operators`：1/3/4 通道、空图像、尺寸不一致、参数越界、多端口、结构化输出、资源释放和缓存失效。
- `Project`：目录创建、路径越界、原子保存、自动保存、Unicode 路径、输入图片复制和 `.vstudio` 文件关联。
- `CodeExport`：多结果流程必须指定导出目标；只包含目标的上游依赖；源码不得引用 Vision Studio 内部运行时；在仓库外临时目录仅使用公开 EmguCV 包完成还原、编译，并以项目图片执行得到非空结果。未实现独立代码生成的算子必须拒绝导出，禁止回退为序列化工作流包装器。
- `RuntimeApp`：直接读取 `.vstudio`；逐帧复用 `WorkflowExecutor`；单次/连续/定时/手动触发、状态转换、队列上限、丢帧统计和完整结果映射。
- `Ai`：插件兼容性、外部路径 Profile、标准模型 HTTPS/大小/SHA-256/许可证、JSON 白名单、修订冲突、指定图片结果，以及主包不预装模型、llama.cpp 或 Provider。
- `DesktopInteraction`：操作后进程存活、目标页面出现、重复点击不闪退。
- `Web`：令牌保护、安全响应头、项目隔离、上传校验、工作流修订、OpenCV 执行、受限产物读取、导出和撤销。
- `Packaging`：安装器哈希有效；Windows 安装、运行和卸载完整；Windows/Linux 主包都包含编辑器、CLI 和 Runtime；Linux 依赖、三个启动器、应用菜单、图标及 `.vstudio` MIME 关联完整。
- `EndToEnd`：项目创建到执行、撤销、缓存、导出、关闭文件夹及 CLI 的完整链路。

所有日常自动化测试不得依赖摄像头、互联网或用户目录中的已有文件；临时项目必须在测试结束后删除。`scripts/test-local-ai.ps1` 是显式实机验收例外，由调用者提供本地模型与 llama-server 路径。视觉质量无法由静态断言完全覆盖时，应为本次涉及的主题和页面生成截图并人工检查，但不因此扩大到其他功能维度。
