# AI 工作流自动化接口规范

## 1. 目标

内置 AI 助手接收自然语言需求后，可以读取当前算子目录和工作流，批量添加、配置、连接算子，选择目标节点并执行。AI 不允许操作 Avalonia 控件，也不允许直接读取或覆盖 `workflows/main.vflow`。

稳定入口是 `IWorkflowAutomationApi`。桌面 AI 面板、HTTP API、MCP Server 或其他自动化适配器都只负责把外部请求转换成该接口的 DTO；事务、校验、保存、缓存和界面通知由应用服务统一完成。

```text
用户自然语言
    ↓
AI 规划器（未来实现）
    ↓ 查询目录/快照，提交结构化批量命令
IWorkflowAutomationApi
    ├── WorkflowAutomationSession：事务、校验、版本、幂等、保存
    ├── ProjectService：原子保存、快照、操作日志
    └── IWorkflowAutomationRunner
            └── OpenCvWorkflowAutomationRunner：执行、节点缓存、预览资源
    ↓ 事件
Avalonia 编辑器：刷新画布、选择节点、显示预览和结果摘要
```

## 2. 会话获取

`App.WorkflowAutomation` 是应用级 `WorkflowAutomationApiHost`。打开项目后 `Current` 指向当前项目会话，关闭项目后恢复为 `null`。未来 AI 组件依赖 Host 或 `IWorkflowAutomationApi`，不依赖 `MainWindow`。

一个 API 会话只绑定一个已打开项目。AI 必须先确认 `Current` 非空，项目切换后必须重新取得会话，不能继续持有旧项目对象。

## 3. API 方法

### `GetOperatorCatalogAsync`

返回完整 `OperatorDescriptor` 列表，包括稳定接口名 `Type`、输入输出端口、端口类型、参数类型、默认值、范围和介绍。AI 必须依据目录选择算子，禁止猜测接口名或端口名。

### `GetSnapshotAsync`

返回项目 ID、项目名称、当前 `Revision` 和工作流副本。AI 应基于该快照制定修改，并把 revision 原样放进修改请求。

### `ApplyAsync`

一次事务提交多个编辑。支持：

- `addNode`
- `removeNode`
- `setParameter`
- `renameNode`
- `moveNode`
- `connect`
- `disconnect`

整个批次先在工作流副本上执行，再验证节点、算子、端口、类型、重复输入和环。任意编辑失败时整个批次不落盘。编辑状态允许暂时存在未连接的必需输入；真正执行时仍使用严格验证。

请求字段：

- `requestId`：调用方生成的唯一 ID。重复提交相同 ID 不会重复添加节点。
- `expectedRevision`：乐观并发版本。与当前版本不一致时返回 `WorkflowRevisionConflictException`，AI 必须重新获取快照并重新规划。
- `edits`：有序批量编辑列表。
- `summary`：面向用户的简短操作说明，写入操作记录并作为撤销说明。
- `selectNodeId`：提交后界面选中的节点。
- `executeNodeId`：可选；提交成功后立即执行该节点及其祖先。

### `ExecuteAsync`

不改变工作流，执行指定节点及其祖先。返回端口类型、可序列化摘要、节点耗时、缓存命中信息和可选预览资源路径。执行失败不会撤销已经合法保存的工作流修改，便于 AI 根据错误调整参数后再次提交。

## 4. JSON 示例

以下请求把已有加载图像节点 `load-image` 接到“灰度化 → Canny 边缘 → 查找轮廓 → 拟合直线 → 直线角度”流程，并直接显示角度结果。具体参数仍应根据图像内容调整。

```json
{
  "requestId": "assistant-angle-20260812-001",
  "expectedRevision": 7,
  "summary": "添加零件直线角度测量流程",
  "selectNodeId": "line-angle",
  "executeNodeId": "line-angle",
  "edits": [
    { "kind": "addNode", "nodeId": "gray", "operatorType": "Image.Gray" },
    { "kind": "addNode", "nodeId": "canny", "operatorType": "Edge.Canny" },
    { "kind": "addNode", "nodeId": "contours", "operatorType": "Blob.Contours" },
    { "kind": "addNode", "nodeId": "fit-line", "operatorType": "LineCircle.FitLine" },
    { "kind": "addNode", "nodeId": "line-angle", "operatorType": "LineCircle.LineAngle" },
    { "kind": "connect", "fromNode": "load-image", "fromPort": "Image", "toNode": "gray", "toPort": "Image" },
    { "kind": "connect", "fromNode": "gray", "fromPort": "Image", "toNode": "canny", "toPort": "Image" },
    { "kind": "connect", "fromNode": "canny", "fromPort": "Image", "toNode": "contours", "toPort": "Image" },
    { "kind": "connect", "fromNode": "load-image", "fromPort": "Image", "toNode": "fit-line", "toPort": "Image" },
    { "kind": "connect", "fromNode": "contours", "fromPort": "Contours", "toNode": "fit-line", "toPort": "Contours" },
    { "kind": "connect", "fromNode": "fit-line", "fromPort": "Image", "toNode": "line-angle", "toPort": "Image" },
    { "kind": "connect", "fromNode": "fit-line", "fromPort": "Line", "toNode": "line-angle", "toPort": "Line" }
  ]
}
```

AI 不应一次凭经验固定最终方案。推荐流程是：查询目录 → 获取快照 → 说明拟采用的方法 → 提交完整批次 → 查看执行摘要 → 必要时只修改参数或替换局部节点。

## 5. UI 同步和撤销

`WorkflowChanged` 在事务保存成功后发布。编辑器收到事件后：

1. 把修改前工作流加入现有 Ctrl+Z 撤销栈。
2. 替换画布数据源并刷新节点和线路。
3. 选中 `selectNodeId`。
4. 刷新属性和操作记录。

`ExecutionCompleted` 返回后，编辑器优先读取项目内预览资源并显示；纯数值结果显示端口摘要。OpenCV 运行器把 AI 调用生成的预览写入：

```text
images/cache/assistant/<request>-<node>-<port>.png
```

节点自身仍使用原有内容键缓存，因此 UI 随后再次预览不会重复计算未变化的上游。

## 6. 一致性与安全规则

- 任何外部自动化都必须通过 API，不得修改 `_workflow`、Canvas 或项目文件。
- `requestId` 必须全局唯一且可追踪；重试必须复用原 ID。
- 每次规划必须携带 `expectedRevision`，禁止无版本覆盖用户刚完成的编辑。
- 批量编辑原子提交；失败不能留下半条链路。
- 参数必须匹配描述符 JSON 类型和数值范围。
- 项目路径仍由 `ProjectPaths.ResolveInside` 限制；结果只写项目目录。
- AI 生成的是可撤销建议，不允许绕过操作日志和项目快照。
- 未来涉及删除大量节点、文件写入、相机或外部设备时，应在适配器层增加用户确认；不改变核心 API。
- 未来跨进程开放时需要在传输适配器增加身份认证、调用权限、限流和请求大小限制；核心 DTO 与服务不承载网络协议。

## 7. 扩展原则

未来增加 HTTP/MCP 工具时，应映射为四个稳定工具：`get_operator_catalog`、`get_workflow_snapshot`、`apply_workflow_edits`、`execute_workflow_node`。传输层可以新增认证和审批字段，但不能把 UI 坐标点击、文件格式细节或 EmguCV `Mat` 暴露给模型。

若需要新增编辑能力，优先增加新的 `WorkflowEdit` 类型并保留现有 JSON discriminator；不得修改已发布 discriminator 的含义。工作流 schema 升级和 AI API 版本应分别管理。
