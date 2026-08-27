# AI-REMOTE-001：远程模型添加与重载测试

## 目的

验证“文件 → 设置 → 远程模型”从新增、编辑、加密保存到重新打开和调用 OpenAI 兼容接口的完整路径。测试不使用真实账户，不向公网发送请求。

## 虚拟配置

| 字段 | 测试值 |
| --- | --- |
| 显示名称 | `虚拟 OpenAI 测试模型` |
| 接口模型 ID | `virtual-vision-model` |
| OpenAI Base URL | `http://127.0.0.1:65534/v1` |
| API Key | `vs-test-key-not-real` |
| 图片输入 | 开启 |

端口 `65534` 不应运行服务。仅验证配置时不得发起连接；Provider 请求测试使用内存 HTTP Handler，不依赖该端口。

## 设置界面验收

1. 点击“＋远程模型”后立即创建并选中新条目，右侧显示默认值。
2. 可修改名称、模型 ID、Base URL、API Key 和图片能力。
3. 名称、模型 ID、Base URL 为空时禁止保存，并显示对应字段错误。
4. Base URL 只接受绝对 `http://` 或 `https://` 地址；文件路径、相对 URL 和其他协议必须拒绝。
5. 点击确定后关闭设置窗体，不出现异常。
6. 重新打开设置并进入远程模型页时，首个已选模型的全部字段自动回填；不能出现“左侧已选中、右侧全空”的状态。
7. 添加两个模型，在两项之间切换时，离开前的编辑内容仍被保留。
8. 删除、取消和确定分别遵循预期；取消不覆盖磁盘配置。

## 存储与安全验收

1. 远程模型只写入当前平台的软件级 `ai-remote-models.json`，不进入项目目录或本地模型 JSON。
2. JSON 顶层只保留配置 ID 和 `encryptedPayload`。
3. 文件中不得出现显示名称、Endpoint、模型 ID、API Key 或能力参数明文。
4. 同一系统账户重新打开后可恢复全部字段；损坏密文必须明确报错，不能当作空配置覆盖。
5. Windows 使用当前用户 DPAPI；Linux 使用权限受限的本机密钥文件。密文不承诺跨账户、跨机器可迁移。

## OpenAI 兼容请求验收

使用内存 Handler 捕获请求并验证：

- 请求地址规范化为 `{Base URL}/v1/chat/completions`，已有 `/v1` 时不能重复；
- JSON 中的 `model` 等于配置的接口模型 ID；
- 非空 API Key 以 `Authorization: Bearer` 发送；
- 图片能力关闭时不读取或发送图片；开启时按 data URL 发送预览缩略图；
- 401、404、429、5xx、超时、取消和非 JSON 响应转换为可理解错误，不删除模型配置。

## 自动化与实际界面记录

- `RemoteModel_VirtualAddSaveReloadAndSecondModelPreserveEncryptedFields`：虚拟新增、两次重载、多模型与无明文测试。
- `OpenAiCompatibleProvider_UsesStandardEndpointModelAndBearerToken`：标准接口地址、模型 ID 与 Bearer 测试。
- `scripts/test-ai-ui.ps1`：设置结构、初始回填及切换保存的静态回归检查。
- 实际 Debug 界面测试应填写上述虚拟值，保存、重开确认，然后清理测试配置；不得输入真实 API Key。

## 本次缺陷回归

缺陷现象：首次保存成功后重新打开设置，左侧远程模型处于选中状态，但右侧编辑框为空，点击确定会误报必填项为空。

根因：加载列表时通过 `_loading` 屏蔽了 `SelectionChanged`，加载结束后未显式调用编辑器回填。

通过条件：加载结束后主动回填当前本地/远程项；切换模型前把当前有效编辑值写回内存集合；最终确定时再次严格验证并加密保存。
