# Vision Studio 项目运行平台架构

## 定位

`VisionStudio.Runtimes.App` 是项目级工业视觉运行平台。它直接打开 `.vstudio`
项目、读取项目主工作流并调用现有 `WorkflowExecutor`，不再编译或执行导出的 C# 文件。
C# 导出仍属于编辑器和二次开发能力。

## 复用原则

- 项目解析复用 `ProjectService`。
- 工作流校验和执行复用 `WorkflowValidator`、`WorkflowExecutor`。
- 算子注册和图像编解码复用 `OpenCvOperatorCatalog`、`OpenCvFileCodec`。
- 不在 Runtime 项目复制 Core、OpenCV 算子或编辑器已有实现。
- 新代码只补充采集抽象、运行配置、帧注入、结果映射和运行界面。

## 当前范围

- 打开 `.vstudio` 文件或项目目录。
- 读取 `workflows/main.vflow` 和 `runtime/profiles/default.runtime.json`。
- 通过 `Input.RuntimeFrame` 把采集图像注入工作流。
- 支持 EmguCV 相机、单图片、图片文件夹、视频和模拟图采集源。
- 单次执行并显示主结果图、全部映射结果、节点耗时和诊断。
- 连续、定时和手动触发采集；支持暂停、继续和停止。
- 使用可配置的有界帧队列隔离采集速度与工作流处理速度。
- 不实现 PLC 协议、结果上传和离线重传。

## 分层

- `IFrameSource`：可替换采集边界；负责打开、采集一帧和释放设备。
- `FrameSourceFactory`：根据运行配置创建 EmguCV 或文件采集实现。
- `RuntimeProfileStore`：加载和保存项目级采集、执行目标及结果映射。
- `ProjectRuntimeService`：复用现有项目与工作流服务，注入图像并执行一次工作流。
- `RuntimeStateMachine`：约束 Ready、Starting、Running、Paused、Stopping、Stopped、Faulted 状态迁移。
- `IRuntimeTrigger`：连续、定时、手动/外部触发的统一等待边界。
- `ProjectRuntimeController`：保持采集源打开，协调生产者、消费者、有界队列、状态与统计。
- `MainWindow`：只负责运行操作台交互和结果呈现。

## 数据流

`触发源 → IFrameSource → CapturedFrame → 有界队列 → ProjectRuntimeService → Input.RuntimeFrame.Image → WorkflowExecutor → resultMappings → InspectionResult → UI`

Runtime 每次执行不启用项目节点缓存。采集帧每次都不同，禁止复用上一帧的下游缓存结果。

连续运行控制位于 `ProjectRuntimeService` 之外，单次执行服务仍可独立复用。外部硬件触发
后续实现 `IRuntimeTrigger`，厂商相机实现 `IFrameSource`；记录和上传通过独立结果消费者
接入，均不实现成图像算子。

## 队列策略

- `ProcessAll`：队列满时等待，保证每个已采集帧进入工作流，背压会降低采集速率。
- `LatestFrame`：队列满时丢弃最旧帧，适合实时显示和只关心最新状态的检测。
- `DropWhenBusy`：队列满时丢弃新帧，适合保持已排队帧顺序的触发场景。

被丢弃、停止时残留以及处理完成的帧都由控制器释放。队列容量限制为 1–1024，运行统计
包含采集、处理、丢弃、错误和当前队列深度。
