# Web 稳定版与 Ubuntu Server 部署

## 交付范围

Web 稳定版采用浏览器编辑器、ASP.NET Core Web API 和服务器端 OpenCV 执行器。它复用桌面版的项目格式、工作流模型、算子目录、缓存、C# 生成器及 `WorkflowAutomationSession`，不让浏览器直接读写服务器路径，也不操作 `main.vflow`。

前端支持项目创建和打开、三类主题、中英文界面、算子搜索与折叠分类、节点拖动、画布平移、圆角正交连线、参数编辑、图片上传、点击节点执行与预览、自动排版、撤销、操作记录和 C# 导出。窄屏时算子栏和属性栏变为左右抽屉；桌面宽屏保持三栏工作台。

## 运行结构

```text
Browser (HTML/CSS/JavaScript)
        │ HTTPS + X-VisionStudio-Token
        ▼
Nginx reverse proxy
        │ http://127.0.0.1:5080
        ▼
VisionStudio.Web (ASP.NET Core)
        ├── WorkflowAutomationSession / ProjectService
        ├── OpenCvWorkflowAutomationRunner
        ├── Operator catalog / C# generator
        └── /var/lib/visionstudio-web/<project>/
```

每个项目有独立目录。上传只接受 PNG、JPEG、BMP、TIFF 和 WebP，单文件上限 50 MiB，并在保存后由 OpenCV 解码验证。文件读取 API 仅开放 `images/input`、`images/cache`、`images/output` 和 `output`，所有路径仍经过项目根目录越界检查。

工作流写入携带 `expectedRevision`，冲突返回 HTTP 409；`requestId` 保证重试幂等。一次批量修改形成一个撤销步骤，并继续使用项目原子保存、操作日志和节点缓存失效规则。

## 生成发布包

在开发机执行：

```powershell
./scripts/publish-web.ps1 -Version 2.0.0
./scripts/test-changes.ps1 -Dimension Web -NoBuild
```

输出是 `artifacts/web/VisionStudio.Web-2.0.0-ubuntu-x64.tar.gz` 及对应 `.sha256`。这是 Ubuntu 24.04 x64 自包含包，不需要在服务器安装 .NET，也不是安装程序。

## Ubuntu Server 部署

```bash
sha256sum -c VisionStudio.Web-2.0.0-ubuntu-x64.tar.gz.sha256
sudo mkdir -p /opt/visionstudio-web
sudo tar -xzf VisionStudio.Web-2.0.0-ubuntu-x64.tar.gz -C /opt/visionstudio-web
cd /opt/visionstudio-web
sudo ./install-dependencies.sh
sudo cp .env.example .env
sudo sed -i "s/replace-with-a-long-random-token/$(openssl rand -hex 32)/" .env
sudo chmod 600 .env
```

先以前台方式验证：

```bash
sudo ./start.sh
```

默认只监听 `127.0.0.1:5080`。确认 `curl http://127.0.0.1:5080/api/health` 返回 `healthy` 后按 Ctrl+C 停止，再启用服务：

```bash
sudo cp visionstudio-web.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now visionstudio-web
sudo systemctl status visionstudio-web
```

复制并修改 `nginx.conf.example` 的域名，启用站点后配置 TLS。不要把 Kestrel 的 HTTP 端口直接暴露到不可信网络。浏览器第一次打开时输入 `.env` 中的访问令牌；令牌只保存在当前浏览器标签会话的 `sessionStorage`。

## 运维边界

- 业务数据默认位于 `/var/lib/visionstudio-web`，备份和恢复以整个目录为单位。
- `.env` 包含访问令牌，权限应保持 `600`；更换令牌后重启服务。
- systemd 单元使用动态低权限用户、专属状态目录和文件系统保护。
- Nginx 示例将请求体限制为 51 MiB，并关闭代理缓冲以避免大图片重复落盘。
- 当前鉴权面向内部单租户部署，不提供账号、角色和项目级权限。部署到多用户或公网场景前应接入组织身份认证、审计和细粒度授权。
- 当前进程内维护编辑会话与撤销栈，因此稳定版按单实例运行。若要多实例横向扩容，需要外置会话/锁、共享存储和分布式并发控制。

## 故障排查

```bash
journalctl -u visionstudio-web -n 200 --no-pager
curl http://127.0.0.1:5080/api/health
sudo nginx -t
```

服务在 Production 环境缺少 `VISIONSTUDIO_WEB_TOKEN` 时会拒绝启动，这是预期的安全保护。OpenCV 加载错误通常表示 `install-dependencies.sh` 未执行完整，或服务器不是 Ubuntu 24.04 x64。
