# AI 架构：仅远程模型 + 对话助手

## 范围

Vision Studio 的 AI 能力只依赖**远程 OpenAI 兼容接口**。本地推理运行时（llama.cpp / GGUF）、Provider 插件加载、标准模型目录与一键下载全部已移除，仓库不再包含 `plugins/VisionStudio.Ai.LlamaCpp`，也不需要任何模型文件。

因此运行 AI 只需要三样东西：Base URL、模型 ID、API Key。每个模型还可以配置三个运行参数（与 DeepSeek Harness 的模型定义一致）：

| 设置项 | 线上字段 | 作用 |
| --- | --- | --- |
| 思考等级 | `reasoning_effort: low/high/max`；「关闭」发送 `thinking.type=disabled`；「自动」不发送 | 控制推理模型的思考开销 |
| 最大输入 token | 不发送（客户端预算） | 裁剪对话历史与算子上下文，留出输出空间；可填 `128K` / `1M` |
| 最大输出 token | `max_tokens` | 单次回答上限，推理模型建议 `4K` 以上；同样支持 `8K` / `1M` 写法 |

两个 token 字段由 `AiTokenCount` 解析：`K`=1024、`M`=1048576、`G`=1073741824，允许 `1.5M`、`128 k`、`1,048,576` 等写法，非法输入在保存时被拒绝。

网关差异由客户端兜底：若端点以 HTTP 400/422 拒绝推理参数，会自动去掉这些参数重试一次；若 `content` 为空且 `finish_reason=length`，会用 4 倍预算重试一次。

## 分层

```text
SettingsWindow            配置远程模型（列表 + 编辑器）
MainWindow.Ai.cs          AI 侧边栏：对话、图像引用、建议改动卡片
VisionStudio.Ai
  AiWorkflowAgent         助手回合：自然语言回答 + 可选 JSON 建议
  OpenAiCompatibleProvider  /chat/completions 调用
  AiModelProfileStore     远程配置加密存储（AES，整体加密载荷）
  AiConversationContextStore  按「模型 + 项目」保存上下文
VisionStudio.Ai.Abstractions  契约：AiModelProfile / IAiProvider / AiAssistantRequest 等
```

## 助手的输入与输出

一次对话回合：

| 环节 | 内容 |
| --- | --- |
| 输入 | 用户自然语言 + 可选图像引用（`IMG-01` / 节点名 / 选中节点）+ 最近 20 条历史（仅 `chat` 类） |
| 上下文 | 当前图（节点**含参数**、连接、revision、选中节点）、算子目录、与问题相关的 `recommendedOperators`（含端口与参数） |
| 输出（正文） | 自然语言回答，始终展示 |
| 输出（可选） | 回答末尾最多一个 ```json 代码块，即 `AiWorkflowPlan` |
| 落盘 | 正文写入该「模型 + 项目」的独立上下文；建议改动不会自动应用 |

`RequireJson = false`，不再强制模型输出 JSON。消息分三类：`chat` 参与回放，`error`（错误提示）与 `execution`（节点执行输出）只用于界面显示，**不会**发回模型。

### 只读工具：执行节点取数

模型可以只回复一个 fenced JSON 块请求执行某个节点：

```json
{"tool":"execute_node","nodeId":"otsu"}
```

宿主在后台线程执行该节点，把 `[tool result]`（端口=摘要）回灌给模型继续作答。上限：每轮 3 次、只允许已存在的节点、只读（不修改工作流）。这让助手能拿到真实测量值，而不是靠猜。

### 散文校验

计划走 `ValidatePlan`，但正文不受约束，因此正文里出现的 `Namespace.Operator` 会与算子目录比对；不在目录中的会追加一行提示，避免把不存在的算子留在建议里。

### 界面

- 流式：思考内容（`reasoning_content`）灰色显示、正文逐字追加，状态行每 200ms 刷新耗时；
- 失败气泡带「重试」，重试不会重复入栈用户问题；
- 轻量 Markdown：标题、粗体、行内代码、列表、引用、分隔线；
- AI 侧栏宽度可通过左侧分栏器拖动。

`RequireJson = false`，不再强制模型输出 JSON。若模型只返回裸的 plan 对象（没有散文），也会被识别为建议，`Message` 回退为 `plan.Summary`。

## 建议改动卡片

- 只有通过 `ValidatePlan` / `ValidatePlanningPolicy` 校验的 plan 才会返回给界面。
- 界面渲染为「建议改动」卡片：摘要 + 动作计数 + `应用这个建议` 按钮。
- 用户点击后才调用 `ApplyPlanAsync`，走 `IWorkflowAutomationApi` 的事务、revision 冲突和幂等机制。
- 校验失败时不会抛异常给用户，而是在正文后追加「改动建议未通过校验，已忽略：…」，工作流保持原样。
- 每次修复尝试是一次额外的模型调用，最多一次；模型随后改为纯文本回答也属正常结果。

## 安全与存储

- API Key 与端点、模型 ID、能力参数一起整体加密后写入 `ai-remote-models.json`，JSON 中不出现明文。
- 配置文件位于软件级目录（Windows：安装目录 `config/`；Linux：`$XDG_CONFIG_HOME/visionstudio/`），与项目目录无关。
- 助手不能执行命令、读写任意文件或修改算子实现，只能通过受控 Graph Action 提议画布变更。
