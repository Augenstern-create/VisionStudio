# 本地 AI 插件架构

## 目标

Vision Studio 1.1 的 AI 助手负责把自然语言需求转换成受控的工作流修改：理解需求、检索算子、提出方案、生成严格动作、经工作流校验后一次提交、执行并解释结果。AI 不直接操作 Avalonia 控件，不直接读写 `.vflow`，也不生成要执行的 C#。

AI 采用可下载插件。主程序发布包只包含插件契约、插件加载器、通用对话壳和 `IWorkflowAutomationApi`，不得包含 GGUF、llama.cpp、CUDA 动态库、模型专用提示词或 Provider 实现。

## 分层

```text
VisionStudio.App
  ├── 左侧 Activity Bar（算子 / AI）
  ├── 内嵌 AI 对话、模型选择、建议与应用
  ├── 文件 → 设置 → 本地模型 / 远程模型
  ├── AiPluginHost
  └── WorkflowAutomationApiHost
          │
          ▼
VisionStudio.Ai.Abstractions
  ├── IAiAssistantPlugin
  ├── IAiProvider
  ├── AiModelProfile
  ├── AiConversationRequest/Response
  └── 插件清单与能力声明
          ▲
          │ 动态加载，不由 App 项目引用
plugins/VisionStudio.Ai.LlamaCpp
  ├── LlamaServerProvider
  ├── llama-server 生命周期
  └── OpenAI 兼容 HTTP 适配
          │ HTTP /v1/chat/completions
          ▼
外部 llama-server + 外部 GGUF/mmproj
```

`OperatorSearchService`、`AiWorkflowAgent`、动作验证与结果协调位于主程序携带的通用 `VisionStudio.Ai`，不属于任何模型插件，因此切换 Qwen、Llama 或其他本地模型时不修改工作流核心。

插件通过独立 `AssemblyLoadContext` 从软件安装目录的 `plugins` 加载；开发和测试可通过 `VISIONSTUDIO_AI_PLUGIN_PATH` 指定额外目录。每个插件目录包含 `plugin.json` 和入口程序集。

`llama-server` 与 Provider 插件是两个不同组件：前者是模型推理进程，后者是 Vision Studio 调用该进程的连接器。源码开发时，程序会检查当前可执行目录，并沿父目录定位 `VisionStudio.sln` 后自动加入 `plugins/VisionStudio.Ai.LlamaCpp/bin/{Debug|Release}/net9.0`，因此从 App 或 Tests 的输出目录启动都不需要手工复制插件。正式发布仍只检查安装目录 `plugins` 和显式的 `VISIONSTUDIO_AI_PLUGIN_PATH`。

## 插件清单

```json
{
  "schemaVersion": 1,
  "id": "visionstudio.ai.llamacpp",
  "name": "Vision Studio Local AI - llama.cpp",
  "version": "2.0.0",
  "entryAssembly": "VisionStudio.Ai.LlamaCpp.dll",
  "entryType": "VisionStudio.Ai.LlamaCpp.LlamaCppAssistantPlugin",
  "minimumHostVersion": "2.0.0",
  "capabilities": ["chat", "operator-search", "graph-actions", "vision"]
}
```

入口路径必须解析后仍在插件目录内；清单、程序集、ID 和版本均先验证再加载。插件是本机代码，安装等同于信任发布者，不能把它视为脚本沙箱。

## 模型配置

AI 是软件级配置，不写入项目目录。本地和远程配置使用不同文件与不同持久化结构：

- `<软件配置目录>/ai-local-models.json`：只保存本地/标准模型路径和推理参数；
- `<软件配置目录>/ai-remote-models.json`：每个远程 Profile 只暴露内部 ID，名称、模型 ID、端点、API Key 与能力参数整体加密；
- `<安装目录>/config/standard-models.json`：随程序只读分发的标准模型下载目录；可由软件配置目录同名文件覆盖。

本地配置示例：

```json
{
  "id": "qwen25-vl-3b-q4",
  "displayName": "Qwen2.5-VL 3B Q4_K_M",
  "modelPath": "E:/models/Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf",
  "mmprojPath": null,
  "llamaServerPath": "<由软件自动检测或安装>",
  "contextSize": 4096,
  "gpuLayers": -1,
  "endpoint": "http://127.0.0.1:11435",
  "source": "Local",
  "modelId": "qwen2.5-vl-3b-instruct",
  "providerPluginId": "visionstudio.ai.llamacpp"
}
```

`Source` 支持 `Local`、`Remote`、`StandardLocal`。远程模型使用标准 OpenAI `/v1/chat/completions` 协议。Windows 使用当前用户 DPAPI 加密完整远程参数；其他桌面平台使用 AES-GCM 和权限为 `0600` 的独立主密钥文件。本地设置不显示 llama-server 路径和 Provider ID：软件依次检查已保存路径、环境变量、软件 runtime、常用目录与 PATH；仍未发现时从 llama.cpp 官方 GitHub Release API 选择当前 CPU/平台包。Windows 当前用户安装目录可写，配置、模型和 runtime 位于安装目录；Linux DEB 安装在只读系统目录，因此配置使用 `$XDG_CONFIG_HOME/visionstudio`，模型、runtime 和用户插件使用 `$XDG_DATA_HOME/visionstudio`。这些都是软件级目录，不进入项目。

V2.0 预制 `ggml-org/Qwen2.5-VL-3B-Instruct-GGUF` 的固定修订版本，下载 Q4_K_M 主模型和 Q8_0 mmproj。设置页在下载前显示来源、Apache-2.0 许可证和总大小；每个文件在原子安装前校验预期字节数与 SHA-256，失败时删除临时文件。模型和 llama.cpp 仍不嵌入主安装包。

配置只保存路径、接口和启动参数，不保存模型内容。对话上下文保存在 `config/ai-contexts.json`，键由模型 ID 与项目 ID 组成，切换模型或项目不会串用上下文。缺少多模态投影文件时本地模型声明 `vision=false`，文字规划仍可用；原图测量始终由 OpenCV 完成。

每个 `Image.Load` 节点在画布上显示 `IMG-01` 形式的引用。对话框上方同时列出图片引用；用户可说“处理 IMG-02 / 图片2 / 零件背面”，点击引用，或把画布节点右上角编号拖入对话框。存在多张输入图且没有明确引用时，助手要求用户指定，不再隐式取第一张。

## 兼容与发布

- 主程序与插件契约使用独立程序集和 `ContractVersion`。
- 插件声明最低主程序版本，不兼容时显示诊断而不是崩溃。
- 主程序安装器不构建、不复制 `plugins/` 项目的输出。
- AI 插件单独生成压缩包；模型只提供下载链接、文件大小、SHA-256 和兼容说明。
- 插件更新不得修改项目格式。AI 产生的节点仍是普通 Vision Studio 节点，没有插件时项目也可查看和执行非 AI 节点。
