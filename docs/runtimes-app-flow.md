# Vision Studio 项目运行平台流程

## 单次运行

1. 用户打开 `.vstudio` 项目。
2. `ProjectService` 读取项目与主工作流。
3. `RuntimeProfileStore` 读取默认运行配置。
4. 运行器验证 `Input.RuntimeFrame`、执行目标和全部 `resultMappings`。
5. 用户选择采集源并点击“运行一次”。
6. `IFrameSource` 打开相机或文件源并获取一帧。
7. 采集帧以外部输出形式注入 `Input.RuntimeFrame.Image`。
8. `WorkflowExecutor` 执行到配置的目标节点。
9. 运行器提取映射结果，将图像写入 `images/output/runtime`。
10. UI 显示结果图、全部结果值、采集信息、总耗时和节点耗时。
11. 采集源和本次工作流结果被可靠释放，程序回到可再次运行状态。

加载、采集、验证或执行失败时，主窗口保持打开，诊断显示项目、采集源和失败阶段。

## 连续运行

1. UI 保存采集源、触发类型、间隔、队列策略和容量。
2. 控制器从 Ready/Stopped 进入 Starting，创建并打开一个长期存活的 `IFrameSource`。
3. 控制器创建 `IRuntimeTrigger` 和有界帧队列，进入 Running。
4. 采集生产者等待触发；获得一帧后按队列策略写入队列。
5. 工作流消费者逐帧调用现有 `ProjectRuntimeService.ExecuteFrameAsync`。
6. 每个结果继续按原有 `resultMappings` 提取并推送到 UI；工作流和结果格式不变。
7. 暂停将状态切到 Paused 并阻止后续采集，正在执行的帧允许完成；继续回到 Running。
8. 停止进入 Stopping，取消触发等待和采集，释放队列残留、触发源和采集设备，最终进入 Stopped。
9. 不可恢复错误进入 Faulted；`ContinueAfterError=true` 时单帧算法错误被记录后继续下一帧。

手动触发采用容量为 1 的信号队列，重复触发不会无限堆积。未来 PLC、光电开关或相机硬触发
适配器只需实现同一触发接口。
