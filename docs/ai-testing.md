# 本地 AI 插件测试规范

## 测试维度

| 维度 | 内容 |
| --- | --- |
| `AiContracts` | 清单/Profile/请求响应序列化、契约版本兼容 |
| `AiPlugin` | 插件发现、路径越界、重复 ID、错误程序集、生命周期 |
| `AiDevHost` | 从 App/Test bin 启动时自动发现同级清单或仓库插件构建输出 |
| `AiSearch` | 中文/英文关键词、别名、用途 Top-K 和确定性排序 |
| `AiActions` | 严格 JSON、动作白名单、参数与端口校验、事务和 revision 冲突 |
| `AiProvider` | llama-server 健康检查、超时、取消、进程归属和文本降级 |
| `AiSettings` | 本地/远程设置页、独立 JSON、多模型增删、标准模型安装和 llama 自动发现 |
| `AiSecrets` | 远程 API Key 密文落盘、当前账户解密、JSON 无明文、损坏密文拒绝 |
| `AiRemoteModel` | 虚拟远程模型新增、必填项与 URL 校验、加密保存、重开回填、多模型切换和 OpenAI 兼容请求 |
| `AiImageReference` | IMG 编号、节点名称、选中节点、点击插入、节点拖入和多图歧义拒绝 |
| `AiContext` | 模型与项目组合键隔离、切换恢复、清空和最多 40 条裁剪 |
| `OpenAiCompatible` | `/v1/chat/completions`、Bearer、模型 ID、图片能力声明和错误处理 |
| `CompositePlanning` | 紧凑流程展开、多阶段完整性、实例 ID、端口匹配和无效计划不落地 |
| `AiConnections` | 必需输入完整、输入单连接、合法输出分支、多输入语义、无环与分层排版 |
| `CrackMeasurement` | 通用组合流程与显式专项算子的无裂纹、单裂纹、多裂纹、路径长度和预览输出 |
| `AiPackaging` | 主程序及安装包不含模型、llama.cpp、CUDA 与 Provider 插件 |
| `AiEndToEnd` | 自然语言需求 → 计划 → Graph → 执行 → 数量/像素长度 |

## 指定验收图

输入：`tests/image/1.png`

需求：`我想要测量一下图片中是否有裂纹，裂纹的数量，像素长度`

验收条件：

1. 未明确指定专项算子时，Agent 不得添加 `Defect.CrackMeasure`，而应生成灰度、平滑、黑帽、自动阈值、形态学清理、轮廓和路径统计等可检查阶段。
2. 小模型可返回一个 `add_pipeline` 紧凑动作；宿主必须展开为标准 `add_node` 与 `connect_nodes`，并在落地前完成端口、环和修订校验。
3. 计划至少包含 `Threshold.Otsu`、`Blob.Contours`、`Contour.PathMetrics`，结果节点输出 `Image`、`Count`、`PixelLength` 和 `Summary`。
4. 工作流验证成功，执行产生带标注预览；数量和长度为非负值。单图数值只作为回归观测值，不作为通用算法精度承诺。
5. 明确输入“使用 `Defect.CrackMeasure`”或“使用专用裂纹算子”时，才允许专项单算子方案；其当前基线输出可单独测试。
6. 三次规划均无效时必须停止，不能应用最后一次无效 JSON。
7. 重复执行且输入/参数不变时命中缓存；修改任一中间参数后只使该节点及下游失效。

详细模板见 [AI 测试用例模板](ai-test-case-template.md)，当前项目实例见 [裂纹数量与像素长度组合流程测试](ai-test-crack-count-length.md)。

远程模型设置的具体测试步骤与验收边界见 [远程模型添加与重载测试](ai-test-remote-model.md)。测试使用环回地址或内存 HTTP Handler，不向真实服务发送 API Key。

AI 自动连线的基数、语义、完整性和排版要求见 [AI 工作流连线与排版规范](ai-workflow-connection-spec.md)。

## Provider 实机测试

- 使用 `E:/models` 中的 GGUF；验证软件能自动发现 `D:/Programs/llama`，测试不手工向 Profile 填写运行时路径。
- 服务器只绑定 `127.0.0.1`，上下文至少 8192，规划输出上限至少 2048 token；优先让模型输出紧凑 `add_pipeline`，避免小模型重复生成大量连线 JSON。
- 若没有兼容 mmproj，记录 `vision=false` 并执行文字规划测试；不得把失败误报为完整 VLM 验收。
- 测试结束后终止且只终止测试启动的服务器进程。

真实模型端到端测试命令：

```powershell
./scripts/test-local-ai.ps1
```

日常 `Ai` 维度不加载约 2 GB 模型；上述脚本显式开启实机测试。本机目录没有 `mmproj`，因此已验证的是“Qwen 文本规划 → 受控动作 → OpenCV 原图测量”，不是完整 VLM 看图能力。

## 发布负向测试

递归扫描桌面和 Web 发布目录，出现以下任一模式即失败：

```text
*.gguf
llama-server*
llama.dll
mtmd.dll
ggml-*
cublas*
cudart*
VisionStudio.Ai.LlamaCpp*
```
