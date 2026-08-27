# VisionAI 原生推理算子接入说明

## 1. 接入结论

Vision Studio 通过 VisionAI 0.1.0 的稳定 C ABI 动态加载原生推理能力。主程序不引用 VisionAI 的 C++ 工程，也不把 ONNX 模型编入软件本体；运行库和模型均可独立替换。

当前接入的真实算子：

| Vision Studio 算子 | VisionAI C API | 主要输出 |
| --- | --- | --- |
| `AI.Detection` 目标检测 | `vai_detector_*` | 标注图、JSON、目标数量 |
| `AI.Segmentation` 实例分割 | `vai_segmenter_*` | 掩膜叠加图、JSON、实例数量 |
| `AI.OCR` 文字识别 | `vai_ocr_*` | 文本框标注图、UTF-8 JSON、文本数量 |
| `AI.Anomaly` 异常检测 | `vai_anomaly_detector_*` | 热力图、JSON、异常分数、布尔判定 |

VisionAI 0.1.0 没有分类 API，因此原有只透传图像的 `AI.Classification` 占位算子已移除。只有底层 SDK 真正提供的能力才允许出现在算子目录中。

## 2. 关键技术节点

### 2.1 动态原生加载

适配层使用 `NativeLibrary.Load` 和 `NativeLibrary.GetExport` 在运行时绑定 `vision_ai.dll`，调用约定固定为 `cdecl`。软件未安装插件时仍可正常启动；只有执行 AI 算子时才返回明确的安装提示。

加载顺序为：

1. `onnxruntime.dll`
2. `onnxruntime_providers_shared.dll`
3. `vision_ai.dll`
4. 选择 CUDA 时，由 ONNX Runtime 按需加载 CUDA provider

原生库句柄在进程内保持有效，防止委托指向已卸载模块。每次算子执行创建并销毁 VisionAI context 和具体任务句柄；项目结果缓存命中时不会重新推理。

### 2.2 图像内存边界

输入限定为 OpenCV 8 位 1、3 或 4 通道图像，对应 `GRAY8`、`BGR8` 和 `BGRA8`。适配层克隆输入 Mat，向 VisionAI 传递非持有的 `data + width + height + stride` 视图，并保证两阶段查询/填充调用期间内存不变。

所有 VisionAI 输出缓冲区由 .NET 侧按第一次查询返回的容量分配并在调用后释放。SDK 的线程局部错误文本会立即复制为托管字符串，避免下一次原生调用覆盖错误内容。

### 2.3 输出可视化

- 目标检测：绘制边界框、类别和置信度。
- 实例分割：根据 `mask_offset` 和 `mask_stride_bytes` 重建 ROI 局部掩膜，叠加颜色并绘制边界框。
- OCR：按 UTF-8 字节偏移解码文本，绘制四点文本框。
- 异常检测：把 float 热力图归一化、应用 Jet 色图并与原图融合。

每个算子同时输出可供后续逻辑节点使用的数量、分数或布尔值；详细结果以 JSON 字符串输出，避免界面只能看到图片而无法消费推理结果。

### 2.4 缓存失效

AI 算子沿用项目节点缓存。以下变化会触发重新计算：

- 算子参数改变；
- 任一上游节点或连接改变；
- 模型、标签或 OCR 字典的文件大小/最后修改时间改变。

因此替换同路径模型不会错误复用旧推理结果。

## 3. 文件与配置边界

### 软件级运行库

默认目录：

```text
VisionStudio/
  plugins/
    VisionAI/
      visionai-runtime.json
      bin/
        vision_ai.dll
        onnxruntime.dll
        onnxruntime_providers_shared.dll
        onnxruntime_providers_cuda.dll   # 仅 CUDA 安装
```

开发或自动测试可设置 `VISIONSTUDIO_VISIONAI_RUNTIME` 指向插件根目录或 `bin` 目录。发布环境还可在安装目录 `config/visionai-runtime.json` 中配置：

```json
{
  "runtimePath": "plugins/VisionAI"
}
```

运行库路径是软件级配置，不保存在 `.vstudio` 项目中。

### 项目级模型

模型和标签必须使用项目内相对路径，不能通过 `..` 或绝对路径越出项目目录：

```text
MyProject/
  models/
    detection.onnx
    segmentation.onnx
    anomaly.onnx
    labels.txt
    ocr/
      detection.onnx
      recognition.onnx
      dictionary.txt
```

这种划分让一个 VisionAI 插件服务多个视觉项目，同时保证项目复制后模型引用仍然有效。

## 4. 安装与发布

安装 CPU 运行库：

```powershell
.\scripts\install-visionai-runtime.ps1 `
  -PackagePath .\visionai\VisionAI-0.1.0-windows-x64.zip `
  -Destination <VisionStudio安装目录>\plugins\VisionAI
```

需要 CUDA 时追加 `-IncludeCuda`。CPU 安装不会解压约 170 MB 的 CUDA provider。

软件主体安装包不包含 VisionAI ZIP 和模型。VisionAI 应作为单独的可下载插件发布；模型再作为独立资源包发布或由用户导入。当前 VisionAI 包清单标记为“完成项目和模型许可证审查前仅限内部使用”，公开分发前必须完成 ONNX Runtime、CUDA provider、模型及训练数据许可审核。

## 5. 边界与限制

- 当前仅支持 Windows x64 版 VisionAI 0.1.0；Linux 需要对应 ABI 的原生包。
- `CPU` 是默认 provider；`CUDA` 失败不会静默回退，`Auto` 才允许回退。
- 模型输入输出契约必须与 VisionAI 对应模块兼容，仅扩展名为 ONNX 并不代表可运行。
- 异常检测的输入尺寸、阈值和预处理必须与模型清单一致。
- 标签文件为可选逐行文本；缺失时显示 `class N`。
- VisionAI 0.1.0 的检测、分割和 OCR 容量查询与填充阶段会执行两次推理，这是 SDK 当前接口行为，不是 Vision Studio 重复运行节点。
- 公开版不得内置来源和再分发许可不明确的模型。

## 6. 定向测试

运行：

```powershell
.\scripts\test-visionai-operators.ps1
```

测试脚本从 ZIP 只提取 CPU 运行库，并验证：

1. 算子目录只包含真实支持的 4 个 VisionAI 算子；
2. 模型文件变化会改变缓存指纹；
3. YOLO 检测模型对正样本产生真实检测结果；
4. YOLO 分割模型返回并绘制真实实例掩膜；
5. PP-OCRv5 对合成文字图执行检测和 UTF-8 识别；
6. EfficientAD 契约模型返回热力图、分数和异常判定。

该测试属于 `VisionAI` 维度，不会运行界面、Web、导出代码或安装包等非目标测试。
