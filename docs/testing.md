# 分维度自动化测试规范

## 核心规则

每次修改只运行受影响的测试维度，不再默认执行完整测试套件。开始修改前识别影响面，完成后通过 `scripts/test-changes.ps1` 明确列出维度；最终交付说明必须记录实际运行的维度和结果。

编译是源代码变更的共同底线，不算完整回归。统一入口默认先执行一次 Release 构建，再运行所选维度。纯文档修改可以不构建；已经有可信的新鲜构建产物时可使用 `-NoBuild`。

只有以下情况运行 `EndToEnd` 或 `All`：发布候选版本、修改公共启动/生命周期基础设施、修改测试框架自身且无法确定边界、多个维度同时出现系统性风险，或用户明确要求完整回归。不能因为“保险”而在单一局部修改后默认运行全部测试。

## 测试维度

| 维度 | 覆盖内容 | 典型文件或变更 | 执行内容 |
| --- | --- | --- | --- |
| `UiStyle` | 布局、颜色、主题、间距、图标和动态资源 | `*.axaml`、`UiThemeManager.cs`、`Assets/` | 主题资源、三种主题菜单和多尺寸 ICO 静态检查 |
| `Typography` | 字体、字号、界面文案、中英文资源 | `App.axaml` 字体规则、`Resources/Strings.*.json`、显示名和介绍 | Inter 配置检查及 `LocalizationTests` |
| `CanvasInteraction` | 画布平移、节点布局、端口连接、线路命中和选择 | `GraphCanvas.cs` 及相关交互代码 | `GraphCanvasTests` |
| `Workflow` | 图结构、验证、拓扑、代码生成基础契约 | `VisionStudio.Core`、基础代码生成 | `WorkflowTests` |
| `Operators` | OpenCV 算法、参数校验、多输入输出、缓存和高级算子导出 | `VisionStudio.Operators.OpenCv`、算子代码生成 | `OpenCvIntegrationTests`、`ExtendedOperatorTests` |
| `Project` | 新建/保存/打开、目录、自动保存、输入图片、文件关联 | `ProjectService`、项目存储、`WindowsFileAssociation.cs` | `ProjectTests`、`FileAssociationTests` |
| `DesktopInteraction` | 页面切换、窗口生命周期和单个高风险桌面回归 | `MainWindow` 页面状态、关闭文件夹流程 | `test-back-navigation.ps1` |
| `Web` | Web 鉴权、响应式编辑器、服务端项目、上传、执行、产物与导出 | `VisionStudio.Web`、`deploy/web/` | `test-web.ps1` API 全链路与静态入口检查 |
| `Packaging` | Windows/Linux 安装器、安装卸载、品牌资源、系统集成 | `installers/`、`build-installers.ps1`、发布元数据 | 编译安装包；Windows 安装/运行/卸载；Linux DEB 元数据、权限、入口与 MIME 检查 |
| `EndToEnd` | 跨模块完整桌面工作流 | 启动、菜单、节点执行、缓存、撤销、导出等跨域修改 | `test-ui-workflows.ps1` |

一项修改可以命中多个维度。例如调整主题菜单文字，应运行 `UiStyle,Typography`；改变连线绘制但不改变命中算法只运行 `UiStyle`，改变线路命中范围则运行 `CanvasInteraction`，两者都改则组合运行。

## 变更选择规则

1. 按实际行为影响选择，不只按文件扩展名。`MainWindow.axaml.cs` 可能涉及界面、语言、项目或桌面交互。
2. 纯样式资源、颜色、间距、图标修改不得触发功能或完整桌面回归。
3. 字体或文字修改必须检查所有受影响语言；只改文案不运行 OpenCV 算子测试。
4. 算子参数、执行结果或缓存键变化选择 `Operators`；仅修改算子在菜单中的视觉样式选择 `UiStyle`。
5. 修改公共序列化、启动参数或应用生命周期时，选择所有直接受影响维度；无法证明边界时升级到 `EndToEnd`。
6. 测试脚本自身发生修改时，至少实际执行被修改脚本所代表的维度。

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
- `DesktopInteraction`：操作后进程存活、目标页面出现、重复点击不闪退。
- `Web`：令牌保护、安全响应头、项目隔离、上传校验、工作流修订、OpenCV 执行、受限产物读取、导出和撤销。
- `Packaging`：安装器哈希有效；Windows 安装、运行和卸载完整；Linux 依赖、启动器、应用菜单、图标及 `.vstudio` MIME 关联完整。
- `EndToEnd`：项目创建到执行、撤销、缓存、导出、关闭文件夹及 CLI 的完整链路。

所有自动化测试不得依赖摄像头、互联网或用户目录中的已有文件；临时项目必须在测试结束后删除。视觉质量无法由静态断言完全覆盖时，应为本次涉及的主题和页面生成截图并人工检查，但不因此扩大到其他功能维度。
