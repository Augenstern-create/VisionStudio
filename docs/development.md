# 开发说明

## 环境

- .NET SDK 9.0+
- Windows x64 运行 OpenCV 测试与桌面端
- NuGet：Avalonia 12.1.0、EmguCV 4.13.0.5924。Windows 使用同版本完整运行库；Ubuntu 24.04 使用同版本 `Emgu.CV.runtime.mini.ubuntu-24.04-x64`，禁止混用不同版本的托管与原生组件。

```powershell
dotnet restore
dotnet build VisionStudio.sln
./scripts/test-changes.ps1 -Dimension Workflow
```

测试按修改影响维度选择，不默认运行完整套件。维度映射、组合规则与发布验收要求见 `docs/testing.md`。

## 新增算子

1. 在 `VisionStudio.Operators.OpenCv` 注册唯一 `Type`。
2. 声明输入、输出端口及参数模式，端口名属于持久化契约。
3. 每个逻辑输入/输出都声明独立端口；不得把多输入塞进一个字符串参数。实现执行逻辑后，所有声明输出都必须且只能赋值一次。
4. 新建的 `Mat` 所有权交给执行上下文；轮廓/测量结果同时输出结构化值和带标注的 `Image`。
5. 在 `VisionStudio.CodeGen` 增加对应发射器。
6. 增加验证、像素结果、失败路径、多端口和缓存恢复测试。

参数必须使用 JSON 原始值，加载时集中做范围和枚举校验。不得让 Avalonia 控件类型进入核心层，也不得在节点中保存上一次执行得到的 `Mat`。

## AI 与自动化接入

AI、脚本和未来的 HTTP/MCP 适配器统一调用 `IWorkflowAutomationApi`，不得直接操作 `MainWindow`、`GraphCanvas` 或 `workflows/main.vflow`。调用顺序与 DTO 兼容要求见 `docs/ai-workflow-automation-api.md`。新增自动化编辑类型时必须保持已有 JSON discriminator 语义，并补充事务回滚、revision 冲突、幂等和 UI 事件测试。

## 运行模型

- 点击节点后创建独立、可取消的执行上下文，只执行该节点及其祖先。
- 同一执行切片当前串行执行，保证可重复性；新选择会取消尚未完成的预览。
- 节点耗时和缓存命中状态写入执行结果。
- 参数、输入文件或上游连接变化通过内容键自动使本节点及下游缓存失效。
- 画布位置、节点选择等纯 UI 变化不会触发重新计算。
- 新增非 `Mat` 输出类型时必须同步扩展缓存序列化器；不能把任意 CLR 对象直接二进制序列化到项目。

## 版本和兼容

工作流包含 `schemaVersion`。读取器拒绝高于当前实现的版本；低版本迁移应通过显式迁移器完成，禁止静默猜测。算子类型和端口名一旦发布不能直接重命名，应提供别名迁移。

## 发布

桌面发布示例：

```powershell
dotnet publish src/VisionStudio.App -c Release -r win-x64 --self-contained true
```

正式桌面包使用 `scripts/publish-desktop.ps1` 同时生成 Windows x64、Ubuntu Linux x64 压缩包和 SHA-256。Android 与 Web 的发布边界见 `docs/platform-release.md`。

## 桌面交互约定

- 应用启动时只显示新建/打开项目页，项目成功加载后才创建编辑上下文。
- 端口连线使用按下—拖动—释放，不使用两次点击状态。
- 空白画布左键拖动用于视口平移；节点坐标始终使用画布世界坐标，新增命中行为必须先做屏幕坐标到世界坐标转换。
- 新增节点使用空位扫描，批量整理使用 DAG 层级自动排版；自动排版属于一次可撤销的非执行变更。
- 连线采用圆角正交路径；蓝色为输出，绿色为输入。线路使用 18 像素命中带并按最近距离选择，悬停/选中分别高亮；左键选择后 Delete 删除，右键可直接删除。
- 算子名、分类、参数名、参数说明和端口显示名面向使用者统一使用中文；持久化的算子类型、端口名仍保持稳定英文标识。
- `OperatorDescriptor.DisplayName` 必须是纯中文显示名，`EnglishName` 和 `EnglishDescription` 提供规范英文回退；属性面板显示稳定 `Type` 接口名，不并排显示英文名称。
- 通用界面、分类和需要修订措辞的算子名称/介绍写入 `Resources/Strings.<语言代码>.json`。新增语言文件会被项目页自动发现，资源键规则见 `docs/operator-naming-and-description-standard.md`。
- 每次改变工作流前只记录一份撤销快照；连续拖动节点只产生一条“移动算子”记录。新增编辑行为必须同时接入撤销快照和操作日志。
- UI 的 C# 导出路径固定为项目 `output/GeneratedVisionAlgorithm.cs`；CLI 仍允许显式指定目标路径。
- UI 导出成功后通过系统外壳打开项目 `output` 文件夹。
- 结果预览默认适应窗口，滚轮和按钮缩放只改变显示尺寸，不触发算法重新执行；操作记录面板默认折叠。
- 追加操作记录必须触发防抖自动保存；自动保存写主工作流和 `.project/autosave`，使用 `createSnapshot: false` 避免产生大量快照。
