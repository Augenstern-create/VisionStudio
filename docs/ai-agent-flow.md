# AI 助手流程与工具协议

## 用户流程

```text
用户点击/输入/拖入 [IMG-01] 加载图像引用
  ↓
输入“我想要测量图片中是否有裂纹，裂纹的数量，像素长度”
  ↓
插件读取 GraphSummary 和选中节点，不读取 UI 控件
  ↓
发送运行时完整算子目录，并单独给出按需求检索的推荐接口
  ↓
模型输出严格 JSON；多阶段链优先使用紧凑 add_pipeline
  ↓
宿主展开流程，执行动作白名单、参数、端口、环与修订版本校验
  ↓
建议模式展示计划
  ↓ 用户确认
整批 ApplyAsync（一个撤销事务）
  ↓
execute_graph / inspect_result
  ↓
对话显示裂纹数量、像素长度、阈值和结果节点
```

第一版默认“建议模式”，不允许模型在未确认时写 Graph。测试或明确启用自动模式时才可直接应用。

## 图片节点寻址

`Image.Load` 按工作流顺序显示 `IMG-01`、`IMG-02` 等短编号，并保留节点名称和内部 Node ID。UI 只把短编号作为交互入口；Agent 收到请求前将其解析为真实 Node ID 和项目内图片路径。多图流程没有匹配到编号、名称或当前选中图像节点时停止请求并提示用户指定，避免分析错图。

## 固定工具集合

模型只能使用以下工具，不为每个 OpenCV 算子增加 Function：

1. `search_operators`
2. `get_operator_detail`
3. `get_current_graph`
4. `add_pipeline`（仅模型侧紧凑表示，提交前展开）
5. `add_node`
6. `remove_node`
7. `connect_nodes`
8. `set_parameter`
9. `execute_graph`
10. `inspect_result`
11. `report_missing_operator`

工具动作由 Agent 转成 `WorkflowEdit`，再交给 `IWorkflowAutomationApi.ApplyAsync`。一次模型计划必须一次提交，以便一次 Ctrl+Z 回退。

## 限制

- 一次请求最多 8 个 Agent 循环。
- 验证失败最多自动修正 3 次。
- 同一请求最多发送 2 次缩略图。
- 图片预览最长边 768，原图只给 OpenCV。
- 规划上下文至少 8192，单轮输出上限至少 2048 token；JSON 外文本不作为动作执行。
- Current Graph 发送摘要，默认最多 40 个相关节点。
- 每次规划都从 `IWorkflowAutomationApi.GetOperatorCatalogAsync` 读取并发送软件当前注册的完整算子索引，紧凑条目包含接口名和中文名；相关性较高的算子另带输入输出端口、参数、说明、用途和限制，不存在需要人工同步的提示词算子白名单。
- 算子检索先使用关键词、别名、分类、用途和接口名评分，结果只写入 `recommendedOperatorIds` 作为注意力提示，不能限制模型使用完整目录中的其他算子；算子超过 300～500 后再引入 Embedding。

## 裂纹组合流程

普通的裂纹数量与像素长度需求默认规划为：`Image.Gray → Image.GaussianBlur → Morphology.BlackHat → Threshold.Otsu → Morphology.Close → Blob.Contours → Contour.PathMetrics`。每个阶段都能在画布中查看和调参。`Contour.PathMetrics` 是面向裂纹、划痕、纤维等细长目标的通用轮廓统计端点，不负责前面的缺陷分割。

模型可用一条 `add_pipeline` 提交算子 ID 和实例 ID 数组，宿主按真实端口类型展开连接。该紧凑动作不是 Graph 持久化格式，`.vflow` 中仍保存普通节点与连线。

## 专项裂纹算子基线

`Defect.CrackMeasure` 仅在用户明确指定接口名、专用裂纹算子或单算子方案时使用。它输入原图，计算中心像素与上下邻域的暗线对比度，再用带平滑惩罚的动态规划寻找连续横向暗色脊线。该实现有意针对当前验收图中的长横向裂纹。输出：

- `Image`：在原图绘制裂纹路径与编号；
- `CrackCount`：过滤后的独立裂纹数量；
- `PixelLength`：所有裂纹路径的欧氏像素长度；
- `Summary`：方向、数量和总长度摘要。

像素长度不是物理长度。当前基线不适合短裂纹或竖直裂纹；需要毫米值时必须完成像素标定并换算。
