# Web、CLI、发布与部署

## 1. 四种运行入口共享什么

桌面、Runtime、Web 和 CLI 都复用 Core 与 OpenCV 算子注册表，因此项目格式、端口校验、执行顺序和缓存语义保持一致。编辑器、Web 和 CLI 还复用代码生成器；Runtime 直接执行 `.vstudio`，不运行导出脚本。

| 入口 | 最适合 | 限制 |
| --- | --- | --- |
| 桌面端 | 本地可视化编辑、调参、AI 对话 | 需要安装，受本机资源影响 |
| Runtime | 相机/文件/视频逐帧检测、状态和完整结果显示 | 厂商相机、PLC、MES 需要后续适配 |
| Web | 内部团队统一访问、服务器集中运行 | 当前单租户、前端需单独维护 |
| CLI | CI、批处理、脚本和无界面环境 | 不提供可视化交互 |

## 2. Web 架构

浏览器只负责编辑、上传和展示，不加载 EmguCV 原生库。所有工作流修改和 OpenCV 执行发生在 Ubuntu Server：

```text
Browser
  → HTTPS / X-VisionStudio-Token
  → Cloudflare Tunnel 或 Nginx
  → http://127.0.0.1:5080
  → ASP.NET Core
  → WorkflowAutomationSession
  → OpenCV / 项目目录
```

主要 API 包括健康检查、项目列表/创建、算子目录、快照、批量修改、执行、撤销、上传、导出、操作记录和受限文件读取。

上传限制为常见图片格式和 50 MiB，并在保存后用 OpenCV 解码验证。读取接口只开放项目中的 input/cache/output 相关目录，路径仍经过越界保护。

## 3. Web 并发语义

客户端修改携带 `expectedRevision`，旧版本提交返回 409。`requestId` 防止网络重试重复应用。当前服务器进程内维护项目会话和撤销栈，稳定版按单实例运行；横向扩容前需要分布式锁、共享存储和外置会话。

## 4. Cloudflare Tunnel

使用 Tunnel 时，Kestrel 只监听 `127.0.0.1:5080`，Cloudflare 连接到 `http://localhost:5080`。服务器防火墙不需要对公网开放 5080。若把源服务错误配置为 HTTPS，而 Kestrel 实际是 HTTP，会出现连接或握手失败。

Tunnel 解决公网入口和 TLS，不替代应用令牌、授权、审计和业务隔离。

## 5. CLI

CLI 支持项目创建、工作流验证、执行和 C# 导出。它适合：

- 在 CI 中验证提交的 `.vflow`；
- 对一批项目或图片执行固定流程；
- 在没有桌面的服务器生成代码；
- 作为集成测试的稳定入口。

CLI 不维护另一套算法逻辑，而是调用相同服务和注册表。

## 6. 桌面发布

V2.0 正式生成并发布 Windows x64 自包含包和安装程序，主包同时包含编辑器、CLI 和 Runtime。Windows 使用 Inno Setup，包含编辑器/Runtime 开始菜单、图标、可选桌面快捷方式和 `.vstudio` 文件关联。Ubuntu DEB 工程仍保留，但在真实 Ubuntu 24.04 桌面设备验收前不作为 V2.0 发布物上传。

主安装包不包含 AI 插件、模型或 llama.cpp。插件单独生成压缩包，标准模型提供下载地址、大小、许可和校验值。

## 7. Web 发布

Web 发布为 Ubuntu x64 tar.gz，而不是安装器。典型目录为 `/opt/visionstudio-web`，数据位于 `/var/lib/visionstudio-web`，通过 systemd 托管。生产环境缺少 `VISIONSTUDIO_WEB_TOKEN` 时拒绝启动，这是安全保护。

## 8. 移动平台边界

V2.0 明确不支持 Android/iOS，不生成移动安装包。移动设备需要访问时使用响应式 Web 端；原生移动应用属于未来独立版本。

## 9. 发布前清单

- Release 构建无警告；
- 运行受影响维度和发布级完整测试；
- 核对 OpenCV、EmguCV、Avalonia、Inno Setup、模型与插件许可；
- Windows 主程序和安装器代码签名；
- 生成 SHA-256；
- 在干净 Windows 环境安装、启动、关联文件和卸载；Linux 桌面发布恢复时再补做 Ubuntu 验收；
- 验证主包不包含 GGUF、mmproj、llama-server 或 Provider 插件；
- 验证 Windows/Linux 主包都包含编辑器、CLI 和 Runtime；
- 记录版本、变更、已知问题和恢复方式。
