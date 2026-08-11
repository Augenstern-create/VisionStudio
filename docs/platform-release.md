# 多平台发布说明

## 当前发布等级

| 平台 | 状态 | 说明 |
| --- | --- | --- |
| Windows x64 | 稳定桌面版 | 自包含目录发布，包含 Windows EmguCV 原生运行库和 `.vstudio` 文件关联 |
| Ubuntu 24.04 x64 | 稳定桌面版 | 自包含发布；EmguCV 4.13 原生执行、CLI 工作流及虚拟显示器 GUI 启动均已验收 |
| Android | 尚未形成稳定版 | 当前仓库没有 Android workload、移动启动项目、触控布局、存储权限适配、签名密钥和设备验收 |
| Web / Ubuntu 24.04 x64 | 稳定服务端版 | 响应式浏览器编辑器 + ASP.NET Core API + 服务端 EmguCV；提供自包含 tar.gz，供 Ubuntu Server 手动部署 |

## Windows 与 Linux

```powershell
./scripts/publish-desktop.ps1 -Platform All -Version 1.0.0
```

输出位于 `artifacts/releases`，每个平台同时生成压缩包和 SHA-256 文件。桌面应用和 CLI 位于同一个自包含包，采用非裁剪模式，避免 Avalonia XAML、反射和 EmguCV 代码被链接器错误删除。Ubuntu 包同时携带 ICU，避免目标系统缺少匹配版本时在启动阶段失败；它使用与托管库完全同版的 `Emgu.CV.runtime.mini.ubuntu-24.04-x64`。

Ubuntu 包可在 WSL 或原生 Ubuntu 中执行定向验收：

```bash
./scripts/test-linux-release.sh
```

Linux 包当前准确范围是 Ubuntu 24.04 x64，不应标记为“任意 Linux”。发布包内 `RELEASE.txt` 给出了必要的 `apt` 依赖命令；其中包括桌面图形库以及 EmguCV 所需的 FFmpeg、GStreamer、GeoTIFF、OpenJPEG、BLAS/LAPACK 和 GTK 运行库。已在隔离的 Ubuntu 24.04 环境执行项目校验、4 节点 OpenCV 工作流、结果文件检查和 GUI 启动测试。EmguCV 的 Linux 分发许可仍应在正式对外发布前由产品所有者确认。

## 安装程序

```powershell
./scripts/build-installers.ps1 -Platform All -Version 1.0.0
./scripts/test-changes.ps1 -Dimension Packaging -NoBuild
```

Windows 输出 `VisionStudio-<版本>-win-x64-setup.exe`，采用与应用默认皮肤一致的深色现代向导、蓝色强调色和产品 Logo，支持中英文、开始菜单、可选桌面快捷方式、当前用户安装与 `.vstudio` 文件关联。Linux 输出 `visionstudio_<版本>_amd64.deb`，由 Ubuntu 应用中心或 `apt` 安装；包内包含依赖声明、应用菜单入口、SVG 图标、AppStream 品牌元数据、命令行入口以及 `.vstudio` MIME 关联。

当前 Windows 安装器使用 Inno Setup 7.0.2。内部测试可直接使用；正式商业分发前必须由产品所有者取得相应商业许可，并使用受信任的代码签名证书签署安装程序和主程序，避免 Windows SmartScreen 的未知发布者警告。

## Android 为什么不能直接发布

Avalonia 支持 Android，但桌面项目不能直接改 RID 生成 APK，需要单独 Android 启动项目和移动生命周期。当前编辑器最小宽度 1100，依赖多栏桌面布局、窗口对话框、键盘快捷键、Windows 文件关联和项目目录访问；即使强行编译，手机上也不构成可用的稳定版本。

Android 稳定版至少需要：

1. 安装与 Avalonia 版本兼容的 Android workload 和 SDK。
2. 新建 Android 入口，使用移动 Application Lifetime，不创建桌面 `Window`。
3. 把编辑器主体从 `MainWindow` 提取为可复用视图和 ViewModel。
4. 设计触控布局：算子栏与属性栏改为抽屉/页面，端口扩大触摸命中区。
5. 使用 `Emgu.CV.runtime.maui.mini.android`，并在应用启动时执行 `CvInvokeAndroid.Init()`；核对 mini runtime 是否覆盖全部当前算子。
6. 使用 Android Storage Access Framework 代替任意项目目录路径访问。
7. 配置应用 ID、图标、权限、正式 keystore、APK/AAB 签名和至少一台 arm64 真机回归。

在这些条件完成前，只能称 Android 原型，不能标记稳定版。

## Web Server

Web 端没有把 EmguCV 放入浏览器，而是通过 `VisionStudio.Web` 在 Ubuntu Server 执行现有工作流。浏览器 UI 针对 Web 文件上传、安全预览和响应式布局实现，Core、项目协议、算子实现、缓存和 AI 自动化会话均与桌面版复用。发布与生产部署见 [Web 稳定版与 Ubuntu Server 部署](./web-deployment.md)。
