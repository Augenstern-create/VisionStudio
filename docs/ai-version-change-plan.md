# 1.1 本地 AI 插件改造方向

## 新增项目

| 项目 | 职责 | 是否进入主安装包 |
| --- | --- | --- |
| `VisionStudio.Ai.Abstractions` | 稳定插件契约、请求响应、模型 Profile、诊断 | 是 |
| `VisionStudio.Ai` | 插件发现、加载、生命周期与通用对话协调 | 是 |
| `plugins/VisionStudio.Ai.LlamaCpp` | llama-server Provider 与进程生命周期 | 否，单独发布 |

## 核心修改

1. 扩展 `OperatorDescriptor`，增加 `Aliases`、`UseCases`、`Limitations`、`Keywords`，保持旧构造调用兼容。
2. 增加插件清单验证和受控加载器，主程序只依赖抽象契约。
3. Llama Provider 支持外部 Endpoint 和受控启动本机 `llama-server` 两种模式。
4. Agent 只接受严格 JSON，动作转为现有 `WorkflowEdit` 批量提交。
5. 增加通用细长目标路径统计算子及可选专项裂纹算子，提供可验证的结构化输出。
6. 增加模型侧 `add_pipeline` 紧凑协议，由宿主按端口展开为标准 Graph 动作，降低小模型生成长 JSON 的失败率。
7. 桌面端使用 VS Code 式 Activity Bar 切换算子与内嵌 AI 对话；“文件 → 设置”管理多个本地、远程和标准版模型。
8. 发布脚本增加负向检查，发现 `.gguf`、`llama-server`、`ggml-*`、CUDA DLL 或插件 Provider 即失败。

## 配置与目录

```text
<VisionStudio 安装目录>/
├── plugins/
│   └── visionstudio.ai.llamacpp/2.0.0/
│       ├── plugin.json
│       └── *.dll
├── models/
├── runtime/
│   └── llama.cpp/
└── config/
    ├── ai-local-models.json
    ├── ai-remote-models.json
    ├── standard-models.json
    └── ai-contexts.json
```

自定义模型可位于任意目录；标准版模型下载到平台可写的软件数据目录。Windows 当前用户安装使用安装目录，Linux 使用 `$XDG_DATA_HOME/visionstudio`。llama.cpp 优先自动发现，缺失时安装到同一软件数据目录的 `runtime/llama.cpp`。项目文件夹不保存 AI 配置或对话上下文，安装程序仍不捆绑大模型与运行时。

## 迁移原则

- `.vstudio` 和 `.vflow` schema 本版本不升级。
- 没有安装 AI 插件时现有项目行为完全不变。
- AI 创建的普通 OpenCV 节点可由旧执行器正常运行；若使用新裂纹算子，旧版本会明确报告未知算子。
- `IWorkflowAutomationApi` 是唯一 Graph 写入接口，避免未来更换模型时再次修改项目核心。
