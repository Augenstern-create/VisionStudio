# 多平台发布说明

## 当前发布等级

| 平台 | 状态 | 说明 |
| --- | --- | --- |
| Windows x64 | 稳定桌面版 | 主安装包包含编辑器、CLI、项目运行平台、Windows EmguCV 原生运行库和 `.vstudio` 文件关联 |
| Ubuntu 24.04 x64 | 暂缓发布 | 构建脚本与 DEB 工程保留，但 V2.0 不上传 Linux 桌面发布物，等待真实桌面设备验收 |
| Web / Ubuntu 24.04 x64 | 稳定服务端版 | 响应式浏览器编辑器 + ASP.NET Core API + 服务端 EmguCV；提供自包含 tar.gz，供 Ubuntu Server 手动部署 |
| Android / iOS | V2.0 不支持 | 不生成、不测试、不发布移动安装包 |

## Windows 与 Linux

```powershell
./scripts/publish-desktop.ps1 -Platform All -Version 2.0.0
```

输出位于 `artifacts/releases`，每个平台同时生成压缩包和 SHA-256 文件。编辑器、CLI 和 `VisionStudio.Runtimes.App` 位于同一个自包含包，采用非裁剪模式，避免 Avalonia XAML、反射和 EmguCV 代码被链接器错误删除。Ubuntu 包同时携带 ICU，避免目标系统缺少匹配版本时在启动阶段失败；它使用与托管库完全同版的 `Emgu.CV.runtime.mini.ubuntu-24.04-x64`。

Ubuntu 包可在 WSL 或原生 Ubuntu 中执行定向验收：

```bash
./scripts/test-linux-release.sh
```

Linux 桌面包的目标范围是 Ubuntu 24.04 x64，不应标记为“任意 Linux”。构建脚本、DEB 工程和依赖声明继续保留，但由于本轮没有真实 Ubuntu 24.04 桌面设备完成 GUI、Runtime 和原生 OpenCV 验收，V2.0 不上传或发布 Linux 桌面包。Ubuntu Server Web 仍是正式发布项。

## 安装程序

```powershell
./scripts/build-installers.ps1 -Platform All -Version 2.0.0
./scripts/test-changes.ps1 -Dimension Packaging -NoBuild
```

Windows 输出 `VisionStudio-<版本>-win-x64-setup.exe`，采用与应用默认皮肤一致的深色现代向导、蓝色强调色和产品 Logo，支持中英文、编辑器与 Runtime 开始菜单入口、可选桌面快捷方式、当前用户安装与 `.vstudio` 文件关联。Linux 输出 `visionstudio_<版本>_amd64.deb`，由 Ubuntu 应用中心或 `apt` 安装；包内包含编辑器、CLI、Runtime、`visionstudio-runtime` 启动器、应用菜单入口、SVG 图标、AppStream 品牌元数据以及 `.vstudio` MIME 关联。

当前 Windows 安装器使用 Inno Setup 7.0.2。内部测试可直接使用；正式商业分发前必须由产品所有者取得相应商业许可，并使用受信任的代码签名证书签署安装程序和主程序，避免 Windows SmartScreen 的未知发布者警告。

## V2.0 移动平台边界

V2.0 明确排除 Android 和 iOS，不生成移动安装包，也不把移动端列为实验支持。Avalonia 支持 Android，但桌面项目不能直接改 RID 生成 APK，需要单独 Android 启动项目和移动生命周期；以下内容仅作为未来路线，不属于 V2.0 承诺。

未来若单独启动移动版，至少需要：

1. 安装与 Avalonia 版本兼容的 Android workload 和 SDK。
2. 新建 Android 入口，使用移动 Application Lifetime，不创建桌面 `Window`。
3. 把编辑器主体从 `MainWindow` 提取为可复用视图和 ViewModel。
4. 设计触控布局：算子栏与属性栏改为抽屉/页面，端口扩大触摸命中区。
5. 使用 `Emgu.CV.runtime.maui.mini.android`，并在应用启动时执行 `CvInvokeAndroid.Init()`；核对 mini runtime 是否覆盖全部当前算子。
6. 使用 Android Storage Access Framework 代替任意项目目录路径访问。
7. 配置应用 ID、图标、权限、正式 keystore、APK/AAB 签名和至少一台 arm64 真机回归。

这些事项不进入 V2.0 发布计划。

## Web Server

Web 端没有把 EmguCV 放入浏览器，而是通过 `VisionStudio.Web` 在 Ubuntu Server 执行现有工作流。浏览器 UI 针对 Web 文件上传、安全预览和响应式布局实现，Core、项目协议、算子实现、缓存和 AI 自动化会话均与桌面版复用。发布与生产部署见 [Web 稳定版与 Ubuntu Server 部署](./web-deployment.md)。
