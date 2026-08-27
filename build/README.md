# Vision Studio 构建入口

本目录只保存构建脚本，所有生成物写入根目录 `artifacts`，不会把二进制文件提交到 Git。

## 测试版

测试版使用 `Debug` 配置并保留调试符号。默认构建 Windows x64，运行单元测试，并在仓库外独立编译、执行两条 C# 导出流程。

```powershell
.\build\build-test.ps1
.\build\build-test.ps1 -Platform All -IncludeWeb
.\build\build-test.ps1 -UiAutomation
```

如只需要快速编译，可使用 `-SkipTests`。测试版输出位于 `artifacts/builds/test`。

测试发布目录同时包含编辑器、CLI 和 `VisionStudio.Runtimes.App`。运行平台直接读取 `.vstudio`，不执行导出的 C# 脚本。

## 发布版

发布版使用 `Release` 配置。默认构建 Windows x64 安装包，并运行单元测试、C# 独立导出测试和 Web API 测试。

```powershell
.\build\build-release.ps1 -Version 2.0.0
.\build\build-release.ps1 -Version 2.0.0 -Platform All -IncludeWeb
.\build\build-release.ps1 -Version 2.0.0 -Platform Windows -FullValidation
```

- `-SkipInstaller`：只生成桌面免安装压缩包。
- `-IncludeWeb`：同时生成 Ubuntu Web 部署包。
- `-FullValidation`：增加真实桌面 UI、Windows 安装/卸载或 Linux DEB 验收。
- `-SkipTests`：仅在已经完成同一提交的可信测试后使用。

发布目录：

- 桌面压缩包：`artifacts/releases`
- Windows/Linux 安装包：`artifacts/installers`
- Web 部署包：`artifacts/web`

桌面压缩包和主安装包都包含 `VisionStudio.Runtimes.App`，不再单独生成旧的 C# 脚本运行器产品。

Windows 也可以直接运行同名 `.cmd`，参数会原样传给 PowerShell 脚本。
