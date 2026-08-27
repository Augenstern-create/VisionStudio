# 技术栈与选型理由

## 1. 核心语言与运行时

项目使用 C#、.NET 9，启用 Nullable、Implicit Usings、最新语言版本、确定性构建和警告即错误。统一运行时使桌面、Web、CLI、核心库和测试共享类型系统与大部分代码。

选择 .NET 的主要原因：

- 强类型、异步、依赖管理和跨平台发布能力成熟；
- 适合构建长期维护的工程工具，而不只是算法脚本；
- ASP.NET Core、xUnit 和桌面框架可以共享语言与 DTO；
- 支持自包含发布，服务器不必预装 .NET；
- `AssemblyLoadContext` 适合插件隔离加载。

## 2. Avalonia 12 桌面 UI

Avalonia 提供 XAML 风格的跨平台桌面 UI。项目使用它实现 Windows 和 Linux 编辑器、主题动态资源、绘制型画布、自定义聊天消息控件、文件选择、窗口装饰和 UIAutomation 可访问性。

为什么没有使用 WPF：Avalonia 为未来恢复 Linux 桌面发布保留了跨平台能力；V2.0 当前只正式发布 Windows 桌面版。为什么 Web 不直接复用 Avalonia UI：浏览器端的文件上传、响应式抽屉、部署和安全模型与桌面不同，当前 Web 使用独立 HTML/CSS/JavaScript 前端，复用的是 Core 和 API，不是控件树。

## 3. Emgu CV 4.13 / OpenCV

Emgu CV 是 OpenCV 的 .NET 封装。算子执行层使用 `Mat`、颜色转换、滤波、阈值、形态学、轮廓和几何 API；Windows 与 Ubuntu 使用对应原生运行库包。

选用封装而不是通过进程调用 Python的原因：

- 图像在同一进程内流转，减少序列化和临时文件；
- 类型、异常、资源释放和性能测量更容易统一；
- C# 代码生成与运行时算子可以保持相近语义。

代价是原生库发布需要按平台处理，Linux 还需要系统图形/编解码依赖；`Mat` 等非托管资源必须明确释放。

## 4. ASP.NET Core Minimal API

Web 端使用 Minimal API 暴露健康检查、项目、算子目录、快照、修改、执行、撤销、上传、导出、日志和受限文件读取。Minimal API 对当前内部服务的路由规模足够简洁，同时保留依赖注入、中间件、请求限制和生产托管能力。

## 5. OpenAI 兼容协议与 llama.cpp

远程模型和本地 llama-server 都统一到 `/v1/chat/completions` 风格接口。这样 `VisionStudio.Ai` 只需要处理一种会话协议；本地插件负责进程生命周期和端点连接，远程 Provider 负责 Base URL、Model ID 和 Bearer Token。

GGUF 用于本地量化模型，mmproj 用于多模态视觉投影。没有 mmproj 时模型仍可做文本规划，但不能宣称它看过图片；像素运算始终由 OpenCV 完成。

## 6. JSON、SHA-256 与 DPAPI/AES-GCM

- `.vstudio`、`.vflow` 和大部分配置采用 JSON，便于诊断和版本管理；
- 工作流缓存键使用 SHA-256，综合算子类型、参数、上游键和外部文件指纹；
- Windows 远程模型参数使用当前用户 DPAPI；其他桌面平台使用 AES-GCM 与独立主密钥；
- 项目保存使用临时文件后原子替换，降低中断导致半写文件的风险。

## 7. xUnit 与 PowerShell 自动化

xUnit 验证核心模型、算法、项目、AI 和代码生成。PowerShell 脚本负责真实进程、UIAutomation、鼠标键盘、安装器、Web API 和跨平台发布检查。二者分别回答“内部状态是否正确”和“用户入口是否真的可用”。

## 8. 技术栈版本基线

| 项目 | 当前基线 |
| --- | --- |
| .NET | 9.0 |
| C# | `latest`，Nullable 开启 |
| Avalonia | 12.1.0 |
| Emgu CV / OpenCV 封装 | 4.13.0.5924 |
| ASP.NET Core | .NET 9 内置 |
| xUnit | 2.9.2 |
| 测试 SDK | 17.12.0 |

版本升级时必须同时验证 Avalonia 控件行为、EmguCV 原生 RID、安装包、Linux 依赖和 UIAutomation，不能只看编译通过。
