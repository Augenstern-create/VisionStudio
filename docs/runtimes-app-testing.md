# Vision Studio 项目运行平台测试

## 本阶段测试维度

- `RuntimeProject`：打开 `.vstudio`、读取主工作流和运行配置。
- `RuntimeInput`：`Input.RuntimeFrame` 注册、外部图像注入和所有权释放。
- `RuntimeSource`：单图片、图片文件夹、视频、模拟图和 EmguCV 相机工厂路由。
- `RuntimeExecution`：DemoProject 使用 `tests/image/1.png` 完成真实工作流执行。
- `RuntimeState`：合法状态链、非法迁移、暂停、继续和停止。
- `RuntimeTrigger`：手动触发前零采集，触发后单帧；定时触发连续产出。
- `RuntimeQueue`：高速连续采集下容量有界，保留最新帧策略产生可观测丢帧。
- `RuntimeResult`：主图、数值、文本、单位、节点耗时和错误上下文完整。
- `RuntimeUi`：实际打开项目、运行一次、显示结果图和完整映射结果。
- `CodeStyle`：不得增加现有规范基线。

## 自动化场景

1. 外部图像成功注入 `Input.RuntimeFrame`，下游灰度节点输出有效 Mat。
2. 图片、文件夹和模拟源能采集非空帧；视频与相机类型能创建对应可替换实现。
3. `samples/DemoProject` 默认配置能加载并通过 `WorkflowValidator`。
4. DemoProject 单次运行成功，正式结果图存在且所有配置映射均被返回。
5. 缺少输入节点、无效结果端口或采集文件时返回明确错误，桌面进程不退出。
6. 状态机执行 Ready → Starting → Running → Paused → Running → Stopping → Stopped，非法迁移抛出明确错误。
7. 手动模式启动 100 ms 后仍为零采集；提交触发后产生一项完整结果。
8. 定时模式处理至少两帧，可暂停、继续并停止，停止后队列深度不超过配置容量。
9. 连续模式配合容量 1 的 `LatestFrame` 队列形成处理积压时，统计必须出现丢帧且队列深度始终不超过 1。

## 实际界面验收

使用以下入口预加载项目，仍必须在界面点击“运行一次”，不得用测试进程代替界面：

```text
VisionStudio.Runtimes.App.exe --project <DemoProject.vstudio>
```

验收界面必须显示项目名称、工作流节点、采集源、触发与队列设置、运行状态、统计、
主结果图、全部结果映射、总耗时、节点耗时和诊断。分别验证“运行一次”和连续运行的
开始、暂停、继续、手动触发、停止。相机硬件测试属于可选设备测试；自动化测试不得依赖
本机存在相机。

本次只测试上述 Runtime 维度，不执行 AI、Web、编辑器和安装包测试。
